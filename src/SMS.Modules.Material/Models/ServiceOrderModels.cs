namespace SMS.Modules.Material.Models;

// A36 — API-CONTRACT.md §3. Dates travel as 'yyyy-MM-dd' and times as 'HH:mm' strings (contract notes 2–3).

public class CreateServiceOrderRequest
{
    public Guid     ServiceProductUuid { get; set; }
    public Guid?    ServiceVariantUuid { get; set; }
    public Guid     CustomerUuid       { get; set; }
    public decimal  Quantity           { get; set; } = 1m;
    public Guid     WarehouseUuid      { get; set; }
    public int?     AssignedUserId     { get; set; }
    public int?     AssignedRoleId     { get; set; }
    public string?  ScheduledDate      { get; set; }
    public string?  ScheduledTime      { get; set; }
    public decimal? EstimatedHours     { get; set; }
    public int?     Priority           { get; set; }
    public string?  Notes              { get; set; }
}

/// <summary>DRAFT/PLANNED: every field (null clears); later only <see cref="Notes"/> is applied (SVC-12).</summary>
public class UpdateServiceOrderRequest
{
    public Guid     CustomerUuid   { get; set; }
    public decimal  Quantity       { get; set; }
    public Guid     WarehouseUuid  { get; set; }
    public int?     AssignedUserId { get; set; }
    public int?     AssignedRoleId { get; set; }
    public string?  ScheduledDate  { get; set; }
    public string?  ScheduledTime  { get; set; }
    public decimal? EstimatedHours { get; set; }
    public int      Priority       { get; set; } = 1;
    public string?  Notes          { get; set; }
    /// <summary>Base64 of the row version the client read.</summary>
    public string?  RowVersion     { get; set; }
}

public class ConsumedMaterialRequest
{
    public Guid    SmrUuid          { get; set; }
    public decimal ConsumedQuantity { get; set; }
}

public class CompleteServiceOrderRequest
{
    public List<ConsumedMaterialRequest>? ConsumedMaterials { get; set; }
    public decimal? ActualHours       { get; set; }
    public string?  CompletionNotes   { get; set; }
    public bool     CustomerSignature { get; set; }
}

public class CancelServiceOrderRequest
{
    public string? Reason { get; set; }
}

public class AddAdhocMaterialRequest
{
    public Guid    VariantUuid { get; set; }
    public decimal Quantity    { get; set; }
    public string? Notes       { get; set; }
}

public class ServiceOrderListFilter
{
    /// <summary>Comma-separated statuses.</summary>
    public string?   Status         { get; set; }
    public Guid?     CustomerUuid   { get; set; }
    public int?      AssignedUserId { get; set; }
    public DateTime? FromDate       { get; set; }
    public DateTime? ToDate         { get; set; }
    public int?      Priority       { get; set; }
    public string?   Search         { get; set; }
    public int       Page           { get; set; } = 1;
    public int       PageSize       { get; set; } = 25;
}

public class ServiceOrderListItemModel
{
    public Guid    Uuid               { get; set; }
    public string  ServiceNumber      { get; set; } = string.Empty;
    public string  ServiceProductName { get; set; } = string.Empty;
    public string? ServiceVariantName { get; set; }
    public Guid    CustomerUuid       { get; set; }
    public string  CustomerName       { get; set; } = string.Empty;
    public string? ScheduledDate      { get; set; }
    public string? ScheduledTime      { get; set; }
    public int?    AssignedUserId     { get; set; }
    public string? AssignedUserName   { get; set; }
    public string  Status             { get; set; } = string.Empty;
    public int     Priority           { get; set; }
    public string  MaterialReadiness  { get; set; } = string.Empty;
    public decimal Quantity           { get; set; }
}

public class ServiceOrderDetailModel : ServiceOrderListItemModel
{
    public Guid      ServiceProductUuid { get; set; }
    public Guid      ServiceVariantUuid { get; set; }
    public Guid      WarehouseUuid      { get; set; }
    public string    WarehouseName      { get; set; } = string.Empty;
    public int?      AssignedRoleId     { get; set; }
    public string?   AssignedRoleName   { get; set; }
    public int?      BomId              { get; set; }
    public string?   BomNumber          { get; set; }
    public int?      BomVersion         { get; set; }
    public decimal?  EstimatedHours     { get; set; }
    public decimal?  ActualHours        { get; set; }
    public DateTime? ActualStartDate    { get; set; }
    public DateTime? ActualEndDate      { get; set; }
    public string    SourceType         { get; set; } = string.Empty;
    public Guid?     SourceUuid         { get; set; }
    public Guid?     SourceLineUuid     { get; set; }
    public string?   SourceReference    { get; set; }
    public string    InvoicingPolicy    { get; set; } = string.Empty;
    public string    BillingModel       { get; set; } = string.Empty;
    public string?   CompletionNotes    { get; set; }
    public bool      CustomerSignature  { get; set; }
    public string?   Notes              { get; set; }
    public Guid      TraceId            { get; set; }
    public string    RowVersion         { get; set; } = string.Empty;
    public DateTime  CreatedAt          { get; set; }
    public DateTime  UpdatedAt          { get; set; }
    public List<ServiceMaterialModel>    Materials      { get; set; } = [];
    public List<ServiceLedgerEntryModel> Ledger         { get; set; } = [];
    public List<string>                  AllowedActions { get; set; } = [];
}

public class ServiceMaterialModel
{
    public Guid    Uuid                    { get; set; }
    public Guid    ProductUuid             { get; set; }
    public Guid    VariantUuid             { get; set; }
    public string  ProductName             { get; set; } = string.Empty;
    public string? VariantName             { get; set; }
    public string? Sku                     { get; set; }
    public string  SourceType              { get; set; } = string.Empty;
    public decimal RequiredQuantity        { get; set; }
    public decimal NetQuantity             { get; set; }
    public decimal ScrapAllowance          { get; set; }
    public decimal ReservedQuantity        { get; set; }
    public decimal IssuedQuantity          { get; set; }
    public decimal ConsumedQuantity        { get; set; }
    public decimal ReturnedQuantity        { get; set; }
    public decimal ShortageQuantity        { get; set; }
    public decimal AvailableQuantity       { get; set; }
    public string  Uom                     { get; set; } = string.Empty;
    public bool    IsCritical              { get; set; }
    public bool    IsAdhoc                 { get; set; }
    public string  Status                  { get; set; } = string.Empty;
    public string  RequiredDate            { get; set; } = string.Empty;
    public string? AddedByName             { get; set; }
    public string? Notes                   { get; set; }
    public string? SupplyRequirementNumber { get; set; }
    public string? SupplyRequirementStatus { get; set; }
    public bool    CanRemove               { get; set; }
}

public class ServiceLedgerEntryModel
{
    public int      Id                   { get; set; }
    public string   EntryType            { get; set; } = string.Empty;
    public Guid     ProductUuid          { get; set; }
    public Guid     VariantUuid          { get; set; }
    public string   ProductName          { get; set; } = string.Empty;
    public decimal  Quantity             { get; set; }
    public string   Uom                  { get; set; } = string.Empty;
    public string   WarehouseName        { get; set; } = string.Empty;
    public string   SourceDocumentType   { get; set; } = string.Empty;
    public string   SourceDocumentNumber { get; set; } = string.Empty;
    public string   MovementType         { get; set; } = string.Empty;
    public DateTime TransactionDate      { get; set; }
    public string?  Notes                { get; set; }
}

public class ServiceLedgerNetModel
{
    public Guid    VariantUuid { get; set; }
    public string  ProductName { get; set; } = string.Empty;
    public string  Uom         { get; set; } = string.Empty;
    public decimal NetQuantity { get; set; }
}

public class ServiceLedgerModel
{
    public List<ServiceLedgerEntryModel> Entries      { get; set; } = [];
    public List<ServiceLedgerNetModel>   NetByProduct { get; set; } = [];
}

public class ServiceCompletionRateModel
{
    public int     CompletedThisWeek { get; set; }
    public int     ScheduledThisWeek { get; set; }
    public decimal Percent           { get; set; }
}

public class ServiceDashboardModel
{
    public List<ServiceOrderListItemModel> Today               { get; set; } = [];
    public List<ServiceOrderListItemModel> WaitingForMaterials { get; set; } = [];
    public List<ServiceOrderListItemModel> Mine                { get; set; } = [];
    public ServiceCompletionRateModel      CompletionRate      { get; set; } = new();
}
