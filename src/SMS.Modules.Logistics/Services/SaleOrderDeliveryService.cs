using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// The sale-order-facing face of fulfilment (A29 §7.8): raise a delivery for an order, and see the
/// deliveries an order has. Everything else — release, pick, pack, issue, collect — is the ordinary
/// delivery API, because a sale-order delivery is an ordinary delivery.
/// <para>
/// Lives in Logistics rather than Demand because Logistics already reads Demand's context to raise
/// deliveries from purchase orders, and Demand referencing Logistics would be a cycle.
/// </para>
/// </summary>
public interface ISaleOrderDeliveryService
{
    /// <summary>Raises a delivery for the order's outstanding lines, or the ones the request picks.</summary>
    Task<Guid> CreateAsync(Guid saleOrderUuid, CreateSaleOrderDeliveryRequest? req, int createdBy);

    /// <summary>The order's deliveries, oldest first. Null when the order does not exist here.</summary>
    Task<IReadOnlyList<DeliveryListItemModel>?> GetDeliveriesAsync(Guid saleOrderUuid);

    /// <summary>
    /// A33 PD-05 recovery (D-12 button): the route deliveries for a confirmed order's lines, from their confirmed route
    /// snapshots, through <see cref="SMS.Shared.Common.ISaleOrderDeliveryCreator"/>. Idempotent. Null when the order is
    /// not the caller organization's.
    /// </summary>
    Task<SMS.Shared.Common.SaleOrderDeliveryCreationResult?> CreateRouteDeliveriesAsync(Guid saleOrderUuid, int userId);
}

internal sealed class SaleOrderDeliveryService : ISaleOrderDeliveryService
{
    private readonly DemandDbContext               _demand;
    private readonly IDeliveryRepository           _deliveries;
    private readonly IDeliveryFromSourceRepository _fromSource;
    private readonly SMS.Shared.Common.ISaleOrderDeliveryCreator? _creator;
    private readonly Demand.Services.ISaleOrderService?         _orders;

    public SaleOrderDeliveryService(
        DemandDbContext demand,
        IDeliveryRepository deliveries,
        IDeliveryFromSourceRepository fromSource,
        SMS.Shared.Common.ISaleOrderDeliveryCreator? creator = null,
        Demand.Services.ISaleOrderService? orders = null)
    {
        _demand     = demand;
        _deliveries = deliveries;
        _fromSource = fromSource;
        _creator    = creator;
        _orders     = orders;
    }


    public Task<Guid> CreateAsync(Guid saleOrderUuid, CreateSaleOrderDeliveryRequest? req, int createdBy)
    {
        req ??= new CreateSaleOrderDeliveryRequest();

        return _fromSource.CreateFromSourceAsync(new CreateDeliveryFromSourceRequest
        {
            SourceType            = LogisticsCode.Of(DeliverySourceType.SaleOrder),
            SourceUuid            = saleOrderUuid,
            DeliveryMode          = req.DeliveryMode,
            ShipFromWarehouseUuid = req.ShipFromWarehouseUuid,
            ShipFromAddress       = req.ShipFromAddress,
            ShipToAddress         = req.ShipToAddress,
            RequestedDate         = req.RequestedDate,
            PromisedDate          = req.PromisedDate,
            Priority              = req.Priority,
            Incoterm              = req.Incoterm,
            Notes                 = req.Notes,
            Lines                 = req.Lines
        }, createdBy);
    }

    public async Task<IReadOnlyList<DeliveryListItemModel>?> GetDeliveriesAsync(Guid saleOrderUuid)
    {
        // An order that does not exist — or belongs to another organization, which the tenant
        // filter makes the same thing — is a 404, not an empty list that reads as "nothing yet".
        // A33 (R-14): the caller's own organization explicitly, as the sale order pages are (A32 PF-05).
        var ownOrg = _demand.TenantContext.OrganizationId;
        var exists = await _demand.SaleOrders.AnyAsync(s => s.UUID == saleOrderUuid && !s.IsDeleted && s.OrganizationId == ownOrg);
        if (!exists) return null;

        return await _deliveries.GetForSaleOrderAsync(saleOrderUuid);
    }

    private static readonly string CancelledLine =
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderLineStatus>.Of(Demand.Domain.SaleOrderLineStatus.Cancelled);

    private static readonly string DropShipLine =
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderLineFulfillmentMode>.Of(Demand.Domain.SaleOrderLineFulfillmentMode.DropShip);

    // A36 D-10 — a service line is performed by a service order, never delivered.
    private static readonly string ServiceLine =
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderLineFulfillmentMode>.Of(Demand.Domain.SaleOrderLineFulfillmentMode.Service);

    private static readonly string[] DeliverableSoStatuses =
    [
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderStatus>.Of(Demand.Domain.SaleOrderStatus.Confirmed),
        Demand.Domain.EnumCode<Demand.Domain.SaleOrderStatus>.Of(Demand.Domain.SaleOrderStatus.PartiallyFulfilled)
    ];

    public async Task<SMS.Shared.Common.SaleOrderDeliveryCreationResult?> CreateRouteDeliveriesAsync(Guid saleOrderUuid, int userId)
    {
        var ownOrg = _demand.TenantContext.OrganizationId;
        var so = await _demand.SaleOrders.AsNoTracking()
            .Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.UUID == saleOrderUuid && !s.IsDeleted && s.OrganizationId == ownOrg);
        if (so is null) return null;

        if (!DeliverableSoStatuses.Contains(so.Status))
            throw new SMS.Shared.Exceptions.BadRequestException(
                $"Sale order {so.SoNumber} is {so.Status}. Deliveries are created for an order that is " +
                $"{string.Join(" or ", DeliverableSoStatuses)}.");

        if (_creator is null)
            throw new SMS.Shared.Exceptions.ConflictException("Creating deliveries by route is not available here.");

        var lines = so.Lines
            .Where(l => l.Status != CancelledLine && l.FulfillmentMode != DropShipLine && l.FulfillmentMode != ServiceLine)
            .OrderBy(l => l.Id)
            .ToList();

        // The confirmed snapshot (D-16) is the line's route with a RouteSource; a line confirmed before routes has none.
        var routed  = lines.Where(l => l.FulfillmentRouteUuid is not null && l.RouteSource is not null).ToList();
        var legacy  = lines.Except(routed)
            .Select(l => new SMS.Shared.Common.SkippedSaleOrderLine(l.UUID,
                $"Line {so.Lines.OrderBy(x => x.Id).ToList().IndexOf(l) + 1} was confirmed before fulfillment routes, " +
                "so it has no route to follow. Use \"Create delivery\" to raise its delivery by hand."))
            .ToList();

        var result = routed.Count == 0
            ? new SMS.Shared.Common.SaleOrderDeliveryCreationResult([], [])
            : await _creator.CreateForConfirmedOrderAsync(
                ownOrg, so.UUID,
                [.. routed.Select(l => new SMS.Shared.Common.SaleOrderLineRoute(l.UUID, l.FulfillmentRouteUuid!.Value))],
                userId);

        // REV-01 — the creator returned without throwing, so the D-12 sweep has nothing left to retry for this order.
        if (_orders is not null) await _orders.MarkDeliveriesCreatedAsync(so.UUID);

        return new SMS.Shared.Common.SaleOrderDeliveryCreationResult(result.Created, [.. result.Skipped, .. legacy]);
    }
}
