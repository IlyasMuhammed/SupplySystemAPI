namespace SMS.Modules.Finance.Models;

// A29-P8-05 §11.4 — the product ledger as callers see it. The entity stays internal to Finance; these
// are what an endpoint or a report can serialize.

public class ProductLedgerEntryModel
{
    public Guid     Uuid            { get; set; }
    public Guid     VariantUuid     { get; set; }
    public Guid     ProductUuid     { get; set; }
    /// <summary>Position in this variant's ledger, 1, 2, 3…; the order the entries were posted in.</summary>
    public int      SequenceNo      { get; set; }
    /// <summary>The business date of the movement.</summary>
    public DateTime EntryDate       { get; set; }
    /// <summary>PURCHASE, SALE, RETURN_IN, RETURN_OUT, ADJUSTMENT or WRITE_OFF.</summary>
    public string   EntryType       { get; set; } = string.Empty;
    public string   ReferenceType   { get; set; } = string.Empty;
    public Guid     ReferenceId     { get; set; }
    public string   ReferenceNumber { get; set; } = string.Empty;
    /// <summary>The supplier or customer on the other side; none for an adjustment or a write-off.</summary>
    public Guid?    PartnerId       { get; set; }
    /// <summary>IN or OUT.</summary>
    public string   Direction       { get; set; } = string.Empty;
    /// <summary>Always positive; <see cref="Direction"/> says which way it moved.</summary>
    public decimal  Quantity        { get; set; }
    /// <summary>What one unit cost. For an OUT entry, the weighted-average cost the units left at.</summary>
    public decimal  UnitCost        { get; set; }
    /// <summary>The value that moved. For a SALE, its cost of goods sold.</summary>
    public decimal  TotalCost       { get; set; }
    public decimal  RunningQty      { get; set; }
    public decimal  RunningValue    { get; set; }
    /// <summary><see cref="RunningValue"/> over <see cref="RunningQty"/> after this entry; zero when nothing is left.</summary>
    public decimal  WeightedAverageCost { get; set; }
    public string?  Narration       { get; set; }
    public int      CreatedBy       { get; set; }
    public DateTime CreatedDate     { get; set; }
}

/// <summary>
/// Which of a variant's entries to page through. The range is in whole days and inclusive at both ends,
/// as the customer ledger's is: a <c>DateTo</c> of the 20th includes an entry at 15:00 on the 20th.
/// </summary>
public class ProductLedgerFilter
{
    public DateTime? DateFrom  { get; set; }
    public DateTime? DateTo    { get; set; }
    /// <summary>Only this kind of entry — PURCHASE, SALE, RETURN_IN, RETURN_OUT, ADJUSTMENT or WRITE_OFF.</summary>
    public string?   EntryType { get; set; }
    /// <summary>Only entries that moved this way — IN or OUT.</summary>
    public string?   Direction { get; set; }
    public int       Page      { get; set; } = 1;
    /// <summary>Clamped to 1–100.</summary>
    public int       PageSize  { get; set; } = 20;
}

/// <summary>
/// Where a variant stands and how it got there. The purchased and sold figures are lifetime totals of
/// PURCHASE and SALE entries only; returns, adjustments and write-offs move the stock and its value
/// (which is why the current figures come from the last entry, not from these two) but are neither.
/// </summary>
public class ProductLedgerSummaryModel
{
    public Guid      VariantUuid         { get; set; }
    /// <summary>Null for a variant that has no entries yet.</summary>
    public Guid?     ProductUuid         { get; set; }

    /// <summary>Units received on GRNs.</summary>
    public decimal   PurchasedQuantity   { get; set; }
    /// <summary>What they cost, at the purchase order line prices.</summary>
    public decimal   PurchasedCost       { get; set; }
    /// <summary>Units sold on issued invoices.</summary>
    public decimal   SoldQuantity        { get; set; }
    /// <summary>What they cost when they left — the cost of goods sold.</summary>
    public decimal   CostOfGoodsSold     { get; set; }

    /// <summary>Units the ledger holds now.</summary>
    public decimal   CurrentQuantity     { get; set; }
    /// <summary>What they are worth now.</summary>
    public decimal   StockValue          { get; set; }
    /// <summary><see cref="StockValue"/> over <see cref="CurrentQuantity"/>, to four places; zero when nothing is held.</summary>
    public decimal   WeightedAverageCost { get; set; }

    public int       EntryCount          { get; set; }
    public DateTime? LastEntryDate       { get; set; }
}

/// <summary>
/// The period the report covers, by the date the sale's cost was booked (the day its invoice was issued),
/// in whole days and inclusive at both ends. Leave a bound out for an open-ended period.
/// </summary>
public class ProductProfitabilityFilter
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public int       Page     { get; set; } = 1;
    /// <summary>Clamped to 1–100.</summary>
    public int       PageSize { get; set; } = 20;
}

/// <summary>
/// One product's revenue against what it cost, in one currency. There are no exchange rates in the
/// system, so a product sold in two currencies is two rows rather than one figure that adds rupees to
/// dollars; the cost side is the ledger's own, which has no currency.
/// </summary>
public class ProductProfitabilityItemModel
{
    public Guid     ProductUuid     { get; set; }
    /// <summary>Null when Inventory no longer knows the product's variants (they have all been deactivated).</summary>
    public string?  ProductName     { get; set; }
    /// <summary>The currency of the invoices the revenue is from.</summary>
    public string   CurrencyCode    { get; set; } = string.Empty;
    public decimal  QuantitySold    { get; set; }
    /// <summary>What was invoiced for it, after discount and before tax.</summary>
    public decimal  Revenue         { get; set; }
    public decimal  CostOfGoodsSold { get; set; }
    /// <summary><see cref="Revenue"/> less <see cref="CostOfGoodsSold"/>.</summary>
    public decimal  GrossProfit     { get; set; }
    /// <summary>Gross profit as a percentage of revenue, to two places; null when there was no revenue to take it of.</summary>
    public decimal? MarginPercent   { get; set; }
}
