using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>
/// A34 PA-01 — <c>A34_RouteCategoryAndProductionDeliveries</c> on a real SQL Server (LocalDB, throwaway database): up,
/// down, up again; a re-run against a database that already has the schema but no history row (the drifted shared
/// database); existing routes read STOCK; the CHECK refuses an unknown category; the seeder then adds the two MFG routes.
/// </summary>
public sealed class RouteCategoryMigrationSqlServerTests : IAsyncLifetime
{
    private const string Server    = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private const string A33       = "20261003081429_A33_FulfillmentRoutes";
    private const string A34Name   = "A34_RouteCategoryAndProductionDeliveries";
    private readonly string _connection = $"{Server}Database=A34_LogMig_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();

    private LogisticsDbContext NewContext() => new(
        new DbContextOptionsBuilder<LogisticsDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = _org });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<int> ColumnCountAsync() => ScalarAsync<int>(
        "SELECT (CASE WHEN COL_LENGTH('logistics.fulfillment_routes','RouteCategory') IS NULL THEN 0 ELSE 1 END) " +
        "+ (CASE WHEN COL_LENGTH('logistics.delivery_orders','ProductionOrderUuid') IS NULL THEN 0 ELSE 1 END) " +
        "+ (SELECT COUNT(*) FROM sys.check_constraints WHERE name = 'CK_fulfillment_routes_RouteCategory') " +
        "+ (SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_delivery_orders_OrganizationId_ProductionOrderUuid')");

    [Fact]
    public async Task Up_down_up_and_a_rerun_on_a_migrated_database_all_work_and_existing_routes_become_stock()
    {
        // Up to A33, with an A33-era route in place.
        await using (var db = NewContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(A33);
        }
        await ExecAsync(
            "INSERT INTO logistics.fulfillment_routes (UUID, OrganizationId, Code, Name, IsDefault, IsActive, IsSystem, " +
            "RequiresPacking, RequiresShipping, DisplayOrder, CreatedBy, CreatedDate) " +
            $"VALUES (NEWID(), '{_org}', 'OLD_ROUTE', 'Old', 0, 1, 0, 0, 0, 10, 1, SYSUTCDATETIME());");

        // Up.
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        (await ColumnCountAsync()).Should().Be(4);
        (await ScalarAsync<string>("SELECT RouteCategory FROM logistics.fulfillment_routes WHERE Code = 'OLD_ROUTE'"))
            .Should().Be("STOCK", "R-2: every existing route becomes a stock route");

        // The CHECK refuses an unknown category, accepts the reserved ones.
        var bad = () => ExecAsync("UPDATE logistics.fulfillment_routes SET RouteCategory = 'TELEPORT' WHERE Code = 'OLD_ROUTE'");
        await bad.Should().ThrowAsync<SqlException>();
        await ExecAsync("UPDATE logistics.fulfillment_routes SET RouteCategory = 'DROPSHIP' WHERE Code = 'OLD_ROUTE'");
        await ExecAsync("UPDATE logistics.fulfillment_routes SET RouteCategory = 'STOCK' WHERE Code = 'OLD_ROUTE'");

        // Down.
        await using (var db = NewContext()) await db.GetService<IMigrator>().MigrateAsync(A33);
        (await ColumnCountAsync()).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM logistics.fulfillment_routes")).Should().Be(1, "down drops no rows");

        // Up again.
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        (await ColumnCountAsync()).Should().Be(4);

        // Re-run on a database that already has the schema but lost the history row (the drifted shared database).
        await ExecAsync($"DELETE FROM [dbo].[__EFMigrationsHistory] WHERE MigrationId LIKE '%{A34Name}'");
        var historyTable = await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory] WHERE MigrationId LIKE '%{A34Name}'");
        historyTable.Should().Be(0);
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        (await ColumnCountAsync()).Should().Be(4);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory] WHERE MigrationId LIKE '%{A34Name}'"))
            .Should().Be(1);

        // The model matches the migrated schema: the seeder writes the MFG routes with their category.
        await using (var db = NewContext())
            (await new FulfillmentRouteSeeder(db).EnsureSeededAsync(_org)).Should().Be(5);
        (await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM logistics.fulfillment_routes WHERE OrganizationId = '{_org}' AND RouteCategory = 'MANUFACTURE'"))
            .Should().Be(2);
    }
}
