using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Domain;
using Xunit;

namespace SMS.Modules.Inventory.Tests.Pos;

/// <summary>A38 D-6 (INV) — the POS catalog read side.</summary>
public class PosCatalogSourceTests
{
    private static async Task Tick() => await Task.Delay(15); // distinct ModifiedAt stamps

    [Fact]
    public async Task Changed_since_is_strictly_after_oldest_first_and_limited()
    {
        var h = new PosHarness();
        var a = await h.VariantAsync("A"); await Tick();
        var b = await h.VariantAsync("B"); await Tick();
        var c = await h.VariantAsync("C");

        var all = await h.Catalog.GetChangedAsync(h.Org, null, 500);
        all.Select(x => x.VariantUuid).Should().Equal(a.Uuid, b.Uuid, c.Uuid);

        var first = await h.Catalog.GetChangedAsync(h.Org, null, 2);
        first.Select(x => x.VariantUuid).Should().Equal(a.Uuid, b.Uuid);

        var rest = await h.Catalog.GetChangedAsync(h.Org, first[^1].ModifiedAt, 2);
        rest.Select(x => x.VariantUuid).Should().Equal(c.Uuid);

        (await h.Catalog.GetChangedAsync(h.Org, rest[^1].ModifiedAt, 2)).Should().BeEmpty();
        (await h.Catalog.GetChangedAsync(Guid.NewGuid(), null, 10)).Should().BeEmpty("another organization's catalog is absent");
    }

    [Fact]
    public async Task A_product_edit_brings_its_variants_back_and_inactive_ones_are_included()
    {
        var h = new PosHarness();
        var a = await h.VariantAsync("A"); await Tick();
        var b = await h.VariantAsync("B"); await Tick();
        var since = (await h.Catalog.GetChangedAsync(h.Org, null, 10))[^1].ModifiedAt;
        await Tick();

        var product = await h.Db.Products.SingleAsync(p => p.Id == a.ProductId);
        product.IsActive = false;
        await h.Db.SaveChangesAsync();

        var changed = await h.Catalog.GetChangedAsync(h.Org, since, 10);
        changed.Should().ContainSingle().Which.Should().BeEquivalentTo(new { VariantUuid = a.Uuid, IsActive = false });
        changed[0].ModifiedAt.Should().BeAfter(since);
    }

    [Fact]
    public async Task A_full_page_is_extended_with_rows_sharing_the_last_timestamp()
    {
        var h = new PosHarness();
        var product = new Product { Uuid = Guid.NewGuid(), Name = "Chips", Sku = "CHIPS", IsActive = true };
        h.Db.Products.Add(product);
        await h.Db.SaveChangesAsync();
        await Tick();
        for (var i = 0; i < 3; i++)
            h.Db.ProductVariants.Add(new ProductVariant { Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"CHIPS-{i}", VariantName = $"Flavour {i}", IsActive = true });
        await h.Db.SaveChangesAsync(); // one save → one ModifiedAt for all three

        var page = await h.Catalog.GetChangedAsync(h.Org, null, 2);

        page.Should().HaveCount(3, "resuming strictly after the shared timestamp would otherwise skip the third");
    }

    [Fact]
    public async Task Items_carry_price_category_flags_and_names()
    {
        var h = new PosHarness();
        var category = new ProductCategory { Name = "Drinks", Code = "DRK", IsActive = true };
        h.Db.ProductCategories.Add(category);
        h.Db.ProductCategories.Add(new ProductCategory { Name = "Archived", Code = "OLD", IsActive = false });
        await h.Db.SaveChangesAsync();

        var cola  = await h.VariantAsync("Cola", price: 2.5m, categoryId: category.Id);
        var water = await h.VariantAsync("Water", price: null);
        var product = await h.Db.Products.SingleAsync(p => p.Id == water.ProductId);
        product.IsSaleable = false;
        var variant = await h.Db.ProductVariants.SingleAsync(v => v.Id == water.Id);
        variant.IsAvailableForPos = false;
        variant.Barcode = "5000112637922";
        await h.Db.SaveChangesAsync();

        var items = await h.Catalog.GetByVariantUuidsAsync(h.Org, [cola.Uuid, water.Uuid]);

        items.Single(i => i.VariantUuid == cola.Uuid).Should().BeEquivalentTo(new
        {
            ProductName = "Cola", VariantName = (string?)null, Price = 2.5m, CategoryId = (int?)category.Id, CategoryName = "Drinks",
            UomCode = "EA", IsActive = true, IsSaleable = true, IsAvailableForPos = true
        });
        items.Single(i => i.VariantUuid == water.Uuid).Should().BeEquivalentTo(new
        {
            Price = 0m, Barcode = "5000112637922", IsSaleable = false, IsAvailableForPos = false, CategoryName = (string?)null
        });

        (await h.Catalog.GetByProductUuidsAsync(h.Org, [product.Uuid])).Should().ContainSingle(i => i.VariantUuid == water.Uuid);
        (await h.Catalog.GetCategoriesAsync(h.Org)).Should().Equal((category.Id, "Drinks"));
    }
}
