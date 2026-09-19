using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Services;

internal sealed class SaleOrderGrnLinkService : ISaleOrderGrnLinkService
{
    private static readonly GrnReservationResult NotLinked = new(false, null, 0m, 0m, null);

    private readonly DemandDbContext _db;
    private readonly IStockReservationService _stock;
    private readonly ISaleOrderConfigService _config;
    private readonly ISaleOrderEmailService _email;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<SaleOrderGrnLinkService> _log;

    public SaleOrderGrnLinkService(
        DemandDbContext db, IStockReservationService stock, ISaleOrderConfigService config,
        ISaleOrderEmailService email, IBackgroundJobClient jobs, ILogger<SaleOrderGrnLinkService> log)
    {
        _db     = db;
        _stock  = stock;
        _config = config;
        _email  = email;
        _jobs   = jobs;
        _log    = log;
    }

    public async Task<GrnReservationResult> ReserveForGrnAsync(GrnReceipt receipt)
    {
        var linkedLineId = await _db.PurchaseOrders.AsNoTracking()
            .Where(p => p.UUID == receipt.PoUuid && !p.IsDelete)
            .Select(p => p.LinkedSoLineId)
            .FirstOrDefaultAsync();
        if (linkedLineId is null) return NotLinked;

        var line = await _db.SaleOrderLines.Include(l => l.SaleOrder)
            .FirstOrDefaultAsync(l => l.Id == linkedLineId.Value);
        if (line is null) return NotLinked;
        var order = line.SaleOrder;

        // What of this receipt is the variant the line is waiting on. A PO's lines can be edited
        // after it is raised (§6.2), so a receipt need not contain it at all.
        var received = receipt.Lines.Where(l => l.VariantUuid == line.VariantUuid).Sum(l => l.PostedQty);

        var (reserved, note) = await TryReserveAsync(receipt, line, order, received);

        // Deficit is the line's unreserved balance: it only ever comes down by what was actually
        // reserved, and the line only leaves OPEN once nothing is left to reserve.
        var deficit = Math.Max(0m, (line.DeficitQty ?? 0m) - reserved);
        if (reserved > 0)
        {
            line.DeficitQty = deficit;
            if (deficit == 0m && line.Status == EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open))
                line.Status = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Reserved);
            // The reservation is already committed on Inventory's own database by now; a failure
            // here leaves a hold the line does not know about, which the expiry sweep (P4-05)
            // releases when its TTL runs out — no cross-context transaction exists to lean on.
            await _db.SaveChangesAsync();
        }

        EnqueueTimeline(order, receipt, received, reserved, note);

        if (reserved > 0)
            await _email.SendGrnReceivedAsync(order.UUID, receipt.GrnNumber, received, reserved);

        return new GrnReservationResult(true, order.UUID, reserved, deficit, note);
    }

    private async Task<(decimal Reserved, string? Note)> TryReserveAsync(
        GrnReceipt receipt, SaleOrderLine line, SaleOrder order, decimal received)
    {
        if (received <= 0m)
            return (0m, "The receipt holds none of the variant this line is waiting on.");

        // A cancelled order's goods still arrive — the PO was already sent — but they go back to
        // free stock rather than being held for an order nobody is fulfilling.
        if (order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed)
            && order.Status != EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled))
            return (0m, $"Sale order {order.SoNumber} is {order.Status}.");

        // Never reserve past what the line still lacks: a PO can be edited to buy more than the
        // deficit (§3.4), and the extra is free stock, not this order's.
        var wanted = Math.Min(received, line.DeficitQty ?? 0m);
        if (wanted <= 0m)
            return (0m, "The line's deficit is already covered.");

        // Same preview-then-reserve as AvailabilityCheckService's SPLIT scenario, for the same
        // reason: ReserveAsync is all-or-nothing, and something else may have taken part of what
        // was just posted between the posting and this call.
        var available = (await _stock.GetAvailableAsync([line.VariantUuid], receipt.WarehouseUuid)).FirstOrDefault();
        var qty = Math.Min(wanted, available?.Available ?? 0m);
        if (qty <= 0m)
        {
            _log.LogWarning(
                "GRN {GrnNumber} received {Qty} of variant {Variant} for sale order {SoNumber}, but none of it is free to reserve.",
                receipt.GrnNumber, received, line.VariantUuid, order.SoNumber);
            return (0m, "None of the received stock was free to reserve.");
        }

        var ttlHours = (await _config.GetConfigAsync()).ReservationTtlHours;
        var result = await _stock.ReserveAsync(
            ReservationSourceType.SalesOrder, order.UUID,
            [new ReservationRequest(line.VariantUuid, receipt.WarehouseUuid, qty, line.UUID)],
            receipt.ApprovedBy, DateTime.UtcNow.AddHours(ttlHours));

        return result.Succeeded
            ? (qty, qty < wanted ? "Only part of the received stock was free to reserve." : null)
            : (0m, "The received stock was taken before it could be reserved.");
    }

    // §13.4 — GRN_FOR_SO_PO ("40 received, auto-reserved") then SO_STOCK_RESERVED ("40 from GRN"),
    // both on the sale order's trace (the PO and GRN carry it too). Organization passed explicitly
    // (P5-04/§13.7): this runs inside GRN approval, which can be reached from a background job.
    private void EnqueueTimeline(SaleOrder order, GrnReceipt receipt, decimal received, decimal reserved, string? note)
    {
        var receiptNote = $"{received:0.####} received, {reserved:0.####} reserved for {order.SoNumber}"
            + (note is null ? "" : $" ({note})");

        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            order.TraceId,
            new TimelineEvent(
                SaleOrderTimelineEventTypes.GrnForSoPo, "GRN", receipt.GrnUuid, receipt.GrnNumber,
                DateTime.UtcNow, receipt.ApprovedBy, receiptNote),
            null, null, order.OrganizationId));

        if (reserved > 0)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                order.TraceId,
                new TimelineEvent(
                    SaleOrderTimelineEventTypes.SoStockReserved, "SO", order.UUID, order.SoNumber,
                    DateTime.UtcNow, receipt.ApprovedBy, $"{reserved:0.####} from GRN {receipt.GrnNumber}"),
                null, null, order.OrganizationId));
    }
}
