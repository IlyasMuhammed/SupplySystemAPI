using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;

namespace SMS.Modules.Material.Services;

public sealed record ReservationMigrationResult(int Read, int Migrated, int AlreadyPresent);

internal interface IMirReservationMigrationService
{
    Task<ReservationMigrationResult> MigrateAsync(CancellationToken ct = default);
}

/// <summary>
/// Moves the MIR reservation rows from <c>material.stock_reservations</c> into the shared
/// <c>inventory.StockReservations</c> ledger.
/// <para>
/// <b>It copies holds, it does not create them.</b> <c>InventoryItem.QtyReserved</c> already
/// counts every one of these rows, so the migration must not touch it — doing so would double
/// every existing hold and make that much stock permanently unavailable.
/// </para>
/// <para>
/// Idempotent: each row keeps its original UUID, so a row already carried across is skipped. Runs
/// at startup after both schemas have migrated, and is a no-op on every start after the first.
/// </para>
/// </summary>
internal sealed class MirReservationMigrationService : IMirReservationMigrationService
{
    private readonly MaterialDbContext  _material;
    private readonly InventoryDbContext _inventory;
    private readonly ILogger<MirReservationMigrationService> _logger;

    public MirReservationMigrationService(
        MaterialDbContext material,
        InventoryDbContext inventory,
        ILogger<MirReservationMigrationService> logger)
    {
        _material  = material;
        _inventory = inventory;
        _logger    = logger;
    }

    public async Task<ReservationMigrationResult> MigrateAsync(CancellationToken ct = default)
    {
        // Query filters off: this is a system migration covering every organization, and at
        // startup the ambient tenant bypasses the filter anyway — relying on that would make the
        // behaviour depend on who happened to trigger it.
        var legacy = await _material.StockReservations
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        if (legacy.Count == 0) return new ReservationMigrationResult(0, 0, 0);

        var alreadyThere = (await _inventory.StockReservations
            .IgnoreQueryFilters()
            .Select(r => r.UUID)
            .ToListAsync(ct))
            .ToHashSet();

        var pending = legacy.Where(r => !alreadyThere.Contains(r.UUID)).ToList();

        if (pending.Count == 0)
            return new ReservationMigrationResult(legacy.Count, 0, legacy.Count);

        // The new ledger identifies a source by UUID; the old rows carry integer MIR ids.
        var mirIds  = pending.Select(r => r.MirId).Distinct().ToList();
        var lineIds = pending.Select(r => r.MirLineId).Distinct().ToList();

        var mirUuids = await _material.MaterialIssueRequests
            .IgnoreQueryFilters()
            .Where(m => mirIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.UUID, ct);

        var lineUuids = await _material.MaterialIssueRequestDetails
            .IgnoreQueryFilters()
            .Where(l => lineIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => l.UUID, ct);

        var migrated = 0;

        foreach (var row in pending)
        {
            // A reservation whose request no longer exists cannot be addressed by source, so it
            // could never be released or consumed through the new ledger. Skipped and reported
            // rather than carried across as an orphan.
            if (!mirUuids.TryGetValue(row.MirId, out var mirUuid))
            {
                _logger.LogWarning(
                    "Stock reservation {Uuid} references material issue request {MirId}, which no longer exists. Not migrated.",
                    row.UUID, row.MirId);
                continue;
            }

            _inventory.StockReservations.Add(new Inventory.Domain.StockReservation
            {
                UUID            = row.UUID,
                OrganizationId  = row.OrganizationId,
                InventoryItemId = row.InventoryItemId,
                VariantUuid     = row.VariantUuid,
                WarehouseId     = row.WarehouseId,
                ReservedQty     = row.ReservedQty,
                SourceType      = Shared.Common.ReservationSourceType.Mir,
                SourceUuid      = mirUuid,
                SourceLineUuid  = lineUuids.TryGetValue(row.MirLineId, out var lineUuid) ? lineUuid : null,
                Status          = row.Status,
                IsFlagged       = row.IsFlagged,
                FlaggedAt       = row.FlaggedAt,
                ReservedAt      = row.ReservedAt,
                // The old table never recorded who reserved or released; leaving it zero says
                // "unknown" rather than inventing an actor.
                ReservedBy      = 0,
                ReleasedAt      = row.ReleasedAt,
                ReleaseReason   = row.ReleaseReason
            });

            migrated++;
        }

        if (migrated > 0)
        {
            await _inventory.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Migrated {Migrated} MIR stock reservation(s) into the shared ledger; {Existing} were already there.",
                migrated, legacy.Count - pending.Count);
        }

        return new ReservationMigrationResult(legacy.Count, migrated, legacy.Count - pending.Count);
    }
}
