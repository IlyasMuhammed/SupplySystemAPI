namespace SMS.Modules.Reports.Models;

/// <summary>
/// Everything the home dashboard shows, in one read. Each section is null when the caller may not see that area
/// (the page then leaves it out, instead of showing a zero that only means "not allowed").
/// <para>
/// Counts are by status, as lists rather than dictionaries: status codes are UPPER_SNAKE and a camel-casing key
/// policy would mangle them. Money is per currency — nothing here converts, because nothing here knows the rate.
/// </para>
/// </summary>
public class DashboardSummaryModel
{
    public DateTime GeneratedAt { get; set; }

    public DashboardProcurementModel? Procurement { get; set; }
    public DashboardReceivingModel?   Receiving   { get; set; }
    public DashboardSalesModel?       Sales       { get; set; }
    public DashboardDeliveryModel?    Deliveries  { get; set; }
    public DashboardProductionModel?  Production  { get; set; }
    public DashboardMoneyModel?       Receivables { get; set; }
    public DashboardMoneyModel?       Payables    { get; set; }
    public DashboardMaterialModel?    Material    { get; set; }
    public DashboardInventoryModel?   Inventory   { get; set; }

    /// <summary>Documents waiting on someone's decision, across the areas the caller can see. Only non-zero rows.</summary>
    public List<DashboardAttentionItem> Attention { get; set; } = [];
}

public class StatusCount
{
    public string Status { get; set; } = string.Empty;
    public int    Count  { get; set; }
}

public class CurrencyAmount
{
    public string  Currency { get; set; } = string.Empty;
    public decimal Amount   { get; set; }
    public int     Count    { get; set; }
}

/// <summary>A queue that needs action: a stable key the page maps to a label and a link, and how many are in it.</summary>
public class DashboardAttentionItem
{
    public string Key   { get; set; } = string.Empty;
    public int    Count { get; set; }
}

public class DashboardProcurementModel
{
    public List<StatusCount> PurchaseOrders       { get; set; } = [];
    public List<StatusCount> Requisitions         { get; set; } = [];
    /// <summary>Null when the caller may not see suppliers.</summary>
    public int? ActiveSuppliers { get; set; }
    public int? TotalSuppliers  { get; set; }
}

public class DashboardReceivingModel
{
    public List<StatusCount> Grns { get; set; } = [];
}

public class DashboardSalesModel
{
    /// <summary>Null when the caller may not see that document type.</summary>
    public List<StatusCount>? Inquiries  { get; set; }
    public List<StatusCount>? Quotations { get; set; }
    public List<StatusCount>? SaleOrders { get; set; }
    /// <summary>Confirmed or partly fulfilled orders whose expected delivery date has passed.</summary>
    public int? LateSaleOrders  { get; set; }
    /// <summary>Sale orders taken this calendar month (not cancelled).</summary>
    public int? OrdersThisMonth { get; set; }
    public int? ActiveCustomers { get; set; }
}

public class DashboardDeliveryModel
{
    /// <summary>Outbound and transfer deliveries — what leaves our warehouses.</summary>
    public List<StatusCount> Outbound { get; set; } = [];
    /// <summary>Inbound advance shipping notices still expected.</summary>
    public int InboundExpected { get; set; }
    /// <summary>Outbound deliveries not yet issued or delivered whose promised (else requested) date has passed.</summary>
    public int Late { get; set; }
    /// <summary>Outbound deliveries whose goods left (goods issue posted) this calendar month.</summary>
    public int ShippedThisMonth { get; set; }
}

public class DashboardProductionModel
{
    /// <summary>Null when the caller may not see production orders.</summary>
    public List<StatusCount>? Orders { get; set; }
    /// <summary>Open production orders past their required date.</summary>
    public int? Late { get; set; }
    /// <summary>Production orders created from a sale order line (make-to-order) that are still open.</summary>
    public int? OpenMakeToOrder { get; set; }
    /// <summary>Null when the caller may not see BOMs.</summary>
    public List<StatusCount>? Boms { get; set; }
}

/// <summary>Receivables (sales invoices) or payables (supplier invoices).</summary>
public class DashboardMoneyModel
{
    public List<StatusCount>    Invoices    { get; set; } = [];
    /// <summary>Still owed, per currency.</summary>
    public List<CurrencyAmount> Outstanding { get; set; } = [];
    /// <summary>Still owed and past the due date, per currency.</summary>
    public List<CurrencyAmount> Overdue     { get; set; } = [];
}

public class DashboardMaterialModel
{
    public List<StatusCount> Mirs { get; set; } = [];
}

public class DashboardInventoryModel
{
    public int ActiveProducts       { get; set; }
    public int ManufacturedProducts { get; set; }
}
