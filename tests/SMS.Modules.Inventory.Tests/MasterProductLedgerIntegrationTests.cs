using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using InventoryWarehouse = SMS.Modules.Inventory.Domain.Warehouse;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

// FSD Addendum 24 (ML-003) — verifies InventoryLedgerService.CreateEntryAsync's integration with
// the master product ledger. MasterProductLedgerService is constructed directly here (no DI) against
// a real (InMemory) FinanceDbContext, exactly like production DI would resolve it — proving the
// wiring works end to end, not just that each half compiles in isolation.

file static class Build
{
    internal static (InventoryLedgerService service, InventoryDbContext inv, FinanceDbContext fin) New(
        IMasterProductLedgerService? masterLedger = null)
    {
        var invOpts = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var finOpts = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        var tenantContext = new StaticTenantContext();
        var inv = new InventoryDbContext(invOpts, tenantContext);
        var fin = new FinanceDbContext(finOpts, tenantContext);
        var masterLedgerSvc = masterLedger ?? new MasterProductLedgerService(fin);
        var service = new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance, masterLedgerSvc);
        return (service, inv, fin);
    }

    internal static async Task<(Product product, InventoryWarehouse warehouse)> SeedAsync(InventoryDbContext db)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = "LAPTOP-001", Name = "Business Laptop",
            Status = "ACTIVE", IsActive = true, CreatedBy = 1
        };
        var warehouse = new InventoryWarehouse
        {
            Uuid = Guid.NewGuid(), Code = "WH1", Name = "Main Warehouse",
            IsActive = true, CreatedBy = 1
        };
        db.Products.Add(product);
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();
        return (product, warehouse);
    }
}

public class MasterProductLedger_Integration_Tests
{
    [Fact]
    public async Task CreateEntryAsync_With_SourceDestination_Writes_Both_The_InventoryEntry_And_MasterProductEntry()
    {
        var (service, inv, fin) = Build.New();
        var (product, warehouse) = await Build.SeedAsync(inv);

        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            ProductId       = product.Id,
            WarehouseId     = warehouse.Id,
            TransactionType = "GRN_RECEIPT",
            ReferenceType   = "GRN",
            ReferenceId     = Guid.NewGuid(),
            ReferenceNumber = "GRN-2026-00001",
            QuantityIn      = 100m,
            UnitCost        = 500m,
            CreatedBy       = 1,
            SourceType      = "SUPPLIER",
            SourceName      = "Lenovo Distributors",
            DestinationType = "WAREHOUSE",
            DestinationName = warehouse.Name
        });
        await inv.SaveChangesAsync();

        (await inv.InventoryLedgerEntries.CountAsync()).Should().Be(1);

        var master = await fin.MasterProductLedgers.SingleAsync();
        master.ProductCode.Should().Be("LAPTOP-001");
        master.ProductName.Should().Be("Business Laptop");
        master.WarehouseName.Should().Be("Main Warehouse");
        master.SourceType.Should().Be("SUPPLIER");
        master.SourceName.Should().Be("Lenovo Distributors");
        master.DestinationType.Should().Be("WAREHOUSE");
        master.DestinationName.Should().Be("Main Warehouse");
        master.QuantityIn.Should().Be(100m);
        master.TotalValue.Should().Be(50000m);
    }

    [Fact]
    public async Task CreateEntryAsync_Without_SourceDestination_Skips_The_Master_Write()
    {
        var (service, inv, fin) = Build.New();
        var (product, warehouse) = await Build.SeedAsync(inv);

        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            ProductId = product.Id, WarehouseId = warehouse.Id,
            TransactionType = "GRN_RECEIPT", ReferenceType = "GRN",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "GRN-1",
            QuantityIn = 10m, UnitCost = 5m, CreatedBy = 1
            // No SourceType/DestinationType — matches every existing pre-ML-003 test/call site.
        });
        await inv.SaveChangesAsync();

        (await inv.InventoryLedgerEntries.CountAsync()).Should().Be(1);
        (await fin.MasterProductLedgers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Simulated_Master_Product_Ledger_Failure_Prevents_The_InventoryLedgerEntry_From_Being_Saved()
    {
        var invOpts = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var inv = new InventoryDbContext(invOpts, new StaticTenantContext());

        var failingMaster = new Mock<IMasterProductLedgerService>();
        failingMaster
            .Setup(m => m.PostMovementAsync(It.IsAny<ProductMovementContext>(), It.IsAny<System.Data.Common.DbTransaction>()))
            .ThrowsAsync(new InvalidOperationException("Simulated master product ledger failure"));

        var service = new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance, failingMaster.Object);
        var (product, warehouse) = await Build.SeedAsync(inv);

        var act = async () => await service.CreateEntryAsync(new LedgerEntryCommand
        {
            ProductId = product.Id, WarehouseId = warehouse.Id,
            TransactionType = "GRN_RECEIPT", ReferenceType = "GRN",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "GRN-1",
            QuantityIn = 10m, UnitCost = 5m, CreatedBy = 1,
            SourceType = "SUPPLIER", SourceName = "Some Supplier",
            DestinationType = "WAREHOUSE", DestinationName = warehouse.Name
        });

        await act.Should().ThrowAsync<InvalidOperationException>();

        // CreateEntryAsync never calls SaveChangesAsync itself — the caller does, AFTER this call
        // returns. Since it threw, the caller's SaveChangesAsync never runs, so nothing (including
        // the already-tracked-but-unsaved InventoryLedgerEntry) is actually persisted.
        (await inv.InventoryLedgerEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MaterialIssue_To_Project_Writes_Correct_Source_And_Destination()
    {
        var (service, inv, fin) = Build.New();
        var (product, warehouse) = await Build.SeedAsync(inv);

        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            ProductId = product.Id, WarehouseId = warehouse.Id,
            TransactionType = "MATERIAL_ISSUE", ReferenceType = "MIV",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "MIV-2026-00001",
            QuantityOut = 20m, UnitCost = 15m, CreatedBy = 1,
            SourceType = "WAREHOUSE", SourceName = warehouse.Name,
            DestinationType = "PROJECT", DestinationName = "Tower Block A"
        });
        await inv.SaveChangesAsync();

        var master = await fin.MasterProductLedgers.SingleAsync();
        master.SourceType.Should().Be("WAREHOUSE");
        master.SourceName.Should().Be("Main Warehouse");
        master.DestinationType.Should().Be("PROJECT");
        master.DestinationName.Should().Be("Tower Block A");
        master.QuantityOut.Should().Be(20m);
        master.TotalValue.Should().Be(300m);
    }

    [Fact]
    public async Task MaterialReturn_From_Project_Writes_Correct_Source_And_Destination()
    {
        var (service, inv, fin) = Build.New();
        var (product, warehouse) = await Build.SeedAsync(inv);

        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            ProductId = product.Id, WarehouseId = warehouse.Id,
            TransactionType = "MATERIAL_RETURN", ReferenceType = "RETURN_VOUCHER",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "MRV-2026-00001",
            QuantityIn = 10m, UnitCost = 15m, CreatedBy = 1,
            SourceType = "PROJECT", SourceName = "Tower Block A",
            DestinationType = "WAREHOUSE", DestinationName = warehouse.Name
        });
        await inv.SaveChangesAsync();

        var master = await fin.MasterProductLedgers.SingleAsync();
        master.SourceType.Should().Be("PROJECT");
        master.SourceName.Should().Be("Tower Block A");
        master.DestinationType.Should().Be("WAREHOUSE");
        master.DestinationName.Should().Be("Main Warehouse");
    }
}

// FSD Addendum 24 (ML-003) — same failure scenario as above, but exercised through a real business
// action (GRN posting) rather than calling CreateEntryAsync directly, proving the whole action
// (not just the ledger write) rolls back when the master product ledger write fails.
public class MasterProductLedger_GrnBusinessAction_Atomicity_Tests
{
    [Fact]
    public async Task Simulated_Master_Product_Ledger_Failure_Rolls_Back_The_Entire_GRN_Stock_Posting()
    {
        var invOpts = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var tenantContext = new StaticTenantContext();
        var inv = new InventoryDbContext(invOpts, tenantContext);

        var failingMaster = new Mock<IMasterProductLedgerService>();
        failingMaster
            .Setup(m => m.PostMovementAsync(It.IsAny<ProductMovementContext>(), It.IsAny<System.Data.Common.DbTransaction>()))
            .ThrowsAsync(new InvalidOperationException("Simulated master product ledger failure"));

        var ledger = new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance, failingMaster.Object);
        var (product, warehouse) = await Build.SeedAsync(inv);

        var poster = new SMS.Modules.Warehouse.Services.EfGrnInventoryPoster(inv, ledger);
        var grn = new SMS.Modules.Warehouse.Domain.Grn
        {
            UUID          = Guid.NewGuid(),
            GrnNumber     = "GRN-2026-00002",
            PoUuid        = Guid.NewGuid(),
            PoNumber      = "PO-001",
            SupplierId    = Guid.NewGuid(),
            SupplierName  = "Lenovo Distributors",
            WarehouseUuid = warehouse.Uuid,
            ReceivedAt    = DateTime.UtcNow,
            Status        = "PENDING_APPROVAL",
            ReceivedBy    = 1,
            CreatedBy     = 1,
            CreatedDate   = DateTime.UtcNow,
            Lines = new List<SMS.Modules.Warehouse.Domain.GrnLine>
            {
                new()
                {
                    UUID            = Guid.NewGuid(),
                    PoLineUuid      = Guid.NewGuid(),
                    ProductUuid     = product.Uuid,
                    LineNo          = 1,
                    ItemDescription = "Business Laptop",
                    UnitOfMeasure   = "EA",
                    QtyOrdered      = 100m,
                    QtyReceived     = 100m,
                    QtyAccepted     = 100m,
                    QtyRejected     = 0m,
                    UnitCost        = 500m
                }
            }
        };

        var act = async () => await poster.PostToInventoryAsync(grn, approvedBy: 99);
        await act.Should().ThrowAsync<InvalidOperationException>();

        // Fresh context against the same in-memory database — proves nothing was actually
        // persisted: no InventoryLedgerEntry, and the InventoryItem's QtyOnHand never moved.
        var verifyInv = new InventoryDbContext(invOpts, tenantContext);
        (await verifyInv.InventoryLedgerEntries.AnyAsync()).Should().BeFalse();
        var item = await verifyInv.InventoryItems.FirstOrDefaultAsync(i => i.ProductId == product.Id);
        (item is null || item.QtyOnHand == 0m).Should().BeTrue();
    }
}
