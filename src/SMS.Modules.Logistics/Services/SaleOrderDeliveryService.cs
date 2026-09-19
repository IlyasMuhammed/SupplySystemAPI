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
}

internal sealed class SaleOrderDeliveryService : ISaleOrderDeliveryService
{
    private readonly DemandDbContext               _demand;
    private readonly IDeliveryRepository           _deliveries;
    private readonly IDeliveryFromSourceRepository _fromSource;

    public SaleOrderDeliveryService(
        DemandDbContext demand,
        IDeliveryRepository deliveries,
        IDeliveryFromSourceRepository fromSource)
    {
        _demand     = demand;
        _deliveries = deliveries;
        _fromSource = fromSource;
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
        var exists = await _demand.SaleOrders.AnyAsync(s => s.UUID == saleOrderUuid && !s.IsDeleted);
        if (!exists) return null;

        return await _deliveries.GetForSaleOrderAsync(saleOrderUuid);
    }
}
