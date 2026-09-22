namespace SMS.Modules.Reports.Models;

// A29-P9-01 §15 — the sales reports (GET /api/reports/sales/{name}). R1, the sale order register, first.

/// <summary>
/// Which sale orders the register lists. The order-date range is in whole days and inclusive at both
/// ends: a <c>DateTo</c> of the 20th includes every order dated the 20th. Leave a filter out to leave
/// that dimension open. <c>Status</c> and <c>DeliveryMode</c> take the codes an order carries
/// (<c>CONFIRMED</c>, <c>SELF_PICKUP</c>…), in any case.
/// </summary>
public class SalesOrderRegisterFilter
{
    public DateTime? DateFrom     { get; set; }
    public DateTime? DateTo       { get; set; }
    public string?   Status       { get; set; }
    /// <summary>The customer — a business partner's UUID.</summary>
    public Guid?     PartnerId    { get; set; }
    public string?   DeliveryMode { get; set; }
    public int       Page         { get; set; } = 1;
    /// <summary>Clamped to 1–100. The PDF and Excel exports ignore paging and carry every matching order.</summary>
    public int       PageSize     { get; set; } = 20;
}

public class SalesOrderRegisterItem
{
    public Guid      Uuid                 { get; set; }
    public string    SoNumber             { get; set; } = string.Empty;
    public DateTime  OrderDate            { get; set; }
    public DateTime? ExpectedDeliveryDate { get; set; }
    public Guid      PartnerId            { get; set; }
    /// <summary>Null when the customer's record cannot be found.</summary>
    public string?   CustomerName         { get; set; }
    /// <summary>DRAFT, CONFIRMED, PARTIALLY_FULFILLED, FULFILLED, INVOICED, CLOSED or CANCELLED.</summary>
    public string    Status               { get; set; } = string.Empty;
    /// <summary>SHIP or SELF_PICKUP.</summary>
    public string    DeliveryMode         { get; set; } = string.Empty;
    /// <summary>The order's currency; <c>?</c> when the catalog no longer has it.</summary>
    public string    CurrencyCode         { get; set; } = string.Empty;
    public int       LineCount            { get; set; }
    public decimal   Subtotal             { get; set; }
    public decimal   DiscountAmount       { get; set; }
    public decimal   TaxAmount            { get; set; }
    public decimal   GrandTotal           { get; set; }
}

/// <summary>
/// What every listed order comes to, in one currency. Kept per currency because there are no exchange
/// rates to add one to another with. It is over <i>every</i> order the filter matches — not only the
/// page — and over all their statuses, cancelled and draft ones included: filter by status for the
/// figure you want.
/// </summary>
public class SalesOrderRegisterTotal
{
    public string  CurrencyCode   { get; set; } = string.Empty;
    public int     OrderCount     { get; set; }
    public decimal Subtotal       { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxAmount      { get; set; }
    public decimal GrandTotal     { get; set; }
}

/// <summary>The filter as it was applied, with the customer's name where one was named — what a printed copy says it is a register of.</summary>
public class SalesOrderRegisterCriteria
{
    public DateTime? DateFrom     { get; set; }
    public DateTime? DateTo       { get; set; }
    public string?   Status       { get; set; }
    public Guid?     PartnerId    { get; set; }
    public string?   CustomerName { get; set; }
    public string?   DeliveryMode { get; set; }
}

public class SalesOrderRegisterReport
{
    /// <summary>The company's name from the document letterhead, when one is configured.</summary>
    public string?   CompanyName  { get; set; }
    public DateTime  GeneratedAt  { get; set; }
    public SalesOrderRegisterCriteria Criteria { get; set; } = new();

    /// <summary>Newest order first.</summary>
    public List<SalesOrderRegisterItem>  Items  { get; set; } = [];
    public List<SalesOrderRegisterTotal> Totals { get; set; } = [];

    /// <summary>Every order the filter matches, not only those on this page.</summary>
    public int TotalRecords { get; set; }
    public int Page         { get; set; }
    public int PageSize     { get; set; }
    public int TotalPages   { get; set; }
}
