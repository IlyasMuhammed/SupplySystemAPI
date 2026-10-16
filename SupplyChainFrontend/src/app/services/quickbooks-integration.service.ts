import { Injectable } from '@angular/core';
import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Observable, OperatorFunction, map } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  AccountMappingRowModel, AccountMappingsModel, ApiClientModel, AutoMapAccountsRequest, AutoMapAccountsResult, BackfillResult,
  ConfirmMatchesRequest, ProviderDefaultsModel,
  ConnectResponse, ConnectionStatusModel, CoverageModel, CreateApiClientRequest, DocumentOverrideModel, IntegrationSettingsModel,
  IssueApiKeyRequest, IssuedApiKeyModel, LinkRemoteRequest, ManualPushResult, MatchCandidateModel, MatchDecision, MatchKind,
  MatchScanResultModel, PlatformAppKeysModel, PreflightResultModel, ProviderDefaultModel, QboApiResponse, QboPage,
  ReferenceDataModel, ReleaseHeldResult, RemoteEntityModel, RemoteImportResultModel, ResolveSyncItemRequest, RetryFailedResult,
  SaveAccountMappingsRequest, SaveDocumentOverridesRequest, SavePlatformAppKeysRequest, SaveProviderDefaultsRequest,
  SaveTaxCodeMappingsRequest, SaveTenantAppKeysRequest, SaveTermMappingsRequest, StatusLookupResult, SyncControlDocumentType,
  SyncControlRowModel, SyncItemModel, SyncItemQuery, SyncKind, SyncLogItemModel, SyncLogModel, SyncLogQuery, SyncMode,
  SyncSummaryModel, TaxCodeMappingModel,
  TaxImportResultModel, TenantAppKeysModel, TermMappingModel, TestConnectionResult, UpdateIntegrationSettingsRequest,
  UpdateSyncControlRequest, UpdateSyncControlResult
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
  private readonly platformBase = `${environment.apiUrl}/system/integrations/quickbooks/app`;

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

  // ── Intuit app keys (docs/quickbooks/APP-CREDENTIALS.md) ─────────────────────────────────────

  /** The organization's own Intuit app (FEATURE_QBO_OWN_APP). */
  getOwnAppKeys(): Observable<TenantAppKeysModel> {
    return this.http.get<QboApiResponse<TenantAppKeysModel>>(`${this.base}/app-keys`).pipe(unwrap());
  }

  saveOwnAppKeys(body: SaveTenantAppKeysRequest): Observable<TenantAppKeysModel> {
    return this.http.put<QboApiResponse<TenantAppKeysModel>>(`${this.base}/app-keys`, body).pipe(unwrap());
  }

  deleteOwnAppKeys(): Observable<TenantAppKeysModel> {
    return this.http.delete<QboApiResponse<TenantAppKeysModel>>(`${this.base}/app-keys`).pipe(unwrap());
  }

  /** Super admin: the platform's Intuit app. */
  getPlatformAppKeys(): Observable<PlatformAppKeysModel> {
    return this.http.get<QboApiResponse<PlatformAppKeysModel>>(this.platformBase).pipe(unwrap());
  }

  savePlatformAppKeys(body: SavePlatformAppKeysRequest): Observable<PlatformAppKeysModel> {
    return this.http.put<QboApiResponse<PlatformAppKeysModel>>(this.platformBase, body).pipe(unwrap());
  }

  deletePlatformAppKeys(confirmBreakingChange = false): Observable<PlatformAppKeysModel> {
    const params = new HttpParams().set('confirmBreakingChange', String(confirmBreakingChange));
    return this.http.delete<QboApiResponse<PlatformAppKeysModel>>(this.platformBase, { params }).pipe(unwrap());
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
    if (query.deadLetter) params = params.set('deadLetter', 'true');
    else if (query.state) params = params.set('state', query.state);
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

  // ══ Addendum 44 (API-CONTRACT §4, CTRL) ═══════════════════════════════════════════════════

  // ── Dead letter (D-11) & unlink (D-17) ─────────────────────────────────────────────────────

  /** Queues every Failed / NeedsResolution record again (of one kind, or all). */
  retryFailed(kind?: SyncKind | null): Observable<RetryFailedResult> {
    return this.http.post<QboApiResponse<RetryFailedResult>>(`${this.base}/sync/items/retry-failed`, kind ? { kind } : {}).pipe(unwrap());
  }

  /** Dismisses a dead-letter record: it is not tried again. */
  skipSyncItem(itemId: string): Observable<SyncItemModel> {
    return this.http.post<QboApiResponse<SyncItemModel>>(
      `${this.base}/sync/items/${encodeURIComponent(itemId)}/skip`, {}).pipe(unwrap());
  }

  /** Forgets the link to the QuickBooks record (kept in QuickBooks). 409 while documents synced against it are open. */
  unlinkSyncItem(itemId: string): Observable<SyncItemModel> {
    return this.http.post<QboApiResponse<SyncItemModel>>(
      `${this.base}/sync/items/${encodeURIComponent(itemId)}/unlink`, {}).pipe(unwrap());
  }

  // ── Sync control (D-14) ────────────────────────────────────────────────────────────────────

  getSyncControl(): Observable<SyncControlRowModel[]> {
    return this.http.get<QboApiResponse<SyncControlRowModel[] | { rows: SyncControlRowModel[] }>>(`${this.base}/sync-control`)
      .pipe(unwrap(), map(r => listOf<SyncControlRowModel>(r, 'rows')));
  }

  updateSyncControl(documentType: SyncControlDocumentType, body: UpdateSyncControlRequest): Observable<UpdateSyncControlResult> {
    return this.http.put<QboApiResponse<UpdateSyncControlResult | SyncControlRowModel>>(
      `${this.base}/sync-control/${encodeURIComponent(documentType)}`, body).pipe(
        unwrap(),
        // { row, released } per the contract; a bare row is accepted too.
        map(r => (r && 'row' in r ? r : { row: r as SyncControlRowModel, released: 0 })));
  }

  releaseHeld(documentType: SyncControlDocumentType): Observable<ReleaseHeldResult> {
    return this.http.post<QboApiResponse<ReleaseHeldResult>>(
      `${this.base}/sync-control/${encodeURIComponent(documentType)}/release`, {}).pipe(unwrap());
  }

  // ── Provider defaults & overrides (D-15) ───────────────────────────────────────────────────

  getProviderDefaults(): Observable<ProviderDefaultsModel> {
    return this.http.get<QboApiResponse<ProviderDefaultsModel>>(`${this.base}/provider-defaults`).pipe(unwrap(), map(normaliseDefaults));
  }

  /** Saves the values sent (null / empty removes one); answers the whole screen again. */
  saveProviderDefaults(body: SaveProviderDefaultsRequest): Observable<ProviderDefaultsModel> {
    return this.http.put<QboApiResponse<ProviderDefaultsModel>>(`${this.base}/provider-defaults`, body).pipe(unwrap(), map(normaliseDefaults));
  }

  getDocumentOverrides(): Observable<DocumentOverrideModel[]> {
    return this.http.get<QboApiResponse<DocumentOverrideModel[] | { overrides: DocumentOverrideModel[] }>>(`${this.base}/document-overrides`)
      .pipe(unwrap(), map(r => listOf<DocumentOverrideModel>(r, 'overrides')));
  }

  saveDocumentOverrides(body: SaveDocumentOverridesRequest): Observable<DocumentOverrideModel[]> {
    return this.http.put<QboApiResponse<DocumentOverrideModel[] | { overrides: DocumentOverrideModel[] }>>(`${this.base}/document-overrides`, body)
      .pipe(unwrap(), map(r => listOf<DocumentOverrideModel>(r, 'overrides')));
  }

  // ── Account mapping (D-16) ─────────────────────────────────────────────────────────────────

  getAccountMappings(): Observable<AccountMappingsModel> {
    return this.http.get<QboApiResponse<AccountMappingsModel>>(`${this.base}/accounts`).pipe(unwrap(), map(normaliseAccounts));
  }

  /** By AcctNum, then exact name, then normalised name; only unmapped accounts unless `overwrite`. */
  autoMapAccounts(body: AutoMapAccountsRequest = {}): Observable<AutoMapAccountsResult> {
    return this.http.post<QboApiResponse<AutoMapAccountsResult>>(`${this.base}/accounts/auto-map`, body).pipe(
      unwrap(), map(r => ({ ...r, accounts: r?.accounts ? normaliseAccounts(r.accounts) : null })));
  }

  saveAccountMappings(body: SaveAccountMappingsRequest): Observable<AccountMappingsModel> {
    return this.http.put<QboApiResponse<AccountMappingsModel>>(`${this.base}/accounts`, body).pipe(unwrap(), map(normaliseAccounts));
  }

  // ── Tax import (D-18) ──────────────────────────────────────────────────────────────────────

  importTaxMappings(): Observable<TaxImportResultModel> {
    return this.http.post<QboApiResponse<TaxImportResultModel>>(`${this.base}/tax-mappings/import`, {}).pipe(unwrap());
  }

  // ── Remote-only entities (D-17) ────────────────────────────────────────────────────────────

  importRemote(kind: MatchKind): Observable<RemoteImportResultModel> {
    return this.http.post<QboApiResponse<RemoteImportResultModel>>(`${this.base}/matching/${kind}/import`, {}).pipe(unwrap());
  }

  /** The imported QuickBooks records; `linked=false` gives only those not linked to an SCM record. */
  getRemote(kind: MatchKind, linked: boolean | null = false, pageSize = 200): Observable<RemoteEntityModel[]> {
    let params = new HttpParams();
    if (linked !== null) params = params.set('linked', String(linked));
    // Paged on the server (default 50): the page lists up to this many, then the search narrows it.
    params = params.set('page', '1').set('pageSize', String(pageSize));
    return this.http.get<QboApiResponse<RemoteEntityModel[] | QboPage<RemoteEntityModel>>>(`${this.base}/matching/${kind}/remote`, { params })
      .pipe(unwrap(), map(r => listOf<RemoteEntityModel>(r, 'data')));
  }

  linkRemote(kind: MatchKind, remoteId: string, body: LinkRemoteRequest): Observable<unknown> {
    return this.http.post<QboApiResponse<unknown>>(
      `${this.base}/matching/${kind}/remote/${encodeURIComponent(remoteId)}/link`, body).pipe(unwrap());
  }

  // ── QuickBooks logs (D-34) ─────────────────────────────────────────────────────────────────

  /** Every call to QuickBooks, newest first; failedOnly is sent explicitly (the server's default is true). */
  getCallLog(query: SyncLogQuery = {}): Observable<QboPage<SyncLogItemModel>> {
    let params = new HttpParams().set('failedOnly', String(query.failedOnly ?? true));
    // D-69a / QA-7 — the server defaults to true: sent explicitly both ways (false = the full history).
    params = params.set('unresolvedOnly', String(query.unresolvedOnly ?? true));
    if (query.kind) params = params.set('kind', query.kind);
    if (query.operation) params = params.set('operation', query.operation);
    if (query.outcome) params = params.set('outcome', query.outcome);
    if (query.from) params = params.set('from', query.from);
    if (query.to) params = params.set('to', query.to);
    if (query.search?.trim()) params = params.set('search', query.search.trim());
    params = params.set('page', String(query.page ?? 1)).set('pageSize', String(query.pageSize ?? 50));
    return this.http.get<QboApiResponse<QboPage<SyncLogItemModel>>>(`${this.base}/sync/log`, { params }).pipe(unwrap());
  }

  /** One call with its redacted request / response bodies. */
  getCallLogEntry(id: string): Observable<SyncLogItemModel> {
    return this.http.get<QboApiResponse<SyncLogItemModel>>(`${this.base}/sync/log/${encodeURIComponent(id)}`).pipe(unwrap());
  }

  // ── Coverage & validation (D-27) ───────────────────────────────────────────────────────────

  getCoverage(): Observable<CoverageModel> {
    return this.http.get<QboApiResponse<CoverageModel>>(`${this.base}/coverage`).pipe(unwrap(), map(c => ({
      ...c,
      ready: !!c?.ready,
      entities: c?.entities ?? [],
      blockingIssues: c?.blockingIssues ?? [],
      warnings: c?.warnings ?? []
    })));
  }
}

function normaliseDefaults(d: ProviderDefaultsModel | ProviderDefaultModel[]): ProviderDefaultsModel {
  if (Array.isArray(d)) return { defaults: d };
  return { ...d, defaults: d?.defaults ?? [] };
}

/** An array, or the array under `key` of an object (the contract left the wrapping open). */
export function listOf<T>(value: unknown, key: string): T[] {
  if (Array.isArray(value)) return value as T[];
  const inner = (value as Record<string, unknown> | null | undefined)?.[key];
  if (Array.isArray(inner)) return inner as T[];
  const items = (value as Record<string, unknown> | null | undefined)?.['items'];
  return Array.isArray(items) ? items as T[] : [];
}

function normaliseAccounts(a: AccountMappingsModel): AccountMappingsModel {
  const accounts: AccountMappingRowModel[] = a?.accounts ?? [];
  return {
    ...a,
    glAvailable: a?.glAvailable ?? accounts.length > 0,
    accounts,
    qboAccounts: a?.qboAccounts ?? [],
    totalAccounts: a?.totalAccounts ?? accounts.length,
    mapped: a?.mapped ?? accounts.filter(r => !!r.qboAccountId).length,
    unmapped: a?.unmapped ?? accounts.filter(r => !r.qboAccountId).length,
    invalid: a?.invalid ?? accounts.filter(r => r.status === 'Invalid').length
  };
}
