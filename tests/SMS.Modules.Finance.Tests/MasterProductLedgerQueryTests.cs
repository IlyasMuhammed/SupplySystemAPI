using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
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

    internal static Task PostAsync(
        MasterProductLedgerService svc, int productId, int warehouseId, string transactionType,
        string sourceType, string sourceName, string destinationType, string destinationName,
        decimal? qtyIn = null, decimal? qtyOut = null, decimal unitCost = 10m,
        int categoryId = 1, DateTime? at = null) =>
        svc.PostMovementAsync(new ProductMovementContext
        {
            ProductId       = productId,
            ProductCode     = $"P-{productId}",
            ProductName     = $"Product {productId}",
            CategoryId      = categoryId,
            CategoryName    = "IT Equipment",
            WarehouseId     = warehouseId,
            WarehouseName   = $"WH-{warehouseId}",
            TransactionType = transactionType,
            ReferenceType   = "REF",
            ReferenceId     = Guid.NewGuid(),
            ReferenceNumber = $"REF-{Guid.NewGuid():N}"[..10],
            QuantityIn      = qtyIn,
            QuantityOut     = qtyOut,
            UnitCost        = unitCost,
            SourceType      = sourceType,
            SourceName      = sourceName,
            DestinationType = destinationType,
            DestinationName = destinationName,
            CreatedBy       = 1
        });
}

public class MasterProductLedgerQuery_Tests
{
    [Fact]
    public async Task Unfiltered_View_Shows_All_Movements_Across_All_Warehouses()
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        await Build.PostAsync(svc, 1, 10, "GRN_RECEIPT", "SUPPLIER", "Lenovo", "WAREHOUSE", "Main WH", qtyIn: 100m);
        await Build.PostAsync(svc, 1, 20, "MATERIAL_ISSUE", "WAREHOUSE", "Site WH", "PROJECT", "Tower A", qtyOut: 20m);

        var result = await svc.GetProductLedgerAsync(new MasterProductLedgerFilter { PageSize = 50 });

        result.TotalRecords.Should().Be(2);
        result.Data.Should().HaveCount(2);
    }

    [Fact]
    public async Task Filtering_By_Product_Shows_Only_That_Products_Movements()
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        await Build.PostAsync(svc, 1, 10, "GRN_RECEIPT", "SUPPLIER", "Lenovo", "WAREHOUSE", "Main WH", qtyIn: 100m);
        await Build.PostAsync(svc, 2, 10, "GRN_RECEIPT", "SUPPLIER", "Dell", "WAREHOUSE", "Main WH", qtyIn: 50m);

        var result = await svc.GetProductLedgerAsync(new MasterProductLedgerFilter { ProductId = 1, PageSize = 50 });

        result.Data.Should().ContainSingle();
        result.Data[0].ProductId.Should().Be(1);
    }

    [Fact]
    public async Task Filtering_By_DestinationType_Project_Shows_All_Issues_To_Projects()
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        await Build.PostAsync(svc, 1, 10, "MATERIAL_ISSUE", "WAREHOUSE", "Main WH", "PROJECT", "Tower A", qtyOut: 10m);
        await Build.PostAsync(svc, 1, 10, "MATERIAL_ISSUE", "WAREHOUSE", "Main WH", "DEPARTMENT", "IT Dept", qtyOut: 5m);
        await Build.PostAsync(svc, 1, 10, "RETURN_DISPATCH", "WAREHOUSE", "Main WH", "SUPPLIER", "Lenovo", qtyOut: 2m);

        var result = await svc.GetProductLedgerAsync(new MasterProductLedgerFilter { DestinationType = "PROJECT", PageSize = 50 });

        result.Data.Should().ContainSingle();
        result.Data[0].DestinationType.Should().Be("PROJECT");
        result.Data[0].DestinationName.Should().Be("Tower A");
    }

    [Fact]
    public async Task Summary_Correctly_Totals_QtyIn_And_QtyOut_For_The_Filtered_Period()
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        await Build.PostAsync(svc, 1, 10, "GRN_RECEIPT", "SUPPLIER", "Lenovo", "WAREHOUSE", "Main WH", qtyIn: 100m, unitCost: 500m);
        await Build.PostAsync(svc, 1, 10, "MATERIAL_ISSUE", "WAREHOUSE", "Main WH", "PROJECT", "Tower A", qtyOut: 20m, unitCost: 500m);
        await Build.PostAsync(svc, 1, 10, "MATERIAL_ISSUE", "WAREHOUSE", "Main WH", "PROJECT", "Tower A", qtyOut: 5m, unitCost: 500m);

        var summary = await svc.GetProductLedgerSummaryAsync(new MasterProductLedgerFilter { ProductId = 1 });

        summary.TotalReceiptsQty.Should().Be(100m);
        summary.TotalIssuesQty.Should().Be(25m);
        summary.NetMovement.Should().Be(75m);
        summary.TotalValueMoved.Should().Be(62500m); // (100 + 20 + 5) x 500
    }

    [Fact]
    public async Task ProductJourney_Returns_Chronological_History_Across_Multiple_Warehouses()
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        // GRN receipt into Main WH, then a transfer to Site WH, then issue to a department —
        // three different warehouses touched by the same product.
        await Build.PostAsync(svc, 1, 10, "GRN_RECEIPT", "SUPPLIER", "Lenovo", "WAREHOUSE", "Main WH", qtyIn: 5m);
        await Build.PostAsync(svc, 1, 10, "TRANSFER_OUT", "WAREHOUSE", "Main WH", "WAREHOUSE", "Site WH", qtyOut: 5m);
        await Build.PostAsync(svc, 1, 30, "TRANSFER_IN", "WAREHOUSE", "Main WH", "WAREHOUSE", "Site WH", qtyIn: 5m);
        await Build.PostAsync(svc, 1, 30, "MATERIAL_ISSUE", "WAREHOUSE", "Site WH", "DEPARTMENT", "IT Department", qtyOut: 1m);

        var journey = await svc.GetProductJourneyAsync(1);

        journey.Should().HaveCount(4);
        journey.Select(e => e.TransactionType).Should().ContainInOrder(
            "GRN_RECEIPT", "TRANSFER_OUT", "TRANSFER_IN", "MATERIAL_ISSUE");
        journey.Select(e => e.WarehouseId).Should().Contain([10, 30]);
        journey.Last().DestinationType.Should().Be("DEPARTMENT");
        journey.Last().DestinationName.Should().Be("IT Department");
    }

    [Fact]
    public async Task ProductJourney_Ignores_Other_Products()
    {
        var db  = Build.NewFinanceDb();
        var svc = new MasterProductLedgerService(db);

        await Build.PostAsync(svc, 1, 10, "GRN_RECEIPT", "SUPPLIER", "Lenovo", "WAREHOUSE", "Main WH", qtyIn: 5m);
        await Build.PostAsync(svc, 2, 10, "GRN_RECEIPT", "SUPPLIER", "Dell", "WAREHOUSE", "Main WH", qtyIn: 5m);

        var journey = await svc.GetProductJourneyAsync(1);

        journey.Should().ContainSingle();
        journey[0].ProductId.Should().Be(1);
    }
}
