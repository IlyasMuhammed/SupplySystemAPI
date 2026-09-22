using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

/// <summary>
/// Holds and frees stock for every module that needs to.
/// <para>
/// This is the only code that writes <see cref="InventoryItem.QtyReserved"/>. Availability is
/// computed from that counter, and the reservation rows are the record of who put it there — if
/// the two are written from different places they eventually disagree, and nothing notices until
/// somebody cannot ship goods the system says are free.
/// </para>
/// <para>
/// Every operation commits the detail row and the counter in <b>one transaction</b>. The MIR path
/// this replaces saved them separately, so a failure between the two left a hold recorded with no
/// counter to back it.
/// </para>
/// </summary>
internal sealed class StockReservationService : IStockReservationService
{
    private readonly InventoryDbContext _db;

    public StockReservationService(InventoryDbContext db) => _db = db;

    public async Task<ReservationResult> ReserveAsync(
        string sourceType,
        Guid sourceUuid,
        IReadOnlyList<ReservationRequest> requests,
        int userId,
        DateTime? expiresAt = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
            return new ReservationResult(true, []);

        var lines = new List<ReservationLineResult>();
        var plan  = new List<(InventoryItem Item, decimal Quantity, ReservationRequest Request)>();

        // Rows already spoken for earlier in this same call. Two lines of one document can ask
        // for the same variant, and without this the second would be planned against stock the
        // first has already claimed — the counter is not written until the transaction below.
        var claimed = new Dictionary<int, decimal>();

        foreach (var request in requests)
        {
            if (request.Quantity <= 0)
            {
                lines.Add(new ReservationLineResult(
                    request.VariantUuid, request.SourceLineUuid, request.Quantity, 0,
                    request.Quantity, 0, "A reservation must be for more than zero."));
                continue;
            }

            var candidates = await CandidatesAsync(request, ct);
            var allocation = Allocate(candidates, request.Quantity, claimed);

            if (allocation.Shortfall > 0)
            {
                lines.Add(new ReservationLineResult(
                    request.VariantUuid, request.SourceLineUuid, request.Quantity, 0,
                    allocation.Shortfall, allocation.Available,
                    allocation.Available <= 0 && candidates.Count == 0
                        ? "No stock record exists for this item in the requested warehouse."
                        : $"Only {allocation.Available:0.###} available."));
                continue;
            }

            foreach (var (item, quantity) in allocation.Take)
            {
                plan.Add((item, quantity, request));
                claimed[item.Id] = claimed.GetValueOrDefault(item.Id) + quantity;
            }

            lines.Add(new ReservationLineResult(
                request.VariantUuid, request.SourceLineUuid, request.Quantity,
                request.Quantity, 0, allocation.Available, null));
        }

        // All or nothing. A document holding some of what it promised is worse than one that
        // refused: the shortfall is invisible until someone tries to pick it.
        //
        // Keyed on Reason rather than Shortfall alone — a request for zero or a negative
        // quantity is refused too, and its shortfall is legitimately zero, so testing the
        // shortfall by itself would let it through as a successful reservation of nothing.
        if (lines.Any(l => l.Reason is not null))
            return new ReservationResult(false, lines);

        var now = DateTime.UtcNow;

        await InTransactionAsync(async () =>
        {
            foreach (var (item, quantity, request) in plan)
            {
                _db.StockReservations.Add(new StockReservation
                {
                    UUID            = Guid.NewGuid(),
                    OrganizationId  = item.OrganizationId,
                    InventoryItemId = item.Id,
                    VariantUuid     = request.VariantUuid,
                    WarehouseId     = item.WarehouseId,
                    ReservedQty     = quantity,
                    SourceType      = sourceType,
                    SourceUuid      = sourceUuid,
                    SourceLineUuid  = request.SourceLineUuid,
                    Status          = StockReservation.StatusActive,
                    ReservedAt      = now,
                    ReservedBy      = userId,
                    ExpiresAt       = expiresAt
                });

                item.QtyReserved += quantity;
                item.LastUpdated  = now;
            }

            await _db.SaveChangesAsync(ct);
        }, ct);

        return new ReservationResult(true, lines);
    }

    public Task<int> ReleaseBySourceAsync(
        string sourceType, Guid sourceUuid, string reason, int userId, CancellationToken ct = default) =>
        CloseAsync(sourceType, sourceUuid, StockReservation.StatusReleased, reason, userId, ct);

    public Task<int> ConsumeBySourceAsync(
        string sourceType, Guid sourceUuid, int userId, CancellationToken ct = default) =>
        CloseAsync(sourceType, sourceUuid, StockReservation.StatusConsumed,
                   "Stock issued against this document.", userId, ct);

    public async Task<decimal> ConsumeLineAsync(
        string sourceType, Guid sourceUuid, Guid sourceLineUuid, decimal quantity,
        int userId, CancellationToken ct = default)
    {
        if (quantity <= 0) return 0m;

        var held = await _db.StockReservations
            .Where(r => r.SourceType == sourceType
                     && r.SourceUuid == sourceUuid
                     && r.SourceLineUuid == sourceLineUuid
                     && r.Status == StockReservation.StatusActive)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        if (held.Count == 0) return 0m;

        var itemIds = held.Select(r => r.InventoryItemId).Distinct().ToList();
        var items   = await _db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToListAsync(ct);

        var now       = DateTime.UtcNow;
        var remaining = quantity;
        var consumed  = 0m;

        await InTransactionAsync(async () =>
        {
            foreach (var reservation in held)
            {
                if (remaining <= 0) break;

                // Take only what this hold still has. Issuing more than was reserved is possible
                // — stock can be picked that nobody held — and it must not drive the hold, or the
                // counter, negative.
                var take = Math.Min(remaining, reservation.ReservedQty);

                reservation.ReservedQty -= take;
                remaining               -= take;
                consumed                += take;

                if (reservation.ReservedQty <= 0)
                {
                    reservation.ReservedQty   = 0;
                    reservation.Status        = StockReservation.StatusConsumed;
                    reservation.ReleasedAt    = now;
                    reservation.ReleasedBy    = userId;
                    reservation.ReleaseReason = "Stock issued against this document.";
                }

                var item = items.FirstOrDefault(i => i.Id == reservation.InventoryItemId);
                if (item is null) continue;

                item.QtyReserved = Math.Max(0, item.QtyReserved - take);
                item.LastUpdated = now;
            }

            await _db.SaveChangesAsync(ct);
        }, ct);

        return consumed;
    }

    public async Task<decimal> ReleaseAllocationAsync(
        Guid reservationUuid, decimal quantity, string reason, int userId,
        CancellationToken ct = default)
    {
        if (quantity <= 0) return 0m;

        var held = await _db.StockReservations
            .FirstOrDefaultAsync(r => r.UUID == reservationUuid
                                   && r.Status == StockReservation.StatusActive, ct);

        // Already released or consumed: nothing to give back. Silent rather than an error, so a
        // retried confirmation does not fail on work it already did.
        if (held is null) return 0m;

        var give = Math.Min(quantity, held.ReservedQty);
        if (give <= 0) return 0m;

        var item = await _db.InventoryItems.FirstOrDefaultAsync(i => i.Id == held.InventoryItemId, ct);
        var now  = DateTime.UtcNow;

        await InTransactionAsync(async () =>
        {
            held.ReservedQty -= give;

            if (held.ReservedQty <= 0)
            {
                held.ReservedQty   = 0;
                held.Status        = StockReservation.StatusReleased;
                held.ReleasedAt    = now;
                held.ReleasedBy    = userId;
                held.ReleaseReason = reason;
            }

            if (item is not null)
            {
                // Clamped for the same reason every other path clamps: pre-existing drift must
                // not compound into a negative counter.
                item.QtyReserved = Math.Max(0, item.QtyReserved - give);
                item.LastUpdated = now;
            }

            await _db.SaveChangesAsync(ct);
        }, ct);

        return give;
    }

    public async Task<decimal> TransferLineAsync(
        string fromSourceType, Guid fromSourceUuid, Guid fromSourceLineUuid,
        string toSourceType, Guid toSourceUuid, Guid? toSourceLineUuid,
        decimal quantity, int userId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fromSourceType);
        ArgumentException.ThrowIfNullOrWhiteSpace(toSourceType);

        if (quantity <= 0) return 0m;

        var held = await _db.StockReservations
            .Where(r => r.SourceType == fromSourceType
                     && r.SourceUuid == fromSourceUuid
                     && r.SourceLineUuid == fromSourceLineUuid
                     && r.Status == StockReservation.StatusActive
                     && r.ReservedQty > 0)
            .Include(r => r.InventoryItem)
            .ToListAsync(ct);

        if (held.Count == 0) return 0m;

        // Same order the pick list walks them (GetAllocationsAsync), so a partial hand-over takes
        // the soonest-expiring batch first, exactly as issuing the order directly would have.
        held = held
            .OrderBy(r => r.InventoryItem!.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(r => r.InventoryItem!.ExpiryDate)
            .ThenBy(r => r.InventoryItemId)
            .ToList();

        var now       = DateTime.UtcNow;
        var remaining = quantity;
        var moved     = 0m;

        await InTransactionAsync(async () =>
        {
            foreach (var reservation in held)
            {
                if (remaining <= 0) break;

                var take = Math.Min(remaining, reservation.ReservedQty);

                if (take == reservation.ReservedQty)
                {
                    // The whole row changes hands: same stock, same age, new owner.
                    reservation.SourceType          = toSourceType;
                    reservation.SourceUuid          = toSourceUuid;
                    reservation.SourceLineUuid      = toSourceLineUuid;
                    reservation.ExpiresAt           = null;
                    reservation.ExpiryWarningSentAt = null;
                }
                else
                {
                    reservation.ReservedQty -= take;

                    _db.StockReservations.Add(new StockReservation
                    {
                        UUID            = Guid.NewGuid(),
                        OrganizationId  = reservation.OrganizationId,
                        InventoryItemId = reservation.InventoryItemId,
                        VariantUuid     = reservation.VariantUuid,
                        WarehouseId     = reservation.WarehouseId,
                        ReservedQty     = take,
                        SourceType      = toSourceType,
                        SourceUuid      = toSourceUuid,
                        SourceLineUuid  = toSourceLineUuid,
                        Status          = StockReservation.StatusActive,
                        ReservedAt      = now,
                        ReservedBy      = userId
                    });
                }

                remaining -= take;
                moved     += take;
            }

            // The counter is untouched on purpose: the units are exactly as reserved as before.
            await _db.SaveChangesAsync(ct);
        }, ct);

        return moved;
    }

    public async Task<IReadOnlyList<ReservationSummary>> GetBySourceAsync(
        string sourceType, Guid sourceUuid, CancellationToken ct = default) =>
        await _db.StockReservations
            .Where(r => r.SourceType == sourceType && r.SourceUuid == sourceUuid)
            .Include(r => r.InventoryItem!).ThenInclude(i => i.Warehouse)
            .AsNoTracking()
            .Select(r => new ReservationSummary(
                r.UUID, r.VariantUuid, r.InventoryItem!.Warehouse.Uuid,
                r.ReservedQty, r.Status, r.SourceLineUuid))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<StockAllocation>> GetAllocationsAsync(
        string sourceType, Guid sourceUuid, CancellationToken ct = default)
    {
        var held = await _db.StockReservations
            .Where(r => r.SourceType == sourceType
                     && r.SourceUuid == sourceUuid
                     && r.Status == StockReservation.StatusActive)
            .Include(r => r.InventoryItem!).ThenInclude(i => i.Warehouse)
            .Include(r => r.InventoryItem!).ThenInclude(i => i.Bin!).ThenInclude(b => b.Zone)
            .Include(r => r.InventoryItem!).ThenInclude(i => i.Zone!)
            .AsNoTracking()
            .ToListAsync(ct);

        // Same order the allocation was made in, so the pick list reads the way the stock was
        // chosen rather than in row-insert order.
        return held
            .OrderBy(r => r.InventoryItem!.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(r => r.InventoryItem!.ExpiryDate)
            .ThenBy(r => r.InventoryItemId)
            .Select(r =>
            {
                var item = r.InventoryItem!;

                return new StockAllocation(
                    r.UUID, r.VariantUuid, r.SourceLineUuid, r.ReservedQty,
                    item.Warehouse.Uuid, item.Warehouse.Name,
                    // A bin knows its own zone; an item may also carry one directly.
                    item.Bin?.Zone?.Name ?? item.Zone?.Name,
                    item.Bin?.Code,
                    item.BatchNumber, item.SerialNumber, item.ExpiryDate);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<VariantAvailability>> GetAvailableAsync(
        IReadOnlyList<Guid> variantUuids, Guid? warehouseUuid, CancellationToken ct = default)
    {
        if (variantUuids is null || variantUuids.Count == 0) return [];

        var wanted = variantUuids.Where(id => id != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0) return [];

        // Variant is included, not just filtered on: the grouping below runs in memory, and a
        // navigation that was only used in the WHERE clause comes back null.
        var query = _db.InventoryItems
            .Include(i => i.Warehouse)
            .Include(i => i.Variant)
            .Where(i => wanted.Contains(i.Variant.Uuid));

        if (warehouseUuid is { } uuid)
            query = query.Where(i => i.Warehouse.Uuid == uuid);

        var items = await query.ToListAsync(ct);

        return items
            .GroupBy(i => i.Variant.Uuid)
            .Select(g =>
            {
                // The same choice a reservation would make, so the figure shown is the figure a
                // reservation is measured against.
                var best = BestWarehouse(g.ToList(), null);

                return best is null
                    ? new VariantAvailability(g.Key, null, null, 0m)
                    : new VariantAvailability(
                        g.Key, best.Rows[0].Warehouse.Uuid, best.Rows[0].Warehouse.Name, best.Free);
            })
            .ToList();
    }

    // ── A29-P4-05 §4.4/§5.1 — expiry sweep support ──────────────────────────────
    // IgnoreQueryFilters explicitly, not the ambient tenant filter: a bare recurring job has no
    // HttpContext and so no real organization to filter to, same reasoning as
    // DocumentNumberGenerator's design-time-equivalent read. The caller groups the result by
    // OrganizationId itself before doing anything tenant-sensitive with it.

    public Task<IReadOnlyList<ExpiringReservation>> GetExpiredAsync(string sourceType, CancellationToken ct = default) =>
        QueryExpiring(sourceType, r => r.ExpiresAt < DateTime.UtcNow, ct);

    public Task<IReadOnlyList<ExpiringReservation>> GetExpiringWithinAsync(
        string sourceType, TimeSpan within, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var horizon = now.Add(within);
        return QueryExpiring(sourceType,
            r => r.ExpiresAt >= now && r.ExpiresAt <= horizon && r.ExpiryWarningSentAt == null, ct);
    }

    private async Task<IReadOnlyList<ExpiringReservation>> QueryExpiring(
        string sourceType, Expression<Func<StockReservation, bool>> extra, CancellationToken ct)
    {
        return await _db.StockReservations.IgnoreQueryFilters()
            .Where(r => r.SourceType == sourceType && r.Status == StockReservation.StatusActive && r.ExpiresAt != null)
            .Where(extra)
            .Select(r => new ExpiringReservation(
                r.UUID, r.OrganizationId, r.SourceType, r.SourceUuid, r.SourceLineUuid,
                r.VariantUuid, r.ReservedQty, r.ExpiresAt!.Value))
            .ToListAsync(ct);
    }

    public async Task MarkExpiryWarningSentAsync(IReadOnlyList<Guid> reservationUuids, CancellationToken ct = default)
    {
        if (reservationUuids.Count == 0) return;

        var now = DateTime.UtcNow;
        var rows = await _db.StockReservations.IgnoreQueryFilters()
            .Where(r => reservationUuids.Contains(r.UUID))
            .ToListAsync(ct);

        foreach (var row in rows) row.ExpiryWarningSentAt = now;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<ExpiringReservation?> GetByUuidAsync(Guid reservationUuid, CancellationToken ct = default) =>
        await _db.StockReservations.IgnoreQueryFilters()
            .Where(r => r.UUID == reservationUuid && r.ExpiresAt != null)
            .Select(r => new ExpiringReservation(
                r.UUID, r.OrganizationId, r.SourceType, r.SourceUuid, r.SourceLineUuid,
                r.VariantUuid, r.ReservedQty, r.ExpiresAt!.Value))
            .FirstOrDefaultAsync(ct);

    // ── Allocation ────────────────────────────────────────────────────────────

    /// <summary>What is on hand in one row and not already promised to someone else.</summary>
    private static decimal Free(InventoryItem item) => item.QtyOnHand - item.QtyReserved;

    private sealed record Allocation(
        IReadOnlyList<(InventoryItem Item, decimal Quantity)> Take,
        decimal Available,
        decimal Shortfall);

    /// <summary>The rows of one warehouse, in pick order, and what they hold free between them.</summary>
    private sealed record WarehouseStock(IReadOnlyList<InventoryItem> Rows, decimal Free);

    /// <summary>
    /// The single warehouse a line should come out of — the one with the most free stock — with
    /// its rows already in pick order.
    /// </summary>
    private static WarehouseStock? BestWarehouse(
        IReadOnlyList<InventoryItem> candidates, IReadOnlyDictionary<int, decimal>? alreadyClaimed)
    {
        decimal FreeNow(InventoryItem i) =>
            Math.Max(0m, Free(i) - (alreadyClaimed?.GetValueOrDefault(i.Id) ?? 0m));

        return candidates
            .Where(i => FreeNow(i) > 0)
            .GroupBy(i => i.WarehouseId)
            // Most free stock wins; warehouse id only to make ties deterministic.
            .OrderByDescending(g => g.Sum(FreeNow))
            .ThenBy(g => g.Key)
            .Select(g => new WarehouseStock([.. InPickOrder(g)], g.Sum(FreeNow)))
            .FirstOrDefault();
    }

    /// <summary>
    /// Chooses which stock rows a quantity should come out of.
    /// <para>
    /// <b>One warehouse, but as many rows within it as it takes.</b> Stock is held per
    /// (warehouse, batch, serial, bin), so a batch-tracked item's stock is spread across a row per
    /// batch. Treating one row as the limit meant a warehouse holding 40 and 30 of the same item
    /// in two batches refused a line for 60 — stock that is on the shelf and free. Splitting
    /// across <em>warehouses</em> is still refused: a picker walking to a second building is a
    /// different problem from walking to a second bin, and callers that want it should ask per
    /// warehouse.
    /// </para>
    /// <para>
    /// Rows are consumed <b>FEFO</b> — earliest expiry first, so the stock most at risk of being
    /// written off leaves first — falling back to arrival order for rows that never expire, which
    /// is plain FIFO. This is where the pick order is really decided: the pick list (T-24) reports
    /// the rows this chose rather than choosing again, so what is picked is always what is held.
    /// </para>
    /// <para>
    /// <see cref="ReserveAsync"/> and <see cref="GetAvailableAsync"/> both come through here, so a
    /// preview and the reservation that follows it cannot disagree.
    /// </para>
    /// </summary>
    /// <param name="alreadyClaimed">
    /// Quantities taken by earlier lines of the same call, which are not yet on the rows.
    /// </param>
    private static Allocation Allocate(
        IReadOnlyList<InventoryItem> candidates,
        decimal wanted,
        IReadOnlyDictionary<int, decimal>? alreadyClaimed)
    {
        var best = BestWarehouse(candidates, alreadyClaimed);

        if (best is null)          return new Allocation([], 0m, wanted);
        if (best.Free < wanted)    return new Allocation([], best.Free, wanted - best.Free);

        decimal FreeNow(InventoryItem i) =>
            Math.Max(0m, Free(i) - (alreadyClaimed?.GetValueOrDefault(i.Id) ?? 0m));

        var take      = new List<(InventoryItem, decimal)>();
        var remaining = wanted;

        foreach (var row in best.Rows)
        {
            if (remaining <= 0) break;

            var amount = Math.Min(remaining, FreeNow(row));
            if (amount <= 0) continue;

            take.Add((row, amount));
            remaining -= amount;
        }

        return new Allocation(take, best.Free, 0m);
    }

    /// <summary>
    /// FEFO, then FIFO for rows with no expiry date. <c>Id</c> is the arrival proxy: a row is
    /// created the first time a batch is received into a warehouse, so ascending id is the order
    /// the stock turned up in.
    /// </summary>
    private static IEnumerable<InventoryItem> InPickOrder(IEnumerable<InventoryItem> rows) =>
        rows.OrderBy(i => i.ExpiryDate.HasValue ? 0 : 1)
            .ThenBy(i => i.ExpiryDate)
            .ThenBy(i => i.Id);

    // ── Shared close path ─────────────────────────────────────────────────────

    /// <summary>
    /// Releasing and consuming differ only in what the audit trail says: both end the hold and
    /// both decrement the counter by the same amount.
    /// </summary>
    private async Task<int> CloseAsync(
        string sourceType, Guid sourceUuid, string status, string reason, int userId, CancellationToken ct)
    {
        var held = await _db.StockReservations
            .Where(r => r.SourceType == sourceType
                     && r.SourceUuid == sourceUuid
                     && r.Status == StockReservation.StatusActive)
            .ToListAsync(ct);

        // Idempotent: nothing active means nothing to do, which is what a second cancel or a
        // retried job should see rather than an error.
        if (held.Count == 0) return 0;

        var itemIds = held.Select(r => r.InventoryItemId).Distinct().ToList();
        var items   = await _db.InventoryItems.Where(i => itemIds.Contains(i.Id)).ToListAsync(ct);

        var now = DateTime.UtcNow;

        await InTransactionAsync(async () =>
        {
            foreach (var reservation in held)
            {
                reservation.Status        = status;
                reservation.ReleasedAt    = now;
                reservation.ReleasedBy    = userId;
                reservation.ReleaseReason = reason;

                var item = items.FirstOrDefault(i => i.Id == reservation.InventoryItemId);
                if (item is null) continue;

                // Clamped at zero. If the counter and the rows have already drifted, refusing to
                // go negative keeps availability believable rather than compounding the error.
                item.QtyReserved = Math.Max(0, item.QtyReserved - reservation.ReservedQty);
                item.LastUpdated = now;
            }

            await _db.SaveChangesAsync(ct);
        }, ct);

        return held.Count;
    }

    /// <summary>
    /// Runs the write inside a transaction under the context's execution strategy, which is
    /// required because the DbContext is configured with retry-on-failure.
    /// <para>
    /// When the caller has already begun a transaction on this context, the write joins it and the
    /// caller commits. A material issue voucher posts its stock movement and consumes its hold in
    /// one transaction, and a second BEGIN on the same connection is an error, not a nested scope.
    /// </para>
    /// </summary>
    private async Task InTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        // The in-memory provider used by tests supports neither transactions nor an execution
        // strategy; the write itself is still exercised.
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }

        var strategy = _db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await work();
            await tx.CommitAsync(ct);
        });
    }

    private async Task<List<InventoryItem>> CandidatesAsync(ReservationRequest request, CancellationToken ct)
    {
        var query = _db.InventoryItems
            .Include(i => i.Warehouse)
            .Where(i => i.Variant.Uuid == request.VariantUuid);

        if (request.WarehouseUuid is { } warehouseUuid)
            query = query.Where(i => i.Warehouse.Uuid == warehouseUuid);

        return await query.ToListAsync(ct);
    }
}
