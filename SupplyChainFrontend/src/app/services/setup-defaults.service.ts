import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import type { ApiResponse } from '../pages/service/auth.service';

// A50 — Super admin Setup Defaults (api/system/setup-defaults, PLATFORM_SUPER_ADMIN). API-CONTRACT "Details › D-CORE".

export interface SetupDefaultRow {
  uuid: string;
  countryCode: string;
  industryType: string;
  dataCategory: string;
  displayName: string;
  version: number;
  isActive: boolean;
  notes: string | null;
  isSystemPack?: boolean;
  recordCount: number;
  appliedCount: number;
  createdAt?: string | null;
  createdBy?: number | null;
  modifiedAt?: string | null;
  modifiedBy?: number | null;
  rowVersion?: string | null;
}

/** GET {uuid} / POST / PUT: the row plus its payload (a JSON object). */
export interface SetupDefaultDetail extends SetupDefaultRow { dataPayload: any; }

export interface SetupDefaultsMeta {
  categories: { code: string; hasSeeder: boolean; hasSchema: boolean; requiresModule?: string | null }[];
  countries: string[];
  industries: string[];
}

export interface SetupDefaultsFilter { country?: string | null; industry?: string | null; category?: string | null; active?: boolean | null; }

export interface CreateSetupDefaultRequest {
  countryCode: string;
  industryType: string;
  dataCategory: string;
  displayName: string;
  notes: string | null;
  isActive: boolean;
  dataPayload: any;
}

export interface UpdateSetupDefaultRequest {
  displayName: string;
  notes: string | null;
  isActive: boolean;
  dataPayload: any;
  rowVersion?: string | null;
}

/** One item of the pack file (`defaults[]`) or of a bare import array. */
export interface SetupDefaultPackItem {
  countryCode: string;
  industryType: string;
  dataCategory: string;
  displayName?: string | null;
  notes?: string | null;
  isActive?: boolean;
  version?: number;
  payload: any;
}

export interface SetupDefaultPackFile { format?: string; version?: number; exportedAt?: string; defaults: SetupDefaultPackItem[]; }

export interface SetupDefaultsImportResult {
  dryRun: boolean;
  created: number;
  updated: number;
  unchanged: number;
  /** Records that failed validation (A51 P5). */
  invalid?: number;
  errors: { index: number; key: string; errors: string[] }[];
  items: { key: string; action: 'Created' | 'Updated' | 'Unchanged' | 'Invalid' }[];
}

export interface SetupDefaultValidation { valid: boolean; errors: string[]; }

function filterParams(f: SetupDefaultsFilter = {}): HttpParams {
  let p = new HttpParams();
  if (f.country) p = p.set('country', f.country);
  if (f.industry) p = p.set('industry', f.industry);
  if (f.category) p = p.set('category', f.category);
  if (f.active !== null && f.active !== undefined) p = p.set('active', String(f.active));
  return p;
}

@Injectable({ providedIn: 'root' })
export class SetupDefaultsService {
  private readonly base = `${environment.apiUrl}/system/setup-defaults`;

  constructor(private http: HttpClient) {}

  list(filter: SetupDefaultsFilter = {}): Observable<ApiResponse<SetupDefaultRow[]>> {
    return this.http.get<ApiResponse<SetupDefaultRow[]>>(this.base, { params: filterParams(filter) });
  }

  meta(): Observable<ApiResponse<SetupDefaultsMeta>> {
    return this.http.get<ApiResponse<SetupDefaultsMeta>>(`${this.base}/meta`);
  }

  get(uuid: string): Observable<ApiResponse<SetupDefaultDetail>> {
    return this.http.get<ApiResponse<SetupDefaultDetail>>(`${this.base}/${uuid}`);
  }

  create(body: CreateSetupDefaultRequest): Observable<ApiResponse<SetupDefaultDetail>> {
    return this.http.post<ApiResponse<SetupDefaultDetail>>(this.base, body);
  }

  update(uuid: string, body: UpdateSetupDefaultRequest): Observable<ApiResponse<SetupDefaultDetail>> {
    return this.http.put<ApiResponse<SetupDefaultDetail>>(`${this.base}/${uuid}`, body);
  }

  delete(uuid: string): Observable<ApiResponse<unknown>> {
    return this.http.delete<ApiResponse<unknown>>(`${this.base}/${uuid}`);
  }

  publish(uuid: string): Observable<ApiResponse<SetupDefaultRow>> {
    return this.http.post<ApiResponse<SetupDefaultRow>>(`${this.base}/${uuid}/publish`, {});
  }

  unpublish(uuid: string): Observable<ApiResponse<SetupDefaultRow>> {
    return this.http.post<ApiResponse<SetupDefaultRow>>(`${this.base}/${uuid}/unpublish`, {});
  }

  validate(dataCategory: string, dataPayload: any): Observable<ApiResponse<SetupDefaultValidation>> {
    return this.http.post<ApiResponse<SetupDefaultValidation>>(`${this.base}/validate`, { dataCategory, dataPayload });
  }

  import(body: SetupDefaultPackFile | SetupDefaultPackItem[], dryRun: boolean, mode: 'upsert' | 'insertOnly' = 'upsert', skipInvalid = false):
    Observable<ApiResponse<SetupDefaultsImportResult>> {
    let params = new HttpParams().set('dryRun', String(dryRun)).set('mode', mode);
    if (skipInvalid) params = params.set('skipInvalid', 'true');
    return this.http.post<ApiResponse<SetupDefaultsImportResult>>(`${this.base}/import`, body, { params });
  }

  /** A JSON file download in the pack format. */
  export(filter: SetupDefaultsFilter = {}): Observable<Blob> {
    return this.http.get(`${this.base}/export`, { params: filterParams(filter), responseType: 'blob' });
  }
}
