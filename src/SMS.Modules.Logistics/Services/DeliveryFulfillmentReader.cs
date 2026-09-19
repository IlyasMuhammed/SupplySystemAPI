using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// What Finance reads to bill a delivery (A29-P7-04). Read-only and deliberately thin: it says what
/// the delivery is and what reached the customer, and leaves every decision about whether to invoice
/// it — status, quantities already billed, prices — to the module that owns invoicing.
/// </summary>
internal sealed class DeliveryFulfillmentReader : IDeliveryFulfillmentReader
{
    private readonly LogisticsDbContext _db;

    public DeliveryFulfillmentReader(LogisticsDbContext db) => _db = db;

    public async Task<DeliveryForInvoicing?> GetAsync(Guid deliveryUuid, CancellationToken ct = default)
    {
        var delivery = await _db.DeliveryOrders.AsNoTracking()
            .Include(d => d.Lines)
            .FirstOrDefaultAsync(d => d.UUID == deliveryUuid && !d.IsDelete, ct);

        if (delivery is null) return null;

        return new DeliveryForInvoicing(
            delivery.UUID,
            delivery.DeliveryNumber,
            delivery.Status,
            delivery.SaleOrderUuid,
            [.. delivery.Lines
                .OrderBy(l => l.LineNo)
                .Select(l => new DeliveredLineForInvoicing(
                    l.UUID, l.LineNo, l.SoLineUuid, l.VariantUuid, l.ItemDescription, l.QtyDelivered))]);
    }
}
