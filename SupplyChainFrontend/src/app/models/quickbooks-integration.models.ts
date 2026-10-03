// QuickBooks Online integration — the admin API's request/response shapes (/api/integrations/quickbooks/*).
//
// Mirrors, field for field:
//   src/SMS.Modules.Integration/Models/AdminModels.cs            (every DTO below)
//   src/SMS.Shared/Integration/QuickBooks/SyncEnums.cs           (SyncKind, SyncState)
//   src/SMS.Shared/Integration/QuickBooks/GatewayResult.cs       (SyncStatus)
// AdminModels.cs names this file as its twin: change the two together.
//
// JSON is camelCase. Enum-like values travel as their C# names (strings). C# DateTime values arrive
// as ISO strings. Guids arrive as strings.

// ── Enum-like values ────────────────────────────────────────────────────────────────────────────

/** What kind of record is sent to QuickBooks. */
export type SyncKind = 'Customer' | 'Vendor' | 'Item' | 'SalesInvoice' | 'Bill';

export const SYNC_KINDS: readonly SyncKind[] = ['Customer', 'Vendor', 'Item', 'SalesInvoice', 'Bill'];

/** The kinds that can be matched against records already in QuickBooks. */
export type MatchKind = 'Customer' | 'Vendor' | 'Item';

export const MATCH_KINDS: readonly MatchKind[] = ['Customer', 'Vendor', 'Item'];

/** Where one record stands, as the gateway sees it. */
export type SyncState =
  | 'NotSynced'
  | 'Pending'
  | 'InProgress'
  | 'Synced'
  | 'DryRunOk'
  | 'Failed'
  | 'Blocked'
  | 'WaitingOnDependency'
  | 'NeedsResolution'
  | 'Voided';

export const SYNC_STATES: readonly SyncState[] = [
  'NotSynced', 'Pending', 'InProgress', 'Synced', 'DryRunOk', 'Failed', 'Blocked',
  'WaitingOnDependency', 'NeedsResolution', 'Voided'
];

export type ConnectionStatus = 'NotConnected' | 'Connecting' | 'Connected' | 'NeedsSetup' | 'Live' | 'Revoked' | 'Expired';
export type QboEnvironment = 'Sandbox' | 'Production';
export type SyncMode = 'DryRun' | 'Live';
export type ItemTypeDefault = 'NonInventory' | 'Service';
export type PartnerScope = 'OnlyWhenReferenced' | 'AllActive';
export type PreflightStatus = 'Pass' | 'Warn' | 'Fail';
export type MatchConfidence = 'Exact' | 'Probable' | 'None';
export type MatchDecision = 'Pending' | 'Link' | 'CreateNew' | 'Skip';
/** A decision that can be sent — "Pending" is only ever read back. */
export type MatchDecisionChoice = Exclude<MatchDecision, 'Pending'>;
export type ResolveAction = 'LinkRemote' | 'MarkResolved' | 'Requeue';

/** The six scopes an API client can be granted (SMS.Modules.Integration data endpoints, plan §5.2). */
export type ApiScope = 'customers:write' | 'vendors:write' | 'items:write' | 'invoices:write' | 'bills:write' | 'status:read';

export const API_SCOPES: readonly ApiScope[] = [
  'customers:write', 'vendors:write', 'items:write', 'invoices:write', 'bills:write', 'status:read'
];

/** How the OAuth callback reports a failed connection (…/quickbooks?result=error&reason=…). */
export type ConnectErrorReason =
  | 'state_invalid' | 'state_expired' | 'state_used' | 'access_denied' | 'realm_mismatch' | 'realm_in_use'
  | 'exchange_failed' | 'not_configured';

// ── Envelopes ───────────────────────────────────────────────────────────────────────────────────

/** SMS.Shared.Pagination.ApiResponse<T> */
export interface QboApiResponse<T> {
  success: boolean;
  message: string;
  result: T;
}

/** SMS.Shared.Pagination.PaginatedResponse<T> */
export interface QboPage<T> {
  data: T[];
  totalRecords: number;
  page: number;
  pageSize: number;
  totalPages: number;
  hasNext: boolean;
  hasPrevious: boolean;
}

// ── Connection ──────────────────────────────────────────────────────────────────────────────────

export interface ConnectionStatusModel {
  /** False when the deployment has no Intuit app keys configured — the Connect button is disabled. */
  appConfigured: boolean;
  status: ConnectionStatus;
  isConnected: boolean;
  companyName?: string | null;
  realmId?: string | null;
  environment: QboEnvironment;
  homeCurrencyCode?: string | null;
  multiCurrencyEnabled?: boolean | null;
  country?: string | null;
  connectedAt?: string | null;
  connectedByUserId?: number | null;
  accessTokenExpiresAt?: string | null;
  refreshTokenExpiresAt?: string | null;
  /** True when the refresh token's hard expiry is within the warning window. */
  reconnectSoon: boolean;
  mode: SyncMode;
  lastError?: string | null;
}

export interface ConnectResponse {
  consentUrl: string;
}

export interface TestConnectionResult {
  ok: boolean;
  companyName?: string | null;
  message?: string | null;
}

// ── Reference data & preflight ──────────────────────────────────────────────────────────────────

export interface ReferenceItemModel {
  id: string;
  name: string;
  /** Accounts: AccountType (Income, Expense, Cost of Goods Sold, …). Tax codes: "Taxable"/"NonTaxable". */
  type?: string | null;
  subType?: string | null;
  /** Tax codes: the rate in percent where it is a single rate. */
  rate?: number | null;
  /** Terms: due days. */
  days?: number | null;
  active: boolean;
}

export interface ReferenceDataModel {
  fetchedAt?: string | null;
  accounts: ReferenceItemModel[];
  taxCodes: ReferenceItemModel[];
  terms: ReferenceItemModel[];
  /** Id = ISO code. */
  currencies: ReferenceItemModel[];
}

export interface PreflightCheckModel {
  /** e.g. HOME_CURRENCY, MULTICURRENCY, COUNTRY, CUSTOM_TXN_NUMBERS, TAX_CODES, ACCOUNTS_MAPPED, MATCHING_CONFIRMED */
  code: string;
  title: string;
  status: PreflightStatus;
  message: string;
}

export interface PreflightResultModel {
  /** No check failed (warnings allowed). */
  passed: boolean;
  /** Passed, and matching confirmed — the Live switch may be turned on. */
  canGoLive: boolean;
  checks: PreflightCheckModel[];
}

// ── Settings & mappings ─────────────────────────────────────────────────────────────────────────

export interface IntegrationSettingsModel {
  /** Read-only here — changed through POST /mode. */
  mode: SyncMode;
  autoPushCustomers: boolean;
  autoPushVendors: boolean;
  autoPushItems: boolean;
  autoPushSalesInvoices: boolean;
  autoPushBills: boolean;
  itemTypeDefault: ItemTypeDefault;
  partnerScope: PartnerScope;
  defaultIncomeAccountId?: string | null;
  defaultExpenseAccountId?: string | null;
  freightExpenseAccountId?: string | null;
  discountAccountId?: string | null;
  defaultPurchaseTaxCodeId?: string | null;
  documentStartDate?: string | null;
  matchingConfirmedAt?: string | null;
}

export interface UpdateIntegrationSettingsRequest {
  autoPushCustomers: boolean;
  autoPushVendors: boolean;
  autoPushItems: boolean;
  autoPushSalesInvoices: boolean;
  autoPushBills: boolean;
  itemTypeDefault: ItemTypeDefault;
  partnerScope: PartnerScope;
  defaultIncomeAccountId: string | null;
  defaultExpenseAccountId: string | null;
  freightExpenseAccountId: string | null;
  discountAccountId: string | null;
  defaultPurchaseTaxCodeId: string | null;
  /** yyyy-MM-dd, or null for no start date. */
  documentStartDate: string | null;
}

/**
 * One row of the tax mapping table. Two kinds (SAP alignment S-11): a code row (`sourceTaxCode` set) maps an
 * SMS tax code — every line carrying that code uses it; a percent row (`sourceTaxCode` null) maps a bare rate,
 * for lines without a code (invoices from before tax codes, other systems). Every active SMS tax code has a code
 * row, even one no document uses yet, so it can be mapped up front.
 */
export interface TaxCodeMappingModel {
  /** The SMS tax code (upper-case). Null or absent for a percent row. */
  sourceTaxCode?: string | null;
  /**
   * Code rows for an active SMS tax code (Settings → Tax Codes): its name there, e.g. "GST 17%". Null or absent
   * otherwise — a code only seen on older documents, one added on the mapping screen, every percent row. Display only.
   */
  sourceTaxCodeName?: string | null;
  /** Code rows for an active SMS tax code: who may use it — 'SALES' | 'PURCHASE' | 'BOTH'. Null or absent otherwise. Display only. */
  sourceTaxCodeUsage?: string | null;
  /** Percent row: the rate mapped. Code row: the code's rate (the one most of its lines carry) — for information. */
  taxPercent: number;
  qboTaxCodeId?: string | null;
  qboTaxCodeName?: string | null;
  /** How many stored payload lines use this code (code row) or this rate without a code (percent row). 0 when none does yet. */
  timesSeen: number;
  /** Unmapped code rows: the QuickBooks tax code the code's rate is mapped to as a percent row. Never applied by itself. */
  suggestedQboTaxCodeId?: string | null;
  suggestedQboTaxCodeName?: string | null;
}

export interface TaxCodeMappingItem {
  /** The SMS tax code for a code row (max 20 characters, case-insensitive). Omit or null for a percent row. */
  sourceTaxCode?: string | null;
  /** Percent row: the rate (0–100). Code row: the code's rate, stored for information. */
  taxPercent: number;
  /** Null or empty removes the mapping. */
  qboTaxCodeId: string | null;
}

export interface SaveTaxCodeMappingsRequest {
  mappings: TaxCodeMappingItem[];
}

export interface TermMappingModel {
  paymentTermExternalId: string;
  paymentTermName?: string | null;
  qboTermId?: string | null;
  qboTermName?: string | null;
}

export interface TermMappingItem {
  paymentTermExternalId: string;
  paymentTermName: string | null;
  /** Null or empty removes the mapping. */
  qboTermId: string | null;
}

export interface SaveTermMappingsRequest {
  mappings: TermMappingItem[];
}

export interface SetModeRequest {
  mode: SyncMode;
}

// ── API clients ─────────────────────────────────────────────────────────────────────────────────

export interface ApiClientKeyModel {
  id: string;
  keyPrefix: string;
  createdAt: string;
  expiresAt?: string | null;
  revokedAt?: string | null;
  lastUsedAt?: string | null;
  isActive: boolean;
}

export interface ApiClientModel {
  id: string;
  name: string;
  scopes: string[];
  isActive: boolean;
  createdAt: string;
  keys: ApiClientKeyModel[];
}

export interface CreateApiClientRequest {
  name: string;
  scopes: string[];
}

export interface IssueApiKeyRequest {
  expiresAt: string | null;
}

/** Returned once, when a key is issued. The key is never retrievable again. */
export interface IssuedApiKeyModel {
  keyId: string;
  apiKey: string;
  keyPrefix: string;
  /** The value to send as X-Tenant-Id. */
  tenantId: string;
  expiresAt?: string | null;
}

// ── Matching ────────────────────────────────────────────────────────────────────────────────────

export interface MatchScanResultModel {
  kind: string;
  localCount: number;
  remoteCount: number;
  exact: number;
  probable: number;
  unmatched: number;
}

export interface MatchCandidateModel {
  id: string;
  kind: MatchKind;
  externalId: string;
  sourceSystem: string;
  localLabel: string;
  remoteId?: string | null;
  remoteName?: string | null;
  confidence: MatchConfidence;
  reason?: string | null;
  decision: MatchDecision;
}

export interface MatchDecisionItem {
  candidateId: string;
  decision: MatchDecisionChoice;
  /** For Link: the QuickBooks id to link (defaults to the proposed one). */
  remoteId: string | null;
}

export interface ConfirmMatchesRequest {
  decisions: MatchDecisionItem[];
}

export interface ConfirmMatchingCompleteRequest {
  confirmed: boolean;
}

// ── Sync dashboard ──────────────────────────────────────────────────────────────────────────────

export interface SyncKindSummaryModel {
  kind: SyncKind;
  total: number;
  /** Keyed by SyncState name. */
  countsByState: Partial<Record<SyncState, number>>;
}

export interface SyncSummaryModel {
  connectionStatus: ConnectionStatus;
  mode: SyncMode;
  queueDepth: number;
  lastRunAt?: string | null;
  kinds: SyncKindSummaryModel[];
}

export interface SyncItemQuery {
  kind?: SyncKind | null;
  state?: SyncState | null;
  search?: string | null;
  page?: number;
  pageSize?: number;
}

export interface SyncItemModel {
  id: string;
  kind: SyncKind;
  externalId: string;
  sourceSystem: string;
  displayLabel: string;
  state: SyncState;
  remoteId?: string | null;
  remoteDocNumber?: string | null;
  deepLink?: string | null;
  lastErrorCode?: string | null;
  lastError?: string | null;
  warning?: string | null;
  lastSyncedAt?: string | null;
  updatedAt?: string | null;
  attemptCount: number;
}

export interface SyncLogModel {
  id: string;
  operation: string;
  outcome: string;
  errorCode?: string | null;
  message?: string | null;
  durationMs: number;
  intuitTid?: string | null;
  createdAt: string;
  requestJson?: string | null;
  responseJson?: string | null;
}

export interface ResolveSyncItemRequest {
  /** LinkRemote (link to remoteId) | MarkResolved (accept as synced without a remote change) | Requeue */
  action: ResolveAction;
  remoteId: string | null;
}

export interface ManualPushRequest {
  kind: SyncKind;
  externalIds: string[];
}

export interface ManualPushResult {
  requested: number;
}

export interface BackfillResult {
  kind: string;
  sent: number;
}

export interface StatusLookupRequest {
  kind: SyncKind;
  externalIds: string[];
}

/** SMS.Shared.Integration.QuickBooks.SyncStatus — one record's status, for badges. */
export interface SyncStatus {
  kind: SyncKind;
  externalId: string;
  state: SyncState;
  remoteId?: string | null;
  remoteDocNumber?: string | null;
  lastErrorCode?: string | null;
  lastError?: string | null;
  lastSyncedAt?: string | null;
  deepLink?: string | null;
}

export interface StatusLookupResult {
  items: SyncStatus[];
}
