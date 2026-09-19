namespace SMS.Modules.Demand.Models;

// A29-P3-03 §3.2/§3.5.

public class SaleOrderConfigModel
{
    public Guid     Uuid                      { get; set; }
    public bool     AutoPoEnabled             { get; set; }
    public string   SupplierSelectionMode     { get; set; } = string.Empty;
    public string   AutoPoApprovalMode        { get; set; } = string.Empty;
    public bool     DropShipEnabled           { get; set; }
    public bool     SelfPickupEnabled         { get; set; }
    public string   DefaultFulfillmentMode    { get; set; } = string.Empty;
    public int      ReservationTtlHours       { get; set; }
    public bool     PartialFulfillmentAllowed { get; set; }
    public bool     EmailIntimationEnabled    { get; set; }
    public int?     IntimationDepartmentId    { get; set; }
    public string?  IntimationCcEmails        { get; set; }
    public bool     ShipmentRequiredDefault   { get; set; }
    public int?      UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class UpdateSaleOrderConfigRequest
{
    public bool     AutoPoEnabled             { get; set; }
    public string   SupplierSelectionMode     { get; set; } = string.Empty;
    public string   AutoPoApprovalMode        { get; set; } = string.Empty;
    public bool     DropShipEnabled           { get; set; }
    public bool     SelfPickupEnabled         { get; set; }
    public string   DefaultFulfillmentMode    { get; set; } = string.Empty;
    public int      ReservationTtlHours       { get; set; }
    public bool     PartialFulfillmentAllowed { get; set; }
    public bool     EmailIntimationEnabled    { get; set; }
    public int?     IntimationDepartmentId    { get; set; }
    public string?  IntimationCcEmails        { get; set; }
    public bool     ShipmentRequiredDefault   { get; set; }
}

public class SaleOrderConfigAuditModel
{
    public int      Id           { get; set; }
    public string   FieldChanged { get; set; } = string.Empty;
    public string?  OldValue     { get; set; }
    public string?  NewValue     { get; set; }
    public int      ChangedBy    { get; set; }
    public string?  ChangedByName { get; set; }
    public DateTime ChangedAt    { get; set; }
}

public class SaleOrderConfigAuditFilter
{
    public int Page     { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
