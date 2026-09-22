using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

/// <summary>The read side of the product ledger (A29 §11.4). Writing is <see cref="ProductLedgerService"/>.</summary>
internal sealed class ProductLedgerQueryService : IProductLedgerQueryService
{
    /// <summary>
    /// An invoice whose sale stands: issued and not since cancelled or credited. A draft has sold nothing,
    /// and a cancelled or credit-noted invoice has been undone.
    /// </summary>
    private static readonly string[] IssuedStatuses =
    [
        SalesInvoiceStatuses.Issued, SalesInvoiceStatuses.PartiallyPaid, SalesInvoiceStatuses.Paid, SalesInvoiceStatuses.Overdue
    ];

    private readonly FinanceDbContext        _db;
    private readonly IProductVariantResolver _variants;

    public ProductLedgerQueryService(FinanceDbContext db, IProductVariantResolver variants)
    {
        _db       = db;
        _variants = variants;
    }

    // ── One variant's ledger ─────────────────────────────────────────────────

    public async Task<PaginatedResponse<ProductLedgerEntryModel>> GetLedgerAsync(Guid variantUuid, ProductLedgerFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "product ledger");
        var entryType = Normalize(filter.EntryType, ProductLedgerEntryTypes.All, "an entry type");
        var direction = Normalize(filter.Direction, ProductLedgerDirections.All, "a direction");

        var query = _db.ProductLedgerEntries.AsNoTracking().Where(e => e.VariantUuid == variantUuid);

        if (start is { } from)       query = query.Where(e => e.EntryDate >= from);
        if (endExclusive is { } end) query = query.Where(e => e.EntryDate < end);
        if (entryType is not null)   query = query.Where(e => e.EntryType == entryType);
        if (direction is not null)   query = query.Where(e => e.Direction == direction);

        var total = await query.CountAsync();
        var (page, pageSize) = PagedResults.Clamp(filter.Page, filter.PageSize);

        var entries = await query
            .OrderByDescending(e => e.SequenceNo)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return PagedResults.Of([.. entries.Select(ToModel)], total, page, pageSize);
    }

    // ── Where it stands ──────────────────────────────────────────────────────

    public async Task<ProductLedgerSummaryModel> GetSummaryAsync(Guid variantUuid)
    {
        var entries = _db.ProductLedgerEntries.AsNoTracking().Where(e => e.VariantUuid == variantUuid);

        var last = await entries.OrderByDescending(e => e.SequenceNo).FirstOrDefaultAsync();
        if (last is null) return new ProductLedgerSummaryModel { VariantUuid = variantUuid };

        var totals = await entries
            .GroupBy(e => e.EntryType)
            .Select(g => new { EntryType = g.Key, Quantity = g.Sum(x => x.Quantity), Cost = g.Sum(x => x.TotalCost) })
            .ToListAsync();

        var purchased = totals.FirstOrDefault(t => t.EntryType == ProductLedgerEntryTypes.Purchase);
        var sold      = totals.FirstOrDefault(t => t.EntryType == ProductLedgerEntryTypes.Sale);

        return new ProductLedgerSummaryModel
        {
            VariantUuid         = variantUuid,
            ProductUuid         = last.ProductUuid,
            PurchasedQuantity   = purchased?.Quantity ?? 0m,
            PurchasedCost       = purchased?.Cost ?? 0m,
            SoldQuantity        = sold?.Quantity ?? 0m,
            CostOfGoodsSold     = sold?.Cost ?? 0m,
            CurrentQuantity     = last.RunningQty,
            StockValue          = last.RunningValue,
            WeightedAverageCost = WeightedAverageCosting.Wac(last.RunningQty, last.RunningValue),
            EntryCount          = await entries.CountAsync(),
            LastEntryDate       = last.EntryDate
        };
    }

    // ── Revenue against cost ─────────────────────────────────────────────────

    public async Task<PaginatedResponse<ProductProfitabilityItemModel>> GetProfitabilityAsync(ProductProfitabilityFilter filter)
    {
        var rows = await RankedProfitabilityAsync(filter);

        var (page, pageSize) = PagedResults.Clamp(filter.Page, filter.PageSize);
        var onPage = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return PagedResults.Of(await NameAsync(onPage), rows.Count, page, pageSize);
    }

    public async Task<IReadOnlyList<ProductProfitabilityItemModel>> GetProfitabilityRowsAsync(ProductProfitabilityFilter filter) =>
        await NameAsync(await RankedProfitabilityAsync(filter));

    /// <summary>One product in one currency, before its name is looked up.</summary>
    private sealed record ProfitRow(Guid ProductUuid, string CurrencyCode, Guid Variant, decimal Quantity, decimal Revenue, decimal Cost, decimal Profit);

    /// <summary>Every product's revenue against cost, most profitable first: the whole ranking, whatever the filter's paging says.</summary>
    private async Task<List<ProfitRow>> RankedProfitabilityAsync(ProductProfitabilityFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "product profitability report");

        var sales = _db.ProductLedgerEntries.AsNoTracking()
            .Where(e => e.EntryType == ProductLedgerEntryTypes.Sale && e.ReferenceType == SalesInvoiceReferenceType);

        if (start is { } from)       sales = sales.Where(e => e.EntryDate >= from);
        if (endExclusive is { } end) sales = sales.Where(e => e.EntryDate < end);

        var liveInvoices = _db.SalesInvoices.AsNoTracking().Where(i => !i.IsDelete && IssuedStatuses.Contains(i.Status));

        // What the goods cost: the SALE entries of issued invoices, per variant and invoice currency.
        var cost = await (
            from e in sales
            join i in liveInvoices on e.ReferenceId equals i.UUID
            group e by new { e.ProductUuid, e.VariantUuid, i.CurrencyCode } into g
            select new
            {
                g.Key.ProductUuid, g.Key.VariantUuid, g.Key.CurrencyCode,
                Quantity = g.Sum(x => x.Quantity),
                Cost     = g.Sum(x => x.TotalCost)
            }).ToListAsync();

        // What was invoiced for them: the lines of exactly those sales — an invoice-and-variant that has a
        // SALE entry in the period — per variant and currency, so the two sides can never be about
        // different sales. Each line is counted once however many entries it has.
        var revenue = await (
            from l in _db.SalesInvoiceLines.AsNoTracking()
            where !l.SalesInvoice.IsDelete
               && IssuedStatuses.Contains(l.SalesInvoice.Status)
               && sales.Any(e => e.ReferenceId == l.SalesInvoice.UUID && e.VariantUuid == l.VariantUuid)
            group l by new { l.VariantUuid, l.SalesInvoice.CurrencyCode } into g
            select new
            {
                g.Key.VariantUuid, g.Key.CurrencyCode,
                Gross    = g.Sum(x => x.Quantity * x.UnitPrice),
                Discount = g.Sum(x => x.Quantity * x.UnitPrice * x.DiscountPercent / 100m)
            }).ToListAsync();

        var revenueOf = revenue.ToLookup(r => (r.VariantUuid, r.CurrencyCode));

        var rows = cost
            .GroupBy(c => (c.ProductUuid, c.CurrencyCode))
            .Select(g =>
            {
                var invoiced = g.SelectMany(c => revenueOf[(c.VariantUuid, c.CurrencyCode)]).ToList();

                // Rounded once, on the product's total, the way an invoice rounds its own header:
                // revenue is the gross less the discount, each to the cent.
                var revenueTotal = WeightedAverageCosting.Money(invoiced.Sum(r => r.Gross))
                                 - WeightedAverageCosting.Money(invoiced.Sum(r => r.Discount));
                var costTotal    = g.Sum(c => c.Cost);
                var profit       = revenueTotal - costTotal;

                return new ProfitRow(
                    g.Key.ProductUuid,
                    g.Key.CurrencyCode,
                    Variant:  g.Select(c => c.VariantUuid).Min(),
                    Quantity: g.Sum(c => c.Quantity),
                    Revenue:  revenueTotal,
                    Cost:     costTotal,
                    Profit:   profit);
            })
            .OrderByDescending(r => r.Profit).ThenBy(r => r.ProductUuid).ThenBy(r => r.CurrencyCode)
            .ToList();

        return rows;
    }

    /// <summary>Puts the product names on the rows. Product names live in Inventory; one variant of each product is enough to ask.</summary>
    private async Task<List<ProductProfitabilityItemModel>> NameAsync(IReadOnlyList<ProfitRow> rows)
    {
        var described = rows.Count == 0
            ? new Dictionary<Guid, VariantDescription>()
            : new Dictionary<Guid, VariantDescription>(await _variants.DescribeVariantsAsync([.. rows.Select(r => r.Variant).Distinct()]));

        return [.. rows.Select(r => new ProductProfitabilityItemModel
        {
            ProductUuid     = r.ProductUuid,
            ProductName     = described.GetValueOrDefault(r.Variant)?.ProductName,
            CurrencyCode    = r.CurrencyCode,
            QuantitySold    = r.Quantity,
            Revenue         = r.Revenue,
            CostOfGoodsSold = r.Cost,
            GrossProfit     = r.Profit,
            MarginPercent   = r.Revenue == 0m ? null : Math.Round(r.Profit / r.Revenue * 100m, 2, MidpointRounding.AwayFromZero)
        })];
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private const string SalesInvoiceReferenceType = "SalesInvoice";

    /// <summary>A filter word in the ledger's own upper case, or a refusal that lists what is valid.</summary>
    private static string? Normalize(string? value, IReadOnlyList<string> valid, string what)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized)) return null;

        return valid.Contains(normalized)
            ? normalized
            : throw new BadRequestException($"'{value}' is not {what}. Valid: {string.Join(", ", valid)}.");
    }

    private static ProductLedgerEntryModel ToModel(ProductLedgerEntry e) => new()
    {
        Uuid                = e.UUID,
        VariantUuid         = e.VariantUuid,
        ProductUuid         = e.ProductUuid,
        SequenceNo          = e.SequenceNo,
        EntryDate           = e.EntryDate,
        EntryType           = e.EntryType,
        ReferenceType       = e.ReferenceType,
        ReferenceId         = e.ReferenceId,
        ReferenceNumber     = e.ReferenceNumber,
        PartnerId           = e.PartnerId,
        Direction           = e.Direction,
        Quantity            = e.Quantity,
        UnitCost            = e.UnitCost,
        TotalCost           = e.TotalCost,
        RunningQty          = e.RunningQty,
        RunningValue        = e.RunningValue,
        WeightedAverageCost = WeightedAverageCosting.Wac(e.RunningQty, e.RunningValue),
        Narration           = e.Narration,
        CreatedBy           = e.CreatedBy,
        CreatedDate         = e.CreatedDate
    };
}
