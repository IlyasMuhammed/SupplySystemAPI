import { HttpClient } from '@angular/common/http';
import { Injectable, computed, effect, signal } from '@angular/core';
import { Observable, catchError, map, of } from 'rxjs';
import { environment } from '../../environments/environment';
import type { ApiResponse } from '../pages/service/auth.service';
import { TenantService } from '../pages/service/tenant.service';
import { EnabledModules, ModuleCard, ModuleHistoryEntry, ModuleImpact } from './module.models';

/**
 * A37 D-15 — which modules and features the signed-in user's organization can use right now
 * (GET /api/tenant/modules/enabled). Loaded with the shell (AppLayout) after login, refreshed after any module action
 * on Settings › Modules and whenever the API answers MODULE_NOT_LICENSED.
 *
 * Until the first answer arrives, the checks fall back to GET /api/tenant/current's enabledFeatureCodes (TenantService),
 * so nothing flickers. A Super Admin is outside organization scope and passes every check (as on the server).
 */
@Injectable({ providedIn: 'root' })
export class ModuleService {
  private readonly url = `${environment.apiUrl}/tenant/modules/enabled`;
  private readonly state = signal<EnabledModules | null>(null);

  /** True once GET /api/tenant/modules/enabled has answered for this session. */
  readonly loaded = computed(() => this.state() !== null);
  /** Bumped on every successful load — read it in a computed() to re-evaluate when modules change. */
  readonly version = signal(0);

  private readonly usable = computed(() => {
    const s = this.state();
    return s ? new Set([...(s.modules ?? []), ...(s.features ?? [])]) : null;
  });

  constructor(private http: HttpClient, private tenant: TenantService) {
    // Logout clears the tenant; the next organization must not inherit this one's modules.
    effect(() => {
      if (!this.tenant.tenant() && this.state()) this.state.set(null);
    });
  }

  /** Loads (or reloads) the enabled set. Errors leave the previous state in place and resolve to null. */
  load(): Observable<EnabledModules | null> {
    return this.http.get<ApiResponse<EnabledModules>>(this.url).pipe(
      map(res => {
        if (res?.success && res.result) {
          this.state.set({
            modules: res.result.modules ?? [],
            features: res.result.features ?? [],
            grace: res.result.grace ?? []
          });
          this.version.update(v => v + 1);
          return this.state();
        }
        return null;
      }),
      catchError(() => of(null))
    );
  }

  /**
   * Reloads the enabled set and GET /api/tenant/current (the menu's enabledFeatureCodes), so the sidebar, every
   * *smsIfModule and every isEnabled() caller re-evaluate. Fire-and-forget.
   */
  refresh(): void {
    this.load().subscribe();
    this.tenant.loadCurrent().subscribe({ error: () => {} });
  }

  /** A module (MODULE_*) or feature code is usable now. Grace counts as not usable (creates are refused). */
  isEnabled(code: string): boolean {
    if (this.tenant.isSuperAdmin()) return true;
    const usable = this.usable();
    return usable ? usable.has(code) : this.tenant.hasFeature(code);
  }

  /** Same check, named for sub-features (FEATURE_*): the API lists a feature only while its module is on. */
  isFeatureEnabled(code: string): boolean {
    return this.isEnabled(code);
  }

  /** Every listed code is usable. */
  allEnabled(codes: string | string[]): boolean {
    return ([] as string[]).concat(codes).every(c => this.isEnabled(c));
  }

  /** End of the grace period of a module that was switched off, or null when it is not in grace. */
  graceFor(code: string): string | null {
    return this.state()?.grace.find(g => g.code === code)?.graceEndsAt ?? null;
  }

  isInGrace(code: string): boolean {
    return this.graceFor(code) !== null;
  }
}

/** A37 §1.1 — the org-admin endpoints behind Settings › Modules. */
@Injectable({ providedIn: 'root' })
export class ModuleAdminService {
  private readonly base = `${environment.apiUrl}/tenant/modules`;

  constructor(private http: HttpClient) {}

  list(): Observable<ApiResponse<ModuleCard[]>> {
    return this.http.get<ApiResponse<ModuleCard[]>>(this.base);
  }

  enable(code: string, rowVersion?: string): Observable<ApiResponse<ModuleCard>> {
    return this.http.post<ApiResponse<ModuleCard>>(`${this.base}/${code}/enable`, { rowVersion });
  }

  disable(code: string, body: { graceDays?: number; notes?: string; rowVersion?: string }): Observable<ApiResponse<ModuleCard>> {
    return this.http.post<ApiResponse<ModuleCard>>(`${this.base}/${code}/disable`, body);
  }

  setFeature(code: string, featureCode: string, enabled: boolean, rowVersion?: string): Observable<ApiResponse<ModuleCard>> {
    return this.http.put<ApiResponse<ModuleCard>>(`${this.base}/${code}/features/${featureCode}`, { enabled, rowVersion });
  }

  history(code: string): Observable<ApiResponse<ModuleHistoryEntry[]>> {
    return this.http.get<ApiResponse<ModuleHistoryEntry[]>>(`${this.base}/${code}/history`);
  }

  impact(code: string): Observable<ApiResponse<ModuleImpact>> {
    return this.http.get<ApiResponse<ModuleImpact>>(`${this.base}/${code}/impact`);
  }
}
