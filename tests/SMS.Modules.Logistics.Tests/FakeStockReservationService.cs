using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// Stands in for SMS.Modules.Inventory's reservation ledger. Logistics only knows the SMS.Shared
/// contract, so these tests need no inventory database.
/// <para>
/// It models the two behaviours Logistics depends on: an all-or-nothing hold, and a release that
/// is idempotent. Stock levels are whatever a test declares them to be.
/// </para>
/// </summary>
internal sealed class FakeStockReservationService : IStockReservationService
{
    private readonly Dictionary<Guid, decimal> _available = [];
    private readonly List<Held> _held = [];

    private sealed record Held(string SourceType, Guid SourceUuid, Guid VariantUuid,
                               Guid? SourceLineUuid, decimal InitialQty)
    {
        internal Guid Uuid { get; } = Guid.NewGuid();

        /// <summary>What is still held — a partial consume reduces it without closing the hold.</summary>
        internal decimal Qty { get; set; } = InitialQty;
        internal string Status { get; set; } = "ACTIVE";

        /// <summary>Where the real ledger would have taken these units from.</summary>
        internal StockLocation Location { get; set; } = StockLocation.Unplaced;
    }

    /// <summary>A place on a shelf, as the allocation would report it.</summary>
    internal sealed record StockLocation(
        Guid      WarehouseUuid,
        string    WarehouseName,
        string?   ZoneName     = null,
        string?   BinCode      = null,
        string?   BatchNumber  = null,
        string?   SerialNumber = null,
        DateTime? ExpiryDate   = null)
    {
        internal static readonly StockLocation Unplaced = new(Guid.NewGuid(), "Test Warehouse");
    }

    /// <summary>
    /// How a variant's stock is laid out, when a test cares. Several entries mean the hold is
    /// split across bins or batches, which is what makes a pick list more than one line.
    /// </summary>
    private readonly Dictionary<Guid, List<(StockLocation Where, decimal Qty)>> _layout = [];

    internal void SetLayout(Guid variantUuid, params (StockLocation Where, decimal Qty)[] rows)
    {
        _layout[variantUuid] = [.. rows];
        _available[variantUuid] = rows.Sum(r => r.Qty);
    }

    /// <summary>Declares how much of a variant is on hand and free.</summary>
    internal void SetAvailable(Guid variantUuid, decimal quantity) => _available[variantUuid] = quantity;

    private readonly List<string> _releaseReasons = [];
    /// <summary>The reason recorded each time a release actually freed something.</summary>
    internal IReadOnlyList<string> ReasonsGiven => _releaseReasons;

    internal decimal ActiveFor(Guid sourceUuid) =>
        _held.Where(h => h.SourceUuid == sourceUuid && h.Status == "ACTIVE").Sum(h => h.Qty);

    internal decimal RemainingAvailable(Guid variantUuid) =>
        _available.GetValueOrDefault(variantUuid)
        - _held.Where(h => h.VariantUuid == variantUuid && h.Status == "ACTIVE").Sum(h => h.Qty);

    internal int ReserveCallCount { get; private set; }

    public Task<ReservationResult> ReserveAsync(
        string sourceType, Guid sourceUuid, IReadOnlyList<ReservationRequest> requests,
        int userId, DateTime? expiresAt = null, CancellationToken ct = default)
    {
        ReserveCallCount++;

        var lines = requests.Select(r =>
        {
            var free = RemainingAvailable(r.VariantUuid);
            var short_ = r.Quantity > free ? r.Quantity - Math.Max(0, free) : 0;

            return new ReservationLineResult(
                r.VariantUuid, r.SourceLineUuid, r.Quantity,
                short_ > 0 ? 0 : r.Quantity, short_, Math.Max(0, free),
                short_ > 0 ? $"Only {Math.Max(0, free):0.###} available." : null);
        }).ToList();

        if (lines.Any(l => l.Shortfall > 0))
            return Task.FromResult(new ReservationResult(false, lines));

        foreach (var r in requests)
            foreach (var (where, qty) in Draw(r.VariantUuid, r.Quantity))
                _held.Add(new Held(sourceType, sourceUuid, r.VariantUuid, r.SourceLineUuid, qty)
                {
                    Location = where
                });

        return Task.FromResult(new ReservationResult(true, lines));
    }

    public Task<int> ReleaseBySourceAsync(
        string sourceType, Guid sourceUuid, string reason, int userId, CancellationToken ct = default)
    {
        var active = _held
            .Where(h => h.SourceType == sourceType && h.SourceUuid == sourceUuid && h.Status == "ACTIVE")
            .ToList();

        foreach (var h in active) h.Status = "RELEASED";
        if (active.Count > 0) _releaseReasons.Add(reason);

        return Task.FromResult(active.Count);
    }

    public Task<int> ConsumeBySourceAsync(
        string sourceType, Guid sourceUuid, int userId, CancellationToken ct = default)
    {
        var active = _held
            .Where(h => h.SourceType == sourceType && h.SourceUuid == sourceUuid && h.Status == "ACTIVE")
            .ToList();

        foreach (var h in active) h.Status = "CONSUMED";
        return Task.FromResult(active.Count);
    }

    public Task<decimal> ConsumeLineAsync(
        string sourceType, Guid sourceUuid, Guid sourceLineUuid, decimal quantity,
        int userId, CancellationToken ct = default)
    {
        var held = _held.FirstOrDefault(h => h.SourceType == sourceType
                                          && h.SourceUuid == sourceUuid
                                          && h.SourceLineUuid == sourceLineUuid
                                          && h.Status == "ACTIVE");

        if (held is null || quantity <= 0) return Task.FromResult(0m);

        var take = Math.Min(quantity, held.Qty);
        held.Qty -= take;
        if (held.Qty <= 0) held.Status = "CONSUMED";

        return Task.FromResult(take);
    }

    public Task<IReadOnlyList<VariantAvailability>> GetAvailableAsync(
        IReadOnlyList<Guid> variantUuids, Guid? warehouseUuid, CancellationToken ct = default)
    {
        IReadOnlyList<VariantAvailability> result = variantUuids
            .Distinct()
            .Where(_available.ContainsKey)
            .Select(id => new VariantAvailability(
                id, warehouseUuid, "Test Warehouse", Math.Max(0m, RemainingAvailable(id))))
            .ToList();

        return Task.FromResult(result);
    }

    /// <summary>
    /// Splits a quantity across the declared layout, the way the real ledger allocates across
    /// bins and batches. With no layout it is one hold in one unnamed place.
    /// </summary>
    private List<(StockLocation Where, decimal Qty)> Draw(Guid variantUuid, decimal quantity)
    {
        if (!_layout.TryGetValue(variantUuid, out var rows))
            return [(StockLocation.Unplaced, quantity)];

        var taken     = new List<(StockLocation, decimal)>();
        var remaining = quantity;

        foreach (var (where, available) in rows)
        {
            if (remaining <= 0) break;

            var amount = Math.Min(remaining, available);
            if (amount <= 0) continue;

            taken.Add((where, amount));
            remaining -= amount;
        }

        return taken;
    }

    public Task<decimal> ReleaseAllocationAsync(
        Guid reservationUuid, decimal quantity, string reason, int userId,
        CancellationToken ct = default)
    {
        var held = _held.FirstOrDefault(h => h.Uuid == reservationUuid && h.Status == "ACTIVE");

        if (held is null || quantity <= 0) return Task.FromResult(0m);

        var give = Math.Min(quantity, held.Qty);
        held.Qty -= give;

        if (held.Qty <= 0)
        {
            held.Qty    = 0;
            held.Status = "RELEASED";
        }

        if (give > 0) _releaseReasons.Add(reason);

        return Task.FromResult(give);
    }

    public Task<IReadOnlyList<StockAllocation>> GetAllocationsAsync(
        string sourceType, Guid sourceUuid, CancellationToken ct = default)
    {
        IReadOnlyList<StockAllocation> result = _held
            .Where(h => h.SourceType == sourceType && h.SourceUuid == sourceUuid && h.Status == "ACTIVE")
            .Select(h => new StockAllocation(
                h.Uuid, h.VariantUuid, h.SourceLineUuid, h.Qty,
                h.Location.WarehouseUuid, h.Location.WarehouseName,
                h.Location.ZoneName, h.Location.BinCode,
                h.Location.BatchNumber, h.Location.SerialNumber, h.Location.ExpiryDate))
            .ToList();

        return Task.FromResult(result);
    }

    /// <summary>
    /// Logistics never exercises SALES_ORDER expiry sweeping — MIR/Delivery holds carry no TTL —
    /// so these three exist only to satisfy the interface and always report nothing to do.
    /// </summary>
    public Task<IReadOnlyList<ExpiringReservation>> GetExpiredAsync(string sourceType, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExpiringReservation>>([]);

    public Task<IReadOnlyList<ExpiringReservation>> GetExpiringWithinAsync(
        string sourceType, TimeSpan within, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ExpiringReservation>>([]);

    public Task MarkExpiryWarningSentAsync(IReadOnlyList<Guid> reservationUuids, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<ExpiringReservation?> GetByUuidAsync(Guid reservationUuid, CancellationToken ct = default) =>
        Task.FromResult<ExpiringReservation?>(null);

    public Task<decimal> TransferLineAsync(
        string fromSourceType, Guid fromSourceUuid, Guid fromSourceLineUuid,
        string toSourceType, Guid toSourceUuid, Guid? toSourceLineUuid,
        decimal quantity, int userId, CancellationToken ct = default)
    {
        var remaining = quantity;
        var moved     = 0m;

        foreach (var held in _held.Where(h => h.SourceType == fromSourceType
                                           && h.SourceUuid == fromSourceUuid
                                           && h.SourceLineUuid == fromSourceLineUuid
                                           && h.Status == "ACTIVE").ToList())
        {
            if (remaining <= 0) break;

            var take = Math.Min(remaining, held.Qty);
            held.Qty -= take;
            if (held.Qty <= 0) held.Status = "TRANSFERRED";

            // The same place on the shelf, now held by the other document. The counter (what
            // RemainingAvailable reports) is unchanged: the units were reserved before and after.
            _held.Add(new Held(toSourceType, toSourceUuid, held.VariantUuid, toSourceLineUuid, take)
            {
                Location = held.Location
            });

            remaining -= take;
            moved     += take;
        }

        return Task.FromResult(moved);
    }

    public Task<IReadOnlyList<ReservationSummary>> GetBySourceAsync(
        string sourceType, Guid sourceUuid, CancellationToken ct = default)
    {
        IReadOnlyList<ReservationSummary> result = _held
            .Where(h => h.SourceType == sourceType && h.SourceUuid == sourceUuid)
            .Select(h => new ReservationSummary(
                h.Uuid, h.VariantUuid, h.Location.WarehouseUuid, h.Qty, h.Status, h.SourceLineUuid))
            .ToList();

        return Task.FromResult(result);
    }
}
