using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>
/// A33 PA-09 — what only a real SQL Server can show (LocalDB, migrated through the real migrations, A33_FulfillmentRoutes
/// included): the per-class default index refuses a second default, racing set-default calls leave exactly one default
/// (sp_getapplock + the index), replacing a route's steps does not trip the (route, order) / (route, code) unique indexes,
/// and seeding is idempotent.
/// </summary>
public sealed class FulfillmentRouteSqlServerTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A33_Routes_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();

    private LogisticsDbContext NewContext() => new(
        new DbContextOptionsBuilder<LogisticsDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = _org });

    private FulfillmentRouteService Service(LogisticsDbContext db) =>
        new(db, new StaticTenantContext { OrganizationId = _org }, []);

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private static CreateFulfillmentRouteRequest Route(string code, params string[] steps) => new()
    {
        Code = code, Name = code,
        Steps = steps.Select((s, i) => new FulfillmentRouteStepRequest { StepCode = s, StepOrder = i + 1 }).ToList()
    };

    [Fact]
    public async Task The_database_refuses_a_second_default_of_the_same_class_but_allows_one_per_class()
    {
        await using (var db = NewContext())
            (await new FulfillmentRouteSeeder(db).EnsureSeededAsync(_org)).Should().Be(3);

        await using var raw = NewContext();
        var packShip = await raw.FulfillmentRoutes.SingleAsync(r => r.OrganizationId == _org && r.Code == "PICK_PACK_SHIP");
        packShip.IsDefault = true;   // PICK_AND_SHIP already is the SHIP default

        var act = () => raw.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Racing_set_default_calls_leave_exactly_one_default_in_the_class()
    {
        Guid a, b;
        await using (var db = NewContext())
        {
            a = (await Service(db).CreateAsync(Route("SHIP_A", "PICK", "GOODS_ISSUE", "SHIP"), 1)).Uuid;
            b = (await Service(db).CreateAsync(Route("SHIP_B", "PICK", "PACK", "GOODS_ISSUE", "SHIP"), 1)).Uuid;
        }

        var calls = Enumerable.Range(0, 12).Select(async i =>
        {
            await using var db = NewContext();
            await Service(db).SetDefaultAsync(i % 2 == 0 ? a : b, 1);
        });
        await Task.WhenAll(calls);

        await using var check = NewContext();
        (await check.FulfillmentRoutes.CountAsync(r => r.OrganizationId == _org && r.IsDefault && r.RequiresShipping))
            .Should().Be(1);
    }

    [Fact]
    public async Task Replacing_a_routes_steps_back_and_forth_does_not_trip_the_unique_step_indexes()
    {
        Guid uuid;
        await using (var db = NewContext())
            uuid = (await Service(db).CreateAsync(Route("GROW", "PICK", "GOODS_ISSUE"), 1)).Uuid;

        foreach (var steps in new[]
                 {
                     new[] { "PICK", "PACK", "GOODS_ISSUE", "SHIP" },
                     new[] { "PICK", "GOODS_ISSUE", "SHIP" },
                     new[] { "PICK", "STAGE", "APPROVAL", "GOODS_ISSUE" }
                 })
        {
            await using var db = NewContext();
            var updated = await Service(db).UpdateAsync(uuid, new UpdateFulfillmentRouteRequest
            {
                Name = "Grow", DisplayOrder = 1,
                Steps = steps.Select((s, i) => new FulfillmentRouteStepRequest { StepCode = s, StepOrder = i + 1 }).ToList()
            }, 1);
            updated!.Steps.Select(s => s.StepCode).Should().Equal(steps);
        }

        await using var check = NewContext();
        (await check.FulfillmentRouteSteps.CountAsync(s => s.FulfillmentRoute.UUID == uuid)).Should().Be(4);
    }

    [Fact]
    public async Task Seeding_on_sql_server_is_idempotent_and_the_lookup_reads_the_defaults()
    {
        await using (var db = NewContext())
        {
            var seeder = new FulfillmentRouteSeeder(db);
            (await seeder.EnsureSeededAsync(_org)).Should().Be(3);
            (await seeder.EnsureSeededAsync(_org)).Should().Be(0);
        }

        await using var read = NewContext();
        var defaults = await new FulfillmentRouteLookup(read).GetOrgDefaultsAsync(_org);
        defaults.Shipping!.Code.Should().Be("PICK_AND_SHIP");
        defaults.NonShipping!.Code.Should().Be("PICK_ONLY");
    }
}
