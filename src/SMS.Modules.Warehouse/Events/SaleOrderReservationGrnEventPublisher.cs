using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Services;
using SMS.Modules.Warehouse.Data;

namespace SMS.Modules.Warehouse.Events;

// A29-P5-06 §6.4 — GRN approval's hook for sale orders. GrnStatusHandler has already posted the
// stock, updated the PO and committed the approval by the time this runs, which is what makes
// reserving it possible (a reservation can only hold stock that is on the shelf) and what makes a
// failure here harmless to the approval itself: the handler catches and logs any publisher's
// exception and carries on with the rest. Deliberately thin — this translates a Warehouse GRN into
// Demand's neutral GrnReceipt and leaves every sale order decision to ISaleOrderGrnLinkService.
internal sealed class SaleOrderReservationGrnEventPublisher : IGrnEventPublisher
{
    private readonly WarehouseDbContext _wh;
    private readonly ISaleOrderGrnLinkService _link;

    public SaleOrderReservationGrnEventPublisher(WarehouseDbContext wh, ISaleOrderGrnLinkService link)
    {
        _wh   = wh;
        _link = link;
    }

    public async Task PublishGrnApprovedAsync(GrnApprovedEvent evt)
    {
        var grn = await _wh.Grns.AsNoTracking()
            .Include(g => g.Lines)
            .FirstOrDefaultAsync(g => g.UUID == evt.GrnUuid && !g.IsDelete);

        // No warehouse means nothing was posted anywhere, so there is nothing to reserve.
        if (grn?.WarehouseUuid is not { } warehouseUuid) return;

        var lines = grn.Lines
            .Where(l => l.VariantUuid.HasValue && l.PostedQty > 0)
            .GroupBy(l => l.VariantUuid!.Value)
            .Select(g => new GrnReceiptLine(g.Key, g.Sum(l => l.PostedQty)))
            .ToList();
        if (lines.Count == 0) return;

        await _link.ReserveForGrnAsync(new GrnReceipt(
            evt.PoUuid, evt.GrnUuid, evt.GrnNumber, warehouseUuid, lines, evt.ApprovedBy));
    }
}
