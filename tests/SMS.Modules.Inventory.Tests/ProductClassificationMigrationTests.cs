using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Migrations;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// The classification migration's own SQL (A30 M1 + M2), run against a real SQL Server. The
/// integration factory builds schemas from the model and stamps the history, so this is the only
/// place the statements execute before they reach the shared dev database.
/// </summary>
public class ProductClassificationMigrationTests
{
    private static readonly string[] Columns =
        ["ProductType", "SupplyMethod", "IsSaleable", "IsPurchasable", "IsStockable", "IsManufacturable", "DefaultProductionWarehouseId"];

    [SqlServerFact]
    public async Task Existing_products_become_purchased_stock_items_and_the_migration_replays_safely()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();

        // A product that predates the columns: seed through the model, then take the columns
        // away again so the table looks the way it did before this migration.
        var stock = await harness.SeedStockAsync(onHand: 1);
        await harness.ExecuteAsync("ALTER TABLE [inventory].[Products] DROP CONSTRAINT [FK_Products_Warehouses_DefaultProductionWarehouseId]");
        await harness.ExecuteAsync("DROP INDEX [IX_Products_DefaultProductionWarehouseId] ON [inventory].[Products]");
        await harness.ExecuteAsync("DROP INDEX [IX_Products_OrganizationId_SupplyMethod] ON [inventory].[Products]");
        foreach (var column in Columns)
            await harness.ExecuteAsync($"ALTER TABLE [inventory].[Products] DROP COLUMN [{column}]");
        (await harness.ColumnExistsAsync("inventory.Products", "ProductType")).Should().BeFalse();

        await harness.ApplyUpAsync(new AddProductManufacturingClassification());

        foreach (var column in Columns)
            (await harness.ColumnExistsAsync("inventory.Products", column)).Should().BeTrue(column);

        var replay = async () => await harness.ApplyUpAsync(new AddProductManufacturingClassification());
        await replay.Should().NotThrowAsync("every statement is guarded");

        // §6.6 — the pre-existing product is a purchased, saleable, stockable stock item.
        await using var db = harness.NewContext(stock.OrganizationId);
        var product = await db.Products.SingleAsync();
        product.ProductType.Should().Be(SMS.Shared.Common.ProductType.StockItem);
        product.SupplyMethod.Should().Be(SMS.Shared.Common.SupplyMethod.Purchase);
        product.IsSaleable.Should().BeTrue();
        product.IsPurchasable.Should().BeTrue();
        product.IsStockable.Should().BeTrue();
        product.IsManufacturable.Should().BeFalse();
        product.DefaultProductionWarehouseId.Should().BeNull();
    }
}
