namespace SMS.Modules.Reports.Models;

// A29-P9-06 §15 — R9 product ledger and R10 product profitability.
//
// R9 is a product's stock account: every movement of every variant of it, with the quantity and value the
// product held after each. R10 is what each product earned against what it cost, ranked, over the sales that
// have both an invoice and a cost of sales booked on the product ledger (A29-P8-05). It leaves out a sale whose
// cost was never booked, such as an invoice issued before the ledger existed, rather than show it at a 100%
// margin; the sales-vs-purchase report (R8) is the one that shows such revenue and says it has no cost.
// Costs have no currency and there are no exchange rates, so R10 is kept per currency and a cost is taken to be
// in the currency of the sale it is set against.

// ── R9 Product ledger ────────────────────────────────────────────────────────

/// <summary>
/// Which movements to list. <c>ProductId</c> is required: a product ledger is one product's account, of all its
/// variants; <c>VariantId</c> narrows it to one of them. The range is in whole days, inclusive at both ends, on
/// the day of the movement; leave an end out to leave it open.
/// </summary>
public class ProductLedgerReportFilter
{
    /// <summary>The product's UUID.</summary>
    public Guid?     ProductId { get; set; }
    /// <summary>One of the product's variants, by UUID.</summary>
    public Guid?     VariantId { get; set; }
    public DateTime? DateFrom  { get; set; }
    public DateTime? DateTo    { get; set; }
    public int       Page      { get; set; } = 1;
    /// <summary>Clamped to 1–100. The PDF and Excel exports ignore paging and carry every movement.</summary>
    public int       PageSize  { get; set; } = 20;
}

public class ProductLedgerReportEntry
{
    public Guid     EntryUuid       { get; set; }
    public Guid     VariantUuid     { get; set; }
    /// <summary>Null when Inventory no longer knows the variant.</summary>
    public string?  Sku             { get; set; }
    public string?  VariantName     { get; set; }
    /// <summary>The business date of the movement.</summary>
    public DateTime EntryDate       { get; set; }
    /// <summary>PURCHASE, SALE, RETURN_IN, RETURN_OUT, ADJUSTMENT or WRITE_OFF.</summary>
    public string   EntryType       { get; set; } = string.Empty;
    public string   ReferenceType   { get; set; } = string.Empty;
    public Guid     ReferenceId     { get; set; }
    public string   ReferenceNumber { get; set; } = string.Empty;
    /// <summary>The supplier or customer on the other side; none for an adjustment or a write-off.</summary>
    public Guid?    PartnerId       { get; set; }
    /// <summary>Null when there is no partner or the lookup cannot find them.</summary>
    public string?  PartnerName     { get; set; }
    /// <summary>IN or OUT.</summary>
    public string   Direction       { get; set; } = string.Empty;
    /// <summary>Always positive; <see cref="Direction"/> says which way it moved.</summary>
    public decimal  Quantity        { get; set; }
    /// <summary>What one unit cost; for an OUT entry, the weighted-average cost it left at.</summary>
    public decimal  UnitCost        { get; set; }
    /// <summary>The value that moved; for a SALE, its cost of goods sold.</summary>
    public decimal  TotalCost       { get; set; }

    /// <summary>
    /// What the <b>product</b> held after this movement, over all its variants: the opening quantity plus
    /// every movement up to and including this one, in the order the report lists them.
    /// </summary>
    public decimal  RunningQty          { get; set; }
    public decimal  RunningValue        { get; set; }
    /// <summary><see cref="RunningValue"/> over <see cref="RunningQty"/>, to four places; zero when nothing is held.</summary>
    public decimal  WeightedAverageCost { get; set; }

    /// <summary>What the ledger itself recorded for this <b>variant</b> when the entry was posted: for a product of one variant, the same as the product's.</summary>
    public decimal  VariantRunningQty   { get; set; }
    public decimal  VariantRunningValue { get; set; }

    public string?  Narration       { get; set; }
}

/// <summary>Where the product stood when the range began, what moved, and where it stood when it ended.</summary>
public class ProductLedgerReportSummary
{
    public decimal OpeningQuantity { get; set; }
    public decimal OpeningValue    { get; set; }
    /// <summary>Units that came in, and what they were worth, over the whole range and not only this page.</summary>
    public decimal QuantityIn      { get; set; }
    public decimal ValueIn         { get; set; }
    public decimal QuantityOut     { get; set; }
    public decimal ValueOut        { get; set; }
    public decimal ClosingQuantity { get; set; }
    public decimal ClosingValue    { get; set; }
    /// <summary>Closing value over closing quantity, to four places; zero when nothing is held.</summary>
    public decimal ClosingWeightedAverageCost { get; set; }
    /// <summary>Movements in the range.</summary>
    public int     MovementCount   { get; set; }
}

public class ProductLedgerReportCriteria
{
    public Guid      ProductUuid { get; set; }
    /// <summary>Null when Inventory cannot name the product.</summary>
    public string?   ProductName { get; set; }
    public Guid?     VariantUuid { get; set; }
    public string?   VariantName { get; set; }
    public DateTime? DateFrom    { get; set; }
    public DateTime? DateTo      { get; set; }
}

public class ProductLedgerReport
{
    public string?   CompanyName { get; set; }
    public DateTime  GeneratedAt { get; set; }
    public ProductLedgerReportCriteria Criteria { get; set; } = new();
    public ProductLedgerReportSummary  Summary  { get; set; } = new();

    /// <summary>Oldest first, as a ledger reads.</summary>
    public List<ProductLedgerReportEntry> Items { get; set; } = [];

    /// <summary>Every movement in the range, not only those on this page.</summary>
    public int TotalRecords { get; set; }
    public int Page         { get; set; }
    public int PageSize     { get; set; }
    public int TotalPages   { get; set; }
}

// ── R10 Product profitability ────────────────────────────────────────────────

/// <summary>
/// The period the report covers, by the day each sale's cost was booked, which is the day its invoice was issued,
/// in whole days and inclusive at both ends. Leave an end out to leave it open.
/// </summary>
public class ProfitabilityReportFilter
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public int       Page     { get; set; } = 1;
    /// <summary>Clamped to 1–100. The PDF and Excel exports ignore paging and carry every product.</summary>
    public int       PageSize { get; set; } = 20;
}

public class ProfitabilityReportItem
{
    /// <summary>1 for the most profitable product in this row's currency. Currencies are ranked apart: there is no exchange rate to compare them with.</summary>
    public int      Rank            { get; set; }
    public Guid     ProductUuid     { get; set; }
    /// <summary>Null when Inventory no longer knows the product's variants.</summary>
    public string?  ProductName     { get; set; }
    public string   CurrencyCode    { get; set; } = string.Empty;
    public decimal  QuantitySold    { get; set; }
    /// <summary>What was invoiced for it, after discount and before tax.</summary>
    public decimal  Revenue         { get; set; }
    public decimal  CostOfGoodsSold { get; set; }
    /// <summary>Revenue less cost of goods sold: the margin the ranking is by.</summary>
    public decimal  GrossProfit     { get; set; }
    /// <summary>Gross profit as a percentage of revenue; null when there was no revenue to take it of.</summary>
    public decimal? MarginPercent   { get; set; }
}

/// <summary>Every product in one currency. Not a quantity: units of different products do not add.</summary>
public class ProfitabilityReportTotal
{
    public string   CurrencyCode    { get; set; } = string.Empty;
    public int      ProductCount    { get; set; }
    public decimal  Revenue         { get; set; }
    public decimal  CostOfGoodsSold { get; set; }
    public decimal  GrossProfit     { get; set; }
    public decimal? MarginPercent   { get; set; }
}

public class ProfitabilityReportCriteria
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
}

public class ProfitabilityReport
{
    public string?   CompanyName { get; set; }
    public DateTime  GeneratedAt { get; set; }
    public ProfitabilityReportCriteria Criteria { get; set; } = new();

    /// <summary>One row per currency, over every product and not only this page.</summary>
    public List<ProfitabilityReportTotal> Totals { get; set; } = [];

    /// <summary>Most profitable first.</summary>
    public List<ProfitabilityReportItem> Items { get; set; } = [];

    /// <summary>Every product and currency sold, not only those on this page.</summary>
    public int TotalRecords { get; set; }
    public int Page         { get; set; }
    public int PageSize     { get; set; }
    public int TotalPages   { get; set; }
}
