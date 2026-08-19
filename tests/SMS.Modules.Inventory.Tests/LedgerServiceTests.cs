using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Warehouse.Domain;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Common;
using InventoryWarehouse = SMS.Modules.Inventory.Domain.Warehouse;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

// ── Test infrastructure ───────────────────────────────────────────────────────

file static class LedgerBuild
{
    internal static (InventoryLedgerService service, InventoryDbContext db) New()
    {
        var opts = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db      = new InventoryDbContext(opts, new StaticTenantContext());
        var logger  = NullLogger<InventoryLedgerService>.Instance;
        var service = new InventoryLedgerService(db, logger);
        return (service, db);
    }

    internal static async Task<(ProductVariant variant, InventoryWarehouse warehouse)> SeedAsync(InventoryDbContext db)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = "TEST-001", Name = "Test Product",
            Status = "ACTIVE", IsActive = true, CreatedBy = 1
        };
        var warehouse = new InventoryWarehouse
        {
            Uuid = Guid.NewGuid(), Code = "WH1", Name = "Test Warehouse",
            IsActive = true, CreatedBy = 1
        };
        db.Products.Add(product);
        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();

        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = "TEST-001-DEFAULT",
            VariantName = "Default", PurchasePrice = 0m, IsDefault = true, IsActive = true, CreatedBy = 1
        };
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync();
        return (variant, warehouse);
    }

    internal static async Task<ProductVariant> SeedOtherVariantAsync(InventoryDbContext db)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = "OTHER-001", Name = "Other Product",
            Status = "ACTIVE", IsActive = true, CreatedBy = 1
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = "OTHER-001-DEFAULT",
            VariantName = "Default", PurchasePrice = 0m, IsDefault = true, IsActive = true, CreatedBy = 1
        };
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync();
        return variant;
    }
}

// ── INV-LED-TC-1: QuantityIn → balance incremented ───────────────────────────

public class CreateEntry_QuantityIn_Tests
{
    [Fact]
    public async Task CreateEntry_WithQuantityIn_BalanceIsIncremented()
    {
        var (service, db) = LedgerBuild.New();
        var (variant, warehouse) = await LedgerBuild.SeedAsync(db);

        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId       = variant.Id,
            WarehouseId     = warehouse.Id,
            TransactionType = "GRN_RECEIPT",
            ReferenceType   = "GRN",
            ReferenceId     = Guid.NewGuid(),
            ReferenceNumber = "GRN-2026-00001",
            QuantityIn      = 10m,
            UnitCost        = 100m,
            CreatedBy       = 1
        });
        await db.SaveChangesAsync();

        var balance = await service.GetCurrentBalanceAsync(variant.Id, warehouse.Id);
        balance.Should().Be(10m);
    }
}

// ── INV-LED-TC-2: QuantityOut → balance decremented ──────────────────────────

public class CreateEntry_QuantityOut_Tests
{
    [Fact]
    public async Task CreateEntry_WithQuantityOut_BalanceIsDecremented()
    {
        var (service, db) = LedgerBuild.New();
        var (variant, warehouse) = await LedgerBuild.SeedAsync(db);

        // Seed initial balance of 20 directly
        db.InventoryLedgerEntries.Add(new InventoryLedgerEntry
        {
            LedgerId        = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = warehouse.Id,
            TransactionDate = DateTime.UtcNow, TransactionType = "GRN_RECEIPT",
            ReferenceType   = "GRN", ReferenceId = Guid.NewGuid(), ReferenceNumber = "GRN-2026-00001",
            QuantityIn      = 20m, BalanceAfter = 20m, UnitCost = 50m, TransactionValue = 1000m,
            CreatedBy       = 1,  CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId       = variant.Id,
            WarehouseId     = warehouse.Id,
            TransactionType = "RETURN_DISPATCH",
            ReferenceType   = "SRO",
            ReferenceId     = Guid.NewGuid(),
            ReferenceNumber = "SRO-2026-00001",
            QuantityOut     = 5m,
            UnitCost        = 50m,
            CreatedBy       = 1
        });
        await db.SaveChangesAsync();

        var balance = await service.GetCurrentBalanceAsync(variant.Id, warehouse.Id);
        balance.Should().Be(15m);
    }
}

// ── INV-LED-TC-3: Both QuantityIn and QuantityOut → ArgumentException ─────────

public class CreateEntry_BothQty_Tests
{
    [Fact]
    public async Task CreateEntry_WithBothQuantities_ThrowsArgumentException()
    {
        var (service, db) = LedgerBuild.New();
        var (variant, warehouse) = await LedgerBuild.SeedAsync(db);

        var act = () => service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId = variant.Id, WarehouseId = warehouse.Id,
            TransactionType = "TEST", ReferenceType = "TEST",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "TEST",
            QuantityIn = 10m, QuantityOut = 5m, UnitCost = 1m, CreatedBy = 1
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

// ── INV-LED-TC-4: Neither QuantityIn nor QuantityOut → ArgumentException ─────

public class CreateEntry_NoQty_Tests
{
    [Fact]
    public async Task CreateEntry_WithNoQuantity_ThrowsArgumentException()
    {
        var (service, db) = LedgerBuild.New();
        var (variant, warehouse) = await LedgerBuild.SeedAsync(db);

        var act = () => service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId = variant.Id, WarehouseId = warehouse.Id,
            TransactionType = "TEST", ReferenceType = "TEST",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "TEST",
            UnitCost = 1m, CreatedBy = 1
        });

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

// ── INV-LED-TC-5: GetCurrentBalance returns 0 when no entries ─────────────────

public class GetCurrentBalance_NoEntries_Tests
{
    [Fact]
    public async Task GetCurrentBalance_ReturnsZero_WhenNoEntries()
    {
        var (service, db) = LedgerBuild.New();
        var (variant, warehouse) = await LedgerBuild.SeedAsync(db);

        var balance = await service.GetCurrentBalanceAsync(variant.Id, warehouse.Id);
        balance.Should().Be(0m);
    }
}

// ── INV-LED-TC-6: GetCurrentBalance returns correct product+warehouse balance ─

public class GetCurrentBalance_CorrectScope_Tests
{
    [Fact]
    public async Task GetCurrentBalance_ReturnsLatestBalance_ForCorrectProductWarehouse()
    {
        var (service, db) = LedgerBuild.New();
        var (variant, warehouse) = await LedgerBuild.SeedAsync(db);

        var otherVariant = await LedgerBuild.SeedOtherVariantAsync(db);

        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId = variant.Id, WarehouseId = warehouse.Id,
            TransactionType = "GRN_RECEIPT", ReferenceType = "GRN",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "GRN-001",
            QuantityIn = 50m, UnitCost = 10m, CreatedBy = 1
        });
        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId = otherVariant.Id, WarehouseId = warehouse.Id,
            TransactionType = "GRN_RECEIPT", ReferenceType = "GRN",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "GRN-001",
            QuantityIn = 200m, UnitCost = 5m, CreatedBy = 1
        });
        await db.SaveChangesAsync();

        var balance = await service.GetCurrentBalanceAsync(variant.Id, warehouse.Id);
        balance.Should().Be(50m);
    }
}

// ── INV-LED-TC-7: Sequential entries maintain running balance ─────────────────

public class SequentialEntries_Tests
{
    [Fact]
    public async Task SequentialEntries_MaintainRunningBalance()
    {
        var (service, db) = LedgerBuild.New();
        var (variant, warehouse) = await LedgerBuild.SeedAsync(db);

        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId = variant.Id, WarehouseId = warehouse.Id,
            TransactionType = "GRN_RECEIPT", ReferenceType = "GRN",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "GRN-001",
            QuantityIn = 100m, UnitCost = 10m, CreatedBy = 1
        });
        await db.SaveChangesAsync();

        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId = variant.Id, WarehouseId = warehouse.Id,
            TransactionType = "RETURN_DISPATCH", ReferenceType = "SRO",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "SRO-001",
            QuantityOut = 30m, UnitCost = 10m, CreatedBy = 1
        });
        await db.SaveChangesAsync();

        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId = variant.Id, WarehouseId = warehouse.Id,
            TransactionType = "GRN_RECEIPT", ReferenceType = "GRN",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "GRN-002",
            QuantityIn = 50m, UnitCost = 10m, CreatedBy = 1
        });
        await db.SaveChangesAsync();

        var balance = await service.GetCurrentBalanceAsync(variant.Id, warehouse.Id);
        balance.Should().Be(120m); // 100 - 30 + 50
    }
}

// ── INV-LED-TC-8: GRN approval integration → GRN_RECEIPT ledger entry ─────────

public class GrnReceipt_Integration_Tests
{
    [Fact]
    public async Task PostToInventory_CreatesGrnReceiptLedgerEntry()
    {
        var productUuid   = Guid.NewGuid();
        var variantUuid   = Guid.NewGuid();
        var warehouseUuid = Guid.NewGuid();

        var invOpts = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var inv    = new InventoryDbContext(invOpts, new StaticTenantContext());
        var ledger = new InventoryLedgerService(inv, NullLogger<InventoryLedgerService>.Instance);

        var product = new Product
        {
            Uuid = productUuid, Sku = "PROD-001", Name = "Test Product",
            Status = "ACTIVE", IsActive = true, CreatedBy = 1
        };
        inv.Products.Add(product);
        inv.Warehouses.Add(new InventoryWarehouse
        {
            Uuid = warehouseUuid, Code = "WH1", Name = "Main WH",
            IsActive = true, CreatedBy = 1
        });
        await inv.SaveChangesAsync();

        inv.ProductVariants.Add(new ProductVariant
        {
            Uuid = variantUuid, ProductId = product.Id, Sku = "PROD-001-DEFAULT",
            VariantName = "Default", PurchasePrice = 10m, IsDefault = true, IsActive = true, CreatedBy = 1
        });
        await inv.SaveChangesAsync();

        var poster = new EfGrnInventoryPoster(inv, ledger);
        var grn = new Grn
        {
            UUID          = Guid.NewGuid(),
            GrnNumber     = "GRN-2026-00001",
            PoUuid        = Guid.NewGuid(),
            PoNumber      = "PO-001",
            SupplierId    = Guid.NewGuid(),
            SupplierName  = "Test Supplier",
            WarehouseUuid = warehouseUuid,
            ReceivedAt    = DateTime.UtcNow,
            Status        = "PENDING_APPROVAL",
            ReceivedBy    = 1,
            CreatedBy     = 1,
            CreatedDate   = DateTime.UtcNow,
            Lines         = new List<GrnLine>
            {
                new()
                {
                    UUID            = Guid.NewGuid(),
                    PoLineUuid      = Guid.NewGuid(),
                    VariantUuid     = variantUuid,
                    LineNo          = 1,
                    ItemDescription = "Test Product",
                    UnitOfMeasure   = "EA",
                    QtyOrdered      = 10m,
                    QtyReceived     = 10m,
                    QtyAccepted     = 10m,
                    QtyRejected     = 0m,
                    UnitCost        = 100m
                }
            }
        };

        await poster.PostToInventoryAsync(grn, approvedBy: 99);

        var entry = await inv.InventoryLedgerEntries.FirstOrDefaultAsync();
        entry.Should().NotBeNull();
        entry!.TransactionType.Should().Be("GRN_RECEIPT");
        entry.QuantityIn.Should().Be(10m);
        entry.BalanceAfter.Should().Be(10m);
        entry.CreatedBy.Should().Be(99);
    }
}

// ── INV-LED-TC-9: Negative stock adjustment → STOCK_ADJUSTMENT with QuantityOut

public class StockAdjustment_Integration_Tests
{
    [Fact]
    public async Task NegativeAdjustment_CreatesStockAdjustmentEntry_WithQuantityOut()
    {
        var (service, db) = LedgerBuild.New();
        var (variant, warehouse) = await LedgerBuild.SeedAsync(db);

        // Create initial inventory balance via service
        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId = variant.Id, WarehouseId = warehouse.Id,
            TransactionType = "GRN_RECEIPT", ReferenceType = "GRN",
            ReferenceId = Guid.NewGuid(), ReferenceNumber = "GRN-001",
            QuantityIn = 100m, UnitCost = 10m, CreatedBy = 1
        });
        await db.SaveChangesAsync();

        // Negative adjustment: 100 - 15 = 85
        await service.CreateEntryAsync(new LedgerEntryCommand
        {
            VariantId       = variant.Id,
            WarehouseId     = warehouse.Id,
            TransactionType = "STOCK_ADJUSTMENT",
            ReferenceType   = "ADJUSTMENT",
            ReferenceId     = Guid.NewGuid(),
            ReferenceNumber = "ADJ-2026-00001",
            QuantityOut     = 15m,
            UnitCost        = 10m,
            Notes           = "Damaged stock write-off",
            CreatedBy       = 2
        });
        await db.SaveChangesAsync();

        var adjustmentEntry = await db.InventoryLedgerEntries
            .Where(e => e.TransactionType == "STOCK_ADJUSTMENT")
            .FirstOrDefaultAsync();

        adjustmentEntry.Should().NotBeNull();
        adjustmentEntry!.QuantityOut.Should().Be(15m);
        adjustmentEntry.QuantityIn.Should().BeNull();
        adjustmentEntry.BalanceAfter.Should().Be(85m);
        adjustmentEntry.Notes.Should().Be("Damaged stock write-off");
    }
}
