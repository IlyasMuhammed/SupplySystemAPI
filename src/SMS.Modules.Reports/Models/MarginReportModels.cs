namespace SMS.Modules.Reports.Models;

// A29-P9-05 §15 — R7 margin analysis and R8 sales vs purchase.
//
// The two answer different questions and read different books. R7 is the margin an order is expected to
// make, from the price on the sale order line against what the purchase orders raised for it cost: it exists
// before anything is invoiced, and it covers only what was bought in for the order. R8 is the margin actually
// made, from what the invoices that stand billed against the cost of sales the product ledger booked when they
// were issued. Both are before tax, and both are kept per currency. Purchase prices and ledger costs carry no
// currency, so a cost is taken to be in the currency it is set against; there is no exchange rate to convert one.

/// <summary>What R7 can be grouped by.</summary>
public static class MarginGroupings
{
    public const string Product  = "PRODUCT";
    public const string Customer = "CUSTOMER";
    public const string Order    = "ORDER";

    public static readonly IReadOnlyList<string> All = [Product, Customer, Order];
}

/// <summary>What R8 can be bucketed by. Weeks run Monday to Sunday.</summary>
public static class SalesPeriods
{
    public const string Day   = "DAY";
    public const string Week  = "WEEK";
    public const string Month = "MONTH";

    public static readonly IReadOnlyList<string> All = [Day, Week, Month];
}

// ── R7 Margin analysis ───────────────────────────────────────────────────────

/// <summary>
/// Which orders to analyse. The range is in whole days, inclusive at both ends, on the order date; leave an
/// end out to leave it open. <c>GroupBy</c> is PRODUCT, CUSTOMER or ORDER in any case, and PRODUCT when left out.
/// </summary>
public class MarginAnalysisFilter
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public string?   GroupBy  { get; set; }
    public int       Page     { get; set; } = 1;
    /// <summary>Clamped to 1–100. The PDF and Excel exports ignore paging and carry every row.</summary>
    public int       PageSize { get; set; } = 20;
}

public class MarginAnalysisItem
{
    /// <summary>
    /// The product, customer or sale order the row is of, by grouping. A variant Inventory cannot name stands as
    /// its own product, and this is its variant's UUID.
    /// </summary>
    public Guid     GroupId      { get; set; }
    /// <summary>The product's or customer's name, or the order's number. Null when the record cannot be found.</summary>
    public string?  Name         { get; set; }
    /// <summary>The order's customer, when grouped by order; otherwise null.</summary>
    public string?  Detail       { get; set; }
    public string   CurrencyCode { get; set; } = string.Empty;
    /// <summary>Order lines in the row that have a purchase order behind them.</summary>
    public int      LineCount    { get; set; }
    /// <summary>Units bought in and sold on those lines. Only when grouped by product: units of different products do not add.</summary>
    public decimal? Quantity     { get; set; }
    /// <summary>What those units sell for on the order: price less the line discount, before tax.</summary>
    public decimal  SellingValue { get; set; }
    /// <summary>What the purchase orders raised for them cost.</summary>
    public decimal  Cost         { get; set; }
    public decimal  Margin       { get; set; }
    /// <summary><c>Margin / SellingValue × 100</c>; null when nothing was sold at a price.</summary>
    public decimal? MarginPercent { get; set; }
    /// <summary>Per unit, when grouped by product; otherwise null.</summary>
    public decimal? AverageSellingPrice { get; set; }
    public decimal? AverageCost         { get; set; }
}

/// <summary>Every row in one currency.</summary>
public class MarginAnalysisTotal
{
    public string   CurrencyCode      { get; set; } = string.Empty;
    public int      GroupCount        { get; set; }
    public int      LineCount         { get; set; }
    /// <summary>Order lines in scope with no purchase order behind them — filled from stock, so no cost to set against them. They are not in any figure here.</summary>
    public int      UncostedLineCount { get; set; }
    public decimal  SellingValue      { get; set; }
    public decimal  Cost              { get; set; }
    public decimal  Margin            { get; set; }
    public decimal? MarginPercent     { get; set; }
}

public class MarginAnalysisCriteria
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public string    GroupBy  { get; set; } = MarginGroupings.Product;
}

public class MarginAnalysisReport
{
    public string?   CompanyName { get; set; }
    public DateTime  GeneratedAt { get; set; }
    public MarginAnalysisCriteria Criteria { get; set; } = new();

    /// <summary>One row per currency, over every row of the report and not only this page.</summary>
    public List<MarginAnalysisTotal> Totals { get; set; } = [];

    /// <summary>Highest margin first.</summary>
    public List<MarginAnalysisItem> Items { get; set; } = [];

    /// <summary>Every product, customer or order and currency in scope, not only those on this page.</summary>
    public int TotalRecords { get; set; }
    public int Page         { get; set; }
    public int PageSize     { get; set; }
    public int TotalPages   { get; set; }
}

// ── R8 Sales vs purchase ─────────────────────────────────────────────────────

/// <summary>
/// Which sales to compare with what they cost. The range is in whole days, inclusive at both ends, on the day
/// each invoice was issued; leave an end out to leave it open. <c>Period</c> is DAY, WEEK or MONTH in any case,
/// and MONTH when left out.
/// </summary>
public class SalesVsPurchaseFilter
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public string?   Period   { get; set; }
    public int       Page     { get; set; } = 1;
    /// <summary>Clamped to 1–100. The PDF and Excel exports ignore paging and carry every period.</summary>
    public int       PageSize { get; set; } = 20;
}

public class SalesVsPurchaseItem
{
    /// <summary>The first day of the period: the day itself, the Monday of the week, or the first of the month.</summary>
    public DateTime PeriodStart  { get; set; }
    /// <summary>"2026-09-14", "2026-W38" or "2026-09".</summary>
    public string   PeriodLabel  { get; set; } = string.Empty;
    public string   CurrencyCode { get; set; } = string.Empty;
    public int      InvoiceCount { get; set; }
    /// <summary>What the invoices billed before tax: the goods at their price, less the discount.</summary>
    public decimal  Revenue      { get; set; }
    /// <summary>What the product ledger booked as the cost of the goods when each invoice was issued.</summary>
    public decimal  CostOfGoodsSold { get; set; }
    public decimal  GrossMargin  { get; set; }
    /// <summary><c>GrossMargin / Revenue × 100</c>; null when nothing was billed.</summary>
    public decimal? GrossMarginPercent { get; set; }
    /// <summary>
    /// The part of <c>Revenue</c> from invoices with no cost of sales booked at all, such as one issued before
    /// the product ledger existed. It is in <c>Revenue</c> and has no cost in <c>CostOfGoodsSold</c>, so it makes
    /// the margin look better than it was.
    /// </summary>
    public decimal  UncostedRevenue { get; set; }
}

/// <summary>Every period in one currency.</summary>
public class SalesVsPurchaseTotal
{
    public string   CurrencyCode    { get; set; } = string.Empty;
    public int      PeriodCount     { get; set; }
    public int      InvoiceCount    { get; set; }
    public decimal  Revenue         { get; set; }
    public decimal  CostOfGoodsSold { get; set; }
    public decimal  GrossMargin     { get; set; }
    public decimal? GrossMarginPercent { get; set; }
    public decimal  UncostedRevenue { get; set; }
}

public class SalesVsPurchaseCriteria
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public string    Period   { get; set; } = SalesPeriods.Month;
}

public class SalesVsPurchaseReport
{
    public string?   CompanyName { get; set; }
    public DateTime  GeneratedAt { get; set; }
    public SalesVsPurchaseCriteria Criteria { get; set; } = new();

    /// <summary>One row per currency, over every period and not only this page.</summary>
    public List<SalesVsPurchaseTotal> Totals { get; set; } = [];

    /// <summary>Oldest period first. A period with no sales has no row.</summary>
    public List<SalesVsPurchaseItem> Items { get; set; } = [];

    /// <summary>Every period and currency with sales, not only those on this page.</summary>
    public int TotalRecords { get; set; }
    public int Page         { get; set; }
    public int PageSize     { get; set; }
    public int TotalPages   { get; set; }
}
