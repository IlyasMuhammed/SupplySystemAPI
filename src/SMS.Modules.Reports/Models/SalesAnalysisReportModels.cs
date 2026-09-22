namespace SMS.Modules.Reports.Models;

// A29-P9-04 §15 — R4 sales by product, R5 sales by customer, R6 fulfilment status.
//
// "Sales" in R4 and R5 means invoices that stand: issued and not since cancelled or credited, counted on
// the day they were issued (the day the ledger booked them, as in the customer ledger and aging reports),
// and revenue means what they billed before tax — the goods at their price, less the discount. That is the
// same definition the product profitability report uses, so the reports agree. Every figure is kept per
// currency, because there is no exchange rate to add one currency to another with.

// ── R4 Sales by product ──────────────────────────────────────────────────────

/// <summary>
/// Which sales to add up. The range is in whole days, inclusive at both ends, on the day each invoice was
/// issued; leave an end out to leave it open. <c>PartnerId</c> narrows the report to one customer's purchases.
/// </summary>
public class SalesByProductFilter
{
    public DateTime? DateFrom  { get; set; }
    public DateTime? DateTo    { get; set; }
    /// <summary>The customer — a business partner's UUID.</summary>
    public Guid?     PartnerId { get; set; }
    public int       Page      { get; set; } = 1;
    /// <summary>Clamped to 1–100. The PDF and Excel exports ignore paging and carry every product.</summary>
    public int       PageSize  { get; set; } = 20;
}

public class SalesByProductItem
{
    /// <summary>
    /// The product. A variant whose product the books cannot name — one that was never in the product
    /// ledger — stands as its own product, and this is its variant's UUID.
    /// </summary>
    public Guid     ProductUuid       { get; set; }
    /// <summary>Null when the product's record cannot be found.</summary>
    public string?  ProductName       { get; set; }
    public string   CurrencyCode      { get; set; } = string.Empty;
    /// <summary>Units invoiced, over all of the product's variants.</summary>
    public decimal  QuantitySold      { get; set; }
    /// <summary>The goods at their price less the discount, before tax.</summary>
    public decimal  Revenue           { get; set; }
    /// <summary><c>Revenue / QuantitySold</c>; null when nothing was sold by the unit.</summary>
    public decimal? AverageUnitPrice  { get; set; }
}

/// <summary>What was sold in one currency: how many products it was of, and what they came to. Not a quantity — units of different products do not add.</summary>
public class SalesByProductTotal
{
    public string  CurrencyCode { get; set; } = string.Empty;
    public int     ProductCount { get; set; }
    public decimal Revenue      { get; set; }
}

public class SalesByProductCriteria
{
    public DateTime? DateFrom     { get; set; }
    public DateTime? DateTo       { get; set; }
    public Guid?     PartnerId    { get; set; }
    public string?   CustomerName { get; set; }
}

public class SalesByProductReport
{
    public string?   CompanyName { get; set; }
    public DateTime  GeneratedAt { get; set; }
    public SalesByProductCriteria Criteria { get; set; } = new();

    /// <summary>One row per currency, over every product and not only this page.</summary>
    public List<SalesByProductTotal> Totals { get; set; } = [];

    /// <summary>Highest revenue first.</summary>
    public List<SalesByProductItem> Items { get; set; } = [];

    /// <summary>Every product and currency sold, not only those on this page.</summary>
    public int TotalRecords { get; set; }
    public int Page         { get; set; }
    public int PageSize     { get; set; }
    public int TotalPages   { get; set; }
}

// ── R5 Sales by customer ─────────────────────────────────────────────────────

/// <summary>Which sales to add up: the days they were issued on, in whole days inclusive at both ends. Leave an end out to leave it open.</summary>
public class SalesByCustomerFilter
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public int       Page     { get; set; } = 1;
    /// <summary>Clamped to 1–100. The PDF and Excel exports ignore paging and carry every customer.</summary>
    public int       PageSize { get; set; } = 20;
}

public class SalesByCustomerItem
{
    public Guid     PartnerId    { get; set; }
    /// <summary>Null when the customer's record cannot be found.</summary>
    public string?  CustomerName { get; set; }
    public string   CurrencyCode { get; set; } = string.Empty;
    /// <summary>Sale orders with at least one invoice in the period; an order billed in several invoices is one order.</summary>
    public int      OrderCount   { get; set; }
    public int      InvoiceCount { get; set; }
    /// <summary>What they were billed before tax: the goods at their price, less the discount.</summary>
    public decimal  Revenue      { get; set; }
    /// <summary><c>Revenue / OrderCount</c>: what a sale order is worth on average.</summary>
    public decimal  AverageOrderValue { get; set; }
}

/// <summary>All customers in one currency.</summary>
public class SalesByCustomerTotal
{
    public string  CurrencyCode  { get; set; } = string.Empty;
    public int     CustomerCount { get; set; }
    public int     OrderCount    { get; set; }
    public int     InvoiceCount  { get; set; }
    public decimal Revenue       { get; set; }
    public decimal AverageOrderValue { get; set; }
}

public class SalesByCustomerCriteria
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
}

public class SalesByCustomerReport
{
    public string?   CompanyName { get; set; }
    public DateTime  GeneratedAt { get; set; }
    public SalesByCustomerCriteria Criteria { get; set; } = new();

    /// <summary>One row per currency, over every customer and not only this page.</summary>
    public List<SalesByCustomerTotal> Totals { get; set; } = [];

    /// <summary>Highest revenue first.</summary>
    public List<SalesByCustomerItem> Items { get; set; } = [];

    /// <summary>Every customer and currency billed, not only those on this page.</summary>
    public int TotalRecords { get; set; }
    public int Page         { get; set; }
    public int PageSize     { get; set; }
    public int TotalPages   { get; set; }
}

// ── R6 Fulfilment status ─────────────────────────────────────────────────────

/// <summary>
/// Which open sale-order deliveries to list. <c>Status</c> is one of the open delivery statuses and
/// <c>DeliveryMode</c> is SHIP or SELF_PICKUP, both in any case; <c>WarehouseId</c> is the warehouse the
/// goods leave from. Leave a filter out to leave that dimension open.
/// </summary>
public class FulfilmentStatusFilter
{
    public string? Status       { get; set; }
    public Guid?   WarehouseId  { get; set; }
    public string? DeliveryMode { get; set; }
    public int     Page         { get; set; } = 1;
    /// <summary>Clamped to 1–100. The PDF and Excel exports ignore paging and carry every open delivery.</summary>
    public int     PageSize     { get; set; } = 20;
}

public class FulfilmentStatusItem
{
    public Guid      DeliveryUuid      { get; set; }
    public string    DeliveryNumber    { get; set; } = string.Empty;
    public Guid?     SaleOrderUuid     { get; set; }
    public string?   SaleOrderNumber   { get; set; }
    public Guid?     PartnerId         { get; set; }
    /// <summary>Null when the sale order or its customer cannot be found.</summary>
    public string?   CustomerName      { get; set; }
    public string    Status            { get; set; } = string.Empty;
    /// <summary>SHIP or SELF_PICKUP.</summary>
    public string?   DeliveryMode      { get; set; }
    public Guid?     WarehouseUuid     { get; set; }
    /// <summary>The warehouse the goods leave from; null when none is set or it cannot be found.</summary>
    public string?   WarehouseName     { get; set; }
    public DateTime? RequestedDate     { get; set; }
    public DateTime? PromisedDate      { get; set; }
    public DateTime  CreatedDate       { get; set; }
    /// <summary>Whole days from the day the delivery was raised to the day the report was made.</summary>
    public int       DaysOpen          { get; set; }
    public int       LineCount         { get; set; }
    public decimal   QuantityOrdered   { get; set; }
    public decimal   QuantityDelivered { get; set; }
}

public class FulfilmentStatusCount
{
    public string Status { get; set; } = string.Empty;
    public int    Count  { get; set; }
}

public class FulfilmentWarehouseCount
{
    /// <summary>Null for deliveries that name no warehouse.</summary>
    public Guid?   WarehouseUuid { get; set; }
    public string? WarehouseName  { get; set; }
    public int     Count          { get; set; }
}

public class FulfilmentModeCount
{
    public string DeliveryMode { get; set; } = string.Empty;
    public int    Count        { get; set; }
}

public class FulfilmentStatusCriteria
{
    public string? Status        { get; set; }
    public Guid?   WarehouseUuid { get; set; }
    public string? WarehouseName { get; set; }
    public string? DeliveryMode  { get; set; }
}

public class FulfilmentStatusReport
{
    public string?   CompanyName { get; set; }
    public DateTime  GeneratedAt { get; set; }
    public FulfilmentStatusCriteria Criteria { get; set; } = new();

    // Each breakdown is over every open delivery the filters match, and each adds up to TotalRecords.
    /// <summary>In the order a delivery moves through them.</summary>
    public List<FulfilmentStatusCount>    ByStatus       { get; set; } = [];
    /// <summary>Busiest warehouse first.</summary>
    public List<FulfilmentWarehouseCount> ByWarehouse    { get; set; } = [];
    public List<FulfilmentModeCount>      ByDeliveryMode { get; set; } = [];

    /// <summary>Oldest delivery first: the one that has waited longest.</summary>
    public List<FulfilmentStatusItem> Items { get; set; } = [];

    /// <summary>Every open delivery the filters match, not only those on this page.</summary>
    public int TotalRecords { get; set; }
    public int Page         { get; set; }
    public int PageSize     { get; set; }
    public int TotalPages   { get; set; }
}
