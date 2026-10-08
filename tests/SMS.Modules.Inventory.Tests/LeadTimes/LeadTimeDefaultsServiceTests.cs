using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services.LeadTimes;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>A34-PB-03/04 (T-C3-06, D-10): the organization's lead-time defaults — read with defaults, upsert, validation, tenancy, the first-save race.</summary>
public class LeadTimeDefaultsServiceTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static InventoryDbContext Db(string name, Guid org, bool superAdmin = false) => new(
        new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options,
        new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin });

    private static UpdateLeadTimeDefaultsRequest Request(int pickPack = 2, int shipping = 5, int sales = 0, int mfgBuffer = 1, int qc = 2, int transfer = 3) => new()
    {
        PickPackDays = pickPack, ShippingLeadTimeDays = shipping, SalesBufferDays = sales,
        ManufacturingBufferDays = mfgBuffer, QualityInspectionDays = qc, InternalTransferDays = transfer
    };

    [Fact]
    public async Task Without_a_row_GET_returns_the_system_defaults_unsaved_and_writes_nothing()
    {
        var name = Guid.NewGuid().ToString();

        var model = await new LeadTimeDefaultsService(Db(name, OrgA)).GetAsync();

        model.Should().BeEquivalentTo(new LeadTimeDefaultsModel
        {
            PickPackDays = 1, ShippingLeadTimeDays = 3, SalesBufferDays = 1,
            ManufacturingBufferDays = 0, QualityInspectionDays = 0, InternalTransferDays = 0, IsSaved = false
        });
        (await Db(name, OrgA).LeadTimeDefaults.IgnoreQueryFilters().CountAsync()).Should().Be(0, "D-10: no row until the first PUT");
    }

    [Fact]
    public async Task T_C3_06_PUT_creates_the_row_then_updates_the_same_row()
    {
        var name = Guid.NewGuid().ToString();

        var created = await new LeadTimeDefaultsService(Db(name, OrgA)).UpdateAsync(Request(), userId: 7);
        created.Should().BeEquivalentTo(new { PickPackDays = 2, ShippingLeadTimeDays = 5, SalesBufferDays = 0,
            ManufacturingBufferDays = 1, QualityInspectionDays = 2, InternalTransferDays = 3, IsSaved = true, ModifiedBy = 7 });
        created.ModifiedDate.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));

        var updated = await new LeadTimeDefaultsService(Db(name, OrgA)).UpdateAsync(Request(pickPack: 0, shipping: 365), userId: 8);
        updated.PickPackDays.Should().Be(0);
        updated.ShippingLeadTimeDays.Should().Be(365);
        updated.ModifiedBy.Should().Be(8);

        var rows = await Db(name, OrgA).LeadTimeDefaults.IgnoreQueryFilters().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].OrganizationId.Should().Be(OrgA);
        rows[0].CreatedBy.Should().Be(7);
        (await new LeadTimeDefaultsService(Db(name, OrgA)).GetAsync()).Should().BeEquivalentTo(updated);
    }

    [Theory]
    [InlineData(-1, "Pick & pack days must be between 0 and 365.")]
    [InlineData(366, "Pick & pack days must be between 0 and 365.")]
    public async Task BR_C3_01_values_outside_0_to_365_are_refused_and_nothing_is_saved(int pickPack, string message)
    {
        var name = Guid.NewGuid().ToString();

        var act = () => new LeadTimeDefaultsService(Db(name, OrgA)).UpdateAsync(Request(pickPack: pickPack), 1);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Be(message);
        (await Db(name, OrgA).LeadTimeDefaults.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Every_field_is_checked_and_required()
    {
        var name = Guid.NewGuid().ToString();
        var service = new LeadTimeDefaultsService(Db(name, OrgA));

        (await FluentActions.Awaiting(() => service.UpdateAsync(Request(transfer: 400), 1)).Should().ThrowAsync<BadRequestException>())
            .Which.Message.Should().Be("Internal transfer days must be between 0 and 365.");
        (await FluentActions.Awaiting(() => service.UpdateAsync(Request(shipping: -3), 1)).Should().ThrowAsync<BadRequestException>())
            .Which.Message.Should().Be("Shipping days must be between 0 and 365.");
        var missing = Request();
        missing.SalesBufferDays = null;
        (await FluentActions.Awaiting(() => service.UpdateAsync(missing, 1)).Should().ThrowAsync<BadRequestException>())
            .Which.Message.Should().Contain("Sales safety buffer").And.Contain("required");
    }

    [Fact]
    public async Task Each_organization_reads_and_writes_only_its_own_row_super_admin_included()
    {
        var name = Guid.NewGuid().ToString();
        await new LeadTimeDefaultsService(Db(name, OrgA)).UpdateAsync(Request(pickPack: 9), 1);

        // A super admin of B: no tenant filter, so only the explicit own-organization filter keeps A's row out.
        (await new LeadTimeDefaultsService(Db(name, OrgB, superAdmin: true)).GetAsync())
            .Should().Match<LeadTimeDefaultsModel>(m => !m.IsSaved && m.PickPackDays == 1);
        await new LeadTimeDefaultsService(Db(name, OrgB, superAdmin: true)).UpdateAsync(Request(pickPack: 4), 2);

        var rows = await Db(name, OrgA).LeadTimeDefaults.IgnoreQueryFilters().OrderBy(r => r.PickPackDays).ToListAsync();
        rows.Select(r => (r.OrganizationId, r.PickPackDays)).Should().Equal((OrgB, 4), (OrgA, 9));
        (await new LeadTimeDefaultsService(Db(name, OrgA)).GetAsync()).PickPackDays.Should().Be(9);
    }

    // ── The first-save race, on SQL Server ──────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task A_concurrent_first_save_loses_the_insert_and_is_retried_as_an_update()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        // Just before this context's INSERT reaches the server, another request's first save commits a row for the
        // same organization: the unique index refuses ours, and the service must re-read and update instead.
        var competitor = new InsertCompetingRowFirst(org);

        await using (var db = harness.NewContext(org, competitor))
        {
            var result = await new LeadTimeDefaultsService(db).UpdateAsync(Request(pickPack: 6), userId: 5);
            result.PickPackDays.Should().Be(6);
            result.IsSaved.Should().BeTrue();
        }

        competitor.Fired.Should().BeTrue();
        await using var check = harness.NewContext(org);
        var rows = await check.LeadTimeDefaults.IgnoreQueryFilters().ToListAsync();
        rows.Should().ContainSingle();
        rows[0].PickPackDays.Should().Be(6, "the losing request's values win as an update");
        rows[0].ModifiedBy.Should().Be(5);
    }

    [SqlServerFact]
    public async Task Eight_concurrent_first_saves_all_succeed_and_leave_one_row()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();

        async Task<LeadTimeDefaultsModel> SaveAsync(int days)
        {
            await using var db = harness.NewContext(org);
            return await new LeadTimeDefaultsService(db).UpdateAsync(Request(pickPack: days), days);
        }

        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(d => Task.Run(() => SaveAsync(d))));

        results.Should().OnlyContain(r => r.IsSaved);
        await using var check = harness.NewContext(org);
        (await check.LeadTimeDefaults.IgnoreQueryFilters().CountAsync(r => r.OrganizationId == org)).Should().Be(1);
    }

    /// <summary>Inserts a competing row for the organization the first time this context inserts into LeadTimeDefaults.</summary>
    private sealed class InsertCompetingRowFirst : DbCommandInterceptor
    {
        private readonly Guid _org;
        public bool Fired { get; private set; }

        public InsertCompetingRowFirst(Guid org) => _org = org;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if (!Fired && command.CommandText.Contains("INSERT INTO [inventory].[LeadTimeDefaults]", StringComparison.Ordinal))
            {
                Fired = true;
                var cs = new SqlConnectionStringBuilder(command.Connection!.ConnectionString).ConnectionString;
                await using var other = new SqlConnection(cs);
                await other.OpenAsync(ct);
                await using var insert = other.CreateCommand();
                // Every column: the harness builds the table from the model, which has no store defaults.
                insert.CommandText =
                    "INSERT INTO [inventory].[LeadTimeDefaults] ([Uuid], [OrganizationId], [PickPackDays], [ShippingLeadTimeDays], "
                  + "[SalesBufferDays], [ManufacturingBufferDays], [QualityInspectionDays], [InternalTransferDays], [CreatedBy], [CreatedDate]) "
                  + "VALUES (NEWID(), @org, 2, 3, 1, 0, 0, 0, 99, SYSUTCDATETIME())";
                insert.Parameters.AddWithValue("@org", _org);
                await insert.ExecuteNonQueryAsync(ct);
            }
            return result;
        }
    }
}
