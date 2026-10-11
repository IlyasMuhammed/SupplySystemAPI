using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Inventory.Services;

/// <summary>
/// A38 C9 / D-9 (INV, D-24) — POS stock postings against the store warehouse.
/// <list type="bullet">
/// <item><b>Sale (OUT)</b> needs no reservation and never refuses: free stock (on hand − reserved, per row) is taken FEFO
/// (expiry, then oldest row), batch rows included; whatever is still owed goes onto the variant's default row (no batch,
/// no serial — created when missing), which may go below zero. Other documents' reservations are never consumed and their
/// QtyReserved is never touched.</item>
/// <item><b>Return / void (IN)</b> puts the units back on the default row (not the batches the sale took — the ledger does
/// not record which inventory row a movement hit, and a refunded unit is re-shelved anyway).</item>
/// <item>One ledger row per inventory row touched, CUSTOMER ⇄ WAREHOUSE so the master product ledger mirrors it,
/// reference = the POS document. Idempotent per (SourceUuid, TransactionType): a repeat posts nothing.</item>
/// <item>Non-stockable products (services) move no stock.</item>
/// </list>
/// </summary>
internal sealed class PosStockPoster : IPosStockPoster
{
    private const int MaxAttempts = 3;

    private readonly InventoryDbContext      _db;
    private readonly IInventoryLedgerService _ledger;

    public PosStockPoster(InventoryDbContext db, IInventoryLedgerService ledger)
    {
        _db     = db;
        _ledger = ledger;
    }

    public async Task<PosStockResult> PostAsync(PosStockPosting posting, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(posting);
        Validate(posting);

        var ownsTransaction = _db.Database.IsRelational() && _db.Database.CurrentTransaction is null;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PostOnceAsync(posting, ct);
            }
            // Lost a race on InventoryItem.RowVersion, or two first sales created the same default row: forget what was
            // loaded and re-plan from fresh rows. Inside a caller's transaction the caller retries the whole unit.
            catch (DbUpdateException) when (ownsTransaction && attempt < MaxAttempts)
            {
                Forget();
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 60) * attempt), ct);
            }
        }
    }

    public async Task<IReadOnlyList<PosStockLevel>> GetLevelsAsync(
        Guid organizationId, Guid warehouseUuid, IReadOnlyCollection<Guid>? variantUuids = null, CancellationToken ct = default)
    {
        var warehouseId = await _db.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.OrganizationId == organizationId && w.Uuid == warehouseUuid)
            .Select(w => (int?)w.Id).FirstOrDefaultAsync(ct);
        if (warehouseId is null) return [];

        var query = _db.InventoryItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && i.WarehouseId == warehouseId.Value);

        if (variantUuids is not null)
        {
            if (variantUuids.Count == 0) return [];
            var wanted = variantUuids.Distinct().ToList();
            query = query.Where(i => wanted.Contains(i.Variant.Uuid));
        }

        return await query
            .GroupBy(i => i.Variant.Uuid)
            .Select(g => new PosStockLevel(g.Key, g.Sum(i => i.QtyOnHand - i.QtyReserved)))
            .ToListAsync(ct);
    }

    // ── Posting ───────────────────────────────────────────────────────────────

    private static void Validate(PosStockPosting posting)
    {
        var expectedOutbound = posting.TransactionType switch
        {
            PosStockTransactionType.Sale   => true,
            PosStockTransactionType.Return => false,
            PosStockTransactionType.Void   => false,
            _ => throw new BadRequestException($"'{posting.TransactionType}' is not a POS stock transaction type.")
        };
        if (posting.IsOutbound != expectedOutbound)
            throw new BadRequestException(
                $"{posting.TransactionType} moves stock {(expectedOutbound ? "out of" : "into")} the store warehouse.");
        if (posting.OrganizationId == Guid.Empty || posting.WarehouseUuid == Guid.Empty || posting.SourceUuid == Guid.Empty)
            throw new BadRequestException("A POS stock posting needs its organization, warehouse and source document.");
        if (posting.Lines.Any(l => l.Quantity <= 0))
            throw new BadRequestException("Every POS stock line needs a quantity above zero.");
    }

    private async Task<PosStockResult> PostOnceAsync(PosStockPosting posting, CancellationToken ct)
    {
        var org = posting.OrganizationId;

        var warehouse = await _db.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.OrganizationId == org && w.Uuid == posting.WarehouseUuid, ct)
            ?? throw new BadRequestException($"Warehouse {posting.WarehouseUuid} does not exist.");

        var lines = posting.Lines
            .GroupBy(l => l.VariantUuid)
            .Select(g => (VariantUuid: g.Key, Quantity: g.Sum(l => l.Quantity)))
            .ToList();
        var uuids = lines.Select(l => l.VariantUuid).ToList();

        var variants = await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Include(v => v.Product)
            .Where(v => v.OrganizationId == org && uuids.Contains(v.Uuid))
            .ToDictionaryAsync(v => v.Uuid, ct);

        var unknown = uuids.Where(u => !variants.ContainsKey(u)).ToList();
        if (unknown.Count > 0)
            throw new BadRequestException($"Unknown product variant(s): {string.Join(", ", unknown)}.");

        // Idempotence: a document that already moved stock under this type moves none again.
        var alreadyPosted = await _db.InventoryLedgerEntries.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(e => e.OrganizationId == org
                        && e.ReferenceId == posting.SourceUuid
                        && e.TransactionType == posting.TransactionType, ct);
        if (alreadyPosted)
            return new PosStockResult(await NegativeAsync(org, warehouse.Id, variants.Values.Select(v => v.Id).ToList(), ct));

        var negative = new List<Guid>();

        await InTransactionAsync(async () =>
        {
            negative.Clear(); // the execution strategy may run this again
            foreach (var (variantUuid, quantity) in lines)
            {
                var variant = variants[variantUuid];
                if (!variant.Product.IsStockable) continue;

                var rows = await _db.InventoryItems.IgnoreQueryFilters()
                    .Where(i => i.OrganizationId == org && i.VariantId == variant.Id && i.WarehouseId == warehouse.Id)
                    .ToListAsync(ct);

                var moves = posting.IsOutbound
                    ? Deduct(rows, variant, warehouse.Id, org, quantity)
                    : AddBack(rows, variant, warehouse.Id, org, quantity);

                foreach (var (item, qty, unitCost) in moves)
                {
                    await _ledger.CreateEntryAsync(new LedgerEntryCommand
                    {
                        VariantId       = variant.Id,
                        WarehouseId     = warehouse.Id,
                        TransactionType = posting.TransactionType,
                        ReferenceType   = posting.TransactionType == PosStockTransactionType.Return ? "POS_RETURN" : "POS_ORDER",
                        ReferenceId     = posting.SourceUuid,
                        ReferenceNumber = Truncate(posting.SourceNumber, 50),
                        QuantityOut     = posting.IsOutbound ? qty : null,
                        QuantityIn      = posting.IsOutbound ? null : qty,
                        UnitCost        = unitCost,
                        Notes           = item.BatchNumber is { } batch ? $"POS {posting.SourceNumber}, batch {batch}" : $"POS {posting.SourceNumber}",
                        CreatedBy       = posting.UserId,
                        SourceType      = posting.IsOutbound ? "WAREHOUSE" : "CUSTOMER",
                        SourceName      = posting.IsOutbound ? warehouse.Name : "POS customer",
                        DestinationType = posting.IsOutbound ? "CUSTOMER" : "WAREHOUSE",
                        DestinationName = posting.IsOutbound ? "POS customer" : warehouse.Name
                    });
                    StampOrganization(org);

                    // Saved per entry: the ledger reads the running balance from the database, so a second row for the
                    // same variant and warehouse in one posting would otherwise start from the same balance.
                    await _db.SaveChangesAsync(ct);
                }

                if (rows.Sum(r => r.QtyOnHand - r.QtyReserved) < 0m) negative.Add(variantUuid);
            }

            await _db.SaveChangesAsync(ct);
        }, ct);

        return new PosStockResult(negative);
    }

    /// <summary>Free stock first (FEFO, then oldest row); the rest goes negative on the default row.</summary>
    private List<(InventoryItem Item, decimal Qty, decimal UnitCost)> Deduct(
        List<InventoryItem> rows, ProductVariant variant, int warehouseId, Guid org, decimal quantity)
    {
        var taken     = new Dictionary<InventoryItem, decimal>();
        var remaining = quantity;
        var now       = DateTime.UtcNow;

        foreach (var row in rows.OrderBy(r => r.ExpiryDate ?? DateTime.MaxValue).ThenBy(r => r.Id))
        {
            if (remaining <= 0m) break;
            var free = row.QtyOnHand - row.QtyReserved;
            if (free <= 0m) continue;

            var take = Math.Min(free, remaining);
            row.QtyOnHand  -= take;
            row.LastUpdated = now;
            taken[row]      = take;
            remaining      -= take;
        }

        if (remaining > 0m)
        {
            var lastCost = taken.Keys.Select(r => r.UnitCost).LastOrDefault(c => c is not null);
            var fallback = AverageCost(rows) ?? lastCost ?? FallbackCost(variant);
            var row      = DefaultRow(rows, variant, warehouseId, org, fallback);

            row.QtyOnHand  -= remaining;
            row.LastUpdated = now;
            taken[row]      = taken.GetValueOrDefault(row) + remaining;
        }

        return taken.Select(t => (t.Key, t.Value, t.Key.UnitCost ?? FallbackCost(variant))).ToList();
    }

    private List<(InventoryItem Item, decimal Qty, decimal UnitCost)> AddBack(
        List<InventoryItem> rows, ProductVariant variant, int warehouseId, Guid org, decimal quantity)
    {
        var cost = AverageCost(rows) ?? FallbackCost(variant);
        var row  = DefaultRow(rows, variant, warehouseId, org, cost);

        row.QtyOnHand  += quantity;
        row.LastUpdated = DateTime.UtcNow;

        return [(row, quantity, row.UnitCost ?? cost)];
    }

    /// <summary>The variant's un-batched, un-serialled row in the warehouse (one per variant/warehouse by unique index).</summary>
    private InventoryItem DefaultRow(List<InventoryItem> rows, ProductVariant variant, int warehouseId, Guid org, decimal unitCost)
    {
        var row = rows.FirstOrDefault(r => r.BatchNumber == null && r.SerialNumber == null);
        if (row is not null) return row;

        row = new InventoryItem
        {
            Uuid           = Guid.NewGuid(),
            OrganizationId = org,
            VariantId      = variant.Id,
            WarehouseId    = warehouseId,
            QtyOnHand      = 0m,
            QtyReserved    = 0m,
            QtyOnOrder     = 0m,
            UnitCost       = unitCost,
            LastUpdated    = DateTime.UtcNow
        };
        _db.InventoryItems.Add(row);
        rows.Add(row);
        return row;
    }

    private static decimal? AverageCost(List<InventoryItem> rows)
    {
        var costed = rows.Where(r => r.UnitCost is not null && r.QtyOnHand > 0m).ToList();
        var qty    = costed.Sum(r => r.QtyOnHand);
        return qty > 0m ? Math.Round(costed.Sum(r => r.QtyOnHand * r.UnitCost!.Value) / qty, 4) : rows.FirstOrDefault(r => r.UnitCost is not null)?.UnitCost;
    }

    private static decimal FallbackCost(ProductVariant variant) =>
        variant.LastPurchasePrice ?? variant.PurchasePrice;

    private async Task<List<Guid>> NegativeAsync(Guid org, int warehouseId, List<int> variantIds, CancellationToken ct) =>
        await _db.InventoryItems.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.WarehouseId == warehouseId && variantIds.Contains(i.VariantId))
            .GroupBy(i => i.Variant.Uuid)
            .Where(g => g.Sum(i => i.QtyOnHand - i.QtyReserved) < 0m)
            .Select(g => g.Key)
            .ToListAsync(ct);

    /// <summary>The ledger service relies on the ambient tenant to stamp its rows; POS names the organization itself.</summary>
    private void StampOrganization(Guid org)
    {
        foreach (var entry in _db.ChangeTracker.Entries<InventoryLedgerEntry>())
            if (entry.State == EntityState.Added && entry.Entity.OrganizationId == Guid.Empty)
                entry.Entity.OrganizationId = org;
    }

    private void Forget()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
            if (entry.Entity is InventoryItem or InventoryLedgerEntry)
                entry.State = EntityState.Detached;
    }

    private static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    /// <summary>Same arrangement as GoodsIssuePoster: joins a caller's transaction, else its own under the execution strategy.</summary>
    private async Task InTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            await work();
            await transaction.CommitAsync(ct);
        });
    }
}
