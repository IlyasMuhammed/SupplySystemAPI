using SMS.Shared.Common;

namespace SMS.Modules.Material.Domain;

// ── Codes (A36 D-5..D-9, FSD §5, §6.6) ────────────────────────────────────────

internal static class ServiceOrderStatus
{
    public const string Draft           = "DRAFT";
    public const string Planned         = "PLANNED";
    public const string MaterialPending = "MATERIAL_PENDING";
    public const string Waiting         = "WAITING";
    public const string Ready           = "READY";
    public const string InProgress      = "IN_PROGRESS";
    public const string Completed       = "COMPLETED";
    public const string Closed          = "CLOSED";
    public const string Cancelled       = "CANCELLED";

    public static readonly IReadOnlyList<string> All =
        [Draft, Planned, MaterialPending, Waiting, Ready, InProgress, Completed, Closed, Cancelled];

    /// <summary>Before the job starts: readiness decides READY vs MATERIAL_PENDING (ST-02..04).</summary>
    public static bool IsAwaitingMaterials(string status) => status is Planned or MaterialPending or Ready;

    /// <summary>While the job runs: readiness decides IN_PROGRESS vs WAITING (ST-06/07).</summary>
    public static bool IsRunning(string status) => status is InProgress or Waiting;

    public static bool IsTerminal(string status) => status is Completed or Closed or Cancelled;

    /// <summary>ST-10 — any non-terminal state.</summary>
    public static bool CanCancel(string status) => !IsTerminal(status);
}

/// <summary>A36 D-5 — production's four readiness codes plus NOT_APPLICABLE (no materials at all).</summary>
internal static class ServiceReadinessCode
{
    public const string NotChecked    = MaterialReadiness.NotChecked;
    public const string Partial       = MaterialReadiness.Partial;
    public const string Ready         = MaterialReadiness.Ready;
    public const string Shortage      = MaterialReadiness.Shortage;
    public const string NotApplicable = "NOT_APPLICABLE";
}

internal static class SmrStatus
{
    public const string Pending           = "PENDING";
    public const string PartiallyReserved = "PARTIALLY_RESERVED";
    public const string FullyReserved     = "FULLY_RESERVED";
    public const string Issued            = "ISSUED";
    public const string Consumed          = "CONSUMED";
    public const string Returned          = "RETURNED";
    public const string Cancelled         = "CANCELLED";

    /// <summary>Settled at completion or cancellation; readiness never rewrites these.</summary>
    public static bool IsFinal(string status) => status is Consumed or Returned or Cancelled;
}

internal static class ServiceIssueType
{
    public const string Issue  = "ISSUE";
    public const string Return = "RETURN";
}

internal static class ServiceOrderSource
{
    public const string Manual     = "MANUAL";
    public const string SalesOrder = ProductionSourceType.SalesOrder;
}

internal static class ServiceDocumentPrefix
{
    public const string ServiceOrder = "SVC";
    /// <summary>Service material issue / return document.</summary>
    public const string ServiceIssue = "SMI";
}

/// <summary>Ledger codes (D-9): debit-only, returns are negative debits.</summary>
internal static class ServiceLedgerCodes
{
    public const string Debit = "DEBIT";
}

// ── Entities ──────────────────────────────────────────────────────────────────

/// <summary>A job delivering a service product to a customer (A36 D-5, FSD §4).</summary>
internal class ServiceOrder : ITenantScopedEntity
{
    public int       Id                 { get; set; }
    public Guid      UUID               { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId     { get; set; }
    public Guid      TraceId            { get; set; } = Guid.NewGuid();
    public string    ServiceNumber      { get; set; } = string.Empty;
    public Guid      ServiceProductUuid { get; set; }
    public Guid      ServiceVariantUuid { get; set; }
    /// <summary>Business partner (customer) UUID, no FK across modules.</summary>
    public Guid      CustomerUuid       { get; set; }
    /// <summary>The service BOM snapshotted at plan; immutable afterwards (SVC-02). Null: no BOM (ad-hoc service).</summary>
    public int?      BomId              { get; set; }
    public int?      BomVersion         { get; set; }
    public decimal   Quantity           { get; set; } = 1m;
    public Guid      WarehouseUuid      { get; set; }
    public int?      AssignedUserId     { get; set; }
    /// <summary>The team, as an auth role id (D-5).</summary>
    public int?      AssignedRoleId     { get; set; }
    public decimal?  EstimatedHours     { get; set; }
    public decimal?  ActualHours        { get; set; }
    public DateTime? ScheduledDate      { get; set; }
    public TimeSpan? ScheduledTime      { get; set; }
    public DateTime? ActualStartDate    { get; set; }
    public DateTime? ActualEndDate      { get; set; }
    public string    SourceType         { get; set; } = ServiceOrderSource.Manual;
    public Guid?     SourceUuid         { get; set; }
    public Guid?     SourceLineUuid     { get; set; }
    public string?   SourceReference    { get; set; }
    public string    InvoicingPolicy    { get; set; } = ServiceInvoicingPolicy.FixedPrice;
    public string    BillingModel       { get; set; } = ServiceBillingModel.Inclusive;
    public int       Priority           { get; set; } = AllocationPriority.Normal;
    public string    Status             { get; set; } = ServiceOrderStatus.Draft;
    public string    MaterialReadiness  { get; set; } = ServiceReadinessCode.NotChecked;
    public string?   CompletionNotes    { get; set; }
    public bool      CustomerSignature  { get; set; }
    public string?   Notes              { get; set; }
    public int       CreatedBy          { get; set; }
    public DateTime  CreatedAt          { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt          { get; set; } = DateTime.UtcNow;
    public byte[]    RowVersion         { get; set; } = [];

    public BillOfMaterial? Bom { get; set; }
    public ICollection<ServiceMaterialRequirement> Materials { get; set; } = new List<ServiceMaterialRequirement>();
}

/// <summary>One thing a service order needs: a stock material, subcontracted labour or internal labour (D-6, FSD §6).</summary>
internal class ServiceMaterialRequirement : ITenantScopedEntity
{
    public int       Id                   { get; set; }
    public Guid      UUID                 { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId       { get; set; }
    public int       ServiceOrderId       { get; set; }
    public int?      BomLineId            { get; set; }
    public int       Sequence             { get; set; }
    /// <summary><see cref="BomLineSourceType"/>; ad-hoc materials are always STOCK.</summary>
    public string    SourceType           { get; set; } = BomLineSourceType.Stock;
    public Guid      MaterialProductUuid  { get; set; }
    public Guid      MaterialVariantUuid  { get; set; }
    public decimal   NetQuantity          { get; set; }
    public decimal   ScrapAllowance       { get; set; }
    public decimal   RequiredQuantity     { get; set; }
    public decimal   ReservedQuantity     { get; set; }
    public decimal   IssuedQuantity       { get; set; }
    public decimal   ConsumedQuantity     { get; set; }
    public decimal   ReturnedQuantity     { get; set; }
    /// <summary>What nothing physically covers yet (stock: outstanding less held; subcontract: all until ordered).</summary>
    public decimal   ShortageQuantity     { get; set; }
    public string    Uom                  { get; set; } = string.Empty;
    public Guid      WarehouseUuid        { get; set; }
    public bool      IsCritical           { get; set; } = true;
    public bool      IsAdhoc              { get; set; }
    public string    Status               { get; set; } = SmrStatus.Pending;
    public DateTime  RequiredDate         { get; set; }
    public int?      AddedBy              { get; set; }
    public string?   Notes                { get; set; }
    /// <summary>A SUBCONTRACT line's vendor (from the BOM line).</summary>
    public Guid?     SubcontractSupplierUuid { get; set; }
    /// <summary>The allocation engine's registry id for a STOCK requirement's demand.</summary>
    public Guid?     AllocationDemandUuid { get; set; }
    public DateTime  CreatedAt            { get; set; } = DateTime.UtcNow;
    public DateTime  UpdatedAt            { get; set; } = DateTime.UtcNow;

    public ServiceOrder ServiceOrder { get; set; } = null!;

    /// <summary>Still to come out of stock. Returns only happen at completion or cancellation.</summary>
    public decimal Outstanding => Math.Max(0m, RequiredQuantity - IssuedQuantity);

    public bool IsStock => SourceType == BomLineSourceType.Stock;
}

/// <summary>Materials moving between stock and a service job (D-7): ISSUE out, RETURN back.</summary>
internal class ServiceMaterialIssue : ITenantScopedEntity
{
    public int       Id             { get; set; }
    public Guid      UUID           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    public string    IssueNumber    { get; set; } = string.Empty;
    public int       ServiceOrderId { get; set; }
    public Guid      WarehouseUuid  { get; set; }
    public string    IssueType      { get; set; } = ServiceIssueType.Issue;
    public string?   Notes          { get; set; }
    public int       CreatedBy      { get; set; }
    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;

    public ServiceOrder ServiceOrder { get; set; } = null!;
    public ICollection<ServiceMaterialIssueLine> Lines { get; set; } = new List<ServiceMaterialIssueLine>();
}

internal class ServiceMaterialIssueLine : ITenantScopedEntity
{
    public int      Id                  { get; set; }
    public Guid     UUID                { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId      { get; set; }
    public int      IssueId             { get; set; }
    public int      RequirementId       { get; set; }
    public Guid     MaterialVariantUuid { get; set; }
    public decimal  Quantity            { get; set; }
    public string   Uom                 { get; set; } = string.Empty;
    public decimal  UnitCost            { get; set; }

    public ServiceMaterialIssue       Issue       { get; set; } = null!;
    public ServiceMaterialRequirement Requirement { get; set; } = null!;
}

/// <summary>
/// A36 D-9 — what a service job consumed, written once in the completion's transaction and never changed
/// (<see cref="Data.MaterialDbContext"/> refuses updates and deletes). Issues are positive debits, returns negative.
/// </summary>
internal class ServiceLedgerEntry : ITenantScopedEntity
{
    public int      Id                   { get; set; }
    public Guid     OrganizationId       { get; set; }
    public Guid     TraceId              { get; set; }
    public int      ServiceOrderId       { get; set; }
    public string   ServiceNumber        { get; set; } = string.Empty;
    public string   EntryType            { get; set; } = ServiceLedgerCodes.Debit;
    public Guid     ProductUuid          { get; set; }
    public Guid     VariantUuid          { get; set; }
    public string   ProductName          { get; set; } = string.Empty;
    public string   ProductType          { get; set; } = string.Empty;
    public decimal  Quantity             { get; set; }
    public string   Uom                  { get; set; } = string.Empty;
    public Guid     WarehouseUuid        { get; set; }
    public string   WarehouseName        { get; set; } = string.Empty;
    /// <summary>SERVICE_ISSUE or SERVICE_RETURN.</summary>
    public string   SourceDocumentType   { get; set; } = string.Empty;
    public Guid     SourceDocumentUuid   { get; set; }
    public string   SourceDocumentNumber { get; set; } = string.Empty;
    public string   MovementType         { get; set; } = string.Empty;
    public DateTime TransactionDate      { get; set; }
    public string?  Notes                { get; set; }
    public DateTime CreatedAt            { get; set; } = DateTime.UtcNow;
}
