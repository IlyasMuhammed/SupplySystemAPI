using SMS.Shared.Exceptions;

namespace SMS.Shared.Common;

// A35 — Multi-currency (docs/multi-currency/ADDENDUM-35-ANALYSIS.md D-1..D-21, API-CONTRACT.md). Owner: CUR; changes go to
// CUR by message.
//
// Who implements what:
//   Finance   implements  ICurrencyService, IOrgCurrencyLookup, IExchangeRateProvider (re-implemented, D-4),
//                         ICurrencyUsageChecker (issued sales invoices / approved supplier invoices / non-system rates)
//   Demand    implements  ICurrencyUsageChecker (confirmed SOs, sent quotations, approved+ POs)
//   Suppliers implements  IPartnerCurrencyDefaults (D-9)
//   Tenancy   implements  IOrganizationCurrencyService (+ GetBaseCurrencyIdAsync(org, domain), GetSettingsAsync → OrgCurrencySettingsSnapshot)
//
// Currency identity is the global lookups.Currencies Guid everywhere (D-1). Every rate is "units of the organization's RATE
// CURRENCY per 1 unit of X" (D-2); the rate currency's own rate is 1.0 forever. X → Y on date d = amount × rate(X,d) / rate(Y,d).
//
// Every interface here is optional-safe for module test hosts: resolve with GetService / IEnumerable<T> or take it as an
// optional constructor parameter. Methods that take an organization explicitly treat other organizations' rows as absent
// (the EF tenant filter is off for super admins and in Hangfire); the parameterless ones use the caller's tenant.

/// <summary>A35 C3 — which of the organization's three base currencies a document converts to (D-7).</summary>
public enum TransactionDomain
{
    Sale,
    Purchase,
    Service
}

/// <summary>A35 — fixed values shared by every module.</summary>
public static class CurrencyConventions
{
    /// <summary>EffectiveTo of the currently active rate (spec §3.1).</summary>
    public static readonly DateOnly OpenEnd       = new(9999, 12, 31);
    /// <summary>EffectiveFrom of the rate currency's permanent 1.0 row (spec §3.5).</summary>
    public static readonly DateOnly SystemStart   = new(2000, 1, 1);
    /// <summary>Decimals a stored or locked exchange rate keeps (decimal(18,10)).</summary>
    public const int RateDecimals = 10;
    /// <summary>Decimals used when a currency has no org configuration row.</summary>
    public const int DefaultDecimalPlaces = 2;
    /// <summary>The fallback base / rate currency code when an organization has none configured (D-2, D-7).</summary>
    public const string FallbackCurrencyCode = "PKR";

    public const string SourceManual = "MANUAL";
    public const string SourceSystem = "SYSTEM";

    public const string SymbolBefore = "before";
    public const string SymbolAfter  = "after";

    /// <summary>Round a money amount at a currency's decimals, away from zero (D-13).</summary>
    public static decimal RoundAmount(decimal amount, int decimalPlaces) =>
        Math.Round(amount, Math.Clamp(decimalPlaces, 0, 6), MidpointRounding.AwayFromZero);

    /// <summary>Round an exchange rate to the stored precision (10 decimals, away from zero).</summary>
    public static decimal RoundRate(decimal rate) =>
        Math.Round(rate, RateDecimals, MidpointRounding.AwayFromZero);

    public static string DomainCode(TransactionDomain domain) => domain switch
    {
        TransactionDomain.Sale     => "SALE",
        TransactionDomain.Purchase => "PURCHASE",
        _                          => "SERVICE"
    };
}

/// <summary>
/// A35 C2 — no exchange rate covers the date (BR-C2-05, D-5). A <see cref="BadRequestException"/>, so the API answers 400
/// with <see cref="Exception.Message"/>: "No exchange rate for {CODE} on {yyyy-MM-dd}. Add one under Settings → Exchange Rates."
/// </summary>
public sealed class CurrencyRateNotFoundException : BadRequestException
{
    public CurrencyRateNotFoundException(Guid currencyId, string? currencyCode, DateOnly date)
        : base(BuildMessage(currencyCode, currencyId, date))
    {
        CurrencyId   = currencyId;
        CurrencyCode = currencyCode;
        Date         = date;
    }

    public Guid     CurrencyId   { get; }
    public string?  CurrencyCode { get; }
    public DateOnly Date         { get; }

    public static string BuildMessage(string? currencyCode, Guid currencyId, DateOnly date) =>
        $"No exchange rate for {(string.IsNullOrWhiteSpace(currencyCode) ? currencyId.ToString() : currencyCode.Trim().ToUpperInvariant())} "
      + $"on {date:yyyy-MM-dd}. Add one under Settings → Exchange Rates.";
}

/// <summary>A35 C2 — one stored rate row: units of the organization's rate currency per 1 unit of <see cref="CurrencyCode"/>.</summary>
public sealed record CurrencyRateInfo(
    Guid     Id,
    Guid     CurrencyId,
    string   CurrencyCode,
    decimal  Rate,
    decimal  InverseRate,
    DateOnly EffectiveFrom,
    DateOnly EffectiveTo,
    string   Source,
    string?  Notes)
{
    public bool IsCurrent => EffectiveTo == CurrencyConventions.OpenEnd;
}

/// <summary>A35 C6 — the result of a conversion (spec §7.1, Guid ids).</summary>
/// <param name="RateUsed">Units of To per 1 From on <paramref name="RateDate"/> (cross rate through the rate currency, 10dp). 1 when same currency.</param>
/// <param name="ConvertedAmount">Rounded at the To currency's decimal places (D-13).</param>
public sealed record CurrencyConversionResult(
    decimal  OriginalAmount,
    Guid     FromCurrencyId,
    string   FromCurrencyCode,
    decimal  ConvertedAmount,
    Guid     ToCurrencyId,
    string   ToCurrencyCode,
    decimal  RateUsed,
    DateOnly RateDate);

/// <summary>
/// A35 C6 — the rate a document locks (D-12): units of the domain base per 1 unit of the document currency on
/// <see cref="RateDate"/>. The caller persists <see cref="Rate"/>, <see cref="BaseCurrencyId"/> and its lock timestamp on the
/// document; nothing is stored by the service (BR-C6-02).
/// </summary>
/// <param name="SameCurrency">True when currency = base: <see cref="Rate"/> is exactly 1 and no rate was looked up (BR-C5-04).</param>
/// <param name="BaseDecimalPlaces">Decimals of the base currency, for rounding the *_base amounts (D-13).</param>
/// <param name="CurrencyDecimalPlaces">Decimals of the document currency.</param>
public sealed record DocumentRateLock(
    Guid              CurrencyId,
    string            CurrencyCode,
    Guid              BaseCurrencyId,
    string            BaseCurrencyCode,
    TransactionDomain Domain,
    decimal           Rate,
    DateOnly          RateDate,
    bool              SameCurrency,
    int               CurrencyDecimalPlaces,
    int               BaseDecimalPlaces)
{
    /// <summary>amount (in the document currency) × Rate, rounded at the base currency's decimals.</summary>
    public decimal ToBase(decimal amount) =>
        SameCurrency ? CurrencyConventions.RoundAmount(amount, BaseDecimalPlaces)
                     : CurrencyConventions.RoundAmount(amount * Rate, BaseDecimalPlaces);
}

/// <summary>
/// A35 C6 — currency conversion and rate locking (spec §7.1 with Guid ids, D-2/D-4). Implemented in Finance over
/// <c>finance.currency_rates</c>. The parameterless-organization overloads use the caller's tenant; the
/// <c>organizationId</c> overloads are for Hangfire jobs and cross-tenant callers (super admin) and never read another
/// organization's rows. Every method that needs a rate it cannot find throws <see cref="CurrencyRateNotFoundException"/> (400).
/// Same-currency work never looks a rate up (BR-C5-04).
/// </summary>
public interface ICurrencyService
{
    /// <summary>The stored rate (vs the rate currency) covering <paramref name="date"/>. Rate currency → its 1.0 row (synthesised if missing).</summary>
    Task<CurrencyRateInfo> GetRateAsync(Guid currencyId, DateOnly date, CancellationToken ct = default);
    Task<CurrencyRateInfo> GetRateAsync(Guid organizationId, Guid currencyId, DateOnly date, CancellationToken ct = default);

    /// <summary>amount From → To on date, through the rate currency (BR-C6-01). Rounded at To's decimals.</summary>
    Task<CurrencyConversionResult> ConvertAsync(decimal amount, Guid fromCurrencyId, Guid toCurrencyId, DateOnly date, CancellationToken ct = default);
    Task<CurrencyConversionResult> ConvertAsync(Guid organizationId, decimal amount, Guid fromCurrencyId, Guid toCurrencyId, DateOnly date, CancellationToken ct = default);

    /// <summary>amount in <paramref name="sourceCurrencyId"/> → the organization's <paramref name="domain"/> base.</summary>
    Task<CurrencyConversionResult> ToBaseCurrencyAsync(decimal amount, Guid sourceCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default);
    Task<CurrencyConversionResult> ToBaseCurrencyAsync(Guid organizationId, decimal amount, Guid sourceCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default);

    /// <summary>amount in the organization's <paramref name="domain"/> base → <paramref name="targetCurrencyId"/>.</summary>
    Task<CurrencyConversionResult> FromBaseCurrencyAsync(decimal amount, Guid targetCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default);
    Task<CurrencyConversionResult> FromBaseCurrencyAsync(Guid organizationId, decimal amount, Guid targetCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default);

    /// <summary>The rate a document in <paramref name="currencyId"/> locks against its <paramref name="domain"/> base on <paramref name="date"/> (D-12).</summary>
    Task<DocumentRateLock> LockRateAsync(Guid currencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default);
    Task<DocumentRateLock> LockRateAsync(Guid organizationId, Guid currencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default);

    /// <summary>Every currently active rate (EffectiveTo = 9999-12-31), the rate currency's 1.0 row included.</summary>
    Task<IReadOnlyList<CurrencyRateInfo>> GetActiveRatesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CurrencyRateInfo>> GetActiveRatesAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>The organization's base currency for the domain (D-7 fallback: Organization.BaseCurrency ?? PKR).</summary>
    Task<Guid> GetBaseCurrencyIdAsync(TransactionDomain domain, CancellationToken ct = default);
    Task<Guid> GetBaseCurrencyIdAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default);
}

/// <summary>A35 C1 — one of an organization's currencies with its formatting (finance.org_currencies, D-1).</summary>
public sealed record OrgCurrencyInfo(
    Guid    CurrencyId,
    string  Code,
    string  Name,
    string  Symbol,
    int     DecimalPlaces,
    decimal Rounding,
    string  SymbolPosition,
    bool    IsActive,
    int     DisplayOrder)
{
    /// <summary>The amount rounded at this currency's decimals, away from zero (BR-C1-05, D-13).</summary>
    public decimal Round(decimal amount) => CurrencyConventions.RoundAmount(amount, DecimalPlaces);
}

/// <summary>
/// A35 C1 / D-13 — an organization's currency configuration for other modules (rounding, display, "is it an active org
/// currency" for BR-C3-02 / BR-C4-01). Implemented in Finance. Explicit organization; another org's rows are absent.
/// </summary>
public interface IOrgCurrencyLookup
{
    /// <summary>Null when the currency is not configured for the organization (callers round at <see cref="CurrencyConventions.DefaultDecimalPlaces"/>).</summary>
    Task<OrgCurrencyInfo?> GetAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default);
    Task<OrgCurrencyInfo?> GetByCodeAsync(Guid organizationId, string code, CancellationToken ct = default);
    Task<IReadOnlyList<OrgCurrencyInfo>> ListAsync(Guid organizationId, bool activeOnly, CancellationToken ct = default);
    /// <summary>The currency's decimals for the organization, <see cref="CurrencyConventions.DefaultDecimalPlaces"/> when not configured.</summary>
    Task<int> GetDecimalPlacesAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default);
}

/// <summary>
/// A35 D-7 — an organization's currency settings as Tenancy returns them from
/// <c>IOrganizationCurrencyService.GetSettingsAsync(org)</c>. Never null: a missing settings row reads as all three bases and
/// the rate currency = Organization.BaseCurrency ?? PKR, with <see cref="IsStored"/> false.
/// </summary>
public sealed record OrgCurrencySettingsSnapshot(
    Guid    OrganizationId,
    Guid    SaleBaseCurrencyId,
    Guid    PurchaseBaseCurrencyId,
    Guid    ServiceBaseCurrencyId,
    Guid    RateCurrencyId,
    string? ExchangeGainAccountCode,
    string? ExchangeLossAccountCode,
    string? UnrealizedGainAccountCode,
    string? UnrealizedLossAccountCode,
    bool    IsStored)
{
    public Guid BaseFor(TransactionDomain domain) => domain switch
    {
        TransactionDomain.Sale     => SaleBaseCurrencyId,
        TransactionDomain.Purchase => PurchaseBaseCurrencyId,
        _                          => ServiceBaseCurrencyId
    };
}

/// <summary>
/// A35 D-8 — "is this currency / this domain's base used by locked documents?" (BR-C1-03/04, BR-C3-03). Multi-registration:
/// Demand (confirmed SOs ≠ DRAFT/CANCELLED, sent quotations → Sale; approved+ POs → Purchase) and Finance (issued sales
/// invoices → Sale; approved supplier invoices → Purchase; non-system rates). Callers ask every registered checker and
/// refuse with 409 (settings) / 400 (currency deactivation) on the first non-null answer. Explicit organization.
/// Each method returns a short human description of the usage ("3 confirmed sale orders") or null when unused.
/// </summary>
public interface ICurrencyUsageChecker
{
    /// <summary>Locked documents of this module whose transaction currency or base currency is <paramref name="currencyId"/>.</summary>
    Task<string?> DescribeCurrencyUsageAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default);

    /// <summary>Locked documents of this module in <paramref name="domain"/> (their base is the current domain base).</summary>
    Task<string?> DescribeDomainBaseUsageAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default);

    /// <summary>Data expressed against the rate currency (non-SYSTEM rates). Only Finance answers; default: unused.</summary>
    Task<string?> DescribeRateCurrencyUsageAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>
/// A35 — a step that must run after Finance has the organization's currencies and rates in place (org currencies seeded,
/// the rate currency's 1.0 row, legacy <c>finance.exchange_rates</c> converted). Multi-registration. Finance's
/// <c>CurrencyBootstrapper</c> calls every registered participant, per organization, in the same startup backfill (API start,
/// after Finance migrates) and from its provisioning handler, under the same per-organization app lock, after its own work.
/// Must be idempotent (it runs on every API start) and must stamp/filter the organization it is given. A participant that
/// throws is logged and skipped; the next start retries.
/// </summary>
public interface ICurrencyRatesReadyParticipant
{
    Task OnCurrencyRatesReadyAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>A35 C4 — a business partner's default currencies (D-9). Partner = suppliers.BusinessPartners.UUID.</summary>
public sealed record PartnerCurrencyDefaults(
    Guid  PartnerId,
    Guid? DefaultSaleCurrencyId,
    Guid? DefaultPurchaseCurrencyId);

/// <summary>
/// A35 C4 / D-9 / D-14 — partner default currencies, implemented in Suppliers. Explicit organization; a partner of another
/// organization is absent (null).
/// </summary>
public interface IPartnerCurrencyDefaults
{
    Task<PartnerCurrencyDefaults?> GetAsync(Guid organizationId, Guid partnerId, CancellationToken ct = default);

    /// <summary>
    /// The currency a new document for the partner defaults to: the partner's default for the domain (Sale →
    /// DefaultSaleCurrency, Purchase → DefaultPurchaseCurrency (= PreferredCurrency); Service has no partner default),
    /// else the organization's domain base (BR-C4-02/03). Unknown partner → the domain base.
    /// </summary>
    Task<Guid> ResolveDefaultCurrencyAsync(Guid organizationId, Guid partnerId, TransactionDomain domain, CancellationToken ct = default);
}
