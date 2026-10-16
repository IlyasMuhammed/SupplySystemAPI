namespace SMS.Modules.Integration.Models;

// A44 admin shapes (docs/financial-posting/API-CONTRACT.md §4, "Details" → CTRL): sync control, provider defaults and
// overrides, account mapping, tax import, QuickBooks record import / link / unlink, dead-letter actions, coverage and
// validation. Same conventions as AdminModels.cs: wrapped in ApiResponse<T>, camelCase, enum-like values as strings.

// ── Sync control (D-14) ─────────────────────────────────────────────────────────────────────

public sealed class SyncControlRowModel
{
    /// <summary>Customer | Vendor | Item | SalesInvoice | CreditNote | Payment | POSSale | POSReturn | PurchaseBill | BillPayment | VendorCredit</summary>
    public string    DocumentType     { get; set; } = string.Empty;
    public string    DisplayLabel     { get; set; } = string.Empty;
    public bool      IsEnabled        { get; set; } = true;
    public bool      HoldWhenDisabled { get; set; }
    /// <summary>Process (enabled) | Hold (disabled, records kept and sent on enable) | Skip (disabled, records ignored)</summary>
    public string    Action           { get; set; } = "Process";
    /// <summary>Records of this type held now, waiting for it to be enabled.</summary>
    public int       HeldCount        { get; set; }
    /// <summary>True when nothing is stored for the type yet and the value shown is the default.</summary>
    public bool      IsDefault        { get; set; }
    public int?      ModifiedBy       { get; set; }
    public DateTime? ModifiedAt       { get; set; }
}

public sealed class SyncControlModel
{
    public List<SyncControlRowModel> Rows      { get; set; } = new();
    public int                       TotalHeld { get; set; }
}

public sealed class UpdateSyncControlRequest
{
    public bool IsEnabled        { get; set; } = true;
    public bool HoldWhenDisabled { get; set; }
    /// <summary>
    /// Enabling a type with held records sends them all. Without this the request is refused (409, result = the
    /// held count) so the screen can ask first.
    /// </summary>
    public bool ConfirmRelease   { get; set; }
}

public sealed class SyncControlUpdateResult
{
    public SyncControlRowModel Row      { get; set; } = new();
    /// <summary>Held records offered again because the type was enabled.</summary>
    public int                 Released { get; set; }
    /// <summary>Queued-but-unsent records held because the type was disabled with hold.</summary>
    public int                 HeldNow  { get; set; }
}

public sealed class SyncControlReleaseResult
{
    public string DocumentType { get; set; } = string.Empty;
    public int    Released     { get; set; }
    public int    StillHeld    { get; set; }
}

/// <summary>Result of a 409 from PUT sync-control/{type} without <c>confirmRelease</c>.</summary>
public sealed class ReleaseConfirmationModel
{
    public string DocumentType { get; set; } = string.Empty;
    public int    HeldCount    { get; set; }
}

// ── Provider defaults & overrides (D-15) ────────────────────────────────────────────────────

public sealed class ProviderDefaultModel
{
    /// <summary>e.g. ClassRef, DepartmentRef, SalesTermRef, PaymentMethodRef.CASH, DepositToAccountRef.CARD, BankAccountRef.CHEQUE</summary>
    public string    SettingKey    { get; set; } = string.Empty;
    /// <summary>The key without the method: ClassRef, DepositToAccountRef, …</summary>
    public string    BaseKey       { get; set; } = string.Empty;
    /// <summary>CASH | CHEQUE | BANK_TRANSFER | CARD | ONLINE | MOBILE for method keys, else null.</summary>
    public string?   Method        { get; set; }
    public string    Label         { get; set; } = string.Empty;
    /// <summary>What the value is chosen from: Class | Department | Term | PaymentMethod | Account | Customer</summary>
    public string    ReferenceKind { get; set; } = string.Empty;
    /// <summary>Account keys: the QuickBooks account types allowed (as QuickBooks names them).</summary>
    public List<string> AccountTypes { get; set; } = new();
    /// <summary>The QuickBooks id, or null when not set.</summary>
    public string?   SettingValue  { get; set; }
    public string?   ValueName     { get; set; }
    public string?   Description   { get; set; }
    /// <summary>Set when the stored value is no longer usable (gone, inactive) or not sent (class/location tracking off).</summary>
    public string?   Warning       { get; set; }
    public int?      ModifiedBy    { get; set; }
    public DateTime? ModifiedAt    { get; set; }
}

public sealed class ProviderDefaultsModel
{
    /// <summary>QuickBooks "Track classes" — null when not known (refresh the reference data).</summary>
    public bool?   ClassTrackingEnabled      { get; set; }
    /// <summary>QuickBooks "Track locations" — null when not known.</summary>
    public bool?   DepartmentTrackingEnabled { get; set; }
    /// <summary>What the company calls locations (Location, Department, Division…), when known.</summary>
    public string? DepartmentTerminology     { get; set; }
    public List<ProviderDefaultModel> Defaults      { get; set; } = new();
    /// <summary>Document types an override can be set for.</summary>
    public List<string>               DocumentTypes { get; set; } = new();
}

public sealed class ProviderDefaultValueItem
{
    public string  SettingKey   { get; set; } = string.Empty;
    /// <summary>Null or empty removes the default.</summary>
    public string? SettingValue { get; set; }
    public string? Description  { get; set; }
}

public sealed class SaveProviderDefaultsRequest
{
    public List<ProviderDefaultValueItem> Values { get; set; } = new();
}

public sealed class DocumentOverrideModel
{
    public string    DocumentType  { get; set; } = string.Empty;
    public string    SettingKey    { get; set; } = string.Empty;
    public string    SettingValue  { get; set; } = string.Empty;
    public string?   ValueName     { get; set; }
    public string?   Warning       { get; set; }
    public int?      ModifiedBy    { get; set; }
    public DateTime? ModifiedAt    { get; set; }
}

public sealed class DocumentOverrideItem
{
    public string  DocumentType { get; set; } = string.Empty;
    public string  SettingKey   { get; set; } = string.Empty;
    /// <summary>Null or empty removes the override.</summary>
    public string? SettingValue { get; set; }
}

public sealed class SaveDocumentOverridesRequest
{
    public List<DocumentOverrideItem> Overrides { get; set; } = new();
}

// ── Reference additions (D-15) ──────────────────────────────────────────────────────────────

public sealed class ReferenceTrackingModel
{
    public bool?   ClassTrackingPerTransaction { get; set; }
    public bool?   ClassTrackingPerLine        { get; set; }
    public bool?   DepartmentTracking          { get; set; }
    public string? DepartmentTerminology       { get; set; }
}

// ── Accounts (D-16) ─────────────────────────────────────────────────────────────────────────

public sealed class AccountMappingRowModel
{
    public Guid         AccountUuid    { get; set; }
    public string       Code           { get; set; } = string.Empty;
    public string       Name           { get; set; } = string.Empty;
    public string?      AccountType    { get; set; }
    public bool         IsActive       { get; set; }
    public string?      QboAccountId   { get; set; }
    public string?      QboAccountName { get; set; }
    public string?      QboAccountType { get; set; }
    /// <summary>Mapped | Unmapped | Invalid (mapped to an account QuickBooks no longer has active)</summary>
    public string       Status         { get; set; } = "Unmapped";
    /// <summary>The A42 default keys assigned to this account (DEFAULT_REVENUE, …).</summary>
    public List<string> DefaultKeys    { get; set; } = new();
}

public sealed class QboAccountModel
{
    public string  Id          { get; set; } = string.Empty;
    public string  Name        { get; set; } = string.Empty;
    public string? AcctNum     { get; set; }
    public string  Type        { get; set; } = string.Empty;
    public string? SubType     { get; set; }
    public bool    Active      { get; set; } = true;
    /// <summary>How many SCM accounts are mapped to it.</summary>
    public int     MappedCount { get; set; }
}

public sealed class DefaultRoleMappingModel
{
    public string  DefaultKey     { get; set; } = string.Empty;
    public string? QboAccountId   { get; set; }
    public string? QboAccountName { get; set; }
    /// <summary>What the QuickBooks documents use it for when the settings account is empty (D-16), e.g. "Default income account".</summary>
    public string? UsedFor        { get; set; }
}

public sealed class AccountMappingsModel
{
    /// <summary>False when the general ledger (A42) is not set up or not licensed — nothing to map.</summary>
    public bool      GlAvailable        { get; set; }
    public DateTime? ReferenceFetchedAt { get; set; }
    public int       TotalAccounts      { get; set; }
    public int       Mapped             { get; set; }
    public int       Unmapped           { get; set; }
    public int       Invalid            { get; set; }
    public List<AccountMappingRowModel>  Accounts     { get; set; } = new();
    public List<QboAccountModel>         QboAccounts  { get; set; } = new();
    public List<DefaultRoleMappingModel> DefaultRoles { get; set; } = new();
}

public sealed class AccountMappingItem
{
    public Guid    AccountUuid  { get; set; }
    /// <summary>Null or empty removes the mapping.</summary>
    public string? QboAccountId { get; set; }
}

public sealed class SaveAccountMappingsRequest
{
    public List<AccountMappingItem> Mappings { get; set; } = new();
}

public sealed class AutoMapAccountsRequest
{
    /// <summary>False (default): only unmapped accounts are touched.</summary>
    public bool Overwrite      { get; set; }
    /// <summary>False (default): inactive SCM accounts are left alone.</summary>
    public bool IncludeInactive { get; set; }
}

public sealed class AutoMappedAccountModel
{
    public Guid   AccountUuid    { get; set; }
    public string Code           { get; set; } = string.Empty;
    public string Name           { get; set; } = string.Empty;
    public string QboAccountId   { get; set; } = string.Empty;
    public string QboAccountName { get; set; } = string.Empty;
    /// <summary>AcctNum | ExactName | NormalizedName</summary>
    public string Rule           { get; set; } = string.Empty;
}

public sealed class AmbiguousAccountModel
{
    public Guid                  AccountUuid { get; set; }
    public string                Code        { get; set; } = string.Empty;
    public string                Name        { get; set; } = string.Empty;
    public string                Rule        { get; set; } = string.Empty;
    public List<QboAccountModel> Candidates  { get; set; } = new();
}

public sealed class AutoMapAccountsResult
{
    public int                          Mapped    { get; set; }
    public List<AutoMappedAccountModel> Matches   { get; set; } = new();
    public List<AmbiguousAccountModel>  Ambiguous { get; set; } = new();
    /// <summary>Accounts no rule matched.</summary>
    public int                          Unmatched { get; set; }
    public AccountMappingsModel         Accounts  { get; set; } = new();
}

// ── Tax import (D-18) ───────────────────────────────────────────────────────────────────────

public sealed class TaxImportRowModel
{
    /// <summary>Null for a bare-rate row.</summary>
    public string?                  SourceTaxCode     { get; set; }
    public string?                  SourceTaxCodeName { get; set; }
    public decimal                  TaxPercent        { get; set; }
    /// <summary>Saved rows: what was saved. Review rows: null.</summary>
    public string?                  QboTaxCodeId      { get; set; }
    public string?                  QboTaxCodeName    { get; set; }
    /// <summary>Why it was saved, or why it needs a person.</summary>
    public string                   Reason            { get; set; } = string.Empty;
    public List<ReferenceItemModel> Candidates        { get; set; } = new();
}

public sealed class TaxImportResultModel
{
    public int                       Saved         { get; set; }
    public int                       AlreadyMapped { get; set; }
    public List<TaxImportRowModel>   SavedRows     { get; set; } = new();
    public List<TaxImportRowModel>   NeedsReview   { get; set; } = new();
    /// <summary>The tax mapping screen after the save (same as GET tax-mappings).</summary>
    public List<TaxCodeMappingModel> Mappings      { get; set; } = new();
}

// ── QuickBooks record import / link (D-17) ──────────────────────────────────────────────────

public sealed class RemoteImportResultModel
{
    public string   Kind       { get; set; } = string.Empty;
    public int      Read       { get; set; }
    public int      Added      { get; set; }
    public int      Updated    { get; set; }
    /// <summary>Imported before, no longer in QuickBooks' list.</summary>
    public int      Removed    { get; set; }
    /// <summary>Of those read, already linked to an SCM record.</summary>
    public int      Linked     { get; set; }
    public int      Unlinked   { get; set; }
    public DateTime ImportedAt { get; set; }
}

public sealed class RemoteEntityQuery
{
    /// <summary>false = only records no SCM record is linked to; true = only linked; null = all.</summary>
    public bool?   Linked   { get; set; }
    public string? Search   { get; set; }
    public int     Page     { get; set; } = 1;
    public int     PageSize { get; set; } = 50;
}

public sealed class RemoteEntityModel
{
    public string    RemoteId         { get; set; } = string.Empty;
    public string    Kind             { get; set; } = string.Empty;
    public string    Name             { get; set; } = string.Empty;
    public string?   CompanyName      { get; set; }
    public string?   Email            { get; set; }
    public string?   TaxId            { get; set; }
    public string?   AccountNumber    { get; set; }
    public string?   Sku              { get; set; }
    public string?   CurrencyCode     { get; set; }
    public bool      Active           { get; set; }
    public DateTime  ImportedAt       { get; set; }
    public bool      Linked           { get; set; }
    /// <summary>The sync item (EntityMap uuid) linked to it.</summary>
    public Guid?     LinkedItemId     { get; set; }
    public string?   LinkedExternalId { get; set; }
    public string?   LinkedLabel      { get; set; }
    public string?   DeepLink         { get; set; }
}

public sealed class LinkRemoteEntityRequest
{
    /// <summary>The SCM record's id as the gateway knows it (partner / variant uuid).</summary>
    public string ExternalId { get; set; } = string.Empty;
}

// ── Dead letter & unlink (D-11, D-17) ───────────────────────────────────────────────────────

public sealed class RetryFailedRequest
{
    /// <summary>Only this kind (SyncKind name); null = every kind.</summary>
    public string? Kind { get; set; }
}

public sealed class RetrySkippedItemModel
{
    public Guid   Id           { get; set; }
    public string Kind         { get; set; } = string.Empty;
    public string DisplayLabel { get; set; } = string.Empty;
    public string Reason       { get; set; } = string.Empty;
}

public sealed class RetryFailedResult
{
    /// <summary>Dead-letter records found (Failed or NeedsResolution).</summary>
    public int                         Found    { get; set; }
    public int                         Requeued { get; set; }
    public List<RetrySkippedItemModel> Skipped  { get; set; } = new();
}

/// <summary>Result of a 409 from POST sync/items/{id}/unlink.</summary>
public sealed class UnlinkRefusedModel
{
    public int          OpenDocuments { get; set; }
    /// <summary>Up to 20 of their labels.</summary>
    public List<string> Examples      { get; set; } = new();
}

// ── QuickBooks logs page (D-34) ─────────────────────────────────────────────────────────────

public sealed class SyncLogQuery
{
    /// <summary>Default true: every outcome except Succeeded and DryRun.</summary>
    public bool      FailedOnly { get; set; } = true;
    /// <summary>
    /// Default true (QA-7): only calls whose record is not Synced or Voided now. Calls that belong to no record (matching
    /// scans, imports) are left out too. False = every call, whatever became of its record.
    /// </summary>
    public bool      UnresolvedOnly { get; set; } = true;
    /// <summary>SyncKind name or path form; only calls that belong to a record of that kind.</summary>
    public string?   Kind       { get; set; }
    /// <summary>Create | Update | Void | Find | Query | Read …, exact.</summary>
    public string?   Operation  { get; set; }
    /// <summary>Succeeded | Refused | Duplicate | StaleObject | NotFound | Throttled | Transient | AuthRevoked | Exception | DryRun, exact.</summary>
    public string?   Outcome    { get; set; }
    /// <summary>Inclusive, UTC.</summary>
    public DateTime? From       { get; set; }
    /// <summary>Inclusive; a date without a time means the whole day.</summary>
    public DateTime? To         { get; set; }
    /// <summary>Document label / number, external id, QuickBooks name, error code / message, Intuit tid.</summary>
    public string?   Search     { get; set; }
    public int       Page       { get; set; } = 1;
    public int       PageSize   { get; set; } = 50;
}

public sealed class SyncLogItemModel
{
    public Guid      Id              { get; set; }
    public DateTime  CreatedAt       { get; set; }
    /// <summary>The record's kind; null for calls that belong to no record (a matching scan, an import).</summary>
    public string?   Kind            { get; set; }
    /// <summary>The sync item (EntityMap uuid) — the Sync tab's row and its Retry.</summary>
    public Guid?     ItemId          { get; set; }
    /// <summary>The SCM record's id (uuid for SCM records) — links to the SCM page.</summary>
    public string?   ExternalId      { get; set; }
    public string?   SourceSystem    { get; set; }
    public string?   DisplayLabel    { get; set; }
    public string    Operation       { get; set; } = string.Empty;
    public string    Outcome         { get; set; } = string.Empty;
    public string?   ErrorCode       { get; set; }
    public string?   Message         { get; set; }
    public int       DurationMs      { get; set; }
    public string?   IntuitTid       { get; set; }
    /// <summary>The record's state now (not at the time of the call).</summary>
    public string?   State           { get; set; }
    public string?   RemoteId        { get; set; }
    public string?   RemoteDocNumber { get; set; }
    /// <summary>"Open in QuickBooks" — set when the record is linked to a QuickBooks record.</summary>
    public string?   DeepLink        { get; set; }
    /// <summary>Detail only (GET sync/log/{id}); redacted when it was written.</summary>
    public string?   RequestJson     { get; set; }
    public string?   ResponseJson    { get; set; }
}

// ── Coverage & validation (D-27) ────────────────────────────────────────────────────────────

public sealed class CoverageKindModel
{
    /// <summary>Customer | Vendor | Item</summary>
    public string  Kind            { get; set; } = string.Empty;
    /// <summary>Records SCM has sent to the gateway.</summary>
    public int     Total           { get; set; }
    /// <summary>Linked to a QuickBooks record (created or adopted).</summary>
    public int     Mapped          { get; set; }
    /// <summary>Skipped in matching ("not synced").</summary>
    public int     Skipped         { get; set; }
    /// <summary>Neither: still to be created or linked.</summary>
    public int     Pending         { get; set; }
    /// <summary>Of those, Failed / Blocked / NeedsResolution.</summary>
    public int     Failed          { get; set; }
    public int     RemoteImported  { get; set; }
    public int     RemoteUnlinked  { get; set; }
    public decimal PercentMapped   { get; set; }
}

public sealed class CoverageTaxModel
{
    public int          Total    { get; set; }
    public int          Mapped   { get; set; }
    /// <summary>Codes (and "x%" rates) seen on stored documents or active in SCM without a mapping.</summary>
    public List<string> Unmapped { get; set; } = new();
}

public sealed class CoverageAccountsModel
{
    public bool GlAvailable { get; set; }
    public int  Total       { get; set; }
    public int  Mapped      { get; set; }
    public int  Unmapped    { get; set; }
    /// <summary>Always false: QuickBooks documents do not need GL account mappings (D-16); shown for information.</summary>
    public bool Blocking    { get; set; }
}

public sealed class CoverageIssueModel
{
    /// <summary>e.g. NOT_CONNECTED, REFERENCE_DATA, PREFLIGHT, DEPOSIT_ACCOUNTS, BANK_ACCOUNTS, CLASS_DEFAULT, DEPARTMENT_DEFAULT, TAX_UNMAPPED, ENTITIES_PENDING</summary>
    public string  Code         { get; set; } = string.Empty;
    /// <summary>Connection | Preflight | SyncControl | Defaults | Tax | Accounts | Customer | Vendor | Item</summary>
    public string  Area         { get; set; } = string.Empty;
    public string  Message      { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    /// <summary>The QuickBooks page tab that fixes it: connection | setup | sync-control | defaults | accounts | tax | match</summary>
    public string? Tab          { get; set; }
}

public sealed class CoverageDefaultsModel
{
    public int                      Required { get; set; }
    public int                      Set      { get; set; }
    public List<CoverageIssueModel> Missing  { get; set; } = new();
}

public sealed class CoverageModel
{
    /// <summary>No blocking issue.</summary>
    public bool                     Ready              { get; set; }
    public string                   ConnectionStatus   { get; set; } = "NotConnected";
    public string                   Mode               { get; set; } = "DryRun";
    public DateTime?                ReferenceFetchedAt { get; set; }
    public List<CoverageKindModel>  Entities           { get; set; } = new();
    public CoverageTaxModel         TaxCodes           { get; set; } = new();
    public CoverageAccountsModel    Accounts           { get; set; } = new();
    public CoverageDefaultsModel    ProviderDefaults   { get; set; } = new();
    public List<CoverageIssueModel> BlockingIssues     { get; set; } = new();
    public List<CoverageIssueModel> Warnings           { get; set; } = new();
}

public sealed class ValidationCheckRequest
{
    public string  Kind         { get; set; } = string.Empty;
    public string  ExternalId   { get; set; } = string.Empty;
    /// <summary>Default SCM.</summary>
    public string? SourceSystem { get; set; }
}

public sealed class ValidationErrorModel
{
    public string Field   { get; set; } = string.Empty;
    public string Code    { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class ValidationDependencyModel
{
    public string  Kind       { get; set; } = string.Empty;
    public string  ExternalId { get; set; } = string.Empty;
    public string? Label      { get; set; }
    /// <summary>The dependency's sync state, or null when the gateway has never received it.</summary>
    public string? State      { get; set; }
}

public sealed class ValidationCheckResult
{
    public string  Kind         { get; set; } = string.Empty;
    public string  ExternalId   { get; set; } = string.Empty;
    /// <summary>False when the gateway holds no payload for the record.</summary>
    public bool    Found        { get; set; }
    public string? State        { get; set; }
    public string? DisplayLabel { get; set; }
    /// <summary>The sync-control type that governs it, and what it says now: Process | Hold | Skip.</summary>
    public string? DocumentType { get; set; }
    public string? SyncControl  { get; set; }
    /// <summary>Found, no errors, nothing missing — what it would take for it to be sent now.</summary>
    public bool    Valid        { get; set; }
    public List<ValidationErrorModel>      Errors              { get; set; } = new();
    public List<string>                    Warnings            { get; set; } = new();
    public List<ValidationDependencyModel> MissingDependencies { get; set; } = new();
}
