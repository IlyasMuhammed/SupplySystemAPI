using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A41 M14 / D-13 on real SQL Server (LocalDB): A41_SourceOpportunity adds SourceOpportunityUuid + its filtered index to the
/// inquiry, quotation and sale order tables, goes down and up, and re-runs cleanly when the shared database already has it.
/// </summary>
public sealed class A41SourceOpportunityMigrationTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A41_DEM_Migr_{Guid.NewGuid():N};";
    private static readonly string[] Tables = ["sale_inquiries", "sale_quotations", "sale_orders"];

    private DemandDbContext NewContext(Guid? org = null) => new(
        new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = org ?? Guid.NewGuid() });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private static async Task<int> CountAsync(DemandDbContext db, string sql)
    {
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private static Task<int> ColumnsAsync(DemandDbContext db) => CountAsync(db,
        "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'demand' AND COLUMN_NAME = 'SourceOpportunityUuid' " +
        "AND TABLE_NAME IN ('sale_inquiries', 'sale_quotations', 'sale_orders')");

    private static Task<int> IndexesAsync(DemandDbContext db) => CountAsync(db,
        "SELECT COUNT(*) FROM sys.indexes WHERE name IN ('IX_sale_inquiries_SourceOpportunity', 'IX_sale_quotations_SourceOpportunity', " +
        "'IX_sale_orders_SourceOpportunity') AND has_filter = 1");

    [Fact]
    public async Task Goes_up_down_up_and_reruns_on_a_database_that_already_has_it()
    {
        await using var db = NewContext();
        var a41 = db.Database.GetMigrations().Single(m => m.EndsWith("_A41_SourceOpportunity", StringComparison.Ordinal));
        var before = db.Database.GetMigrations().TakeWhile(m => m != a41).Last();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(a41);
        (await ColumnsAsync(db)).Should().Be(3);
        (await IndexesAsync(db)).Should().Be(3);

        await migrator.MigrateAsync(before);
        (await ColumnsAsync(db)).Should().Be(0);
        (await IndexesAsync(db)).Should().Be(0);

        await migrator.MigrateAsync(a41);
        // Drift: the history forgets A41 but the columns and indexes are there. The guarded Up runs again without error.
        await db.Database.ExecuteSqlRawAsync(db.GetService<IHistoryRepository>().GetDeleteScript(a41));
        await migrator.MigrateAsync(a41);
        (await ColumnsAsync(db)).Should().Be(3);
        (await IndexesAsync(db)).Should().Be(3);
    }

    [Fact]
    public async Task An_order_typed_from_an_opportunity_keeps_it_and_the_source_type_fits_the_column()
    {
        var org = Guid.NewGuid();
        var opp = Guid.NewGuid();
        await using (var db = NewContext(org))
        {
            await db.Database.MigrateAsync();
            db.SaleOrders.Add(new SaleOrder
            {
                OrganizationId = org, SoNumber = "SO-2026-00001", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
                CurrencyId = Guid.NewGuid(), Status = "DRAFT", SourceType = "FROM_OPPORTUNITY", SourceOpportunityUuid = opp
            });
            await db.SaveChangesAsync();
        }
        await using var check = NewContext(org);
        (await check.SaleOrders.IgnoreQueryFilters().SingleAsync(o => o.SourceOpportunityUuid == opp)).SourceType.Should().Be("FROM_OPPORTUNITY");
    }
}
