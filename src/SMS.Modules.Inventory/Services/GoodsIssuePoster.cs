using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

/// <summary>
/// Writes the movement that takes stock off the books, against the holds a document is carrying.
/// <para>
/// Every deduction here is matched to a reservation row, and every reservation row names the exact
/// inventory row it was taken from. That is what keeps the ledger honest for batch-tracked stock:
/// the batch that leaves the books is the batch the picker took off the shelf (T-24), not a batch
/// re-chosen at the last moment.
/// </para>
/// </summary>
internal sealed class GoodsIssuePoster : IGoodsIssuePoster
{
    private readonly InventoryDbContext      _db;
    private readonly IInventoryLedgerService _ledger;

    public GoodsIssuePoster(InventoryDbContext db, IInventoryLedgerService ledger)
    {
        _db     = db;
        _ledger = ledger;
    }

    public async Task<GoodsIssueResult> PostAsync(
        string sourceType,
        Guid sourceUuid,
        GoodsIssuePosting posting,
        int userId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceType);
        ArgumentNullException.ThrowIfNull(posting);

        var held = await _db.StockReservations
            .Where(r => r.SourceType == sourceType
                     && r.SourceUuid == sourceUuid
                     && r.Status == StockReservation.StatusActive
                     && r.ReservedQty > 0)
            .Include(r => r.InventoryItem!).ThenInclude(i => i.Warehouse)
            .Include(r => r.InventoryItem!).ThenInclude(i => i.Variant)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        // Nothing held. A document issued once has no active holds left, so a retry lands here and
        // deducts nothing — which is the only form of idempotence that matters for this operation.
        if (held.Count == 0) return new GoodsIssueResult(0, 0m, 0m);

        var isTransfer = posting.ToWarehouseUuid is not null;

        var destinationId = isTransfer
            ? await _db.Warehouses
                .Where(w => w.Uuid == posting.ToWarehouseUuid!.Value)
                .Select(w => (int?)w.Id)
                .FirstOrDefaultAsync(ct)
              ?? throw new InvalidOperationException(
                     $"The destination warehouse {posting.ToWarehouseUuid} does not exist, so the " +
                     "transfer has nowhere to land. Nothing was deducted.")
            : (int?)null;

        var now       = DateTime.UtcNow;
        var totalOut  = 0m;
        var totalIn   = 0m;
        var movements = 0;

        await InTransactionAsync(async () =>
        {
            foreach (var reservation in held)
            {
                var item = reservation.InventoryItem
                    ?? throw new InvalidOperationException(
                        $"Reservation {reservation.UUID} points at a stock row that no longer exists.");

                var qty      = reservation.ReservedQty;
                var unitCost = item.UnitCost ?? 0m;

                // Out of the source row. Clamped at zero: a negative on-hand is never a truthful
                // number, and if the counters have already drifted, driving them further negative
                // buries the evidence rather than surfacing it.
                item.QtyOnHand   = Math.Max(0m, item.QtyOnHand - qty);
                item.QtyReserved = Math.Max(0m, item.QtyReserved - qty);
                item.LastUpdated = now;

                reservation.ReservedQty   = 0m;
                reservation.Status        = StockReservation.StatusConsumed;
                reservation.ReleasedAt    = now;
                reservation.ReleasedBy    = userId;
                reservation.ReleaseReason = $"Goods issued on {posting.ReferenceNumber}.";

                await _ledger.CreateEntryAsync(new LedgerEntryCommand
                {
                    VariantId       = item.VariantId,
                    WarehouseId     = item.WarehouseId,
                    TransactionType = isTransfer
                        ? GoodsIssueTransactionType.TransferOut
                        : posting.TransactionType ?? GoodsIssueTransactionType.DeliveryIssue,
                    ReferenceType   = posting.ReferenceType,
                    ReferenceId     = posting.ReferenceUuid,
                    ReferenceNumber = posting.ReferenceNumber,
                    QuantityOut     = qty,
                    UnitCost        = unitCost,
                    Notes           = posting.Notes,
                    CreatedBy       = userId,
                    SourceType      = "WAREHOUSE",
                    SourceName      = item.Warehouse.Name,
                    DestinationType = isTransfer ? "WAREHOUSE" : "CUSTOMER",
                    DestinationName = posting.DestinationName
                });

                totalOut += qty;
                movements++;

                if (!isTransfer) continue;

                // The other half of a transfer: the same units, same batch, arriving somewhere
                // else. Written here rather than left to a later receipt because a transfer that
                // posts only the outbound leg makes stock vanish between two warehouses.
                var destination = await DestinationRowAsync(item, destinationId!.Value, now, ct);

                destination.QtyOnHand  += qty;
                destination.LastUpdated = now;

                await _ledger.CreateEntryAsync(new LedgerEntryCommand
                {
                    VariantId       = item.VariantId,
                    WarehouseId     = destinationId.Value,
                    TransactionType = GoodsIssueTransactionType.TransferIn,
                    ReferenceType   = posting.ReferenceType,
                    ReferenceId     = posting.ReferenceUuid,
                    ReferenceNumber = posting.ReferenceNumber,
                    QuantityIn      = qty,
                    UnitCost        = unitCost,
                    Notes           = posting.Notes,
                    CreatedBy       = userId,
                    SourceType      = "WAREHOUSE",
                    SourceName      = item.Warehouse.Name,
                    DestinationType = "WAREHOUSE",
                    DestinationName = posting.DestinationName
                });

                totalIn += qty;
                movements++;
            }

            await _db.SaveChangesAsync(ct);
        }, ct);

        return new GoodsIssueResult(movements, totalOut, totalIn);
    }

    /// <summary>
    /// The row the transferred units land on, created if the destination has never held this
    /// batch before. Matched on batch and serial so a batch-tracked item stays batch-tracked on
    /// arrival rather than collapsing into an untracked pile.
    /// </summary>
    private async Task<InventoryItem> DestinationRowAsync(
        InventoryItem source, int destinationWarehouseId, DateTime now, CancellationToken ct)
    {
        bool Matches(InventoryItem i) =>
            i.VariantId    == source.VariantId
         && i.WarehouseId  == destinationWarehouseId
         && i.BatchNumber  == source.BatchNumber
         && i.SerialNumber == source.SerialNumber;

        // Local first: several reservation rows can land on the same destination row within one
        // posting, and a second database lookup would not see the one just added.
        var existing = _db.InventoryItems.Local.FirstOrDefault(Matches)
            ?? await _db.InventoryItems.FirstOrDefaultAsync(
                   i => i.VariantId    == source.VariantId
                     && i.WarehouseId  == destinationWarehouseId
                     && i.BatchNumber  == source.BatchNumber
                     && i.SerialNumber == source.SerialNumber, ct);

        if (existing is not null) return existing;

        var created = new InventoryItem
        {
            Uuid            = Guid.NewGuid(),
            OrganizationId  = source.OrganizationId,
            VariantId       = source.VariantId,
            WarehouseId     = destinationWarehouseId,
            BatchNumber     = source.BatchNumber,
            SerialNumber    = source.SerialNumber,
            ExpiryDate      = source.ExpiryDate,
            QtyOnHand       = 0m,
            QtyReserved     = 0m,
            QtyOnOrder      = 0m,
            UnitCost        = source.UnitCost,
            ValuationMethod = source.ValuationMethod,
            LastUpdated     = now
        };

        _db.InventoryItems.Add(created);
        return created;
    }

    /// <summary>
    /// Mirrors <c>StockReservationService.InTransactionAsync</c>: the in-memory provider used by
    /// tests has no transactions, and wrapping there throws rather than protecting anything.
    /// </summary>
    private async Task InTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        await work();
        await transaction.CommitAsync(ct);
    }
}
