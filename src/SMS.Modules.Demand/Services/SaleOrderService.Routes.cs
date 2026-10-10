using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Demand.Services;

// A33 (DEM) — fulfillment routes on sale orders: the line route, the confirmation gate, the D-16 snapshot, the delivery
// preview, and the calls to Logistics' delivery creator (after confirm) and canceller (on cancel).
// Contract: docs/fulfillment-routes/API-CONTRACT.md §5 and §9.
internal sealed partial class SaleOrderService
{
    private readonly IEffectiveRouteResolver? _routes;
    private readonly ISaleOrderDeliveryCreator? _deliveryCreator;
    private readonly ISaleOrderDeliveryCanceller? _deliveryCanceller;
    private readonly ILogger<SaleOrderService> _log;

    // ── resolution ────────────────────────────────────────────────────────────

    /// <summary>The order's lines resolved (BR-C3-03) with the gate's blockers; routes off (D-11) when there is no resolver.</summary>
    private async Task<RouteResolution> ResolveRoutesAsync(SaleOrder order, SaleOrderConfig config)
    {
        var lines = order.Lines.OrderBy(l => l.Id)
            .Select((l, i) => new RouteLineInput(l.UUID, i + 1, l.VariantUuid, l.Quantity, l.FulfillmentRouteUuid, l.FulfillmentMode))
            .ToList();
        return await ResolveLinesAsync(lines, order.DeliveryMode, order.ShippingAddressId, config);
    }

    private async Task<RouteResolution> ResolveLinesAsync(
        IReadOnlyList<RouteLineInput> lines, string deliveryMode, Guid? shippingAddressId, SaleOrderConfig config)
    {
        if (_routes is null)
            return new RouteResolution(false,
                lines.Select(l => new ResolvedLineRoute(l, null, null, FulfillmentRouteSource.None, null, Exempt: false)).ToList(), []);

        return await _routes.ResolveAsync(
            _tenantContext.OrganizationId,
            new RouteOrderContext(deliveryMode, shippingAddressId, config.SelfPickupEnabled, config.DropShipEnabled),
            lines);
    }

    private Task<bool> RoutesEnabledAsync() =>
        _routes is null ? Task.FromResult(false) : _routes.RoutesEnabledAsync(_tenantContext.OrganizationId);

    /// <summary>
    /// BR-C3-01 — every override in a create/update is an active route of the caller's organization. One lookup for the
    /// whole request, before any line is built. An organization without routes (D-11) cannot name one.
    /// </summary>
    private Task ValidateRouteOverridesAsync(IReadOnlyList<CreateSaleOrderLineRequest> lines) =>
        ValidateRoutesAsync(lines.Select((l, i) => (Route: l.FulfillmentRouteUuid, Number: i + 1))
            .Where(x => x.Route is { } r && r != Guid.Empty)
            .Select(x => (x.Route!.Value, x.Number))
            .ToList());

    /// <param name="overrides">Each chosen route with the 1-based number of its line, for the message.</param>
    private async Task ValidateRoutesAsync(IReadOnlyList<(Guid Route, int Number)> overrides)
    {
        if (overrides.Count == 0) return;

        if (!await RoutesEnabledAsync())
            throw new BadRequestException(
                "Fulfillment routes are not available for this organization (it has no Logistics module), so a sale order " +
                "line cannot name one. Leave the route empty.");

        var found = await _routes!.GetRoutesAsync(_tenantContext.OrganizationId, overrides.Select(o => o.Route).Distinct().ToList());
        foreach (var (route, number) in overrides)
        {
            if (!found.TryGetValue(route, out var summary))
                throw new BadRequestException(
                    $"Line {number}: fulfillment route {route} does not exist in this organization. The route must belong to your organization.");
            if (!summary.IsActive)
                throw new BadRequestException(
                    $"Line {number}: fulfillment route '{summary.Code}' is inactive. Choose an active route, or leave the line to inherit its route.");
            // A37 RTE-01 — a make-to-order route while Manufacturing is switched off.
            if (!summary.IsAvailable)
                throw new BadRequestException($"Line {number}: '{summary.Code}' — {FulfillmentRouteAvailability.ManufacturingOffMessage}");
        }
    }

    /// <summary>D-16 — the route each line resolved to, written at confirm. Exempt lines (DROP_SHIP) keep none.</summary>
    private static void SnapshotRoutes(SaleOrder order, RouteResolution routing)
    {
        foreach (var resolved in routing.Lines)
        {
            var line = order.Lines.Single(l => l.UUID == resolved.Line.LineUuid);
            if (resolved.IsRoutable)
            {
                line.FulfillmentRouteUuid = resolved.Route!.Uuid;
                line.FulfillmentRouteCode = resolved.Route.Code;
                line.RouteSource          = resolved.Source;
                line.FulfillmentRouteCategory = resolved.Route.Category;   // A34 — snapshotted with the route
            }
            else
            {
                line.FulfillmentRouteUuid = null;
                line.FulfillmentRouteCode = null;
                line.RouteSource          = null;
                line.FulfillmentRouteCategory = null;
            }
        }
    }

    /// <summary>The detail's route fields: live while DRAFT (with the blockers), the confirmed snapshot afterwards.</summary>
    private async Task ApplyRoutesAsync(SaleOrder order, SaleOrderModel model)
    {
        if (order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft))
        {
            var routing = await ResolveRoutesAsync(order, await ReadConfigAsync());
            model.RoutesEnabled   = routing.RoutesEnabled;
            model.ConfirmBlockers = [.. routing.Blockers];
            foreach (var resolved in routing.Lines)
            {
                var line = model.Lines.Single(l => l.Uuid == resolved.Line.LineUuid);
                line.FulfillmentRouteUuid = resolved.Line.OverrideRouteUuid;
                FillEffective(line, resolved.Route, resolved.RouteUuid, resolved.Route?.Code);
                line.RouteSource  = resolved.Source;
                line.RouteBlocker = resolved.Blocker;
                line.RouteWarning = resolved.Warning;
                // A34 §6.1 — live while DRAFT; null for exempt (DROP_SHIP) lines and lines with no known route.
                line.EffectiveRouteCategory = resolved.Exempt ? null : resolved.Route?.Category;
            }
            return;
        }

        model.RoutesEnabled = await RoutesEnabledAsync();
        var snapshots = order.Lines.Where(l => l.FulfillmentRouteUuid is not null).Select(l => l.FulfillmentRouteUuid!.Value).Distinct().ToList();
        var routes = _routes is null || snapshots.Count == 0
            ? new Dictionary<Guid, FulfillmentRouteSummary>()
            : await _routes.GetRoutesAsync(_tenantContext.OrganizationId, snapshots);
        foreach (var entity in order.Lines)
        {
            var line = model.Lines.Single(l => l.Uuid == entity.UUID);
            line.FulfillmentRouteUuid = entity.FulfillmentRouteUuid;
            var route = entity.FulfillmentRouteUuid is { } u ? routes.GetValueOrDefault(u) : null;
            FillEffective(line, route, entity.FulfillmentRouteUuid, entity.FulfillmentRouteCode);
            line.RouteSource = entity.RouteSource ?? FulfillmentRouteSource.None;
            // A34 §6.1 — the confirm snapshot; a line confirmed before A34 has none, and its route was a stock route then.
            line.EffectiveRouteCategory = entity.FulfillmentRouteCategory
                ?? (entity.FulfillmentRouteUuid is null ? null : route?.Category ?? FulfillmentRouteCategory.Stock);
        }
    }

    private static void FillEffective(SaleOrderLineModel line, FulfillmentRouteSummary? route, Guid? uuid, string? code)
    {
        line.EffectiveRouteUuid  = route?.Uuid ?? (code is null ? null : uuid);
        line.EffectiveRouteCode  = route?.Code ?? code;
        line.EffectiveRouteName  = route?.Name;
        line.EffectiveRouteSteps = route is null ? [] : [.. route.Steps];
    }

    // ── confirm → deliveries (D-1, REV-01/02) ──────────────────────────────────

    private async Task CreateDeliveriesAfterConfirmAsync(SaleOrder order, int userId, SaleOrderConfirmResultModel result)
    {
        var outcome = await SaleOrderDeliveryCreation.RunAsync(_db, _deliveryCreator!, _jobs, _log, order, userId);
        if (outcome.Result is { } created)
        {
            result.Deliveries = created.Created.Select(d => new CreatedSaleOrderDeliveryModel
            {
                DeliveryUuid = d.DeliveryUuid, DeliveryNumber = d.DeliveryNumber, RouteUuid = d.RouteUuid, RouteCode = d.RouteCode,
                DeliveryMode = d.DeliveryMode, ShipFromWarehouseUuid = d.ShipFromWarehouseUuid, LineCount = d.LineCount
            }).ToList();
            result.SkippedLines = created.Skipped.Select(s => new SkippedSaleOrderLineModel { SoLineUuid = s.SoLineUuid, Reason = s.Reason }).ToList();
        }
        else
        {
            result.DeliveryCreationFailed = true;
            result.DeliveryMessage = outcome.Cleared
                ? $"The order is confirmed, but its deliveries could not be created: {outcome.Error!.Message} " +
                  "Use \"Create deliveries\" on the order once that is put right."
                : "The order is confirmed, but its deliveries could not be created just now. They will be created " +
                  "automatically shortly, or use \"Create deliveries\" on the order.";
        }
    }

    public async Task<bool> MarkDeliveriesCreatedAsync(Guid uuid)
    {
        var order = await OwnOrders().FirstOrDefaultAsync(o => o.UUID == uuid);
        if (order is null) return false;
        if (order.DeliveryCreationPendingSince is not null)
        {
            order.DeliveryCreationPendingSince = null;
            await _db.SaveChangesAsync();
        }
        return true;
    }

    public async Task<SaleOrderLineModel?> UpdateLineRouteAsync(Guid uuid, Guid lineUuid, Guid? fulfillmentRouteUuid, int userId)
    {
        if (!await OwnOrders().AnyAsync(o => o.UUID == uuid)) return null;

        // Under the order's lock, like confirm: a change that read DRAFT must not land after a confirm has snapshotted
        // the line's route (D-16).
        var changed = await SaleOrderHolds.OneChangeAtATimeAsync<SaleOrder?>(_db, uuid, async () =>
        {
            var order = await OwnOrders().Include(o => o.Lines).FirstOrDefaultAsync(o => o.UUID == uuid);
            var line  = order?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
            if (order is null || line is null) return null;

            if (order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft))
                throw new BadRequestException(
                    "Only a DRAFT sale order's line route can be changed. A confirmed line keeps the route it was confirmed with.");

            var route = fulfillmentRouteUuid is { } r && r != Guid.Empty ? r : (Guid?)null;
            if (route is { } chosen)
                await ValidateRoutesAsync([(chosen, order.Lines.OrderBy(l => l.Id).ToList().IndexOf(line) + 1)]);

            // Only the route: the price, quantity, tax and totals the salesperson agreed stay exactly as they are.
            line.FulfillmentRouteUuid = route;
            order.ModifiedBy   = userId;
            order.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return order;
        });
        if (changed is null) return null;

        var model = ToModel(changed, includeLines: true);
        await ApplyRoutesAsync(changed, model);
        return model.Lines.Single(l => l.Uuid == lineUuid);
    }

    // ── cancel (D-15) ─────────────────────────────────────────────────────────

    private static SaleOrderCancelResultModel ToCancelResult(SaleOrderDeliveryCancellationResult? deliveries) => new()
    {
        CancelledDeliveries = deliveries?.Cancelled.Select(ToRef).ToList() ?? [],
        IssuedDeliveries    = deliveries?.AlreadyIssued.Select(ToRef).ToList() ?? []
    };

    private static SaleOrderDeliveryRefModel ToRef(SaleOrderDeliveryRef d) =>
        new() { DeliveryUuid = d.DeliveryUuid, DeliveryNumber = d.DeliveryNumber, Status = d.Status };

    private static string DeliveriesCancelledNote(SaleOrderDeliveryCancellationResult deliveries)
    {
        var parts = new List<string>();
        if (deliveries.Cancelled.Count > 0)
            parts.Add($"Deliveries cancelled: {string.Join(", ", deliveries.Cancelled.Select(d => d.DeliveryNumber))}.");
        if (deliveries.AlreadyIssued.Count > 0)
            parts.Add("Already goods-issued, left as they are (reverse them by hand): " +
                      $"{string.Join(", ", deliveries.AlreadyIssued.Select(d => $"{d.DeliveryNumber} ({d.Status})"))}.");
        return string.Join(" ", parts);
    }

    // ── delivery preview (BR-C3-05) ───────────────────────────────────────────

    public async Task<SaleOrderDeliveryPreviewModel?> GetDeliveryPreviewAsync(Guid uuid)
    {
        var order = await OwnOrders().AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == uuid);
        if (order is null) return null;

        RouteResolution routing;
        if (order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft))
        {
            routing = await ResolveRoutesAsync(order, await ReadConfigAsync());
        }
        else
        {
            // After confirm: the snapshot, nothing re-resolved and nothing blocking any more.
            var enabled = await RoutesEnabledAsync();
            var snapshots = order.Lines.Where(l => l.FulfillmentRouteUuid is not null).Select(l => l.FulfillmentRouteUuid!.Value).Distinct().ToList();
            var routes = _routes is null || !enabled || snapshots.Count == 0
                ? new Dictionary<Guid, FulfillmentRouteSummary>()
                : await _routes.GetRoutesAsync(_tenantContext.OrganizationId, snapshots);
            var cancelled = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);
            routing = new RouteResolution(enabled, order.Lines.OrderBy(l => l.Id).Select((l, i) =>
            {
                var input = new RouteLineInput(l.UUID, i + 1, l.VariantUuid, l.Quantity, l.FulfillmentRouteUuid, l.FulfillmentMode);
                var route = l.FulfillmentRouteUuid is { } u && l.Status != cancelled ? routes.GetValueOrDefault(u) : null;
                // A snapshot whose route was since deactivated still delivers on it (D-10): shown, not blocked.
                return new ResolvedLineRoute(input, route is null ? null : route with { IsActive = true }, l.FulfillmentRouteUuid,
                    l.RouteSource ?? FulfillmentRouteSource.None, null, Exempt: route is null);
            }).ToList(), []);
        }

        var warehouses = await WarehouseByLineAsync(order);
        return await BuildPreviewAsync(routing, warehouses);
    }

    public async Task<SaleOrderDeliveryPreviewModel> PreviewDeliveriesAsync(SaleOrderDeliveryPreviewRequest req)
    {
        if (req.SaleOrderUuid is { } soUuid && soUuid != Guid.Empty && !await OwnOrders().AnyAsync(o => o.UUID == soUuid))
            throw new NotFoundException("SaleOrder", soUuid);

        var config = await ReadConfigAsync();
        DeliveryMode mode;
        if (string.IsNullOrWhiteSpace(req.DeliveryMode))
            mode = DefaultDeliveryMode(config);
        else if (!EnumCode<DeliveryMode>.TryParse(req.DeliveryMode.Trim().ToUpperInvariant(), out mode))
            throw new BadRequestException($"'{req.DeliveryMode}' is not a valid delivery mode.");

        // What each line would be saved as (the organization's default sourcing), so drop-ship lines are exempt here too.
        var lineMode = DefaultLineMode(config, mode);
        // A36 D-10 — and service lines are exempt too.
        IReadOnlySet<Guid> services = _serviceVariants is null || (req.Lines ?? []).Count == 0
            ? new HashSet<Guid>()
            : await _serviceVariants.ServiceVariantsAsync(_tenantContext.OrganizationId, req.Lines!.Select(l => l.VariantUuid).Distinct().ToList());
        var lines = (req.Lines ?? []).Select((l, i) => new RouteLineInput(
            null, i + 1, l.VariantUuid, l.Quantity,
            l.FulfillmentRouteUuid is { } r && r != Guid.Empty ? r : null,
            services.Contains(l.VariantUuid) ? SaleOrderServiceLines.Code : lineMode)).ToList();

        var routing = await ResolveLinesAsync(lines, EnumCode<DeliveryMode>.Of(mode), req.ShippingAddressId, config);
        return await BuildPreviewAsync(routing, new Dictionary<Guid, IReadOnlyList<Guid>>());
    }

    /// <summary>
    /// Once the order holds stock, every warehouse each line is held in. The delivery creator splits a line across all
    /// of them (C-4, REV-06), so the preview does too: a line held in two warehouses is on two deliveries.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> WarehouseByLineAsync(SaleOrder order)
    {
        if (order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft)) return new Dictionary<Guid, IReadOnlyList<Guid>>();
        var holds = await _stock.GetBySourceAsync(ReservationSourceType.SalesOrder, order.UUID) ?? [];
        return holds.Where(h => h.Status == "ACTIVE" && h.SourceLineUuid is not null && h.ReservedQty > 0m)
            .GroupBy(h => h.SourceLineUuid!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(h => h.WarehouseUuid).Distinct().ToList());
    }

    private async Task<SaleOrderDeliveryPreviewModel> BuildPreviewAsync(
        RouteResolution routing, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> warehouses)
    {
        IReadOnlyDictionary<Guid, VariantDescription>? described = null;
        if (_variants is not null && routing.Lines.Count > 0)
            described = await _variants.DescribeVariantsAsync(routing.Lines.Select(l => l.Line.VariantUuid).Distinct().ToList());

        var lines = routing.Lines.Select(r => new DeliveryPreviewLineModel
        {
            LineUuid             = r.Line.LineUuid,
            LineNumber           = r.Line.LineNumber,
            VariantUuid          = r.Line.VariantUuid,
            ItemDescription      = described is not null && described.TryGetValue(r.Line.VariantUuid, out var v) ? v.DisplayName : null,
            Quantity             = r.Line.Quantity,
            FulfillmentRouteUuid = r.Line.OverrideRouteUuid,
            EffectiveRouteUuid   = r.Route?.Uuid,
            EffectiveRouteCode   = r.Route?.Code,
            EffectiveRouteName   = r.Route?.Name,
            EffectiveRouteSteps  = r.Route is null ? [] : [.. r.Route.Steps],
            RouteSource          = r.Source,
            RouteBlocker         = r.Blocker,
            RouteWarning         = r.Warning,
            EffectiveRouteCategory = r.Exempt ? null : r.Route?.Category
        }).ToList();

        // A34 §6.3 — make-to-order lines get a production order at confirm, not a delivery: listed apart.
        var productionLines = routing.Lines.Where(r => r.IsRoutable && r.Route!.IsManufacture)
            .OrderBy(r => r.Line.LineNumber)
            .Select(r => new DeliveryPreviewProductionLineModel
            {
                LineUuid        = r.Line.LineUuid,
                LineNumber      = r.Line.LineNumber,
                VariantUuid     = r.Line.VariantUuid,
                ItemDescription = described is not null && described.TryGetValue(r.Line.VariantUuid, out var pv) ? pv.DisplayName : null,
                Quantity        = r.Line.Quantity,
                RouteUuid       = r.Route!.Uuid,
                RouteCode       = r.Route.Code,
                RouteName       = r.Route.Name,
                Steps           = [.. r.Route.Steps]
            }).ToList();

        // One delivery per route × ship-from warehouse (C-4), in the order their first line appears.
        var groups = routing.Lines.Where(r => r.IsRoutable && !r.Route!.IsManufacture)
            .SelectMany(r => r.Line.LineUuid is { } lu && warehouses.TryGetValue(lu, out var held) && held.Count > 0
                ? held.Select(w => (Line: r, Warehouse: (Guid?)w))
                : [(Line: r, Warehouse: (Guid?)null)])
            .GroupBy(x => (Route: x.Line.Route!.Uuid, x.Warehouse), x => x.Line)
            .OrderBy(g => g.Min(r => r.Line.LineNumber))
            .Select(g =>
            {
                var route = g.First().Route!;
                return new DeliveryPreviewGroupModel
                {
                    RouteUuid        = route.Uuid,
                    RouteCode        = route.Code,
                    RouteName        = route.Name,
                    Steps            = [.. route.Steps],
                    StepsText        = route.StepsText,
                    RequiresShipping = route.RequiresShipping,
                    DeliveryMode     = route.RequiresShipping ? EnumCode<DeliveryMode>.Of(DeliveryMode.Ship) : EnumCode<DeliveryMode>.Of(DeliveryMode.SelfPickup),
                    WarehouseUuid    = g.Key.Warehouse,
                    LineNumbers      = g.Select(r => r.Line.LineNumber).OrderBy(n => n).ToList()
                };
            }).ToList();

        return new SaleOrderDeliveryPreviewModel
        {
            RoutesEnabled = routing.RoutesEnabled,
            CanConfirm    = routing.Blockers.Count == 0,
            DeliveryCount = groups.Count,
            Lines         = lines,
            Groups        = groups,
            Blockers      = [.. routing.Blockers],
            ProductionLines = productionLines
        };
    }
}
