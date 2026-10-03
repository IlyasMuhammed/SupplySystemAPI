using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Providers;

// Provider-neutral contracts. Nothing in this folder (or anywhere outside Providers/QuickBooks) may
// reference Intuit.Ipp.* — that is what lets a raw-REST or second provider replace the SDK one.

/// <summary>Which company a provider call is for. Built by the caller from an IntegrationConnection.</summary>
internal sealed record ProviderContext(
    int                    ConnectionId,
    Guid                   OrganizationId,
    string                 RealmId,
    IntegrationEnvironment Environment);

internal enum ProviderOutcomeKind
{
    Succeeded,
    /// <summary>QuickBooks said no to this data (validation). Final — never retried until the payload changes.</summary>
    Refused,
    /// <summary>A record with that name / document number already exists. Hand to matching.</summary>
    Duplicate,
    /// <summary>The SyncToken was stale — someone changed the record in QuickBooks. Re-read, re-apply, retry once.</summary>
    StaleObject,
    NotFound,
    /// <summary>HTTP 429. Back off and retry.</summary>
    Throttled,
    /// <summary>5xx, timeout, dropped connection: the outcome is UNKNOWN. Look it up before any retry.</summary>
    Transient,
    /// <summary>Token invalid / grant revoked. Connection-level: suspend, do not retry.</summary>
    AuthRevoked
}

/// <summary>What one provider call produced. <see cref="RequestJson"/>/<see cref="ResponseJson"/> are already redacted.</summary>
internal sealed class ProviderResult<T>
{
    public ProviderOutcomeKind Outcome      { get; init; }
    public T?                  Value        { get; init; }
    public string?             ErrorCode    { get; init; }
    public string?             ErrorField   { get; init; }
    public string?             Message      { get; init; }
    public string?             IntuitTid    { get; init; }
    public string?             RequestJson  { get; init; }
    public string?             ResponseJson { get; init; }
    public int                 DurationMs   { get; init; }

    public bool IsSuccess => Outcome == ProviderOutcomeKind.Succeeded;

    public static ProviderResult<T> Ok(T value) => new() { Outcome = ProviderOutcomeKind.Succeeded, Value = value };

    public static ProviderResult<T> Fail(ProviderOutcomeKind outcome, string? code, string? message, string? field = null) =>
        new() { Outcome = outcome, ErrorCode = code, Message = message, ErrorField = field };
}

/// <summary>The identity of a record in QuickBooks after a write or read.</summary>
internal sealed record RemoteRecord(
    string   RemoteId,
    string   SyncToken,
    string?  Name,
    string?  DocNumber,
    decimal? TotalAmount = null,
    decimal? TotalTax    = null,
    bool     Active      = true);

/// <summary>One row of a remote list, for matching.</summary>
internal sealed record RemoteListEntry(
    string  RemoteId,
    string  Name,
    string? CompanyName,
    string? Email,
    string? TaxId,
    string? AccountNumber,
    string? Sku,
    string? CurrencyCode,
    bool    Active);

/// <summary>How to find an existing record: by display name / item name, by document number (+ vendor for bills).</summary>
internal sealed record RemoteLookup(string? Name = null, string? DocNumber = null, string? VendorRemoteId = null)
{
    /// <summary>Items only: searched when <see cref="Name"/> is not given.</summary>
    public string? Sku { get; init; }
}

// ── What gets written. All references are already resolved to QuickBooks ids. ──────────────

internal abstract class RemoteEntity
{
    public abstract SyncKind Kind { get; }
}

internal sealed class RemoteAddress
{
    public string? Line1      { get; set; }
    public string? Line2      { get; set; }
    public string? City       { get; set; }
    public string? Region     { get; set; }
    public string? PostalCode { get; set; }
    public string? Country    { get; set; }
}

internal abstract class RemoteParty : RemoteEntity
{
    public string         DisplayName  { get; set; } = string.Empty;
    public string?        CompanyName  { get; set; }
    public string?        Email        { get; set; }
    public string?        Phone        { get; set; }
    public string?        Fax          { get; set; }
    public string?        Website      { get; set; }
    public string?        TaxId        { get; set; }
    public RemoteAddress? BillAddress  { get; set; }
    public string?        CurrencyCode { get; set; }
    public string?        TermId       { get; set; }
    public string?        Notes        { get; set; }
    public bool           Active       { get; set; } = true;
}

internal sealed class RemoteCustomer : RemoteParty
{
    public override SyncKind Kind => SyncKind.Customer;
}

internal sealed class RemoteVendor : RemoteParty
{
    public override SyncKind Kind => SyncKind.Vendor;
    public string? AccountNumber { get; set; }
}

internal enum RemoteItemType
{
    NonInventory,
    Service
}

internal sealed class RemoteItem : RemoteEntity
{
    public override SyncKind Kind => SyncKind.Item;
    public string         Name             { get; set; } = string.Empty;
    public string?        Sku              { get; set; }
    public RemoteItemType Type             { get; set; } = RemoteItemType.NonInventory;
    public string?        Description      { get; set; }
    public decimal?       UnitPrice        { get; set; }
    public string?        PurchaseDesc     { get; set; }
    public decimal?       PurchaseCost     { get; set; }
    public string?        IncomeAccountId  { get; set; }
    public string?        ExpenseAccountId { get; set; }
    public bool           Active           { get; set; } = true;
}

internal sealed class RemoteSalesLine
{
    public string?  ItemId      { get; set; }
    public string?  Description { get; set; }
    public decimal  Quantity    { get; set; }
    public decimal  UnitPrice   { get; set; }
    /// <summary>Always Quantity × UnitPrice (QuickBooks validates it).</summary>
    public decimal  Amount      { get; set; }
    public string?  TaxCodeId   { get; set; }
}

internal sealed class RemoteInvoice : RemoteEntity
{
    public override SyncKind Kind => SyncKind.SalesInvoice;
    public string                CustomerId        { get; set; } = string.Empty;
    public string                DocNumber         { get; set; } = string.Empty;
    public DateTime              TxnDate           { get; set; }
    public DateTime?             DueDate           { get; set; }
    public string?               CurrencyCode      { get; set; }
    /// <summary>
    /// Plan S-10: home-currency units per one unit of <see cref="CurrencyCode"/> (QuickBooks' own meaning of
    /// a transaction's ExchangeRate). Set only for a foreign-currency document in a multicurrency company,
    /// from the caller's rate for the document date; null otherwise (nothing is sent).
    /// </summary>
    public decimal?              ExchangeRate      { get; set; }
    public List<RemoteSalesLine> Lines             { get; set; } = new();
    /// <summary>Plan D-5: one fixed-amount discount line (line discounts + header discount). Zero = none.</summary>
    public decimal               DiscountAmount    { get; set; }
    public string?               DiscountAccountId { get; set; }
    public string?               CustomerMemo      { get; set; }
    public string?               PrivateNote       { get; set; }
}

internal sealed class RemoteBillLine
{
    /// <summary>Item-based line when set…</summary>
    public string?  ItemId      { get; set; }
    /// <summary>…otherwise account-based on this account.</summary>
    public string?  AccountId   { get; set; }
    public string?  Description { get; set; }
    public decimal? Quantity    { get; set; }
    public decimal? UnitPrice   { get; set; }
    public decimal  Amount      { get; set; }
    public string?  TaxCodeId   { get; set; }
}

internal sealed class RemoteBill : RemoteEntity
{
    public override SyncKind Kind => SyncKind.Bill;
    public string               VendorId     { get; set; } = string.Empty;
    public string               DocNumber    { get; set; } = string.Empty;
    public DateTime             TxnDate      { get; set; }
    public DateTime?            DueDate      { get; set; }
    public string?              CurrencyCode { get; set; }
    /// <summary>As <see cref="RemoteInvoice.ExchangeRate"/>: home units per one unit of <see cref="CurrencyCode"/>, foreign bills only.</summary>
    public decimal?             ExchangeRate { get; set; }
    public List<RemoteBillLine> Lines        { get; set; } = new();
    public string?              PrivateNote  { get; set; }
}

// ── Reference data ──────────────────────────────────────────────────────────────────────────

internal sealed record RemoteAccount(string Id, string Name, string AccountType, string? AccountSubType, string? Classification, string? CurrencyCode, bool Active);
internal sealed record RemoteTaxCode(string Id, string Name, string? Description, bool Taxable, decimal? RatePercent, bool Active);
internal sealed record RemoteTerm(string Id, string Name, int? DueDays, bool Active);
internal sealed record RemoteCurrency(string Code, string Name);

internal sealed record RemotePreferences(
    string? HomeCurrencyCode,
    bool    MultiCurrencyEnabled,
    bool    CustomTxnNumbersEnabled,
    bool    UsingSalesTax)
{
    /// <summary>
    /// QuickBooks' "Allow discount" sales-form preference. The invoice discount line (plan D-5) is
    /// refused while it is off. Null when the company did not say (older snapshots).
    /// </summary>
    public bool? DiscountsEnabled { get; init; }
}

internal sealed record RemoteCompanyInfo(string CompanyName, string? LegalName, string? Country, string? Email);

internal sealed class RemoteReferenceData
{
    public IReadOnlyList<RemoteAccount>  Accounts    { get; init; } = [];
    public IReadOnlyList<RemoteTaxCode>  TaxCodes    { get; init; } = [];
    public IReadOnlyList<RemoteTerm>     Terms       { get; init; } = [];
    public IReadOnlyList<RemoteCurrency> Currencies  { get; init; } = [];
    public RemotePreferences?            Preferences { get; init; }
    public RemoteCompanyInfo?            CompanyInfo { get; init; }
}

/// <summary>
/// Everything the sync engine asks of an accounting system. One implementation per provider,
/// resolved by <see cref="ProviderKey"/> through <see cref="IAccountingProviderRegistry"/>.
/// Implementations never throw for a provider-side failure — they return it as a
/// <see cref="ProviderResult{T}"/> so the caller can tell "they said no" from "we do not know".
/// </summary>
internal interface IAccountingProvider
{
    string ProviderKey { get; }

    Task<ProviderResult<RemoteCompanyInfo>>   GetCompanyInfoAsync(ProviderContext ctx, CancellationToken ct = default);
    Task<ProviderResult<RemoteReferenceData>> GetReferenceDataAsync(ProviderContext ctx, CancellationToken ct = default);

    Task<ProviderResult<RemoteRecord>> CreateAsync(ProviderContext ctx, RemoteEntity entity, CancellationToken ct = default);

    /// <summary>Always a sparse update — never clears fields the accountant added in QuickBooks.</summary>
    Task<ProviderResult<RemoteRecord>> UpdateAsync(
        ProviderContext ctx, RemoteEntity entity, string remoteId, string syncToken, CancellationToken ct = default);

    Task<ProviderResult<RemoteRecord>> GetByIdAsync(ProviderContext ctx, SyncKind kind, string remoteId, CancellationToken ct = default);

    /// <summary>Null value (with Succeeded) when nothing matches.</summary>
    Task<ProviderResult<RemoteRecord?>> FindAsync(ProviderContext ctx, SyncKind kind, RemoteLookup lookup, CancellationToken ct = default);

    Task<ProviderResult<RemoteRecord>> VoidInvoiceAsync(ProviderContext ctx, string remoteId, string syncToken, CancellationToken ct = default);

    /// <summary>A page of Customers / Vendors / Items for matching. <paramref name="startPosition"/> is 1-based.</summary>
    Task<ProviderResult<IReadOnlyList<RemoteListEntry>>> ListAsync(
        ProviderContext ctx, SyncKind kind, int startPosition, int maxResults, CancellationToken ct = default);

    /// <summary>"View in QuickBooks" link for a record, or null for kinds with no page.</summary>
    string? BuildDeepLink(IntegrationEnvironment environment, SyncKind kind, string remoteId);
}

internal interface IAccountingProviderRegistry
{
    IAccountingProvider Get(string providerKey);
}

// ── OAuth ───────────────────────────────────────────────────────────────────────────────────

internal sealed record TokenGrant(
    string   AccessToken,
    string   RefreshToken,
    DateTime AccessTokenExpiresAt,
    DateTime RefreshTokenExpiresAt);

/// <summary>Thrown by <see cref="IAccountingAuthProvider.RefreshAsync"/> when the grant is gone (invalid_grant).</summary>
internal sealed class AuthorizationRevokedException : Exception
{
    public AuthorizationRevokedException(string message, Exception? inner = null) : base(message, inner) { }
}

internal interface IAccountingAuthProvider
{
    string ProviderKey { get; }

    /// <summary>The provider's consent page, carrying our single-use state token.</summary>
    string BuildConsentUrl(string state);

    Task<TokenGrant> ExchangeCodeAsync(string code, CancellationToken ct = default);

    /// <summary>Throws <see cref="AuthorizationRevokedException"/> when the refresh token is no longer valid.</summary>
    Task<TokenGrant> RefreshAsync(string refreshToken, CancellationToken ct = default);

    /// <summary>Best effort — disconnect proceeds on our side even if this fails.</summary>
    Task RevokeAsync(string refreshToken, CancellationToken ct = default);
}
