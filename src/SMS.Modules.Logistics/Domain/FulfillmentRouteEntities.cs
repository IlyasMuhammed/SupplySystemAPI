using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// A33 C1 — a fulfillment route: which warehouse operations a sale order line goes through (pick only, pick and ship,
/// pick-pack-ship, or a custom chain). Defined per organization; referenced by bare UUID from
/// <c>inventory.ProductVariants</c>, <c>demand.sale_order_lines</c> and <see cref="DeliveryOrder"/> (no FK across
/// DbContexts).
/// <para>
/// <b>Defaults are per class (D-4).</b> A route with the SHIP step can be the default for SHIP orders, one without
/// SHIP the default for SELF_PICKUP orders; at most one of each per organization, enforced by a unique filtered
/// index on (OrganizationId, RequiresShipping) WHERE IsDefault = 1.
/// </para>
/// <para>
/// <b>System routes</b> (the three seeds) keep their code and steps; their name, description and display order may
/// be edited (D-10). They can be deactivated but never deleted (BR-C1-06).
/// </para>
/// </summary>
internal class FulfillmentRoute : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>Upper case, letters/digits/underscores, unique per organization (BR-C1-01). Never changes after creation.</summary>
    public string  Code        { get; set; } = string.Empty;
    public string  Name        { get; set; } = string.Empty;
    public string? Description { get; set; }

    public bool IsDefault { get; set; }
    public bool IsActive  { get; set; } = true;
    public bool IsSystem  { get; set; }

    /// <summary>Derived from the steps on every save (BR-C1-09): has PACK.</summary>
    public bool RequiresPacking  { get; set; }

    /// <summary>Derived from the steps on every save (BR-C1-09): has SHIP. Also the route's default class (D-4).</summary>
    public bool RequiresShipping { get; set; }

    public int DisplayOrder { get; set; }

    /// <summary>
    /// A34 C1 — one of <see cref="FulfillmentRouteCategory"/> (CHECK constraint; default STOCK, so every route from
    /// before A34 is a stock route). MANUFACTURE = make to order (D-1): never a default (D-6); locked on system,
    /// default and in-use routes (D-8). BUY / DROPSHIP are reserved and refused by the service (D-7).
    /// </summary>
    public string RouteCategory { get; set; } = FulfillmentRouteCategory.Stock;

    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; }
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }

    /// <summary>Optimistic concurrency: two admins editing one route must not silently overwrite each other's steps.</summary>
    public byte[] RowVersion { get; set; } = [];

    public ICollection<FulfillmentRouteStep> Steps { get; set; } = new List<FulfillmentRouteStep>();

    /// <summary>The step codes in step order — what a delivery snapshots (D-10) and what <see cref="FulfillmentRouteSummary"/> carries.</summary>
    public IReadOnlyList<string> OrderedStepCodes() =>
        Steps.OrderBy(s => s.StepOrder).Select(s => s.StepCode).ToList();
}

/// <summary>A33 C1 — one step of a route. Unique per route by order and by code.</summary>
internal class FulfillmentRouteStep : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid OrganizationId { get; set; }

    public int              FulfillmentRouteId { get; set; }
    public FulfillmentRoute FulfillmentRoute   { get; set; } = null!;

    /// <summary>See <see cref="FulfillmentStep"/> / <see cref="FulfillmentStepCode"/>. Stored as its code.</summary>
    public string StepCode { get; set; } = string.Empty;

    /// <summary>1, 2, 3 … with no gaps (BR-C1-04).</summary>
    public int StepOrder { get; set; }

    /// <summary>
    /// Stored as the spec asks. PICK and GOODS_ISSUE are always mandatory. For the other steps it is informational
    /// in this release: a step in the route is always performed (or auto-completed, D-3), never skipped at runtime.
    /// </summary>
    public bool IsMandatory { get; set; } = true;

    public string? Description { get; set; }
}

/// <summary>A33 — the route steps as an enum, for code that reasons in types. Codes are <see cref="FulfillmentStepCode"/>'s.</summary>
internal enum FulfillmentStep
{
    [Code(FulfillmentStepCode.Pick)]       Pick,
    [Code(FulfillmentStepCode.Pack)]       Pack,
    [Code(FulfillmentStepCode.Stage)]      Stage,
    [Code(FulfillmentStepCode.Approval)]   Approval,
    [Code(FulfillmentStepCode.GoodsIssue)] GoodsIssue,
    [Code(FulfillmentStepCode.Ship)]       Ship
}

/// <summary>
/// A33 D-2/D-3 — how a route's steps map onto the delivery's real statuses. The transition table
/// (<c>DeliveryStateMachine</c>) is unchanged: a step that is not in the route is <b>auto-completed</b> by the
/// operation before it (no PACK → confirming the pick boxes the lines into LOOSE units → PACKED; no STAGE → staged
/// automatically), so goods issue, carrier booking and invoicing see exactly what they see today. Every route ends
/// at DELIVERED (the spec's COMPLETED): with SHIP through the consignment, without SHIP through "Record collection".
/// </summary>
internal static class FulfillmentRouteStatusMap
{
    /// <summary>The statuses each step covers (D-2). DRAFT belongs to no step; CLOSED follows DELIVERED.</summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> StatusesByStep =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [FulfillmentStepCode.Pick]       = ["RELEASED", "PICKING", "PICKED"],
            [FulfillmentStepCode.Pack]       = ["PACKED"],
            [FulfillmentStepCode.Stage]      = ["STAGED"],
            // D-7: the "Approve dispatch" action stamps ApprovedAt on a STAGED delivery. PENDING_APPROVAL stays the
            // workflow engine's (DeliveryStatusHandler) and is shown under this step when it is used.
            [FulfillmentStepCode.Approval]   = ["PENDING_APPROVAL"],
            [FulfillmentStepCode.GoodsIssue] = ["GOODS_ISSUED"],
            [FulfillmentStepCode.Ship]       = ["IN_TRANSIT", "PARTIALLY_DELIVERED", "DELIVERED"],
        };

    /// <summary>
    /// The statuses a delivery on these steps is seen to pass through, in order — the route editor's "Preview"
    /// line, in the spec's shape. Steps left out are not listed even though their status is still entered
    /// automatically (D-3). APPROVAL happens on a STAGED delivery (D-7), so it shows as STAGED; PARTIALLY_DELIVERED
    /// is an off-path status and is not shown. Always ends at DELIVERED (the spec's COMPLETED); a route without
    /// SHIP reaches it by "Record collection".
    /// </summary>
    internal static IReadOnlyList<string> StatusPath(IEnumerable<string> steps)
    {
        var path = new List<string> { "DRAFT" };
        foreach (var step in steps)
        {
            IEnumerable<string> statuses = step switch
            {
                FulfillmentStepCode.Approval => ["STAGED"],
                FulfillmentStepCode.Ship     => ["IN_TRANSIT", "DELIVERED"],
                _ => StatusesByStep.TryGetValue(step, out var s) ? s : []
            };
            path.AddRange(statuses.Where(s => !path.Contains(s)));
        }

        if (!path.Contains("DELIVERED")) path.Add("DELIVERED");
        return path;
    }
}
