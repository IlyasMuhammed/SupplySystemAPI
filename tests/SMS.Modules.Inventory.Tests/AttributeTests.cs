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

file static class AttrBuild
{
    internal static (InventoryRepository repo, InventoryDbContext db) New(ITenantContext? tenantContext = null)
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenantContext ?? new StaticTenantContext());

        var ledger = new Mock<IInventoryLedgerService>().Object;
        return (new InventoryRepository(db, ledger), db);
    }

    internal static async Task<int> SeedCategoryAsync(InventoryDbContext db, string name = "Laptop")
    {
        var category = new ProductCategory { Name = name, Code = name.ToUpperInvariant(), IsActive = true };
        db.ProductCategories.Add(category);
        await db.SaveChangesAsync();
        return category.Id;
    }

    internal static CreateAttributeDefinitionRequest CpuAttribute() => new()
    {
        AttributeName = "cpu",
        DisplayName   = "CPU",
        DataType      = "DROPDOWN",
        ControlType   = "DROPDOWN",
        DropdownOptions = ["Intel i3", "Intel i5", "Intel i7", "Intel i9", "AMD Ryzen 5", "AMD Ryzen 7"],
        IsRequired    = true,
        IsSearchable  = true
    };
}

public class AttributeTests
{
    // "POST /api/attributes creates attribute with correct data_type and dropdown_options"
    [Fact]
    public async Task CreateAttributeAsync_CreatesAttributeWithCorrectTypeAndOptions()
    {
        var (repo, db) = AttrBuild.New();

        var uuid = await repo.CreateAttributeAsync(AttrBuild.CpuAttribute());

        var saved = await db.AttributeDefinitions.SingleAsync(a => a.Uuid == uuid);
        saved.AttributeName.Should().Be("cpu");
        saved.DataType.Should().Be("DROPDOWN");
        saved.DropdownOptions.Should().Contain("Intel i7");
    }

    [Fact]
    public async Task CreateAttributeAsync_InvalidDataType_ThrowsBadRequest()
    {
        var (repo, _) = AttrBuild.New();
        var req = AttrBuild.CpuAttribute();
        req.DataType = "NOT_A_TYPE";

        var act = () => repo.CreateAttributeAsync(req);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task CreateAttributeAsync_DropdownWithoutOptions_ThrowsBadRequest()
    {
        var (repo, _) = AttrBuild.New();
        var req = AttrBuild.CpuAttribute();
        req.DropdownOptions = null;

        var act = () => repo.CreateAttributeAsync(req);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task CreateAttributeAsync_DuplicateNameInSameOrg_ThrowsConflict()
    {
        var (repo, _) = AttrBuild.New();
        await repo.CreateAttributeAsync(AttrBuild.CpuAttribute());

        var act = () => repo.CreateAttributeAsync(AttrBuild.CpuAttribute());

        await act.Should().ThrowAsync<ConflictException>();
    }

    // "POST /api/categories/{laptopId}/attributes links cpu attribute to Laptop category with
    // display_order=1, is_required=true"
    [Fact]
    public async Task LinkCategoryAttributeAsync_LinksWithDisplayOrderAndRequiredOverride()
    {
        var (repo, db) = AttrBuild.New();
        var laptopId = await AttrBuild.SeedCategoryAsync(db, "Laptop");
        var cpuUuid  = await repo.CreateAttributeAsync(AttrBuild.CpuAttribute());

        await repo.LinkCategoryAttributeAsync(laptopId, new CreateCategoryAttributeRequest
        {
            AttributeUuid = cpuUuid, IsRequired = true, DisplayOrder = 1
        });

        var links = await repo.GetCategoryAttributesAsync(laptopId);
        links.Should().ContainSingle();
        links[0].AttributeName.Should().Be("cpu");
        links[0].DisplayOrder.Should().Be(1);
        links[0].IsRequired.Should().BeTrue();
    }

    // "GET /api/categories/{laptopId}/attributes returns 6 attributes in correct display_order"
    [Fact]
    public async Task GetCategoryAttributesAsync_ReturnsAllSixInDisplayOrder()
    {
        var (repo, db) = AttrBuild.New();
        var laptopId = await AttrBuild.SeedCategoryAsync(db, "Laptop");

        var specs = new (string Name, string Type, string Control, string[]? Options)[]
        {
            ("cpu",         "DROPDOWN", "DROPDOWN",  ["Intel i5", "Intel i7"]),
            ("ram",         "DROPDOWN", "DROPDOWN",  ["8GB", "16GB"]),
            ("storage",     "DROPDOWN", "DROPDOWN",  ["256GB SSD", "512GB SSD"]),
            ("screen_size", "DECIMAL",  "NUMBERBOX", null),
            ("color",       "DROPDOWN", "DROPDOWN",  ["Silver", "Black"]),
            ("gpu",         "TEXT",     "TEXTBOX",   null)
        };

        var order = 1;
        foreach (var (name, type, control, options) in specs)
        {
            var uuid = await repo.CreateAttributeAsync(new CreateAttributeDefinitionRequest
            {
                AttributeName = name, DisplayName = name, DataType = type, ControlType = control,
                DropdownOptions = options is null ? null : [.. options]
            });
            await repo.LinkCategoryAttributeAsync(laptopId, new CreateCategoryAttributeRequest
            {
                AttributeUuid = uuid, DisplayOrder = order++
            });
        }

        var result = await repo.GetCategoryAttributesAsync(laptopId);

        result.Should().HaveCount(6);
        result.Select(r => r.AttributeName).Should().ContainInOrder("cpu", "ram", "storage", "screen_size", "color", "gpu");
    }

    // "Submitting variant value 'Intel i99' for cpu (DROPDOWN) returns 400 - value not in options"
    [Fact]
    public async Task SetVariantAttributeValuesAsync_DropdownValueNotInOptions_ThrowsBadRequest()
    {
        var (repo, db) = AttrBuild.New();
        var cpuUuid = await repo.CreateAttributeAsync(AttrBuild.CpuAttribute());
        var variant = await SeedVariantAsync(db);

        var act = () => repo.SetVariantAttributeValuesAsync(variant.Uuid, new SetVariantAttributeValuesRequest
        {
            Values = [new VariantAttributeValueInput { AttributeUuid = cpuUuid, Value = "Intel i99" }]
        });

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // "Submitting variant value 'abc' for screen_size (DECIMAL) returns 400 - not a valid decimal"
    [Fact]
    public async Task SetVariantAttributeValuesAsync_InvalidDecimal_ThrowsBadRequest()
    {
        var (repo, db) = AttrBuild.New();
        var screenSizeUuid = await repo.CreateAttributeAsync(new CreateAttributeDefinitionRequest
        {
            AttributeName = "screen_size", DisplayName = "Screen Size", DataType = "DECIMAL", ControlType = "NUMBERBOX"
        });
        var variant = await SeedVariantAsync(db);

        var act = () => repo.SetVariantAttributeValuesAsync(variant.Uuid, new SetVariantAttributeValuesRequest
        {
            Values = [new VariantAttributeValueInput { AttributeUuid = screenSizeUuid, Value = "abc" }]
        });

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // "TEST DATA - Seed VariantAttributeValues for Dell i7 variant: cpu='Intel i7', ram='16GB',
    // storage='512GB SSD', color='Silver', screen_size='15.6'"
    [Fact]
    public async Task SetVariantAttributeValuesAsync_ValidValues_PersistsAndRoundTrips()
    {
        var (repo, db) = AttrBuild.New();
        var cpuUuid    = await repo.CreateAttributeAsync(AttrBuild.CpuAttribute());
        var screenUuid = await repo.CreateAttributeAsync(new CreateAttributeDefinitionRequest
        {
            AttributeName = "screen_size", DisplayName = "Screen Size", DataType = "DECIMAL", ControlType = "NUMBERBOX"
        });
        var variant = await SeedVariantAsync(db);

        var updated = await repo.SetVariantAttributeValuesAsync(variant.Uuid, new SetVariantAttributeValuesRequest
        {
            Values =
            [
                new VariantAttributeValueInput { AttributeUuid = cpuUuid, Value = "Intel i7" },
                new VariantAttributeValueInput { AttributeUuid = screenUuid, Value = "15.6" }
            ]
        });

        updated.Should().Be(variant.Id);
        var values = await repo.GetVariantAttributeValuesAsync(variant.Uuid);
        values.Should().Contain(v => v.AttributeName == "cpu" && v.Value == "Intel i7");
        values.Should().Contain(v => v.AttributeName == "screen_size" && v.Value == "15.6");
    }

    // Configure Attributes admin screen's Save button — full-replace in one call.
    [Fact]
    public async Task SetCategoryAttributesAsync_ReplacesEntireLinkSet()
    {
        var (repo, db) = AttrBuild.New();
        var laptopId = await AttrBuild.SeedCategoryAsync(db, "Laptop");
        var cpuUuid  = await repo.CreateAttributeAsync(AttrBuild.CpuAttribute());
        var ramUuid  = await repo.CreateAttributeAsync(new CreateAttributeDefinitionRequest
        {
            AttributeName = "ram", DisplayName = "RAM", DataType = "DROPDOWN", ControlType = "DROPDOWN",
            DropdownOptions = ["8GB", "16GB"]
        });

        // Initial link: just cpu.
        await repo.LinkCategoryAttributeAsync(laptopId, new CreateCategoryAttributeRequest { AttributeUuid = cpuUuid, DisplayOrder = 1 });

        // Save replaces it with [ram(order 1, required), cpu(order 2, required)] — cpu re-ordered, ram added.
        var result = await repo.SetCategoryAttributesAsync(laptopId, new SetCategoryAttributesRequest
        {
            Attributes =
            [
                new CategoryAttributeOrderItem { AttributeUuid = ramUuid, IsRequired = true, DisplayOrder = 1 },
                new CategoryAttributeOrderItem { AttributeUuid = cpuUuid, IsRequired = true, DisplayOrder = 2 }
            ]
        });

        result.Should().HaveCount(2);
        result.Select(r => r.AttributeName).Should().ContainInOrder("ram", "cpu");
        result.Should().OnlyContain(r => r.IsRequired);
    }

    [Fact]
    public async Task SetCategoryAttributesAsync_EmptyList_UnlinksEverything()
    {
        var (repo, db) = AttrBuild.New();
        var laptopId = await AttrBuild.SeedCategoryAsync(db, "Laptop");
        var cpuUuid  = await repo.CreateAttributeAsync(AttrBuild.CpuAttribute());
        await repo.LinkCategoryAttributeAsync(laptopId, new CreateCategoryAttributeRequest { AttributeUuid = cpuUuid, DisplayOrder = 1 });

        var result = await repo.SetCategoryAttributesAsync(laptopId, new SetCategoryAttributesRequest { Attributes = [] });

        result.Should().BeEmpty();
    }

    // "Attribute in Org A is invisible to Org B (tenant isolation verified)"
    [Fact]
    public async Task Attribute_CreatedInOrgA_IsInvisibleToOrgB()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var orgB = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        var dbA = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(dbName).Options, orgA);
        var realRepoA = new InventoryRepository(dbA, new Mock<IInventoryLedgerService>().Object);
        await realRepoA.CreateAttributeAsync(AttrBuild.CpuAttribute());

        var dbB = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(dbName).Options, orgB);
        var realRepoB = new InventoryRepository(dbB, new Mock<IInventoryLedgerService>().Object);

        var orgBAttributes = await realRepoB.GetAttributesAsync();

        orgBAttributes.Should().BeEmpty();
    }

    private static async Task<ProductVariant> SeedVariantAsync(InventoryDbContext db)
    {
        var product = new Product { Uuid = Guid.NewGuid(), Sku = "SKU-TEST", Name = "Test Product", Status = "ACTIVE", IsActive = true, CreatedBy = 1 };
        product.Variants.Add(new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = "SKU-TEST-DEFAULT", VariantName = "Test Product",
            PurchasePrice = 100m, IsDefault = true, IsActive = true, CreatedDate = DateTime.UtcNow
        });
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Variants.Single();
    }
}
