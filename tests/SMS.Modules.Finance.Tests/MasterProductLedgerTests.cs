using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

file static class Build
{
    internal static FinanceDbContext NewFinanceDb(string? dbName = null) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options);
}

// ── ML-003: MasterProductLedgerService.PostMovementAsync ────────────────────

public class MasterProductLedgerService_PostMovementAsync_Tests
{
    [Fact]
    public async Task GrnReceipt_Creates_Entry_With_Supplier_Source_And_Warehouse_Destination()
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        await svc.PostMovementAsync(new ProductMovementContext
        {
            ProductId       = 1,
            ProductCode     = "LAPTOP-001",
            ProductName     = "Business Laptop",
            CategoryName    = "IT Equipment",
            WarehouseId     = 10,
            WarehouseName   = "Main Warehouse",
            TransactionType = "GRN_RECEIPT",
            ReferenceType   = "GRN",
            ReferenceId     = Guid.NewGuid(),
            ReferenceNumber = "GRN-2026-00001",
            QuantityIn      = 100m,
            UnitCost        = 500m,
            SourceType      = "SUPPLIER",
            SourceName      = "Lenovo Distributors",
            DestinationType = "WAREHOUSE",
            DestinationName = "Main Warehouse",
            CreatedBy       = 1
        });

        var entry = await db.MasterProductLedgers.SingleAsync();
        entry.SourceType.Should().Be("SUPPLIER");
        entry.SourceName.Should().Be("Lenovo Distributors");
        entry.DestinationType.Should().Be("WAREHOUSE");
        entry.DestinationName.Should().Be("Main Warehouse");
        entry.QuantityIn.Should().Be(100m);
        entry.TotalValue.Should().Be(50000m); // 100 x 500
    }

    [Fact]
    public async Task MaterialIssue_Creates_Entry_With_Warehouse_Source_And_Project_Destination()
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        await svc.PostMovementAsync(new ProductMovementContext
        {
            ProductId       = 2,
            ProductCode     = "CEMENT-050",
            ProductName     = "Cement 50kg",
            WarehouseId     = 10,
            WarehouseName   = "Main WH",
            TransactionType = "MATERIAL_ISSUE",
            ReferenceType   = "MIV",
            ReferenceId     = Guid.NewGuid(),
            ReferenceNumber = "MIV-2026-00001",
            QuantityOut     = 20m,
            UnitCost        = 15m,
            SourceType      = "WAREHOUSE",
            SourceName      = "Main WH",
            DestinationType = "PROJECT",
            DestinationName = "Tower Block A",
            CreatedBy       = 1
        });

        var entry = await db.MasterProductLedgers.SingleAsync();
        entry.SourceType.Should().Be("WAREHOUSE");
        entry.SourceName.Should().Be("Main WH");
        entry.DestinationType.Should().Be("PROJECT");
        entry.DestinationName.Should().Be("Tower Block A");
        entry.QuantityOut.Should().Be(20m);
        entry.TotalValue.Should().Be(300m); // 20 x 15
    }

    [Fact]
    public async Task MaterialReturn_Creates_Entry_With_Project_Source_And_Warehouse_Destination()
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        await svc.PostMovementAsync(new ProductMovementContext
        {
            ProductId       = 2,
            ProductCode     = "CEMENT-050",
            ProductName     = "Cement 50kg",
            WarehouseId     = 10,
            WarehouseName   = "Main WH",
            TransactionType = "MATERIAL_RETURN",
            ReferenceType   = "RETURN_VOUCHER",
            ReferenceId     = Guid.NewGuid(),
            ReferenceNumber = "MRV-2026-00001",
            QuantityIn      = 10m,
            UnitCost        = 15m,
            SourceType      = "PROJECT",
            SourceName      = "Tower Block A",
            DestinationType = "WAREHOUSE",
            DestinationName = "Main WH",
            CreatedBy       = 1
        });

        var entry = await db.MasterProductLedgers.SingleAsync();
        entry.SourceType.Should().Be("PROJECT");
        entry.DestinationType.Should().Be("WAREHOUSE");
        entry.QuantityIn.Should().Be(10m);
        entry.TotalValue.Should().Be(150m); // 10 x 15
    }

    [Fact]
    public async Task TotalValue_Is_Always_Quantity_Times_UnitCost_Regardless_Of_In_Or_Out()
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        await svc.PostMovementAsync(new ProductMovementContext
        {
            ProductId = 1, ProductCode = "X", ProductName = "X", WarehouseId = 1, WarehouseName = "WH",
            TransactionType = "STOCK_ADJUSTMENT", ReferenceType = "ADJUSTMENT", ReferenceId = Guid.NewGuid(),
            ReferenceNumber = "ADJ-1", QuantityOut = 7m, UnitCost = 12.5m,
            SourceType = "WAREHOUSE", SourceName = "WH", DestinationType = "ADJUSTMENT", DestinationName = "Stock Adjustment",
            CreatedBy = 1
        });

        var entry = await db.MasterProductLedgers.SingleAsync();
        entry.TotalValue.Should().Be(87.5m); // 7 x 12.5
    }

    [Theory]
    [InlineData("GRN_RECEIPT",     "SUPPLIER",   "WAREHOUSE")]
    [InlineData("RETURN_DISPATCH", "WAREHOUSE",  "SUPPLIER")]
    [InlineData("STOCK_ADJUSTMENT","ADJUSTMENT", "WAREHOUSE")]
    [InlineData("MATERIAL_ISSUE",  "WAREHOUSE",  "PROJECT")]
    [InlineData("MATERIAL_ISSUE",  "WAREHOUSE",  "DEPARTMENT")]
    [InlineData("MATERIAL_RETURN", "PROJECT",    "WAREHOUSE")]
    [InlineData("MATERIAL_RETURN", "DEPARTMENT", "WAREHOUSE")]
    [InlineData("TRANSFER_OUT",    "WAREHOUSE",  "WAREHOUSE")]
    [InlineData("TRANSFER_IN",     "WAREHOUSE",  "WAREHOUSE")]
    public async Task Each_Wired_Transaction_Type_Maps_To_The_Expected_Source_And_Destination_Types(
        string transactionType, string sourceType, string destinationType)
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        await svc.PostMovementAsync(new ProductMovementContext
        {
            ProductId = 1, ProductCode = "X", ProductName = "X", WarehouseId = 1, WarehouseName = "WH",
            TransactionType = transactionType, ReferenceType = "REF", ReferenceId = Guid.NewGuid(),
            ReferenceNumber = "REF-1", QuantityIn = 1m, UnitCost = 1m,
            SourceType = sourceType, SourceName = "Source", DestinationType = destinationType, DestinationName = "Destination",
            CreatedBy = 1
        });

        var entry = await db.MasterProductLedgers.SingleAsync();
        entry.TransactionType.Should().Be(transactionType);
        entry.SourceType.Should().Be(sourceType);
        entry.DestinationType.Should().Be(destinationType);
    }
}
