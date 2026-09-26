using SMS.Modules.Material.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A30-P5-01, FSD §30. Every notification goes through the same <see cref="INotificationService"/>/
/// Hangfire pipeline <c>SupplierSelectionService.NotifySupplyTeamAsync</c> and
/// <c>WorkflowEscalationJob</c> already use — no new mechanism. Two shapes of recipient, matching
/// those precedents: a document that needs someone *other than its own creator* to act on it (a new
/// production order, a new supply requirement, or an order now needing an inspector — §18.4 says the
/// creator cannot be one) escalates to the creator's supervisor, falling back to the creator when
/// none is configured, the same fallback <c>NotifySupplyTeamAsync</c> uses for a department head.
/// Everything else is "your own order changed" and goes straight to the order's creator regardless of
/// who performed the action that caused it — a floor operator confirming an FGR does not make them
/// the notification's audience, the planner who is waiting on the order is.
/// </summary>
internal sealed class ManufacturingNotificationService : IManufacturingNotificationService
{
    private readonly INotificationService _notifications;
    private readonly IOrgChartService     _orgChart;

    public ManufacturingNotificationService(INotificationService notifications, IOrgChartService orgChart)
    {
        _notifications = notifications;
        _orgChart      = orgChart;
    }

    private async Task<int> EscalateAsync(int actorUserId)
    {
        var supervisor = await _orgChart.GetSupervisorAsync(actorUserId);
        return supervisor?.UserId ?? actorUserId;
    }

    public async Task ProductionOrderCreatedAsync(ProductionOrder po, string productName, int actingUserId) =>
        await _notifications.TryCreateAsync(new NotificationRequest(
            UserId: await EscalateAsync(actingUserId), Type: "PROD_CREATED", Title: "Production Order Created",
            Message: $"{po.ProductionNumber} created for {po.PlannedQuantity:0.####} {productName}.",
            Category: "Manufacturing", EntityType: "ProductionOrder", EntityUuid: po.UUID.ToString(), CreatedBy: actingUserId));

    public Task ProductionOrderReadyAsync(ProductionOrder po) =>
        _notifications.TryCreateAsync(new NotificationRequest(
            UserId: po.CreatedBy, Type: "PROD_READY", Title: "Production Order Ready",
            Message: $"{po.ProductionNumber} is ready — all materials available.",
            Category: "Manufacturing", EntityType: "ProductionOrder", EntityUuid: po.UUID.ToString(), CreatedBy: po.CreatedBy));

    public Task ProductionOrderCompletedAsync(ProductionOrder po) =>
        _notifications.TryCreateAsync(new NotificationRequest(
            UserId: po.CreatedBy, Type: "PROD_COMPLETED", Title: "Production Order Completed",
            Message: $"{po.ProductionNumber} completed: {po.AcceptedQuantity:0.####} accepted into stock.",
            Category: "Manufacturing", EntityType: "ProductionOrder", EntityUuid: po.UUID.ToString(), CreatedBy: po.CreatedBy));

    public Task ShortageAlertAsync(ProductionOrder po, string materialName, decimal quantity, string uom) =>
        _notifications.TryCreateAsync(new NotificationRequest(
            UserId: po.CreatedBy, Type: "PROD_SHORTAGE", Title: "Material Shortage",
            Message: $"{po.ProductionNumber}: shortage of {materialName} ({quantity:0.####} {uom}).",
            Category: "Manufacturing", EntityType: "ProductionOrder", EntityUuid: po.UUID.ToString(), CreatedBy: po.CreatedBy, SendEmail: true));

    public async Task QualityInspectionRequiredAsync(ProductionOrder po) =>
        await _notifications.TryCreateAsync(new NotificationRequest(
            UserId: await EscalateAsync(po.CreatedBy), Type: "QI_REQUIRED", Title: "Quality Inspection Needed",
            Message: $"{po.ProductionNumber} is ready for quality inspection.",
            Category: "Manufacturing", EntityType: "ProductionOrder", EntityUuid: po.UUID.ToString(), CreatedBy: po.CreatedBy));

    public Task QualityInspectionCompletedAsync(ProductionOrder po, QualityInspection qi) =>
        _notifications.TryCreateAsync(new NotificationRequest(
            UserId: po.CreatedBy, Type: "QI_COMPLETED", Title: "Quality Inspection Complete",
            Message: $"QI for {po.ProductionNumber}: {qi.AcceptedQuantity:0.####} accepted, {qi.RejectedQuantity:0.####} rejected.",
            Category: "Manufacturing", EntityType: "ProductionOrder", EntityUuid: po.UUID.ToString(), CreatedBy: qi.InspectedBy));

    public Task FinishedGoodsReceiptConfirmedAsync(ProductionOrder po, FinishedGoodsReceipt fgr, string productName, string warehouseName) =>
        _notifications.TryCreateAsync(new NotificationRequest(
            UserId: po.CreatedBy, Type: "FGR_CONFIRMED", Title: "Finished Goods Received",
            Message: $"{fgr.FgrNumber}: {fgr.TotalQuantity:0.####} {productName} received into {warehouseName}.",
            Category: "Manufacturing", EntityType: "ProductionOrder", EntityUuid: po.UUID.ToString(), CreatedBy: fgr.ReceivedBy));

    public async Task SupplyRequirementCreatedAsync(SupplyRequirement sr, string productName) =>
        await _notifications.TryCreateAsync(new NotificationRequest(
            UserId: await EscalateAsync(sr.CreatedBy), Type: "SR_CREATED", Title: "Supply Requirement Created",
            Message: $"New supply requirement {sr.SupplyNumber}: {productName} x {sr.QuantityRequired:0.####}.",
            Category: "Manufacturing", EntityType: "SupplyRequirement", EntityUuid: sr.UUID.ToString(), CreatedBy: sr.CreatedBy));

    public Task AllocationCompletedAsync(ProductionOrder po, string materialName, decimal quantity, string uom) =>
        _notifications.TryCreateAsync(new NotificationRequest(
            UserId: po.CreatedBy, Type: "ALLOCATION_COMPLETED", Title: "Allocation Completed",
            Message: $"{materialName} for {po.ProductionNumber} is now fully allocated ({quantity:0.####} {uom}).",
            Category: "Manufacturing", EntityType: "ProductionOrder", EntityUuid: po.UUID.ToString(), CreatedBy: po.CreatedBy));

    public Task ChainedProductionOrderCreatedAsync(ProductionOrder parent, ProductionOrder child, string materialName) =>
        _notifications.TryCreateAsync(new NotificationRequest(
            UserId: parent.CreatedBy, Type: "PROD_CHAINED", Title: "Chained Production Order Created",
            Message: $"{child.ProductionNumber} was automatically raised to cover a shortage of {materialName} on {parent.ProductionNumber}.",
            Category: "Manufacturing", EntityType: "ProductionOrder", EntityUuid: parent.UUID.ToString(), CreatedBy: child.CreatedBy));
}
