using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Repositories;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

// GET /api/products?availableFor=... — the server-side half of the "Available For" channel
// checkboxes: the product picker used by sale orders and MIRs only offers products that have at
// least one variant checked for the channel it is being used from.
public class ProductListAvailabilityFilterTests
{
    private static async Task<Product> SeedProductWithOneVariantAsync(
        InventoryDbContext db, string sku, bool retail = false, bool pos = false, bool mirMiv = false,
        bool production = false, bool services = false, bool variantActive = true)
    {
        var product = new Product { Uuid = Guid.NewGuid(), Sku = sku, Name = sku, Status = "ACTIVE", IsActive = true, CreatedBy = 1 };
        product.Variants.Add(new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = $"{sku}-1", VariantName = "Default", IsDefault = true, IsActive = variantActive,
            PurchasePrice = 1m, CreatedDate = DateTime.UtcNow,
            IsAvailableForRetail = retail, IsAvailableForPos = pos, IsAvailableForMirMiv = mirMiv,
            IsAvailableForProduction = production, IsAvailableForServices = services
        });
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    [Fact]
    public async Task WithNoFilter_EveryProductIsListedRegardlessOfChannels()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var repo = new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object);
        await SeedProductWithOneVariantAsync(db, "NONE-CHECKED");

        var result = await repo.GetProductsAsync(new ProductListFilter());

        result.Data.Should().ContainSingle(p => p.Sku == "NONE-CHECKED");
    }

    [Fact]
    public async Task RetailFilter_OnlyListsProductsWithAnActiveRetailEligibleVariant()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var repo = new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object);
        await SeedProductWithOneVariantAsync(db, "RETAIL-OK", retail: true);
        await SeedProductWithOneVariantAsync(db, "MIR-ONLY", mirMiv: true);
        await SeedProductWithOneVariantAsync(db, "NOTHING-CHECKED");

        var result = await repo.GetProductsAsync(new ProductListFilter { AvailableFor = VariantAvailabilityChannel.Retail });

        result.Data.Select(p => p.Sku).Should().BeEquivalentTo(["RETAIL-OK"]);
    }

    [Fact]
    public async Task MirMivFilter_OnlyListsProductsWithAnActiveMirMivEligibleVariant()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var repo = new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object);
        await SeedProductWithOneVariantAsync(db, "RETAIL-OK", retail: true);
        await SeedProductWithOneVariantAsync(db, "MIR-OK", mirMiv: true);

        var result = await repo.GetProductsAsync(new ProductListFilter { AvailableFor = VariantAvailabilityChannel.MirMiv });

        result.Data.Select(p => p.Sku).Should().BeEquivalentTo(["MIR-OK"]);
    }

    [Fact]
    public async Task AProductWithSeveralVariants_IsListedIfAnyOneOfThemMatchesTheChannel()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var repo = new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object);
        var product = await SeedProductWithOneVariantAsync(db, "MIXED", retail: false);
        product.Variants.Add(new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = "MIXED-2", VariantName = "Bulk", IsActive = true, PurchasePrice = 1m,
            CreatedDate = DateTime.UtcNow, IsAvailableForRetail = true
        });
        await db.SaveChangesAsync();

        var result = await repo.GetProductsAsync(new ProductListFilter { AvailableFor = VariantAvailabilityChannel.Retail });

        result.Data.Should().ContainSingle(p => p.Sku == "MIXED");
    }

    [Fact]
    public async Task AnInactiveEligibleVariant_DoesNotQualifyTheProduct()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var repo = new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object);
        await SeedProductWithOneVariantAsync(db, "INACTIVE-RETAIL", retail: true, variantActive: false);

        var result = await repo.GetProductsAsync(new ProductListFilter { AvailableFor = VariantAvailabilityChannel.Retail });

        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnrecognisedChannel_IsRefused()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var repo = new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object);

        var act = () => repo.GetProductsAsync(new ProductListFilter { AvailableFor = "NOT_A_CHANNEL" });

        await act.Should().ThrowAsync<BadRequestException>();
    }
}
