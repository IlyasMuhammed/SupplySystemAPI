using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Constants;
using SMS.Modules.Logistics.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// A37 D-18 — what is still moving when MODULE_LOGISTICS is about to be switched off: deliveries not yet delivered,
/// closed or cancelled, and consignments not yet at an end state. The organization is taken explicitly (R-11).
/// </summary>
internal sealed class LogisticsModuleImpact : IModuleImpactProvider
{
    internal const string OpenDeliveries    = "Open deliveries";
    internal const string OpenConsignments  = "Open consignments";

    private static readonly string[] ClosedDeliveries =
    [
        LogisticsStatuses.Delivery.Delivered, LogisticsStatuses.Delivery.Closed,
        LogisticsStatuses.Delivery.ShortClosed, LogisticsStatuses.Delivery.Cancelled
    ];

    private static readonly string[] ClosedConsignments =
    [
        LogisticsStatuses.Shipment.Delivered, LogisticsStatuses.Shipment.ReturnedToOrigin,
        LogisticsStatuses.Shipment.Cancelled, LogisticsStatuses.Shipment.Lost
    ];

    private readonly LogisticsDbContext _db;
    public LogisticsModuleImpact(LogisticsDbContext db) => _db = db;

    public string ModuleCode => ModuleCodes.Logistics;

    public async Task<IReadOnlyList<ModuleImpactItem>> GetInProgressAsync(Guid organizationId, CancellationToken ct = default)
    {
        var deliveries = await _db.DeliveryOrders.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(d => d.OrganizationId == organizationId && !d.IsDelete && !ClosedDeliveries.Contains(d.Status), ct);
        var consignments = await _db.Consignments.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(c => c.OrganizationId == organizationId && !c.IsDelete && !ClosedConsignments.Contains(c.Status), ct);
        return [new(OpenDeliveries, deliveries), new(OpenConsignments, consignments)];
    }
}
