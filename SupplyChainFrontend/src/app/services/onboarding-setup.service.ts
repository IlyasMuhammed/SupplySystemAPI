import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, signal } from '@angular/core';
import { Observable, catchError, map, of, shareReplay, tap } from 'rxjs';
import { environment } from '../../environments/environment';
import type { ApiResponse } from '../pages/service/auth.service';

// A50 — tenant onboarding: the setup wizard, the Default Data dashboard and the first-login status.
// Shapes: docs/onboarding/API-CONTRACT.md "Details › D-CORE". Every payload is a JSON object (never a string).

export type SetupStatusCode = 'NotStarted' | 'InProgress' | 'Completed' | 'Skipped';
export type SetupResetMode = 'Merge' | 'Overwrite' | 'Full';
export type PreviewStatus = 'Ready' | 'ModuleDisabled' | 'NoDefaults' | 'NoSeeder' | 'Invalid';
export type ApplyResultStatus = 'Applied' | 'Skipped' | 'ModuleDisabled' | 'NoDefaults' | 'NoSeeder' | 'Invalid' | 'Failed' | 'NotRun';
export type InviteOutcome = 'INVITED' | 'EXISTS' | 'INVALID' | 'FAILED';

/** GET setup/status — any signed-in user (banner / first-login guard). Null for a caller with no organization. */
export interface SetupStatusInfo {
  setupStatus: SetupStatusCode;
  setupStep: number;
  countryCode: string | null;
  industryType: string | null;
  completedAt: string | null;
  canRunWizard: boolean;
}

export interface SetupCountry { code: string; name: string; defaultsCount?: number; }
export interface SetupIndustry { code: string; name: string; }

export interface SetupPreviewCounts { category?: string; inPayload: number; alreadyPresent: number; warnings: string[]; }

export interface SetupCategoryPreview {
  category: string;
  displayName?: string | null;
  status: PreviewStatus;
  setupDefaultUuid?: string | null;
  version: number | null;
  resolvedFrom: { country: string; industry: string } | null;
  payload: any;
  verified: boolean | null;
  preview: SetupPreviewCounts | null;
  requiresModule?: string | null;
  moduleEnabled: boolean;
  hasSeeder: boolean;
  errors: string[];
}

export interface SetupInvite { email: string; firstName?: string | null; lastName?: string | null; roleRef: string; }

export interface SetupApplyRequest {
  countryCode: string;
  industryType: string;
  payloadOverrides: Record<string, any>;
  wizardInputs: Record<string, any>;
  skipCategories: string[];
  invites: SetupInvite[];
}

export interface SetupCategoryResult {
  category: string;
  status: ApplyResultStatus;
  action: string | null;
  ok: boolean;
  created: number;
  skipped: number;
  removed: number;
  version?: number | null;
  warnings: string[];
  message: string | null;
}

export interface SetupInviteResult { email: string; outcome: InviteOutcome; userId?: number | null; message?: string | null; }

export interface SetupApplyResponse {
  setupStatus: SetupStatusCode;
  completed: boolean;
  results: SetupCategoryResult[];
  invites: SetupInviteResult[];
}

export interface SetupState {
  setupStatus: SetupStatusCode;
  setupStep: number;
  countryCode: string | null;
  industryType: string | null;
  stepData: Record<string, any>;
  completedAt?: string | null;
  completedBy?: number | string | null;
  note?: string | null;
  lastAppliedAt?: string | null;
}

export interface DefaultDataCategory {
  category: string;
  displayName?: string | null;
  requiresModule?: string | null;
  moduleEnabled: boolean;
  hasSeeder: boolean;
  seededCount: number;
  currentCount: number;
  modifiedCount: number;
  missingCount: number;
  modifiedKeys: string[];
  missingKeys: string[];
  isModified: boolean;
  lastAppliedAt: string | null;
  lastAction: string | null;
  appliedVersion: number | null;
  latestVersion: number | null;
  updateAvailable: boolean;
}

export interface DefaultDataOverview {
  countryCode: string | null;
  industryType: string | null;
  setupStatus: SetupStatusCode;
  completedAt: string | null;
  categories: DefaultDataCategory[];
}

/** The 409 body of a Full reset blocked by references (BR-50-008). */
export interface SetupReferenceBlock { referencedCount: number; referencedKeys: string[]; }

@Injectable({ providedIn: 'root' })
export class OnboardingSetupService {
  private readonly base = `${environment.apiUrl}/onboarding/setup`;

  /** The last status read this session (undefined = not read yet, null = no organization). Banner + guard share it. */
  readonly status = signal<SetupStatusInfo | null | undefined>(undefined);
  private inflight: Observable<SetupStatusInfo | null> | null = null;

  constructor(private http: HttpClient) {}

  /** GET setup/status, cached for the session unless `force`. Errors resolve to null and are not cached. */
  loadStatus(force = false): Observable<SetupStatusInfo | null> {
    const cached = this.status();
    if (!force && cached !== undefined) return of(cached);
    if (!force && this.inflight) return this.inflight;
    this.inflight = this.http.get<ApiResponse<SetupStatusInfo | null>>(`${this.base}/status`).pipe(
      map(r => r?.result ?? null),
      tap(s => { this.status.set(s); this.inflight = null; }),
      catchError(() => { this.inflight = null; return of(null); }),
      shareReplay(1)
    );
    return this.inflight;
  }

  /** Updates the cached status after an action changed it (state save, apply, skip). */
  setCachedStatus(code: SetupStatusCode): void {
    const s = this.status();
    if (s) this.status.set({ ...s, setupStatus: code });
  }

  countries(): Observable<ApiResponse<SetupCountry[]>> {
    return this.http.get<ApiResponse<SetupCountry[]>>(`${this.base}/countries`);
  }

  industries(country: string): Observable<ApiResponse<SetupIndustry[]>> {
    return this.http.get<ApiResponse<SetupIndustry[]>>(`${this.base}/industries`, { params: new HttpParams().set('country', country) });
  }

  preview(body: { countryCode: string; industryType: string; categories?: string[] | null; payloadOverrides?: Record<string, any> }):
    Observable<ApiResponse<SetupCategoryPreview[]>> {
    return this.http.post<ApiResponse<SetupCategoryPreview[]>>(`${this.base}/preview`, body);
  }

  apply(body: SetupApplyRequest): Observable<ApiResponse<SetupApplyResponse>> {
    return this.http.post<ApiResponse<SetupApplyResponse>>(`${this.base}/apply`, body).pipe(
      tap(r => { if (r?.result?.setupStatus) this.setCachedStatus(r.result.setupStatus); })
    );
  }

  skip(): Observable<ApiResponse<SetupApplyResponse>> {
    return this.http.post<ApiResponse<SetupApplyResponse>>(`${this.base}/skip`, {}).pipe(
      tap(r => this.setCachedStatus(r?.result?.setupStatus ?? 'Skipped'))
    );
  }

  getState(): Observable<ApiResponse<SetupState>> {
    return this.http.get<ApiResponse<SetupState>>(`${this.base}/state`);
  }

  saveState(step: number, data: any, countryCode?: string | null, industryType?: string | null): Observable<ApiResponse<SetupState>> {
    const body: Record<string, any> = { data };
    if (countryCode) body['countryCode'] = countryCode;
    if (industryType) body['industryType'] = industryType;
    return this.http.post<ApiResponse<SetupState>>(`${this.base}/state/${step}`, body).pipe(
      tap(r => { if (r?.result?.setupStatus) this.setCachedStatus(r.result.setupStatus); })
    );
  }

  categories(): Observable<ApiResponse<DefaultDataOverview>> {
    return this.http.get<ApiResponse<DefaultDataOverview>>(`${this.base}/categories`);
  }

  reset(category: string, mode: SetupResetMode, source: 'applied' | 'latest' = 'applied'): Observable<ApiResponse<SetupCategoryResult>> {
    return this.http.post<ApiResponse<SetupCategoryResult>>(`${this.base}/categories/${encodeURIComponent(category)}/reset`,
      { mode, confirm: true, source });
  }
}
