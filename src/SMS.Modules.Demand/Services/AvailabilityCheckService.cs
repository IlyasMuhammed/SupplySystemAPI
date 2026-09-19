using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Demand.Services;

// A29-P4-02/P4-03 §4.3's four scenarios. Trusts its caller on the order's own status
// (ISaleOrderService.ConfirmAsync already refuses a non-DRAFT order before this ever runs) — this
// class only owns what happens to the lines.
//
// Deliberately does not call SaveChangesAsync — P4-03 §4.5's "all in one transaction" means
// ConfirmAsync commits this method's line mutations and its own header status change together, in
// a single save, so a failure after this method returns can never leave the order CONFIRMED
// without its lines reflecting what was actually reserved, or vice versa. The stock reservation
// itself is a separate atomic unit on Inventory's own database (StockReservationService.ReserveAsync
// commits internally) — a true single transaction spanning two DbContexts has no precedent
// anywhere in this codebase (Material's MirWorkflowService calls the same shared reservation
// service the same two-step way), so this follows that existing pattern rather than reaching for
// TransactionScope on its own authority.
//
// Out of scope, same as this task's own literal wording: back-to-back PO creation (§Section 6 —
// BACK_TO_BACK and SPLIT's deficit only ever becomes a DeficitQty number here, never a PO),
// vendor selection for that PO (§3.3's BEST_MATCH — TC-06), drop-ship PO creation, and every §5
// email. A line only takes the DROP_SHIP path if something upstream already set
// FulfillmentMode = DROP_SHIP before confirm — no API in this addendum yet lets a caller request
// that, so the branch is real but currently unreachable in practice.
internal sealed class AvailabilityCheckService : IAvailabilityCheckService
{
    private readonly DemandDbContext _db;
    private readonly IStockReservationService _stock;
    private readonly ISaleOrderConfigService _config;

    public AvailabilityCheckService(DemandDbContext db, IStockReservationService stock, ISaleOrderConfigService config)
    {
        _db     = db;
        _stock  = stock;
        _config = config;
    }

    public async Task<IReadOnlyList<LineReservation>> CheckAndReserveAsync(Guid saleOrderUuid, int userId)
    {
        var order = await _db.SaleOrders.Include(x => x.Lines).FirstOrDefaultAsync(x => x.UUID == saleOrderUuid)
            ?? throw new NotFoundException("SaleOrder", saleOrderUuid);

        var config    = await _config.GetConfigAsync();
        var expiresAt = DateTime.UtcNow.AddHours(config.ReservationTtlHours);
        var dropShipCode = EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.DropShip);

        var variantUuids = order.Lines.Select(l => l.VariantUuid).Distinct().ToList();
        var available = await _stock.GetAvailableAsync(variantUuids, warehouseUuid: null);
        var byVariant = available.ToDictionary(a => a.VariantUuid);
        var reserved  = new List<LineReservation>();

        foreach (var line in order.Lines)
        {
            // §4.3's fourth scenario — only reachable if a line already asked for it before
            // confirm; no impact on stock or the reservation ledger either way.
            if (line.FulfillmentMode == dropShipCode && config.DropShipEnabled)
            {
                line.AvailableQtyAtConfirm = null;
                line.DeficitQty            = line.Quantity;
                line.Status                = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open);
                continue;
            }

            byVariant.TryGetValue(line.VariantUuid, out var availability);
            var availableQty = availability?.Available ?? 0m;
            line.AvailableQtyAtConfirm = availableQty;

            if (availableQty >= line.Quantity)
            {
                // Scenario 1 — IN_STOCK: available covers the whole line.
                await ReserveLineAsync(order, line, line.Quantity, availability?.WarehouseUuid, expiresAt, userId);
                reserved.Add(new LineReservation(line.UUID, line.VariantUuid, line.Quantity, availability?.WarehouseUuid, availability?.WarehouseName));
                line.DeficitQty      = 0;
                line.FulfillmentMode = EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.InStock);
                line.Status          = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Reserved);
            }
            else if (availableQty > 0)
            {
                // Scenario 2 — SPLIT: reserve exactly what preview said was free (ReserveAsync is
                // all-or-nothing, so asking for more than the preview found would refuse the
                // whole line), track the rest as a deficit for Section 6 to pick up later.
                await ReserveLineAsync(order, line, availableQty, availability?.WarehouseUuid, expiresAt, userId);
                reserved.Add(new LineReservation(line.UUID, line.VariantUuid, availableQty, availability?.WarehouseUuid, availability?.WarehouseName));
                line.DeficitQty      = line.Quantity - availableQty;
                line.FulfillmentMode = EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.Split);
                line.Status          = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Reserved);
            }
            else
            {
                // Scenario 3 — BACK_TO_BACK: nothing on the shelf, nothing to reserve.
                line.DeficitQty      = line.Quantity;
                line.FulfillmentMode = EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.BackToBack);
                line.Status          = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open);
            }
        }

        return reserved;
    }

    private async Task ReserveLineAsync(
        SaleOrder order, SaleOrderLine line, decimal qty, Guid? warehouseUuid, DateTime expiresAt, int userId)
    {
        var result = await _stock.ReserveAsync(
            ReservationSourceType.SalesOrder, order.UUID,
            [new ReservationRequest(line.VariantUuid, warehouseUuid, qty, line.UUID)],
            userId, expiresAt);

        // The preview and the reserve are two calls, not one — something else could have taken
        // the stock in between. Refusing outright beats silently reclassifying the line into a
        // worse scenario the caller never asked to confirm.
        if (!result.Succeeded)
            throw new ConflictException(
                $"Availability changed for variant {line.VariantUuid} while confirming — it is no longer free.");
    }
}
