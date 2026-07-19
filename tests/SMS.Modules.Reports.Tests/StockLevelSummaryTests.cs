using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Data;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Repositories;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Services;
using Xunit;
using InventoryWarehouse = SMS.Modules.Inventory.Domain.Warehouse;
using WarehouseDbContext = SMS.Modules.Warehouse.Data.WarehouseDbContext;

namespace SMS.Modules.Reports.Tests;

file static class Build
{
    internal static InventoryDbContext NewInventoryDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);

    internal static ReportsRepository NewRepo(InventoryDbContext inventory)
    {
        var dbName = Guid.NewGuid().ToString();
        return new ReportsRepository(
            db:         new ReportsDbContext(new DbContextOptionsBuilder<ReportsDbContext>().UseInMemoryDatabase(dbName).Options),
            demand:     new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options),
            warehouse:  new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(dbName).Options),
            inventory:  inventory,
            finance:    new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options),
            logistics:  new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>().UseInMemoryDatabase(dbName).Options),
            suppliers:  new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(dbName).Options),
            material:   new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>().UseInMemoryDatabase(dbName).Options),
            userQuery:  new Mock<IUserQueryService>().Object,
            timeline:   new Mock<ITimelineService>().Object);
    }

    internal static ProductCategory SeedCategory(InventoryDbContext db, string name)
    {
        var c = new ProductCategory { Name = name, Code = name.ToUpper(), IsActive = true, CreatedDate = DateTime.UtcNow };
        db.ProductCategories.Add(c);
        db.SaveChanges();
        return c;
    }

    internal static ProductSubCategory SeedSubCategory(InventoryDbContext db, int categoryId, string name)
    {
        var sc = new ProductSubCategory { CategoryId = categoryId, Name = name, Code = name.ToUpper(), IsActive = true, CreatedDate = DateTime.UtcNow };
        db.ProductSubCategories.Add(sc);
        db.SaveChanges();
        return sc;
    }

    internal static InventoryWarehouse SeedWarehouse(InventoryDbContext db, string name)
    {
        var w = new InventoryWarehouse { Uuid = Guid.NewGuid(), Code = name.ToUpper(), Name = name, IsActive = true, CreatedDate = DateTime.UtcNow, CreatedBy = 1 };
        db.Warehouses.Add(w);
        db.SaveChanges();
        return w;
    }

    internal static Product SeedProduct(
        InventoryDbContext db, string sku, string name, int? categoryId = null, int? subCategoryId = null, decimal? reorderPoint = null)
    {
        var p = new Product
        {
            Uuid = Guid.NewGuid(), Sku = sku, Name = name, CategoryId = categoryId, SubCategoryId = subCategoryId,
            ReorderPoint = reorderPoint, Status = "ACTIVE", IsActive = true, CreatedDate = DateTime.UtcNow, CreatedBy = 1
        };
        db.Products.Add(p);
        db.SaveChanges();
        return p;
    }

    internal static InventoryItem SeedInventoryItem(
        InventoryDbContext db, int productId, int warehouseId, decimal qtyOnHand, decimal qtyReserved, decimal? unitCost)
    {
        var i = new InventoryItem
        {
            Uuid = Guid.NewGuid(), ProductId = productId, WarehouseId = warehouseId,
            QtyOnHand = qtyOnHand, QtyReserved = qtyReserved, UnitCost = unitCost, LastUpdated = DateTime.UtcNow
        };
        db.InventoryItems.Add(i);
        db.SaveChanges();
        return i;
    }
}

public class GetStockLevelSummaryAsync_Tests
{
    [Fact]
    public async Task Returns_Correct_QtyOnHand_QtyReserved_QtyAvailable_For_Known_Product_Warehouse()
    {
        var db = Build.NewInventoryDb();
        var wh = Build.SeedWarehouse(db, "Main Warehouse");
        var product = Build.SeedProduct(db, "SKU-001", "Widget");
        Build.SeedInventoryItem(db, product.Id, wh.Id, qtyOnHand: 100m, qtyReserved: 30m, unitCost: 25m);

        var repo = Build.NewRepo(db);
        var result = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter());

        var row = result.Items.Single();
        row.QtyOnHand.Should().Be(100m);
        row.QtyReserved.Should().Be(30m);
        row.QtyAvailable.Should().Be(70m);
        row.ProductCode.Should().Be("SKU-001");
        row.Warehouse.Should().Be("Main Warehouse");
    }

    [Fact]
    public async Task Filtering_By_Warehouse_Returns_Only_Products_In_That_Warehouse()
    {
        var db = Build.NewInventoryDb();
        var whA = Build.SeedWarehouse(db, "Warehouse A");
        var whB = Build.SeedWarehouse(db, "Warehouse B");
        var productA = Build.SeedProduct(db, "SKU-A", "Product A");
        var productB = Build.SeedProduct(db, "SKU-B", "Product B");
        Build.SeedInventoryItem(db, productA.Id, whA.Id, 50m, 0m, 10m);
        Build.SeedInventoryItem(db, productB.Id, whB.Id, 60m, 0m, 10m);

        var repo = Build.NewRepo(db);
        var result = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter { WarehouseId = whA.Id });

        result.Items.Should().ContainSingle();
        result.Items[0].Warehouse.Should().Be("Warehouse A");
    }

    [Fact]
    public async Task Filtering_By_Category_And_SubCategory_Works()
    {
        var db = Build.NewInventoryDb();
        var wh = Build.SeedWarehouse(db, "Main");
        var catA = Build.SeedCategory(db, "Electronics");
        var catB = Build.SeedCategory(db, "Furniture");
        var subA = Build.SeedSubCategory(db, catA.Id, "Cables");
        var productA = Build.SeedProduct(db, "SKU-A", "Cable", catA.Id, subA.Id);
        var productB = Build.SeedProduct(db, "SKU-B", "Chair", catB.Id);
        Build.SeedInventoryItem(db, productA.Id, wh.Id, 10m, 0m, 5m);
        Build.SeedInventoryItem(db, productB.Id, wh.Id, 20m, 0m, 5m);

        var repo = Build.NewRepo(db);

        var byCategory = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter { CategoryId = catA.Id });
        byCategory.Items.Should().ContainSingle(i => i.ProductCode == "SKU-A");

        var bySubCategory = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter { SubCategoryId = subA.Id });
        bySubCategory.Items.Should().ContainSingle(i => i.ProductCode == "SKU-A");
    }

    [Fact]
    public async Task Filtering_By_Search_Matches_Product_Name_Or_Code()
    {
        var db = Build.NewInventoryDb();
        var wh = Build.SeedWarehouse(db, "Main");
        var product = Build.SeedProduct(db, "WID-100", "Blue Widget");
        Build.SeedProduct(db, "OTH-200", "Other Item");
        Build.SeedInventoryItem(db, product.Id, wh.Id, 10m, 0m, 5m);

        var repo = Build.NewRepo(db);
        var byName = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter { Search = "widget" });
        byName.Items.Should().ContainSingle(i => i.ProductCode == "WID-100");

        var byCode = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter { Search = "wid-100" });
        byCode.Items.Should().ContainSingle(i => i.ProductCode == "WID-100");
    }

    [Fact]
    public async Task TotalValue_Is_QtyOnHand_Times_UnitCost_Per_Row()
    {
        var db = Build.NewInventoryDb();
        var wh = Build.SeedWarehouse(db, "Main");
        var product = Build.SeedProduct(db, "SKU-X", "X");
        Build.SeedInventoryItem(db, product.Id, wh.Id, qtyOnHand: 40m, qtyReserved: 0m, unitCost: 12.5m);

        var repo = Build.NewRepo(db);
        var result = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter());

        result.Items.Single().TotalValue.Should().Be(500m); // 40 * 12.5
    }

    [Fact]
    public async Task GrandTotal_Sums_TotalValue_Across_All_Filtered_Rows()
    {
        var db = Build.NewInventoryDb();
        var wh = Build.SeedWarehouse(db, "Main");
        var p1 = Build.SeedProduct(db, "SKU-1", "One");
        var p2 = Build.SeedProduct(db, "SKU-2", "Two");
        var p3 = Build.SeedProduct(db, "SKU-3", "Three");
        Build.SeedInventoryItem(db, p1.Id, wh.Id, 10m, 0m, 10m);  // 100
        Build.SeedInventoryItem(db, p2.Id, wh.Id, 20m, 0m, 5m);   // 100
        Build.SeedInventoryItem(db, p3.Id, wh.Id, 5m, 0m, 4m);    // 20

        var repo = Build.NewRepo(db);
        var result = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter());

        result.GrandTotalValue.Should().Be(220m);
        result.Items.Sum(i => i.TotalValue).Should().Be(result.GrandTotalValue);
    }

    [Fact]
    public async Task Products_Below_Reorder_Level_Are_Flagged()
    {
        var db = Build.NewInventoryDb();
        var wh = Build.SeedWarehouse(db, "Main");
        var lowStock  = Build.SeedProduct(db, "SKU-LOW", "Low Stock Item", reorderPoint: 50m);
        var highStock = Build.SeedProduct(db, "SKU-HIGH", "High Stock Item", reorderPoint: 50m);
        // available = 20 - 0 = 20 <= reorderPoint 50 -> below reorder level
        Build.SeedInventoryItem(db, lowStock.Id, wh.Id, qtyOnHand: 20m, qtyReserved: 0m, unitCost: 1m);
        // available = 100 - 0 = 100 > reorderPoint 50 -> not below
        Build.SeedInventoryItem(db, highStock.Id, wh.Id, qtyOnHand: 100m, qtyReserved: 0m, unitCost: 1m);

        var repo = Build.NewRepo(db);
        var result = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter());

        result.Items.Single(i => i.ProductCode == "SKU-LOW").BelowReorderLevel.Should().BeTrue();
        result.Items.Single(i => i.ProductCode == "SKU-HIGH").BelowReorderLevel.Should().BeFalse();
    }

    [Fact]
    public async Task Product_With_No_ReorderPoint_Is_Never_Flagged()
    {
        var db = Build.NewInventoryDb();
        var wh = Build.SeedWarehouse(db, "Main");
        var product = Build.SeedProduct(db, "SKU-NONE", "No Reorder Point", reorderPoint: null);
        Build.SeedInventoryItem(db, product.Id, wh.Id, qtyOnHand: 0m, qtyReserved: 0m, unitCost: 1m);

        var repo = Build.NewRepo(db);
        var result = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter());

        result.Items.Single().BelowReorderLevel.Should().BeFalse();
        result.Items.Single().ReorderPoint.Should().BeNull();
    }

    [Fact]
    public async Task Returns_Correct_Category_And_SubCategory_Names()
    {
        var db = Build.NewInventoryDb();
        var wh = Build.SeedWarehouse(db, "Main");
        var cat = Build.SeedCategory(db, "Electronics");
        var sub = Build.SeedSubCategory(db, cat.Id, "Cables");
        var product = Build.SeedProduct(db, "SKU-1", "Cable", cat.Id, sub.Id);
        Build.SeedInventoryItem(db, product.Id, wh.Id, 10m, 0m, 1m);

        var repo = Build.NewRepo(db);
        var result = await repo.GetStockLevelSummaryAsync(new StockLevelSummaryFilter());

        result.Items.Single().Category.Should().Be("Electronics");
        result.Items.Single().SubCategory.Should().Be("Cables");
    }
}
