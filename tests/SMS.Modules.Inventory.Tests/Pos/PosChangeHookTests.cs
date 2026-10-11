using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Domain;
using Xunit;

namespace SMS.Modules.Inventory.Tests.Pos;

/// <summary>A38 C11 (INV) — InventoryDbContext tells the POS listener about price, product and stock changes after the save.</summary>
public class PosChangeHookTests
{
    [Fact]
    public async Task A_price_change_is_reported_with_old_and_new_price_once_it_is_saved()
    {
        var h = new PosHarness(withListener: true);
        var v = await h.VariantAsync(price: 2.5m);
        h.Listener.Clear();

        decimal? seenByListener = null;
        h.Listener.OnCall = async () =>
        {
            await using var other = h.NewContext();
            seenByListener = (await other.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == v.Id)).SellingPrice;
        };

        var tracked = await h.Db.ProductVariants.SingleAsync(x => x.Id == v.Id);
        tracked.SellingPrice = 3m;
        await h.Db.SaveChangesAsync();

        h.Listener.PriceCalls.Should().Equal((h.Org, v.Uuid, (decimal?)2.5m, (decimal?)3m));
        h.Listener.VariantCalls.Should().BeEmpty("a price-only edit is a price change, not a product update");
        seenByListener.Should().Be(3m, "the listener runs after the save");
    }

    [Fact]
    public async Task Variant_and_product_edits_report_the_variants()
    {
        var h = new PosHarness(withListener: true);
        var v = await h.VariantAsync();
        h.Listener.Clear();

        var variant = await h.Db.ProductVariants.SingleAsync(x => x.Id == v.Id);
        variant.IsActive = false;
        await h.Db.SaveChangesAsync();
        h.Listener.VariantCalls.Should().ContainSingle().Which.Variants.Should().Equal(v.Uuid);

        h.Listener.Clear();
        var product = await h.Db.Products.SingleAsync(x => x.Id == v.ProductId);
        product.Name = "Cola Zero";
        await h.Db.SaveChangesAsync();
        h.Listener.VariantCalls.Should().ContainSingle();
        h.Listener.VariantCalls[0].Org.Should().Be(h.Org);
        h.Listener.VariantCalls[0].Variants.Should().Equal(v.Uuid);
        h.Listener.PriceCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Stock_changes_are_reported_per_warehouse()
    {
        var h = new PosHarness(withListener: true);
        var store = await h.WarehouseAsync("Store");
        var other = await h.WarehouseAsync("Central");
        var a = await h.VariantAsync("A");
        var b = await h.VariantAsync("B");
        await h.RowAsync(a, store, 5m);
        await h.RowAsync(b, other, 5m);
        h.Listener.Clear();

        foreach (var row in await h.Db.InventoryItems.ToListAsync()) row.QtyReserved += 1m;
        await h.Db.SaveChangesAsync();

        h.Listener.StockCalls.Should().BeEquivalentTo(new[]
        {
            (h.Org, store.Uuid, (IReadOnlyCollection<Guid>)new[] { a.Uuid }),
            (h.Org, other.Uuid, (IReadOnlyCollection<Guid>)new[] { b.Uuid })
        });

        h.Listener.Clear();
        await h.Poster.PostAsync(h.Sale(store, Guid.NewGuid(), (a, 2m)));
        h.Listener.StockCalls.Should().NotBeEmpty().And.OnlyContain(c => c.Warehouse == store.Uuid && c.Variants.Single() == a.Uuid);
    }

    [Fact]
    public async Task Untouched_counters_report_nothing()
    {
        var h = new PosHarness(withListener: true);
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync();
        await h.RowAsync(v, w, 5m);
        h.Listener.Clear();

        var row = await h.Db.InventoryItems.SingleAsync();
        row.LastCountDate = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();

        h.Listener.Total.Should().Be(0);
    }

    [Fact]
    public async Task A_failing_listener_never_fails_the_save()
    {
        var h = new PosHarness(withListener: true);
        var v = await h.VariantAsync(price: 1m);
        h.Listener.Throw = true;

        var tracked = await h.Db.ProductVariants.SingleAsync(x => x.Id == v.Id);
        tracked.SellingPrice = 9m;
        tracked.VariantName = "Big";
        await h.Db.Invoking(db => db.SaveChangesAsync()).Should().NotThrowAsync();
        h.Db.Invoking(db => db.SaveChanges()).Should().NotThrow();

        await using var other = h.NewContext();
        (await other.ProductVariants.AsNoTracking().SingleAsync(x => x.Id == v.Id)).SellingPrice.Should().Be(9m);
        h.Listener.PriceCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_save_that_fails_reports_nothing()
    {
        var h = new PosHarness(withListener: true);
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync();
        var existing = await h.RowAsync(v, w, 5m);
        h.Listener.Clear();

        await using var db = h.NewContext();
        db.InventoryItems.Add(new InventoryItem { Id = existing.Id, Uuid = Guid.NewGuid(), VariantId = v.Id, WarehouseId = w.Id, QtyOnHand = 1m });

        await db.Invoking(d => d.SaveChangesAsync()).Should().ThrowAsync<Exception>();
        h.Listener.Total.Should().Be(0);
    }

    [Fact]
    public async Task Without_a_registered_listener_saves_work_as_before()
    {
        var h = new PosHarness(withListener: false);
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync();
        await h.RowAsync(v, w, 5m);

        await h.Poster.Invoking(p => p.PostAsync(h.Sale(w, Guid.NewGuid(), (v, 1m)))).Should().NotThrowAsync();
        h.Listener.Total.Should().Be(0);
    }
}
