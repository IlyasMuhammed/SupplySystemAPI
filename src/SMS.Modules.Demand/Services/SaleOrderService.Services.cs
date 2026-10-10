using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

// A36 (DEM) — service lines (D-10, D-11): marking them, the detail's serviceOrders[] and the retry of any service order a
// confirm could not raise. Contract: docs/service-orders/API-CONTRACT.md §4.
internal sealed partial class SaleOrderService
{
    private readonly IServiceVariantClassifier? _serviceVariants;
    private readonly IServiceOrderDemandService? _serviceOrders;

    private Task MarkServiceLinesAsync(IEnumerable<SaleOrderLine> lines, string? defaultMode) =>
        SaleOrderServiceLines.MarkAsync(_serviceVariants, _tenantContext.OrganizationId, lines, defaultMode);

    /// <summary>
    /// §4 — serviceOrders[] (empty for a draft, without Material's service or without MODULE_SERVICES). An open order with
    /// a live, unfulfilled service line that has no service order at all (the confirm's call failed) gets it raised here,
    /// idempotently and silently, as the order's last modifier — the A36 stand-in for production's pending flag and sweep.
    /// </summary>
    private async Task ApplyServiceOrdersAsync(SaleOrder order, SaleOrderModel model)
    {
        if (_serviceOrders is null || order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft)) return;
        if (!await FeatureEnabledAsync(SaleOrderServiceLines.ServicesFeature)) return;

        try
        {
            var refs = await _serviceOrders.GetForSaleOrderAsync(order.OrganizationId, order.UUID);
            var missing = SaleOrderProductionCreation.IsOpen(order)
                && SaleOrderServiceLines.Live(order).Any(l => l.FulfilledQty < l.Quantity && !refs.Any(r => r.SoLineUuid == l.UUID));
            if (missing)
            {
                var outcome = await SaleOrderServiceLines.RunAsync(_db, _serviceOrders, _jobs, _log, order.OrganizationId, order.UUID,
                    order.ModifiedBy ?? order.CreatedBy, interactive: false);
                if (outcome.Created.Count > 0)
                    refs = await _serviceOrders.GetForSaleOrderAsync(order.OrganizationId, order.UUID);
            }
            model.ServiceOrders = ServiceOrderModels(order, refs);
        }
        catch (Exception ex)
        {
            // A read for display: Material being unreachable must not take the order's page down with it.
            _log.LogWarning(ex, "Service orders of sale order {SoNumber} could not be read.", order.SoNumber);
        }
    }

    private static List<SaleOrderServiceOrderModel> ServiceOrderModels(SaleOrder order, IReadOnlyList<SaleOrderServiceOrderRef> refs) =>
        refs.Select(r => new SaleOrderServiceOrderModel
        {
            ServiceOrderUuid = r.ServiceOrderUuid,
            ServiceNumber    = r.ServiceNumber,
            SoLineUuid       = r.SoLineUuid,
            LineNumber       = SaleOrderProductionCreation.LineNumber(order, r.SoLineUuid),
            Status           = r.Status,
            Quantity         = r.Quantity
        }).ToList();
}
