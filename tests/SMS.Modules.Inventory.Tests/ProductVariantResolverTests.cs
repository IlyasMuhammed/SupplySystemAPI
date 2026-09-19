using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

// A29-P6-02 — describing variants a document line references only by id, for modules that
// cannot see this DbContext (a sale-order delivery line needs a readable description).
public class ProductVariantResolverTests
{
    private static (ProductVariantResolver Resolver, InventoryDbContext Db, string DbName) New(Guid? org = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var db = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = org ?? Guid.NewGuid() });
        return (new ProductVariantResolver(db), db, dbName);
    }

    private static async Task<Product> SeedLaptop(InventoryDbContext db)
    {
        var product = new Product
        {
            Sku = "DELL-LAT-5450", Name = "Dell Latitude 5450", UomCode = "Piece", CreatedBy = 1,
            Variants =
            {
                new ProductVariant { Sku = "DELL-5450-I5", VariantName = "i5 / 8GB",  IsDefault = false, CreatedBy = 1 },
                new ProductVariant { Sku = "DELL-5450-I7", VariantName = "i7 / 16GB", IsDefault = true,  CreatedBy = 1 },
                new ProductVariant { Sku = "DELL-5450-OLD", VariantName = "i3 / 4GB", IsDefault = false, IsActive = false, CreatedBy = 1 }
            }
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return product;
    }

    [Fact]
    public async Task Describes_each_active_variant_with_its_product_sku_and_unit()
    {
        var (resolver, db, _) = New();
        var product = await SeedLaptop(db);
        var i5 = product.Variants.Single(v => v.Sku == "DELL-5450-I5");
        var i7 = product.Variants.Single(v => v.Sku == "DELL-5450-I7");

        var found = await resolver.DescribeVariantsAsync([i5.Uuid, i7.Uuid]);

        found.Should().HaveCount(2);
        found[i5.Uuid].Should().Be(new VariantDescription(
            i5.Uuid, product.Uuid, "DELL-5450-I5", "i5 / 8GB", "Dell Latitude 5450", false, "Piece"));
        found[i5.Uuid].DisplayName.Should().Be("Dell Latitude 5450 - i5 / 8GB (DELL-5450-I5)");
        found[i7.Uuid].DisplayName.Should().Be("Dell Latitude 5450 (DELL-5450-I7)",
            "the default variant reads as its product");
    }

    [Fact]
    public async Task Unknown_inactive_and_empty_ids_are_simply_absent()
    {
        var (resolver, db, _) = New();
        var product = await SeedLaptop(db);
        var retired = product.Variants.Single(v => !v.IsActive);

        var found = await resolver.DescribeVariantsAsync([retired.Uuid, Guid.NewGuid(), Guid.Empty]);

        found.Should().BeEmpty();
        (await resolver.DescribeVariantsAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_organizations_variants_are_invisible()
    {
        var (resolver, db, dbName) = New();
        var product = await SeedLaptop(db);

        var other = new ProductVariantResolver(new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid() }));

        (await other.DescribeVariantsAsync(product.Variants.Select(v => v.Uuid).ToList())).Should().BeEmpty();
        (await resolver.DescribeVariantsAsync(product.Variants.Select(v => v.Uuid).ToList())).Should().HaveCount(2);
    }
}
