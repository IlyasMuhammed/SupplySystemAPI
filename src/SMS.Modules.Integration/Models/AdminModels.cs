using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Models;

// Request/response shapes of the admin endpoints (/api/integrations/quickbooks/*, JWT). These are
// the contract the Angular screens are built against — change them only together with
// SupplyChainFrontend/src/app/models/quickbooks-integration.models.ts.
//
// Every response is wrapped in SMS.Shared.Pagination.ApiResponse<T> ({ success, message, result }).
// Enum-like values travel as strings (the names of the enums in Domain/IntegrationEnums.cs).

// ── Connection ──────────────────────────────────────────────────────────────────────────────

public sealed class ConnectionStatusModel
{
    /// <summary>False when the deployment has no Intuit app keys configured — the Connect button is disabled.</summary>
    public bool      AppConfigured        { get; set; }
    /// <summary>NotConnected | Connecting | Connected | NeedsSetup | Live | Revoked | Expired</summary>
    public string    Status               { get; set; } = "NotConnected";
    public bool      IsConnected          { get; set; }
    public string?   CompanyName          { get; set; }
    public string?   RealmId              { get; set; }
    /// <summary>Sandbox | Production</summary>
    public string    Environment          { get; set; } = "Sandbox";
    public string?   HomeCurrencyCode     { get; set; }
    public bool?     MultiCurrencyEnabled { get; set; }
    public string?   Country              { get; set; }
    public DateTime? ConnectedAt          { get; set; }
    public int?      ConnectedByUserId    { get; set; }
    public DateTime? AccessTokenExpiresAt { get; set; }
    public DateTime? RefreshTokenExpiresAt { get; set; }
    /// <summary>True when the refresh token's hard expiry is within the warning window.</summary>
    public bool      ReconnectSoon        { get; set; }
    /// <summary>DryRun | Live</summary>
    public string    Mode                 { get; set; } = "DryRun";
    public string?   LastError            { get; set; }
}

public sealed class ConnectResponse
{
    public string ConsentUrl { get; set; } = string.Empty;
}

public sealed class TestConnectionResult
{
    public bool    Ok          { get; set; }
    public string? CompanyName { get; set; }
    public string? Message     { get; set; }
}

// ── Reference data & preflight ─────────────────────────────────────────────────────────────

public sealed class ReferenceItemModel
{
    public string   Id       { get; set; } = string.Empty;
    public string   Name     { get; set; } = string.Empty;
    /// <summary>Accounts: AccountType (Income, Expense, Cost of Goods Sold, …). Tax codes: "Taxable"/"NonTaxable".</summary>
    public string?  Type     { get; set; }
    public string?  SubType  { get; set; }
    /// <summary>Tax codes: the rate in percent where it is a single rate.</summary>
    public decimal? Rate     { get; set; }
    /// <summary>Terms: due days.</summary>
    public int?     Days     { get; set; }
    public bool     Active   { get; set; } = true;
}

public sealed class ReferenceDataModel
{
    public DateTime?                FetchedAt  { get; set; }
    public List<ReferenceItemModel> Accounts   { get; set; } = new();
    public List<ReferenceItemModel> TaxCodes   { get; set; } = new();
    public List<ReferenceItemModel> Terms      { get; set; } = new();
    /// <summary>Id = ISO code.</summary>
    public List<ReferenceItemModel> Currencies { get; set; } = new();
}

public sealed class PreflightCheckModel
{
    /// <summary>e.g. HOME_CURRENCY, MULTICURRENCY, COUNTRY, CUSTOM_TXN_NUMBERS, TAX_CODES, ACCOUNTS_MAPPED, MATCHING_CONFIRMED</summary>
    public string Code    { get; set; } = string.Empty;
    public string Title   { get; set; } = string.Empty;
    /// <summary>Pass | Warn | Fail</summary>
    public string Status  { get; set; } = "Pass";
    public string Message { get; set; } = string.Empty;
}

public sealed class PreflightResultModel
{
    /// <summary>No check failed (warnings allowed).</summary>
    public bool                      Passed    { get; set; }
    /// <summary>Passed, and matching confirmed — the Live switch may be turned on.</summary>
    public bool                      CanGoLive { get; set; }
    public List<PreflightCheckModel> Checks    { get; set; } = new();
}

// ── Settings & mappings ────────────────────────────────────────────────────────────────────

public sealed class IntegrationSettingsModel
{
    /// <summary>DryRun | Live (read-only here — changed through POST /mode)</summary>
    public string    Mode                     { get; set; } = "DryRun";
    public bool      AutoPushCustomers        { get; set; } = true;
    public bool      AutoPushVendors          { get; set; } = true;
    public bool      AutoPushItems            { get; set; } = true;
    public bool      AutoPushSalesInvoices    { get; set; } = true;
    public bool      AutoPushBills            { get; set; } = true;
    /// <summary>NonInventory | Service</summary>
    public string    ItemTypeDefault          { get; set; } = "NonInventory";
    /// <summary>OnlyWhenReferenced | AllActive</summary>
    public string    PartnerScope             { get; set; } = "OnlyWhenReferenced";
    public string?   DefaultIncomeAccountId   { get; set; }
    public string?   DefaultExpenseAccountId  { get; set; }
    public string?   FreightExpenseAccountId  { get; set; }
    public string?   DiscountAccountId        { get; set; }
    public string?   DefaultPurchaseTaxCodeId { get; set; }
    public DateTime? DocumentStartDate        { get; set; }
    public DateTime? MatchingConfirmedAt      { get; set; }
}

public sealed class UpdateIntegrationSettingsRequest
{
    public bool      AutoPushCustomers        { get; set; } = true;
    public bool      AutoPushVendors          { get; set; } = true;
    public bool      AutoPushItems            { get; set; } = true;
    public bool      AutoPushSalesInvoices    { get; set; } = true;
    public bool      AutoPushBills            { get; set; } = true;
    public string    ItemTypeDefault          { get; set; } = "NonInventory";
    public string    PartnerScope             { get; set; } = "OnlyWhenReferenced";
    public string?   DefaultIncomeAccountId   { get; set; }
    public string?   DefaultExpenseAccountId  { get; set; }
    public string?   FreightExpenseAccountId  { get; set; }
    public string?   DiscountAccountId        { get; set; }
    public string?   DefaultPurchaseTaxCodeId { get; set; }
    public DateTime? DocumentStartDate        { get; set; }
}

/// <summary>
/// One row of the tax mapping screen. Two kinds (plan S-11): a <b>code row</b> (<see cref="SourceTaxCode"/>
/// set) maps an SMS tax code, and is what every line carrying that code uses; a <b>percent row</b>
/// (<see cref="SourceTaxCode"/> null) maps a bare rate, for lines without a code (records from before tax
/// codes, other systems).
/// </summary>
public sealed class TaxCodeMappingModel
{
    /// <summary>The SMS tax code (upper-case). Null for a percent row.</summary>
    public string? SourceTaxCode  { get; set; }
    /// <summary>Code rows of an active SMS tax code: its name in SMS (e.g. "GST 17%"). Null otherwise.</summary>
    public string? SourceTaxCodeName  { get; set; }
    /// <summary>Code rows of an active SMS tax code: SALES | PURCHASE | BOTH — which documents use it. Null otherwise.</summary>
    public string? SourceTaxCodeUsage { get; set; }
    /// <summary>
    /// Percent row: the rate mapped. Code row: the code's rate — the one most lines carrying the code
    /// have, else its current rate in SMS, else the rate stored with the mapping (informational; a code row
    /// never maps a rate).
    /// </summary>
    public decimal TaxPercent     { get; set; }
    public string? QboTaxCodeId   { get; set; }
    public string? QboTaxCodeName { get; set; }
    /// <summary>How many stored payload lines use this code (code row) or this rate without a code (percent row). 0 for a mapping no payload uses yet.</summary>
    public int     TimesSeen      { get; set; }
    /// <summary>
    /// Code rows not mapped yet: the QuickBooks tax code the code's rate is mapped to as a percent row, offered
    /// as a starting point. Never applied automatically — two codes can share a rate.
    /// </summary>
    public string? SuggestedQboTaxCodeId   { get; set; }
    public string? SuggestedQboTaxCodeName { get; set; }
}

public sealed class TaxCodeMappingItem
{
    /// <summary>The SMS tax code for a code row (max 20 characters, compared case-insensitively). Null or empty: a percent row.</summary>
    public string? SourceTaxCode { get; set; }
    /// <summary>Percent row: the rate (0–100, kept to four places). Code row: the code's rate, stored for information.</summary>
    public decimal TaxPercent    { get; set; }
    /// <summary>Null or empty removes the mapping.</summary>
    public string? QboTaxCodeId  { get; set; }
}

public sealed class SaveTaxCodeMappingsRequest
{
    public List<TaxCodeMappingItem> Mappings { get; set; } = new();
}

public sealed class TermMappingModel
{
    public string  PaymentTermExternalId { get; set; } = string.Empty;
    public string? PaymentTermName       { get; set; }
    public string? QboTermId             { get; set; }
    public string? QboTermName           { get; set; }
}

public sealed class TermMappingItem
{
    public string  PaymentTermExternalId { get; set; } = string.Empty;
    public string? PaymentTermName       { get; set; }
    /// <summary>Null or empty removes the mapping.</summary>
    public string? QboTermId             { get; set; }
}

public sealed class SaveTermMappingsRequest
{
    public List<TermMappingItem> Mappings { get; set; } = new();
}

public sealed class SetModeRequest
{
    /// <summary>DryRun | Live</summary>
    public string Mode { get; set; } = "DryRun";
}

// ── API clients ────────────────────────────────────────────────────────────────────────────

public sealed class ApiClientKeyModel
{
    public Guid      Id         { get; set; }
    public string    KeyPrefix  { get; set; } = string.Empty;
    public DateTime  CreatedAt  { get; set; }
    public DateTime? ExpiresAt  { get; set; }
    public DateTime? RevokedAt  { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool      IsActive   { get; set; }
}

public sealed class ApiClientModel
{
    public Guid                    Id        { get; set; }
    public string                  Name      { get; set; } = string.Empty;
    public List<string>            Scopes    { get; set; } = new();
    public bool                    IsActive  { get; set; }
    public DateTime                CreatedAt { get; set; }
    public List<ApiClientKeyModel> Keys      { get; set; } = new();
}

public sealed class CreateApiClientRequest
{
    public string       Name   { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = new();
}

public sealed class IssueApiKeyRequest
{
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>Returned once, when a key is issued. The key is never retrievable again.</summary>
public sealed class IssuedApiKeyModel
{
    public Guid      KeyId     { get; set; }
    public string    ApiKey    { get; set; } = string.Empty;
    public string    KeyPrefix { get; set; } = string.Empty;
    /// <summary>The value to send as X-Tenant-Id.</summary>
    public Guid      TenantId  { get; set; }
    public DateTime? ExpiresAt { get; set; }
}

// ── Matching ───────────────────────────────────────────────────────────────────────────────

public sealed class MatchScanResultModel
{
    public string Kind        { get; set; } = string.Empty;
    public int    LocalCount  { get; set; }
    public int    RemoteCount { get; set; }
    public int    Exact       { get; set; }
    public int    Probable    { get; set; }
    public int    Unmatched   { get; set; }
}

public sealed class MatchCandidateModel
{
    public Guid    Id           { get; set; }
    /// <summary>Customer | Vendor | Item</summary>
    public string  Kind         { get; set; } = string.Empty;
    public string  ExternalId   { get; set; } = string.Empty;
    public string  SourceSystem { get; set; } = string.Empty;
    public string  LocalLabel   { get; set; } = string.Empty;
    public string? RemoteId     { get; set; }
    public string? RemoteName   { get; set; }
    /// <summary>Exact | Probable | None</summary>
    public string  Confidence   { get; set; } = "None";
    public string? Reason       { get; set; }
    /// <summary>Pending | Link | CreateNew | Skip</summary>
    public string  Decision     { get; set; } = "Pending";
}

public sealed class MatchDecisionItem
{
    public Guid    CandidateId { get; set; }
    /// <summary>Link | CreateNew | Skip</summary>
    public string  Decision    { get; set; } = "Link";
    /// <summary>For Link: the QuickBooks id to link (defaults to the proposed one).</summary>
    public string? RemoteId    { get; set; }
}

public sealed class ConfirmMatchesRequest
{
    public List<MatchDecisionItem> Decisions { get; set; } = new();
}

/// <summary>Records that matching is done for customers, vendors and items — a precondition for Live.</summary>
public sealed class ConfirmMatchingCompleteRequest
{
    public bool Confirmed { get; set; } = true;
}

// ── Sync dashboard ─────────────────────────────────────────────────────────────────────────

public sealed class SyncKindSummaryModel
{
    public string                  Kind          { get; set; } = string.Empty;
    public int                     Total         { get; set; }
    /// <summary>Keyed by SyncState name.</summary>
    public Dictionary<string, int> CountsByState { get; set; } = new();
}

public sealed class SyncSummaryModel
{
    public string                     ConnectionStatus { get; set; } = "NotConnected";
    public string                     Mode             { get; set; } = "DryRun";
    public int                        QueueDepth       { get; set; }
    public DateTime?                  LastRunAt        { get; set; }
    public List<SyncKindSummaryModel> Kinds            { get; set; } = new();
}

public sealed class SyncItemQuery
{
    public string? Kind     { get; set; }
    public string? State    { get; set; }
    public string? Search   { get; set; }
    public int     Page     { get; set; } = 1;
    public int     PageSize { get; set; } = 25;
}

public sealed class SyncItemModel
{
    public Guid      Id              { get; set; }
    public string    Kind            { get; set; } = string.Empty;
    public string    ExternalId      { get; set; } = string.Empty;
    public string    SourceSystem    { get; set; } = string.Empty;
    public string    DisplayLabel    { get; set; } = string.Empty;
    public string    State           { get; set; } = string.Empty;
    public string?   RemoteId        { get; set; }
    public string?   RemoteDocNumber { get; set; }
    public string?   DeepLink        { get; set; }
    public string?   LastErrorCode   { get; set; }
    public string?   LastError       { get; set; }
    public string?   Warning         { get; set; }
    public DateTime? LastSyncedAt    { get; set; }
    public DateTime? UpdatedAt       { get; set; }
    public int       AttemptCount    { get; set; }
}

public sealed class SyncLogModel
{
    public Guid     Id           { get; set; }
    public string   Operation    { get; set; } = string.Empty;
    public string   Outcome      { get; set; } = string.Empty;
    public string?  ErrorCode    { get; set; }
    public string?  Message      { get; set; }
    public int      DurationMs   { get; set; }
    public string?  IntuitTid    { get; set; }
    public DateTime CreatedAt    { get; set; }
    public string?  RequestJson  { get; set; }
    public string?  ResponseJson { get; set; }
}

public sealed class ResolveSyncItemRequest
{
    /// <summary>LinkRemote (link to <see cref="RemoteId"/>) | MarkResolved (accept as synced without a remote change) | Requeue</summary>
    public string  Action   { get; set; } = "Requeue";
    public string? RemoteId { get; set; }
}

public sealed class ManualPushRequest
{
    /// <summary>Customer | Vendor | Item | SalesInvoice | Bill</summary>
    public string       Kind        { get; set; } = string.Empty;
    public List<string> ExternalIds { get; set; } = new();
}

public sealed class ManualPushResult
{
    public int Requested { get; set; }
}

public sealed class BackfillResult
{
    public string Kind { get; set; } = string.Empty;
    public int    Sent { get; set; }
}

public sealed class StatusLookupRequest
{
    public string       Kind        { get; set; } = string.Empty;
    public List<string> ExternalIds { get; set; } = new();
}

/// <summary>Re-exported so the admin status lookup and the data endpoints return the same shape.</summary>
public sealed class StatusLookupResult
{
    public List<SyncStatus> Items { get; set; } = new();
}
