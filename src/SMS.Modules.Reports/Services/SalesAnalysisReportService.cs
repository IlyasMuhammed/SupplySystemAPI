using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Reports.Services;

/// <summary>
/// R4, R5 and R8 of A29 §15. Reads the sales invoices, and for R8 the product ledger's cost of sales, straight
/// from Finance's context, as the other reports read their modules', and asks Suppliers and Inventory only for
/// what a row stores by id: a customer's or a product's name.
/// <para>
/// A sale is an invoice that stands (issued and not since cancelled or credited), counted on the day its
/// ledger entry was posted — the day it was issued, not the day its draft was dated — and revenue is what
/// it billed before tax: the goods at their price, less the discount. The product profitability report
/// reads revenue the same way, so a product's revenue here is its revenue there.
/// </para>
/// <para>
/// R8 sets those same invoices against what issuing each of them booked as the cost of the goods: the SALE
/// entries the product ledger wrote, at the weighted-average cost of the day. Revenue and cost are bucketed by
/// the one invoice on the one day it was issued, so a period's cost is the cost of what its revenue sold. An
/// invoice with no cost of sales booked at all is still revenue, and is reported as such rather than hidden.
/// </para>
/// </summary>
internal sealed class SalesAnalysisReportService : ISalesAnalysisReportService
{
    /// <summary>The most products or customers one report carries.</summary>
    internal const int MaxRows = 10_000;

    private const string InvoiceReferenceType = "SalesInvoice";
    /// <summary>A38 (FIN) — the ledger reference of a customer credit note.</summary>
    private const string CreditNoteReferenceType = CounterSaleReferences.CreditNote;

    /// <summary>An invoice whose sale stands. A draft has sold nothing; a cancelled or credit-noted invoice has been undone.</summary>
    private static readonly string[] LiveStatuses =
    [
        SalesInvoiceStatuses.Issued, SalesInvoiceStatuses.PartiallyPaid, SalesInvoiceStatuses.Paid, SalesInvoiceStatuses.Overdue
    ];

    private readonly FinanceDbContext            _finance;
    private readonly ISupplierNameLookupService  _names;
    private readonly IProductVariantResolver     _variants;
    private readonly IPoDocumentTemplateService  _templates;
    private readonly TimeProvider                _clock;

    public SalesAnalysisReportService(
        FinanceDbContext finance, ISupplierNameLookupService names, IProductVariantResolver variants,
        IPoDocumentTemplateService templates, TimeProvider? clock = null)
    {
        _finance   = finance;
        _names     = names;
        _variants  = variants;
        _templates = templates;
        _clock     = clock ?? TimeProvider.System;
    }

    public Task<SalesByProductReport> GetSalesByProductAsync(SalesByProductFilter filter) =>
        BuildByProductAsync(filter, forExport: false);

    public Task<SalesByProductReport> GetSalesByProductForExportAsync(SalesByProductFilter filter) =>
        BuildByProductAsync(filter, forExport: true);

    public Task<SalesByCustomerReport> GetSalesByCustomerAsync(SalesByCustomerFilter filter) =>
        BuildByCustomerAsync(filter, forExport: false);

    public Task<SalesByCustomerReport> GetSalesByCustomerForExportAsync(SalesByCustomerFilter filter) =>
        BuildByCustomerAsync(filter, forExport: true);

    // ── R4 Sales by product ──────────────────────────────────────────────────

    private async Task<SalesByProductReport> BuildByProductAsync(SalesByProductFilter filter, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "sales by product report");

        var invoices = LiveInvoices(start, endExclusive);
        if (filter.PartnerId is { } only) invoices = invoices.Where(i => i.PartnerId == only);

        var credits = LiveCreditNotes(start, endExclusive);
        if (filter.PartnerId is { } onlyCredits) credits = credits.Where(i => i.PartnerId == onlyCredits);

        // What was invoiced, a variant and a currency at a time; the products come after. A38 (FIN): a POS line carries its
        // absolute tax (TaxAmount), so its revenue is its net (LineTotal − tax); credit notes booked in the range come off.
        var sold = await (
            from l in _finance.SalesInvoiceLines.AsNoTracking()
            where invoices.Any(i => i.Id == l.SalesInvoiceId)
            group l by new { l.VariantUuid, l.SalesInvoice.CurrencyCode } into g
            select new LineSum(
                g.Key.VariantUuid, g.Key.CurrencyCode,
                g.Sum(x => x.Quantity),
                g.Sum(x => x.TaxAmount == null ? x.Quantity * x.UnitPrice : 0m),
                g.Sum(x => x.TaxAmount == null ? x.Quantity * x.UnitPrice * x.DiscountPercent / 100m : 0m),
                g.Sum(x => x.TaxAmount != null ? x.LineTotal - (x.TaxAmount ?? 0m) : 0m))).ToListAsync();

        var credited = await (
            from l in _finance.SalesInvoiceLines.AsNoTracking()
            where credits.Any(i => i.Id == l.SalesInvoiceId)
            group l by new { l.VariantUuid, l.SalesInvoice.CurrencyCode } into g
            select new LineSum(
                g.Key.VariantUuid, g.Key.CurrencyCode,
                -g.Sum(x => x.Quantity), 0m, 0m,
                -g.Sum(x => x.LineTotal - (x.TaxAmount ?? 0m)))).ToListAsync();

        var lines = sold.Concat(credited).ToList();

        // A variant's product is the one the product ledger recorded it under when it was sold. A variant
        // the ledger never saw is its own product rather than dropped from the report.
        var variantIds = lines.Select(l => l.VariantUuid).Distinct().ToList();
        var productOf  = new Dictionary<Guid, Guid>();
        if (variantIds.Count > 0)
        {
            var mapped = await _finance.ProductLedgerEntries.AsNoTracking()
                .Where(e => variantIds.Contains(e.VariantUuid))
                .Select(e => new { e.VariantUuid, e.ProductUuid })
                .Distinct()
                .ToListAsync();

            foreach (var g in mapped.GroupBy(m => m.VariantUuid)) productOf[g.Key] = g.Min(m => m.ProductUuid);
        }

        var rows = lines
            .GroupBy(l => (Product: productOf.GetValueOrDefault(l.VariantUuid, l.VariantUuid), l.CurrencyCode))
            .Select(g => new
            {
                ProductUuid  = g.Key.Product,
                g.Key.CurrencyCode,
                Variant      = g.Min(l => l.VariantUuid),
                Quantity     = g.Sum(l => l.Quantity),
                // Rounded once, on the product's total, the way an invoice rounds its own header.
                Revenue      = Cents(g.Sum(l => l.Gross)) - Cents(g.Sum(l => l.Discount)) + g.Sum(l => l.Net)
            })
            .OrderByDescending(r => r.Revenue).ThenBy(r => r.ProductUuid).ThenBy(r => r.CurrencyCode, StringComparer.Ordinal)
            .ToList();

        if (rows.Count > MaxRows)
            throw new BadRequestException(
                $"{rows.Count} products were sold, more than the {MaxRows} one report carries. Narrow the date range or name a customer.");

        var (page, pageSize) = forExport
            ? (1, Math.Max(1, rows.Count))
            : PagedResults.Clamp(filter.Page, filter.PageSize);
        var onPage = rows.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        // Product names live in Inventory; one variant of each product on the page is enough to ask.
        var described = onPage.Count == 0
            ? new Dictionary<Guid, VariantDescription>()
            : new Dictionary<Guid, VariantDescription>(await _variants.DescribeVariantsAsync([.. onPage.Select(r => r.Variant).Distinct()]));

        var customerName = filter.PartnerId is { } named
            ? (await _names.GetNamesAsync([named])).GetValueOrDefault(named)
            : null;

        return new SalesByProductReport
        {
            CompanyName = await CompanyNameAsync(),
            GeneratedAt = _clock.GetUtcNow().UtcDateTime,
            Criteria    = new SalesByProductCriteria
            {
                DateFrom = start, DateTo = filter.DateTo?.Date, PartnerId = filter.PartnerId, CustomerName = customerName
            },
            Totals = [.. rows
                .GroupBy(r => r.CurrencyCode)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new SalesByProductTotal { CurrencyCode = g.Key, ProductCount = g.Count(), Revenue = g.Sum(r => r.Revenue) })],
            Items = [.. onPage.Select(r => new SalesByProductItem
            {
                ProductUuid      = r.ProductUuid,
                ProductName      = described.GetValueOrDefault(r.Variant)?.ProductName,
                CurrencyCode     = r.CurrencyCode,
                QuantitySold     = r.Quantity,
                Revenue          = r.Revenue,
                AverageUnitPrice = r.Quantity == 0m ? null : Cents(r.Revenue / r.Quantity)
            })],
            TotalRecords = rows.Count,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(rows.Count / (double)pageSize)
        };
    }

    // ── R5 Sales by customer ─────────────────────────────────────────────────

    private async Task<SalesByCustomerReport> BuildByCustomerAsync(SalesByCustomerFilter filter, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "sales by customer report");

        // Revenue is the sum of what each invoice printed before tax (its subtotal less its discount), so
        // a customer's figure is the figure on their invoices. An order billed in several invoices, one
        // to a delivery, is still one order.
        // A38 (FIN): a POS sale has no sale order — it is its own order. Credit notes booked in the range come off revenue.
        var billed = await LiveInvoices(start, endExclusive)
            .GroupBy(i => new { i.PartnerId, i.CurrencyCode })
            .Select(g => new
            {
                g.Key.PartnerId, g.Key.CurrencyCode,
                Orders   = g.Select(i => i.SaleOrderUuid ?? i.UUID).Distinct().Count(),
                Invoices = g.Count(),
                Revenue  = g.Sum(i => i.Subtotal - i.DiscountAmount)
            })
            .ToListAsync();

        var creditedBy = await LiveCreditNotes(start, endExclusive)
            .GroupBy(i => new { i.PartnerId, i.CurrencyCode })
            .Select(g => new { g.Key.PartnerId, g.Key.CurrencyCode, Revenue = g.Sum(i => i.Subtotal - i.DiscountAmount) })
            .ToListAsync();

        var sold = billed
            .Select(b => new { b.PartnerId, b.CurrencyCode, b.Orders, b.Invoices, b.Revenue })
            .Concat(creditedBy.Select(c => new { c.PartnerId, c.CurrencyCode, Orders = 0, Invoices = 0, Revenue = -c.Revenue }))
            .GroupBy(x => (x.PartnerId, x.CurrencyCode))
            .Select(g => new
            {
                g.Key.PartnerId, g.Key.CurrencyCode,
                Orders   = g.Sum(x => x.Orders),
                Invoices = g.Sum(x => x.Invoices),
                Revenue  = g.Sum(x => x.Revenue)
            })
            .ToList();

        if (sold.Count > MaxRows)
            throw new BadRequestException(
                $"{sold.Count} customers were billed, more than the {MaxRows} one report carries. Narrow the date range.");

        var names = sold.Count == 0
            ? new Dictionary<Guid, string>()
            : new Dictionary<Guid, string>(await _names.GetNamesAsync([.. sold.Select(s => s.PartnerId).Distinct()]));

        var rows = sold
            .Select(s => new SalesByCustomerItem
            {
                PartnerId         = s.PartnerId,
                CustomerName      = names.GetValueOrDefault(s.PartnerId),
                CurrencyCode      = s.CurrencyCode,
                OrderCount        = s.Orders,
                InvoiceCount      = s.Invoices,
                Revenue           = s.Revenue,
                AverageOrderValue = Average(s.Revenue, s.Orders)
            })
            .OrderByDescending(r => r.Revenue).ThenBy(r => r.PartnerId).ThenBy(r => r.CurrencyCode, StringComparer.Ordinal)
            .ToList();

        var (page, pageSize) = forExport
            ? (1, Math.Max(1, rows.Count))
            : PagedResults.Clamp(filter.Page, filter.PageSize);

        return new SalesByCustomerReport
        {
            CompanyName = await CompanyNameAsync(),
            GeneratedAt = _clock.GetUtcNow().UtcDateTime,
            Criteria    = new SalesByCustomerCriteria { DateFrom = start, DateTo = filter.DateTo?.Date },
            Totals = [.. rows
                .GroupBy(r => r.CurrencyCode)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new SalesByCustomerTotal
                {
                    CurrencyCode      = g.Key,
                    CustomerCount     = g.Count(),
                    OrderCount        = g.Sum(r => r.OrderCount),
                    InvoiceCount      = g.Sum(r => r.InvoiceCount),
                    Revenue           = g.Sum(r => r.Revenue),
                    AverageOrderValue = Average(g.Sum(r => r.Revenue), g.Sum(r => r.OrderCount))
                })],
            Items        = [.. rows.Skip((page - 1) * pageSize).Take(pageSize)],
            TotalRecords = rows.Count,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(rows.Count / (double)pageSize)
        };
    }

    // ── R8 Sales vs purchase ─────────────────────────────────────────────────

    public Task<SalesVsPurchaseReport> GetSalesVsPurchaseAsync(SalesVsPurchaseFilter filter) =>
        BuildSalesVsPurchaseAsync(filter, forExport: false);

    public Task<SalesVsPurchaseReport> GetSalesVsPurchaseForExportAsync(SalesVsPurchaseFilter filter) =>
        BuildSalesVsPurchaseAsync(filter, forExport: true);

    private async Task<SalesVsPurchaseReport> BuildSalesVsPurchaseAsync(SalesVsPurchaseFilter filter, bool forExport)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var period = NormalizePeriod(filter.Period);
        var (start, endExclusive) = DayRange.Of(filter.DateFrom, filter.DateTo, "sales vs purchase report");

        var booked = BookedInvoices(start, endExclusive);
        var live   = LiveInvoices(start, endExclusive);

        // Each invoice that stands, with the day it was issued: revenue and cost are bucketed by the same
        // invoice on the same day, so a period's cost is always the cost of what its revenue sold.
        var invoicesBilled = await live
            .Select(i => new
            {
                i.UUID, i.CurrencyCode,
                Revenue = i.Subtotal - i.DiscountAmount,
                Day     = booked.Where(b => b.ReferenceId == i.UUID).Min(b => (DateTime?)b.EntryDate)
            })
            .ToListAsync();

        // A38 (FIN) — credit notes booked in the range: revenue back off on the day they were booked, and their RETURN_IN
        // (the goods back at the cost they left at) off the cost of goods sold.
        var bookedCredits = BookedCreditNotes(start, endExclusive);
        var credits       = LiveCreditNotes(start, endExclusive);
        var creditsBilled = await credits
            .Select(i => new
            {
                i.UUID, i.CurrencyCode,
                Revenue = i.Subtotal - i.DiscountAmount,
                Day     = bookedCredits.Where(b => b.ReferenceId == i.UUID).Min(b => (DateTime?)b.EntryDate)
            })
            .ToListAsync();

        var billed = invoicesBilled.Select(b => (b.UUID, b.CurrencyCode, b.Revenue, b.Day, IsCredit: false))
            .Concat(creditsBilled.Select(c => (c.UUID, c.CurrencyCode, Revenue: -c.Revenue, c.Day, IsCredit: true)))
            .ToList();

        // What issuing each of them booked as the cost of the goods: one SALE entry to an invoice line.
        var costs = await (
            from e in _finance.ProductLedgerEntries.AsNoTracking()
            where e.EntryType == ProductLedgerEntryTypes.Sale && e.ReferenceType == InvoiceReferenceType
               && live.Any(i => i.UUID == e.ReferenceId)
            group e by e.ReferenceId into g
            select new { InvoiceUuid = g.Key, Cost = g.Sum(x => x.TotalCost) }).ToListAsync();

        var returned = await (
            from e in _finance.ProductLedgerEntries.AsNoTracking()
            where e.EntryType == ProductLedgerEntryTypes.ReturnIn && e.ReferenceType == CreditNoteReferenceType
               && credits.Any(i => i.UUID == e.ReferenceId)
            group e by e.ReferenceId into g
            select new { InvoiceUuid = g.Key, Cost = g.Sum(x => x.TotalCost) }).ToListAsync();

        var costOf = costs.ToDictionary(c => c.InvoiceUuid, c => c.Cost);
        foreach (var r in returned) costOf[r.InvoiceUuid] = -r.Cost;

        var rows = billed
            .GroupBy(b => (Start: PeriodStart(b.Day!.Value, period), b.CurrencyCode))
            .Select(g =>
            {
                var revenue = Cents(g.Sum(b => b.Revenue));
                var cost    = Cents(g.Sum(b => costOf.GetValueOrDefault(b.UUID)));
                return new SalesVsPurchaseItem
                {
                    PeriodStart        = g.Key.Start,
                    PeriodLabel        = PeriodLabel(g.Key.Start, period),
                    CurrencyCode       = g.Key.CurrencyCode,
                    InvoiceCount       = g.Count(b => !b.IsCredit),
                    Revenue            = revenue,
                    CostOfGoodsSold    = cost,
                    GrossMargin        = revenue - cost,
                    GrossMarginPercent = Percent(revenue - cost, revenue),
                    UncostedRevenue    = Cents(g.Where(b => !costOf.ContainsKey(b.UUID)).Sum(b => b.Revenue))
                };
            })
            .OrderBy(r => r.PeriodStart).ThenBy(r => r.CurrencyCode, StringComparer.Ordinal)
            .ToList();

        if (rows.Count > MaxRows)
            throw new BadRequestException(
                $"{rows.Count} periods would be listed, more than the {MaxRows} one report carries. Narrow the date range or use a longer period.");

        var (page, pageSize) = forExport
            ? (1, Math.Max(1, rows.Count))
            : PagedResults.Clamp(filter.Page, filter.PageSize);

        return new SalesVsPurchaseReport
        {
            CompanyName = await CompanyNameAsync(),
            GeneratedAt = _clock.GetUtcNow().UtcDateTime,
            Criteria    = new SalesVsPurchaseCriteria { DateFrom = start, DateTo = filter.DateTo?.Date, Period = period },
            Totals = [.. rows
                .GroupBy(r => r.CurrencyCode)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var revenue = g.Sum(r => r.Revenue);
                    var margin  = g.Sum(r => r.GrossMargin);
                    return new SalesVsPurchaseTotal
                    {
                        CurrencyCode       = g.Key,
                        PeriodCount        = g.Count(),
                        InvoiceCount       = g.Sum(r => r.InvoiceCount),
                        Revenue            = revenue,
                        CostOfGoodsSold    = g.Sum(r => r.CostOfGoodsSold),
                        GrossMargin        = margin,
                        GrossMarginPercent = Percent(margin, revenue),
                        UncostedRevenue    = g.Sum(r => r.UncostedRevenue)
                    };
                })],
            Items        = [.. rows.Skip((page - 1) * pageSize).Take(pageSize)],
            TotalRecords = rows.Count,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(rows.Count / (double)pageSize)
        };
    }

    private static string NormalizePeriod(string? value)
    {
        var normalized = value?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(normalized)) return SalesPeriods.Month;

        return SalesPeriods.All.Contains(normalized)
            ? normalized
            : throw new BadRequestException($"'{value}' is not a period. Valid: {string.Join(", ", SalesPeriods.All)}.");
    }

    /// <summary>The day itself, the Monday of its week, or the first of its month.</summary>
    private static DateTime PeriodStart(DateTime day, string period) => period switch
    {
        SalesPeriods.Day  => day.Date,
        SalesPeriods.Week => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
        _                 => new DateTime(day.Year, day.Month, 1)
    };

    private static string PeriodLabel(DateTime start, string period) => period switch
    {
        SalesPeriods.Day  => start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        SalesPeriods.Week => $"{ISOWeek.GetYear(start)}-W{ISOWeek.GetWeekOfYear(start):00}",
        _                 => start.ToString("yyyy-MM", CultureInfo.InvariantCulture)
    };

    private static decimal? Percent(decimal margin, decimal revenue) =>
        revenue > 0m ? Cents(margin / revenue * 100m) : null;

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>The ledger entries that booked an invoice as issued, in the range: an invoice is issued the day its entry was posted.</summary>
    private IQueryable<CustomerLedgerEntry> BookedInvoices(DateTime? start, DateTime? endExclusive)
    {
        var booked = _finance.CustomerLedgerEntries.Where(e =>
            e.EntryType == CustomerLedgerEntryTypes.Invoice && e.ReferenceType == InvoiceReferenceType);

        if (start is { } from)       booked = booked.Where(e => e.EntryDate >= from);
        if (endExclusive is { } end) booked = booked.Where(e => e.EntryDate < end);
        return booked;
    }

    /// <summary>
    /// The invoices that count as sales in the range: live, and issued in it. An invoice is issued the day
    /// its ledger entry was posted, so a draft that waited is a sale of the day it was issued, and one never
    /// issued is no sale at all.
    /// </summary>
    private IQueryable<SalesInvoice> LiveInvoices(DateTime? start, DateTime? endExclusive)
    {
        var booked = BookedInvoices(start, endExclusive);

        return _finance.SalesInvoices.AsNoTracking()
            .Where(i => !i.IsDelete && LiveStatuses.Contains(i.Status) && booked.Any(b => b.ReferenceId == i.UUID));
    }

    /// <summary>A38 (FIN) — the CREDIT_NOTE ledger entries that booked a customer credit note, in the range.</summary>
    private IQueryable<CustomerLedgerEntry> BookedCreditNotes(DateTime? start, DateTime? endExclusive)
    {
        var booked = _finance.CustomerLedgerEntries.Where(e =>
            e.EntryType == CustomerLedgerEntryTypes.CreditNote && e.ReferenceType == CreditNoteReferenceType);

        if (start is { } from)       booked = booked.Where(e => e.EntryDate >= from);
        if (endExclusive is { } end) booked = booked.Where(e => e.EntryDate < end);
        return booked;
    }

    /// <summary>
    /// A38 (FIN) — customer credit notes (POS returns and voids) booked in the range: they net the sales reports, on the day
    /// they were booked. (A cancelled invoice is not one of them — it simply stops being a live invoice.)
    /// </summary>
    private IQueryable<SalesInvoice> LiveCreditNotes(DateTime? start, DateTime? endExclusive)
    {
        var booked = BookedCreditNotes(start, endExclusive);

        return _finance.SalesInvoices.AsNoTracking()
            .Where(i => !i.IsDelete && i.Status == SalesInvoiceStatuses.CreditNote && booked.Any(b => b.ReferenceId == i.UUID));
    }

    private sealed record LineSum(Guid VariantUuid, string CurrencyCode, decimal Quantity, decimal Gross, decimal Discount, decimal Net);

    private static decimal Cents(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static decimal Average(decimal revenue, int orders) => orders == 0 ? 0m : Cents(revenue / orders);

    private async Task<string?> CompanyNameAsync()
    {
        var template = await _templates.GetActiveAsync();
        return string.IsNullOrWhiteSpace(template?.CompanyName) ? null : template.CompanyName.Trim();
    }
}
