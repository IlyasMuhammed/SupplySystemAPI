namespace SMS.Modules.Demand.Services;

// A29-P4-02 §4.3. Per-line only — the order header's own DRAFT -> CONFIRMED transition and the
// timeline events stay ISaleOrderService.ConfirmAsync's job, which calls this first. Returns what it
// reserved (A29-P5-07) so that job can emit SO_STOCK_RESERVED with the quantity and warehouse
// without re-deriving them; a line that reserved nothing (back-to-back, drop ship) is simply absent.
public interface IAvailabilityCheckService
{
    Task<IReadOnlyList<LineReservation>> CheckAndReserveAsync(Guid saleOrderUuid, int userId);
}

public sealed record LineReservation(Guid LineUuid, Guid VariantUuid, decimal ReservedQty, Guid? WarehouseUuid, string? WarehouseName);
