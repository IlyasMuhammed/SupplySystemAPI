using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Repositories;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

// PV-006 — ProductSearchIndex rebuild + barcode lookup. The actual CONTAINS/FREETEXT predicate
// in InventoryRepository.SearchProductsAsync has no InMemory-provider translation (it only runs
// against real SQL Server with Full-Text Search installed), so it isn't exercised here — these
// tests cover everything around it: index maintenance (RebuildForVariantAsync/RebuildAllAsync),
// the empty-query pagination path (which never calls EF.Functions.FreeText), tenant isolation,
// and the pre-existing exact-match barcode lookup PV-006 also specifies.
file static class SearchBuild
{
    internal static InventoryDbContext NewDb(ITenantContext? tenantContext = null) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenantContext ?? new StaticTenantContext());

    internal static InventoryRepository NewRepo(InventoryDbContext db) =>
        new(db, new Mock<IInventoryLedgerService>().Object);

    internal static ProductSearchIndexService NewSearchIndexService(InventoryDbContext db) => new(db);

    // Dell Latitude 5450, i7/16GB/512GB variant — matches the ticket's literal test data.
    internal static async Task<(Product product, ProductVariant variant)> SeedDellVariantAsync(
        InventoryDbContext db, string barcode = "8901234567892", bool variantActive = true)
    {
        var category = new ProductCategory { Name = "Laptop", Code = "LAPTOP", IsActive = true };
        db.ProductCategories.Add(category);
        await db.SaveChangesAsync();

        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = "PRD-DELL-5450", Name = "Dell Latitude 5450",
            CategoryId = category.Id, Status = "ACTIVE", IsActive = true, CreatedBy = 1
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = "DELL-5450-I7-16-512",
            VariantName = "i7 / 16GB / 512GB", Barcode = barcode, PurchasePrice = 135000m,
            IsDefault = false, IsActive = variantActive, CreatedDate = DateTime.UtcNow
        };
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync();

        return (product, variant);
    }

    internal static async Task<AttributeDefinition> SeedAttributeAsync(
        InventoryDbContext db, string name, string value, bool isSearchable)
    {
        var attr = new AttributeDefinition
        {
            Uuid = Guid.NewGuid(), AttributeName = name, DisplayName = name,
            DataType = "TEXT", ControlType = "TEXTBOX", IsSearchable = isSearchable, IsActive = true
        };
        db.AttributeDefinitions.Add(attr);
        await db.SaveChangesAsync();
        return attr;
    }

    internal static async Task LinkAttributeValueAsync(
        InventoryDbContext db, int variantId, int attributeId, string value)
    {
        db.VariantAttributeValues.Add(new VariantAttributeValue { VariantId = variantId, AttributeId = attributeId, Value = value });
        await db.SaveChangesAsync();
    }
}

public class ProductSearchIndexServiceTests
{
    [Fact]
    public async Task RebuildForVariantAsync_ComposesSearchableTextFromProductVariantSkuBarcodeAndSearchableAttributes()
    {
        var db = SearchBuild.NewDb();
        var (_, variant) = await SearchBuild.SeedDellVariantAsync(db);
        var cpu    = await SearchBuild.SeedAttributeAsync(db, "cpu", "Intel i7", isSearchable: true);
        var ram    = await SearchBuild.SeedAttributeAsync(db, "ram", "16GB", isSearchable: true);
        var color  = await SearchBuild.SeedAttributeAsync(db, "color", "Silver", isSearchable: true);
        await SearchBuild.LinkAttributeValueAsync(db, variant.Id, cpu.Id, "Intel i7");
        await SearchBuild.LinkAttributeValueAsync(db, variant.Id, ram.Id, "16GB");
        await SearchBuild.LinkAttributeValueAsync(db, variant.Id, color.Id, "Silver");
        var svc = SearchBuild.NewSearchIndexService(db);

        await svc.RebuildForVariantAsync(variant.Id);

        var entry = await db.ProductSearchIndexEntries.SingleAsync(x => x.VariantId == variant.Id);
        entry.ProductName.Should().Be("Dell Latitude 5450");
        entry.ProductCode.Should().Be("PRD-DELL-5450");
        entry.Sku.Should().Be("DELL-5450-I7-16-512");
        entry.Barcode.Should().Be("8901234567892");
        entry.VariantName.Should().Be("i7 / 16GB / 512GB");
        entry.CategoryName.Should().Be("Laptop");
        entry.IsActive.Should().BeTrue();
        // Concatenation of product name + variant name + sku + barcode + every searchable
        // attribute value — order matches ProductSearchIndexService.RebuildForVariantAsync.
        entry.SearchableText.Should().Be(
            "Dell Latitude 5450 i7 / 16GB / 512GB DELL-5450-I7-16-512 8901234567892 Intel i7 16GB Silver");
    }

    [Fact]
    public async Task RebuildForVariantAsync_ExcludesNonSearchableAttributeValues()
    {
        var db = SearchBuild.NewDb();
        var (_, variant) = await SearchBuild.SeedDellVariantAsync(db);
        var cpu = await SearchBuild.SeedAttributeAsync(db, "cpu", "Intel i7", isSearchable: true);
        var sku = await SearchBuild.SeedAttributeAsync(db, "warranty_code", "WX-INTERNAL-9", isSearchable: false);
        await SearchBuild.LinkAttributeValueAsync(db, variant.Id, cpu.Id, "Intel i7");
        await SearchBuild.LinkAttributeValueAsync(db, variant.Id, sku.Id, "WX-INTERNAL-9");
        var svc = SearchBuild.NewSearchIndexService(db);

        await svc.RebuildForVariantAsync(variant.Id);

        var entry = await db.ProductSearchIndexEntries.SingleAsync(x => x.VariantId == variant.Id);
        entry.SearchableText.Should().Contain("Intel i7");
        entry.SearchableText.Should().NotContain("WX-INTERNAL-9");
    }

    [Fact]
    public async Task RebuildForVariantAsync_CalledTwice_UpsertsSameRowRatherThanDuplicating()
    {
        var db = SearchBuild.NewDb();
        var (_, variant) = await SearchBuild.SeedDellVariantAsync(db);
        var color = await SearchBuild.SeedAttributeAsync(db, "color", "Silver", isSearchable: true);
        await SearchBuild.LinkAttributeValueAsync(db, variant.Id, color.Id, "Silver");
        var svc = SearchBuild.NewSearchIndexService(db);

        await svc.RebuildForVariantAsync(variant.Id);

        // "Updating a variant's color attribute from Silver to Black triggers Hangfire rebuild;
        // subsequent search for 'Black' returns the updated variant."
        var colorValue = await db.VariantAttributeValues.SingleAsync(v => v.VariantId == variant.Id && v.AttributeId == color.Id);
        colorValue.Value = "Black";
        await db.SaveChangesAsync();
        await svc.RebuildForVariantAsync(variant.Id);

        (await db.ProductSearchIndexEntries.CountAsync(x => x.VariantId == variant.Id)).Should().Be(1);
        var entry = await db.ProductSearchIndexEntries.SingleAsync(x => x.VariantId == variant.Id);
        entry.SearchableText.Should().Contain("Black");
        entry.SearchableText.Should().NotContain("Silver");
    }

    [Fact]
    public async Task RebuildForVariantAsync_UnknownVariantId_IsNoOp()
    {
        var db  = SearchBuild.NewDb();
        var svc = SearchBuild.NewSearchIndexService(db);

        await svc.RebuildForVariantAsync(999999);

        (await db.ProductSearchIndexEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RebuildAllAsync_RebuildsOnlyActiveVariants()
    {
        var db = SearchBuild.NewDb();
        var (_, active)   = await SearchBuild.SeedDellVariantAsync(db, barcode: "1111111111111");
        var (_, inactive) = await SearchBuild.SeedDellVariantAsync(db, barcode: "2222222222222", variantActive: false);
        var svc = SearchBuild.NewSearchIndexService(db);

        await svc.RebuildAllAsync();

        (await db.ProductSearchIndexEntries.AnyAsync(x => x.VariantId == active.Id)).Should().BeTrue();
        (await db.ProductSearchIndexEntries.AnyAsync(x => x.VariantId == inactive.Id)).Should().BeFalse();
    }
}

public class SearchProductsAsync_EmptyQueryTests
{
    [Fact]
    public async Task EmptyQuery_ReturnsPaginatedListOfActiveVariants_WithoutInvokingFreeText()
    {
        var db = SearchBuild.NewDb();
        var (_, dell)   = await SearchBuild.SeedDellVariantAsync(db, barcode: "1111111111111");
        var (_, hidden) = await SearchBuild.SeedDellVariantAsync(db, barcode: "2222222222222", variantActive: false);
        var indexSvc = SearchBuild.NewSearchIndexService(db);
        await indexSvc.RebuildForVariantAsync(dell.Id);
        await indexSvc.RebuildForVariantAsync(hidden.Id);
        var repo = SearchBuild.NewRepo(db);

        var result = await repo.SearchProductsAsync(new ProductSearchFilter { Query = null });

        result.Data.Should().ContainSingle(x => x.VariantUuid == dell.Uuid);
        result.Data.Should().NotContain(x => x.VariantUuid == hidden.Uuid);
        result.TotalRecords.Should().Be(1);
    }

    [Fact]
    public async Task Results_IncludeSearchableAttributeValues()
    {
        var db = SearchBuild.NewDb();
        var (_, variant) = await SearchBuild.SeedDellVariantAsync(db);
        var cpu = await SearchBuild.SeedAttributeAsync(db, "cpu", "Intel i7", isSearchable: true);
        await SearchBuild.LinkAttributeValueAsync(db, variant.Id, cpu.Id, "Intel i7");
        await SearchBuild.NewSearchIndexService(db).RebuildForVariantAsync(variant.Id);
        var repo = SearchBuild.NewRepo(db);

        var result = await repo.SearchProductsAsync(new ProductSearchFilter());

        var item = result.Data.Single();
        item.Attributes.Should().ContainSingle(a => a.AttributeName == "cpu" && a.Value == "Intel i7");
        item.ProductUuid.Should().NotBeEmpty();
        item.PurchasePrice.Should().Be(135000m);
    }

    [Fact]
    public async Task TenantIsolation_OrgA_Search_DoesNotIncludeOrgB_Products()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        // Same physical (InMemory) store, different tenant context per DbContext — mirrors how
        // both orgs share one real SQL Server database in production, isolated only by the
        // OrganizationId query filter.
        var sharedStoreName = Guid.NewGuid().ToString();

        var dbA = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(sharedStoreName).Options,
            new StaticTenantContext { OrganizationId = orgA });
        var (_, variantA) = await SearchBuild.SeedDellVariantAsync(dbA, barcode: "1111111111111");
        await SearchBuild.NewSearchIndexService(dbA).RebuildForVariantAsync(variantA.Id);

        var dbB = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(sharedStoreName).Options,
            new StaticTenantContext { OrganizationId = orgB });

        var repoB = SearchBuild.NewRepo(dbB);
        var resultB = await repoB.SearchProductsAsync(new ProductSearchFilter());

        resultB.Data.Should().BeEmpty();
    }
}

public class VariantBarcodeLookupTests
{
    [Fact]
    public async Task GetVariantByBarcodeAsync_KnownBarcode_ReturnsVariantWithProductInfo()
    {
        var db = SearchBuild.NewDb();
        var (product, variant) = await SearchBuild.SeedDellVariantAsync(db);
        var repo = SearchBuild.NewRepo(db);

        var result = await repo.GetVariantByBarcodeAsync("8901234567892");

        result.Should().NotBeNull();
        result!.Uuid.Should().Be(variant.Uuid);
        result.Sku.Should().Be("DELL-5450-I7-16-512");
        result.ProductId.Should().Be(product.Id);
        result.ProductName.Should().Be("Dell Latitude 5450");
    }

    [Fact]
    public async Task GetVariantByBarcodeAsync_UnknownBarcode_ReturnsNull()
    {
        var db = SearchBuild.NewDb();
        await SearchBuild.SeedDellVariantAsync(db, barcode: "1111111111111");
        var repo = SearchBuild.NewRepo(db);

        var result = await repo.GetVariantByBarcodeAsync("0000000000000");

        result.Should().BeNull();
    }
}
