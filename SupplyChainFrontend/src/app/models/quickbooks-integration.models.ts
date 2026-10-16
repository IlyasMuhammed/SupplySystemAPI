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

/** What kind of record is sent to QuickBooks. A44 (D-19..D-24) added the six payment / credit kinds. */
export type SyncKind =
  | 'Customer' | 'Vendor' | 'Item' | 'SalesInvoice' | 'Bill'
  | 'Payment' | 'BillPayment' | 'SalesReceipt' | 'CreditMemo' | 'RefundReceipt' | 'VendorCredit';

export const SYNC_KINDS: readonly SyncKind[] = [
  'Customer', 'Vendor', 'Item', 'SalesInvoice', 'Bill',
  'Payment', 'BillPayment', 'SalesReceipt', 'CreditMemo', 'RefundReceipt', 'VendorCredit'
];

/** The kinds "Sync all" (POST sync/backfill/{kind}) has always served; the A44 kinds are sent by their documents. */
export const BACKFILL_KINDS: readonly SyncKind[] = ['Customer', 'Vendor', 'Item', 'SalesInvoice', 'Bill'];

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

/** Where a set of Intuit app keys came from (docs/quickbooks/APP-CREDENTIALS.md D-2). */
export type QboAppSource = 'Tenant' | 'Platform' | 'Configuration';

export interface ConnectionStatusModel {
  /** False when no Intuit app keys are available to this organization — the Connect button is disabled. */
  appConfigured: boolean;
  /** Which keys a new connection would use; null when none. */
  appSource?: QboAppSource | null;
  /** Which keys the current connection was made with (null for an older connection, or none). */
  connectedWithAppSource?: QboAppSource | null;
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

// ── Intuit app keys (docs/quickbooks/APP-CREDENTIALS.md) — the secret is write-only, never returned ──

/** System Administration › QuickBooks App (super admin). */
export interface PlatformAppKeysModel {
  isConfigured: boolean;
  /** Platform (saved here) | Configuration (server environment) | null. */
  source?: 'Platform' | 'Configuration' | null;
  clientId?: string | null;
  hasSecret: boolean;
  environment: QboEnvironment;
  /** The callback URL to register on every Intuit app. */
  redirectUri?: string | null;
  frontendReturnUrl?: string | null;
  savedInDatabase: boolean;
  configurationAvailable: boolean;
  configurationClientId?: string | null;
  connectionsUsingPlatformKeys: number;
  organizationsWithOwnKeys: number;
  modifiedAt?: string | null;
  modifiedBy?: number | null;
}

export interface SavePlatformAppKeysRequest {
  clientId: string;
  /** Blank keeps the stored secret. */
  clientSecret?: string | null;
  environment: QboEnvironment;
  redirectUri: string;
  frontendReturnUrl?: string | null;
  /** Required when live connections use the keys being replaced (they must reconnect). */
  confirmBreakingChange?: boolean;
}

/** The organization's own Intuit app card (FEATURE_QBO_OWN_APP). */
export interface TenantAppKeysModel {
  featureEnabled: boolean;
  hasOwnKeys: boolean;
  clientId?: string | null;
  hasSecret: boolean;
  environment: QboEnvironment;
  /** The callback URL to register on the organization's Intuit app. */
  redirectUriToRegister?: string | null;
  newConnectionsUse?: QboAppSource | null;
  /** Connected through these keys: changing the Client ID or environment needs a disconnect first. */
  connectedWithOwnKeys: boolean;
  modifiedAt?: string | null;
}

export interface SaveTenantAppKeysRequest {
  clientId: string;
  /** Blank keeps the stored secret. */
  clientSecret?: string | null;
  environment: QboEnvironment;
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
  /** Accounts: QuickBooks' account number, when used (A44 D-16). */
  acctNum?: string | null;
  /** Classes / departments: the full "Parent:Child" name. */
  fullName?: string | null;
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
  /** A44 D-15 — empty until the reference data is read from QuickBooks again after the upgrade. */
  classes?: ReferenceItemModel[] | null;
  /** QuickBooks "Locations" (the API calls them Departments). */
  departments?: ReferenceItemModel[] | null;
  /** Type: CREDIT_CARD | NON_CREDIT_CARD when QuickBooks says. */
  paymentMethods?: ReferenceItemModel[] | null;
  /** The company's preferences; null values = not known yet. */
  tracking?: ReferenceTrackingModel | null;
}

export interface ReferenceTrackingModel {
  classTrackingPerTransaction?: boolean | null;
  classTrackingPerLine?: boolean | null;
  departmentTracking?: boolean | null;
  /** What the company calls locations (Location, Department, Division…). */
  departmentTerminology?: string | null;
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
  /** "Sync POS sales" — off by default; POS sales are held (not sent) while it is off. */
  autoPushPosSales?: boolean;
  /** POS sales held and waiting to be sent. */
  posSalesWaiting?: number;
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
  /** "Sync POS sales". Left out = unchanged on the server. */
  autoPushPosSales?: boolean;
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
  /** A44 D-11 — Failed + NeedsResolution together (sent as deadLetter=true; `state` is then left out). */
  deadLetter?: boolean;
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
  /** ExternalIds left alone because they were skipped in matching (a push never overrides that). */
  skippedInMatching?: string[];
  /** ExternalIds SCM has nothing to send for (no such document / nothing postable). */
  notFound?: string[];
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

// ══ Addendum 44 (docs/financial-posting/API-CONTRACT.md §4, CTRL) ═══════════════════════════════════
// Mirrors src/SMS.Modules.Integration/Models/PostingAdminModels.cs field for field (change the two together).

// ── Sync control (D-14) ─────────────────────────────────────────────────────────────────────────

export type SyncControlDocumentType =
  | 'Customer' | 'Vendor' | 'Item' | 'SalesInvoice' | 'CreditNote' | 'Payment' | 'POSSale' | 'POSReturn'
  | 'PurchaseBill' | 'BillPayment' | 'VendorCredit';

export const SYNC_CONTROL_DOCUMENT_TYPES: readonly SyncControlDocumentType[] = [
  'Customer', 'Vendor', 'Item', 'SalesInvoice', 'CreditNote', 'Payment', 'POSSale', 'POSReturn',
  'PurchaseBill', 'BillPayment', 'VendorCredit'
];

export interface SyncControlRowModel {
  documentType: SyncControlDocumentType;
  displayLabel?: string | null;
  isEnabled: boolean;
  /** While disabled: keep (hold) the records to send on enable, instead of ignoring them. */
  holdWhenDisabled: boolean;
  /** Process | Hold | Skip */
  action?: 'Process' | 'Hold' | 'Skip' | string | null;
  /** Records of this type held now. */
  heldCount?: number | null;
  /** Nothing stored for the type yet: the value shown is the default. */
  isDefault?: boolean | null;
  modifiedBy?: number | null;
  modifiedAt?: string | null;
}

/** GET sync-control. */
export interface SyncControlModel {
  rows: SyncControlRowModel[];
  totalHeld: number;
}

export interface UpdateSyncControlRequest {
  isEnabled: boolean;
  holdWhenDisabled: boolean;
  /** Enabling a type with held records sends them all; without this the server answers 409 { documentType, heldCount }. */
  confirmRelease: boolean;
}

export interface UpdateSyncControlResult {
  row: SyncControlRowModel;
  /** Held records offered again because the type was enabled. */
  released: number;
  /** Queued-but-unsent records held because the type was disabled with hold. */
  heldNow?: number | null;
}

/** The 409 result of PUT sync-control/{type} without confirmRelease. */
export interface ReleaseConfirmationModel {
  documentType: string;
  heldCount: number;
}

export interface ReleaseHeldResult {
  documentType?: string;
  released: number;
  stillHeld?: number | null;
}

// ── Provider defaults & document overrides (D-15) ───────────────────────────────────────────────

export interface ProviderDefaultModel {
  /** ClassRef, DepartmentRef, SalesTermRef, PosDefaultCustomerRef, RefundFromAccountRef, PaymentMethodRef.{M},
   *  DepositToAccountRef.{M}, BankAccountRef.{M} */
  settingKey: string;
  /** The key without the method. */
  baseKey?: string | null;
  /** CASH | CHEQUE | BANK_TRANSFER | CARD | ONLINE | MOBILE for method keys. */
  method?: string | null;
  label?: string | null;
  /** Class | Department | Term | PaymentMethod | Account | Customer */
  referenceKind?: string | null;
  /** Account keys: the QuickBooks account types allowed. */
  accountTypes?: string[] | null;
  /** A QuickBooks id; null = not set. */
  settingValue?: string | null;
  valueName?: string | null;
  description?: string | null;
  /** The stored value is no longer usable, or is not sent (tracking off). */
  warning?: string | null;
  modifiedBy?: number | null;
  modifiedAt?: string | null;
}

/** GET / PUT provider-defaults. */
export interface ProviderDefaultsModel {
  /** QuickBooks "Track classes"; null = not known (read the reference data again). */
  classTrackingEnabled?: boolean | null;
  /** QuickBooks "Track locations"; null = not known. */
  departmentTrackingEnabled?: boolean | null;
  departmentTerminology?: string | null;
  defaults: ProviderDefaultModel[];
  /** Document types an override can be set for. */
  documentTypes?: string[] | null;
}

export interface SaveProviderDefaultsRequest {
  /** Null or empty settingValue removes the default. */
  values: { settingKey: string; settingValue: string | null; description?: string | null }[];
}

export interface DocumentOverrideModel {
  documentType: SyncControlDocumentType | string;
  settingKey: string;
  settingValue?: string | null;
  valueName?: string | null;
  warning?: string | null;
  modifiedBy?: number | null;
  modifiedAt?: string | null;
}

export interface SaveDocumentOverridesRequest {
  /** The full set; null / empty settingValue removes one. */
  overrides: { documentType: string; settingKey: string; settingValue: string | null }[];
}

// ── Account mapping (D-16) ──────────────────────────────────────────────────────────────────────

export type AccountMappingStatus = 'Mapped' | 'Unmapped' | 'Invalid';

export interface AccountMappingRowModel {
  /** The A42 leaf account. */
  accountUuid: string;
  code: string;
  name: string;
  accountType?: string | null;
  isActive?: boolean;
  qboAccountId?: string | null;
  qboAccountName?: string | null;
  qboAccountType?: string | null;
  /** Invalid = mapped to an account QuickBooks no longer has active. */
  status?: AccountMappingStatus | string;
  /** A42 default keys assigned to this account (DEFAULT_REVENUE, …). */
  defaultKeys?: string[] | null;
}

export interface QboAccountModel {
  id: string;
  name: string;
  acctNum?: string | null;
  type: string;
  subType?: string | null;
  active: boolean;
  /** How many SCM accounts map to it. */
  mappedCount?: number;
}

export interface DefaultRoleMappingModel {
  defaultKey: string;
  qboAccountId?: string | null;
  qboAccountName?: string | null;
  /** What QuickBooks documents use it for when the settings account is empty, e.g. "Default income account". */
  usedFor?: string | null;
}

export interface AccountMappingsModel {
  /** False when the general ledger is not set up or not licensed: nothing to map. */
  glAvailable: boolean;
  referenceFetchedAt?: string | null;
  totalAccounts: number;
  mapped: number;
  unmapped: number;
  invalid: number;
  accounts: AccountMappingRowModel[];
  qboAccounts: QboAccountModel[];
  defaultRoles?: DefaultRoleMappingModel[] | null;
}

export interface SaveAccountMappingsRequest {
  /** Null / empty qboAccountId removes the mapping. */
  mappings: { accountUuid: string; qboAccountId: string | null }[];
}

export interface AutoMapAccountsRequest {
  /** False (default): only unmapped accounts are touched. */
  overwrite?: boolean;
  includeInactive?: boolean;
}

export interface AutoMappedAccountModel {
  accountUuid: string;
  code: string;
  name: string;
  qboAccountId: string;
  qboAccountName: string;
  /** AcctNum | ExactName | NormalizedName */
  rule: string;
}

export interface AmbiguousAccountModel {
  accountUuid: string;
  code: string;
  name: string;
  rule: string;
  candidates: QboAccountModel[];
}

export interface AutoMapAccountsResult {
  mapped: number;
  matches?: AutoMappedAccountModel[] | null;
  ambiguous?: AmbiguousAccountModel[] | null;
  /** Accounts no rule matched. */
  unmatched?: number | null;
  /** The mapping screen after the save. */
  accounts?: AccountMappingsModel | null;
}

// ── Tax import (D-18) ───────────────────────────────────────────────────────────────────────────

export interface TaxImportRowModel {
  /** Null for a bare-rate row. */
  sourceTaxCode?: string | null;
  sourceTaxCodeName?: string | null;
  taxPercent: number;
  qboTaxCodeId?: string | null;
  qboTaxCodeName?: string | null;
  /** Why it was saved, or why it needs a person. */
  reason: string;
  candidates?: ReferenceItemModel[] | null;
}

export interface TaxImportResultModel {
  saved: number;
  alreadyMapped?: number | null;
  savedRows?: TaxImportRowModel[] | null;
  needsReview?: TaxImportRowModel[] | null;
  /** The tax mapping screen after the save (same rows as GET tax-mappings). */
  mappings?: TaxCodeMappingModel[] | null;
}

// ── QuickBooks record import / link (D-17) ──────────────────────────────────────────────────────

export interface RemoteImportResultModel {
  kind: string;
  read: number;
  added?: number;
  updated?: number;
  removed?: number;
  linked?: number;
  unlinked?: number;
  importedAt?: string | null;
}

export interface RemoteEntityModel {
  remoteId: string;
  kind?: string;
  name: string;
  companyName?: string | null;
  email?: string | null;
  taxId?: string | null;
  accountNumber?: string | null;
  sku?: string | null;
  currencyCode?: string | null;
  active?: boolean;
  importedAt?: string | null;
  linked?: boolean;
  /** The sync item (EntityMap uuid) linked to it. */
  linkedItemId?: string | null;
  linkedExternalId?: string | null;
  linkedLabel?: string | null;
  deepLink?: string | null;
}

export interface LinkRemoteRequest {
  /** The SCM record's id as the gateway knows it (partner / variant uuid). */
  externalId: string;
}

// ── Dead letter & unlink (D-11, D-17) ───────────────────────────────────────────────────────────

export interface RetryFailedResult {
  /** Dead-letter records found (Failed or NeedsResolution). */
  found?: number;
  requeued: number;
  skipped?: { id: string; kind: string; displayLabel: string; reason: string }[] | null;
}

/** The 409 result of POST sync/items/{id}/unlink. */
export interface UnlinkRefusedModel {
  openDocuments: number;
  /** Up to 20 of their labels. */
  examples: string[];
}

// ── QuickBooks logs (D-34) ──────────────────────────────────────────────────────────────────────

export const SYNC_LOG_OUTCOMES = [
  'Succeeded', 'Refused', 'Duplicate', 'StaleObject', 'NotFound', 'Throttled', 'Transient', 'AuthRevoked', 'Exception', 'DryRun'
] as const;

export const SYNC_LOG_OPERATIONS = ['Create', 'Update', 'Void', 'Delete', 'Find', 'Query', 'Read', 'Refresh', 'Preflight'] as const;

export interface SyncLogQuery {
  /** Default true on the server: every outcome except Succeeded and DryRun. */
  failedOnly?: boolean;
  /**
   * QA-7 / D-69a — default true on the server: only calls whose record's current state is not Synced / Voided (calls with
   * no record — scans, imports — are left out too). False = the full history.
   */
  unresolvedOnly?: boolean;
  kind?: SyncKind | null;
  operation?: string | null;
  outcome?: string | null;
  /** yyyy-MM-dd (inclusive; a date means the whole day). */
  from?: string | null;
  to?: string | null;
  search?: string | null;
  page?: number;
  pageSize?: number;
}

/** GET sync/log (list: no bodies) and GET sync/log/{id} (with the redacted bodies). */
export interface SyncLogItemModel {
  id: string;
  createdAt: string;
  /** Null for calls that belong to no record (a matching scan, an import). */
  kind?: SyncKind | null;
  /** The sync item (EntityMap uuid). */
  itemId?: string | null;
  externalId?: string | null;
  sourceSystem?: string | null;
  displayLabel?: string | null;
  operation: string;
  outcome: string;
  errorCode?: string | null;
  message?: string | null;
  durationMs: number;
  intuitTid?: string | null;
  /** The record's state now. */
  state?: SyncState | null;
  remoteId?: string | null;
  remoteDocNumber?: string | null;
  deepLink?: string | null;
  requestJson?: string | null;
  responseJson?: string | null;
}

// ── Coverage (D-27) ─────────────────────────────────────────────────────────────────────────────

/** Customer | Vendor | Item (from the server), plus TaxCode / Account / ProviderDefault rows the page derives. */
export interface CoverageKindModel {
  kind: string;
  label?: string | null;
  total: number;
  mapped: number;
  skipped?: number | null;
  pending?: number | null;
  failed?: number | null;
  remoteImported?: number | null;
  remoteUnlinked?: number | null;
  percentMapped?: number | null;
  /** Information only (account mappings are not needed to send documents, D-16). */
  informational?: boolean | null;
}

export interface CoverageTaxModel {
  total: number;
  mapped: number;
  /** Codes (and "x%" rates) without a mapping. */
  unmapped: string[];
}

export interface CoverageAccountsModel {
  glAvailable: boolean;
  total: number;
  mapped: number;
  unmapped: number;
  /** Always false (D-16): shown for information. */
  blocking: boolean;
}

export interface CoverageIssueModel {
  /** NOT_CONNECTED, REFERENCE_DATA, PREFLIGHT, DEPOSIT_ACCOUNTS, BANK_ACCOUNTS, CLASS_DEFAULT, DEPARTMENT_DEFAULT, TAX_UNMAPPED, ENTITIES_PENDING… */
  code: string;
  /** Connection | Preflight | SyncControl | Defaults | Tax | Accounts | Customer | Vendor | Item */
  area?: string | null;
  message: string;
  documentType?: string | null;
  /** The tab that fixes it: connection | setup | sync-control | defaults | accounts | tax | match */
  tab?: string | null;
}

export interface CoverageDefaultsModel {
  required: number;
  set: number;
  missing: CoverageIssueModel[];
}

export interface CoverageModel {
  /** No blocking issue. */
  ready: boolean;
  connectionStatus?: string | null;
  mode?: string | null;
  referenceFetchedAt?: string | null;
  entities: CoverageKindModel[];
  taxCodes?: CoverageTaxModel | null;
  accounts?: CoverageAccountsModel | null;
  providerDefaults?: CoverageDefaultsModel | null;
  blockingIssues: CoverageIssueModel[];
  warnings: CoverageIssueModel[];
}

