using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A30 Phase 4 Track C. Registers a `SALES_ORDER` demand for a manufactured product's deficit —
/// the same registry <see cref="Domain.SaleOrderLine"/>'s production material counterpart uses —
/// then raises a production order for whatever the run leaves short. Deliberately does not decide
/// who ends up holding the resulting stock: <see cref="SaleOrderFulfillmentListener"/> is what
/// re-parents a hold this call's own <see cref="IAllocationEngine.AllocateAsync"/> makes (or a
/// later run, once the production order's own Finished Goods Receipt confirms) onto the sale order
/// line the delivery pipeline already knows how to read.
/// </summary>
internal sealed class SaleOrderManufacturingService : ISaleOrderManufacturingService
{
    private readonly DemandDbContext         _db;
    private readonly IAllocationEngine       _engine;
    private readonly IProductionDemandService _production;
    private readonly ILogger<SaleOrderManufacturingService> _log;

    public SaleOrderManufacturingService(
        DemandDbContext db, IAllocationEngine engine, IProductionDemandService production,
        ILogger<SaleOrderManufacturingService> log)
    {
        _db         = db;
        _engine     = engine;
        _production = production;
        _log        = log;
    }

    public async Task FulfillDeficitAsync(Guid saleOrderUuid, Guid saleOrderLineUuid, decimal deficit, int userId, CancellationToken ct = default)
    {
        if (deficit <= 0m) return;

        var line = await _db.SaleOrderLines.AsNoTracking().Include(l => l.SaleOrder)
            .FirstOrDefaultAsync(l => l.UUID == saleOrderLineUuid && l.SaleOrder.UUID == saleOrderUuid, ct);
        if (line is null)
        {
            _log.LogWarning("Manufacturing fulfilment: sale order {So} line {Line} not found.", saleOrderUuid, saleOrderLineUuid);
            return;
        }
        var order = line.SaleOrder;

        var requiredDate = order.ExpectedDeliveryDate ?? DateTime.UtcNow.Date.AddDays(7);
        var demand = await _engine.RegisterDemandAsync(new AllocationDemandRegistration(
            AllocationDemandType.SalesOrder, order.UUID, line.UUID, order.SoNumber,
            line.VariantUuid, null, deficit, requiredDate, AllocationPriority.Normal, order.OrderDate), userId, ct);

        // Stock elsewhere, or supply already on its way (another order's receipt, a chained
        // production order's own output) may already cover some or all of this for free — the same
        // "register, then see what a run leaves short" shape a production order's own materials use.
        await _engine.AllocateAsync(line.VariantUuid, null, userId, ct);

        var refreshed = await _engine.GetDemandAsync(demand.Uuid, ct);
        var shortage  = refreshed?.Shortage ?? deficit;
        if (shortage <= 0m) return;

        await _production.EnsureForSourceAsync(
            line.VariantUuid, shortage, requiredDate, AllocationPriority.Normal,
            ProductionSourceType.SalesOrder, order.UUID, line.UUID, order.SoNumber, userId,
            traceId: order.TraceId, ct: ct);
    }
}
