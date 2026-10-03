using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// A33 PD-04 (BR-C4-06/07, D-15) — what Demand's sale order cancel calls, <b>before</b> it releases the order's own
/// holds and from inside its per-order lock: every delivery of the order that has not been goods-issued is cancelled
/// through the ordinary delivery cancel (so a released delivery's hold goes back to the order first, which Demand's
/// release then frees), and the issued ones are reported, untouched, for a manual reversal.
/// <para>
/// Takes <b>no</b> sale order lock (Demand holds it) and never touches Demand's context. Idempotent: a retry cancels
/// nothing more and reports nothing as cancelled.
/// </para>
/// </summary>
internal sealed class SaleOrderDeliveryCanceller : ISaleOrderDeliveryCanceller
{
    private readonly IDeliveryStatusRepository _status;

    public SaleOrderDeliveryCanceller(IDeliveryStatusRepository status) => _status = status;

    public Task<SaleOrderDeliveryCancellationResult> CancelOpenAsync(
        Guid organizationId, Guid saleOrderUuid, string reason, int userId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty)
            throw new BadRequestException("A sale order's deliveries can only be cancelled for a known organization.");

        return _status.CancelOpenForSaleOrderAsync(
            organizationId, saleOrderUuid,
            string.IsNullOrWhiteSpace(reason) ? "Sale order cancelled." : reason, userId, ct);
    }
}
