namespace SMS.Shared.Common;

// A33 — Fulfillment Routes (docs/fulfillment-routes/ADDENDUM-33-ANALYSIS.md §5.2, API-CONTRACT.md §2).
//
// Routes live in Logistics. Inventory (variant default route), Demand (sale order line override, the confirmation
// gate, the delivery preview) and Logistics itself (deliveries) all refer to a route by its bare UUID, with no FK
// across DbContexts. These contracts are how they talk without project references: Demand cannot reference
// Logistics (Logistics already references Demand), and Inventory references neither.
//
// Every method takes the organization explicitly. The EF tenant filter is off for super admins and for Hangfire
// jobs, so "the caller's tenant" is not a safe default here (R-12, R-13): a route of another organization must
// read as absent, whoever asks.

/// <summary>A33 — the step codes a route is built from, in the only order they can run.</summary>
public static class FulfillmentStepCode
{
    /// <summary>RELEASED → PICKING → PICKED. Always required, always first.</summary>
    public const string Pick       = "PICK";
    /// <summary>PICKED → PACKED (handling units). When absent, confirming the pick boxes each line into a LOOSE unit (D-3).</summary>
    public const string Pack       = "PACK";
    /// <summary>PACKED → STAGED. When absent, the delivery is staged automatically (D-3).</summary>
    public const string Stage      = "STAGE";
    /// <summary>"Approve dispatch" on a STAGED delivery (D-7, DELIVERY_APPROVE); goods issue is refused until approved.</summary>
    public const string Approval   = "APPROVAL";
    /// <summary>→ GOODS_ISSUED. Always required.</summary>
    public const string GoodsIssue = "GOODS_ISSUE";
    /// <summary>GOODS_ISSUED → IN_TRANSIT → DELIVERED through a consignment. Without it the customer collects (SELF_PICKUP, "Record collection").</summary>
    public const string Ship       = "SHIP";

    /// <summary>
    /// Every code in canonical order. A route's steps are always a subsequence of this list: the delivery state
    /// machine runs PICKED → PACKED → STAGED → (approval) → GOODS_ISSUED → IN_TRANSIT in that order and nothing
    /// else, so a route that put PACK after GOODS_ISSUE would describe something the warehouse cannot do.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Pick, Pack, Stage, Approval, GoodsIssue, Ship];

    /// <summary>The two steps no route may leave out (BR-C1-03, BR-C5-02); always mandatory.</summary>
    public static readonly IReadOnlyList<string> Required = [Pick, GoodsIssue];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code, StringComparer.Ordinal);

    /// <summary>
    /// The snapshot a delivery keeps of its route's steps (D-10): codes joined by commas in step order, e.g.
    /// "PICK,PACK,GOODS_ISSUE,SHIP". Edits to the route after the delivery was created do not reach it.
    /// </summary>
    public static string Format(IEnumerable<string> steps) => string.Join(',', steps);

    /// <summary>Reads a snapshot back. Null or blank → empty, which means "no route: the legacy full path" (R-1).</summary>
    public static IReadOnlyList<string> Parse(string? snapshot) =>
        string.IsNullOrWhiteSpace(snapshot)
            ? []
            : snapshot.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>A33 — where a sale order line's effective route came from (BR-C3-03). Written onto the line at confirm (D-16).</summary>
public static class FulfillmentRouteSource
{
    public const string LineOverride = "LINE_OVERRIDE";
    public const string Variant      = "VARIANT";
    public const string OrgDefault   = "ORG_DEFAULT";
    /// <summary>Nothing resolved: the line blocks confirmation. Never stored on a confirmed line.</summary>
    public const string None         = "NONE";
}

/// <summary>A33 — a route as other modules see it.</summary>
/// <param name="Steps">Step codes in step order (see <see cref="FulfillmentStepCode"/>).</param>
/// <param name="IsDefault">
/// The organization's default for its class: a route with SHIP is the default for SHIP orders, one without SHIP
/// the default for SELF_PICKUP orders (D-4). At most one default per class per organization.
/// </param>
public sealed record FulfillmentRouteSummary(
    Guid                  Uuid,
    string                Code,
    string                Name,
    bool                  IsActive,
    bool                  IsDefault,
    bool                  IsSystem,
    bool                  RequiresPacking,
    bool                  RequiresShipping,
    IReadOnlyList<string> Steps)
{
    /// <summary>
    /// A34 C1 — one of <see cref="FulfillmentRouteCategory"/> (STOCK, MANUFACTURE; BUY / DROPSHIP reserved). Init-only
    /// so positional callers are unaffected; anything built without it reads as STOCK.
    /// </summary>
    public string Category { get; init; } = FulfillmentRouteCategory.Stock;

    /// <summary>A34 — the route makes to order (D-1).</summary>
    public bool IsManufacture => FulfillmentRouteCategory.IsManufacture(Category);

    /// <summary>
    /// A37 D-12 (RTE-01) — false when the route's category needs a module the organization has switched off (MANUFACTURE
    /// without MODULE_MANUFACTURING). Set by Logistics' lookup; anything built without it reads as available. An
    /// unavailable route cannot be assigned, and the resolver falls back to the org default STOCK route (RTE-02).
    /// </summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>A37 — why <see cref="IsAvailable"/> is false ("Manufacturing is switched off"); null when available.</summary>
    public string? UnavailableReason { get; init; }

    public bool HasStep(string stepCode) => Steps.Contains(stepCode, StringComparer.Ordinal);

    /// <summary>"Pick → Pack → Goods Issue → Ship" — the wording every screen uses.</summary>
    public string StepsText => string.Join(" → ", Steps.Select(FulfillmentRouteText.StepLabel));
}

/// <summary>A33 — the organization's default routes, one per class (D-4). Either may be null (an admin cleared it).</summary>
/// <param name="Shipping">Default for SHIP orders: a route that has the SHIP step.</param>
/// <param name="NonShipping">Default for SELF_PICKUP orders: a route without the SHIP step.</param>
public sealed record FulfillmentRouteDefaults(FulfillmentRouteSummary? Shipping, FulfillmentRouteSummary? NonShipping)
{
    /// <summary>The default for an order's header delivery mode ("SHIP" / "SELF_PICKUP"); null when none is set.</summary>
    public FulfillmentRouteSummary? For(string? deliveryMode) =>
        string.Equals(deliveryMode, "SELF_PICKUP", StringComparison.OrdinalIgnoreCase) ? NonShipping : Shipping;
}

public static class FulfillmentRouteText
{
    public static string StepLabel(string stepCode) => stepCode switch
    {
        FulfillmentStepCode.Pick       => "Pick",
        FulfillmentStepCode.Pack       => "Pack",
        FulfillmentStepCode.Stage      => "Stage",
        FulfillmentStepCode.Approval   => "Approval",
        FulfillmentStepCode.GoodsIssue => "Goods Issue",
        FulfillmentStepCode.Ship       => "Ship",
        _                              => stepCode
    };
}

/// <summary>
/// A33 — reads an organization's fulfillment routes. Implemented in Logistics (which owns them); used by Inventory
/// (BR-C2-01: a variant may only point at an active route of its own organization) and Demand (BR-C3-01, the
/// resolver, the confirmation gate, the delivery preview). Optional-safe: a host without Logistics registers none,
/// and callers then treat routes as switched off (D-11).
/// </summary>
public interface IFulfillmentRouteLookup
{
    /// <summary>
    /// The routes of <paramref name="organizationId"/> among <paramref name="routeUuids"/>, active or not (an
    /// inactive route must still be named in the gate's message). Unknown uuids and other organizations' routes
    /// are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>> GetAsync(
        Guid organizationId, IReadOnlyCollection<Guid> routeUuids, CancellationToken ct = default);

    /// <summary>The organization's default routes (D-4/D-6). Defaults are always active: a default cannot be deactivated.</summary>
    Task<FulfillmentRouteDefaults> GetOrgDefaultsAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>The organization's active routes, by display order then code — for pickers that cannot call the API.</summary>
    Task<IReadOnlyList<FulfillmentRouteSummary>> ListActiveAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>
/// A33 — each variant's default route (C2). Implemented in Inventory (which owns variants); used by Demand's resolver
/// and preview. A separate interface rather than a new member on the positional <see cref="VariantDescription"/>
/// record, which would break every caller.
/// </summary>
public interface IVariantFulfillmentRoutes
{
    /// <summary>Route uuid per variant uuid, for variants of <paramref name="organizationId"/> that have one; others are absent.</summary>
    Task<IReadOnlyDictionary<Guid, Guid>> GetRouteUuidsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default);
}

/// <summary>A37 D-12 — one variant of a product and its configured default route (null = none).</summary>
public sealed record ProductVariantRoute(Guid VariantUuid, string VariantName, string Sku, Guid? RouteUuid);

/// <summary>
/// A37 D-12 — the variants of one product with their default routes, for <c>GET /api/products/{id}/routes</c> (served by
/// Logistics). To be implemented in Inventory (owns variants). Optional-safe: without an implementation the endpoint
/// answers 404.
/// </summary>
public interface IProductVariantRoutes
{
    /// <summary>
    /// Active variants of the product named by its int id (the <c>api/products/{id:int}</c> routes) or its uuid —
    /// exactly one is given. Ordered as the product page lists them. Null when the product is not one of
    /// <paramref name="organizationId"/>'s (another organization's product reads as absent).
    /// </summary>
    Task<IReadOnlyList<ProductVariantRoute>?> GetForProductAsync(
        Guid organizationId, int? productId, Guid? productUuid, CancellationToken ct = default);
}

/// <summary>How many records of one kind still point at a route — one entry of the "in use" refusal (BR-C1-07).</summary>
/// <param name="Description">Plural noun phrase for the message, e.g. "active product variants", "open sale order lines".</param>
public sealed record FulfillmentRouteUsageCount(string Description, int Count);

/// <summary>
/// A33 BR-C1-07 — "is this route still in use?". One implementation per module that stores a route reference
/// (Inventory: active variants; Demand: lines of DRAFT / CONFIRMED / PARTIALLY_FULFILLED orders), injected into
/// Logistics as <c>IEnumerable&lt;IFulfillmentRouteUsage&gt;</c> — the <see cref="IVariantReferenceChecker"/>
/// arrangement. Logistics refuses to deactivate or delete a route while any count is above zero.
/// </summary>
public interface IFulfillmentRouteUsage
{
    Task<FulfillmentRouteUsageCount> CountUsageAsync(Guid organizationId, Guid routeUuid, CancellationToken ct = default);
}

// ── Sale order → deliveries (C4) ─────────────────────────────────────────────────────────────────────

/// <summary>
/// A33 (REV-02) — the per-order application lock Demand's <c>SaleOrderHolds.OneChangeAtATimeAsync</c> takes
/// (sp_getapplock, Exclusive, owner Transaction). Here so Demand and Logistics use one string: the delivery creator
/// takes the same lock in its own transaction before it re-reads the order, so it can never insert deliveries for an
/// order a concurrent cancel is about to commit. Application locks are database-wide, so the two modules' different
/// connections still exclude each other.
/// </summary>
public static class SaleOrderLocks
{
    public const int TimeoutMilliseconds = 15_000;

    public static string HoldsResource(Guid saleOrderUuid) => $"demand.sale_orders/{saleOrderUuid:N}/holds";
}

/// <summary>A33 — one sale order line and the route it was confirmed with (the D-16 snapshot).</summary>
public sealed record SaleOrderLineRoute(Guid SoLineUuid, Guid RouteUuid);

/// <summary>A33 — a delivery the creator made.</summary>
/// <param name="DeliveryMode">SHIP or SELF_PICKUP — taken from the route (D-4).</param>
public sealed record CreatedSaleOrderDelivery(
    Guid    DeliveryUuid,
    string  DeliveryNumber,
    Guid    RouteUuid,
    string  RouteCode,
    string  DeliveryMode,
    Guid?   ShipFromWarehouseUuid,
    int     LineCount);

/// <summary>A33 — a line the creator put on no delivery this time, and why (in words, for the timeline).</summary>
public sealed record SkippedSaleOrderLine(Guid SoLineUuid, string Reason);

public sealed record SaleOrderDeliveryCreationResult(
    IReadOnlyList<CreatedSaleOrderDelivery> Created,
    IReadOnlyList<SkippedSaleOrderLine>     Skipped);

/// <summary>
/// A33 BR-C4-01..05 — creates the DRAFT deliveries for a confirmed sale order, one per route × ship-from warehouse
/// (C-4), for the quantity not yet on a delivery or fulfilled. Implemented in Logistics; called by Demand after the
/// confirm has committed (best effort, logged), by Demand's D-12 sweep, and by the recovery endpoint.
/// <para>
/// <b>Idempotent.</b> It only ever creates for outstanding quantity and serializes per order, so a second call — a
/// retry, the sweep, a double click — creates nothing more. Route splitting is not partial fulfilment (D-8): the
/// deliveries of one call jointly cover every line, so the partial-fulfilment setting does not refuse them.
/// </para>
/// <para>
/// <b>Locking (REV-02).</b> It runs in its own transaction, takes <see cref="SaleOrderLocks.HoldsResource"/> first,
/// and only then re-reads the order and its lines: an order that is no longer CONFIRMED / PARTIALLY_FULFILLED
/// (e.g. cancelled meanwhile) gets nothing. Callers must therefore <b>not</b> hold that lock when they call it
/// (Demand calls it after its confirm has committed); a call made inside the lock waits out the timeout and fails.
/// </para>
/// </summary>
public interface ISaleOrderDeliveryCreator
{
    /// <param name="lineRoutes">
    /// The route each line was confirmed with. Lines of the order not listed here (DROP_SHIP lines, D-5; cancelled
    /// lines) get no delivery.
    /// </param>
    Task<SaleOrderDeliveryCreationResult> CreateForConfirmedOrderAsync(
        Guid organizationId, Guid saleOrderUuid, IReadOnlyList<SaleOrderLineRoute> lineRoutes, int userId,
        CancellationToken ct = default);
}

/// <summary>A33 — a delivery as the sale order's cancellation reports it.</summary>
public sealed record SaleOrderDeliveryRef(Guid DeliveryUuid, string DeliveryNumber, string Status);

/// <param name="Cancelled">Deliveries this call cancelled (they were DRAFT … PENDING_APPROVAL or ON_HOLD).</param>
/// <param name="AlreadyIssued">Deliveries past goods issue, left as they are (BR-C4-07, D-15) — they need a manual reversal.</param>
public sealed record SaleOrderDeliveryCancellationResult(
    IReadOnlyList<SaleOrderDeliveryRef> Cancelled,
    IReadOnlyList<SaleOrderDeliveryRef> AlreadyIssued);

/// <summary>
/// A33 BR-C4-06/07 — cancels a sale order's deliveries that have not been goods-issued, giving their holds back.
/// Implemented in Logistics; called by Demand's cancel <b>before</b> it releases the order's own holds (R-7), from
/// inside Demand's <see cref="SaleOrderLocks.HoldsResource"/> lock — so the canceller must <b>not</b> take that lock
/// itself (it is held by Demand's connection; taking it again from Logistics' connection would wait and fail).
/// Idempotent: already-cancelled deliveries are neither cancelled again nor reported.
/// </summary>
public interface ISaleOrderDeliveryCanceller
{
    Task<SaleOrderDeliveryCancellationResult> CancelOpenAsync(
        Guid organizationId, Guid saleOrderUuid, string reason, int userId, CancellationToken ct = default);
}
