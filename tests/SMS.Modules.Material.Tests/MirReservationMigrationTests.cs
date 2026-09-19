using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// Carrying MIR reservations into the shared inventory ledger.
/// <para>
/// The assertion that matters most is the one about <c>QtyReserved</c>: the counter already
/// includes every row being copied, so a migration that also incremented it would double every
/// existing hold and make that much stock permanently unavailable.
/// </para>
/// </summary>
public class MirReservationMigrationTests
{
    private sealed record Harness(
        MaterialDbContext Material,
        InventoryDbContext Inventory,
        MirReservationMigrationService Service,
        Guid OrgId);

    private static Harness NewHarness()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        DbContextOptions<T> Options<T>() where T : DbContext =>
            new DbContextOptionsBuilder<T>()
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options;

        var material  = new MaterialDbContext(Options<MaterialDbContext>(), tenant);
        var inventory = new InventoryDbContext(Options<InventoryDbContext>(), tenant);

        return new Harness(material, inventory,
            new MirReservationMigrationService(
                material, inventory, NullLogger<MirReservationMigrationService>.Instance),
            tenant.OrganizationId);
    }

    /// <summary>Seeds a MIR, one line and one legacy reservation against it.</summary>
    private static async Task<(Guid MirUuid, Guid LineUuid, Guid ReservationUuid)> SeedLegacy(
        Harness h, decimal qty = 40m, string status = "ACTIVE", bool flagged = false)
    {
        var mir = new MaterialIssueRequest
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = h.OrgId,
            RequestNo = $"MIR-{Guid.NewGuid():N}"[..14], RequestType = "PROJECT",
            RequestedBy = 1, Status = "APPROVED", CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        h.Material.MaterialIssueRequests.Add(mir);
        await h.Material.SaveChangesAsync();

        var line = new MaterialIssueRequestDetail
        {
            UUID = Guid.NewGuid(), OrganizationId = h.OrgId,
            MaterialIssueRequestId = mir.Id, VariantUuid = Guid.NewGuid(),
            ItemDescription = "4mm cable", RequestedQty = qty
        };
        h.Material.MaterialIssueRequestDetails.Add(line);
        await h.Material.SaveChangesAsync();

        var reservation = new StockReservation
        {
            UUID = Guid.NewGuid(), OrganizationId = h.OrgId,
            MirId = mir.Id, MirLineId = line.Id,
            InventoryItemId = 501, VariantUuid = line.VariantUuid, WarehouseId = 9,
            ReservedQty = qty, Status = status, ReservedAt = new DateTime(2026, 5, 1),
            IsFlagged = flagged, FlaggedAt = flagged ? new DateTime(2026, 6, 1) : null
        };
        h.Material.StockReservations.Add(reservation);
        await h.Material.SaveChangesAsync();
        h.Material.ChangeTracker.Clear();

        return (mir.UUID, line.UUID, reservation.UUID);
    }

    // ── The copy ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_reservation_is_carried_across_addressed_by_its_source_document()
    {
        var h = NewHarness();
        var (mirUuid, lineUuid, reservationUuid) = await SeedLegacy(h, qty: 40m);

        var result = await h.Service.MigrateAsync();

        result.Read.Should().Be(1);
        result.Migrated.Should().Be(1);

        var moved = await h.Inventory.StockReservations.IgnoreQueryFilters().SingleAsync();
        moved.UUID.Should().Be(reservationUuid, "the original id is kept so the move is traceable");
        moved.SourceType.Should().Be(ReservationSourceType.Mir);
        moved.SourceUuid.Should().Be(mirUuid);
        moved.SourceLineUuid.Should().Be(lineUuid);
        moved.ReservedQty.Should().Be(40m);
        moved.InventoryItemId.Should().Be(501);
        moved.OrganizationId.Should().Be(h.OrgId);
    }

    [Fact]
    public async Task The_status_and_the_flag_survive_the_move()
    {
        var h = NewHarness();
        await SeedLegacy(h, status: "RELEASED", flagged: true);

        await h.Service.MigrateAsync();

        var moved = await h.Inventory.StockReservations.IgnoreQueryFilters().SingleAsync();
        moved.Status.Should().Be("RELEASED");
        moved.IsFlagged.Should().BeTrue("the Reserved Stock report surfaces this");
        moved.FlaggedAt.Should().Be(new DateTime(2026, 6, 1));
        moved.ReservedAt.Should().Be(new DateTime(2026, 5, 1), "the original age is preserved");
    }

    [Fact]
    public async Task Consumed_and_released_holds_come_across_too()
    {
        // History, not just live holds — the report filters by status and would otherwise show a
        // sudden gap in everything that had already completed.
        var h = NewHarness();
        await SeedLegacy(h, status: "ACTIVE");
        await SeedLegacy(h, status: "CONSUMED");
        await SeedLegacy(h, status: "RELEASED");

        (await h.Service.MigrateAsync()).Migrated.Should().Be(3);

        (await h.Inventory.StockReservations.IgnoreQueryFilters().CountAsync()).Should().Be(3);
    }

    // ── The counter must not move ─────────────────────────────────────────────

    [Fact]
    public async Task The_migration_does_not_touch_reserved_quantities()
    {
        // QtyReserved already counts these holds. Incrementing it again would double every
        // existing reservation and take that stock out of circulation with nothing to explain it.
        var h = NewHarness();

        var item = new Inventory.Domain.InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = 1, WarehouseId = 9,
            QtyOnHand = 500m, QtyReserved = 40m
        };
        h.Inventory.InventoryItems.Add(item);
        await h.Inventory.SaveChangesAsync();
        h.Inventory.ChangeTracker.Clear();

        await SeedLegacy(h, qty: 40m);
        await h.Service.MigrateAsync();

        var after = await h.Inventory.InventoryItems.AsNoTracking().SingleAsync();
        after.QtyReserved.Should().Be(40m, "the migration copies holds, it does not create them");
        after.QtyOnHand.Should().Be(500m);
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Running_it_again_carries_nothing_further()
    {
        // It runs on every application start.
        var h = NewHarness();
        await SeedLegacy(h);
        await SeedLegacy(h);

        (await h.Service.MigrateAsync()).Migrated.Should().Be(2);

        var second = await h.Service.MigrateAsync();
        second.Migrated.Should().Be(0);
        second.AlreadyPresent.Should().Be(2);

        (await h.Inventory.StockReservations.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_reservation_created_after_the_first_run_is_picked_up_next_time()
    {
        var h = NewHarness();
        await SeedLegacy(h);
        await h.Service.MigrateAsync();

        await SeedLegacy(h);

        (await h.Service.MigrateAsync()).Migrated.Should().Be(1);
        (await h.Inventory.StockReservations.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Nothing_to_migrate_is_not_an_error()
    {
        var h = NewHarness();

        var result = await h.Service.MigrateAsync();

        result.Should().Be(new ReservationMigrationResult(0, 0, 0));
    }

    // ── Orphans ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_reservation_whose_request_no_longer_exists_is_left_behind()
    {
        // The new ledger addresses a hold by source UUID, so one with no resolvable source could
        // never be released or consumed through it. Skipped and logged rather than carried
        // across as an unreachable row.
        var h = NewHarness();
        await SeedLegacy(h);

        h.Material.StockReservations.Add(new StockReservation
        {
            UUID = Guid.NewGuid(), OrganizationId = h.OrgId,
            MirId = 99999, MirLineId = 99999,
            InventoryItemId = 1, VariantUuid = Guid.NewGuid(), WarehouseId = 1,
            ReservedQty = 5m, Status = "ACTIVE", ReservedAt = DateTime.UtcNow
        });
        await h.Material.SaveChangesAsync();
        h.Material.ChangeTracker.Clear();

        var result = await h.Service.MigrateAsync();

        result.Read.Should().Be(2);
        result.Migrated.Should().Be(1, "the orphan is not carried across");
        (await h.Inventory.StockReservations.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_legacy_rows_are_left_exactly_where_they_are()
    {
        // The old table is kept, dormant, so the move can be verified — and reversed — before
        // anything is dropped.
        var h = NewHarness();
        var (_, _, reservationUuid) = await SeedLegacy(h, qty: 40m);

        await h.Service.MigrateAsync();

        var legacy = await h.Material.StockReservations.IgnoreQueryFilters().SingleAsync();
        legacy.UUID.Should().Be(reservationUuid);
        legacy.ReservedQty.Should().Be(40m);
        legacy.Status.Should().Be("ACTIVE");
    }
}
