using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Reports.Services;

/// <summary>
/// R9 and R10 of A29 §15. R9 reads the product ledger straight from Finance's context, as the other reports
/// read their modules', and asks Suppliers and Inventory only for what a row stores by id: the partner's name and
/// the variants' and product's. R10 does not work out revenue and cost itself: it takes the ranking A29-P8-05's
/// profitability endpoint already produces, so the report and the endpoint cannot disagree, and adds the rank, the
/// totals and the documents.
/// <para>
/// R9's running figures are the <b>product's</b>, over all its variants, worked from the movements themselves: a
/// movement in adds its quantity and its value and a movement out takes them off, from where the product stood
/// when the range began. That is how the ledger keeps each variant's own running totals, so at the end of the
/// whole history the two agree, and the variant's own figures are on every row beside the product's.
/// </para>
/// </summary>
internal sealed class ProductLedgerReportService : IProductLedgerReportService
{
    /// <summary>The most movements or products one report carries.</summary>
    internal const int MaxRows = 10_000;

    private readonly FinanceDbContext            _finance;
    private readonly ISupplierNameLookupService  _names;
    private readonly IProductVariantResolver     _variants;
    private readonly IProductLedgerQueryService  _profitability;
    private readonly IPoDocumentTemplateService  _templates;
    private readonly TimeProvider                _clock;

    public ProductLedgerReportService(
        FinanceDbContext finance, ISupplierNameLookupService names, IProductVariantResolver variants,
        IProductLedgerQueryService profitability, IPoDocumentTemplateService templates, TimeProvider? clock = null)
    {
        _finance       = finance;
        _names         = names;
        _variants      = variants;
        _profitability = profitability;
        _templates     = templates;
        _clock         = clock ?? TimeProvider.System;
    }

    public Task<ProductLedgerReport> GetProductLedgerAsync(ProductLedgerReportFilter filter) =>
        BuildLedgerAsync(filter, forExport: false);

    public Task<ProductLedgerReport> GetProductLedgerForExportAsync(ProductLedgerReportFilter filter) =>
        BuildLedgerAsync(filter, forExport: true);

    public Task<ProfitabilityReport> GetProductProfitabilityAsync(ProfitabilityReportFilter filter) =>
        BuildProfitabilityAsync(filter, forExport: false);

    public Task<ProfitabilityReport> GetProductProfitabilityForExportAsync(ProfitabilityReportFilter filter) =>
        BuildProfitabilityAsync(filter, forExport: true);

    // ── R9 Product ledger ────────────────────────────────────────────────────

    private async Task<ProductLedgerReport> BuildLedgerAsync(ProductLedgerReportFilter filter, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(filter);

        if (filter.ProductId is not { } product || product == Guid.Empty)
            throw new BadRequestException("Name the product: a product ledger is one product's account.");
        if (filter.VariantId == Guid.Empty)
            throw new BadRequestException("The variant, when given, must be a variant's UUID.");

        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "product ledger");

        var ofProduct = _finance.ProductLedgerEntries.AsNoTracking().Where(e => e.ProductUuid == product);
        var account   = filter.VariantId is { } only ? ofProduct.Where(e => e.VariantUuid == only) : ofProduct;

        // Who the product and its variants are, before anything is read that could be large.
        var variantIds = await ofProduct.Select(e => e.VariantUuid).Distinct().ToListAsync();
        var toDescribe = new List<Guid>(variantIds);
        if (filter.VariantId is { } named && !toDescribe.Contains(named)) toDescribe.Add(named);
        if (variantIds.Count == 0)
        {
            // A product with nothing on the ledger yet is still a product, if Inventory has it.
            var defaults = await _variants.ResolveDefaultVariantsAsync([product]);
            if (defaults.TryGetValue(product, out var standIn)) toDescribe.Add(standIn.VariantUuid);
        }

        var described = toDescribe.Count == 0
            ? new Dictionary<Guid, VariantDescription>()
            : new Dictionary<Guid, VariantDescription>(await _variants.DescribeVariantsAsync(toDescribe));

        var productName = described.Values.Where(v => v.ProductUuid == product).Select(v => v.ProductName).FirstOrDefault();
        if (variantIds.Count == 0 && productName is null)
            throw new NotFoundException("Product", product);

        var inRange = account;
        if (start is { } from)       inRange = inRange.Where(e => e.EntryDate >= from);
        if (endExclusive is { } end) inRange = inRange.Where(e => e.EntryDate < end);

        var total = await inRange.CountAsync();
        if (total > MaxRows)
            throw new BadRequestException(
                $"{total} movements fall in that range, more than the {MaxRows} one report carries. Narrow the date range or name one variant.");

        // What the product held when the range began: everything dated before it, whenever it was posted.
        decimal openingQty = 0m, openingValue = 0m;
        if (start is { } first)
        {
            var before = await account.Where(e => e.EntryDate < first)
                .GroupBy(e => e.Direction)
                .Select(g => new { Direction = g.Key, Quantity = g.Sum(e => e.Quantity), Value = g.Sum(e => e.TotalCost) })
                .ToListAsync();

            openingQty   = before.Sum(b => b.Direction == ProductLedgerDirections.In ? b.Quantity : -b.Quantity);
            openingValue = before.Sum(b => b.Direction == ProductLedgerDirections.In ? b.Value : -b.Value);
        }

        var rows = await inRange
            .Select(e => new
            {
                e.UUID, e.VariantUuid, e.SequenceNo, e.EntryDate, e.CreatedDate, e.EntryType, e.ReferenceType, e.ReferenceId, e.ReferenceNumber,
                e.PartnerId, e.Direction, e.Quantity, e.UnitCost, e.TotalCost, e.RunningQty, e.RunningValue, e.Narration
            })
            .ToListAsync();

        // A ledger reads by business date, and by the order things were posted within a day, so a back-dated
        // movement lands where its goods moved. Each variant's own entries keep their posting order.
        var qty = openingQty;
        var value = openingValue;
        decimal quantityIn = 0m, valueIn = 0m, quantityOut = 0m, valueOut = 0m;
        var entries = new List<ProductLedgerReportEntry>(rows.Count);

        foreach (var r in rows.OrderBy(r => r.EntryDate.Date).ThenBy(r => r.CreatedDate).ThenBy(r => r.SequenceNo).ThenBy(r => r.VariantUuid))
        {
            if (r.Direction == ProductLedgerDirections.In) { qty += r.Quantity; value += r.TotalCost; quantityIn += r.Quantity; valueIn += r.TotalCost; }
            else                                            { qty -= r.Quantity; value -= r.TotalCost; quantityOut += r.Quantity; valueOut += r.TotalCost; }

            var variant = described.GetValueOrDefault(r.VariantUuid);
            entries.Add(new ProductLedgerReportEntry
            {
                EntryUuid           = r.UUID,
                VariantUuid         = r.VariantUuid,
                Sku                 = variant?.Sku,
                VariantName         = variant?.VariantName,
                EntryDate           = r.EntryDate,
                EntryType           = r.EntryType,
                ReferenceType       = r.ReferenceType,
                ReferenceId         = r.ReferenceId,
                ReferenceNumber     = r.ReferenceNumber,
                PartnerId           = r.PartnerId,
                Direction           = r.Direction,
                Quantity            = r.Quantity,
                UnitCost            = r.UnitCost,
                TotalCost           = r.TotalCost,
                RunningQty          = qty,
                RunningValue        = value,
                WeightedAverageCost = WeightedAverageCosting.Wac(qty, value),
                VariantRunningQty   = r.RunningQty,
                VariantRunningValue = r.RunningValue,
                Narration           = r.Narration
            });
        }

        var (page, pageSize) = forExport
            ? (1, Math.Max(1, entries.Count))
            : PagedResults.Clamp(filter.Page, filter.PageSize);
        var onPage = entries.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // Partners are named for the page only.
        var partnerIds = onPage.Where(e => e.PartnerId is not null).Select(e => e.PartnerId!.Value).Distinct().ToList();
        var partnerNames = partnerIds.Count == 0
            ? new Dictionary<Guid, string>()
            : new Dictionary<Guid, string>(await _names.GetNamesAsync(partnerIds));
        foreach (var e in onPage.Where(e => e.PartnerId is not null))
            e.PartnerName = partnerNames.GetValueOrDefault(e.PartnerId!.Value);

        return new ProductLedgerReport
        {
            CompanyName = await CompanyNameAsync(),
            GeneratedAt = _clock.GetUtcNow().UtcDateTime,
            Criteria    = new ProductLedgerReportCriteria
            {
                ProductUuid = product,
                ProductName = productName,
                VariantUuid = filter.VariantId,
                VariantName = filter.VariantId is { } v ? described.GetValueOrDefault(v)?.VariantName : null,
                DateFrom    = start,
                DateTo      = filter.DateTo?.Date
            },
            Summary = new ProductLedgerReportSummary
            {
                OpeningQuantity            = openingQty,
                OpeningValue               = openingValue,
                QuantityIn                 = quantityIn,
                ValueIn                    = valueIn,
                QuantityOut                = quantityOut,
                ValueOut                   = valueOut,
                ClosingQuantity            = qty,
                ClosingValue               = value,
                ClosingWeightedAverageCost = WeightedAverageCosting.Wac(qty, value),
                MovementCount              = entries.Count
            },
            Items        = onPage,
            TotalRecords = entries.Count,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(entries.Count / (double)pageSize)
        };
    }

    // ── R10 Product profitability ────────────────────────────────────────────

    private async Task<ProfitabilityReport> BuildProfitabilityAsync(ProfitabilityReportFilter filter, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(filter);

        // The ranking is A29-P8-05's own, whole; it refuses a range that ends before it starts.
        var ranked = await _profitability.GetProfitabilityRowsAsync(new ProductProfitabilityFilter { DateFrom = filter.DateFrom, DateTo = filter.DateTo });

        if (ranked.Count > MaxRows)
            throw new BadRequestException(
                $"{ranked.Count} products were sold, more than the {MaxRows} one report carries. Narrow the date range.");

        // The list is most profitable first across every currency; each currency is ranked on its own.
        var placed = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = ranked.Select(r =>
        {
            var rank = placed[r.CurrencyCode] = placed.GetValueOrDefault(r.CurrencyCode) + 1;
            return new ProfitabilityReportItem
            {
                Rank            = rank,
                ProductUuid     = r.ProductUuid,
                ProductName     = r.ProductName,
                CurrencyCode    = r.CurrencyCode,
                QuantitySold    = r.QuantitySold,
                Revenue         = r.Revenue,
                CostOfGoodsSold = r.CostOfGoodsSold,
                GrossProfit     = r.GrossProfit,
                MarginPercent   = r.MarginPercent
            };
        }).ToList();

        var (page, pageSize) = forExport
            ? (1, Math.Max(1, rows.Count))
            : PagedResults.Clamp(filter.Page, filter.PageSize);

        return new ProfitabilityReport
        {
            CompanyName = await CompanyNameAsync(),
            GeneratedAt = _clock.GetUtcNow().UtcDateTime,
            Criteria    = new ProfitabilityReportCriteria { DateFrom = filter.DateFrom?.Date, DateTo = filter.DateTo?.Date },
            Totals = [.. rows
                .GroupBy(r => r.CurrencyCode)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var revenue = g.Sum(r => r.Revenue);
                    var profit  = g.Sum(r => r.GrossProfit);
                    return new ProfitabilityReportTotal
                    {
                        CurrencyCode    = g.Key,
                        ProductCount    = g.Count(),
                        Revenue         = revenue,
                        CostOfGoodsSold = g.Sum(r => r.CostOfGoodsSold),
                        GrossProfit     = profit,
                        MarginPercent   = revenue == 0m ? null : Math.Round(profit / revenue * 100m, 2, MidpointRounding.AwayFromZero)
                    };
                })],
            Items        = [.. rows.Skip((page - 1) * pageSize).Take(pageSize)],
            TotalRecords = rows.Count,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(rows.Count / (double)pageSize)
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string?> CompanyNameAsync()
    {
        var template = await _templates.GetActiveAsync();
        return string.IsNullOrWhiteSpace(template?.CompanyName) ? null : template.CompanyName.Trim();
    }
}
