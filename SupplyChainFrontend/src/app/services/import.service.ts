import { HttpClient, HttpEvent, HttpEventType, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, signal } from '@angular/core';
import { EMPTY, Observable, Subject, catchError, filter, interval, map, startWith, switchMap } from 'rxjs';
import * as signalR from '@microsoft/signalr';
import { environment } from '../../environments/environment';
import { AuthService } from '../pages/service/auth.service';

// A40 — Data Import & Upload Framework (docs/data-import/API-CONTRACT.md). Every route needs MODULE_DATA_IMPORT;
// opening-balance entity types also FEATURE_OPENING_BALANCE_IMPORT + IMPORT_OPENING_BALANCE.

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

export interface Paginated<T> {
  data: T[];
  totalRecords: number;
  page: number;
  pageSize: number;
  totalPages?: number;
}

// ── Codes ─────────────────────────────────────────────────────────────────────

export const IMPORT_ROUTE = '/portal/pages/settings/import';
/** Where the opening balance date is set (Settings › Finance Setup › Currency configuration, A40 D-7). */
export const OPENING_DATE_ROUTE = '/portal/pages/finance-setup/currency-configuration';

export const MODULE_DATA_IMPORT = 'MODULE_DATA_IMPORT';
export const FEATURE_OPENING_BALANCE_IMPORT = 'FEATURE_OPENING_BALANCE_IMPORT';

export const IMPORT_PERMS = {
  PROFILE_VIEW: 'IMPORT_PROFILE_VIEW',
  PROFILE_MANAGE: 'IMPORT_PROFILE_MANAGE',
  UPLOAD: 'IMPORT_UPLOAD',
  EXECUTE: 'IMPORT_EXECUTE',
  MANAGE: 'IMPORT_MANAGE',
  OPENING_BALANCE: 'IMPORT_OPENING_BALANCE'
} as const;

export type ImportSessionStatus = 'UPLOADED' | 'MAPPING' | 'VALIDATING' | 'VALIDATED' | 'IMPORTING' | 'COMPLETED'
  | 'COMPLETED_WITH_ERRORS' | 'FAILED' | 'CANCELLED';
export type ImportRowStatus = 'PENDING' | 'VALID' | 'INVALID' | 'IMPORTED' | 'SKIPPED' | 'UPDATED' | 'ERROR';
export type DuplicateStrategy = 'SKIP' | 'UPDATE' | 'CREATE_NEW' | 'ERROR';
export type ImportFileType = 'XLSX' | 'CSV';

/** A session in one of these states is finished — nothing will change it any more. */
export const TERMINAL_STATUSES: ImportSessionStatus[] = ['COMPLETED', 'COMPLETED_WITH_ERRORS', 'FAILED', 'CANCELLED'];

export function isTerminal(status: string | null | undefined): boolean {
  return TERMINAL_STATUSES.includes(status as ImportSessionStatus);
}

// ── Models ────────────────────────────────────────────────────────────────────

export interface ImportProfile {
  entityType: string;
  name: string;
  description?: string | null;
  importOrder: number;
  isOpeningBalance: boolean;
  matchFields: string[];
  duplicateStrategy: DuplicateStrategy;
  maxRowsPerFile: number;
  maxFileSizeMb: number;
  allowUpdate: boolean;
  requiresOpeningBalancePermission: boolean;
  completedSessions: number;
  lastCompletedAt?: string | null;
  lastImportedRows?: number | null;
}

export interface ImportField {
  fieldName: string;
  displayName: string;
  description?: string | null;
  fieldType: string;
  isRequired: boolean;
  isKey: boolean;
  sortOrder: number;
  sampleValue?: string | null;
  maxLength?: number | null;
  minValue?: number | null;
  maxValue?: number | null;
  decimalPlaces?: number | null;
  lookupList?: string | null;
}

export interface ImportProfileDetail extends ImportProfile {
  fields: ImportField[];
}

export interface ColumnMapping {
  sourceColumnIndex: number;
  sourceColumnName: string;
  targetFieldName?: string | null;
  targetDisplayName?: string | null;
  isAutoMapped: boolean;
  confidenceScore?: number | null;
}

export interface SessionListItem {
  uuid: string;
  entityType: string;
  entityName: string;
  fileName: string;
  status: ImportSessionStatus;
  totalRows: number;
  validRows: number;
  invalidRows: number;
  importedRows: number;
  updatedRows: number;
  skippedRows: number;
  errorRows: number;
  createdAt: string;
  createdByName: string;
}

export interface SessionDetail extends SessionListItem {
  fileType: ImportFileType;
  fileSizeBytes: number;
  sheetName?: string | null;
  sheetNames: string[];
  duplicateStrategy: DuplicateStrategy;
  notes?: string | null;
  savedMappingName?: string | null;
  validationCompletedAt?: string | null;
  importStartedAt?: string | null;
  importCompletedAt?: string | null;
  errorMessage?: string | null;
  mappings: ColumnMapping[];
  unmappedColumns: string[];
  unmappedRequiredFields: string[];
}

export interface ImportPreview {
  columns: { fieldName: string; displayName: string }[];
  rows: { rowNumber: number; values: Record<string, string | null>; issues?: Record<string, string> | null }[];
}

export interface ValidationError {
  rowNumber: number;
  fieldName: string;
  errorCode: string;
  message: string;
  sourceValue?: string | null;
}

export interface ValidationResult {
  session: SessionDetail;
  /** The first 200. */
  errors: ValidationError[];
  errorCountByCode: Record<string, number>;
}

export interface ExecuteResult {
  isBackground: boolean;
  jobId?: string | null;
  session: SessionDetail;
}

export interface ImportProgress {
  processed: number;
  total: number;
  status: ImportSessionStatus;
}

export interface ImportProgressEvent extends ImportProgress {
  sessionUuid: string;
}

export interface ImportFieldError {
  field: string;
  code: string;
  message: string;
  value?: string | null;
}

export interface ImportErrorRow {
  rowNumber: number;
  status: ImportRowStatus;
  values: Record<string, string | null>;
  errors: ImportFieldError[];
}

export interface MappingPreset {
  name: string;
  lastUsedAt?: string | null;
}

export interface UploadRequest {
  file: File;
  entityType: string;
  duplicateStrategy?: DuplicateStrategy | null;
  sheetName?: string | null;
  mappingPreset?: string | null;
  notes?: string | null;
}

/** An upload in flight: progress (0–100) while sending, then the created session. */
export type UploadEvent = { type: 'progress'; percent: number } | { type: 'done'; session: SessionDetail };

export interface SessionFilter {
  entityType?: string | null;
  status?: string | null;
  page?: number;
  pageSize?: number;
}

// ── Hub ───────────────────────────────────────────────────────────────────────

/**
 * `/hubs/import` (user group): `ImportProgress { sessionUuid, processed, total, status }` while a background import runs.
 * One lazy connection per app; `connected` is false until it starts (and again after it drops), so watchers poll instead.
 */
@Injectable({ providedIn: 'root' })
export class ImportHubService {
  readonly connected = signal(false);
  private readonly subject = new Subject<ImportProgressEvent>();
  readonly events$ = this.subject.asObservable();
  private hub: signalR.HubConnection | null = null;

  constructor(private auth: AuthService) {}

  connect(): void {
    if (this.hub || !this.auth.getToken()) return;
    const hub = this.build();
    this.hub = hub;
    hub.on('ImportProgress', (e: any) => this.subject.next(normalizeProgress(e)));
    hub.onreconnecting(() => this.connected.set(false));
    hub.onreconnected(() => this.connected.set(true));
    hub.onclose(() => this.connected.set(false));
    hub.start()
      .then(() => this.connected.set(true))
      .catch(err => {
        this.connected.set(false);
        console.warn('[ImportHub] SignalR connect failed — polling instead:', err);
      });
  }

  disconnect(): void {
    this.hub?.stop();
    this.hub = null;
    this.connected.set(false);
  }

  /** Separate so tests can hand in a fake connection. */
  protected build(): signalR.HubConnection {
    return new signalR.HubConnectionBuilder()
      .withUrl(`${environment.apiOrigin}/hubs/import`, {
        accessTokenFactory: () => this.auth.getToken() ?? '',
        transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling
      })
      .withAutomaticReconnect()
      .configureLogging(signalR.LogLevel.Warning)
      .build();
  }
}

/** camelCase per contract; PascalCase tolerated in case the hub serializer differs. */
export function normalizeProgress(e: any): ImportProgressEvent {
  return {
    sessionUuid: String(e?.sessionUuid ?? e?.SessionUuid ?? ''),
    processed: Number(e?.processed ?? e?.Processed ?? 0),
    total: Number(e?.total ?? e?.Total ?? 0),
    status: (e?.status ?? e?.Status ?? 'IMPORTING') as ImportSessionStatus
  };
}

/**
 * Progress of one session: hub events, plus GET /progress every `pollMs` while the hub is not connected (and every fifth
 * tick even when it is, so a missed final event cannot leave the page waiting). Completes after a terminal status.
 */
export function watchImportProgress(
  hub: Pick<ImportHubService, 'connect' | 'connected' | 'events$'>,
  getProgress: (uuid: string) => Observable<ApiResponse<ImportProgress>>,
  uuid: string,
  pollMs = 3000
): Observable<ImportProgress> {
  return new Observable<ImportProgress>(sub => {
    let done = false;
    const emit = (p: ImportProgress | null | undefined) => {
      if (done || !p) return;
      sub.next({ processed: p.processed, total: p.total, status: p.status });
      if (isTerminal(p.status)) { done = true; sub.complete(); }
    };
    const hubSub = hub.events$.pipe(filter(e => e.sessionUuid === uuid)).subscribe(emit);
    hub.connect();
    const pollSub = interval(pollMs).pipe(
      startWith(-1),
      filter(tick => tick === -1 || !hub.connected() || tick % 5 === 4),
      switchMap(() => getProgress(uuid).pipe(catchError(() => EMPTY)))
    ).subscribe(res => emit(res?.result));
    return () => { hubSub.unsubscribe(); pollSub.unsubscribe(); };
  });
}

// ── API ───────────────────────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class ImportService {
  private readonly base = `${environment.apiUrl}/import`;

  constructor(private http: HttpClient, private hub: ImportHubService) {}

  // Profiles
  getProfiles(): Observable<ApiResponse<ImportProfile[]>> {
    return this.http.get<ApiResponse<ImportProfile[]>>(`${this.base}/profiles`);
  }

  getProfile(entityType: string): Observable<ApiResponse<ImportProfileDetail>> {
    return this.http.get<ApiResponse<ImportProfileDetail>>(`${this.base}/profiles/${encodeURIComponent(entityType)}`);
  }

  updateProfileSettings(entityType: string, body: { duplicateStrategy: DuplicateStrategy; maxRowsPerFile: number; maxFileSizeMb: number })
    : Observable<ApiResponse<ImportProfile>> {
    return this.http.put<ApiResponse<ImportProfile>>(`${this.base}/profiles/${encodeURIComponent(entityType)}/settings`, body);
  }

  downloadTemplate(entityType: string): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.base}/profiles/${encodeURIComponent(entityType)}/template`, { responseType: 'blob', observe: 'response' });
  }

  downloadMigrationWorkbook(): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.base}/migration-workbook`, { responseType: 'blob', observe: 'response' });
  }

  // Sessions
  upload(req: UploadRequest): Observable<UploadEvent> {
    const form = new FormData();
    form.append('file', req.file, req.file.name);
    form.append('entityType', req.entityType);
    if (req.duplicateStrategy) form.append('duplicateStrategy', req.duplicateStrategy);
    if (req.sheetName) form.append('sheetName', req.sheetName);
    if (req.mappingPreset) form.append('mappingPreset', req.mappingPreset);
    if (req.notes && req.notes.trim()) form.append('notes', req.notes.trim());
    return this.http.post<ApiResponse<SessionDetail>>(`${this.base}/sessions`, form, { reportProgress: true, observe: 'events' }).pipe(
      map((e: HttpEvent<ApiResponse<SessionDetail>>): UploadEvent | null => {
        if (e.type === HttpEventType.UploadProgress) {
          return { type: 'progress', percent: e.total ? Math.round((100 * e.loaded) / e.total) : 0 };
        }
        if (e.type === HttpEventType.Response) return { type: 'done', session: e.body!.result };
        return null;
      }),
      filter((e): e is UploadEvent => e !== null)
    );
  }

  getSessions(f: SessionFilter = {}): Observable<ApiResponse<Paginated<SessionListItem>>> {
    let params = new HttpParams().set('page', f.page ?? 1).set('pageSize', f.pageSize ?? 20);
    if (f.entityType) params = params.set('entityType', f.entityType);
    if (f.status) params = params.set('status', f.status);
    return this.http.get<ApiResponse<Paginated<SessionListItem>>>(`${this.base}/sessions`, { params });
  }

  getSession(uuid: string): Observable<ApiResponse<SessionDetail>> {
    return this.http.get<ApiResponse<SessionDetail>>(`${this.base}/sessions/${uuid}`);
  }

  getPreview(uuid: string, rows = 20): Observable<ApiResponse<ImportPreview>> {
    return this.http.get<ApiResponse<ImportPreview>>(`${this.base}/sessions/${uuid}/preview`, { params: { rows } });
  }

  getMappings(uuid: string): Observable<ApiResponse<ColumnMapping[]>> {
    return this.http.get<ApiResponse<ColumnMapping[]>>(`${this.base}/sessions/${uuid}/mappings`);
  }

  updateMappings(uuid: string, mappings: { sourceColumnIndex: number; targetFieldName: string | null }[]): Observable<ApiResponse<SessionDetail>> {
    return this.http.put<ApiResponse<SessionDetail>>(`${this.base}/sessions/${uuid}/mappings`, { mappings });
  }

  getPresets(entityType: string): Observable<ApiResponse<MappingPreset[]>> {
    return this.http.get<ApiResponse<MappingPreset[]>>(`${this.base}/mapping-presets`, { params: { entityType } });
  }

  savePreset(uuid: string, presetName: string): Observable<ApiResponse<unknown>> {
    return this.http.post<ApiResponse<unknown>>(`${this.base}/sessions/${uuid}/mappings/save`, { presetName });
  }

  validate(uuid: string): Observable<ApiResponse<ValidationResult>> {
    return this.http.post<ApiResponse<ValidationResult>>(`${this.base}/sessions/${uuid}/validate`, {});
  }

  execute(uuid: string): Observable<ApiResponse<ExecuteResult>> {
    return this.http.post<ApiResponse<ExecuteResult>>(`${this.base}/sessions/${uuid}/execute`, {});
  }

  cancel(uuid: string): Observable<ApiResponse<SessionDetail>> {
    return this.http.post<ApiResponse<SessionDetail>>(`${this.base}/sessions/${uuid}/cancel`, {});
  }

  getProgress(uuid: string): Observable<ApiResponse<ImportProgress>> {
    return this.http.get<ApiResponse<ImportProgress>>(`${this.base}/sessions/${uuid}/progress`);
  }

  getErrors(uuid: string, page = 1, pageSize = 50): Observable<ApiResponse<Paginated<ImportErrorRow>>> {
    return this.http.get<ApiResponse<Paginated<ImportErrorRow>>>(`${this.base}/sessions/${uuid}/errors`, { params: { page, pageSize } });
  }

  downloadErrors(uuid: string): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.base}/sessions/${uuid}/errors/download`, { responseType: 'blob', observe: 'response' });
  }

  downloadResults(uuid: string): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.base}/sessions/${uuid}/results/download`, { responseType: 'blob', observe: 'response' });
  }

  /** Hub events + polling fallback for one session; see watchImportProgress. */
  watchProgress(uuid: string, pollMs = 3000): Observable<ImportProgress> {
    return watchImportProgress(this.hub, id => this.getProgress(id), uuid, pollMs);
  }

  // Finance — opening balance date (D-7)
  getOpeningBalanceDate(): Observable<ApiResponse<{ date: string | null }>> {
    return this.http.get<ApiResponse<{ date: string | null }>>(`${environment.apiUrl}/finance/opening-balance-date`);
  }

  /** null clears it (refused with 409 once opening documents exist). */
  setOpeningBalanceDate(date: string | null): Observable<ApiResponse<{ date: string | null }>> {
    return this.http.put<ApiResponse<{ date: string | null }>>(`${environment.apiUrl}/finance/opening-balance-date`, { date });
  }
}

// ── Downloads ─────────────────────────────────────────────────────────────────

/** filename*=UTF-8''… or filename="…" from Content-Disposition; null when absent (or not exposed to the page by CORS). */
export function fileNameFrom(disposition: string | null | undefined): string | null {
  if (!disposition) return null;
  const star = /filename\*\s*=\s*(?:UTF-8'')?([^;]+)/i.exec(disposition);
  if (star) {
    try { return decodeURIComponent(star[1].trim().replace(/^"|"$/g, '')); } catch { /* fall through */ }
  }
  const plain = /filename\s*=\s*("?)([^";]+)\1/i.exec(disposition);
  return plain ? plain[2].trim() : null;
}

/** Hands the response body to the browser as a file named by the server (fallback when it did not say). */
export function saveDownload(res: HttpResponse<Blob>, fallbackName: string): void {
  const blob = res.body;
  if (!blob) return;
  const name = fileNameFrom(res.headers?.get('Content-Disposition')) ?? fallbackName;
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  link.click();
  URL.revokeObjectURL(url);
}

/** The server's own sentence (JSON or a JSON blob from a failed download), else the fallback. */
export async function importErrorMessage(err: any, fallback: string): Promise<string> {
  const body = err?.error;
  if (typeof Blob !== 'undefined' && body instanceof Blob) {
    try {
      const message = JSON.parse(await body.text())?.message;
      if (typeof message === 'string' && message.trim()) return message;
    } catch { /* not JSON */ }
    return fallback;
  }
  return importErrorText(err, fallback);
}

/** Synchronous form for JSON errors. */
export function importErrorText(err: any, fallback: string): string {
  const body = err?.error;
  if (body && typeof body === 'object' && typeof body.message === 'string' && body.message.trim()) return body.message;
  if (typeof body === 'string' && body.trim() && body.length < 300) return body;
  switch (err?.status) {
    case 0: return 'The server could not be reached. Check your connection and try again.';
    case 403: return 'You do not have permission to do this.';
    case 404: return 'It was not found. It may have been removed.';
    default: return fallback;
  }
}
