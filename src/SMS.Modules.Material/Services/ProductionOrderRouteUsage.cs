using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A34 D-8 — "open production orders" still carrying a route: the organization's make-to-order POs (only the A34 path
/// sets <see cref="ProductionOrder.FulfillmentRouteUuid"/>) that are not COMPLETED, CLOSED or CANCELLED. Logistics asks
/// every <see cref="IFulfillmentRouteUsage"/> before it changes a route's category, deactivates or deletes it.
/// </summary>
internal sealed class ProductionOrderRouteUsage : IFulfillmentRouteUsage
{
    internal const string Description = "open production orders";

    private static readonly string[] Finished =
        [ProductionOrderStatus.Completed, ProductionOrderStatus.Closed, ProductionOrderStatus.Cancelled];

    private readonly MaterialDbContext _db;

    public ProductionOrderRouteUsage(MaterialDbContext db) => _db = db;

    public async Task<FulfillmentRouteUsageCount> CountUsageAsync(Guid organizationId, Guid routeUuid, CancellationToken ct = default)
    {
        var count = await _db.ProductionOrders.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(p => p.OrganizationId == organizationId && p.FulfillmentRouteUuid == routeUuid && !Finished.Contains(p.Status), ct);
        return new FulfillmentRouteUsageCount(Description, count);
    }
}
