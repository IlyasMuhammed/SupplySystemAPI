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
    bool     Active      = true)
{
    /// <summary>
    /// A44 D-53: something the write did that the record's warning should say — e.g. the credit memo was created but
    /// applying it to its invoice failed. Null for every write before A44.
    /// </summary>
    public string? Note { get; init; }
}

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

    /// <summary>
    /// A44: customer-side documents (payments, receipts, credit memos) filtered on the results by customer when given,
    /// as bills are by <see cref="VendorRemoteId"/>.
    /// </summary>
    public string? CustomerRemoteId { get; init; }

    /// <summary>
    /// A44 D-52: payments carry no DocNumber in QuickBooks (only PaymentRefNum, which is not a filterable field), so a
    /// payment is looked up by its date and then by reference number and customer on the results.
    /// </summary>
    public DateTime? TxnDate { get; init; }
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
    /// <summary>
    /// True for an update of an item adopted from the accountant's QuickBooks records (matching, or a duplicate-name
    /// link): its type — Service, NonInventory, Inventory or any other — is theirs and is not sent, so a later change
    /// in SCM never tries to convert it (QuickBooks refuses converting an Inventory item; others would change silently).
    /// </summary>
    public bool           KeepRemoteType   { get; set; }
    public string?        Description      { get; set; }
    public decimal?       UnitPrice        { get; set; }
    public string?        PurchaseDesc     { get; set; }
    public decimal?       PurchaseCost     { get; set; }
    public string?        IncomeAccountId  { get; set; }
    public string?        ExpenseAccountId { get; set; }
    public bool           Active           { get; set; } = true;
}

/// <summary>
/// A44 D-55a (QA-1): a sparse item update that only adds the income account to an item created as not-for-sale, so a
/// sales document using it is accepted. Only ever sent as an update (Id + SyncToken + IncomeAccountRef); every other
/// field of the item is left as it is.
/// </summary>
internal sealed class RemoteItemIncomeAccountFix : RemoteEntity
{
    public override SyncKind Kind => SyncKind.Item;
    public string IncomeAccountId { get; set; } = string.Empty;
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
    /// <summary>
    /// A44 D-58: the line's class, for a company that tracks classes per line (QuickBooks then reads the class from
    /// the lines, not the header). Null = not sent, as before A44.
    /// </summary>
    public string?  ClassId     { get; set; }
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
    /// <summary>A44 D-15 provider defaults — null (nothing sent) unless configured for the connection.</summary>
    public string?               ClassId           { get; set; }
    public string?               DepartmentId      { get; set; }
    public string?               SalesTermId       { get; set; }
}

// ── A44 (D-19..D-24): payments and credits ─────────────────────────────────────────────────

/// <summary>What sales receipts, credit memos and refund receipts share with an invoice (lines + discount line).</summary>
internal abstract class RemoteSalesDocument : RemoteEntity
{
    public string                CustomerId        { get; set; } = string.Empty;
    public string                DocNumber         { get; set; } = string.Empty;
    public DateTime              TxnDate           { get; set; }
    public string?               CurrencyCode      { get; set; }
    /// <summary>As <see cref="RemoteInvoice.ExchangeRate"/>.</summary>
    public decimal?              ExchangeRate      { get; set; }
    public List<RemoteSalesLine> Lines             { get; set; } = new();
    public decimal               DiscountAmount    { get; set; }
    public string?               DiscountAccountId { get; set; }
    public string?               PrivateNote       { get; set; }
    public string?               ClassId           { get; set; }
    public string?               DepartmentId      { get; set; }
}

internal sealed class RemoteSalesReceipt : RemoteSalesDocument
{
    public override SyncKind Kind => SyncKind.SalesReceipt;
    /// <summary>Null: QuickBooks' default (Undeposited Funds).</summary>
    public string?  DepositToAccountId { get; set; }
    public string?  PaymentMethodId    { get; set; }
}

internal sealed class RemoteCreditMemo : RemoteSalesDocument
{
    public override SyncKind Kind => SyncKind.CreditMemo;
    /// <summary>
    /// D-23 ON_ACCOUNT: the invoice the credit is applied to. After a create, the provider links the two with a
    /// zero-total Payment (QuickBooks has no other way to apply a credit memo to an invoice). Never re-applied on an update.
    /// </summary>
    public string?  ApplyToInvoiceId { get; set; }
    /// <summary>The amount applied: the credit memo's own total as SCM computed it.</summary>
    public decimal  ApplyAmount      { get; set; }
}

internal sealed class RemoteRefundReceipt : RemoteSalesDocument
{
    public override SyncKind Kind => SyncKind.RefundReceipt;
    /// <summary>The bank / cash account the refund is paid from (QuickBooks: DepositToAccountRef). Required by QuickBooks.</summary>
    public string?  RefundFromAccountId { get; set; }
    public string?  PaymentMethodId     { get; set; }
}

internal sealed class RemotePaymentLine
{
    public string  InvoiceId { get; set; } = string.Empty;
    public decimal Amount    { get; set; }
}

internal sealed class RemotePayment : RemoteEntity
{
    public override SyncKind Kind => SyncKind.Payment;
    public string                  CustomerId         { get; set; } = string.Empty;
    /// <summary>Sent as PaymentRefNum (QuickBooks payments have no DocNumber of their own).</summary>
    public string                  ReferenceNumber    { get; set; } = string.Empty;
    public DateTime                TxnDate            { get; set; }
    public string?                 CurrencyCode       { get; set; }
    public decimal?                ExchangeRate       { get; set; }
    /// <summary>The whole receipt; what the lines do not apply stays as unapplied credit on the customer.</summary>
    public decimal                 TotalAmount        { get; set; }
    public List<RemotePaymentLine> Lines              { get; set; } = new();
    public string?                 DepositToAccountId { get; set; }
    public string?                 PaymentMethodId    { get; set; }
    public string?                 PrivateNote        { get; set; }
}

internal enum RemoteBillPayType
{
    Check,
    CreditCard
}

internal sealed class RemoteBillPaymentLine
{
    public string  BillId { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

internal sealed class RemoteBillPayment : RemoteEntity
{
    public override SyncKind Kind => SyncKind.BillPayment;
    public string                      VendorId     { get; set; } = string.Empty;
    public string                      DocNumber    { get; set; } = string.Empty;
    public DateTime                    TxnDate      { get; set; }
    public string?                     CurrencyCode { get; set; }
    public decimal?                    ExchangeRate { get; set; }
    public decimal                     TotalAmount  { get; set; }
    public RemoteBillPayType           PayType      { get; set; } = RemoteBillPayType.Check;
    /// <summary>The bank account (Check) or credit-card account (CreditCard) paid from.</summary>
    public string?                     AccountId    { get; set; }
    public List<RemoteBillPaymentLine> Lines        { get; set; } = new();
    public string?                     PrivateNote  { get; set; }
    public string?                     DepartmentId { get; set; }
}

internal sealed class RemoteVendorCredit : RemoteEntity
{
    public override SyncKind Kind => SyncKind.VendorCredit;
    public string               VendorId     { get; set; } = string.Empty;
    public string               DocNumber    { get; set; } = string.Empty;
    public DateTime             TxnDate      { get; set; }
    public string?              CurrencyCode { get; set; }
    public decimal?             ExchangeRate { get; set; }
    public List<RemoteBillLine> Lines        { get; set; } = new();
    public string?              PrivateNote  { get; set; }
    public string?              DepartmentId { get; set; }
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
    /// <summary>A44 D-15: QuickBooks keeps the class of a bill / vendor credit on its lines.</summary>
    public string?  ClassId     { get; set; }
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
    /// <summary>A44 D-15 (QuickBooks "Location"); null = not sent.</summary>
    public string?              DepartmentId { get; set; }
}

// ── Reference data ──────────────────────────────────────────────────────────────────────────

// (A44 reference additions — AcctNum, classes, departments, payment methods, tracking preferences — are CTRL's, D-66.)
internal sealed record RemoteAccount(string Id, string Name, string AccountType, string? AccountSubType, string? Classification, string? CurrencyCode, bool Active)
{
    /// <summary>A44 D-16: QuickBooks' account number, when the company uses them — the first auto-map rule.</summary>
    public string? AcctNum { get; init; }
}
internal sealed record RemoteTaxCode(string Id, string Name, string? Description, bool Taxable, decimal? RatePercent, bool Active);
internal sealed record RemoteTerm(string Id, string Name, int? DueDays, bool Active);
internal sealed record RemoteCurrency(string Code, string Name);
// A44 D-15 — what provider defaults are chosen from. Optional parts of the reference data (empty when not read).
internal sealed record RemoteClass(string Id, string Name, string? FullyQualifiedName, bool Active);
/// <summary>QuickBooks "Department" — shown to users as Location (or whatever the company calls it).</summary>
internal sealed record RemoteDepartment(string Id, string Name, string? FullyQualifiedName, bool Active);
/// <summary><see cref="Type"/>: QuickBooks' CREDIT_CARD | NON_CREDIT_CARD, when given.</summary>
internal sealed record RemotePaymentMethod(string Id, string Name, string? Type, bool Active);

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

    // A44 D-15: a class / location is sent only when the company tracks it. Null = not known (older snapshots).
    public bool?   ClassTrackingPerTxn     { get; init; }
    public bool?   ClassTrackingPerTxnLine { get; init; }
    public bool?   TrackDepartments        { get; init; }
    /// <summary>What the company calls departments (QuickBooks' "Location" by default).</summary>
    public string? DepartmentTerminology   { get; init; }

    /// <summary>True / false when known: the company tracks classes, per transaction or per line.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? ClassTrackingEnabled =>
        ClassTrackingPerTxn is null && ClassTrackingPerTxnLine is null ? null : ClassTrackingPerTxn == true || ClassTrackingPerTxnLine == true;
}

internal sealed record RemoteCompanyInfo(string CompanyName, string? LegalName, string? Country, string? Email);

internal sealed class RemoteReferenceData
{
    public IReadOnlyList<RemoteAccount>  Accounts    { get; init; } = [];
    public IReadOnlyList<RemoteTaxCode>  TaxCodes    { get; init; } = [];
    public IReadOnlyList<RemoteTerm>     Terms       { get; init; } = [];
    public IReadOnlyList<RemoteCurrency> Currencies  { get; init; } = [];
    public IReadOnlyList<RemoteClass>         Classes        { get; init; } = [];
    public IReadOnlyList<RemoteDepartment>    Departments    { get; init; } = [];
    public IReadOnlyList<RemotePaymentMethod> PaymentMethods { get; init; } = [];
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

    /// <summary>
    /// A44 D-51: undoes a document of any kind that QuickBooks lets an integration undo — a void for Invoice, Payment
    /// and SalesReceipt, a delete for BillPayment and RefundReceipt. Other kinds are Refused with
    /// <see cref="VoidUnsupportedCode"/> (the gateway flags those for the accountant and never asks).
    /// The default serves providers (and fakes) written before A44: invoices only.
    /// </summary>
    Task<ProviderResult<RemoteRecord>> VoidAsync(
        ProviderContext ctx, SyncKind kind, string remoteId, string syncToken, CancellationToken ct = default) =>
        kind == SyncKind.SalesInvoice
            ? VoidInvoiceAsync(ctx, remoteId, syncToken, ct)
            : Task.FromResult(ProviderResult<RemoteRecord>.Fail(ProviderOutcomeKind.Refused, VoidUnsupportedCode,
                $"This accounting system cannot undo a {kind} through the integration."));

    public const string VoidUnsupportedCode = "VOID_UNSUPPORTED";

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

/// <summary>
/// The OAuth app a call is made as — resolved per organization by <c>IQuickBooksAppResolver</c>
/// (docs/quickbooks/APP-CREDENTIALS.md). Holds a plaintext secret: never log it, never put it in a model.
/// </summary>
internal sealed record OAuthApp(
    string                 ClientId,
    string                 ClientSecret,
    string                 RedirectUri,
    IntegrationEnvironment Environment,
    AppCredentialSource    Source)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret) && !string.IsNullOrWhiteSpace(RedirectUri);

    /// <summary>Never the secret, whatever prints this record.</summary>
    public override string ToString() => $"OAuthApp {{ ClientId = {ClientId}, Environment = {Environment}, Source = {Source} }}";
}

internal interface IAccountingAuthProvider
{
    string ProviderKey { get; }

    /// <summary>The provider's consent page, carrying our single-use state token.</summary>
    string BuildConsentUrl(OAuthApp app, string state);

    /// <summary>Must be the same <paramref name="app"/> the consent URL was built with.</summary>
    Task<TokenGrant> ExchangeCodeAsync(OAuthApp app, string code, CancellationToken ct = default);

    /// <summary>
    /// Throws <see cref="AuthorizationRevokedException"/> when the refresh token is no longer valid. Must be the app the
    /// token was issued to (the connection's pinned app).
    /// </summary>
    Task<TokenGrant> RefreshAsync(OAuthApp app, string refreshToken, CancellationToken ct = default);

    /// <summary>Best effort — disconnect proceeds on our side even if this fails.</summary>
    Task RevokeAsync(OAuthApp app, string refreshToken, CancellationToken ct = default);
}
