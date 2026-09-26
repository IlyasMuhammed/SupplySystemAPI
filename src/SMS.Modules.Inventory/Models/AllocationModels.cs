using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Models;

// A30 §28.8 — request bodies for AllocationsController. Results are the SMS.Shared.Common records
// the engine itself returns.

public class RunAllocationRequest
{
    public Guid  VariantUuid   { get; set; }
    public Guid? WarehouseUuid { get; set; }
}

public class RegisterDemandRequest
{
    public string    DemandType     { get; set; } = string.Empty;
    public Guid      DemandUuid     { get; set; }
    public Guid?     DemandLineUuid { get; set; }
    public string    Reference      { get; set; } = string.Empty;
    public Guid      VariantUuid    { get; set; }
    public Guid?     WarehouseUuid  { get; set; }
    public decimal   RequiredQty    { get; set; }
    public DateTime  RequiredDate   { get; set; }
    public int       Priority       { get; set; } = AllocationPriority.Normal;
    public DateTime? DocumentDate   { get; set; }
    /// <summary>Run the engine for the variant straight after registering. On by default.</summary>
    public bool      Allocate       { get; set; } = true;

    public AllocationDemandRegistration ToRegistration() => new(
        DemandType, DemandUuid, DemandLineUuid, Reference, VariantUuid, WarehouseUuid,
        RequiredQty, RequiredDate, Priority, DocumentDate);
}

public class RegisterSupplyRequest
{
    public string    SupplyType     { get; set; } = string.Empty;
    public Guid      SupplyUuid     { get; set; }
    public Guid?     SupplyLineUuid { get; set; }
    public string    Reference      { get; set; } = string.Empty;
    public Guid      VariantUuid    { get; set; }
    public Guid      WarehouseUuid  { get; set; }
    public decimal   ExpectedQty    { get; set; }
    public DateTime? ExpectedDate   { get; set; }
    /// <summary>Re-plan the variant's demands against it straight away. On by default.</summary>
    public bool      Allocate       { get; set; } = true;

    public AllocationSupplyRegistration ToRegistration() => new(
        SupplyType, SupplyUuid, SupplyLineUuid, Reference, VariantUuid, WarehouseUuid, ExpectedQty, ExpectedDate);
}

public class AllocationReasonRequest
{
    public string Reason { get; set; } = string.Empty;
}

public class ReallocateRequest
{
    public Guid    ToDemandUuid { get; set; }
    public decimal Quantity     { get; set; }
    public string  Reason       { get; set; } = string.Empty;
}

public class SetAllocationRulesRequest
{
    public List<AllocationRuleDefinition> Rules { get; set; } = [];
}
