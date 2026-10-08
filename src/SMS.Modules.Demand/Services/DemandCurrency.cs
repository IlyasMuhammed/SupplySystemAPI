using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A35 (Multi-Currency) on Demand's documents — the shared rules, so the quotation, the sale order and the purchase order
/// lock, default and validate a currency the same way (docs/multi-currency/ADDENDUM-35-ANALYSIS.md D-5, D-10..D-14).
/// <para>
/// Finance's <see cref="ICurrencyService"/> is optional like every other cross-module service here: production DI always
/// supplies it. Without it (module test hosts) a document in its domain's base still locks at 1 — no rate is needed for
/// that (BR-C5-04) — and a document in another currency is left unlocked (null rate, null base amounts), exactly as it was
/// before A35, rather than refused for a service that is not there.
/// </para>
/// </summary>
internal static class DemandCurrency
{
    public const string LockedMessage = "The currency cannot be changed after the exchange rate is locked.";

    /// <summary>
    /// The rate a document in <paramref name="currencyId"/> locks today against its <paramref name="domain"/> base (D-12).
    /// Throws <see cref="CurrencyRateNotFoundException"/> (400) when the currency is not the base and no rate covers
    /// <paramref name="date"/> (D-5). Null only without Finance for a foreign-currency document (see the class remarks).
    /// </summary>
    public static async Task<DocumentRateLock?> LockAsync(
        ICurrencyService? currency, IOrganizationCurrencyService? orgCurrency,
        Guid organizationId, Guid currencyId, DateOnly date, TransactionDomain domain)
    {
        if (currency is not null)
            return await currency.LockRateAsync(organizationId, currencyId, date, domain);

        var baseId = await BaseAsync(orgCurrency, organizationId, domain);
        return baseId is { } b && b == currencyId
            ? new DocumentRateLock(currencyId, string.Empty, b, string.Empty, domain, 1m, date, SameCurrency: true,
                CurrencyConventions.DefaultDecimalPlaces, CurrencyConventions.DefaultDecimalPlaces)
            : null;
    }

    /// <summary>
    /// D-14 / BR-C4-02/03 — a new document's currency when the request names none: the partner's default for the domain,
    /// else the organization's domain base. Null only when neither can be resolved.
    /// </summary>
    public static async Task<Guid?> DefaultForPartnerAsync(
        IPartnerCurrencyDefaults? partners, IOrganizationCurrencyService? orgCurrency,
        Guid organizationId, Guid partnerId, TransactionDomain domain)
    {
        if (partners is not null && partnerId != Guid.Empty)
        {
            var resolved = await partners.ResolveDefaultCurrencyAsync(organizationId, partnerId, domain);
            if (resolved != Guid.Empty) return resolved;
        }
        return await BaseAsync(orgCurrency, organizationId, domain);
    }

    /// <summary>
    /// The organization's base for the domain (Tenancy, D-7). The sale base also through the pre-A35 one-argument overload,
    /// which is the same value, for callers (test fakes) that only answer that one.
    /// </summary>
    public static async Task<Guid?> BaseAsync(IOrganizationCurrencyService? orgCurrency, Guid organizationId, TransactionDomain domain)
    {
        if (orgCurrency is null) return null;
        var id = await orgCurrency.GetBaseCurrencyIdAsync(organizationId, domain);
        if (id is null && domain == TransactionDomain.Sale)
            id = await orgCurrency.GetBaseCurrencyIdAsync(organizationId);
        return id is { } g && g != Guid.Empty ? g : null;
    }

    /// <summary>
    /// API-CONTRACT §0 — a currency a caller picks must be an active currency of the organization. Checked only when
    /// Finance's lookup is there (module test hosts have none) and only for an explicit choice: a default is the
    /// organization's own configuration.
    /// </summary>
    /// <remarks>
    /// Never refused: the organization's own base for the domain (it is the organization's configuration, configured or
    /// not), and any currency of an organization that has no currency configuration at all yet (not bootstrapped — the
    /// pre-A35 behaviour).
    /// </remarks>
    public static async Task RequireActiveOrgCurrencyAsync(
        IOrgCurrencyLookup? lookup, IOrganizationCurrencyService? orgCurrency, Guid organizationId, Guid currencyId,
        TransactionDomain domain, string? code = null)
    {
        if (lookup is null) return;
        var info = await lookup.GetAsync(organizationId, currencyId);
        if (info is { IsActive: true }) return;
        if (await BaseAsync(orgCurrency, organizationId, domain) == currencyId) return;
        if (info is null && (await lookup.ListAsync(organizationId, activeOnly: false)).Count == 0) return;
        throw new BadRequestException($"{info?.Code ?? code ?? "The selected currency"} is not an active currency of this organization.");
    }

    private static DateTime Stamp(DateTime lockedAt) => DateTime.SpecifyKind(lockedAt, DateTimeKind.Utc);

    /// <summary>
    /// SO at CONFIRMED (BR-C5-02/03). Lines at the base's decimals; same currency → base = amounts exactly (spec §7.5).
    /// Otherwise the header's grand total in base is the sum of the lines' (D-13 — a line total here already includes its
    /// tax and discount), tax and discount are the header amounts × rate, and the subtotal is what keeps
    /// subtotal − discount + tax = grand total true in the base too.
    /// </summary>
    public static void Apply(SaleOrder order, DocumentRateLock rate, DateTime lockedAt)
    {
        order.ExchangeRate   = rate.Rate;
        order.BaseCurrencyId = rate.BaseCurrencyId;
        order.RateLockedAt   = Stamp(lockedAt);
        foreach (var line in order.Lines)
        {
            line.UnitPriceBase = rate.ToBase(line.UnitPrice);
            line.LineTotalBase = rate.ToBase(line.LineTotal);
        }

        if (rate.SameCurrency)
        {
            order.SubtotalBase       = order.Subtotal;
            order.TaxAmountBase      = order.TaxAmount;
            order.DiscountAmountBase = order.DiscountAmount;
            order.GrandTotalBase     = order.GrandTotal;
            return;
        }

        order.TaxAmountBase      = rate.ToBase(order.TaxAmount);
        order.DiscountAmountBase = rate.ToBase(order.DiscountAmount);
        order.GrandTotalBase     = order.Lines.Sum(l => l.LineTotalBase ?? 0m);
        order.SubtotalBase       = order.GrandTotalBase - order.TaxAmountBase + order.DiscountAmountBase;
    }

    /// <summary>SQ at SENT (D-12). Every line, REJECTED ones included (they are priced at zero).</summary>
    public static void Apply(SaleQuotation quotation, DocumentRateLock rate, DateTime lockedAt)
    {
        quotation.ExchangeRate   = rate.Rate;
        quotation.BaseCurrencyId = rate.BaseCurrencyId;
        quotation.RateLockedAt   = Stamp(lockedAt);
        foreach (var line in quotation.Lines)
        {
            line.UnitPriceBase      = rate.ToBase(line.UnitPrice);
            line.DiscountAmountBase = rate.ToBase(line.Quantity * line.UnitPrice * line.DiscountPercent / 100m);
            line.TaxAmountBase      = rate.ToBase(line.TaxAmount);
            line.LineTotalBase      = rate.ToBase(line.LineTotal);
        }
    }

    /// <summary>PO at APPROVED (D-12). POs have no tax or subtotal: the total in base is the sum of the lines' (D-13).</summary>
    public static void Apply(PurchaseOrder po, DocumentRateLock rate, DateTime lockedAt)
    {
        po.CurrencyId     ??= rate.CurrencyId;
        po.ExchangeRate   = rate.Rate;
        po.BaseCurrencyId = rate.BaseCurrencyId;
        po.RateLockedAt   = Stamp(lockedAt);
        foreach (var line in po.Lines)
        {
            line.UnitPriceBase = rate.ToBase(line.UnitPrice);
            line.LineTotalBase = rate.ToBase(line.LineTotal);
        }
        po.TotalAmountBase = rate.SameCurrency ? po.TotalAmount : po.Lines.Sum(l => l.LineTotalBase ?? 0m);
    }

    /// <summary>The date a lock is taken on: today (UTC), date-only.</summary>
    public static DateOnly Today(DateTime utcNow) => DateOnly.FromDateTime(utcNow);
}
