using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Repositories;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

file static class Build
{
    internal static (InventoryRepository repo, InventoryDbContext db) New()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());

        var ledger = new Mock<IInventoryLedgerService>().Object;
        return (new InventoryRepository(db, ledger), db);
    }
}

public class ProductVariantTests
{
    // PV-001 — "POST /api/products with Dell Latitude data creates Product row + 2 Variant rows
    // with correct FKs"
    [Fact]
    public async Task CreateProductAsync_WithExplicitVariants_CreatesProductAndAllVariants()
    {
        var (repo, db) = Build.New();

        var req = new CreateProductRequest
        {
            Sku      = "DELL-LAT-5450",
            Name     = "Dell Latitude 5450",
            Brand    = "Dell",
            UomCode  = "Piece",
            Variants =
            [
                new CreateProductVariantRequest
                {
                    Sku = "DELL-5450-I5-8-256", VariantName = "i5 / 8GB / 256GB",
                    PurchasePrice = 85000m, SellingPrice = 95000m, Barcode = "8901234567890", IsDefault = false
                },
                new CreateProductVariantRequest
                {
                    Sku = "DELL-5450-I7-16-512", VariantName = "i7 / 16GB / 512GB",
                    PurchasePrice = 135000m, SellingPrice = 155000m, Barcode = "8901234567892", IsDefault = true
                }
            ]
        };

        var (id, sku) = await repo.CreateProductAsync(req, userId: 1);

        sku.Should().Be("DELL-LAT-5450");
        var product = await db.Products.Include(p => p.Variants).FirstAsync(p => p.Id == id);
        product.Variants.Should().HaveCount(2);
        product.Variants.Should().OnlyContain(v => v.ProductId == product.Id);
        product.Variants.Should().ContainSingle(v => v.IsDefault);
        product.Variants.Should().Contain(v => v.Sku == "DELL-5450-I5-8-256" && v.PurchasePrice == 85000m);
        product.Variants.Should().Contain(v => v.Sku == "DELL-5450-I7-16-512" && v.IsDefault);
    }

    // PV-001 — "POST /api/products with Cement data (no explicit variants) auto-creates one
    // default variant"
    [Fact]
    public async Task CreateProductAsync_WithoutVariants_AutoCreatesSingleDefaultVariant()
    {
        var (repo, db) = Build.New();

        var req = new CreateProductRequest
        {
            Sku           = "CEMENT-OPC",
            Name          = "OPC Cement 50kg",
            PurchasePrice = 1200m
        };

        var (id, _) = await repo.CreateProductAsync(req, userId: 1);

        var product = await db.Products.Include(p => p.Variants).FirstAsync(p => p.Id == id);
        product.Variants.Should().HaveCount(1);
        var variant = product.Variants.Single();
        variant.Sku.Should().Be("CEMENT-OPC-DEFAULT");
        variant.VariantName.Should().Be("OPC Cement 50kg");
        variant.PurchasePrice.Should().Be(1200m);
        variant.IsDefault.Should().BeTrue();
    }

    // PV-001 — "Attempting to set is_default=false on ALL variants of a product returns 400
    // (at least one must be default)"
    [Fact]
    public async Task CreateProductAsync_NoVariantMarkedDefault_ThrowsBadRequest()
    {
        var (repo, _) = Build.New();

        var req = new CreateProductRequest
        {
            Name = "Test Product",
            Variants =
            [
                new CreateProductVariantRequest { Sku = "A", VariantName = "A", PurchasePrice = 10m, IsDefault = false },
                new CreateProductVariantRequest { Sku = "B", VariantName = "B", PurchasePrice = 20m, IsDefault = false }
            ]
        };

        var act = () => repo.CreateProductAsync(req, userId: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // Same rule, opposite direction — more than one is_default=true is equally invalid ("exactly
    // one variant per product must have is_default = true").
    [Fact]
    public async Task CreateProductAsync_MultipleVariantsMarkedDefault_ThrowsBadRequest()
    {
        var (repo, _) = Build.New();

        var req = new CreateProductRequest
        {
            Name = "Test Product",
            Variants =
            [
                new CreateProductVariantRequest { Sku = "A", VariantName = "A", PurchasePrice = 10m, IsDefault = true },
                new CreateProductVariantRequest { Sku = "B", VariantName = "B", PurchasePrice = 20m, IsDefault = true }
            ]
        };

        var act = () => repo.CreateProductAsync(req, userId: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // PV-001 — "SKU uniqueness: creating variant with sku='DELL-5450-I5-8-256' in same org twice
    // returns 409 Conflict"
    [Fact]
    public async Task CreateProductAsync_DuplicateVariantSku_ThrowsConflict()
    {
        var (repo, _) = Build.New();

        await repo.CreateProductAsync(new CreateProductRequest
        {
            Name = "Product One",
            Variants = [new CreateProductVariantRequest { Sku = "DUP-SKU", VariantName = "V1", PurchasePrice = 10m, IsDefault = true }]
        }, userId: 1);

        var act = () => repo.CreateProductAsync(new CreateProductRequest
        {
            Name = "Product Two",
            Variants = [new CreateProductVariantRequest { Sku = "DUP-SKU", VariantName = "V2", PurchasePrice = 20m, IsDefault = true }]
        }, userId: 1);

        await act.Should().ThrowAsync<ConflictException>();
    }

    // PV-001 — "Barcode uniqueness: two variants in same org with barcode='8901234567890'
    // returns 409"
    [Fact]
    public async Task CreateProductAsync_DuplicateVariantBarcode_ThrowsConflict()
    {
        var (repo, _) = Build.New();

        await repo.CreateProductAsync(new CreateProductRequest
        {
            Name = "Product One",
            Variants = [new CreateProductVariantRequest { Sku = "SKU-A", VariantName = "V1", PurchasePrice = 10m, Barcode = "8901234567890", IsDefault = true }]
        }, userId: 1);

        var act = () => repo.CreateProductAsync(new CreateProductRequest
        {
            Name = "Product Two",
            Variants = [new CreateProductVariantRequest { Sku = "SKU-B", VariantName = "V2", PurchasePrice = 20m, Barcode = "8901234567890", IsDefault = true }]
        }, userId: 1);

        await act.Should().ThrowAsync<ConflictException>();
    }

    // Two NULL barcodes must never collide (the unique index is filtered to non-null values).
    [Fact]
    public async Task CreateProductAsync_TwoVariantsWithNoBarcode_DoesNotConflict()
    {
        var (repo, _) = Build.New();

        await repo.CreateProductAsync(new CreateProductRequest
        {
            Name = "Product One",
            Variants = [new CreateProductVariantRequest { Sku = "SKU-A", VariantName = "V1", PurchasePrice = 10m, IsDefault = true }]
        }, userId: 1);

        var act = () => repo.CreateProductAsync(new CreateProductRequest
        {
            Name = "Product Two",
            Variants = [new CreateProductVariantRequest { Sku = "SKU-B", VariantName = "V2", PurchasePrice = 20m, IsDefault = true }]
        }, userId: 1);

        await act.Should().NotThrowAsync();
    }

    // No variants supplied AND no PurchasePrice — nothing to seed the auto-default variant with.
    [Fact]
    public async Task CreateProductAsync_NoVariantsAndNoPurchasePrice_ThrowsBadRequest()
    {
        var (repo, _) = Build.New();

        var act = () => repo.CreateProductAsync(new CreateProductRequest { Name = "No Price Product" }, userId: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }
}
