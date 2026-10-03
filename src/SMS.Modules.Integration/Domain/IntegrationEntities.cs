using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Domain;

// Schema "integration". Every table is tenant-scoped; cross-module references are bare ids/strings.
// See docs/quickbooks/QUICKBOOKS-INTEGRATION-PLAN.md §2.9.

/// <summary>One SCM organization ↔ one QuickBooks company. Unique on (OrganizationId, ProviderKey).</summary>
internal class IntegrationConnection : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    /// <summary>"QBO". Named so a second accounting provider is a new row, not a new table.</summary>
    public string   ProviderKey    { get; set; } = ProviderKeys.QuickBooksOnline;
    public string?  RealmId        { get; set; }
    public string?  CompanyName    { get; set; }
    public IntegrationEnvironment Environment { get; set; } = IntegrationEnvironment.Sandbox;
    public ConnectionStatus Status { get; set; } = ConnectionStatus.NotConnected;

    /// <summary>Only <c>CredentialVault</c> reads or writes these.</summary>
    public string?  EncryptedAccessToken  { get; set; }
    public string?  EncryptedRefreshToken { get; set; }
    public DateTime? AccessTokenExpiresAt  { get; set; }
    /// <summary>From Intuit's x_refresh_token_expires_in — never hard-coded.</summary>
    public DateTime? RefreshTokenExpiresAt { get; set; }

    public string?  HomeCurrencyCode     { get; set; }
    public bool?    MultiCurrencyEnabled { get; set; }
    public string?  Country              { get; set; }

    public int?      ConnectedByUserId { get; set; }
    public DateTime? ConnectedAt       { get; set; }
    public DateTime? LastRefreshAt     { get; set; }
    /// <summary>When the reconnect-soon notification was last sent, so it is sent once, not daily.</summary>
    public DateTime? ReconnectWarnedAt { get; set; }
    public string?   LastError         { get; set; }

    public DateTime  CreatedDate  { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }

    /// <summary>Stops two concurrent token refreshes from both persisting — the loser reloads.</summary>
    public byte[]   RowVersion { get; set; } = [];
}

/// <summary>
/// The OAuth <c>state</c> parameter. The only thing that tells the anonymous callback which organization
/// is connecting, so it is single-use, short-lived, org-bound, and stored hashed.
/// </summary>
internal class OAuthStateToken : ITenantScopedEntity
{
    public int       Id             { get; set; }
    public Guid      OrganizationId { get; set; }
    /// <summary>SHA-256 of the token, hex. The token itself only ever exists in the consent URL.</summary>
    public string    TokenHash      { get; set; } = string.Empty;
    public int       UserId         { get; set; }
    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime  ExpiresAt      { get; set; }
    public DateTime? UsedAt         { get; set; }
}

/// <summary>A system allowed to call the data endpoints (/api/gateway/quickbooks/v1) with an API key.</summary>
internal class ApiClient : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    /// <summary>Also the SourceSystem this client's records are mapped under. Unique per organization.</summary>
    public string   Name           { get; set; } = string.Empty;
    /// <summary>Comma-separated, see <c>ApiScopes</c>.</summary>
    public string   Scopes         { get; set; } = string.Empty;
    public bool     IsActive       { get; set; } = true;
    public int      CreatedBy      { get; set; }
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;

    public ICollection<ApiClientKey> Keys { get; set; } = new List<ApiClientKey>();
}

/// <summary>
/// One issued key. Up to two active per client, so a key can be rotated without downtime. Only the
/// hash is stored; the key is shown once at creation.
/// </summary>
internal class ApiClientKey : ITenantScopedEntity
{
    public int       Id             { get; set; }
    public Guid      Uuid           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    public int       ApiClientId    { get; set; }
    public ApiClient ApiClient      { get; set; } = null!;
    /// <summary>The key's first characters, for display and for finding the row before hashing. Unique.</summary>
    public string    KeyPrefix      { get; set; } = string.Empty;
    /// <summary>SHA-256 of the whole key, hex.</summary>
    public string    KeyHash        { get; set; } = string.Empty;
    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public int       CreatedBy      { get; set; }
    public DateTime? ExpiresAt      { get; set; }
    public DateTime? RevokedAt      { get; set; }
    public DateTime? LastUsedAt     { get; set; }
}

/// <summary>One row per connection.</summary>
internal class IntegrationSettings : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     OrganizationId { get; set; }
    public int      ConnectionId   { get; set; }

    public SyncMode        Mode            { get; set; } = SyncMode.DryRun;
    public bool            AutoPushCustomers     { get; set; } = true;
    public bool            AutoPushVendors       { get; set; } = true;
    public bool            AutoPushItems         { get; set; } = true;
    public bool            AutoPushSalesInvoices { get; set; } = true;
    public bool            AutoPushBills         { get; set; } = true;
    public ItemTypeDefault ItemTypeDefault { get; set; } = ItemTypeDefault.NonInventory;
    public PartnerScope    PartnerScope    { get; set; } = PartnerScope.OnlyWhenReferenced;

    // QuickBooks account / tax-code ids (from the reference snapshot).
    public string? DefaultIncomeAccountId   { get; set; }
    public string? DefaultExpenseAccountId  { get; set; }
    public string? FreightExpenseAccountId  { get; set; }
    public string? DiscountAccountId        { get; set; }
    /// <summary>Plan D-10: used for bill lines that carry no tax rate. Null = send no tax code.</summary>
    public string? DefaultPurchaseTaxCodeId { get; set; }

    /// <summary>Documents dated before this are never pushed — stops history flooding in.</summary>
    public DateTime? DocumentStartDate { get; set; }

    /// <summary>Set when the admin confirmed matching for customers, vendors and items (a Live precondition).</summary>
    public DateTime? MatchingConfirmedAt { get; set; }

    /// <summary>
    /// Start time of the last reconciliation run in which every source answered. The next run asks
    /// the sources for records changed since then. Written only by ReconciliationJob.
    /// </summary>
    public DateTime? LastReconciledAt { get; set; }

    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>Audit trail of settings/mapping/mode changes.</summary>
internal class SettingsAuditEntry : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     OrganizationId { get; set; }
    public int      ConnectionId   { get; set; }
    /// <summary>e.g. "Settings", "TaxMapping", "TermMapping", "Mode", "ApiClient", "Connection".</summary>
    public string   Area           { get; set; } = string.Empty;
    public string   Action         { get; set; } = string.Empty;
    public string?  BeforeJson     { get; set; }
    public string?  AfterJson      { get; set; }
    public int      UserId         { get; set; }
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Plan D-4: a caller's tax → a QuickBooks TaxCode. Either by the caller's tax CODE
/// (<see cref="SourceTaxCode"/> set; unique per connection) or, for lines that carry only a rate,
/// by bare percentage (<see cref="SourceTaxCode"/> null; unique on (ConnectionId, TaxPercent)).
/// </summary>
internal class TaxCodeMapping : ITenantScopedEntity
{
    public int     Id             { get; set; }
    public Guid    OrganizationId { get; set; }
    public int     ConnectionId   { get; set; }
    /// <summary>The caller's tax code (SCM: Finance TaxCode.Code). Null for a percent-only mapping.</summary>
    public string? SourceTaxCode  { get; set; }
    /// <summary>For a code mapping: the code's rate when mapped (informational). For a percent mapping: the key.</summary>
    public decimal TaxPercent     { get; set; }
    public string  QboTaxCodeId   { get; set; } = string.Empty;
    public int?    ModifiedBy     { get; set; }
    public DateTime ModifiedDate  { get; set; } = DateTime.UtcNow;
}

/// <summary>A caller's payment-term id → a QuickBooks Term. Unique on (ConnectionId, PaymentTermExternalId).</summary>
internal class PaymentTermMapping : ITenantScopedEntity
{
    public int      Id                    { get; set; }
    public Guid     OrganizationId        { get; set; }
    public int      ConnectionId          { get; set; }
    public string   PaymentTermExternalId { get; set; } = string.Empty;
    public string?  PaymentTermName       { get; set; }
    public string   QboTermId             { get; set; } = string.Empty;
    public int?     ModifiedBy            { get; set; }
    public DateTime ModifiedDate          { get; set; } = DateTime.UtcNow;
}

/// <summary>Cached QuickBooks reference data. Unique on (ConnectionId, Kind).</summary>
internal class ReferenceSnapshot : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     OrganizationId { get; set; }
    public int      ConnectionId   { get; set; }
    /// <summary>See <c>ReferenceKinds</c>.</summary>
    public string   Kind           { get; set; } = string.Empty;
    public string   Json           { get; set; } = "[]";
    public DateTime FetchedAt      { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One caller record ↔ one QuickBooks record. Holds the latest payload the caller sent, which is what
/// jobs push from — the gateway never reads the caller's tables. Unique on
/// (ConnectionId, SourceSystem, Kind, ExternalId): a partner that is both customer and vendor is two rows.
/// </summary>
internal class EntityMap : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    public int      ConnectionId   { get; set; }

    /// <summary><c>QuickBooksSourceSystems.Scm</c>, or the API client's name.</summary>
    public string   SourceSystem   { get; set; } = QuickBooksSourceSystems.Scm;
    public SyncKind Kind           { get; set; }
    public string   ExternalId     { get; set; } = string.Empty;
    /// <summary>What the dashboard shows: a name or document number.</summary>
    public string   DisplayLabel   { get; set; } = string.Empty;

    public string?  PayloadJson           { get; set; }
    public string?  PayloadFingerprint    { get; set; }
    public string?  LastPushedFingerprint { get; set; }
    public DateTime? PayloadReceivedAt    { get; set; }

    public string?  RemoteId        { get; set; }
    public string?  RemoteSyncToken { get; set; }
    public string?  RemoteDocNumber { get; set; }
    /// <summary>The name actually used in QuickBooks (after any D-2 suffix), for matching and support.</summary>
    public string?  RemoteName      { get; set; }
    public LinkOrigin? LinkOrigin   { get; set; }

    public SyncState State          { get; set; } = SyncState.NotSynced;
    /// <summary>Set when a document needed this record or someone pushed it explicitly — overrides the scope setting.</summary>
    public DateTime? RequestedAt    { get; set; }
    public string?  LastErrorCode   { get; set; }
    public string?  LastError       { get; set; }
    /// <summary>A non-blocking note, e.g. QuickBooks' tax total differs from the caller's.</summary>
    public string?  Warning         { get; set; }
    public DateTime? LastSyncedAt   { get; set; }
    public int?     LinkedByUserId  { get; set; }
    public DateTime? LinkedAt       { get; set; }

    public DateTime  CreatedDate  { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedDate { get; set; }
    public byte[]    RowVersion   { get; set; } = [];
}

/// <summary>A queued push. At most one open (not Done) entry per EntityMap — re-sending merges into it.</summary>
internal class SyncOutboxEntry : ITenantScopedEntity
{
    public int       Id             { get; set; }
    public Guid      Uuid           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    public int       ConnectionId   { get; set; }
    public int       EntityMapId    { get; set; }
    public EntityMap EntityMap      { get; set; } = null!;
    public OutboxOperation Operation { get; set; } = OutboxOperation.Upsert;
    public OutboxStatus    Status    { get; set; } = OutboxStatus.Queued;
    public int       AttemptCount   { get; set; }
    public DateTime  NextAttemptAt  { get; set; } = DateTime.UtcNow;
    public string?   BlockedReason  { get; set; }
    /// <summary>JSON array of {kind, externalId} this entry waits on.</summary>
    public string?   DependsOnJson  { get; set; }
    public DateTime? WaitingSince   { get; set; }
    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt    { get; set; }
}

/// <summary>
/// The ledger: written before every call to QuickBooks, so a retry asks instead of creating twice.
/// Same semantics as Logistics' CarrierCommand. Unique on (OrganizationId, CommandKey).
/// </summary>
internal class SyncCommandClaim : ITenantScopedEntity
{
    public int       Id             { get; set; }
    public Guid      OrganizationId { get; set; }
    public int       EntityMapId    { get; set; }
    /// <summary>SHA-256 of (connection, kind, externalId, operation, payload fingerprint).</summary>
    public string    CommandKey     { get; set; } = string.Empty;
    public ClaimStatus Status       { get; set; } = ClaimStatus.InFlight;
    public int       AttemptCount   { get; set; }
    public DateTime  FirstAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime  LastAttemptAt  { get; set; } = DateTime.UtcNow;
    public DateTime? LeaseExpiresAt { get; set; }
    public string?   RemoteId       { get; set; }
    public string?   ErrorCode      { get; set; }
}

/// <summary>One attempt, request and response redacted at the boundary. Kept for the retention period.</summary>
internal class SyncLogEntry : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    public int      ConnectionId   { get; set; }
    public int?     EntityMapId    { get; set; }
    /// <summary>e.g. Create, Update, Void, Find, Query, Refresh, Preflight.</summary>
    public string   Operation      { get; set; } = string.Empty;
    /// <summary>ProviderOutcomeKind name, or DryRun.</summary>
    public string   Outcome        { get; set; } = string.Empty;
    public string?  ErrorCode      { get; set; }
    public string?  Message        { get; set; }
    public string?  RequestJson    { get; set; }
    public string?  ResponseJson   { get; set; }
    public int      DurationMs     { get; set; }
    public string?  IntuitTid      { get; set; }
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
}

/// <summary>A proposed link between a caller record the gateway holds and an existing QuickBooks record.</summary>
internal class MatchCandidate : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    public int      ConnectionId   { get; set; }
    public int      EntityMapId    { get; set; }
    public SyncKind Kind           { get; set; }
    public string?  RemoteId       { get; set; }
    public string?  RemoteName     { get; set; }
    public MatchConfidence Confidence { get; set; } = MatchConfidence.None;
    /// <summary>e.g. "Exact name", "Tax id", "Email", "Code", "SKU".</summary>
    public string?  Reason         { get; set; }
    public MatchDecision Decision  { get; set; } = MatchDecision.Pending;
    public int?     DecidedBy      { get; set; }
    public DateTime? DecidedAt     { get; set; }
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
}

internal static class ProviderKeys
{
    public const string QuickBooksOnline = "QBO";
}

/// <summary>Values of <see cref="ReferenceSnapshot.Kind"/>.</summary>
internal static class ReferenceKinds
{
    public const string Accounts    = "Accounts";
    public const string TaxCodes    = "TaxCodes";
    public const string Terms       = "Terms";
    public const string Currencies  = "Currencies";
    public const string Preferences = "Preferences";
    public const string CompanyInfo = "CompanyInfo";
}

/// <summary>API key scopes.</summary>
internal static class ApiScopes
{
    public const string CustomersWrite = "customers:write";
    public const string VendorsWrite   = "vendors:write";
    public const string ItemsWrite     = "items:write";
    public const string InvoicesWrite  = "invoices:write";
    public const string BillsWrite     = "bills:write";
    public const string StatusRead     = "status:read";

    public static readonly IReadOnlyList<string> All =
        [CustomersWrite, VendorsWrite, ItemsWrite, InvoicesWrite, BillsWrite, StatusRead];
}
