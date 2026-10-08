using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Demand.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A34 PC-01 on real SQL Server (LocalDB): the Demand migration A34_LeadTimeAndProductionOnSales goes up, down and up
/// again through the real migration chain, and runs again cleanly on a database that already has everything it adds
/// (SMSGlobal has drifted from its migration history, and every API start migrates it).
/// </summary>
public sealed class A34DemandMigrationTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private const string A33 = "20261003081715_A33_FulfillmentRoutesOnSaleOrders";
    private readonly string _connection = $"{Server}Database=A34_DEM_Migr_{Guid.NewGuid():N};";

    private DemandDbContext NewContext() => new(
        new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = Guid.NewGuid() });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private static readonly (string Table, string Column, string Type)[] Added =
    [
        ("sale_inquiry_lines",   "CalculatedLeadTimeDays",         "int"),
        ("sale_inquiry_lines",   "CalculatedDeliveryDate",         "date"),
        ("sale_inquiry_lines",   "LeadTimeCalculatedAt",           "datetime2"),
        ("sale_quotation_lines", "CalculatedLeadTimeDays",         "int"),
        ("sale_quotation_lines", "CalculatedDeliveryDate",         "date"),
        ("sale_quotation_lines", "LeadTimeCalculatedAt",           "datetime2"),
        ("sale_order_lines",     "CalculatedLeadTimeDays",         "int"),
        ("sale_order_lines",     "CalculatedDeliveryDate",         "date"),
        ("sale_order_lines",     "LeadTimeCalculatedAt",           "datetime2"),
        ("sale_order_lines",     "ManualDeliveryDate",             "date"),
        ("sale_order_lines",     "FulfillmentRouteCategory",       "nvarchar"),
        ("sale_order_lines",     "ProductionShortfallQty",         "decimal"),
        ("sale_orders",          "ProductionCreationPendingSince", "datetime2")
    ];

    private static async Task<List<(string Table, string Column, string Type, int? Length, byte? Precision, int? Scale, bool Nullable)>> ColumnsAsync(DemandDbContext db)
    {
        var result = new List<(string, string, string, int?, byte?, int?, bool)>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, IS_NULLABLE " +
                          "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'demand'";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetByte(4),
                reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.GetString(6) == "YES"));
        return result;
    }

    private static async Task<bool> PendingIndexExistsAsync(DemandDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_sale_orders_ProductionCreationPendingSince' " +
                          "AND object_id = OBJECT_ID(N'demand.sale_orders') AND has_filter = 1";
        return (int)(await cmd.ExecuteScalarAsync())! == 1;
    }

    private static void ShouldHaveEverything(List<(string Table, string Column, string Type, int? Length, byte? Precision, int? Scale, bool Nullable)> columns)
    {
        foreach (var (table, column, type) in Added)
        {
            var found = columns.Where(c => c.Table == table && c.Column == column).ToList();
            found.Should().ContainSingle($"{table}.{column} is added");
            found[0].Type.Should().Be(type, $"{table}.{column}");
            found[0].Nullable.Should().BeTrue($"{table}.{column} is additive and nullable");
        }
        columns.Single(c => c.Table == "sale_order_lines" && c.Column == "FulfillmentRouteCategory").Length.Should().Be(20);
        var shortfall = columns.Single(c => c.Table == "sale_order_lines" && c.Column == "ProductionShortfallQty");
        shortfall.Precision.Should().Be(18);
        shortfall.Scale.Should().Be(4);
    }

    [Fact]
    public async Task Goes_up_down_and_up_and_reruns_cleanly_on_a_database_that_already_has_it()
    {
        await using var db = NewContext();
        var a34 = db.Database.GetMigrations().Single(m => m.EndsWith("_A34_LeadTimeAndProductionOnSales", StringComparison.Ordinal));
        // Up, through the chain to A34 (A35 came after it and has its own test).
        await db.GetService<IMigrator>().MigrateAsync(a34);
        ShouldHaveEverything(await ColumnsAsync(db));
        (await PendingIndexExistsAsync(db)).Should().BeTrue();

        // Down to A33: everything it added is gone, nothing else.
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(A33);
        var down = await ColumnsAsync(db);
        foreach (var (table, column, _) in Added)
            down.Should().NotContain(c => c.Table == table && c.Column == column, $"{table}.{column} is dropped by Down");
        (await PendingIndexExistsAsync(db)).Should().BeFalse();
        down.Should().Contain(c => c.Table == "sale_orders" && c.Column == "DeliveryCreationPendingSince", "A33's columns stay");

        // Up again.
        await migrator.MigrateAsync(a34);
        ShouldHaveEverything(await ColumnsAsync(db));

        // Drift: the history forgets A34 but the columns and index are there (SMSGlobal). It must run again cleanly.
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetDeleteScript(a34));
        await migrator.MigrateAsync(a34);
        ShouldHaveEverything(await ColumnsAsync(db));
        (await PendingIndexExistsAsync(db)).Should().BeTrue();
        (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(a34);
    }
}
