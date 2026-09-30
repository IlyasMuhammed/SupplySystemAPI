namespace SMS.Shared.Common;

// ── Codes ─────────────────────────────────────────────────────────────────────

/// <summary>Who is asking for stock (A30 §14.2 demand_type).</summary>
public static class AllocationDemandType
{
    public const string SalesOrder         = "SALES_ORDER";
    public const string ProductionMaterial = "PRODUCTION_MATERIAL";
    public const string ServiceOrder       = "SERVICE_ORDER";
    public const string Replenishment      = "REPLENISHMENT";
    public const string Transfer           = "TRANSFER";

    public static readonly IReadOnlyList<string> All =
        [SalesOrder, ProductionMaterial, ServiceOrder, Replenishment, Transfer];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);

    /// <summary>
    /// Tie-break between kinds of demand once priority and dates are equal (A30-P1-08's default:
    /// a customer order before a production order before a transfer, anything else last).
    /// </summary>
    public static int Rank(string demandType) => demandType switch
    {
        SalesOrder         => 1,
        ProductionMaterial => 2,
        Transfer           => 3,
        _                  => 4
    };
}

/// <summary>Where allocated stock comes from (A30 §14.2 supply_type).</summary>
public static class AllocationSupplyType
{
    public const string OnHand          = "ON_HAND";
    public const string PurchaseOrder   = "PURCHASE_ORDER";
    public const string ProductionOrder = "PRODUCTION_ORDER";
    public const string TransferOrder   = "TRANSFER_ORDER";

    /// <summary>The kinds that can be registered as expected supply; on-hand is never registered.</summary>
    public static readonly IReadOnlyList<string> Incoming = [PurchaseOrder, ProductionOrder, TransferOrder];

    public static bool IsIncoming(string? code) => code is not null && Incoming.Contains(code);
}

/// <summary>How strongly an allocation binds stock to its demand (A30 §14.5).</summary>
public static class AllocationType
{
    /// <summary>Against expected supply that has not arrived. Re-planned by every engine run.</summary>
    public const string Planned  = "PLANNED";
    /// <summary>Tentative on-hand assignment the engine may move. Not produced by the engine today; kept for the API.</summary>
    public const string Soft     = "SOFT";
    /// <summary>Committed by a person; only ALLOCATION_ADMIN moves it.</summary>
    public const string Firm     = "FIRM";
    /// <summary>Physically held through the stock reservation ledger; only ALLOCATION_ADMIN moves it.</summary>
    public const string Reserved = "RESERVED";
}

public static class AllocationStatus
{
    public const string Active    = "ACTIVE";
    public const string Consumed  = "CONSUMED";
    public const string Released  = "RELEASED";
    public const string Cancelled = "CANCELLED";
}

public static class AllocationDemandStatus
{
    public const string Open      = "OPEN";
    public const string Fulfilled = "FULFILLED";
    public const string Cancelled = "CANCELLED";
}

public static class AllocationSupplyStatus
{
    public const string Open      = "OPEN";
    public const string Received  = "RECEIVED";
    public const string Cancelled = "CANCELLED";
}

/// <summary>Business priority of a demand; higher wins (A30 §14.4 rule 1).</summary>
public static class AllocationPriority
{
    public const int Low    = 0;
    public const int Normal = 1;
    public const int High   = 2;
    public const int Urgent = 3;

    public static bool IsKnown(int value) => value is >= Low and <= Urgent;
}

/// <summary>What an allocation rule sorts competing demands by (A30 §14.3).</summary>
public static class AllocationSortField
{
    public const string Priority       = "PRIORITY";
    public const string RequiredDate   = "REQUIRED_DATE";
    public const string DemandTypeRank = "DEMAND_TYPE_RANK";
    public const string DocumentDate   = "DOCUMENT_DATE";
    public const string CreatedAt      = "CREATED_AT";

    public static readonly IReadOnlyList<string> All = [Priority, RequiredDate, DemandTypeRank, DocumentDate, CreatedAt];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}

public static class AllocationSortDirection
{
    public const string Ascending  = "ASC";
    public const string Descending = "DESC";
}

// ── Inputs ────────────────────────────────────────────────────────────────────

/// <param name="DemandUuid">The demanding document (sale order, production order…), by its own UUID.</param>
/// <param name="DemandLineUuid">The line on it, when the document has lines.</param>
/// <param name="Reference">Its number, for people: "SO-2026-00008", "PROD-2026-00001".</param>
/// <param name="WarehouseUuid">Where the stock must come from; null lets the engine choose.</param>
/// <param name="DocumentDate">When the demanding document was raised — the FIFO date. Defaults to now.</param>
public sealed record AllocationDemandRegistration(
    string    DemandType,
    Guid      DemandUuid,
    Guid?     DemandLineUuid,
    string    Reference,
    Guid      VariantUuid,
    Guid?     WarehouseUuid,
    decimal   RequiredQty,
    DateTime  RequiredDate,
    int       Priority = AllocationPriority.Normal,
    DateTime? DocumentDate = null);

/// <param name="SupplyUuid">The supplying document (purchase order line, production order…), by its own UUID.</param>
public sealed record AllocationSupplyRegistration(
    string    SupplyType,
    Guid      SupplyUuid,
    Guid?     SupplyLineUuid,
    string    Reference,
    Guid      VariantUuid,
    Guid      WarehouseUuid,
    decimal   ExpectedQty,
    DateTime? ExpectedDate);

public sealed record AllocationRuleDefinition(
    string  RuleName,
    int     PriorityOrder,
    string? DemandTypeFilter,
    string  SortField,
    string  SortDirection,
    bool    IsActive);

public sealed record AllocationListFilter(
    Guid?   VariantUuid   = null,
    Guid?   WarehouseUuid = null,
    string? DemandType    = null,
    Guid?   DemandUuid    = null,
    string? Status        = null,
    int     Page          = 1,
    int     PageSize      = 50);

// ── Outputs ───────────────────────────────────────────────────────────────────

/// <param name="Uuid">The demand's registry id — what every other engine call refers to it by.</param>
/// <param name="Shortage">Required, less consumed, reserved and planned: what nothing yet covers.</param>
public sealed record DemandAllocationSummary(
    Guid     Uuid,
    string   DemandType,
    Guid     DemandUuid,
    Guid?    DemandLineUuid,
    string   Reference,
    Guid     VariantUuid,
    Guid?    WarehouseUuid,
    decimal  RequiredQty,
    decimal  ReservedQty,
    decimal  PlannedQty,
    decimal  ConsumedQty,
    decimal  Shortage,
    DateTime RequiredDate,
    int      Priority,
    string   Status)
{
    // Init-only, not positional — GetDemandsAsync fills these in for its own cross-variant
    // listing (the allocation dashboard's "every open demand" view); every other caller of this
    // record (a single run's own results, a per-variant lookup already showing the variant
    // elsewhere on screen) leaves them null rather than pay for a join it doesn't need.
    public Guid?   ProductUuid { get; init; }
    public string? ProductName { get; init; }
    public string? VariantName { get; init; }
    public string? VariantSku  { get; init; }
}

public sealed record AllocationSummary(
    Guid      Uuid,
    Guid      DemandRegistryUuid,
    string    DemandType,
    Guid      DemandUuid,
    Guid?     DemandLineUuid,
    string    DemandReference,
    Guid      VariantUuid,
    Guid      WarehouseUuid,
    decimal   AllocatedQty,
    decimal   ConsumedQty,
    string    SupplyType,
    Guid?     SupplyUuid,
    string?   SupplyReference,
    string    AllocationType,
    int       PriorityScore,
    DateTime  RequiredDate,
    string    Status,
    DateTime  AllocatedAt,
    DateTime? ReleasedAt,
    string?   ReleaseReason);

public sealed record AllocationPage(IReadOnlyList<AllocationSummary> Items, int Total, int Page, int PageSize);

/// <param name="DemandsEvaluated">Open demands for the variant the run considered.</param>
/// <param name="QuantityReserved">Held from on-hand stock by this run.</param>
/// <param name="QuantityPlanned">Assigned to expected supply by this run.</param>
/// <param name="Shortage">What no on-hand or expected supply covers across those demands.</param>
public sealed record AllocationRunResult(
    Guid    VariantUuid,
    Guid?   WarehouseUuid,
    int     DemandsEvaluated,
    decimal QuantityReserved,
    decimal QuantityPlanned,
    decimal Shortage,
    IReadOnlyList<DemandAllocationSummary> Demands);

/// <param name="Available">On hand less reserved, summed over the rows in scope.</param>
/// <param name="Incoming">Expected supply registered and not yet received.</param>
/// <param name="OpenDemand">Required less consumed, over open demands.</param>
/// <param name="Unallocated">Open demand that neither a hold nor expected supply covers.</param>
public sealed record AvailabilityResult(
    Guid    VariantUuid,
    Guid?   WarehouseUuid,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    decimal Incoming,
    decimal OpenDemand,
    decimal Unallocated);

/// <summary>
/// Told after every allocation run has committed, with what each evaluated demand now holds.
/// Registered by the modules whose documents are the demands (production material requirements,
/// sale order lines), so a receipt that changes who holds what reaches the document without the
/// engine knowing what a production order or a sale order is. Implementations must not call the
/// engine back from here.
/// </summary>
public interface IAllocationRunListener
{
    /// <param name="userId">Whoever's action triggered the run — a listener that itself needs an
    /// actor for its own writes (e.g. re-parenting a hold, which <see cref="IStockReservationService"/>
    /// stamps) uses this rather than inventing a system user id.</param>
    Task OnAllocationRunAsync(AllocationRunResult result, int userId, CancellationToken ct = default);
}

/// <summary>
/// A31 C10 §12.2 Step 1 — told after a receipt (or cancellation) against expected supply commits,
/// independent of whether or when allocation is next run for the variant. Booking the receipt and
/// running allocation are now two separate actions (a receipt no longer runs the engine by itself),
/// but bookkeeping that only depends on the receipt itself — a supply requirement's received
/// quantity and fulfilment status, for one — must not wait for someone to run allocation.
/// </summary>
public interface IAllocationReceiptListener
{
    Task OnSupplyClosedAsync(Guid variantUuid, CancellationToken ct = default);
}

// ── The engine ────────────────────────────────────────────────────────────────

/// <summary>
/// Assigns stock, on hand and expected, to the demands competing for it (A30 §14). Shared across
/// sales, production and whatever comes next: a demand is registered here, the engine decides
/// who gets what by the organization's priority rules, and the record of that decision lives in
/// <c>inventory.AllocationRecords</c>. Implemented in SMS.Modules.Inventory; resolved through DI,
/// so a caller in another module needs no project reference to it.
/// <para>
/// Reserved allocations are real holds through <see cref="IStockReservationService"/> (source type
/// <see cref="ReservationSourceType.Allocation"/>, source uuid = the demand's registry uuid), so
/// availability everywhere else already accounts for them. Planned allocations are the engine's
/// own bookkeeping against supply that has not arrived, and every run re-plans them from scratch;
/// a reserved or firm allocation is never moved by a run, only by
/// <see cref="ReallocateAsync"/>.
/// </para>
/// </summary>
public interface IAllocationEngine
{
    // Demands
    /// <summary>Registers, or updates when the same demand and line are already open. Does not allocate.</summary>
    Task<DemandAllocationSummary> RegisterDemandAsync(AllocationDemandRegistration demand, int userId, CancellationToken ct = default);
    Task<DemandAllocationSummary?> GetDemandAsync(Guid demandRegistryUuid, CancellationToken ct = default);
    Task<IReadOnlyList<DemandAllocationSummary>> GetDemandsAsync(
        Guid? variantUuid = null, string? demandType = null, Guid? demandUuid = null, bool openOnly = true,
        Guid? warehouseUuid = null, CancellationToken ct = default);
    /// <summary>Releases every allocation the demand holds and closes it. Idempotent.</summary>
    Task CancelDemandAsync(Guid demandRegistryUuid, string reason, int userId, CancellationToken ct = default);

    // Expected supply
    /// <summary>Registers, or updates when the same supply and line are already open.</summary>
    Task<Guid> RegisterSupplyAsync(AllocationSupplyRegistration supply, CancellationToken ct = default);
    /// <summary>
    /// Books a receipt against registered supply and drops the planned allocations that leaned on
    /// it, so the next run can hold the stock that has now arrived. Returns false when nothing was
    /// registered for that supply, which is not an error: stock that arrives unannounced is still
    /// stock. The caller runs <see cref="AllocateAsync"/> afterwards either way.
    /// </summary>
    Task<bool> SupplyReceivedAsync(string supplyType, Guid supplyUuid, Guid? supplyLineUuid, decimal receivedQty, CancellationToken ct = default);
    Task<bool> CancelSupplyAsync(string supplyType, Guid supplyUuid, Guid? supplyLineUuid, CancellationToken ct = default);

    // Allocation
    /// <summary>
    /// Evaluates every open demand for the variant (in one warehouse, or all) by the priority
    /// rules: holds on-hand stock for the highest first, plans the rest against expected supply,
    /// reports what is still short.
    /// </summary>
    Task<AllocationRunResult> AllocateAsync(Guid variantUuid, Guid? warehouseUuid, int userId, CancellationToken ct = default);
    /// <summary>The same run, scoped by what one demand is for. Its competitors are evaluated too.</summary>
    Task<AllocationRunResult> AllocateForDemandAsync(Guid demandRegistryUuid, int userId, CancellationToken ct = default);
    Task ReleaseAsync(Guid allocationUuid, string reason, int userId, CancellationToken ct = default);
    /// <summary>Moves part or all of one demand's allocation to another demand for the same variant.</summary>
    Task<AllocationSummary> ReallocateAsync(Guid fromAllocationUuid, Guid toDemandRegistryUuid, decimal quantity, string reason, int userId, CancellationToken ct = default);
    /// <summary>Marks allocated stock as used — issued, delivered — and returns how much was.</summary>
    Task<decimal> ConsumeAsync(Guid allocationUuid, decimal quantity, int userId, CancellationToken ct = default);

    // Reading
    Task<AvailabilityResult> GetAvailabilityAsync(Guid variantUuid, Guid? warehouseUuid, CancellationToken ct = default);
    Task<AllocationSummary?> GetAllocationAsync(Guid allocationUuid, CancellationToken ct = default);
    Task<AllocationPage> GetAllocationsAsync(AllocationListFilter filter, CancellationToken ct = default);

    // Rules
    /// <summary>The organization's rules, or the built-in defaults when it has set none.</summary>
    Task<IReadOnlyList<AllocationRuleDefinition>> GetRulesAsync(CancellationToken ct = default);
    /// <summary>Replaces the organization's rules. An empty list returns it to the defaults.</summary>
    Task<IReadOnlyList<AllocationRuleDefinition>> SetRulesAsync(IReadOnlyList<AllocationRuleDefinition> rules, CancellationToken ct = default);
}
