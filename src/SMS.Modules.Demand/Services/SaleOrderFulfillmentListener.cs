using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A30 Phase 4 Track C / decision D1. Told after every allocation run anywhere (a production
/// order's own plan touching the same variant, a chained order's Finished Goods Receipt, a manual
/// run) — when a `SALES_ORDER` demand this run evaluated picked up a real on-hand hold, moves that
/// hold from the engine's own bookkeeping (source <c>ALLOCATION</c>, keyed by the demand's registry
/// uuid) onto the sale order line the delivery pipeline already reads holds from (source
/// <c>SALES_ORDER</c>, keyed by the order and line uuid — <see cref="AvailabilityCheckService"/>'s
/// own reservations live there too). <see cref="IStockReservationService.TransferLineAsync"/> is
/// exactly the re-parenting this needs: the same stock, bin and batch stay held, nothing is
/// re-chosen, and the delivery/pick screens (already reading <c>SALES_ORDER</c>-sourced holds) need
/// no changes to pick up manufactured output at all.
/// <para>
/// Deliberately mirrors <see cref="SaleOrderGrnLinkService"/> (the same job for a purchased
/// deficit's GRN): only the line's own <c>DeficitQty</c>/<c>Status</c> and a timeline event, never
/// the order header — that transition happens at delivery/invoice time, not at reservation time.
/// </para>
/// </summary>
internal sealed class SaleOrderFulfillmentListener : IAllocationRunListener
{
    private readonly DemandDbContext _db;
    private readonly IStockReservationService _reservations;
    private readonly IBackgroundJobClient _jobs;

    public SaleOrderFulfillmentListener(DemandDbContext db, IStockReservationService reservations, IBackgroundJobClient jobs)
    {
        _db           = db;
        _reservations = reservations;
        _jobs         = jobs;
    }

    public async Task OnAllocationRunAsync(AllocationRunResult result, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        var touched = result.Demands.Where(d => d.DemandType == AllocationDemandType.SalesOrder && d.ReservedQty > 0).ToList();
        if (touched.Count == 0) return;

        var changed = false;
        foreach (var demand in touched)
        {
            if (demand.DemandLineUuid is not { } lineUuid) continue;

            var line = await _db.SaleOrderLines.Include(l => l.SaleOrder)
                .FirstOrDefaultAsync(l => l.UUID == lineUuid, ct);
            if (line is null) continue;
            var order = line.SaleOrder;

            var moved = 0m;
            var holds = await _reservations.GetBySourceAsync(ReservationSourceType.Allocation, demand.Uuid, ct);
            foreach (var hold in holds)
            {
                if (hold.Status != "ACTIVE" || hold.SourceLineUuid is not { } recordUuid) continue;
                moved += await _reservations.TransferLineAsync(
                    ReservationSourceType.Allocation, demand.Uuid, recordUuid,
                    ReservationSourceType.SalesOrder, order.UUID, line.UUID,
                    hold.ReservedQty, userId, ct);
            }
            if (moved <= 0m) continue;

            changed = true;
            line.DeficitQty = Math.Max(0m, (line.DeficitQty ?? 0m) - moved);
            if (line.DeficitQty <= 0m && line.Status == EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open))
                line.Status = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Reserved);

            // §13.4's own event for a reservation reaching a sale order line, same as
            // AvailabilityCheckService's at confirm time and SaleOrderGrnLinkService's from a GRN.
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                order.TraceId,
                new TimelineEvent(
                    SaleOrderTimelineEventTypes.SoStockReserved, "SO", order.UUID, order.SoNumber,
                    DateTime.UtcNow, userId, $"{moved:0.####} reserved from production"),
                null, null, order.OrganizationId));
        }

        if (changed) await _db.SaveChangesAsync(ct);
    }
}
