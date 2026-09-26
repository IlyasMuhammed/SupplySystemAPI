using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Domain;

/// <summary>
/// A registered call on stock (A30 §14): a sale order line, a production material requirement…
/// The engine allocates to these, never to the source documents directly, so every kind of demand
/// competes on the same footing.
/// </summary>
internal class AllocationDemand : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    public string   DemandType     { get; set; } = string.Empty;
    public Guid     DemandUuid     { get; set; }
    public Guid?    DemandLineUuid { get; set; }
    public string   Reference      { get; set; } = string.Empty;
    public Guid     VariantUuid    { get; set; }
    /// <summary>Null: any warehouse will do, the engine picks the one with the most free stock.</summary>
    public Guid?    WarehouseUuid  { get; set; }
    public decimal  RequiredQty    { get; set; }
    public decimal  ConsumedQty    { get; set; }
    public DateTime RequiredDate   { get; set; }
    public DateTime DocumentDate   { get; set; }
    public int      Priority       { get; set; } = AllocationPriority.Normal;
    public string   Status         { get; set; } = AllocationDemandStatus.Open;
    public int      CreatedBy      { get; set; }
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt      { get; set; } = DateTime.UtcNow;
    public byte[]   RowVersion     { get; set; } = [];

    public ICollection<AllocationRecord> Allocations { get; set; } = new List<AllocationRecord>();
}

/// <summary>Stock that is on its way: a purchase order line, a production order's expected output.</summary>
internal class AllocationSupply : ITenantScopedEntity
{
    public int       Id             { get; set; }
    public Guid      Uuid           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    public string    SupplyType     { get; set; } = string.Empty;
    public Guid      SupplyUuid     { get; set; }
    public Guid?     SupplyLineUuid { get; set; }
    public string    Reference      { get; set; } = string.Empty;
    public Guid      VariantUuid    { get; set; }
    public Guid      WarehouseUuid  { get; set; }
    public decimal   ExpectedQty    { get; set; }
    public decimal   ReceivedQty    { get; set; }
    public DateTime? ExpectedDate   { get; set; }
    public string    Status         { get; set; } = AllocationSupplyStatus.Open;
    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt      { get; set; } = DateTime.UtcNow;
    public byte[]    RowVersion     { get; set; } = [];

    public ICollection<AllocationRecord> Allocations { get; set; } = new List<AllocationRecord>();
}

/// <summary>
/// One decision of the engine: this much of that supply belongs to this demand (A30 §14.2).
/// Inventory rows never point at a demand; these records do, so who owns what can change without
/// touching the stock.
/// </summary>
internal class AllocationRecord : ITenantScopedEntity
{
    public int       Id             { get; set; }
    public Guid      Uuid           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    public int       DemandId       { get; set; }
    public Guid      VariantUuid    { get; set; }
    public Guid      WarehouseUuid  { get; set; }
    public decimal   AllocatedQty   { get; set; }
    public decimal   ConsumedQty    { get; set; }
    public string    SupplyType     { get; set; } = AllocationSupplyType.OnHand;
    /// <summary>The registered expected supply a PLANNED allocation leans on; null for on-hand stock.</summary>
    public int?      SupplyId       { get; set; }
    public string    AllocationType { get; set; } = SMS.Shared.Common.AllocationType.Reserved;
    /// <summary>The demand's position in the priority order when this was decided; 1 is first.</summary>
    public int       PriorityScore  { get; set; }
    public DateTime  RequiredDate   { get; set; }
    public string    Status         { get; set; } = AllocationStatus.Active;
    public int       AllocatedBy    { get; set; }
    public DateTime  AllocatedAt    { get; set; } = DateTime.UtcNow;
    public DateTime? ReleasedAt     { get; set; }
    public string?   ReleaseReason  { get; set; }
    public byte[]    RowVersion     { get; set; } = [];

    public AllocationDemand  Demand { get; set; } = null!;
    public AllocationSupply? Supply { get; set; }

    public decimal Remaining => AllocatedQty - ConsumedQty;
}

/// <summary>One step of an organization's priority order (A30 §14.3). None set means the defaults.</summary>
internal class AllocationRule : ITenantScopedEntity
{
    public int      Id               { get; set; }
    public Guid     Uuid             { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId   { get; set; }
    public string   RuleName         { get; set; } = string.Empty;
    public int      PriorityOrder    { get; set; }
    public string?  DemandTypeFilter { get; set; }
    public string   SortField        { get; set; } = string.Empty;
    public string   SortDirection    { get; set; } = AllocationSortDirection.Ascending;
    public bool     IsActive         { get; set; } = true;
    public DateTime CreatedAt        { get; set; } = DateTime.UtcNow;
}
