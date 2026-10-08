using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Demand.Services;

// A34 (DEM) — lead time on sale order lines (D-15/D-16), make-to-order production after confirm (D-17/D-19), the cancel
// cascade (D-22) and the detail's production orders (D-25). Contract: docs/route-classification/API-CONTRACT.md §5, §6.
internal sealed partial class SaleOrderService
{
    private readonly ILeadTimeCalculator? _leadTimes;
    private readonly ISaleOrderProductionService? _production;
    private readonly IAllocationEngine? _allocation;
    private readonly ITenantSnapshotProvider? _tenants;
    private readonly INotificationService? _notifications;
    private readonly IManufacturingLevelDays? _levelDays;

    private static readonly string[] DatedStatuses =
    [
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft),
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed),
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled)
    ];

    // ── D-16: line lead time and manual date ───────────────────────────────────

    public async Task<SaleLineLeadTimeModel<SaleOrderLineModel>?> CalculateLineLeadTimeAsync(Guid uuid, Guid lineUuid, int userId)
    {
        var order = await OwnOrders().Include(o => o.Lines).FirstOrDefaultAsync(o => o.UUID == uuid);
        var line  = order?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
        if (order is null || line is null) return null;

        if (order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft))
            throw new BadRequestException($"Sale order {order.SoNumber} is {order.Status}: line lead times can only be calculated on a draft.");

        var calculator = await LeadTimeGate.RequireAsync(_leadTimes, _tenants, _tenantContext.OrganizationId);

        // The line's effective route, as confirm would resolve it (override → variant → org default of the header's mode);
        // an exempt line or one whose route no longer exists leaves the choice to the calculator (variant, else SHIP default).
        var routing  = await ResolveRoutesAsync(order, await ReadConfigAsync());
        var resolved = routing.Lines.Single(r => r.Line.LineUuid == lineUuid);
        var routeUuid = resolved.Exempt ? null : resolved.Route?.Uuid;

        var result = await calculator.CalculateAsync(_tenantContext.OrganizationId,
            new LeadTimeRequest(line.VariantUuid, line.Quantity, routeUuid, order.ExpectedDeliveryDate?.Date));

        // Only the three Calculated* fields: the price, quantity and the manual date stay as they are.
        line.CalculatedLeadTimeDays = result.TotalLeadTimeDays;
        line.CalculatedDeliveryDate = result.EarliestDeliveryDate.Date;
        line.LeadTimeCalculatedAt   = result.CalculatedAt;
        order.ModifiedBy   = userId;
        order.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var model = ToModel(order, includeLines: true);
        await ApplyRoutesAsync(order, model);
        return new SaleLineLeadTimeModel<SaleOrderLineModel> { Line = model.Lines.Single(l => l.Uuid == lineUuid), LeadTime = result };
    }

    public async Task<SaleOrderLineDeliveryDateResultModel?> UpdateLineDeliveryDateAsync(
        Guid uuid, Guid lineUuid, DateTime? manualDeliveryDate, int userId)
    {
        if (!await OwnOrders().AnyAsync(o => o.UUID == uuid)) return null;

        // Under the order's lock, like every other change to a confirmed order's lines: no re-pricing, no rebuild.
        var changed = await SaleOrderHolds.OneChangeAtATimeAsync<SaleOrder?>(_db, uuid, async () =>
        {
            var order = await OwnOrders().Include(o => o.Lines).FirstOrDefaultAsync(o => o.UUID == uuid);
            var line  = order?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
            if (order is null || line is null) return null;

            if (!DatedStatuses.Contains(order.Status))
                throw new BadRequestException($"Sale order {order.SoNumber} is {order.Status}: its delivery dates can no longer change.");
            if (line.Status == EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled))
                throw new BadRequestException(
                    $"Line {order.Lines.OrderBy(l => l.Id).ToList().IndexOf(line) + 1} of sale order {order.SoNumber} is cancelled: its delivery date can no longer change.");

            line.ManualDeliveryDate = manualDeliveryDate?.Date;
            order.ModifiedBy   = userId;
            order.ModifiedDate = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return order;
        });
        if (changed is null) return null;

        var model = ToModel(changed, includeLines: true);
        await ApplyRoutesAsync(changed, model);
        var result = new SaleOrderLineDeliveryDateResultModel { Line = model.Lines.Single(l => l.Uuid == lineUuid) };

        // D-16 — a production order already made for the line keeps the dates it was planned with; say so.
        var planned = (await ProductionOrdersOfAsync(changed))
            .Where(p => p.SoLineUuid == lineUuid && p.Status != ProductionStatusCancelled).Select(p => p.ProductionNumber).ToList();
        if (planned.Count > 0)
        {
            result.ProductionNotRescheduled = true;
            result.Warning = planned.Count == 1
                ? $"{planned[0]} was planned for the earlier date and is not rescheduled."
                : $"{string.Join(", ", planned)} were planned for the earlier date and are not rescheduled.";
        }
        return result;
    }

    // ── D-25: the order's production orders ────────────────────────────────────

    internal const string ProductionStatusCancelled = "CANCELLED";

    /// <summary>Every SALES_ORDER-sourced production order of the order; none for a DRAFT (none can exist) or without Material.</summary>
    private async Task<IReadOnlyList<SaleOrderProductionRef>> ProductionOrdersOfAsync(SaleOrder order)
    {
        if (_production is null || order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft)) return [];
        try
        {
            return await _production.GetForSaleOrderAsync(order.OrganizationId, order.UUID);
        }
        catch (Exception ex)
        {
            // A read for display: Material being unreachable must not take the order's page down with it.
            _log.LogWarning(ex, "Production orders of sale order {SoNumber} could not be read.", order.SoNumber);
            return [];
        }
    }

    /// <summary>
    /// D-25 / REV-05 — the detail's productionOrders[] and productionCreationPending: the flag is set, or the order is open
    /// and some make-to-order line has no live make-to-order production order (e.g. cancelled by hand in Manufacturing).
    /// </summary>
    private async Task ApplyProductionAsync(SaleOrder order, SaleOrderModel model)
    {
        if (order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft)) return;

        var refs = await ProductionOrdersOfAsync(order);
        model.ProductionOrders = await ProductionModelsAsync(order, refs);

        // REV-06 — a make-to-order order still in DRAFT (its planning failed) is not made yet either: the button re-runs the
        // idempotent creation, which returns it, and plans it.
        var unmade = _production is not null && SaleOrderProductionCreation.IsOpen(order)
            && SaleOrderProductionCreation.MakeToOrderLines(order).Any(l => !refs.Any(r =>
                r.SoLineUuid == l.UUID && r.FulfillmentRouteUuid is not null
                && r.Status != ProductionStatusCancelled && r.Status != SaleOrderProductionCreation.ProductionDraft));
        model.ProductionCreationPending = order.ProductionCreationPendingSince is not null || unmade;
    }

    /// <summary>Line numbers from the order, route code and name through Logistics' lookup (also for a deactivated route).</summary>
    private async Task<List<SaleOrderProductionOrderModel>> ProductionModelsAsync(SaleOrder? order, IReadOnlyList<SaleOrderProductionRef> refs)
    {
        if (refs.Count == 0) return [];
        var routeUuids = refs.Where(r => r.FulfillmentRouteUuid is not null).Select(r => r.FulfillmentRouteUuid!.Value).Distinct().ToList();
        IReadOnlyDictionary<Guid, FulfillmentRouteSummary> routes = _routes is null || routeUuids.Count == 0
            ? new Dictionary<Guid, FulfillmentRouteSummary>()
            : await _routes.GetRoutesAsync(_tenantContext.OrganizationId, routeUuids);

        return refs.Select(r =>
        {
            var route = r.FulfillmentRouteUuid is { } u ? routes.GetValueOrDefault(u) : null;
            return new SaleOrderProductionOrderModel
            {
                ProductionOrderUuid  = r.ProductionOrderUuid,
                ProductionNumber     = r.ProductionNumber,
                SoLineUuid           = r.SoLineUuid,
                LineNumber           = order is null ? null : SaleOrderProductionCreation.LineNumber(order, r.SoLineUuid),
                Status               = r.Status,
                PlannedQuantity      = r.PlannedQuantity,
                AcceptedQuantity     = r.AcceptedQuantity,
                IsMakeToOrder        = r.FulfillmentRouteUuid is not null,
                FulfillmentRouteUuid = r.FulfillmentRouteUuid,
                FulfillmentRouteCode = route?.Code,
                FulfillmentRouteName = route?.Name,
                DeliveryOrderUuid    = r.DeliveryOrderUuid,
                DeliveryNumber       = r.DeliveryNumber,
                Created              = r.Created
            };
        }).ToList();
    }

    private static string ProductionCancelledNote(SaleOrderProductionCancellation production)
    {
        var parts = new List<string>();
        if (production.Cancelled.Count > 0)
            parts.Add($"Production orders cancelled: {string.Join(", ", production.Cancelled.Select(p => p.ProductionNumber))}.");
        if (production.KeptRunning.Count > 0)
            parts.Add("Already started or finished, left as they are (a running one finishes into stock; cancel it in Manufacturing if it should stop): " +
                      $"{string.Join(", ", production.KeptRunning.Select(p => $"{p.ProductionNumber} ({p.Status})"))}.");
        return string.Join(" ", parts);
    }

    // ── D-17: production creation after confirm, and its recovery button ───────

    private async Task<(SaleOrderProductionCreation.Outcome Outcome, List<SaleOrderProductionOrderModel> Models)> RunProductionCreationAsync(
        Guid uuid, int userId)
    {
        var outcome = await SaleOrderProductionCreation.RunAsync(
            _db, _production!, _leadTimes, _levelDays, _jobs, _notifications, _log, _tenantContext.OrganizationId, uuid, userId, interactive: true);
        return (outcome, await ProductionModelsAsync(outcome.Order, outcome.ProductionOrders));
    }

    public async Task<SaleOrderProductionCreationResultModel?> CreateProductionOrdersAsync(Guid uuid, int userId)
    {
        var order = await OwnOrders().AsNoTracking().FirstOrDefaultAsync(o => o.UUID == uuid);
        if (order is null) return null;

        if (!SaleOrderProductionCreation.IsOpen(order))
            throw new BadRequestException($"Sale order {order.SoNumber} is {order.Status}: production orders are created once it is confirmed.");
        if (_production is null || !await FeatureEnabledAsync(EffectiveRouteResolver.ManufacturingFeature))
            throw new BadRequestException("Manufacturing is not enabled for your organization.");

        var (outcome, models) = await RunProductionCreationAsync(uuid, userId);
        return new SaleOrderProductionCreationResultModel
        {
            ProductionOrders         = models,
            ProductionCreationFailed = outcome.Failed,
            ProductionMessage        = outcome.Message
        };
    }

    /// <summary>The caller's organization has <paramref name="feature"/>; with no snapshot provider (unit hosts) it is taken as on.</summary>
    private async Task<bool> FeatureEnabledAsync(string feature)
    {
        if (_tenants is null) return true;
        var tenant = await _tenants.GetSnapshotAsync(_tenantContext.OrganizationId);
        return tenant is not null && tenant.EnabledFeatureCodes.Contains(feature);
    }
}
