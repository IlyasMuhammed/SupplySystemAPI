using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Reports.Services;

/// <summary>
/// R7 of A29 §15 (§14.2): the margin a sale order is expected to make — <c>selling price − purchase price</c> —
/// worked from the sale order lines and the purchase orders raised for them, so it can be read before anything is
/// invoiced. It is the figure each line stores as <c>Margin</c>, taken from the same purchase orders, with two
/// differences that make it addable across lines: it is in money rather than per unit, and the selling price is
/// what the line sells for after its discount, so a discounted line shows the margin it will really make.
/// <para>
/// Only the units bought in are analysed. A line filled from stock has no purchase order and so no cost here;
/// they are counted, not costed. A line that is part stock and part bought is analysed on the bought part, and
/// one bought in a larger quantity than it was ordered on the quantity it was ordered — the rest went to stock.
/// </para>
/// </summary>
internal sealed class MarginAnalysisReportService : IMarginAnalysisReportService
{
    /// <summary>The most products, customers or orders one report carries.</summary>
    internal const int MaxRows = 10_000;

    // The two states in which a purchase order is not being bought against: the same pair Demand's own margin
    // recomputation ignores, so a line's figure here is the one it stores.
    private static readonly string[] DeadPurchaseStatuses = ["CANCELLED", "REJECTED"];

    private readonly DemandDbContext            _demand;
    private readonly ISupplierNameLookupService _names;
    private readonly ILookupsService            _lookups;
    private readonly IProductVariantResolver    _variants;
    private readonly IPoDocumentTemplateService _templates;
    private readonly TimeProvider               _clock;

    public MarginAnalysisReportService(
        DemandDbContext demand, ISupplierNameLookupService names, ILookupsService lookups,
        IProductVariantResolver variants, IPoDocumentTemplateService templates, TimeProvider? clock = null)
    {
        _demand    = demand;
        _names     = names;
        _lookups   = lookups;
        _variants  = variants;
        _templates = templates;
        _clock     = clock ?? TimeProvider.System;
    }

    public Task<MarginAnalysisReport> GetMarginAnalysisAsync(MarginAnalysisFilter filter) =>
        BuildAsync(filter, forExport: false);

    public Task<MarginAnalysisReport> GetMarginAnalysisForExportAsync(MarginAnalysisFilter filter) =>
        BuildAsync(filter, forExport: true);

    private async Task<MarginAnalysisReport> BuildAsync(MarginAnalysisFilter filter, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var grouping = NormalizeGrouping(filter.GroupBy);
        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "margin analysis report");

        // An order counts once somebody has decided how to fill it: not a draft, and not one that was called off.
        var draft     = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft);
        var cancelled = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Cancelled);
        var orders    = _demand.SaleOrders.AsNoTracking().Where(o => !o.IsDeleted && o.Status != draft && o.Status != cancelled);
        if (start is { } from)       orders = orders.Where(o => o.OrderDate >= from);
        if (endExclusive is { } end) orders = orders.Where(o => o.OrderDate < end);

        var cancelledLine = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);
        var dead          = DeadPurchaseStatuses;

        // Each line with what was bought for it: the live purchase orders raised against it, for its own variant.
        var lines = await (
            from l in _demand.SaleOrderLines.AsNoTracking()
            join o in orders on l.SaleOrderId equals o.Id
            where l.Status != cancelledLine
            select new
            {
                OrderUuid = o.UUID, o.SoNumber, o.PartnerId, o.CurrencyId,
                l.VariantUuid, l.Quantity, l.UnitPrice, l.DiscountPercent,
                BoughtQty = _demand.PurchaseOrderLines
                    .Where(p => p.PurchaseOrder.LinkedSoLineId == l.Id && p.VariantUuid == l.VariantUuid
                             && !p.PurchaseOrder.IsDelete && !dead.Contains(p.PurchaseOrder.Status))
                    .Sum(p => (decimal?)p.Quantity) ?? 0m,
                BoughtCost = _demand.PurchaseOrderLines
                    .Where(p => p.PurchaseOrder.LinkedSoLineId == l.Id && p.VariantUuid == l.VariantUuid
                             && !p.PurchaseOrder.IsDelete && !dead.Contains(p.PurchaseOrder.Status))
                    .Sum(p => (decimal?)(p.Quantity * p.UnitPrice)) ?? 0m
            }).ToListAsync();

        var currencies = _lookups.GetCurrencies()
            .Where(c => !string.IsNullOrWhiteSpace(c.Code))
            .GroupBy(c => c.Id)
            .ToDictionary(g => g.Key, g => g.First().Code!.Trim());
        string CodeOf(Guid currencyId) => currencies.GetValueOrDefault(currencyId) ?? "?";

        // The units both sold and bought in, with what they sell for and what they cost. The average purchase
        // price is over everything bought for the line, and applies to the units that go to this order.
        var costed = lines
            .Where(l => l.BoughtQty > 0m)
            .Select(l =>
            {
                var units = Math.Min(l.BoughtQty, l.Quantity);
                return new
                {
                    l.OrderUuid, l.SoNumber, l.PartnerId, Currency = CodeOf(l.CurrencyId), l.VariantUuid,
                    Units   = units,
                    Selling = units * l.UnitPrice * (1m - l.DiscountPercent / 100m),
                    Cost    = l.BoughtCost / l.BoughtQty * units
                };
            })
            .ToList();

        // A variant's product is what Inventory says it is. One it cannot name stands as its own product.
        var described = new Dictionary<Guid, VariantDescription>();
        if (grouping == MarginGroupings.Product && costed.Count > 0)
            described = new Dictionary<Guid, VariantDescription>(
                await _variants.DescribeVariantsAsync([.. costed.Select(c => c.VariantUuid).Distinct()]));

        Guid KeyOf(Guid orderUuid, Guid partnerId, Guid variantUuid) => grouping switch
        {
            MarginGroupings.Customer => partnerId,
            MarginGroupings.Order    => orderUuid,
            _                        => described.TryGetValue(variantUuid, out var v) ? v.ProductUuid : variantUuid
        };

        var rows = costed
            .GroupBy(c => (Key: KeyOf(c.OrderUuid, c.PartnerId, c.VariantUuid), c.Currency))
            .Select(g =>
            {
                // Rounded once, on the row's total, the way an invoice rounds its own header; the margin is what
                // is left of the two rounded figures, so a row always adds up.
                var selling = Cents(g.Sum(c => c.Selling));
                var cost    = Cents(g.Sum(c => c.Cost));
                var units   = g.Sum(c => c.Units);
                return new
                {
                    g.Key.Key, g.Key.Currency,
                    Order    = g.First(),
                    LineCount = g.Count(),
                    Units    = units,
                    Selling  = selling,
                    Cost     = cost,
                    Margin   = selling - cost
                };
            })
            .OrderByDescending(r => r.Margin).ThenBy(r => r.Key).ThenBy(r => r.Currency, StringComparer.Ordinal)
            .ToList();

        if (rows.Count > MaxRows)
            throw new BadRequestException(
                $"{rows.Count} rows would be listed, more than the {MaxRows} one report carries. Narrow the date range or group by something coarser.");

        var (page, pageSize) = forExport
            ? (1, Math.Max(1, rows.Count))
            : PagedResults.Clamp(filter.Page, filter.PageSize);
        var onPage = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // Names, for the page only: customers by id, products from what Inventory described, orders by their own number.
        var partnerIds = grouping switch
        {
            MarginGroupings.Customer => onPage.Select(r => r.Key),
            MarginGroupings.Order    => onPage.Select(r => r.Order.PartnerId),
            _                        => []
        };
        var partnerIdList = partnerIds.Distinct().ToList();
        var customerNames = partnerIdList.Count == 0
            ? new Dictionary<Guid, string>()
            : new Dictionary<Guid, string>(await _names.GetNamesAsync(partnerIdList));

        var productNames = described.Values
            .GroupBy(v => v.ProductUuid)
            .ToDictionary(g => g.Key, g => g.First().ProductName);

        var uncosted = lines.Where(l => l.BoughtQty <= 0m).GroupBy(l => CodeOf(l.CurrencyId)).ToDictionary(g => g.Key, g => g.Count());

        // A currency with only uncosted lines has no rows but is still worth a total: it says what was left out.
        var currenciesInReport = rows.Select(r => r.Currency).Union(uncosted.Keys).OrderBy(c => c, StringComparer.Ordinal).ToList();

        return new MarginAnalysisReport
        {
            CompanyName = await CompanyNameAsync(),
            GeneratedAt = _clock.GetUtcNow().UtcDateTime,
            Criteria    = new MarginAnalysisCriteria { DateFrom = start, DateTo = filter.DateTo?.Date, GroupBy = grouping },
            Totals = [.. currenciesInReport
                .Select(currency =>
                {
                    var mine    = rows.Where(r => r.Currency == currency).ToList();
                    var selling = mine.Sum(r => r.Selling);
                    var margin  = mine.Sum(r => r.Margin);
                    return new MarginAnalysisTotal
                    {
                        CurrencyCode      = currency,
                        GroupCount        = mine.Count,
                        LineCount         = mine.Sum(r => r.LineCount),
                        UncostedLineCount = uncosted.GetValueOrDefault(currency),
                        SellingValue      = selling,
                        Cost              = mine.Sum(r => r.Cost),
                        Margin            = margin,
                        MarginPercent     = Percent(margin, selling)
                    };
                })],
            Items = [.. onPage.Select(r => new MarginAnalysisItem
            {
                GroupId      = r.Key,
                Name         = grouping switch
                {
                    MarginGroupings.Customer => customerNames.GetValueOrDefault(r.Key),
                    MarginGroupings.Order    => r.Order.SoNumber,
                    _                        => productNames.GetValueOrDefault(r.Key)
                },
                Detail       = grouping == MarginGroupings.Order ? customerNames.GetValueOrDefault(r.Order.PartnerId) : null,
                CurrencyCode = r.Currency,
                LineCount    = r.LineCount,
                Quantity     = grouping == MarginGroupings.Product ? r.Units : null,
                SellingValue = r.Selling,
                Cost         = r.Cost,
                Margin       = r.Margin,
                MarginPercent = Percent(r.Margin, r.Selling),
                AverageSellingPrice = grouping == MarginGroupings.Product && r.Units > 0m ? Cents(r.Selling / r.Units) : null,
                AverageCost         = grouping == MarginGroupings.Product && r.Units > 0m ? Cents(r.Cost / r.Units) : null
            })],
            TotalRecords = rows.Count,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(rows.Count / (double)pageSize)
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string NormalizeGrouping(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized)) return MarginGroupings.Product;

        return MarginGroupings.All.Contains(normalized)
            ? normalized
            : throw new BadRequestException($"'{value}' is not something to group by. Valid: {string.Join(", ", MarginGroupings.All)}.");
    }

    private static decimal Cents(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal? Percent(decimal margin, decimal selling) =>
        selling > 0m ? Cents(margin / selling * 100m) : null;

    private async Task<string?> CompanyNameAsync()
    {
        var template = await _templates.GetActiveAsync();
        return string.IsNullOrWhiteSpace(template?.CompanyName) ? null : template.CompanyName.Trim();
    }
}
