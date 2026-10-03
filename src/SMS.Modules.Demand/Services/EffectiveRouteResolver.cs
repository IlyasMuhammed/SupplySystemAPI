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
/// <param name="Exempt">DROP_SHIP (D-5): no route, no blocker, no delivery.</param>
internal sealed record ResolvedLineRoute(
    RouteLineInput Line, FulfillmentRouteSummary? Route, Guid? RouteUuid, string Source, string? Blocker, bool Exempt)
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

    private readonly IFulfillmentRouteLookup? _lookup;
    private readonly IVariantFulfillmentRoutes? _variants;
    private readonly ITenantSnapshotProvider? _tenants;

    public EffectiveRouteResolver(
        IFulfillmentRouteLookup? lookup = null, IVariantFulfillmentRoutes? variants = null, ITenantSnapshotProvider? tenants = null)
    {
        _lookup   = lookup;
        _variants = variants;
        _tenants  = tenants;
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
        bool IsExempt(RouteLineInput l) => order.DropShipEnabled && l.FulfillmentMode == dropShip;

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

        // Tier 3 only when some line needs it — at most one read.
        FulfillmentRouteSummary? orgDefault = null;
        if (lines.Any(l => !IsExempt(l) && l.OverrideRouteUuid is null && !variantRoutes.ContainsKey(l.VariantUuid)))
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

            var route = uuid is { } u
                ? (source == FulfillmentRouteSource.OrgDefault ? orgDefault : routes.GetValueOrDefault(u))
                : null;

            string? blocker =
                uuid is null                                     ? ConfirmBlockerCodes.RouteMissing
              : route is null                                    ? ConfirmBlockerCodes.RouteUnknown
              : !route.IsActive                                  ? ConfirmBlockerCodes.RouteInactive
              : !route.RequiresShipping && !order.SelfPickupEnabled ? ConfirmBlockerCodes.SelfPickupDisabled
              : null;

            resolved.Add(new ResolvedLineRoute(line, route, uuid, source, blocker, Exempt: false));
        }

        return new RouteResolution(true, resolved, Blockers(resolved, order));
    }

    /// <summary>
    /// The 400's content, in its order (API-CONTRACT.md §5): each line's own route problem, in line order; then every
    /// line with no route at all, together; then the order-level D-4 shipping address check.
    /// </summary>
    private static List<ConfirmBlockerModel> Blockers(IReadOnlyList<ResolvedLineRoute> lines, RouteOrderContext order)
    {
        var blockers = new List<ConfirmBlockerModel>();
        foreach (var r in lines.Where(r => r.Blocker is not null && r.Blocker != ConfirmBlockerCodes.RouteMissing)
                               .OrderBy(r => r.Line.LineNumber))
        {
            var n = r.Line.LineNumber;
            var message = r.Blocker switch
            {
                ConfirmBlockerCodes.RouteInactive =>
                    $"Line {n}: fulfillment route '{r.Route!.Code}' is inactive.",
                ConfirmBlockerCodes.SelfPickupDisabled =>
                    $"Line {n}: fulfillment route '{r.Route!.Code}' has no Ship step, so the customer would collect, " +
                    "and customer pickup is switched off for this organization. Choose a route that ships.",
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
            blockers.Add(new ConfirmBlockerModel
            {
                Code    = ConfirmBlockerCodes.ShippingAddressRequired,
                Message = "Cannot confirm: some lines are shipped (their fulfillment route has a Ship step), " +
                          "so the order needs a shipping address."
            });

        return blockers;
    }
}
