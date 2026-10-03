using SMS.Modules.Demand.Models;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A32 C4 — manual reservation on sale order lines (owner: FND; PE-03/PE-06). Only a CONFIRMED or
/// PARTIALLY_FULFILLED order's OPEN / RESERVED / PARTIALLY_FULFILLED lines can be reserved; a DRAFT order is
/// reserved by confirming it. Null = order or line not found in the caller's organization (404).
/// </summary>
public interface ISaleOrderReservationService
{
    Task<SaleOrderLineReservationModel?> ReserveLineAsync(Guid orderUuid, Guid lineUuid, ReserveSaleOrderLineRequest req, int userId);
    Task<SaleOrderLineReservationModel?> ReleaseLineAsync(Guid orderUuid, Guid lineUuid, ReleaseSaleOrderLineRequest req, int userId);
    Task<SaleOrderReserveAllModel?> ReserveAllAsync(Guid orderUuid, ReserveAllSaleOrderLinesRequest req, int userId);
}
