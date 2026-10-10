using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Inventory.Migrations;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A37 (OPSA) on LocalDB: <c>A37_SyncModifiedAtAndServiceCategory</c> is additive, guarded (replays on the shared
/// database at every API start), backfills ModifiedAt from the created/updated dates, and runs up, down and up again.
/// </summary>
public class ModuleRegistryMigrationTests
{
    private static readonly string[] Synced = ["Products", "ProductVariants", "ProductCategories", "Warehouses"];

    [SqlServerFact]
    public async Task The_A37_migration_is_additive_guarded_backfilled_and_replays()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var stock = await harness.SeedStockAsync(onHand: 1);
        var created = new DateTime(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        await harness.ExecuteAsync($"UPDATE [inventory].[ProductVariants] SET [CreatedDate] = '{created:yyyy-MM-dd HH:mm:ss}'");
        await harness.ExecuteAsync($"UPDATE [inventory].[Products] SET [CreatedDate] = '{created:yyyy-MM-dd HH:mm:ss}', [UpdatedDate] = '2025-04-01'");

        // Take the A37 part away (the harness builds the schema from the current model).
        foreach (var table in Synced)
        {
            await harness.ExecuteAsync($"DROP INDEX [IX_{table}_OrganizationId_ModifiedAt] ON [inventory].[{table}]");
            await harness.ExecuteAsync($"ALTER TABLE [inventory].[{table}] DROP COLUMN [ModifiedAt]");
        }
        await harness.ExecuteAsync("ALTER TABLE [inventory].[Products] DROP COLUMN [ServiceCategory], [RequiresSiteVisit]");

        await harness.ApplyUpAsync(new A37_SyncModifiedAtAndServiceCategory());
        await AssertMigratedAsync(harness);
        await FluentActions.Awaiting(() => harness.ApplyUpAsync(new A37_SyncModifiedAtAndServiceCategory()))
            .Should().NotThrowAsync("every statement is guarded");

        await using (var db = harness.NewContext(stock.OrganizationId))
        {
            var variant = await db.ProductVariants.Include(v => v.Product).SingleAsync(v => v.Uuid == stock.VariantUuid);
            variant.ModifiedAt.Should().Be(created, "backfilled from CreatedDate");
            variant.Product.ModifiedAt.Should().Be(new DateTime(2025, 4, 1), "a product's UpdatedDate wins over CreatedDate");
            variant.Product.RequiresSiteVisit.Should().BeFalse();
            variant.Product.ServiceCategory.Should().BeNull();

            variant.Barcode = "A37";
            await db.SaveChangesAsync();
            variant.ModifiedAt.Should().BeAfter(created, "the context stamps every update");
        }

        foreach (var sql in new A37_SyncModifiedAtAndServiceCategory().DownOperations.OfType<SqlOperation>())
            await harness.ExecuteAsync(sql.Sql);
        foreach (var table in Synced)
            (await harness.ColumnExistsAsync($"inventory.{table}", "ModifiedAt")).Should().BeFalse(table);

        await harness.ApplyUpAsync(new A37_SyncModifiedAtAndServiceCategory());
        await AssertMigratedAsync(harness);
    }

    private static async Task AssertMigratedAsync(InventorySqlServerHarness harness)
    {
        foreach (var table in Synced)
        {
            (await harness.ColumnExistsAsync($"inventory.{table}", "ModifiedAt")).Should().BeTrue(table);
            await using var db = harness.NewContext(Guid.NewGuid());
            (await db.Database.SqlQueryRaw<int>(
                    $"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = 'IX_{table}_OrganizationId_ModifiedAt'")
                .SingleAsync()).Should().Be(1, table);
        }
        (await harness.ColumnExistsAsync("inventory.Products", "ServiceCategory")).Should().BeTrue();
        (await harness.ColumnExistsAsync("inventory.Products", "RequiresSiteVisit")).Should().BeTrue();
    }
}
