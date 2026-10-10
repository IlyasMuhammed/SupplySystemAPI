using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>A33: one sale order line as the resolver sees it (a saved line, or a line of the unsaved form).</summary>
/// <param name="LineNumber">1-based, by line Id ("Line N").</param>
/// <param name="OverrideRouteUuid">The line's own route (BR-C3-03 tier 1); null = inherit.</param>
/// <param name="FulfillmentMode">Sourcing; DROP_SHIP lines are exempt (D-5).</param>
internal sealed record RouteLineInput(
    Guid? LineUuid, int LineNumber, Guid VariantUuid, decimal Quantity, Guid? OverrideRouteUuid, string? FulfillmentMode);

/// <summary>A33: what the order header contributes to resolution and the D-4 checks.</summary>
internal sealed record RouteOrderContext(string DeliveryMode, Guid? ShippingAddressId, bool SelfPickupEnabled, bool DropShipEnabled);

/// <summary>A33: one line, resolved.</summary>
/// <param name="Route">The effective route when it exists in the organization (active or not); null otherwise.</param>
/// <param name="RouteUuid">The uuid the tier pointed at, even when the route is unknown.</param>
/// <param name="Source">LINE_OVERRIDE | VARIANT | ORG_DEFAULT | NONE.</param>
/// <param name="Blocker">A <see cref="ConfirmBlockerCodes"/> value when the line blocks confirmation.</param>
/// <param name="Exempt">DROP_SHIP (D-5) or SERVICE (A36 D-10): no route, no blocker, no delivery.</param>
/// <param name="Warning">
/// A37 RTE-03 — the line's own or its variant's route is unavailable (Manufacturing switched off): what it fell back to,
/// or that there is nothing to fall back to.
/// </param>
internal sealed record ResolvedLineRoute(
    RouteLineInput Line, FulfillmentRouteSummary? Route, Guid? RouteUuid, string Source, string? Blocker, bool Exempt,
    string? Warning = null)
{
    /// <summary>A usable route: known and active. Only these are snapshotted and sent to the delivery creator.</summary>
    public bool IsRoutable => !Exempt && Blocker is null && Route is { IsActive: true };
}

/// <summary>A33: a whole order's lines resolved, and every reason it cannot be confirmed (in the 400's order).</summary>
internal sealed record RouteResolution(
    bool RoutesEnabled, IReadOnlyList<ResolvedLineRoute> Lines, IReadOnlyList<ConfirmBlockerModel> Blockers)
{
    /// <summary>The confirm gate's 400 message: the blocker messages joined with a newline (API-CONTRACT.md §5).</summary>
    public string Message => string.Join("\n", Blockers.Select(b => b.Message));
}

/// <summary>
/// A33 PC-03: a sale order line's effective fulfillment route (BR-C3-03, D-4, D-5) and the confirmation gate's
/// blockers (BR-C3-02). Batched: one variant-route read, one route read and at most one org-defaults read per call,
/// however many lines.
/// </summary>
internal interface IEffectiveRouteResolver
{
    /// <summary>D-11: the organization has MODULE_LOGISTICS and Logistics' route lookup is registered.</summary>
    Task<bool> RoutesEnabledAsync(Guid organizationId, CancellationToken ct = default);

    Task<RouteResolution> ResolveAsync(
        Guid organizationId, RouteOrderContext order, IReadOnlyList<RouteLineInput> lines, CancellationToken ct = default);

    /// <summary>The organization's routes among <paramref name="routeUuids"/> (active or not); empty when routes are off.</summary>
    Task<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>> GetRoutesAsync(
        Guid organizationId, IReadOnlyCollection<Guid> routeUuids, CancellationToken ct = default);
}

internal sealed class EffectiveRouteResolver : IEffectiveRouteResolver
{
    internal const string LogisticsFeature = "MODULE_LOGISTICS";

    internal const string ManufacturingFeature = "MODULE_MANUFACTURING";

    private readonly IFulfillmentRouteLookup? _lookup;
    private readonly IVariantFulfillmentRoutes? _variants;
    private readonly ITenantSnapshotProvider? _tenants;
    private readonly IManufacturingReadiness? _readiness;
    private readonly IModuleGate? _gate;

    public EffectiveRouteResolver(
        IFulfillmentRouteLookup? lookup = null, IVariantFulfillmentRoutes? variants = null, ITenantSnapshotProvider? tenants = null,
        IManufacturingReadiness? readiness = null, IModuleGate? gate = null)
    {
        _gate      = gate;
        _lookup    = lookup;
        _variants  = variants;
        _tenants   = tenants;
        // A34 D-5 — Material's batched readiness check for make-to-order lines. Without it nothing can be made to order.
        _readiness = readiness;
    }

    public async Task<bool> RoutesEnabledAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (_lookup is null || organizationId == Guid.Empty) return false;
        // No snapshot provider (a host without Tenancy, unit tests): the lookup being registered is the signal.
        if (_tenants is null) return true;
        var tenant = await _tenants.GetSnapshotAsync(organizationId);
        return tenant is not null && tenant.EnabledFeatureCodes.Contains(LogisticsFeature);
    }

    public async Task<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>> GetRoutesAsync(
        Guid organizationId, IReadOnlyCollection<Guid> routeUuids, CancellationToken ct = default)
    {
        if (_lookup is null || routeUuids.Count == 0) return new Dictionary<Guid, FulfillmentRouteSummary>();
        return await _lookup.GetAsync(organizationId, routeUuids.Distinct().ToList(), ct);
    }

    public async Task<RouteResolution> ResolveAsync(
        Guid organizationId, RouteOrderContext order, IReadOnlyList<RouteLineInput> lines, CancellationToken ct = default)
    {
        if (!await RoutesEnabledAsync(organizationId, ct))
            return new RouteResolution(false,
                lines.Select(l => new ResolvedLineRoute(l, null, null, FulfillmentRouteSource.None, null, Exempt: false)).ToList(), []);

        var dropShip = EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.DropShip);
        // A36 D-10 — a service line is exempt the same way: no route, no blocker, no delivery.
        var service = EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.Service);
        bool IsExempt(RouteLineInput l) => (order.DropShipEnabled && l.FulfillmentMode == dropShip) || l.FulfillmentMode == service;

        // Tier 2, for every line that has no override of its own — one read.
        var needVariant = lines.Where(l => !IsExempt(l) && l.OverrideRouteUuid is null).Select(l => l.VariantUuid).Distinct().ToList();
        IReadOnlyDictionary<Guid, Guid> variantRoutes = needVariant.Count == 0 || _variants is null
            ? new Dictionary<Guid, Guid>()
            : await _variants.GetRouteUuidsAsync(organizationId, needVariant, ct);

        // Every route a line points at (override or variant), active or not — one read.
        var pointedAt = lines.Where(l => !IsExempt(l))
            .Select(l => l.OverrideRouteUuid ?? (variantRoutes.TryGetValue(l.VariantUuid, out var v) ? v : (Guid?)null))
            .Where(u => u is not null).Select(u => u!.Value).Distinct().ToList();
        var routes = await GetRoutesAsync(organizationId, pointedAt, ct);

        // A37 RTE-02 — a pointed-at route that is unavailable (MANUFACTURE with Manufacturing off) falls back to tier 3.
        Guid? PointedAt(RouteLineInput l) => l.OverrideRouteUuid ?? (variantRoutes.TryGetValue(l.VariantUuid, out var v) ? v : (Guid?)null);
        FulfillmentRouteSummary? Unavailable(Guid? u) => u is { } x && routes.TryGetValue(x, out var s) && !s.IsAvailable ? s : null;

        // Tier 3 only when some line needs it — at most one read.
        FulfillmentRouteSummary? orgDefault = null;
        if (lines.Any(l => !IsExempt(l) && (PointedAt(l) is null || Unavailable(PointedAt(l)) is not null)))
            orgDefault = (await _lookup!.GetOrgDefaultsAsync(organizationId, ct)).For(order.DeliveryMode);

        var resolved = new List<ResolvedLineRoute>(lines.Count);
        foreach (var line in lines)
        {
            if (IsExempt(line))
            {
                resolved.Add(new ResolvedLineRoute(line, null, null, FulfillmentRouteSource.None, null, Exempt: true));
                continue;
            }

            (Guid? uuid, string source) = line.OverrideRouteUuid is { } o ? (o, FulfillmentRouteSource.LineOverride)
                : variantRoutes.TryGetValue(line.VariantUuid, out var v) ? (v, FulfillmentRouteSource.Variant)
                : orgDefault is not null ? (orgDefault.Uuid, FulfillmentRouteSource.OrgDefault)
                : ((Guid?)null, FulfillmentRouteSource.None);

            string? warning = null;
            if (source != FulfillmentRouteSource.OrgDefault && Unavailable(uuid) is { } off)
            {
                // A default is never MANUFACTURE (A34 D-6), so the fallback is always a stock route.
                warning = orgDefault is not null
                    ? $"Fulfillment route '{off.Code}' is not available ({off.UnavailableReason ?? FulfillmentRouteAvailability.ManufacturingOffReason}); " +
                      $"the default route '{orgDefault.Code}' is used instead."
                    : $"Fulfillment route '{off.Code}' is not available ({off.UnavailableReason ?? FulfillmentRouteAvailability.ManufacturingOffReason}) " +
                      "and there is no default route to use instead.";
                (uuid, source) = orgDefault is not null
                    ? (orgDefault.Uuid, FulfillmentRouteSource.OrgDefault)
                    : ((Guid?)null, FulfillmentRouteSource.None);
            }

            var route = uuid is { } u
                ? (source == FulfillmentRouteSource.OrgDefault ? orgDefault : routes.GetValueOrDefault(u))
                : null;

            string? blocker =
                uuid is null                                     ? ConfirmBlockerCodes.RouteMissing
              : route is null                                    ? ConfirmBlockerCodes.RouteUnknown
              : !route.IsActive                                  ? ConfirmBlockerCodes.RouteInactive
              : !route.RequiresShipping && !order.SelfPickupEnabled ? ConfirmBlockerCodes.SelfPickupDisabled
              : null;

            resolved.Add(new ResolvedLineRoute(line, route, uuid, source, blocker, Exempt: false, warning));
        }

        var items = await ManufacturingGateAsync(organizationId, resolved, ct);
        return new RouteResolution(true, resolved, Blockers(resolved, order, items));
    }

    /// <summary>
    /// A34 D-5 — the make-to-order lines (routable, effective route MANUFACTURE) that cannot be made get one blocker each,
    /// first match: MANUFACTURING_DISABLED, NOT_MANUFACTURED, BOM_MISSING, PRODUCTION_WAREHOUSE_MISSING. One batched
    /// readiness call, only when some line is make to order. Returns each line's item name for the messages.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>> ManufacturingGateAsync(
        Guid organizationId, List<ResolvedLineRoute> resolved, CancellationToken ct)
    {
        var names = new Dictionary<int, string>();
        var makeToOrder = resolved.Select((r, i) => (r, i)).Where(x => x.r.IsRoutable && x.r.Route!.IsManufacture).ToList();
        if (makeToOrder.Count == 0) return names;

        IReadOnlyDictionary<Guid, ManufacturingReadinessInfo>? ready = null;
        if (_readiness is not null && await ManufacturingEnabledAsync(organizationId))
            ready = await _readiness.CheckAsync(organizationId, makeToOrder.Select(x => x.r.Line.VariantUuid).Distinct().ToList(), ct);

        foreach (var (r, i) in makeToOrder)
        {
            string? blocker;
            if (ready is null)
                blocker = ConfirmBlockerCodes.ManufacturingDisabled;
            else if (!ready.TryGetValue(r.Line.VariantUuid, out var info) || !info.IsManufactured)
                blocker = ConfirmBlockerCodes.NotManufactured;
            else if (!info.HasActiveBom)
                blocker = ConfirmBlockerCodes.BomMissing;
            else if (!info.HasProductionWarehouse)
                blocker = ConfirmBlockerCodes.ProductionWarehouseMissing;
            else
                blocker = null;

            if (blocker is null) continue;
            names[r.Line.LineNumber] = ready?.GetValueOrDefault(r.Line.VariantUuid)?.DisplayName ?? "This item";
            resolved[i] = r with { Blocker = blocker };
        }
        return names;
    }

    /// <summary>
    /// D-9 — make-to-order needs MODULE_MANUFACTURING: the A37 module gate when registered (grace counts as off), else the
    /// snapshot; with neither (unit hosts) it is taken as on.
    /// </summary>
    private Task<bool> ManufacturingEnabledAsync(Guid organizationId) =>
        FulfillmentRouteAvailability.ManufacturingOnAsync(_gate, _tenants, organizationId);

    /// <summary>
    /// The 400's content, in its order (API-CONTRACT.md §5): each line's own route problem, in line order; then every
    /// line with no route at all, together; then the order-level D-4 shipping address check.
    /// </summary>
    private static List<ConfirmBlockerModel> Blockers(
        IReadOnlyList<ResolvedLineRoute> lines, RouteOrderContext order, IReadOnlyDictionary<int, string> items)
    {
        var blockers = new List<ConfirmBlockerModel>();
        foreach (var r in lines.Where(r => r.Blocker is not null && r.Blocker != ConfirmBlockerCodes.RouteMissing)
                               .OrderBy(r => r.Line.LineNumber))
        {
            var n = r.Line.LineNumber;
            var item = items.GetValueOrDefault(n, "This item");
            var message = r.Blocker switch
            {
                ConfirmBlockerCodes.RouteInactive =>
                    $"Line {n}: fulfillment route '{r.Route!.Code}' is inactive.",
                ConfirmBlockerCodes.SelfPickupDisabled =>
                    $"Line {n}: fulfillment route '{r.Route!.Code}' has no Ship step, so the customer would collect, " +
                    "and customer pickup is switched off for this organization. Choose a route that ships.",
                // A34 D-5 (API-CONTRACT §6.2).
                ConfirmBlockerCodes.ManufacturingDisabled =>
                    $"Line {n}: '{r.Route!.Code}' is a make-to-order route, but manufacturing is not enabled for your organization. " +
                    "Choose a stock route for this line.",
                ConfirmBlockerCodes.NotManufactured =>
                    $"Line {n}: {item} is not a manufactured product, so it can't use the make-to-order route '{r.Route!.Code}'. " +
                    "Choose a stock route, or set the product's supply method to MANUFACTURE.",
                ConfirmBlockerCodes.BomMissing =>
                    $"Line {n}: {item} has no active bill of materials, so it can't be made to order. " +
                    "Activate a BOM, or choose a stock route for this line.",
                ConfirmBlockerCodes.ProductionWarehouseMissing =>
                    $"Line {n}: {item} has no default production warehouse, so it can't be made to order. " +
                    "Set one on the product, or choose a stock route for this line.",
                _ =>
                    $"Line {n}: its fulfillment route no longer exists in this organization. Choose another route."
            };
            blockers.Add(new ConfirmBlockerModel { LineUuid = r.Line.LineUuid, LineNumber = n, Code = r.Blocker!, Message = message });
        }

        var missing = lines.Where(r => r.Blocker == ConfirmBlockerCodes.RouteMissing).Select(r => r.Line).OrderBy(l => l.LineNumber).ToList();
        if (missing.Count > 0)
        {
            var which = missing.Count == 1
                ? $"line {missing[0].LineNumber} has"
                : $"lines {string.Join(", ", missing.Select(l => l.LineNumber))} have";
            blockers.Add(new ConfirmBlockerModel
            {
                LineUuid   = missing.Count == 1 ? missing[0].LineUuid : null,
                LineNumber = missing.Count == 1 ? missing[0].LineNumber : null,
                Code       = ConfirmBlockerCodes.RouteMissing,
                Message    = $"Cannot confirm: {which} no fulfillment route. " +
                             "Assign a route on each line or set a default route on the product variant."
            });
        }

        // D-4: a route with Ship needs somewhere to ship to, whatever the header mode says.
        if (order.ShippingAddressId is null && lines.Any(r => r.IsRoutable && r.Route!.RequiresShipping))
        {
            var message = "Cannot confirm: some lines are shipped (their fulfillment route has a Ship step), " +
                          "so the order needs a shipping address.";
            // A34 R-17 / C-16 — both seeded manufacture routes ship; a collected order needs one of its own.
            if (order.DeliveryMode == EnumCode<DeliveryMode>.Of(DeliveryMode.SelfPickup)
                && lines.Any(r => r.IsRoutable && r.Route!.IsManufacture && r.Route.RequiresShipping))
                message += " For collection, create a MANUFACTURE route without the SHIP step.";
            blockers.Add(new ConfirmBlockerModel { Code = ConfirmBlockerCodes.ShippingAddressRequired, Message = message });
        }

        return blockers;
    }
}
