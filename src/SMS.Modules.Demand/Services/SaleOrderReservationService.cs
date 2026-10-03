using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A32 PE-03/PE-06 (§6.4) — holding and freeing stock for a sale order's lines by hand. Every hold goes through
/// <see cref="IStockReservationService"/> as a <c>SALES_ORDER</c> reservation (source = the order, source line = the
/// line, expiry = now + SaleOrderConfig.ReservationTtlHours so the expiry sweep frees it, BR-C4-06) — the same rows
/// confirm, the GRN link, the delivery pipeline and the sweep already work with. Nothing is stored on the line but the
/// fields those paths keep (Status, DeficitQty); ReservedQty is read back from the ledger (<see cref="SaleOrderHolds"/>).
/// <para>
/// Each change runs under <see cref="SaleOrderHolds.OneChangeAtATimeAsync"/>, the order's own lock, so two reserve
/// calls (or a reserve and a cancel) on one order cannot both act on the same unreserved balance.
/// </para>
/// </summary>
internal sealed class SaleOrderReservationService : ISaleOrderReservationService
{
    private static readonly string[] ReservableOrderStatuses =
    [
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Confirmed),
        EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.PartiallyFulfilled)
    ];

    private static readonly string[] ReservableLineStatuses =
    [
        EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open),
        EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Reserved),
        EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.PartiallyFulfilled)
    ];

    private static readonly string DropShip = EnumCode<SaleOrderLineFulfillmentMode>.Of(SaleOrderLineFulfillmentMode.DropShip);

    private readonly DemandDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IStockReservationService _stock;
    private readonly ISaleOrderConfigService _config;
    private readonly IBackgroundJobClient _jobs;
    private readonly ISaleOrderDeliveryQuantities? _deliveries;

    public SaleOrderReservationService(
        DemandDbContext db, ITenantContext tenant, IStockReservationService stock, ISaleOrderConfigService config,
        IBackgroundJobClient jobs, ISaleOrderDeliveryQuantities? deliveries = null)
    {
        _db         = db;
        _tenant     = tenant;
        _stock      = stock;
        _config     = config;
        _jobs       = jobs;
        _deliveries = deliveries;
    }

    // ── Reserve one line ─────────────────────────────────────────────────────

    public async Task<SaleOrderLineReservationModel?> ReserveLineAsync(
        Guid orderUuid, Guid lineUuid, ReserveSaleOrderLineRequest req, int userId)
    {
        if (!await OwnOrderExistsAsync(orderUuid)) return null;

        return await SaleOrderHolds.OneChangeAtATimeAsync<SaleOrderLineReservationModel?>(_db, orderUuid, async () =>
        {
            var order = await LoadOwnOrderAsync(orderUuid);
            var line  = order?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
            if (order is null || line is null) return null;

            EnsureOrderCanHold(order);
            if (WhyLineCannotHold(line) is { } why)
                throw new BadRequestException(why);

            var holds      = await ReadHoldsAsync(order);
            var reservable = holds.ReservableFor(line);
            if (reservable <= 0m)
                throw new BadRequestException(
                    $"Nothing is left to reserve on this line: its {line.Quantity:0.####} are already held, on a delivery or delivered.");

            var requested = req.Quantity ?? reservable;
            if (requested <= 0m)
                throw new BadRequestException("The quantity to reserve must be greater than zero.");
            if (requested > reservable)
                throw new BadRequestException(
                    $"Only {reservable:0.####} of this line can still be reserved; {requested:0.####} was asked for.");

            var expiresAt = await ExpiryAsync();
            var result = await ReserveAsync(order, line, requested, req.AllowPartial, req.WarehouseUuid, expiresAt, userId);
            await _db.SaveChangesAsync();
            return result;
        });
    }

    // ── Release one line ─────────────────────────────────────────────────────

    public async Task<SaleOrderLineReservationModel?> ReleaseLineAsync(
        Guid orderUuid, Guid lineUuid, ReleaseSaleOrderLineRequest req, int userId)
    {
        if (!await OwnOrderExistsAsync(orderUuid)) return null;

        return await SaleOrderHolds.OneChangeAtATimeAsync<SaleOrderLineReservationModel?>(_db, orderUuid, async () =>
        {
            var order = await LoadOwnOrderAsync(orderUuid);
            var line  = order?.Lines.FirstOrDefault(l => l.UUID == lineUuid);
            if (order is null || line is null) return null;

            var active = (await _stock.GetBySourceAsync(ReservationSourceType.SalesOrder, order.UUID) ?? [])
                .Where(h => h.Status == "ACTIVE" && h.SourceLineUuid == line.UUID)
                .ToList();
            var held = active.Sum(h => h.ReservedQty);
            if (held <= 0m)
                throw new BadRequestException("This line holds no stock to release.");

            var requested = req.Quantity ?? held;
            if (requested <= 0m)
                throw new BadRequestException("The quantity to release must be greater than zero.");
            if (requested > held)
                throw new BadRequestException($"This line holds {held:0.####}; {requested:0.####} cannot be released.");

            var reason = string.IsNullOrWhiteSpace(req.Reason) ? "Released by hand from the sale order." : req.Reason.Trim();
            var freed  = 0m;
            // Newest holds first: what was added last is given back first, and an older hold keeps its earlier expiry.
            foreach (var hold in active.AsEnumerable().Reverse())
            {
                if (freed >= requested) break;
                freed += await _stock.ReleaseAllocationAsync(hold.Uuid, Math.Min(hold.ReservedQty, requested - freed), reason, userId);
            }

            var holds = await ReadHoldsAsync(order);
            SaleOrderHolds.Reconcile(line, holds);
            await _db.SaveChangesAsync();

            var note = $"{freed:0.####} released" + (string.IsNullOrWhiteSpace(req.Reason) ? "" : $" ({req.Reason.Trim()})");
            Timeline(order, SaleOrderTimelineEventTypes.SoStockReleased, userId, note);

            return Model(order, line, holds, SaleOrderReservationOutcome.Released, requested, freed, 0m, null, null, null);
        });
    }

    // ── Reserve every line ───────────────────────────────────────────────────

    public async Task<SaleOrderReserveAllModel?> ReserveAllAsync(Guid orderUuid, ReserveAllSaleOrderLinesRequest req, int userId)
    {
        if (!await OwnOrderExistsAsync(orderUuid)) return null;

        return await SaleOrderHolds.OneChangeAtATimeAsync<SaleOrderReserveAllModel?>(_db, orderUuid, async () =>
        {
            var order = await LoadOwnOrderAsync(orderUuid);
            if (order is null) return null;
            EnsureOrderCanHold(order);

            var expiresAt = await ExpiryAsync();
            var results = new List<SaleOrderLineReservationModel>();

            foreach (var line in order.Lines.OrderBy(l => l.Id))
            {
                var holds = await ReadHoldsAsync(order);
                var why = WhyLineCannotHold(line)
                       ?? (holds.ReservableFor(line) <= 0m ? "Nothing is left to reserve on this line." : null);
                if (why is not null)
                {
                    results.Add(Model(order, line, holds, SaleOrderReservationOutcome.Skipped, 0m, 0m, 0m, null, null, why));
                    continue;
                }

                results.Add(await ReserveAsync(order, line, holds.ReservableFor(line), req.AllowPartial, null, expiresAt, userId));
                // The ledger has already committed this line's hold; keep the line in step before moving on.
                await _db.SaveChangesAsync();
            }

            var reserved = EnumCode<SaleOrderReservationOutcome>.Of(SaleOrderReservationOutcome.Reserved);
            var partial  = EnumCode<SaleOrderReservationOutcome>.Of(SaleOrderReservationOutcome.Partial);
            return new SaleOrderReserveAllModel
            {
                Lines              = results,
                ReservedLineCount  = results.Count(r => r.Outcome == reserved),
                PartialLineCount   = results.Count(r => r.Outcome == partial),
                UnchangedLineCount = results.Count(r => r.Outcome != reserved && r.Outcome != partial)
            };
        });
    }

    // ── The one reservation step ─────────────────────────────────────────────

    /// <summary>
    /// Holds up to <paramref name="requested"/> of the line from one warehouse — the named one, else the one with the
    /// most free stock (the confirm rule). The preview and the hold are two calls, so a hold that fails because the stock
    /// was just taken is a 409, never a silent partial.
    /// </summary>
    private async Task<SaleOrderLineReservationModel> ReserveAsync(
        SaleOrder order, SaleOrderLine line, decimal requested, bool allowPartial, Guid? warehouseUuid, DateTime expiresAt, int userId)
    {
        var availability = (await _stock.GetAvailableAsync([line.VariantUuid], warehouseUuid) ?? [])
            .FirstOrDefault(a => a.VariantUuid == line.VariantUuid);
        var available = Math.Max(0m, availability?.Available ?? 0m);

        if (available <= 0m)
            return Model(order, line, await ReadHoldsAsync(order), SaleOrderReservationOutcome.NoneAvailable,
                requested, 0m, 0m, availability?.WarehouseUuid ?? warehouseUuid, availability?.WarehouseName,
                "None of this item is free in any single warehouse.");

        if (available < requested && !allowPartial)
            return Model(order, line, await ReadHoldsAsync(order), SaleOrderReservationOutcome.NeedsConfirmation,
                requested, 0m, available, availability!.WarehouseUuid, availability.WarehouseName,
                $"Available: {available:0.####}, required: {requested:0.####}. Reserve the {available:0.####} that are free?");

        var qty = Math.Min(requested, available);
        var result = await _stock.ReserveAsync(
            ReservationSourceType.SalesOrder, order.UUID,
            [new ReservationRequest(line.VariantUuid, availability!.WarehouseUuid, qty, line.UUID)],
            userId, expiresAt);
        if (!result.Succeeded)
            throw new ConflictException(
                "The stock was taken by something else while reserving. Nothing was held for this line; try again.");

        var holds = await ReadHoldsAsync(order);
        SaleOrderHolds.Reconcile(line, holds);

        var note = string.IsNullOrEmpty(availability.WarehouseName)
            ? $"{qty:0.####} reserved by hand"
            : $"{qty:0.####} reserved by hand ({availability.WarehouseName})";
        Timeline(order, SaleOrderTimelineEventTypes.SoStockReserved, userId, note);

        var outcome = qty < requested ? SaleOrderReservationOutcome.Partial : SaleOrderReservationOutcome.Reserved;
        return Model(order, line, holds, outcome, requested, qty, available, availability.WarehouseUuid, availability.WarehouseName, null);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Task<bool> OwnOrderExistsAsync(Guid orderUuid) =>
        _db.SaleOrders.IgnoreQueryFilters().AnyAsync(o => o.UUID == orderUuid && o.OrganizationId == _tenant.OrganizationId && !o.IsDeleted);

    private Task<SaleOrder?> LoadOwnOrderAsync(Guid orderUuid) =>
        _db.SaleOrders.IgnoreQueryFilters().Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.UUID == orderUuid && o.OrganizationId == _tenant.OrganizationId && !o.IsDeleted);

    private Task<SaleOrderLineHolds> ReadHoldsAsync(SaleOrder order) =>
        SaleOrderHolds.ReadAsync(_stock, _deliveries, order.UUID);

    private async Task<DateTime> ExpiryAsync() =>
        DateTime.UtcNow.AddHours((await _config.GetConfigAsync()).ReservationTtlHours);

    private static void EnsureOrderCanHold(SaleOrder order)
    {
        if (!ReservableOrderStatuses.Contains(order.Status))
            throw new BadRequestException(order.Status == EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Draft)
                ? "A draft sale order is reserved by confirming it."
                : $"Stock can only be reserved for a confirmed or partially fulfilled sale order; {order.SoNumber} is {order.Status}.");
    }

    private static string? WhyLineCannotHold(SaleOrderLine line)
    {
        if (!ReservableLineStatuses.Contains(line.Status))
            return $"A {line.Status} line cannot hold stock.";
        if (line.FulfillmentMode == DropShip)
            return "A drop-ship line is shipped by the supplier; there is no stock of ours to hold.";
        return null;
    }

    private void Timeline(SaleOrder order, string eventType, int userId, string note) =>
        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            order.TraceId,
            new TimelineEvent(eventType, "SO", order.UUID, order.SoNumber, DateTime.UtcNow, userId, note),
            "SO", order.SoNumber));

    private static SaleOrderLineReservationModel Model(
        SaleOrder order, SaleOrderLine line, SaleOrderLineHolds holds, SaleOrderReservationOutcome outcome,
        decimal requested, decimal changed, decimal available, Guid? warehouseUuid, string? warehouseName, string? message) => new()
    {
        LineUuid          = line.UUID,
        VariantUuid       = line.VariantUuid,
        Outcome           = EnumCode<SaleOrderReservationOutcome>.Of(outcome),
        RequestedQty      = requested,
        ChangedQty        = changed,
        AvailableQty      = available,
        WarehouseUuid     = warehouseUuid,
        WarehouseName     = warehouseName,
        ReservedQty       = holds.ReservedFor(line.UUID),
        ReservableQty     = holds.ReservableFor(line),
        DeliveryIndicator = holds.IndicatorFor(order, line),
        LineStatus        = line.Status,
        Message           = message
    };
}
