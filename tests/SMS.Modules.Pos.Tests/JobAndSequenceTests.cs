using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Common;
using Xunit;
using static SMS.Modules.Pos.Tests.PosHarness;

namespace SMS.Modules.Pos.Tests;

/// <summary>A38 E4 stale-shift job, impact provider (A37 D-18) and D-7 number sequences.</summary>
public class JobAndSequenceTests
{
    [Fact]
    public async Task Stale_shift_alerts_the_store_supervisors_once()
    {
        var h = new PosHarness();
        var (store, terminal, shift) = await h.OpenShiftAsync();
        await h.StaffAsync(store.Uuid, Cashier2, PosRole.Supervisor);
        await h.Stores.RemoveStaffAsync(store.Uuid, Cashier2); // inactive supervisor: not told

        // Another organization's supervisor in the same database must never be told.
        var other = new PosHarness(dbName: h.DbName);
        var (otherStore, _, _) = await other.OpenShiftAsync();

        var job = new StaleShiftAlertJob(h.NewDb(new StaticTenantContext { IsSuperAdmin = true }), h.Notifications, h.Hub, clock: h.Clock);
        (await job.AlertAsync()).Should().Be(0);

        h.Clock.Advance(TimeSpan.FromHours(12));
        (await job.AlertAsync()).Should().Be(2); // both organizations' shifts are stale…
        // …and each is reported to its own store's supervisor only (same user id in both orgs = two notifications, one per org).
        h.Notifications.Sent.Should().HaveCount(2).And.OnlyContain(n => n.UserId == Supervisor && n.Type == StaleShiftAlertJob.NotificationType);
        h.Notifications.Sent.Select(n => n.EntityUuid).Should().Contain(shift.Uuid.ToString());
        h.Hub.Sent.Should().Contain(e => e.Event == PosHubEvents.ShiftAlert && e.Target == terminal.Uuid);

        (await job.AlertAsync()).Should().Be(0); // once
        _ = otherStore;
    }

    [Fact]
    public async Task Stale_shift_job_runs_per_enabled_organization_when_gated()
    {
        var h = new PosHarness();
        var (_, _, shift) = await h.OpenShiftAsync();
        var gate = new FakeGate();
        gate.Off.Add(ModuleCodes.Pos);
        h.Clock.Advance(TimeSpan.FromHours(13));

        // Bare job tenant = HangfireTenantScope; with the module off for the only org nothing runs.
        var directory = new Directory([h.Org]);
        var db = h.NewDb(new HangfireLikeTenant());
        (await new StaleShiftAlertJob(db, h.Notifications, h.Hub, gate, directory, h.Clock).RunAsync()).Should().Be(0);
        h.Notifications.Sent.Should().BeEmpty();

        // A second, POS-less org makes the run go per enabled org: ours is enabled again → alerted.
        gate.Off.Clear();
        var gate2 = new OrgGate(h.Org);
        (await new StaleShiftAlertJob(db, h.Notifications, h.Hub, gate2, new Directory([h.Org, Guid.NewGuid()]), h.Clock).RunAsync()).Should().Be(1);
        h.Notifications.Sent.Should().ContainSingle(n => n.EntityUuid == shift.Uuid.ToString());
    }

    private sealed class Directory(IReadOnlyList<Guid> ids) : IOrganizationDirectory
    {
        public Task<IReadOnlyList<Guid>> GetOrganizationIdsAsync(CancellationToken ct = default) => Task.FromResult(ids);
    }

    private sealed class OrgGate(Guid enabled) : IModuleGate
    {
        public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) => Task.FromResult(organizationId == enabled);
    }

    /// <summary>The real TenantContext's job behaviour: HangfireTenantScope set ⇒ that org; unset ⇒ bypass.</summary>
    private sealed class HangfireLikeTenant : ITenantContext
    {
        public Guid OrganizationId => HangfireTenantScope.OrganizationId ?? Guid.Empty;
        public bool IsSuperAdmin => HangfireTenantScope.OrganizationId is null;
    }

    [Fact]
    public async Task Impact_provider_counts_open_and_closing_shifts_of_the_organization()
    {
        var h = new PosHarness();
        var (store, _, shift) = await h.OpenShiftAsync();
        var t2 = await h.TerminalAsync(store.Uuid, "T2");
        var s2 = await h.Shifts.OpenAsync(t2.Uuid, new PosOpenShiftRequest(), AdminCaller);
        await h.Shifts.StartCloseAsync(s2.Uuid, AdminCaller);
        var provider = new PosShiftImpactProvider(h.Db);

        provider.ModuleCode.Should().Be(ModuleCodes.Pos);
        (await provider.GetInProgressAsync(h.Org)).Should().ContainSingle().Which.Count.Should().Be(2);
        (await provider.GetInProgressAsync(Guid.NewGuid())).Should().BeEmpty();
        _ = shift;
    }

    [Fact]
    public async Task Sequences_are_per_store_prefix_and_day()
    {
        var h = new PosHarness();
        await h.StoreAsync("S1");
        await h.StoreAsync("S2");
        var stores = await h.Db.Stores.OrderBy(s => s.Code).ToListAsync();
        var day = new DateTime(2026, 10, 10);

        (await h.Numbers.NextValueAsync(stores[0], PosNumberPrefix.Order, day)).Should().Be(1);
        (await h.Numbers.NextValueAsync(stores[0], PosNumberPrefix.Order, day)).Should().Be(2);
        (await h.Numbers.NextValueAsync(stores[0], PosNumberPrefix.Return, day)).Should().Be(1);
        (await h.Numbers.NextValueAsync(stores[1], PosNumberPrefix.Order, day)).Should().Be(1);
        (await h.Numbers.NextValueAsync(stores[0], PosNumberPrefix.Order, day.AddDays(1))).Should().Be(1);
        (await h.Numbers.NextNumberAsync(stores[0], PosNumberPrefix.Order, new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc)))
            .Should().Be("POS-S1-20261010-0003");
        PosNumberSequenceService.Format(PosNumberPrefix.Shift, "S1", day, 7).Should().Be("SH-S1-20261010-07");
        PosNumberSequenceService.Format(PosNumberPrefix.Return, "S1", day, 12).Should().Be("RET-S1-20261010-0012");
    }

    /// <summary>
    /// SQL Server (LocalDB): the migration applies, the MERGE sequence hands out unique values under concurrency, and the
    /// filtered unique indexes hold (one active shift per terminal, one active store per warehouse). Skipped quietly when
    /// LocalDB is not installed.
    /// </summary>
    [Fact]
    public async Task Sql_server_migration_sequence_and_unique_indexes()
    {
        var name = $"PosTests_{Guid.NewGuid():N}";
        var conn = $@"Server=(localdb)\mssqllocaldb;Database={name};Trusted_Connection=True;TrustServerCertificate=True";
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        PosDbContext Db() => new(new DbContextOptionsBuilder<PosDbContext>().UseSqlServer(conn).Options, tenant);

        await using var setup = Db();
        try { await setup.Database.MigrateAsync(); }
        catch (SqlException) { return; } // no LocalDB here
        try
        {
            var warehouse = Guid.NewGuid();
            var store = new PosStore { Code = "S1", Name = "S1", WarehouseUuid = warehouse, WarehouseName = "W" };
            setup.Stores.Add(store);
            await setup.SaveChangesAsync();

            var day = new DateTime(2026, 10, 10);
            var values = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
            {
                await using var db = Db();
                return await new PosNumberSequenceService(db).NextValueAsync(store, PosNumberPrefix.Order, day);
            }));
            values.Should().BeEquivalentTo(Enumerable.Range(1, 20));

            // The store-side reads translate on SQL Server (Contains over the status list, counts).
            var terminal = new PosTerminal { Code = "T1", Name = "T1", StoreId = store.Id };
            setup.Terminals.Add(terminal);
            await setup.SaveChangesAsync();
            setup.Shifts.Add(new PosShift { StoreId = store.Id, TerminalId = terminal.Id, ShiftNumber = "SH-1", OpenedAt = DateTime.UtcNow, BusinessDate = day });
            await setup.SaveChangesAsync();
            (await setup.Shifts.CountAsync(s => PosShiftStatus.Active.Contains(s.Status))).Should().Be(1);

            await using (var db = Db())
            {
                db.Shifts.Add(new PosShift { StoreId = store.Id, TerminalId = terminal.Id, ShiftNumber = "SH-2", OpenedAt = DateTime.UtcNow, BusinessDate = day });
                await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
            }
            await using (var db = Db())
            {
                db.Stores.Add(new PosStore { Code = "S2", Name = "S2", WarehouseUuid = warehouse, WarehouseName = "W" });
                await FluentActions.Awaiting(() => db.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
            }

            // Shift totals recalculation on SQL Server.
            var shiftId = (await setup.Shifts.SingleAsync()).Id;
            await using (var db = Db()) await new PosShiftTotals(db).RecalculateAsync(shiftId);
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }
}
