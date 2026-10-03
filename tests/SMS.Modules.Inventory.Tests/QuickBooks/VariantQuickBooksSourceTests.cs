using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Integration;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Inventory.Tests.QuickBooks;

/// <summary>Records each variant EF materializes, into the same list the gateway writes its calls to.</summary>
internal sealed class VariantMaterializationRecorder : IMaterializationInterceptor
{
    private readonly List<string> _events;
    public VariantMaterializationRecorder(List<string> events) => _events = events;

    public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
    {
        if (entity is ProductVariant v) _events.Add($"load:{v.Uuid}");
        return entity;
    }
}

internal sealed class VariantRig
{
    public required InventoryDbContext                   Db;
    public required RecordingQuickBooksGateway           Gateway;
    public required ListLogger<VariantQuickBooksSource>  Log;
    public required VariantQuickBooksSource              Source;

    internal static VariantRig New(IInterceptor? interceptor = null, string? dbName = null, Guid? orgId = null)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString());
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var db = new InventoryDbContext(options.Options, new StaticTenantContext { OrganizationId = orgId ?? Guid.NewGuid() });
        var gateway = new RecordingQuickBooksGateway();
        var log = new ListLogger<VariantQuickBooksSource>();
        return new VariantRig { Db = db, Gateway = gateway, Log = log, Source = new VariantQuickBooksSource(db, gateway, log) };
    }

    /// <summary>A product and its variants, as (name, active) pairs.</summary>
    internal Product Product(
        string name, bool active = true, DateTime? created = null, DateTime? updated = null,
        DateTime? variantsCreated = null, params (string Name, bool Active)[] variants)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = name.ToUpperInvariant().Replace(' ', '-'), Name = name, IsActive = active,
            Status = active ? "ACTIVE" : "INACTIVE", CreatedBy = 1,
            CreatedDate = created ?? new DateTime(2026, 1, 1), UpdatedDate = updated
        };
        var list = variants.Length > 0 ? variants : [(name, true)];
        for (var i = 0; i < list.Length; i++)
            product.Variants.Add(new ProductVariant
            {
                Uuid = Guid.NewGuid(), Sku = $"{product.Sku}-{i + 1}", VariantName = list[i].Name,
                PurchasePrice = 10m + i, SellingPrice = 20m + i, IsActive = list[i].Active, IsDefault = i == 0,
                CreatedBy = 1, CreatedDate = variantsCreated ?? new DateTime(2026, 1, 1)
            });
        Db.Products.Add(product);
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
        return product;
    }
}

public class VariantQuickBooksSourceTests
{
    [Fact]
    public async Task It_serves_items_and_nothing_else()
    {
        var rig = VariantRig.New();

        rig.Source.Kinds.Should().Equal(SyncKind.Item);
        await rig.Invoking(r => r.Source.PushAsync(SyncKind.Customer, [Guid.NewGuid().ToString()])).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await rig.Invoking(r => r.Source.PushAllAsync(SyncKind.Bill, null)).Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PushAsync_sends_the_asked_for_variants_named_by_how_many_their_product_has()
    {
        var rig = VariantRig.New();
        var laptop = rig.Product("Laptop", variants: [("i5 / 8GB", true), ("i7 / 16GB", true)]);
        var cement = rig.Product("Cement");
        var i7 = laptop.Variants.Single(v => v.VariantName == "i7 / 16GB");
        var bag = cement.Variants.Single();

        await rig.Source.PushAsync(SyncKind.Item, [i7.Uuid.ToString(), bag.Uuid.ToString()]);

        rig.Gateway.Items.Should().HaveCount(2);
        var laptopItem = rig.Gateway.Items.Single(i => i.ExternalId == i7.Uuid.ToString());
        laptopItem.Name.Should().Be("Laptop");
        laptopItem.VariantName.Should().Be("i7 / 16GB");
        laptopItem.Sku.Should().Be(i7.Sku);
        rig.Gateway.Items.Single(i => i.ExternalId == bag.Uuid.ToString()).VariantName.Should().BeNull();
    }

    [Fact]
    public async Task PushAsync_still_sends_a_soft_deleted_variant_as_inactive_because_documents_name_it()
    {
        var rig = VariantRig.New();
        var p = rig.Product("Drill", variants: [("Corded", true), ("Cordless", false)]);
        var retired = p.Variants.Single(v => !v.IsActive);

        await rig.Source.PushAsync(SyncKind.Item, [retired.Uuid.ToString()]);

        var item = rig.Gateway.Items.Should().ContainSingle().Subject;
        item.IsActive.Should().BeFalse();
        item.VariantName.Should().Be("Cordless", "a retiring variant keeps the name QuickBooks knows it by");
    }

    [Fact]
    public async Task PushAsync_skips_malformed_unknown_and_repeated_ids_and_does_nothing_for_none()
    {
        var rig = VariantRig.New();
        var v = rig.Product("Cement").Variants.Single();

        await rig.Source.PushAsync(SyncKind.Item, []);
        await rig.Source.PushAsync(SyncKind.Item, ["nope", Guid.NewGuid().ToString(), v.Uuid.ToString(), v.Uuid.ToString().ToUpperInvariant()]);

        rig.Gateway.Items.Should().ContainSingle().Which.ExternalId.Should().Be(v.Uuid.ToString());
    }

    [Fact]
    public async Task PushAsync_sees_only_the_current_organization()
    {
        var dbName = Guid.NewGuid().ToString();
        var mine   = VariantRig.New(dbName: dbName);
        var theirs = VariantRig.New(dbName: dbName);
        var other  = theirs.Product("Theirs").Variants.Single();

        await mine.Source.PushAsync(SyncKind.Item, [other.Uuid.ToString()]);

        mine.Gateway.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task PushAllAsync_sends_active_variants_of_active_products_only()
    {
        var rig = VariantRig.New();
        rig.Product("Laptop", variants: [("i5", true), ("i7", true), ("i3", false)]);
        rig.Product("Retired Product", active: false, variants: [("Only", true)]);
        rig.Product("Cement");

        var sent = await rig.Source.PushAllAsync(SyncKind.Item, null);

        sent.Should().Be(3);
        rig.Gateway.Items.Select(i => $"{i.Name}/{i.VariantName}").Should().BeEquivalentTo(["Laptop/i5", "Laptop/i7", "Cement/"]);
    }

    [Fact]
    public async Task PushAllAsync_since_takes_new_variants_and_new_or_updated_products()
    {
        var since = new DateTime(2026, 9, 1);
        var old = since.AddMonths(-3);
        var rig = VariantRig.New();
        rig.Product("Old", created: old, variantsCreated: old);
        rig.Product("Updated Product", created: old, updated: since.AddDays(1), variantsCreated: old);
        rig.Product("New Product", created: since, variantsCreated: since);
        rig.Product("Old Product New Variant", created: old, variantsCreated: since.AddHours(5));
        rig.Product("Updated Before", created: old, updated: since.AddTicks(-1), variantsCreated: old);

        await rig.Source.PushAllAsync(SyncKind.Item, since);

        rig.Gateway.Items.Select(i => i.Name).Should().BeEquivalentTo(["Updated Product", "New Product", "Old Product New Variant"]);
    }

    [Fact]
    public async Task PushAllAsync_loads_and_sends_in_batches()
    {
        var events = new List<string>();
        var rig = VariantRig.New(new VariantMaterializationRecorder(events));
        var total = QuickBooksSupport.BatchSize * 2 + 13;
        for (var i = 0; i < total; i++) rig.Product($"P{i:D4}");
        events.Clear();
        rig.Gateway.OnCall = call => events.Add($"send:{call}");

        var sent = await rig.Source.PushAllAsync(SyncKind.Item, null);

        sent.Should().Be(total);
        rig.Gateway.Items.Select(i => i.ExternalId).Should().OnlyHaveUniqueItems();
        Runs(events).Should().Equal(("load", 200), ("send", 200), ("load", 200), ("send", 200), ("load", 13), ("send", 13));
    }

    [Fact]
    public async Task PushAllAsync_carries_on_past_a_failing_variant()
    {
        var rig = VariantRig.New();
        var a = rig.Product("A").Variants.Single();
        var b = rig.Product("B").Variants.Single();
        rig.Gateway.FailWhen = id => id == a.Uuid.ToString();

        var sent = await rig.Source.PushAllAsync(SyncKind.Item, null);

        sent.Should().Be(1);
        rig.Gateway.Items.Should().HaveCount(2);
        rig.Log.At(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain(a.Uuid.ToString());
        _ = b;
    }

    [Fact]
    public async Task A_refusal_is_information_not_a_warning()
    {
        var rig = VariantRig.New();
        var v = rig.Product("A").Variants.Single();
        rig.Gateway.Result = GatewayResult.Invalid([new GatewayError("Name", "COLON", "No ':' allowed.")]);

        await rig.Source.PushAsync(SyncKind.Item, [v.Uuid.ToString()]);

        rig.Log.At(LogLevel.Information).Should().ContainSingle().Which.Message.Should().Contain("Name");
        rig.Log.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    private static List<(string Kind, int Count)> Runs(IEnumerable<string> events)
    {
        var runs = new List<(string, int)>();
        foreach (var kind in events.Select(e => e.Split(':')[0]))
        {
            if (runs.Count > 0 && runs[^1].Item1 == kind) runs[^1] = (kind, runs[^1].Item2 + 1);
            else runs.Add((kind, 1));
        }
        return runs;
    }
}
