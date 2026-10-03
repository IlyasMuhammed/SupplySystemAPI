using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// A32 PE-02 — what Demand reads to know how much of each sale order line sits on deliveries that have not been
/// goods-issued yet. Two numbers, because they answer two questions (A33 C-2):
/// <list type="bullet">
/// <item><b>In flight</b> (<see cref="GetInFlightBySoLineAsync"/>) — the same rule as
/// <c>DeliveryFromSourceRepository.OutstandingBySoLineAsync</c>: DRAFT through STAGED, PENDING_APPROVAL and ON_HOLD.
/// What may not be put on another delivery.</item>
/// <item><b>Held</b> (<see cref="GetHeldBySoLineAsync"/>) — the same minus DRAFT. The order's own SALES_ORDER hold
/// moves onto a delivery only at release (<c>DeliveryReleaseRepository.ReserveStockAsync</c>), so a DRAFT holds
/// nothing and must not count towards what the line holds, or every auto-created draft would zero the line's
/// reservable quantity and DeficitQty.</item>
/// </list>
/// Once issued the units are in the line's FulfilledQty; cancelled and short-closed deliveries are not going.
/// </summary>
internal sealed class SaleOrderDeliveryQuantities : ISaleOrderDeliveryQuantities
{
    /// <summary>
    /// Released and not yet issued. ON_HOLD is always one of these underneath: the state machine refuses to hold a
    /// DRAFT (<c>DeliveryStateMachine</c>: DRAFT → RELEASED or CANCELLED only), so a held delivery was released.
    /// </summary>
    private static readonly string[] Holding =
    [
        LogisticsCode.Of(DeliveryStatus.Released),
        LogisticsCode.Of(DeliveryStatus.Picking),
        LogisticsCode.Of(DeliveryStatus.Picked),
        LogisticsCode.Of(DeliveryStatus.Packed),
        LogisticsCode.Of(DeliveryStatus.Staged),
        LogisticsCode.Of(DeliveryStatus.PendingApproval),
        LogisticsCode.Of(DeliveryStatus.OnHold)
    ];

    /// <summary>"In flight": on a delivery that has not been goods-issued, DRAFT included. Shared with the A33 creator.</summary>
    internal static readonly string[] BeforeIssue = [LogisticsCode.Of(DeliveryStatus.Draft), .. Holding];

    private static readonly string DraftCode  = LogisticsCode.Of(DeliveryStatus.Draft);
    private static readonly string OnHoldCode = LogisticsCode.Of(DeliveryStatus.OnHold);

    private readonly LogisticsDbContext _db;

    public SaleOrderDeliveryQuantities(LogisticsDbContext db) => _db = db;

    public Task<IReadOnlyDictionary<Guid, decimal>> GetInFlightBySoLineAsync(Guid saleOrderUuid, CancellationToken ct = default) =>
        SumBySoLineAsync(saleOrderUuid, BeforeIssue, heldOnly: false, ct);

    public Task<IReadOnlyDictionary<Guid, decimal>> GetHeldBySoLineAsync(Guid saleOrderUuid, CancellationToken ct = default) =>
        SumBySoLineAsync(saleOrderUuid, Holding, heldOnly: true, ct);

    private async Task<IReadOnlyDictionary<Guid, decimal>> SumBySoLineAsync(
        Guid saleOrderUuid, string[] statuses, bool heldOnly, CancellationToken ct) =>
        await _db.DeliveryOrderLines.AsNoTracking()
            .Where(l => l.SoLineUuid != null
                     && !l.DeliveryOrder.IsDelete
                     && l.DeliveryOrder.SaleOrderUuid == saleOrderUuid
                     && statuses.Contains(l.DeliveryOrder.Status)
                     // Belt and braces for rows the state machine never made (imports, hand edits): a hold that
                     // interrupted a DRAFT holds nothing either.
                     && !(heldOnly && l.DeliveryOrder.Status == OnHoldCode && l.DeliveryOrder.StatusBeforeHold == DraftCode))
            .GroupBy(l => l.SoLineUuid!.Value)
            .Select(g => new { SoLineUuid = g.Key, Qty = g.Sum(x => x.QtyOrdered) })
            .ToDictionaryAsync(x => x.SoLineUuid, x => x.Qty, ct);
}
