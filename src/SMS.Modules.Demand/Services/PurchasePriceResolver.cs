using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A45 C3 — resolves a purchase order line's unit price from the supplier pricelists. Read-only (BR-19): no locks, no writes,
/// so any number of buyers resolve concurrently.
/// </summary>
public interface IPurchasePriceResolver
{
    Task<PriceResolutionResult> ResolveAsync(PriceResolutionRequest request, CancellationToken ct = default);
}

/// <summary>
/// Candidate rows: active, this organization, this supplier, <c>MinQuantity &lt;= quantity</c>, and for this variant or — when
/// the row names no variant — this variant's product (D-3). A row is only valid when its own date basis puts the order date or
/// the delivery date (falling back to the order date, BR-03) inside [ValidFrom, ValidTo]. The first valid row in this order
/// wins (D-15, BR-01): Priority ascending; a variant-specific row before a product-level one; MinQuantity descending; then the
/// row's id.
/// <para>
/// A46 C10 / E-9 — currency is not part of matching: the winning row is converted into the purchase order's currency at the
/// effective date through <see cref="ICurrencyConverter"/> (the PO currency, null = the organization's purchase base). No rate
/// is found:true, unitPrice:null, currencyMismatchWarning:true (BR-33). The converter is optional like every cross-module
/// service in Demand: without it the A45 same-currency rule applies (a row in another currency does not match).
/// </para>
/// </summary>
internal sealed class PurchasePriceResolver : IPurchasePriceResolver
{
    /// <summary>Converted prices are stored at 4dp (E-6).</summary>
    public const int PriceDecimals = 4;

    private readonly DemandDbContext _db;
    // Optional like every other cross-module service in Demand: module test hosts omit them.
    private readonly InventoryDbContext? _inv;
    private readonly IOrganizationCurrencyService? _orgCurrency;
    private readonly ICurrencyCodeLookup? _codes;
    private readonly ISupplierNameLookupService? _suppliers;
    private readonly ICurrencyConverter? _converter;

    public PurchasePriceResolver(DemandDbContext db, InventoryDbContext? inv = null, IOrganizationCurrencyService? orgCurrency = null,
        ICurrencyCodeLookup? codes = null, ISupplierNameLookupService? suppliers = null, ICurrencyConverter? converter = null)
    {
        _db          = db;
        _inv         = inv;
        _orgCurrency = orgCurrency;
        _codes       = codes;
        _suppliers   = suppliers;
        _converter   = converter;
    }

    private Guid Org => _db.TenantContext.OrganizationId;

    public async Task<PriceResolutionResult> ResolveAsync(PriceResolutionRequest request, CancellationToken ct = default)
    {
        var orderDate = request.OrderDate.Date;
        var delivery  = request.ExpectedDeliveryDate?.Date;

        // The variant is authoritative for its product; the request's productUuid only matters without Inventory.
        VariantInfo? variant = null;
        if (_inv is not null && request.VariantUuid != Guid.Empty)
            variant = await _inv.ProductVariants.AsNoTracking()
                .Where(v => v.Uuid == request.VariantUuid)
                .Select(v => new VariantInfo(v.Product.Uuid, v.Product.Name, v.VariantName, v.IsDefault))
                .FirstOrDefaultAsync(ct);
        var productUuid = variant?.ProductUuid ?? request.ProductUuid;

        var currencyId = request.EffectiveCurrencyId is { } c && c != Guid.Empty
            ? c
            : await DemandCurrency.BaseAsync(_orgCurrency, Org, TransactionDomain.Purchase);

        var query = _db.SupplierPricelists.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == Org && r.IsActive && r.SupplierUuid == request.SupplierUuid
                     && r.MinQuantity <= request.Quantity);
        query = productUuid is { } p
            ? query.Where(r => r.ProductUuid == p && (r.VariantUuid == request.VariantUuid || r.VariantUuid == null))
            : query.Where(r => r.VariantUuid == request.VariantUuid);

        // Projected to what is consumed so the covering IX_SupplierPricelists_Lookup answers it with no key lookups (BR-37, A46 C11).
        var candidates = (await query
                .Select(r => new Candidate(r.Id, r.UUID, r.VariantUuid, r.MinQuantity, r.Priority, r.PricingDateBasis, r.UnitPrice, r.CurrencyId,
                    r.LeadTimeDays, r.ValidFrom, r.ValidTo))
                .ToListAsync(ct))
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.VariantUuid == null ? 1 : 0)
            .ThenByDescending(r => r.MinQuantity)
            .ThenBy(r => r.Id)
            .ToList();

        Candidate? otherCurrency = null;
        foreach (var row in candidates)
        {
            var effective = row.PricingDateBasis == PricingDateBasis.DeliveryDate && delivery is not null ? delivery.Value : orderDate;
            if (effective < row.ValidFrom.Date || effective > row.ValidTo.Date) continue;

            var sameCurrency = currencyId is not { } wanted || row.CurrencyId == wanted;
            if (!sameCurrency && _converter is null)
            {
                // A45 fallback (no converter registered): another currency is not a match.
                otherCurrency ??= row;
                continue;
            }

            if (sameCurrency)
                return new PriceResolutionResult
                {
                    Found                 = true,
                    UnitPrice             = row.UnitPrice,
                    CurrencyId            = row.CurrencyId,
                    CurrencyCode          = await CodeAsync(row.CurrencyId, ct),
                    SupplierPricelistUuid = row.UUID,
                    DateBasisUsed         = row.PricingDateBasis,
                    EffectiveDateUsed     = effective,
                    LeadTimeDays          = row.LeadTimeDays
                };

            return await ConvertAsync(row, currencyId!.Value, effective, ct);
        }

        return new PriceResolutionResult
        {
            Found             = false,
            EffectiveDateUsed = orderDate,
            WarningMessage    = await NotFoundMessageAsync(variant, request, orderDate, otherCurrency, currencyId, ct)
        };
    }

    /// <summary>BR-33/35 — the matched row is in another currency than the PO's: convert at the effective date, or say why not.</summary>
    private async Task<PriceResolutionResult> ConvertAsync(Candidate row, Guid poCurrency, DateTime effective, CancellationToken ct)
    {
        var fromCode = await CodeAsync(row.CurrencyId, ct);
        var toCode   = await CodeAsync(poCurrency, ct);

        var result = new PriceResolutionResult
        {
            Found                 = true,
            SupplierPricelistUuid = row.UUID,
            DateBasisUsed         = row.PricingDateBasis,
            EffectiveDateUsed     = effective,
            LeadTimeDays          = row.LeadTimeDays,
            OriginalPrice         = row.UnitPrice,
            OriginalCurrencyId    = row.CurrencyId,
            OriginalCurrencyCode  = fromCode
        };

        CurrencyConvertResult? converted = null;
        try
        {
            converted = await _converter!.ConvertAsync(
                new CurrencyConvertRequest(row.UnitPrice, row.CurrencyId, null, poCurrency, null, effective, PriceDecimals), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A converter that cannot answer (organization without a currency setup, rate service down) is "no rate": the buyer
            // is told and prices the line by hand, the save is not lost.
        }

        if (converted is { Success: true })
        {
            result.UnitPrice          = converted.ConvertedAmount;
            result.CurrencyId         = poCurrency;
            result.CurrencyCode       = toCode ?? converted.ToCurrencyCode;
            result.OriginalCurrencyCode ??= converted.FromCurrencyCode;
            result.ExchangeRateUsed   = converted.RateUsed;
            result.ExchangeRateDate   = converted.RateDate.Date;
            result.CurrencyConverted  = true;
            return result;
        }

        var day = effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        result.UnitPrice               = null;
        result.CurrencyId              = poCurrency;
        result.CurrencyCode            = toCode;
        result.CurrencyMismatchWarning = true;
        result.WarningMessage =
            $"No exchange rate available for {fromCode ?? "the pricelist currency"} → {toCode ?? "the purchase order currency"} on {day} " +
            $"(price found: {row.UnitPrice.ToString("0.####", CultureInfo.InvariantCulture)} {fromCode}). Enter price manually.";
        return result;
    }

    private async Task<string?> CodeAsync(Guid currencyId, CancellationToken ct) =>
        _codes is null ? null : (await _codes.GetCodeAsync(currencyId, ct))?.Trim();

    private async Task<string> NotFoundMessageAsync(
        VariantInfo? variant, PriceResolutionRequest request, DateTime date, Candidate? otherCurrency, Guid? wanted, CancellationToken ct)
    {
        var product = variant is null ? "this product"
            : variant.IsDefault ? variant.ProductName
            : $"{variant.ProductName} - {variant.VariantName}";

        var supplier = "this supplier";
        if (_suppliers is not null && request.SupplierUuid != Guid.Empty)
        {
            var names = await _suppliers.GetNamesAsync([request.SupplierUuid]);
            if (names.TryGetValue(request.SupplierUuid, out var n) && !string.IsNullOrWhiteSpace(n)) supplier = n;
        }

        var day = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (otherCurrency is null)
            return $"No configured price found for {product} from {supplier} effective {day}. Enter price manually or create an RFQ.";

        var theirs = await CodeAsync(otherCurrency.CurrencyId, ct) ?? "another currency";
        var ours   = wanted is { } w ? await CodeAsync(w, ct) ?? "a different currency" : "a different currency";
        return $"A configured price for {product} from {supplier} effective {day} is in {theirs}, but this purchase order is in {ours}. " +
               "Enter price manually or create an RFQ.";
    }

    private sealed record Candidate(int Id, Guid UUID, Guid? VariantUuid, decimal MinQuantity, int Priority, PricingDateBasis PricingDateBasis,
        decimal UnitPrice, Guid CurrencyId, int? LeadTimeDays, DateTime ValidFrom, DateTime ValidTo);

    private sealed record VariantInfo(Guid ProductUuid, string ProductName, string VariantName, bool IsDefault);
}

