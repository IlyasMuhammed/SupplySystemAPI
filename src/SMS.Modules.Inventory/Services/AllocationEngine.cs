using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Inventory.Services;

/// <summary>
/// The shared allocation engine (A30 §14). See <see cref="IAllocationEngine"/> for the contract.
/// <para>
/// A run is one unit of work: it sorts every open demand for a variant by the organization's
/// rules, holds on-hand stock for each in that order through <see cref="IStockReservationService"/>,
/// plans what is left against expected supply, and reports the rest as shortage. Planned
/// allocations are thrown away and remade on every run; holds are kept. So a demand that was held
/// first keeps its stock even when a more urgent demand arrives later — moving a hold is a
/// person's decision (<see cref="ReallocateAsync"/>), never the engine's.
/// </para>
/// <para>
/// Concurrency: the run happens inside one transaction and the counters it touches carry a row
/// version, so two runs (or a run and a goods issue) racing on the same stock cannot both commit.
/// The loser forgets everything it read and runs again, and the second time it sees what the
/// winner did.
/// </para>
/// </summary>
internal sealed class AllocationEngine : IAllocationEngine
{
    private const int MaxAttempts = 3;

    /// <summary>A30 §14.4 in order, with A30-P1-08's kind-of-demand tie-break as step three.</summary>
    private static readonly IReadOnlyList<AllocationRuleDefinition> DefaultRules =
    [
        new("Business priority first",                             10, null, AllocationSortField.Priority,       AllocationSortDirection.Descending, true),
        new("Earlier required date first",                         20, null, AllocationSortField.RequiredDate,   AllocationSortDirection.Ascending,  true),
        new("Customer orders before production, then transfers",   30, null, AllocationSortField.DemandTypeRank, AllocationSortDirection.Ascending,  true),
        new("Older documents first",                               40, null, AllocationSortField.DocumentDate,   AllocationSortDirection.Ascending,  true),
        new("First registered first",                              50, null, AllocationSortField.CreatedAt,      AllocationSortDirection.Ascending,  true)
    ];

    private readonly InventoryDbContext        _db;
    private readonly IStockReservationService  _reservations;
    private readonly IReadOnlyList<IAllocationRunListener>     _listeners;
    private readonly IReadOnlyList<IAllocationReceiptListener> _receiptListeners;

    public AllocationEngine(
        InventoryDbContext db, IStockReservationService reservations,
        IEnumerable<IAllocationRunListener>? listeners = null,
        IEnumerable<IAllocationReceiptListener>? receiptListeners = null)
    {
        _db               = db;
        _reservations     = reservations;
        _listeners        = listeners?.ToList() ?? [];
        _receiptListeners = receiptListeners?.ToList() ?? [];
    }

    // ── Demands ───────────────────────────────────────────────────────────────

    public async Task<DemandAllocationSummary> RegisterDemandAsync(
        AllocationDemandRegistration demand, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(demand);

        if (!AllocationDemandType.IsKnown(demand.DemandType))
            throw new BadRequestException($"'{demand.DemandType}' is not a demand type. Use one of: {string.Join(", ", AllocationDemandType.All)}.");
        if (demand.RequiredQty <= 0)
            throw new BadRequestException("A demand must be for more than zero.");
        if (!AllocationPriority.IsKnown(demand.Priority))
            throw new BadRequestException("Priority must be between 0 (low) and 3 (urgent).");
        if (string.IsNullOrWhiteSpace(demand.Reference))
            throw new BadRequestException("A demand needs the number of the document it is for.");
        if (!await _db.ProductVariants.AnyAsync(v => v.Uuid == demand.VariantUuid && v.IsActive, ct))
            throw new BadRequestException($"Variant {demand.VariantUuid} does not exist or is inactive.");
        if (demand.WarehouseUuid is { } pinned && !await _db.Warehouses.AnyAsync(w => w.Uuid == pinned && w.IsActive, ct))
            throw new BadRequestException($"Warehouse {pinned} does not exist or is inactive.");

        var now = DateTime.UtcNow;
        var row = await _db.AllocationDemands.FirstOrDefaultAsync(d =>
            d.DemandType == demand.DemandType && d.DemandUuid == demand.DemandUuid &&
            d.DemandLineUuid == demand.DemandLineUuid && d.Status == AllocationDemandStatus.Open, ct);

        if (row is null)
        {
            row = new AllocationDemand
            {
                DemandType     = demand.DemandType,
                DemandUuid     = demand.DemandUuid,
                DemandLineUuid = demand.DemandLineUuid,
                VariantUuid    = demand.VariantUuid,
                DocumentDate   = demand.DocumentDate ?? now,
                CreatedBy      = userId,
                CreatedAt      = now
            };
            _db.AllocationDemands.Add(row);
        }
        else if (row.VariantUuid != demand.VariantUuid)
        {
            throw new BadRequestException(
                $"'{row.Reference}' is already registered for a different variant. Cancel that demand and register it again.");
        }

        row.Reference     = demand.Reference.Trim();
        row.WarehouseUuid = demand.WarehouseUuid;
        row.RequiredQty   = demand.RequiredQty;
        row.RequiredDate  = demand.RequiredDate;
        row.Priority      = demand.Priority;
        if (demand.DocumentDate is { } documentDate) row.DocumentDate = documentDate;
        row.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return Summary(await LoadDemandAsync(row.Uuid, ct) ?? row);
    }

    public async Task<DemandAllocationSummary?> GetDemandAsync(Guid demandRegistryUuid, CancellationToken ct = default)
    {
        var demand = await LoadDemandAsync(demandRegistryUuid, ct);
        return demand is null ? null : Summary(demand);
    }

    public async Task<IReadOnlyList<DemandAllocationSummary>> GetDemandsAsync(
        Guid? variantUuid = null, string? demandType = null, Guid? demandUuid = null, bool openOnly = true,
        CancellationToken ct = default)
    {
        var query = _db.AllocationDemands.Include(d => d.Allocations).AsQueryable();
        if (variantUuid is { } v)               query = query.Where(d => d.VariantUuid == v);
        if (!string.IsNullOrWhiteSpace(demandType)) query = query.Where(d => d.DemandType == demandType);
        if (demandUuid is { } du)               query = query.Where(d => d.DemandUuid == du);
        if (openOnly)                           query = query.Where(d => d.Status == AllocationDemandStatus.Open);

        var demands = await query.OrderBy(d => d.RequiredDate).ThenBy(d => d.Id).ToListAsync(ct);
        return demands.Select(Summary).ToList();
    }

    public Task CancelDemandAsync(Guid demandRegistryUuid, string reason, int userId, CancellationToken ct = default) =>
        RetryOnConcurrencyAsync(() => InTransactionAsync(async () =>
        {
            var demand = await LoadDemandAsync(demandRegistryUuid, ct)
                ?? throw new NotFoundException("Allocation demand", demandRegistryUuid);
            if (demand.Status != AllocationDemandStatus.Open) return;

            var now = DateTime.UtcNow;
            await _reservations.ReleaseBySourceAsync(ReservationSourceType.Allocation, demand.Uuid, reason, userId, ct);

            foreach (var record in demand.Allocations.Where(a => a.Status == AllocationStatus.Active))
            {
                record.Status        = AllocationStatus.Cancelled;
                record.ReleasedAt    = now;
                record.ReleaseReason = reason;
            }

            demand.Status    = AllocationDemandStatus.Cancelled;
            demand.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }, ct), ct);

    // ── Expected supply ───────────────────────────────────────────────────────

    public async Task<Guid> RegisterSupplyAsync(AllocationSupplyRegistration supply, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(supply);

        if (!AllocationSupplyType.IsIncoming(supply.SupplyType))
            throw new BadRequestException($"'{supply.SupplyType}' is not an expected supply type. Use one of: {string.Join(", ", AllocationSupplyType.Incoming)}.");
        if (supply.ExpectedQty <= 0)
            throw new BadRequestException("Expected supply must be for more than zero.");
        if (string.IsNullOrWhiteSpace(supply.Reference))
            throw new BadRequestException("Expected supply needs the number of the document it comes from.");
        if (!await _db.ProductVariants.AnyAsync(v => v.Uuid == supply.VariantUuid && v.IsActive, ct))
            throw new BadRequestException($"Variant {supply.VariantUuid} does not exist or is inactive.");
        if (!await _db.Warehouses.AnyAsync(w => w.Uuid == supply.WarehouseUuid && w.IsActive, ct))
            throw new BadRequestException($"Warehouse {supply.WarehouseUuid} does not exist or is inactive.");

        var now = DateTime.UtcNow;
        var row = await _db.AllocationSupplies.FirstOrDefaultAsync(s =>
            s.SupplyType == supply.SupplyType && s.SupplyUuid == supply.SupplyUuid &&
            s.SupplyLineUuid == supply.SupplyLineUuid && s.Status == AllocationSupplyStatus.Open, ct);

        if (row is null)
        {
            row = new AllocationSupply
            {
                SupplyType     = supply.SupplyType,
                SupplyUuid     = supply.SupplyUuid,
                SupplyLineUuid = supply.SupplyLineUuid,
                VariantUuid    = supply.VariantUuid,
                CreatedAt      = now
            };
            _db.AllocationSupplies.Add(row);
        }
        else if (row.VariantUuid != supply.VariantUuid)
        {
            throw new BadRequestException($"'{row.Reference}' is already registered as supply of a different variant.");
        }

        row.Reference     = supply.Reference.Trim();
        row.WarehouseUuid = supply.WarehouseUuid;
        row.ExpectedQty   = supply.ExpectedQty;
        row.ExpectedDate  = supply.ExpectedDate;
        row.UpdatedAt     = now;

        await _db.SaveChangesAsync(ct);
        return row.Uuid;
    }

    public Task<bool> SupplyReceivedAsync(
        string supplyType, Guid supplyUuid, Guid? supplyLineUuid, decimal receivedQty, CancellationToken ct = default) =>
        CloseSupplyAsync(supplyType, supplyUuid, supplyLineUuid, receivedQty,
            "Expected supply received; re-allocated from stock.", ct);

    public Task<bool> CancelSupplyAsync(
        string supplyType, Guid supplyUuid, Guid? supplyLineUuid, CancellationToken ct = default) =>
        CloseSupplyAsync(supplyType, supplyUuid, supplyLineUuid, null, "Expected supply cancelled.", ct);

    /// <summary>
    /// A receipt (quantity given) or a cancellation (null). Either way the planned allocations
    /// leaning on the supply are dropped: after a receipt the stock is real and the next run holds
    /// it by the rules, after a cancellation there is nothing to plan against.
    /// </summary>
    private async Task<bool> CloseSupplyAsync(
        string supplyType, Guid supplyUuid, Guid? supplyLineUuid, decimal? receivedQty, string reason, CancellationToken ct)
    {
        var touchedVariants = new List<Guid>();

        var closed = await RetryOnConcurrencyAsync(() => InTransactionAsync(async () =>
        {
            touchedVariants.Clear();

            // A purchase order registers its line as the supply; a goods receipt knows only the line.
            // Matching on either id lets both sides speak their own language.
            var rows = await _db.AllocationSupplies.Include(s => s.Allocations)
                .Where(s => s.SupplyType == supplyType && s.Status == AllocationSupplyStatus.Open &&
                            (s.SupplyUuid == supplyUuid || s.SupplyLineUuid == supplyUuid) &&
                            (supplyLineUuid == null || s.SupplyLineUuid == supplyLineUuid))
                .OrderBy(s => s.Id)
                .ToListAsync(ct);

            if (rows.Count == 0) return false;

            var now = DateTime.UtcNow;
            var remaining = receivedQty;

            foreach (var supply in rows)
            {
                if (remaining is { } qty)
                {
                    var take = Math.Min(qty, Math.Max(0m, supply.ExpectedQty - supply.ReceivedQty));
                    supply.ReceivedQty += take;
                    remaining = qty - take;
                    if (supply.ReceivedQty >= supply.ExpectedQty) supply.Status = AllocationSupplyStatus.Received;
                }
                else
                {
                    supply.Status = AllocationSupplyStatus.Cancelled;
                }

                if (supply.Status != AllocationSupplyStatus.Open || remaining is null)
                {
                    foreach (var record in supply.Allocations.Where(a => a.Status == AllocationStatus.Active))
                    {
                        record.Status        = AllocationStatus.Released;
                        record.ReleasedAt    = now;
                        record.ReleaseReason = reason;
                    }
                }

                supply.UpdatedAt = now;
            }

            await _db.SaveChangesAsync(ct);
            touchedVariants.AddRange(rows.Select(r => r.VariantUuid).Distinct());
            return true;
        }, ct), ct);

        // After the commit, same reasoning as AllocateAsync's own listeners: a receipt listener's
        // failure must not undo the receipt, and it must see what was actually committed.
        if (closed)
            foreach (var listener in _receiptListeners)
                foreach (var variantUuid in touchedVariants)
                    await listener.OnSupplyClosedAsync(variantUuid, ct);

        return closed;
    }

    // ── Allocation ────────────────────────────────────────────────────────────

    public async Task<AllocationRunResult> AllocateAsync(Guid variantUuid, Guid? warehouseUuid, int userId, CancellationToken ct = default)
    {
        var result = await RetryOnConcurrencyAsync(
            () => InTransactionAsync(() => RunAsync(variantUuid, warehouseUuid, userId, ct), ct), ct);

        // After the commit, so a listener sees what the run decided and its own failure cannot
        // undo the run.
        foreach (var listener in _listeners)
            await listener.OnAllocationRunAsync(result, userId, ct);

        return result;
    }

    public async Task<AllocationRunResult> AllocateForDemandAsync(Guid demandRegistryUuid, int userId, CancellationToken ct = default)
    {
        var demand = await _db.AllocationDemands.AsNoTracking().FirstOrDefaultAsync(d => d.Uuid == demandRegistryUuid, ct)
            ?? throw new NotFoundException("Allocation demand", demandRegistryUuid);

        return await AllocateAsync(demand.VariantUuid, demand.WarehouseUuid, userId, ct);
    }

    /// <summary>
    /// One run. Scoped to a warehouse when given: only demands that may draw from it, only its
    /// stock and its expected supply. A run for all warehouses sees everything.
    /// </summary>
    private async Task<AllocationRunResult> RunAsync(Guid variantUuid, Guid? warehouseUuid, int userId, CancellationToken ct)
    {
        var rules = await EffectiveRulesAsync(ct);
        var now   = DateTime.UtcNow;

        var demands = await _db.AllocationDemands.Include(d => d.Allocations)
            .Where(d => d.VariantUuid == variantUuid && d.Status == AllocationDemandStatus.Open &&
                        (warehouseUuid == null || d.WarehouseUuid == null || d.WarehouseUuid == warehouseUuid))
            .ToListAsync(ct);

        demands.Sort((a, b) => Compare(a, b, rules));

        // Tentative allocations are remade from scratch every run — that is what makes them
        // tentative. Removed rather than kept as RELEASED rows: a plan is bookkeeping, not a decision.
        var dropped = new HashSet<AllocationRecord>();
        foreach (var demand in demands)
        {
            var tentative = demand.Allocations
                .Where(a => a.Status == AllocationStatus.Active &&
                            a.AllocationType is AllocationType.Planned or AllocationType.Soft &&
                            (warehouseUuid == null || a.WarehouseUuid == warehouseUuid))
                .ToList();
            foreach (var record in tentative)
            {
                demand.Allocations.Remove(record);
                _db.AllocationRecords.Remove(record);
                dropped.Add(record);
            }
        }

        var free = await FreeByWarehouseAsync(variantUuid, warehouseUuid, ct);

        var supplies = await _db.AllocationSupplies.Include(s => s.Allocations)
            .Where(s => s.VariantUuid == variantUuid && s.Status == AllocationSupplyStatus.Open &&
                        (warehouseUuid == null || s.WarehouseUuid == warehouseUuid))
            .ToListAsync(ct);
        supplies = supplies.OrderBy(s => s.ExpectedDate ?? DateTime.MaxValue).ThenBy(s => s.Id).ToList();

        var supplyRemaining = supplies.ToDictionary(
            s => s.Id,
            s => s.ExpectedQty - s.ReceivedQty
                 - s.Allocations.Where(a => a.Status == AllocationStatus.Active && !dropped.Contains(a)).Sum(a => a.Remaining));

        decimal reservedTotal = 0m, plannedTotal = 0m, shortageTotal = 0m;
        var position = 0;

        foreach (var demand in demands)
        {
            position++;
            var outstanding = Outstanding(demand);

            // On-hand first: the warehouse with the most free stock, then the next, until covered.
            while (outstanding > 0)
            {
                var candidate = free
                    .Where(kv => kv.Value > 0 && (demand.WarehouseUuid == null || kv.Key == demand.WarehouseUuid))
                    .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key)
                    .Select(kv => (Warehouse: kv.Key, Free: kv.Value))
                    .FirstOrDefault();
                if (candidate.Free <= 0) break;

                var take   = Math.Min(outstanding, candidate.Free);
                var record = NewRecord(demand, variantUuid, candidate.Warehouse, take,
                    AllocationSupplyType.OnHand, null, AllocationType.Reserved, position, userId, now);

                var held = await _reservations.ReserveAsync(
                    ReservationSourceType.Allocation, demand.Uuid,
                    [new ReservationRequest(variantUuid, candidate.Warehouse, take, record.Uuid)],
                    userId, null, ct);

                if (!held.Succeeded)
                {
                    // What this run read as free was already gone. Believe the service and go on.
                    var left = held.Lines[0].Available;
                    free[candidate.Warehouse] = left < take ? left : 0m;
                    continue;
                }

                demand.Allocations.Add(record);
                free[candidate.Warehouse] -= take;
                outstanding   -= take;
                reservedTotal += take;
            }

            // Then expected supply, soonest first.
            foreach (var supply in supplies)
            {
                if (outstanding <= 0) break;
                if (demand.WarehouseUuid is { } pinned && supply.WarehouseUuid != pinned) continue;

                var available = supplyRemaining[supply.Id];
                if (available <= 0) continue;

                var take   = Math.Min(outstanding, available);
                var record = NewRecord(demand, variantUuid, supply.WarehouseUuid, take,
                    supply.SupplyType, supply, AllocationType.Planned, position, userId, now);

                demand.Allocations.Add(record);
                supplyRemaining[supply.Id] -= take;
                outstanding  -= take;
                plannedTotal += take;
            }

            shortageTotal    += outstanding;
            demand.UpdatedAt  = now;
        }

        await _db.SaveChangesAsync(ct);

        return new AllocationRunResult(
            variantUuid, warehouseUuid, demands.Count, reservedTotal, plannedTotal, shortageTotal,
            demands.Select(Summary).ToList());
    }

    public Task ReleaseAsync(Guid allocationUuid, string reason, int userId, CancellationToken ct = default) =>
        RetryOnConcurrencyAsync(() => InTransactionAsync(async () =>
        {
            var record = await _db.AllocationRecords.Include(a => a.Demand)
                .FirstOrDefaultAsync(a => a.Uuid == allocationUuid, ct)
                ?? throw new NotFoundException("Allocation", allocationUuid);
            if (record.Status != AllocationStatus.Active) return;

            await ReleaseHoldAsync(record, record.Remaining, reason, userId, ct);

            var now = DateTime.UtcNow;
            record.Status           = AllocationStatus.Released;
            record.ReleasedAt       = now;
            record.ReleaseReason    = reason;
            record.Demand.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }, ct), ct);

    public Task<AllocationSummary> ReallocateAsync(
        Guid fromAllocationUuid, Guid toDemandRegistryUuid, decimal quantity, string reason, int userId,
        CancellationToken ct = default) =>
        RetryOnConcurrencyAsync(() => InTransactionAsync(async () =>
        {
            var from = await _db.AllocationRecords.Include(a => a.Demand).Include(a => a.Supply)
                .FirstOrDefaultAsync(a => a.Uuid == fromAllocationUuid, ct)
                ?? throw new NotFoundException("Allocation", fromAllocationUuid);
            if (from.Status != AllocationStatus.Active)
                throw new BadRequestException("Only an active allocation can be moved.");
            if (quantity <= 0 || quantity > from.Remaining)
                throw new BadRequestException($"Only {from.Remaining:0.####} of this allocation is left to move.");

            var to = await LoadDemandAsync(toDemandRegistryUuid, ct)
                ?? throw new NotFoundException("Allocation demand", toDemandRegistryUuid);
            if (to.Status != AllocationDemandStatus.Open)
                throw new BadRequestException($"'{to.Reference}' is {to.Status.ToLowerInvariant()}; stock cannot be moved to it.");
            if (to.Id == from.DemandId)
                throw new BadRequestException("The allocation already belongs to that demand.");
            if (to.VariantUuid != from.VariantUuid)
                throw new BadRequestException($"'{to.Reference}' is for a different variant.");
            if (to.WarehouseUuid is { } pinned && pinned != from.WarehouseUuid)
                throw new BadRequestException($"'{to.Reference}' must be supplied from its own warehouse, and this stock is elsewhere.");

            var needed = Outstanding(to);
            if (quantity > needed)
                throw new BadRequestException($"'{to.Reference}' only needs {needed:0.####} more.");

            var now = DateTime.UtcNow;

            // A person moved it, so a run must not move it back: a plan becomes firm.
            var kind = from.AllocationType is AllocationType.Planned or AllocationType.Soft
                ? AllocationType.Firm
                : from.AllocationType;

            var moved = NewRecord(to, from.VariantUuid, from.WarehouseUuid, quantity,
                from.SupplyType, from.Supply, kind, 0, userId, now);

            if (from.AllocationType is AllocationType.Reserved or AllocationType.Firm && from.SupplyType == AllocationSupplyType.OnHand)
            {
                await ReleaseHoldAsync(from, quantity, reason, userId, ct);

                var held = await _reservations.ReserveAsync(
                    ReservationSourceType.Allocation, to.Uuid,
                    [new ReservationRequest(from.VariantUuid, from.WarehouseUuid, quantity, moved.Uuid)],
                    userId, null, ct);

                if (!held.Succeeded)
                    throw new ConflictException(
                        $"The stock freed from '{from.Demand.Reference}' could not be held for '{to.Reference}': {held.Lines[0].Reason}");
            }

            from.AllocatedQty -= quantity;
            if (from.Remaining <= 0)
            {
                from.Status        = from.ConsumedQty > 0 ? AllocationStatus.Consumed : AllocationStatus.Released;
                from.ReleasedAt    = now;
                from.ReleaseReason = reason;
            }
            from.Demand.UpdatedAt = now;

            to.Allocations.Add(moved);
            to.UpdatedAt = now;

            await _db.SaveChangesAsync(ct);
            return Summary(moved, to);
        }, ct), ct);

    public Task<decimal> ConsumeAsync(Guid allocationUuid, decimal quantity, int userId, CancellationToken ct = default) =>
        RetryOnConcurrencyAsync(() => InTransactionAsync(async () =>
        {
            if (quantity <= 0) return 0m;

            var record = await _db.AllocationRecords.Include(a => a.Demand).ThenInclude(d => d.Allocations)
                .FirstOrDefaultAsync(a => a.Uuid == allocationUuid, ct)
                ?? throw new NotFoundException("Allocation", allocationUuid);
            if (record.Status != AllocationStatus.Active)
                throw new BadRequestException("Only an active allocation can be consumed.");
            if (record.SupplyType != AllocationSupplyType.OnHand)
                throw new BadRequestException("Stock that has not arrived cannot be consumed.");

            var take     = Math.Min(quantity, record.Remaining);
            var consumed = await _reservations.ConsumeLineAsync(
                ReservationSourceType.Allocation, record.Demand.Uuid, record.Uuid, take, userId, ct);

            var now = DateTime.UtcNow;
            record.ConsumedQty        += consumed;
            record.Demand.ConsumedQty += consumed;
            if (record.Remaining <= 0) record.Status = AllocationStatus.Consumed;

            if (record.Demand.ConsumedQty >= record.Demand.RequiredQty)
            {
                record.Demand.Status = AllocationDemandStatus.Fulfilled;
                foreach (var other in record.Demand.Allocations.Where(a => a.Status == AllocationStatus.Active))
                {
                    await ReleaseHoldAsync(other, other.Remaining, "Demand fulfilled.", userId, ct);
                    other.Status        = AllocationStatus.Released;
                    other.ReleasedAt    = now;
                    other.ReleaseReason = "Demand fulfilled.";
                }
            }
            record.Demand.UpdatedAt = now;

            await _db.SaveChangesAsync(ct);
            return consumed;
        }, ct), ct);

    // ── Reading ───────────────────────────────────────────────────────────────

    public async Task<AvailabilityResult> GetAvailabilityAsync(Guid variantUuid, Guid? warehouseUuid, CancellationToken ct = default)
    {
        var rows = await _db.InventoryItems.AsNoTracking().Include(i => i.Warehouse)
            .Where(i => i.Variant.Uuid == variantUuid && i.Warehouse.IsActive)
            .ToListAsync(ct);
        if (warehouseUuid is { } wh) rows = rows.Where(i => i.Warehouse.Uuid == wh).ToList();

        var supplies = await _db.AllocationSupplies.AsNoTracking()
            .Where(s => s.VariantUuid == variantUuid && s.Status == AllocationSupplyStatus.Open &&
                        (warehouseUuid == null || s.WarehouseUuid == warehouseUuid))
            .ToListAsync(ct);

        var demands = await _db.AllocationDemands.AsNoTracking().Include(d => d.Allocations)
            .Where(d => d.VariantUuid == variantUuid && d.Status == AllocationDemandStatus.Open &&
                        (warehouseUuid == null || d.WarehouseUuid == null || d.WarehouseUuid == warehouseUuid))
            .ToListAsync(ct);

        return new AvailabilityResult(
            variantUuid, warehouseUuid,
            OnHand:      rows.Sum(i => i.QtyOnHand),
            Reserved:    rows.Sum(i => i.QtyReserved),
            Available:   rows.Sum(i => Math.Max(0m, i.QtyOnHand - i.QtyReserved)),
            Incoming:    supplies.Sum(s => Math.Max(0m, s.ExpectedQty - s.ReceivedQty)),
            OpenDemand:  demands.Sum(d => Math.Max(0m, d.RequiredQty - d.ConsumedQty)),
            Unallocated: demands.Sum(Outstanding));
    }

    public async Task<AllocationSummary?> GetAllocationAsync(Guid allocationUuid, CancellationToken ct = default)
    {
        var record = await _db.AllocationRecords.AsNoTracking().Include(a => a.Demand).Include(a => a.Supply)
            .FirstOrDefaultAsync(a => a.Uuid == allocationUuid, ct);
        return record is null ? null : Summary(record, record.Demand);
    }

    public async Task<AllocationPage> GetAllocationsAsync(AllocationListFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _db.AllocationRecords.AsNoTracking().Include(a => a.Demand).Include(a => a.Supply).AsQueryable();
        if (filter.VariantUuid is { } v)                query = query.Where(a => a.VariantUuid == v);
        if (filter.WarehouseUuid is { } w)              query = query.Where(a => a.WarehouseUuid == w);
        if (!string.IsNullOrWhiteSpace(filter.DemandType)) query = query.Where(a => a.Demand.DemandType == filter.DemandType);
        if (filter.DemandUuid is { } d)                 query = query.Where(a => a.Demand.DemandUuid == d || a.Demand.Uuid == d);
        if (!string.IsNullOrWhiteSpace(filter.Status))  query = query.Where(a => a.Status == filter.Status);

        var page     = filter.Page < 1 ? 1 : filter.Page;
        var pageSize = filter.PageSize < 1 ? 50 : Math.Min(filter.PageSize, 500);
        var total    = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(a => a.AllocatedAt).ThenByDescending(a => a.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);

        return new AllocationPage(items.Select(a => Summary(a, a.Demand)).ToList(), total, page, pageSize);
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<AllocationRuleDefinition>> GetRulesAsync(CancellationToken ct = default) =>
        EffectiveRulesAsync(ct);

    public async Task<IReadOnlyList<AllocationRuleDefinition>> SetRulesAsync(
        IReadOnlyList<AllocationRuleDefinition> rules, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rules);

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.RuleName))
                throw new BadRequestException("Every rule needs a name.");
            if (!AllocationSortField.IsKnown(rule.SortField))
                throw new BadRequestException($"'{rule.SortField}' is not a sort field. Use one of: {string.Join(", ", AllocationSortField.All)}.");
            if (rule.SortDirection is not (AllocationSortDirection.Ascending or AllocationSortDirection.Descending))
                throw new BadRequestException("Sort direction must be ASC or DESC.");
            if (rule.DemandTypeFilter is not null && !AllocationDemandType.IsKnown(rule.DemandTypeFilter))
                throw new BadRequestException($"'{rule.DemandTypeFilter}' is not a demand type.");
        }

        var existing = await _db.AllocationRules.ToListAsync(ct);
        _db.AllocationRules.RemoveRange(existing);

        var now = DateTime.UtcNow;
        _db.AllocationRules.AddRange(rules.Select(r => new AllocationRule
        {
            RuleName         = r.RuleName.Trim(),
            PriorityOrder    = r.PriorityOrder,
            DemandTypeFilter = r.DemandTypeFilter,
            SortField        = r.SortField,
            SortDirection    = r.SortDirection,
            IsActive         = r.IsActive,
            CreatedAt        = now
        }));

        await _db.SaveChangesAsync(ct);
        return await EffectiveRulesAsync(ct);
    }

    private async Task<IReadOnlyList<AllocationRuleDefinition>> EffectiveRulesAsync(CancellationToken ct)
    {
        var configured = await _db.AllocationRules.AsNoTracking()
            .OrderBy(r => r.PriorityOrder).ThenBy(r => r.Id)
            .ToListAsync(ct);

        return configured.Count == 0
            ? DefaultRules
            : configured.Select(r => new AllocationRuleDefinition(
                r.RuleName, r.PriorityOrder, r.DemandTypeFilter, r.SortField, r.SortDirection, r.IsActive)).ToList();
    }

    private static int Compare(AllocationDemand a, AllocationDemand b, IReadOnlyList<AllocationRuleDefinition> rules)
    {
        foreach (var rule in rules.Where(r => r.IsActive).OrderBy(r => r.PriorityOrder))
        {
            if (rule.DemandTypeFilter is { } only && (a.DemandType != only || b.DemandType != only))
                continue;

            var c = rule.SortField switch
            {
                AllocationSortField.Priority       => a.Priority.CompareTo(b.Priority),
                AllocationSortField.RequiredDate   => a.RequiredDate.CompareTo(b.RequiredDate),
                AllocationSortField.DemandTypeRank => AllocationDemandType.Rank(a.DemandType).CompareTo(AllocationDemandType.Rank(b.DemandType)),
                AllocationSortField.DocumentDate   => a.DocumentDate.CompareTo(b.DocumentDate),
                AllocationSortField.CreatedAt      => a.CreatedAt.CompareTo(b.CreatedAt),
                _                                  => 0
            };
            if (rule.SortDirection == AllocationSortDirection.Descending) c = -c;
            if (c != 0) return c;
        }

        return a.Id.CompareTo(b.Id);
    }

    // ── Pieces ────────────────────────────────────────────────────────────────

    private Task<AllocationDemand?> LoadDemandAsync(Guid uuid, CancellationToken ct) =>
        _db.AllocationDemands.Include(d => d.Allocations).ThenInclude(a => a.Supply)
            .FirstOrDefaultAsync(d => d.Uuid == uuid, ct);

    /// <summary>Free stock per active warehouse: on hand less reserved, never below zero per row.</summary>
    private async Task<Dictionary<Guid, decimal>> FreeByWarehouseAsync(Guid variantUuid, Guid? warehouseUuid, CancellationToken ct)
    {
        var rows = await _db.InventoryItems.Include(i => i.Warehouse)
            .Where(i => i.Variant.Uuid == variantUuid && i.Warehouse.IsActive)
            .ToListAsync(ct);

        return rows
            .Where(i => warehouseUuid == null || i.Warehouse.Uuid == warehouseUuid)
            .GroupBy(i => i.Warehouse.Uuid)
            .ToDictionary(g => g.Key, g => g.Sum(i => Math.Max(0m, i.QtyOnHand - i.QtyReserved)));
    }

    private static decimal Outstanding(AllocationDemand demand) =>
        Math.Max(0m, demand.RequiredQty - demand.ConsumedQty
                     - demand.Allocations.Where(a => a.Status == AllocationStatus.Active).Sum(a => a.Remaining));

    private static AllocationRecord NewRecord(
        AllocationDemand demand, Guid variantUuid, Guid warehouseUuid, decimal quantity,
        string supplyType, AllocationSupply? supply, string allocationType, int position, int userId, DateTime now) =>
        new()
        {
            VariantUuid    = variantUuid,
            WarehouseUuid  = warehouseUuid,
            AllocatedQty   = quantity,
            SupplyType     = supplyType,
            Supply         = supply,
            SupplyId       = supply?.Id,
            AllocationType = allocationType,
            PriorityScore  = position,
            RequiredDate   = demand.RequiredDate,
            AllocatedBy    = userId,
            AllocatedAt    = now
        };

    /// <summary>Gives back part of one on-hand allocation's hold. Planned allocations hold nothing.</summary>
    private async Task ReleaseHoldAsync(AllocationRecord record, decimal quantity, string reason, int userId, CancellationToken ct)
    {
        if (record.SupplyType != AllocationSupplyType.OnHand || quantity <= 0) return;

        var holds = (await _reservations.GetBySourceAsync(ReservationSourceType.Allocation, record.Demand.Uuid, ct))
            .Where(h => h.SourceLineUuid == record.Uuid && h.Status == StockReservation.StatusActive)
            .ToList();

        var remaining = quantity;
        foreach (var hold in holds)
        {
            if (remaining <= 0) break;
            remaining -= await _reservations.ReleaseAllocationAsync(hold.Uuid, Math.Min(remaining, hold.ReservedQty), reason, userId, ct);
        }
    }

    private static DemandAllocationSummary Summary(AllocationDemand d)
    {
        var active   = d.Allocations.Where(a => a.Status == AllocationStatus.Active).ToList();
        var reserved = active.Where(a => a.SupplyType == AllocationSupplyType.OnHand).Sum(a => a.Remaining);
        var planned  = active.Where(a => a.SupplyType != AllocationSupplyType.OnHand).Sum(a => a.Remaining);

        return new DemandAllocationSummary(
            d.Uuid, d.DemandType, d.DemandUuid, d.DemandLineUuid, d.Reference, d.VariantUuid, d.WarehouseUuid,
            d.RequiredQty, reserved, planned, d.ConsumedQty,
            Math.Max(0m, d.RequiredQty - d.ConsumedQty - reserved - planned),
            d.RequiredDate, d.Priority, d.Status);
    }

    private static AllocationSummary Summary(AllocationRecord a, AllocationDemand d) =>
        new(a.Uuid, d.Uuid, d.DemandType, d.DemandUuid, d.DemandLineUuid, d.Reference,
            a.VariantUuid, a.WarehouseUuid, a.AllocatedQty, a.ConsumedQty,
            a.SupplyType, a.Supply?.SupplyUuid, a.Supply?.Reference,
            a.AllocationType, a.PriorityScore, a.RequiredDate, a.Status,
            a.AllocatedAt, a.ReleasedAt, a.ReleaseReason);

    // ── Transactions and the lost-update retry ────────────────────────────────

    /// <summary>Same arrangement as StockReservationService: joins a caller's transaction, otherwise owns one.</summary>
    private async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
            return await work();

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    private Task InTransactionAsync(Func<Task> work, CancellationToken ct) =>
        InTransactionAsync(async () => { await work(); return true; }, ct);

    private async Task<T> RetryOnConcurrencyAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        var ownsTransaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (DbUpdateConcurrencyException) when (ownsTransaction && attempt < MaxAttempts)
            {
                ForgetState();
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 60) * attempt), ct);
            }
        }
    }

    private Task RetryOnConcurrencyAsync(Func<Task> operation, CancellationToken ct) =>
        RetryOnConcurrencyAsync(async () => { await operation(); return true; }, ct);

    /// <summary>Everything a run reads, detached, so the retry queries fresh rows.</summary>
    private void ForgetState()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AllocationDemand or AllocationRecord or AllocationSupply or AllocationRule
                             or InventoryItem or StockReservation)
                entry.State = EntityState.Detached;
        }
    }
}
