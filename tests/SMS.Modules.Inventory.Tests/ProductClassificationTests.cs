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

// A30 §6 — product type, supply method and the manufacturing flags (A30-P1-01..05).
// Covers T-PB01, T-PB02 and the product side of T-CM01 (a finished good as a production input).
public class ProductClassificationTests
{
    private static (InventoryDbContext Db, InventoryRepository Repo) NewRepo()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        return (db, new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object));
    }

    private static CreateProductRequest Request(
        string name, string? type = null, string? method = null,
        bool? saleable = null, bool? purchasable = null, bool? stockable = null,
        List<CreateProductVariantRequest>? variants = null, int? warehouseId = null) => new()
    {
        Name = name,
        PurchasePrice = variants is null ? 1m : null,
        Variants = variants,
        ProductType = type,
        SupplyMethod = method,
        IsSaleable = saleable,
        IsPurchasable = purchasable,
        IsStockable = stockable,
        DefaultProductionWarehouseId = warehouseId
    };

    private static async Task<ProductDetailModel> CreateAsync(InventoryRepository repo, CreateProductRequest req)
    {
        var (id, _) = await repo.CreateProductAsync(req, 1);
        return (await repo.GetProductByIdAsync(id))!;
    }

    private static CreateProductVariantRequest ProductionInputVariant(bool isDefault = true) => new()
    {
        VariantName = "Default", PurchasePrice = 1m, IsDefault = isDefault, IsAvailableForProduction = true
    };

    // ── Defaults (§6.3, §6.6) ─────────────────────────────────────────────────

    [Fact]
    public async Task A_product_created_without_classification_is_a_purchased_stock_item()
    {
        var (_, repo) = NewRepo();

        var product = await CreateAsync(repo, Request("Bolt"));

        product.ProductType.Should().Be(ProductType.StockItem);
        product.SupplyMethod.Should().Be(SupplyMethod.Purchase);
        product.IsSaleable.Should().BeTrue();
        product.IsPurchasable.Should().BeTrue();
        product.IsStockable.Should().BeTrue();
        product.IsManufacturable.Should().BeFalse();
    }

    [Theory]
    [InlineData(ProductType.FinishedGood, SupplyMethod.Manufacture, true,  false, true,  true)]
    [InlineData(ProductType.SemiFinished, SupplyMethod.Manufacture, false, false, true,  true)]
    [InlineData(ProductType.RawMaterial,  SupplyMethod.Purchase,    false, true,  true,  false)]
    [InlineData(ProductType.Component,    SupplyMethod.Purchase,    false, true,  true,  false)]
    [InlineData(ProductType.Consumable,   SupplyMethod.Purchase,    false, true,  true,  false)]
    [InlineData(ProductType.Service,      SupplyMethod.Service,     true,  true,  false, false)]
    [InlineData(ProductType.Asset,        SupplyMethod.Purchase,    false, true,  false, false)]
    public async Task Flags_and_supply_method_default_from_the_product_type_table(
        string type, string expectedMethod, bool saleable, bool purchasable, bool stockable, bool manufacturable)
    {
        var (_, repo) = NewRepo();

        var product = await CreateAsync(repo, Request($"Item {type}", type));

        product.SupplyMethod.Should().Be(expectedMethod);
        product.IsSaleable.Should().Be(saleable);
        product.IsPurchasable.Should().Be(purchasable);
        product.IsStockable.Should().Be(stockable);
        product.IsManufacturable.Should().Be(manufacturable);
    }

    [Fact]
    public async Task A_default_can_be_overridden_within_the_valid_combinations()
    {
        // A finished good that can also be bought in — "dual supply" (§20.2).
        var (_, repo) = NewRepo();

        var product = await CreateAsync(repo, Request("Printed T-Shirt", ProductType.FinishedGood, purchasable: true));

        product.IsPurchasable.Should().BeTrue();
        product.SupplyMethod.Should().Be(SupplyMethod.Manufacture);
    }

    [Fact]
    public async Task Codes_are_accepted_in_any_case()
    {
        var (_, repo) = NewRepo();

        var product = await CreateAsync(repo, Request("Ink", "raw_material", " purchase "));

        product.ProductType.Should().Be(ProductType.RawMaterial);
        product.SupplyMethod.Should().Be(SupplyMethod.Purchase);
    }

    // ── Validation rules (§6.5) ───────────────────────────────────────────────

    [Fact]
    public async Task A_finished_good_must_be_manufactured()
    {
        var (_, repo) = NewRepo();

        var act = () => repo.CreateProductAsync(Request("Kit", ProductType.FinishedGood, SupplyMethod.Purchase), 1);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*must be MANUFACTURE*");
    }

    [Theory]
    [InlineData(ProductType.RawMaterial)]
    [InlineData(ProductType.StockItem)]
    [InlineData(ProductType.Consumable)]
    public async Task Only_semi_finished_and_finished_goods_can_be_manufactured(string type)
    {
        var (_, repo) = NewRepo();

        var act = () => repo.CreateProductAsync(Request("Thing", type, SupplyMethod.Manufacture), 1);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*cannot be manufactured*");
    }

    [Fact]
    public async Task A_service_cannot_be_stockable()
    {
        var (_, repo) = NewRepo();

        var act = () => repo.CreateProductAsync(Request("Washing", ProductType.Service, stockable: true), 1);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*cannot be stockable*");
    }

    [Fact]
    public async Task An_unknown_type_or_supply_method_is_refused()
    {
        var (_, repo) = NewRepo();

        var badType   = () => repo.CreateProductAsync(Request("X", "WIDGET"), 1);
        var badMethod = () => repo.CreateProductAsync(Request("X", ProductType.Component, "MAGIC"), 1);

        await badType.Should().ThrowAsync<BadRequestException>().WithMessage("*not a product type*");
        await badMethod.Should().ThrowAsync<BadRequestException>().WithMessage("*not a supply method*");
    }

    [Fact]
    public async Task A_default_production_warehouse_must_exist()
    {
        var (db, repo) = NewRepo();

        var missing = () => repo.CreateProductAsync(Request("Kit", ProductType.FinishedGood, warehouseId: 999), 1);
        await missing.Should().ThrowAsync<BadRequestException>().WithMessage("*Warehouse 999*");

        var plant = new Domain.Warehouse { Uuid = Guid.NewGuid(), Name = "Plant", Code = "PLANT", IsActive = true };
        db.Warehouses.Add(plant);
        await db.SaveChangesAsync();

        var product = await CreateAsync(repo, Request("Kit", ProductType.FinishedGood, warehouseId: plant.Id));

        product.DefaultProductionWarehouseId.Should().Be(plant.Id);
        product.DefaultProductionWarehouseName.Should().Be("Plant");
    }

    // ── Production input = the variant's IsAvailableForProduction (decision D2) ──

    [Fact]
    public async Task A_finished_good_variant_can_be_a_production_input()
    {
        // Chained manufacturing (§6.4.1): a bolt made here goes into an assembly kit made here.
        var (_, repo) = NewRepo();

        var bolt = await CreateAsync(repo, Request("Steel Bolt M10", ProductType.FinishedGood,
            variants: [ProductionInputVariant()]));

        bolt.SupplyMethod.Should().Be(SupplyMethod.Manufacture);
        bolt.Variants.Single().IsAvailableForProduction.Should().BeTrue();
    }

    [Theory]
    [InlineData(ProductType.StockItem)]
    [InlineData(ProductType.Asset)]
    public async Task A_variant_of_a_type_that_cannot_be_a_bom_input_cannot_be_available_for_production(string type)
    {
        var (_, repo) = NewRepo();

        var onCreate = () => repo.CreateProductAsync(Request("Laptop", type, variants: [ProductionInputVariant()]), 1);
        await onCreate.Should().ThrowAsync<BadRequestException>().WithMessage("*cannot be a production input*");

        var laptop = await CreateAsync(repo, Request("Laptop 2", type));

        var onAdd = () => repo.CreateVariantAsync(laptop.Id, ProductionInputVariant(isDefault: false), 1);
        await onAdd.Should().ThrowAsync<BadRequestException>().WithMessage("*cannot be a production input*");

        var existing = laptop.Variants.Single();
        var onUpdate = () => repo.UpdateVariantAsync(existing.Uuid, new CreateProductVariantRequest
        {
            VariantName = existing.VariantName, PurchasePrice = 1m, IsDefault = true, IsAvailableForProduction = true
        });
        await onUpdate.Should().ThrowAsync<BadRequestException>().WithMessage("*cannot be a production input*");
    }

    [Fact]
    public async Task A_product_cannot_be_reclassified_away_from_bom_input_while_a_variant_is_one()
    {
        var (_, repo) = NewRepo();
        var bolt = await CreateAsync(repo, Request("Steel Bolt M10", ProductType.FinishedGood,
            variants: [ProductionInputVariant()]));

        var act = () => repo.PatchProductAsync(bolt.Id, new PatchProductRequest
        {
            ProductType = ProductType.StockItem, SupplyMethod = SupplyMethod.Purchase
        });

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*while one of its variants is available for production*");
    }

    // ── PATCH overlays, manufacturing-config re-defaults ──────────────────────

    [Fact]
    public async Task Patch_overlays_only_what_was_sent_and_validates_the_result()
    {
        var (_, repo) = NewRepo();
        var product = await CreateAsync(repo, Request("Kit"));

        // Type alone: the supply method it keeps (PURCHASE) is not valid for a finished good.
        var typeOnly = () => repo.PatchProductAsync(product.Id, new PatchProductRequest { ProductType = ProductType.FinishedGood });
        await typeOnly.Should().ThrowAsync<BadRequestException>().WithMessage("*must be MANUFACTURE*");

        await repo.PatchProductAsync(product.Id, new PatchProductRequest
        {
            ProductType = ProductType.FinishedGood, SupplyMethod = SupplyMethod.Manufacture
        });

        var after = (await repo.GetProductByIdAsync(product.Id))!;
        after.ProductType.Should().Be(ProductType.FinishedGood);
        after.IsManufacturable.Should().BeTrue();
        after.IsPurchasable.Should().BeTrue("a plain patch keeps the flags it was not asked to change");
    }

    [Fact]
    public async Task Manufacturing_config_re_defaults_the_flags_from_the_new_type_unless_given()
    {
        var (_, repo) = NewRepo();
        var product = await CreateAsync(repo, Request("Kit"));

        var updated = await repo.SetManufacturingConfigAsync(product.Id, new ManufacturingConfigRequest
        {
            ProductType = ProductType.FinishedGood, SupplyMethod = SupplyMethod.Manufacture, LeadTimeDays = 5
        });

        updated.Should().BeTrue();
        var after = (await repo.GetProductByIdAsync(product.Id))!;
        after.IsPurchasable.Should().BeFalse("a finished good is not bought in unless someone says so");
        after.IsSaleable.Should().BeTrue();
        after.IsStockable.Should().BeTrue();
        after.IsManufacturable.Should().BeTrue();
        after.LeadTimeDays.Should().Be(5);

        await repo.SetManufacturingConfigAsync(product.Id, new ManufacturingConfigRequest
        {
            ProductType = ProductType.FinishedGood, SupplyMethod = SupplyMethod.Manufacture, IsPurchasable = true
        });
        (await repo.GetProductByIdAsync(product.Id))!.IsPurchasable.Should().BeTrue();
    }

    [Fact]
    public async Task Manufacturing_config_requires_both_codes_and_reports_a_missing_product()
    {
        var (_, repo) = NewRepo();
        var product = await CreateAsync(repo, Request("Kit"));

        var act = () => repo.SetManufacturingConfigAsync(product.Id, new ManufacturingConfigRequest { ProductType = ProductType.Component });
        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*both required*");

        (await repo.SetManufacturingConfigAsync(999, new ManufacturingConfigRequest
        {
            ProductType = ProductType.Component, SupplyMethod = SupplyMethod.Purchase
        })).Should().BeFalse();
    }

    // ── Listing (§28.1) ───────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_filters_by_product_type_and_supply_method_and_carries_both_codes()
    {
        var (_, repo) = NewRepo();
        await CreateAsync(repo, Request("Laptop"));
        await CreateAsync(repo, Request("Printed T-Shirt", ProductType.FinishedGood));
        await CreateAsync(repo, Request("Plain T-Shirt", ProductType.RawMaterial));

        var manufactured = await repo.GetProductsAsync(new ProductListFilter { SupplyMethod = SupplyMethod.Manufacture });
        manufactured.Data.Select(p => p.Name).Should().BeEquivalentTo(["Printed T-Shirt"]);
        manufactured.Data.Single().ProductType.Should().Be(ProductType.FinishedGood);
        manufactured.Data.Single().SupplyMethod.Should().Be(SupplyMethod.Manufacture);

        var rawMaterials = await repo.GetProductsAsync(new ProductListFilter { ProductType = ProductType.RawMaterial });
        rawMaterials.Data.Select(p => p.Name).Should().BeEquivalentTo(["Plain T-Shirt"]);

        var everything = await repo.GetProductsAsync(new ProductListFilter());
        everything.Data.Should().HaveCount(3);

        var unknown = () => repo.GetProductsAsync(new ProductListFilter { ProductType = "WIDGET" });
        await unknown.Should().ThrowAsync<BadRequestException>();
    }
}
