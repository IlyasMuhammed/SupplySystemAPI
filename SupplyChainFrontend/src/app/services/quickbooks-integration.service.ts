import { Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, OperatorFunction, map } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  ApiClientModel, BackfillResult, ConfirmMatchesRequest, ConnectResponse, ConnectionStatusModel,
  CreateApiClientRequest, IntegrationSettingsModel, IssueApiKeyRequest, IssuedApiKeyModel, ManualPushResult,
  MatchCandidateModel, MatchDecision, MatchKind, MatchScanResultModel, PreflightResultModel, QboApiResponse, QboPage,
  ReferenceDataModel, ResolveSyncItemRequest, SaveTaxCodeMappingsRequest, SaveTermMappingsRequest, StatusLookupResult,
  SyncItemModel, SyncItemQuery, SyncKind, SyncLogModel, SyncMode, SyncSummaryModel, TaxCodeMappingModel,
  TermMappingModel, TestConnectionResult, UpdateIntegrationSettingsRequest
} from '../models/quickbooks-integration.models';

export * from '../models/quickbooks-integration.models';

/**
 * A 2xx answer whose envelope said { success: false }. Raised through the error channel so every
 * caller handles one kind of failure, whatever the transport did.
 */
export class QboApiError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'QboApiError';
  }
}

/**
 * The words to show for a failed call: the server's own message where it sent one, a plain
 * sentence for the statuses that have one, otherwise the caller's fallback.
 */
export function qboErrorMessage(err: unknown, fallback: string): string {
  if (err instanceof QboApiError) return err.message || fallback;

  const httpError = err as Partial<HttpErrorResponse> | null | undefined;
  const body = httpError?.error as { message?: unknown; errors?: { message?: unknown }[] } | string | null | undefined;

  if (body && typeof body === 'object') {
    if (typeof body.message === 'string' && body.message.trim()) return body.message;
    const first = Array.isArray(body.errors) ? body.errors.find(e => typeof e?.message === 'string') : undefined;
    if (first) return String(first.message);
  }
  if (typeof body === 'string' && body.trim() && body.length < 300) return body;

  switch (httpError?.status) {
    case 0:   return 'The server could not be reached. Check your connection and try again.';
    case 401: return 'Your session has ended. Sign in again.';
    case 403: return 'You do not have permission to do this.';
    case 404: return 'It was not found. It may have been removed.';
    default:  return fallback;
  }
}

/** Reads `result` out of the { success, message, result } envelope, or fails with its message. */
function unwrap<T>(): OperatorFunction<QboApiResponse<T>, T> {
  return map((res: QboApiResponse<T>) => {
    if (!res || res.success === false) {
      throw new QboApiError(res?.message || 'The request did not succeed.');
    }
    return res.result;
  });
}

/**
 * The QuickBooks admin API (plan §5.1): the screens under Settings → QuickBooks Integration and the
 * sync badges. JWT only — the Angular app never holds a gateway API key.
 *
 * Every method returns the unwrapped `result`; a failure (HTTP error, or { success: false }) comes
 * through the error channel — use qboErrorMessage() to turn it into words.
 */
@Injectable({ providedIn: 'root' })
export class QuickBooksIntegrationService {
  private readonly base = `${environment.apiUrl}/integrations/quickbooks`;

  constructor(private http: HttpClient) {}

  // ── Connection ────────────────────────────────────────────────────────────────────────────

  getConnection(): Observable<ConnectionStatusModel> {
    return this.http.get<QboApiResponse<ConnectionStatusModel>>(`${this.base}/connection`).pipe(unwrap());
  }

  /** Starts the Intuit sign-in: send the browser to the returned consent URL. */
  connect(): Observable<ConnectResponse> {
    return this.http.post<QboApiResponse<ConnectResponse>>(`${this.base}/connect`, {}).pipe(unwrap());
  }

  testConnection(): Observable<TestConnectionResult> {
    return this.http.post<QboApiResponse<TestConnectionResult>>(`${this.base}/test`, {}).pipe(unwrap());
  }

  /** Revokes the grant at Intuit and forgets the tokens. Links to QuickBooks records are kept. */
  disconnect(): Observable<ConnectionStatusModel> {
    return this.http.delete<QboApiResponse<ConnectionStatusModel>>(`${this.base}/connection`).pipe(unwrap());
  }

  // ── Reference data & preflight ────────────────────────────────────────────────────────────

  getReference(): Observable<ReferenceDataModel> {
    return this.http.get<QboApiResponse<ReferenceDataModel>>(`${this.base}/reference`).pipe(unwrap());
  }

  /** Reads accounts, tax codes, terms and currencies from QuickBooks again. */
  refreshReference(): Observable<ReferenceDataModel> {
    return this.http.post<QboApiResponse<ReferenceDataModel>>(`${this.base}/reference/refresh`, {}).pipe(unwrap());
  }

  getPreflight(): Observable<PreflightResultModel> {
    return this.http.get<QboApiResponse<PreflightResultModel>>(`${this.base}/preflight`).pipe(unwrap());
  }

  // ── Settings & mappings ───────────────────────────────────────────────────────────────────

  getSettings(): Observable<IntegrationSettingsModel> {
    return this.http.get<QboApiResponse<IntegrationSettingsModel>>(`${this.base}/settings`).pipe(unwrap());
  }

  updateSettings(body: UpdateIntegrationSettingsRequest): Observable<IntegrationSettingsModel> {
    return this.http.put<QboApiResponse<IntegrationSettingsModel>>(`${this.base}/settings`, body).pipe(unwrap());
  }

  getTaxMappings(): Observable<TaxCodeMappingModel[]> {
    return this.http.get<QboApiResponse<TaxCodeMappingModel[]>>(`${this.base}/tax-mappings`).pipe(unwrap());
  }

  saveTaxMappings(body: SaveTaxCodeMappingsRequest): Observable<TaxCodeMappingModel[]> {
    return this.http.put<QboApiResponse<TaxCodeMappingModel[]>>(`${this.base}/tax-mappings`, body).pipe(unwrap());
  }

  getTermMappings(): Observable<TermMappingModel[]> {
    return this.http.get<QboApiResponse<TermMappingModel[]>>(`${this.base}/term-mappings`).pipe(unwrap());
  }

  saveTermMappings(body: SaveTermMappingsRequest): Observable<TermMappingModel[]> {
    return this.http.put<QboApiResponse<TermMappingModel[]>>(`${this.base}/term-mappings`, body).pipe(unwrap());
  }

  /** Dry run ↔ Live. The server refuses Live until preflight has passed and matching is confirmed. */
  setMode(mode: SyncMode): Observable<IntegrationSettingsModel> {
    return this.http.post<QboApiResponse<IntegrationSettingsModel>>(`${this.base}/mode`, { mode }).pipe(unwrap());
  }

  /** Records that matching is done for customers, vendors and items — a precondition for Live. */
  confirmMatchingComplete(confirmed = true): Observable<IntegrationSettingsModel> {
    return this.http.post<QboApiResponse<IntegrationSettingsModel>>(`${this.base}/matching/complete`, { confirmed }).pipe(unwrap());
  }

  // ── API clients ───────────────────────────────────────────────────────────────────────────

  getApiClients(): Observable<ApiClientModel[]> {
    return this.http.get<QboApiResponse<ApiClientModel[]>>(`${this.base}/api-clients`).pipe(unwrap());
  }

  createApiClient(body: CreateApiClientRequest): Observable<ApiClientModel> {
    return this.http.post<QboApiResponse<ApiClientModel>>(`${this.base}/api-clients`, body).pipe(unwrap());
  }

  /** The key in the answer is shown once and is never retrievable again. */
  issueApiKey(clientId: string, body: IssueApiKeyRequest): Observable<IssuedApiKeyModel> {
    return this.http.post<QboApiResponse<IssuedApiKeyModel>>(
      `${this.base}/api-clients/${encodeURIComponent(clientId)}/keys`, body).pipe(unwrap());
  }

  revokeApiKey(clientId: string, keyId: string): Observable<ApiClientModel> {
    return this.http.delete<QboApiResponse<ApiClientModel>>(
      `${this.base}/api-clients/${encodeURIComponent(clientId)}/keys/${encodeURIComponent(keyId)}`).pipe(unwrap());
  }

  deactivateApiClient(clientId: string): Observable<ApiClientModel> {
    return this.http.delete<QboApiResponse<ApiClientModel>>(
      `${this.base}/api-clients/${encodeURIComponent(clientId)}`).pipe(unwrap());
  }

  // ── Matching ──────────────────────────────────────────────────────────────────────────────

  /** Reads the QuickBooks records of this kind and proposes a match for each SCM record. Nothing is created. */
  scanMatches(kind: MatchKind): Observable<MatchScanResultModel> {
    return this.http.post<QboApiResponse<MatchScanResultModel>>(`${this.base}/matching/${kind}/scan`, {}).pipe(unwrap());
  }

  getMatchCandidates(kind: MatchKind, decision?: MatchDecision | null): Observable<MatchCandidateModel[]> {
    let params = new HttpParams();
    if (decision) params = params.set('decision', decision);
    return this.http.get<QboApiResponse<MatchCandidateModel[]>>(`${this.base}/matching/${kind}`, { params }).pipe(unwrap());
  }

  confirmMatches(kind: MatchKind, body: ConfirmMatchesRequest): Observable<MatchCandidateModel[]> {
    return this.http.post<QboApiResponse<MatchCandidateModel[]>>(`${this.base}/matching/${kind}/confirm`, body).pipe(unwrap());
  }

  // ── Sync dashboard ────────────────────────────────────────────────────────────────────────

  getSyncSummary(): Observable<SyncSummaryModel> {
    return this.http.get<QboApiResponse<SyncSummaryModel>>(`${this.base}/sync/summary`).pipe(unwrap());
  }

  getSyncItems(query: SyncItemQuery = {}): Observable<QboPage<SyncItemModel>> {
    let params = new HttpParams();
    if (query.kind)   params = params.set('kind', query.kind);
    if (query.state)  params = params.set('state', query.state);
    if (query.search?.trim()) params = params.set('search', query.search.trim());
    params = params.set('page', String(query.page ?? 1));
    params = params.set('pageSize', String(query.pageSize ?? 25));
    return this.http.get<QboApiResponse<QboPage<SyncItemModel>>>(`${this.base}/sync/items`, { params }).pipe(unwrap());
  }

  getSyncLog(itemId: string): Observable<SyncLogModel[]> {
    return this.http.get<QboApiResponse<SyncLogModel[]>>(
      `${this.base}/sync/items/${encodeURIComponent(itemId)}/log`).pipe(unwrap());
  }

  retrySyncItem(itemId: string): Observable<SyncItemModel> {
    return this.http.post<QboApiResponse<SyncItemModel>>(
      `${this.base}/sync/items/${encodeURIComponent(itemId)}/retry`, {}).pipe(unwrap());
  }

  resolveSyncItem(itemId: string, body: ResolveSyncItemRequest): Observable<SyncItemModel> {
    return this.http.post<QboApiResponse<SyncItemModel>>(
      `${this.base}/sync/items/${encodeURIComponent(itemId)}/resolve`, body).pipe(unwrap());
  }

  /** "Push now": asks SCM to send these records to the gateway again. */
  pushNow(kind: SyncKind, externalIds: string[]): Observable<ManualPushResult> {
    return this.http.post<QboApiResponse<ManualPushResult>>(`${this.base}/sync/push`, { kind, externalIds }).pipe(unwrap());
  }

  /** "Sync all": every SCM record of this kind is sent to the gateway (validated and held in dry run). */
  backfill(kind: SyncKind): Observable<BackfillResult> {
    return this.http.post<QboApiResponse<BackfillResult>>(`${this.base}/sync/backfill/${kind}`, {}).pipe(unwrap());
  }

  /** Batch status for badges. */
  lookupStatus(kind: SyncKind, externalIds: string[]): Observable<StatusLookupResult> {
    return this.http.post<QboApiResponse<StatusLookupResult>>(`${this.base}/status/lookup`, { kind, externalIds }).pipe(unwrap());
  }
}
