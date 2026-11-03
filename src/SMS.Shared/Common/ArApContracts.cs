namespace SMS.Shared.Common;

// Addendum 48 — AR & AP sub-ledgers: the cross-module seams (docs/ar-ap-subledgers/API-CONTRACT.md §10). Every method takes
// the organization explicitly (the tenant filter is off for super admins, and Hangfire jobs have no request) and treats
// another organization's rows as absent.

/// <summary>The two roles a business partner plays in the sub-ledgers.</summary>
public static class PartnerRoles
{
    public const string Customer = "Customer";
    public const string Vendor   = "Vendor";

    public static readonly IReadOnlyList<string> All = [Customer, Vendor];
}

/// <summary>A business partner as Finance displays it (D-3). <see cref="Email"/> = the partner's, else the primary contact's.</summary>
public sealed record PartnerInfo(
    Guid Uuid, string Code, string Name, bool IsCustomer, bool IsVendor, bool IsActive,
    string? Email, string? Phone, string? Address, string? ContactName, string? ContactEmail,
    decimal? CreditLimit, int PaymentTermsDays, string? CustomerType);

/// <summary>A vendor's bank details, decrypted — for the payment file only (D-15). Never persist or log them.</summary>
public sealed record PartnerBankInfo(Guid Uuid, string? BankName, string? AccountNumber, string? Iban, string? Swift);

/// <summary>
/// A48 D-3 — business partners for the sub-ledgers, without a reference to Suppliers (implemented there, scoped, with a
/// per-request cache). <c>role</c>: null = any partner; <see cref="PartnerRoles.Customer"/> / <see cref="PartnerRoles.Vendor"/> =
/// only partners carrying that flag; any other value throws <see cref="ArgumentException"/>. Deleted partners and other
/// organizations' are absent (null / missing from the dictionary), never an exception.
/// </summary>
public interface IPartnerLookupService
{
    Task<PartnerInfo?> GetAsync(Guid organizationId, Guid partnerUuid, string? role = null, CancellationToken ct = default);

    /// <summary>One query per call for the uuids not cached yet (in chunks); the absent ones are missing from the result.</summary>
    Task<IReadOnlyDictionary<Guid, PartnerInfo>> GetManyAsync(
        Guid organizationId, IEnumerable<Guid> partnerUuids, string? role = null, CancellationToken ct = default);

    /// <summary>Partners whose name or code contains <paramref name="search"/> (by name, at most <paramref name="limit"/>).</summary>
    Task<IReadOnlyList<PartnerInfo>> SearchAsync(
        Guid organizationId, string search, string? role = null, int limit = 200, CancellationToken ct = default);

    /// <summary>Decrypted bank details, null when the partner is absent or has none.</summary>
    Task<PartnerBankInfo?> GetBankDetailsAsync(Guid organizationId, Guid partnerUuid, CancellationToken ct = default);
}

/// <summary>A48 D-5 — the organization's credit-control mode (finance.org_settings.CreditControlMode). Off by default.</summary>
public static class CreditControlModes
{
    public const string Off   = "Off";
    public const string Warn  = "Warn";
    public const string Block = "Block";

    public static readonly IReadOnlyList<string> All = [Off, Warn, Block];
}

/// <summary>
/// What is checked. <paramref name="Amount"/> is in <paramref name="CurrencyCode"/> (null = the sale base);
/// <paramref name="AmountBase"/>, when the caller has it (a locked sale order's GrandTotalBase), wins over converting.
/// <paramref name="DocumentType"/>: SALE_ORDER, SALES_INVOICE or MANUAL.
/// </summary>
public sealed record CreditCheckRequest(
    Guid OrganizationId, Guid CustomerUuid, decimal Amount, string? CurrencyCode, decimal? AmountBase,
    string DocumentType, Guid? DocumentUuid = null, string? DocumentNumber = null);

/// <summary>
/// The outcome (API-CONTRACT §2). Mode Off → Approved, nothing computed. Hold blocks whenever the mode is not Off; the limit
/// blocks only in Block (Warn gives <see cref="Warning"/>). <see cref="EffectiveLimit"/> null = no limit. Amounts in
/// <see cref="BaseCurrencyCode"/>.
/// </summary>
public sealed record CreditCheckResult(
    string Mode, bool Approved, string? BlockReason, string? Warning, bool OnHold,
    decimal? EffectiveLimit, decimal OpenOrdersAmount, decimal OpenInvoicesAmount, decimal Exposure,
    decimal RequestedAmount, decimal ProjectedExposure, decimal? AvailableCredit, string? BaseCurrencyCode);

/// <summary>
/// A48 D-5 — the credit check Demand calls on sale-order confirmation (block, with CREDIT_OVERRIDE) and Finance on sales-invoice
/// creation (warning). Implemented in Finance; resolve with <c>GetService</c> (absent = no check).
/// </summary>
public interface ICreditCheck
{
    Task<CreditCheckResult> CheckAsync(CreditCheckRequest request, CancellationToken ct = default);

    /// <summary>Records that a blocked document went ahead under CREDIT_OVERRIDE (an Override row in the credit audit trail).</summary>
    Task RecordOverrideAsync(CreditCheckRequest request, CreditCheckResult result, string reason, int? userId, string? userName,
        CancellationToken ct = default);

    /// <summary>
    /// A48 AR (REV-11) — the organization's mode only (one settings read, no exposure, no write): what the sale-order confirm
    /// asks first, so Off costs nothing. Default for other implementations: the mode of a zero check.
    /// </summary>
    async Task<string> GetModeAsync(Guid organizationId, CancellationToken ct = default) =>
        (await CheckAsync(new CreditCheckRequest(organizationId, Guid.Empty, 0m, null, 0m, "MANUAL"), ct)).Mode;

    /// <summary>Serializes check + confirm per customer (an application lock on its own connection). Dispose to release.</summary>
    Task<IAsyncDisposable> LockCustomerAsync(Guid organizationId, Guid customerUuid, CancellationToken ct = default);
}
