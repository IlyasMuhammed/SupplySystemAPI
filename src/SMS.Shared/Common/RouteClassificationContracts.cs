namespace SMS.Shared.Common;

// A34 — Route Classification, Lead Time & Production → Delivery
// (docs/route-classification/ADDENDUM-34-ANALYSIS.md §4.1, API-CONTRACT.md §9). Owner: LOG; changes go to LOG by message.
//
// Module references stay as they are (Demand → Inventory; Material → Inventory, Demand; Logistics → Demand, Material,
// Warehouse). Every other direction goes through the interfaces below:
//   Inventory implements  ILeadTimeCalculator                                   (C4)
//   Material  implements  IBomStructureReader, IManufacturingReadiness,         (C4, C5 gate)
//                         ISaleOrderProductionService,                          (C5 make-to-order)
//                         IFulfillmentRouteUsage ("open production orders")     (D-8, the existing A33 interface)
//   Logistics implements  IProductionDeliveryCreator                            (C6, D-29)
//   Demand    implements  ISaleOrderProductionFeedback                          (C6 outcome → SO line)
//   Suppliers implements  ISupplierLeadTimeLookup                               (D-11 tier 4, optional)
//
// Every method takes the organization explicitly and treats other organizations' rows as absent: the EF tenant filter
// is off for super admins and in Hangfire, so "the caller's tenant" is never a safe default (R-11, R-12). Every
// interface is optional-safe: a host that registers no implementation has the feature switched off, and callers must
// resolve these through IEnumerable<T> / IServiceProvider.GetService<T>() or an optional constructor parameter.

/// <summary>
/// A34 C1 — what a fulfillment route is for (BR-C1-01). Stored on <c>logistics.fulfillment_routes.RouteCategory</c>
/// (CHECK: one of <see cref="All"/>, default STOCK). Only <see cref="Active"/> categories can be chosen today; BUY and
/// DROPSHIP are reserved by the CHECK constraint but refused by the service (D-7). A29 line sourcing <c>DROP_SHIP</c>
/// is unrelated and stays route-exempt.
/// </summary>
public static class FulfillmentRouteCategory
{
    /// <summary>Ship from stock (A33 behaviour): reserve at confirm, deliver at confirm.</summary>
    public const string Stock       = "STOCK";
    /// <summary>Make to order (D-1): no reservation at confirm, one production order per line, delivery when it completes.</summary>
    public const string Manufacture = "MANUFACTURE";
    /// <summary>Reserved (D-7): refused with 400 "not yet available".</summary>
    public const string Buy         = "BUY";
    /// <summary>Reserved (D-7): refused with 400 "not yet available".</summary>
    public const string DropShip    = "DROPSHIP";

    /// <summary>Every code the database accepts (the CHECK constraint).</summary>
    public static readonly IReadOnlyList<string> All    = [Stock, Manufacture, Buy, DropShip];
    /// <summary>The codes a route can be created with or changed to today.</summary>
    public static readonly IReadOnlyList<string> Active = [Stock, Manufacture];

    public static bool IsKnown(string? code)  => code is not null && All.Contains(code, StringComparer.Ordinal);
    public static bool IsActive(string? code) => code is not null && Active.Contains(code, StringComparer.Ordinal);
    public static bool IsManufacture(string? code) => string.Equals(code, Manufacture, StringComparison.Ordinal);

    public static string Label(string? code) => code switch
    {
        Stock       => "Stock",
        Manufacture => "Manufacture",
        Buy         => "Buy",
        DropShip    => "Drop ship",
        _           => code ?? string.Empty
    };
}

/// <summary>
/// A34 D-23 — notification <c>Type</c> strings (single recipient each, the A30 pattern). <c>PROD_CREATED</c> already
/// exists in Material; the rest are new.
/// </summary>
public static class RouteClassificationNotificationTypes
{
    /// <summary>Existing (Material). A production order was created; to the creator's supervisor.</summary>
    public const string ProductionCreated      = "PROD_CREATED";
    /// <summary>A delivery was created from a completed make-to-order production order; to the SO creator.</summary>
    public const string DeliveryFromProduction = "SO_DELIVERY_FROM_PRODUCTION";
    /// <summary>Accepted quantity below the line quantity; to the SO creator and the PO creator.</summary>
    public const string ProductionShortfall    = "PROD_SHORTFALL";
    /// <summary>QI accepted nothing on a make-to-order PO; to the PO creator's supervisor and the SO creator.</summary>
    public const string ProductionZeroYield    = "PROD_ZERO_YIELD";
    /// <summary>Production orders could not be created after confirm; to the confirming user.</summary>
    public const string ProductionFailed       = "SO_PRODUCTION_FAILED";
}

// ── C4 — lead time calculator (Inventory implements) ─────────────────────────────────────────────────

/// <summary>A34 C4 — the component codes of a lead-time breakdown, in display order.</summary>
public static class LeadTimeComponentCode
{
    public const string Supplier      = "SUPPLIER";
    public const string Manufacturing = "MANUFACTURING";
    public const string MfgBuffer     = "MFG_BUFFER";
    public const string Qc            = "QC";
    public const string Transfer      = "TRANSFER";
    public const string PickPack      = "PICK_PACK";
    public const string Shipping      = "SHIPPING";
    public const string SalesBuffer   = "SALES_BUFFER";

    public static readonly IReadOnlyList<string> All =
        [Supplier, Manufacturing, MfgBuffer, Qc, Transfer, PickPack, Shipping, SalesBuffer];
}

/// <summary>A34 C3/C4 — where a component's days came from (the "source" badge).</summary>
public static class LeadTimeSource
{
    /// <summary>The variant's own override.</summary>
    public const string Variant        = "VARIANT";
    /// <summary>The organization's LeadTimeDefaults row (or the system defaults when the org has none, D-10).</summary>
    public const string OrgDefault     = "ORG_DEFAULT";
    /// <summary>A VariantSupplier row (preferred, else the default supplier's) (D-11).</summary>
    public const string SupplierRate   = "SUPPLIER_RATE";
    /// <summary>The supplier's BusinessPartner.LeadTimeDays (D-11 tier 4, through <see cref="ISupplierLeadTimeLookup"/>).</summary>
    public const string SupplierRecord = "SUPPLIER_RECORD";
    /// <summary>Product.LeadTimeDays (D-11 / D-12 fallback).</summary>
    public const string Product        = "PRODUCT";
    /// <summary>BOM-aware manufacturing total (D-12/D-13).</summary>
    public const string Bom            = "BOM";
    /// <summary>Enough free stock: the supplier lead does not count (D-14).</summary>
    public const string InStock        = "IN_STOCK";
    /// <summary>Nothing configured anywhere: the built-in value (0, or 1 manufacturing day per level).</summary>
    public const string SystemDefault  = "SYSTEM_DEFAULT";
}

/// <param name="Quantity">The line quantity; stock-aware tiers compare free stock against it (D-14).</param>
/// <param name="RouteUuid">The route to calculate for; null = the variant's route, else the org's SHIP default.</param>
/// <param name="RequestedDate">Date-only. When given, the result carries <c>LatestStartDate</c> and <c>MeetsRequestedDate</c>.</param>
public sealed record LeadTimeRequest(Guid VariantUuid, decimal Quantity, Guid? RouteUuid = null, DateTime? RequestedDate = null);

/// <param name="Code">One of <see cref="LeadTimeComponentCode"/>.</param>
/// <param name="Source">One of <see cref="LeadTimeSource"/>.</param>
/// <param name="Detail">Free text for the popover, e.g. "Supplier ACME, preferred" or "BOM-0012 v2, 2 levels; cycle at X".</param>
public sealed record LeadTimeComponentResult(string Code, string Name, int Days, string Source, string? Detail = null);

/// <param name="EarliestDeliveryDate">Date-only: UTC today + <paramref name="TotalLeadTimeDays"/> (calendar days).</param>
/// <param name="LatestStartDate">Date-only: requested − total; null without a requested date.</param>
/// <param name="RouteCategory">One of <see cref="FulfillmentRouteCategory"/>; STOCK when no route resolved.</param>
public sealed record LeadTimeResult(
    int                                    TotalLeadTimeDays,
    DateTime                               EarliestDeliveryDate,
    DateTime?                              LatestStartDate,
    bool?                                  MeetsRequestedDate,
    Guid?                                  RouteUuid,
    string?                                RouteCode,
    string                                 RouteCategory,
    IReadOnlyList<LeadTimeComponentResult> Components,
    DateTime                               CalculatedAt);

/// <summary>
/// A34 C4 — per-line lead time (BR-C4-01..06). Implemented in Inventory; used by Demand (line ⏱ endpoints, D-19 planned
/// dates). D-27 cache (REV-03): only the date-independent part (resolved route, total, components) is cached, 5 minutes,
/// keyed by (org, variant, <b>resolved</b> route uuid, exact quantity, UTC date); <c>EarliestDeliveryDate</c>,
/// <c>LatestStartDate</c> and <c>MeetsRequestedDate</c> are computed per call from the request.
/// </summary>
public interface ILeadTimeCalculator
{
    /// <exception cref="SMS.Shared.Exceptions.NotFoundException">The variant is not the organization's.</exception>
    /// <exception cref="SMS.Shared.Exceptions.BadRequestException">
    /// Quantity ≤ 0, or <see cref="LeadTimeRequest.RouteUuid"/> is given but is not a route of the organization
    /// ("Route not found in your organization.", as A33 BR-C3-01).
    /// </exception>
    Task<LeadTimeResult> CalculateAsync(Guid organizationId, LeadTimeRequest request, CancellationToken ct = default);
}

// ── C4 / C5 gate — BOM structure and manufacturing readiness (Material implements) ─────────────────────

/// <summary>One BOM input line. Quantities are per <see cref="BomStructure.BaseQuantity"/> of the output.</summary>
public sealed record BomInput(Guid MaterialVariantUuid, Guid MaterialProductUuid, decimal Quantity, decimal ScrapPercentage);

/// <summary>The active, effective BOM of one variant (variant-specific, else product-general) — the PO-create rule.</summary>
public sealed record BomStructure(Guid BomUuid, string BomNumber, int Version, decimal BaseQuantity, IReadOnlyList<BomInput> Inputs);

/// <summary>
/// A34 C4 — reads active BOMs for the calculator's recursion (D-13), batched per level. Uses the same active-BOM rule
/// as production order creation (Material's ActiveBomResolver), so they can never disagree.
/// </summary>
public interface IBomStructureReader
{
    /// <summary>Variants of <paramref name="organizationId"/> with an active, effective BOM; others are absent.</summary>
    Task<IReadOnlyDictionary<Guid, BomStructure>> GetActiveBomsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default);
}

/// <param name="DisplayName">"Product — Variant", for blocker messages.</param>
/// <param name="IsManufactured">The product's SupplyMethod is MANUFACTURE (else NOT_MANUFACTURED).</param>
/// <param name="HasActiveBom">An active, effective BOM exists (else BOM_MISSING).</param>
/// <param name="HasProductionWarehouse">Product.DefaultProductionWarehouseId is set (else PRODUCTION_WAREHOUSE_MISSING).</param>
public sealed record ManufacturingReadinessInfo(
    Guid   VariantUuid,
    string DisplayName,
    bool   IsManufactured,
    bool   HasActiveBom,
    bool   HasProductionWarehouse);

/// <summary>
/// A34 D-5 — the confirm gate's manufacturing checks, one batched call per confirm / preview / detail. The tenant
/// feature check (MANUFACTURING_DISABLED) is the caller's. Variants not of the organization are absent.
/// </summary>
public interface IManufacturingReadiness
{
    Task<IReadOnlyDictionary<Guid, ManufacturingReadinessInfo>> CheckAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default);
}

// ── C5 — make-to-order production (Material implements; A30's IProductionDemandService stays untouched) ──

/// <param name="RequiredDate">Date-only (D-19).</param>
/// <param name="PlannedStartDate">Date-only (D-19); null = the repository's default.</param>
public sealed record SaleOrderProductionLine(
    Guid      SoLineUuid,
    Guid      VariantUuid,
    decimal   Quantity,
    Guid      FulfillmentRouteUuid,
    DateTime  RequiredDate,
    DateTime? PlannedStartDate);

/// <param name="TraceId">The sale order's trace id (A30-P5-07).</param>
/// <param name="Priority">The order's priority, also used for the allocation demand (R-9).</param>
public sealed record SaleOrderProductionRequest(
    Guid                                   SaleOrderUuid,
    string                                 SoNumber,
    Guid                                   TraceId,
    int                                    Priority,
    IReadOnlyList<SaleOrderProductionLine> Lines);

/// <summary>A production order raised for a sale order, as Demand and the SO detail see it (D-25).</summary>
/// <param name="Status">The production order status code (DRAFT … COMPLETED, CANCELLED).</param>
/// <param name="FulfillmentRouteUuid">Set only on make-to-order POs (D-18); null for A30 make-to-shortage POs.</param>
/// <param name="DeliveryOrderUuid">The latest delivery created from this PO (C6), if any.</param>
/// <param name="Created">True when this call created it; false when it already existed (idempotent replay).</param>
public sealed record SaleOrderProductionRef(
    Guid    ProductionOrderUuid,
    string  ProductionNumber,
    Guid?   SoLineUuid,
    string  Status,
    decimal PlannedQuantity,
    decimal AcceptedQuantity,
    Guid?   FulfillmentRouteUuid,
    Guid?   DeliveryOrderUuid,
    string? DeliveryNumber,
    bool    Created);

/// <param name="Cancelled">POs this call cancelled (DRAFT / PLANNED / MATERIAL_PENDING / READY with nothing issued).</param>
/// <param name="KeptRunning">POs left running (IN_PROGRESS or later, or with issued material) — reported, not touched (D-22).</param>
public sealed record SaleOrderProductionCancellation(
    IReadOnlyList<SaleOrderProductionRef> Cancelled,
    IReadOnlyList<SaleOrderProductionRef> KeptRunning);

/// <summary>
/// A34 C5 (D-17, D-18, D-22) — production orders for a sale order. Implemented in Material.
/// <para>
/// <b>Locking.</b> <see cref="CreateDraftsAsync"/> and <see cref="CancelForSaleOrderAsync"/> are called by Demand from
/// inside <see cref="SaleOrderLocks.HoldsResource"/>; they must not take that lock themselves.
/// <see cref="PlanDraftsAsync"/> is slow and cascades, so Demand calls it <b>after</b> the lock is released.
/// </para>
/// </summary>
public interface ISaleOrderProductionService
{
    /// <summary>
    /// One DRAFT production order per line, with SourceType SALES_ORDER and <c>FulfillmentRouteUuid</c> set.
    /// Idempotent per SO line: a non-cancelled PO for (SALES_ORDER, order, line) that carries a route is returned with
    /// <c>Created = false</c>.
    /// </summary>
    Task<IReadOnlyList<SaleOrderProductionRef>> CreateDraftsAsync(
        Guid organizationId, SaleOrderProductionRequest request, int userId, CancellationToken ct = default);

    /// <summary>Plans each order (BOM explosion, PMRs, supply requirements, A30 notifications). Skips non-DRAFT orders.</summary>
    Task PlanDraftsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> productionOrderUuids, int userId, CancellationToken ct = default);

    /// <summary>
    /// D-22 — every SALES_ORDER-sourced PO of the order (make-to-order and A30 make-to-shortage alike). Idempotent:
    /// already-cancelled POs are neither cancelled again nor reported.
    /// </summary>
    Task<SaleOrderProductionCancellation> CancelForSaleOrderAsync(
        Guid organizationId, Guid saleOrderUuid, string reason, int userId, CancellationToken ct = default);

    /// <summary>D-25 — every SALES_ORDER-sourced PO of the order (all statuses), oldest first. <c>Created</c> is false.</summary>
    Task<IReadOnlyList<SaleOrderProductionRef>> GetForSaleOrderAsync(
        Guid organizationId, Guid saleOrderUuid, CancellationToken ct = default);
}

// ── C6 — production completion → delivery (Logistics implements) ─────────────────────────────────────

/// <param name="RouteUuid">The PO's <c>FulfillmentRouteUuid</c>; its steps and mode are snapshotted even if the route was deactivated since.</param>
/// <param name="AcceptedQuantity">The PO's <c>AcceptedQuantity</c> (D-20).</param>
/// <param name="FallbackWarehouseUuid">Ship-from when the line holds no SALES_ORDER stock: the PO's output warehouse, else its production warehouse (D-29).</param>
public sealed record ProductionDeliveryRequest(
    Guid    ProductionOrderUuid,
    string  ProductionNumber,
    Guid    SaleOrderUuid,
    Guid    SoLineUuid,
    Guid    RouteUuid,
    decimal AcceptedQuantity,
    Guid    FallbackWarehouseUuid);

/// <param name="Delivery">The DRAFT delivery this call created (split per ship-from warehouse → the first; see <paramref name="Deliveries"/>); null when nothing was created.</param>
/// <param name="QuantityCreated">Total quantity put on deliveries by this call; 0 on a replay.</param>
/// <param name="SkippedReason">Why nothing (or less than asked) was created, in words for the timeline; null when the full quantity was created.</param>
/// <param name="LatestDelivery">
/// The most recent non-cancelled delivery made from this production order — the one just created or, on a replay, the
/// earlier one — so the caller can stamp <c>DeliveryOrderUuid/DeliveryNumber</c> even when this call created nothing.
/// </param>
/// <param name="Deliveries">Every delivery this call created (more than one only when the line's holds sit in several warehouses).</param>
public sealed record ProductionDeliveryResult(
    CreatedSaleOrderDelivery?                Delivery,
    decimal                                  QuantityCreated,
    string?                                  SkippedReason,
    SaleOrderDeliveryRef?                    LatestDelivery = null,
    IReadOnlyList<CreatedSaleOrderDelivery>? Deliveries     = null);

/// <summary>
/// A34 C6 (D-20, D-21, D-29) — creates the DRAFT delivery for a completed (or partly accepted) make-to-order
/// production order. Implemented in Logistics; called by Material's FGR hook, its sweep and the "Create delivery now"
/// button.
/// <para>
/// <b>Locking.</b> Like <see cref="ISaleOrderDeliveryCreator"/>: it runs in its own transaction, takes
/// <see cref="SaleOrderLocks.HoldsResource"/> first and only then re-reads the order (CONFIRMED / PARTIALLY_FULFILLED,
/// else it skips with a reason). Callers must hold no transaction or lock when they call it.
/// </para>
/// <para>
/// <b>Idempotent.</b> Quantity = min(accepted − quantity already on non-cancelled deliveries from this PO, the line's
/// outstanding = quantity − fulfilled − in flight). A replay creates 0. Ignores <c>PartialFulfillmentAllowed</c> (D-21).
/// </para>
/// <para>
/// <b>Results, not exceptions (REV-05).</b> Every business outcome — order cancelled or not found, line cancelled,
/// nothing left — comes back as a result with <c>SkippedReason</c>; the caller then clears
/// <c>DeliveryCreationPendingSince</c>. Only an unexpected failure (database, lock timeout) throws, and only that keeps
/// the PO pending for the sweep. <c>organizationId</c> is the production order's own organization, never the caller's
/// tenant (R-11: a super admin may confirm another organization's FGR).
/// </para>
/// </summary>
public interface IProductionDeliveryCreator
{
    Task<ProductionDeliveryResult> CreateForProductionOrderAsync(
        Guid organizationId, ProductionDeliveryRequest request, int userId, CancellationToken ct = default);
}

// ── C6 feedback (Demand implements) ──────────────────────────────────────────────────────────────────

/// <param name="AcceptedQuantity">The PO's <c>AcceptedQuantity</c> (received through FGRs so far); final only when <paramref name="Completed"/>.</param>
/// <param name="ZeroYield">True when raised from a QI that accepted nothing (D-21); then no delivery exists.</param>
/// <param name="DeliveryNumber">Set only when <b>this</b> call created a delivery (QuantityCreated &gt; 0); null on a replay.</param>
/// <param name="Completed">
/// REV-02 — the PO is COMPLETED, so <paramref name="AcceptedQuantity"/> is final. A shortfall is recorded only when this
/// or <paramref name="ZeroYield"/> is true; an early "Create delivery now" mid-FGR sends false.
/// </param>
public sealed record ProductionOutcome(
    Guid    SaleOrderUuid,
    Guid    SoLineUuid,
    Guid    ProductionOrderUuid,
    string  ProductionNumber,
    decimal PlannedQuantity,
    decimal AcceptedQuantity,
    string? DeliveryNumber,
    bool    ZeroYield,
    bool    Completed = false);

/// <summary>
/// A34 C6 (D-21, D-23) — records a production outcome on the sale order line. Implemented in Demand. Must be called with
/// no transaction or lock held; never throws for a missing / other-org order (no-op). Idempotent (REV-02):
/// <list type="bullet">
/// <item><c>ProductionShortfallQty</c> is written only when <c>Completed</c> or <c>ZeroYield</c> (accepted &lt; line
/// quantity → the difference; zero yield → the line quantity), and the timeline entry and <c>PROD_SHORTFALL</c> /
/// <c>PROD_ZERO_YIELD</c> go out only when that value changes;</item>
/// <item><c>SO_DELIVERY_FROM_PRODUCTION</c> (timeline + notification) only when <c>DeliveryNumber</c> is set.</item>
/// </list>
/// The organization passed is the production order's own <c>OrganizationId</c>, never the caller's tenant (R-11).
/// </summary>
public interface ISaleOrderProductionFeedback
{
    Task RecordOutcomeAsync(Guid organizationId, ProductionOutcome outcome, int userId, CancellationToken ct = default);
}

// ── D-11 tier 4 (Suppliers implements; optional) ─────────────────────────────────────────────────────

/// <summary>A34 D-11 — <c>BusinessPartner.LeadTimeDays</c> per supplier uuid; suppliers with none, or of another org, are absent.</summary>
public interface ISupplierLeadTimeLookup
{
    Task<IReadOnlyDictionary<Guid, int>> GetAsync(
        Guid organizationId, IReadOnlyCollection<Guid> supplierUuids, CancellationToken ct = default);
}

// D-8 — Material also implements the existing A33 IFulfillmentRouteUsage (FulfillmentRouteContracts.cs), counted as
// "open production orders": non-cancelled, not COMPLETED/CLOSED production orders of the organization whose
// FulfillmentRouteUuid is the route. Logistics already injects IEnumerable<IFulfillmentRouteUsage>.
