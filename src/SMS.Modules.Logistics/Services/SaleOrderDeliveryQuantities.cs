using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// A32 PE-02 — what Demand reads to know how much of each sale order line sits on deliveries that have not been
/// goods-issued yet. The same "in flight" rule as <c>DeliveryFromSourceRepository.OutstandingBySoLineAsync</c> (what
/// may still be put on a delivery): DRAFT through STAGED, PENDING_APPROVAL and ON_HOLD. Once issued the units are in
/// the line's FulfilledQty; cancelled and short-closed deliveries are not going.
/// </summary>
internal sealed class SaleOrderDeliveryQuantities : ISaleOrderDeliveryQuantities
{
    private static readonly string[] BeforeIssue =
    [
        LogisticsCode.Of(DeliveryStatus.Draft),
        LogisticsCode.Of(DeliveryStatus.Released),
        LogisticsCode.Of(DeliveryStatus.Picking),
        LogisticsCode.Of(DeliveryStatus.Picked),
        LogisticsCode.Of(DeliveryStatus.Packed),
        LogisticsCode.Of(DeliveryStatus.Staged),
        LogisticsCode.Of(DeliveryStatus.PendingApproval),
        LogisticsCode.Of(DeliveryStatus.OnHold)
    ];

    private readonly LogisticsDbContext _db;

    public SaleOrderDeliveryQuantities(LogisticsDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetInFlightBySoLineAsync(Guid saleOrderUuid, CancellationToken ct = default) =>
        await _db.DeliveryOrderLines.AsNoTracking()
            .Where(l => l.SoLineUuid != null
                     && !l.DeliveryOrder.IsDelete
                     && l.DeliveryOrder.SaleOrderUuid == saleOrderUuid
                     && BeforeIssue.Contains(l.DeliveryOrder.Status))
            .GroupBy(l => l.SoLineUuid!.Value)
            .Select(g => new { SoLineUuid = g.Key, Qty = g.Sum(x => x.QtyOrdered) })
            .ToDictionaryAsync(x => x.SoLineUuid, x => x.Qty, ct);
}
