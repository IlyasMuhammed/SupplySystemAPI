using System.Text.Json;
using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Integration;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Repositories;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Inventory.Tests.QuickBooks;

/// <summary>The real inventory service and repository over one in-memory database, with a recording gateway.</summary>
file sealed class Rig
{
    public required InventoryDbContext                      Db;
    public required RecordingQuickBooksGateway              Gateway;
    public required QuickBooksEntitySyncBridge              Sync;
    public required VariantQuickBooksSource                 Source;
    public required VariantQuickBooksPublisher              Publisher;
    public required ListLogger<VariantQuickBooksSource>     SourceLog;
    public required ListLogger<VariantQuickBooksPublisher>  PublisherLog;
    public required InventoryService                        Service;

    public static Rig New(IEnumerable<IVariantReferenceChecker>? checkers = null, InventoryDbContext? publisherDb = null)
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var gateway      = new RecordingQuickBooksGateway();
        var sourceLog    = new ListLogger<VariantQuickBooksSource>();
        var publisherLog = new ListLogger<VariantQuickBooksPublisher>();
        var readDb       = publisherDb ?? db;
        var sync         = new QuickBooksEntitySyncBridge(gateway);
        var source       = new VariantQuickBooksSource(readDb, gateway, sourceLog);
        var publisher    = new VariantQuickBooksPublisher(readDb, source, sync, publisherLog);

        return new Rig
        {
            Db = db, Gateway = gateway, Sync = sync, Source = source, Publisher = publisher,
            SourceLog = sourceLog, PublisherLog = publisherLog,
            Service = new InventoryService(
                new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object),
                new ProductSearchIndexService(db), new Mock<IBackgroundJobClient>().Object, checkers ?? [], publisher)
        };
    }

    public async Task<int> NewProductAsync(string name, params string[] variantNames)
    {
        var req = new CreateProductRequest { Name = name, Description = $"{name} description" };
        if (variantNames.Length == 0) { req.PurchasePrice = 100m; req.SellingPrice = 150m; }
        else req.Variants = variantNames.Select((v, i) => new CreateProductVariantRequest
        {
            VariantName = v, PurchasePrice = 100m + i, SellingPrice = 150m + i, IsDefault = i == 0
        }).ToList();

        var (id, _) = await Service.CreateProductAsync(req, userId: 1);
        return id;
    }

    public void Forget()
    {
        Gateway.Items.Clear();
        Gateway.Calls.Clear();
        Sync.Requests.Clear();
        Db.ChangeTracker.Clear();
    }

    public Task<List<SMS.Modules.Inventory.Domain.ProductVariant>> VariantsOf(int productId) =>
        Db.ProductVariants.AsNoTracking().Where(v => v.ProductId == productId).OrderBy(v => v.Id).ToListAsync();
}

public class VariantQuickBooksTriggerTests
{
    [Fact]
    public async Task Creating_a_simple_product_sends_its_one_item_under_the_product_name()
    {
        var rig = Rig.New();

        var id = await rig.NewProductAsync("Portland Cement");

        var variant = (await rig.VariantsOf(id)).Single();
        var item = rig.Gateway.Items.Should().ContainSingle().Subject;
        item.ExternalId.Should().Be(variant.Uuid.ToString());
        item.Name.Should().Be("Portland Cement");
        item.VariantName.Should().BeNull();
        item.Sku.Should().Be(variant.Sku);
        item.Description.Should().Be("Portland Cement description");
        item.SalesPrice.Should().Be(150m);
        item.PurchaseCost.Should().Be(100m);
        item.Kind.Should().Be(ItemPayloadKind.Goods);
    }

    [Fact]
    public async Task Creating_a_product_with_variants_sends_each_with_its_variant_name()
    {
        var rig = Rig.New();

        await rig.NewProductAsync("Laptop", "i5 / 8GB", "i7 / 16GB");

        rig.Gateway.Items.Select(i => i.VariantName).Should().BeEquivalentTo(["i5 / 8GB", "i7 / 16GB"]);
        rig.Gateway.Items.Should().OnlyContain(i => i.Name == "Laptop");
    }

    [Fact]
    public async Task Patching_a_product_resends_every_variant_with_the_new_product_fields()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Laptop", "i5", "i7");
        rig.Forget();

        (await rig.Service.PatchProductAsync(id, new PatchProductRequest { Name = "Notebook", Description = "Thin and light" })).Should().BeTrue();

        rig.Gateway.Items.Should().HaveCount(2).And.OnlyContain(i => i.Name == "Notebook" && i.Description == "Thin and light");
    }

    [Fact]
    public async Task Patching_a_product_that_does_not_exist_sends_nothing()
    {
        var rig = Rig.New();

        (await rig.Service.PatchProductAsync(4242, new PatchProductRequest { Name = "X" })).Should().BeFalse();

        rig.Gateway.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Reclassifying_a_product_as_a_service_resends_its_items_as_services()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Installation");
        rig.Forget();

        await rig.Service.SetManufacturingConfigAsync(id, new ManufacturingConfigRequest
        {
            ProductType = ProductType.Service, SupplyMethod = SupplyMethod.Service, IsPurchasable = false
        });

        var item = rig.Gateway.Items.Should().ContainSingle().Subject;
        item.Kind.Should().Be(ItemPayloadKind.Service);
        item.IsSold.Should().BeTrue();
        item.IsPurchased.Should().BeFalse();
    }

    [Fact]
    public async Task Soft_deleting_a_product_retires_every_one_of_its_items()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Laptop", "i5", "i7");
        rig.Forget();

        (await rig.Service.SoftDeleteProductAsync(id)).Should().BeTrue();

        rig.Gateway.Items.Should().HaveCount(2).And.OnlyContain(i => !i.IsActive);
    }

    [Fact]
    public async Task Adding_a_second_variant_resends_the_first_which_now_needs_its_own_name()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Paint", "5 litre");
        rig.Gateway.Items.Single().VariantName.Should().BeNull("alone, the variant goes as the product");
        rig.Forget();

        var created = await rig.Service.CreateVariantAsync(id, new CreateProductVariantRequest { VariantName = "20 litre", PurchasePrice = 900m }, 1);

        rig.Gateway.Items.Should().HaveCount(2);
        rig.Gateway.Items.Single(i => i.ExternalId == created!.Uuid.ToString()).VariantName.Should().Be("20 litre");
        rig.Gateway.Items.Single(i => i.ExternalId != created!.Uuid.ToString()).VariantName.Should().Be("5 litre");
    }

    [Fact]
    public async Task Adding_a_third_variant_sends_only_the_new_one_because_no_sibling_name_changes()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Paint", "1 litre", "5 litre");
        rig.Forget();

        var created = await rig.Service.CreateVariantAsync(id, new CreateProductVariantRequest { VariantName = "20 litre", PurchasePrice = 900m }, 1);

        rig.Gateway.Items.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { ExternalId = created!.Uuid.ToString(), VariantName = "20 litre", Name = "Paint" });
    }

    [Fact]
    public async Task Soft_deleting_one_of_three_variants_sends_only_the_retired_one()
    {
        var checker = new Mock<IVariantReferenceChecker>();
        checker.Setup(c => c.IsVariantReferencedAsync(It.IsAny<Guid>())).ReturnsAsync(true);
        var rig = Rig.New([checker.Object]);
        var id = await rig.NewProductAsync("Paint", "1 litre", "5 litre", "20 litre");
        var variants = await rig.VariantsOf(id);
        rig.Forget();

        await rig.Service.DeleteVariantAsync(variants[2].Uuid);

        var item = rig.Gateway.Items.Should().ContainSingle().Subject;
        item.ExternalId.Should().Be(variants[2].Uuid.ToString());
        item.IsActive.Should().BeFalse();
        item.VariantName.Should().Be("20 litre");
    }

    [Fact]
    public async Task Editing_a_variant_resends_only_that_variant()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Laptop", "i5", "i7");
        var i7 = (await rig.VariantsOf(id)).Single(v => v.VariantName == "i7");
        rig.Forget();

        (await rig.Service.UpdateVariantAsync(i7.Uuid, new CreateProductVariantRequest
        {
            Sku = i7.Sku, VariantName = "i7 / 32GB", PurchasePrice = 999m, SellingPrice = 1299m
        })).Should().BeTrue();

        var item = rig.Gateway.Items.Should().ContainSingle().Subject;
        item.ExternalId.Should().Be(i7.Uuid.ToString());
        item.VariantName.Should().Be("i7 / 32GB");
        item.PurchaseCost.Should().Be(999m);
        item.SalesPrice.Should().Be(1299m);
    }

    [Fact]
    public async Task Editing_a_variant_that_does_not_exist_sends_nothing()
    {
        var rig = Rig.New();

        (await rig.Service.UpdateVariantAsync(Guid.NewGuid(), new CreateProductVariantRequest { VariantName = "x" })).Should().BeFalse();

        rig.Gateway.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Soft_deleting_a_transacted_variant_retires_it_under_its_name_and_its_last_sibling_drops_its_name()
    {
        var checker = new Mock<IVariantReferenceChecker>();
        checker.Setup(c => c.IsVariantReferencedAsync(It.IsAny<Guid>())).ReturnsAsync(true);
        var rig = Rig.New([checker.Object]);
        var id = await rig.NewProductAsync("Drill", "Corded", "Cordless");
        var variants = await rig.VariantsOf(id);
        rig.Forget();

        var result = await rig.Service.DeleteVariantAsync(variants[1].Uuid);

        result.SoftDeleted.Should().BeTrue();
        var retired = rig.Gateway.Items.Where(i => i.ExternalId == variants[1].Uuid.ToString()).ToList();
        retired.Should().NotBeEmpty().And.OnlyContain(i => !i.IsActive && i.VariantName == "Cordless");
        rig.Gateway.Items.Where(i => i.ExternalId == variants[0].Uuid.ToString())
            .Should().ContainSingle().Which.VariantName.Should().BeNull("it is the product's only active variant now");
    }

    [Fact]
    public async Task Hard_deleting_an_untransacted_variant_still_retires_it_from_what_it_was_before_the_delete()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Drill", "Corded", "Cordless");
        var variants = await rig.VariantsOf(id);
        rig.Forget();

        var result = await rig.Service.DeleteVariantAsync(variants[1].Uuid);

        result.SoftDeleted.Should().BeFalse();
        (await rig.Db.ProductVariants.AnyAsync(v => v.Uuid == variants[1].Uuid)).Should().BeFalse("it is really gone");
        var retired = rig.Gateway.Items.Should().ContainSingle(i => i.ExternalId == variants[1].Uuid.ToString()).Subject;
        retired.IsActive.Should().BeFalse();
        retired.VariantName.Should().Be("Cordless");
        retired.Sku.Should().Be(variants[1].Sku);
        rig.Gateway.Items.Single(i => i.ExternalId == variants[0].Uuid.ToString()).VariantName.Should().BeNull();
    }

    [Fact]
    public async Task Refusing_to_delete_the_last_variant_sends_nothing()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Cement");
        var only = (await rig.VariantsOf(id)).Single();
        rig.Forget();

        await rig.Service.Invoking(s => s.DeleteVariantAsync(only.Uuid)).Should().ThrowAsync<UnprocessableEntityException>();

        rig.Gateway.Calls.Should().BeEmpty();
    }

    // ── The gateway can never fail the inventory operation ───────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_throwing_gateway_does_not_fail_the_save_and_is_logged_with_the_variant_id(bool faultedTask)
    {
        var rig = Rig.New();
        rig.Gateway.Throw = new TimeoutException("gateway slow");
        rig.Gateway.FaultTask = faultedTask;

        var id = await rig.NewProductAsync("Still Saved");

        var variant = (await rig.VariantsOf(id)).Single();
        var warning = rig.PublisherLog.At(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain(variant.Uuid.ToString());
        warning.Exception.Should().BeOfType<TimeoutException>();
    }

    [Fact]
    public async Task A_failure_reading_for_QuickBooks_is_swallowed_and_logged()
    {
        var broken = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        await broken.DisposeAsync();
        var rig = Rig.New(publisherDb: broken);

        var id = await rig.NewProductAsync("Saved Anyway");
        await rig.Service.PatchProductAsync(id, new PatchProductRequest { Notes = "x" });

        (await rig.VariantsOf(id)).Should().ContainSingle();
        rig.PublisherLog.At(LogLevel.Warning).Should().HaveCount(2)
            .And.OnlyContain(w => w.Message.Contains(id.ToString()) && w.Exception is ObjectDisposedException);
    }

    [Fact]
    public async Task An_invalid_answer_is_information_and_the_save_stands()
    {
        var rig = Rig.New();
        rig.Gateway.Result = GatewayResult.Invalid([new GatewayError("Sku", "TOO_LONG", "Too long.")]);

        await rig.NewProductAsync("Fine");

        rig.PublisherLog.At(LogLevel.Information).Should().ContainSingle().Which.Message.Should().Contain("TOO_LONG");
        rig.PublisherLog.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        rig.SourceLog.Entries.Should().BeEmpty("the edit path no longer goes through the source's own send");
    }

    // ── A44 — the edit path is the entity sync gateway (D-25, D-76..D-79) ────

    [Fact]
    public async Task Every_variant_is_one_Product_request_with_the_payload_the_pull_back_would_send()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Laptop", "i5", "i7");
        var variants = await rig.VariantsOf(id);

        var requests = rig.Sync.Requests;
        requests.Should().HaveCount(2).And.OnlyContain(r => r.EntityType == SyncEntityType.Product && r.Action == SyncAction.Update);
        requests.Select(r => r.EntityId).Should().BeEquivalentTo(variants.Select(v => v.Uuid));
        foreach (var r in requests)
        {
            var v = variants.Single(x => x.Uuid == r.EntityId);
            r.IdempotencyKey.Should().MatchRegex($"^Product:Update:{v.Uuid}:\\d+$");
            r.Snapshot!.Sku.Should().Be(v.Sku);
            r.Snapshot.DisplayName.Should().Be($"Laptop - {v.VariantName}");
            r.Snapshot.UnitPrice.Should().Be(v.SellingPrice);
            r.Snapshot.PurchaseCost.Should().Be(v.PurchasePrice);
            r.Document.Should().BeOfType<ItemPayload>();
        }

        // Same JSON as the gateway's pull-back ("Push now" / dependency) → same fingerprint, no re-send.
        var published = requests.ToDictionary(r => r.EntityId.ToString(), r => JsonSerializer.Serialize((ItemPayload)r.Document));
        rig.Forget();
        await rig.Source.PushAsync(SyncKind.Item, variants.Select(v => v.Uuid.ToString()).ToList());
        rig.Gateway.Items.Should().HaveCount(2);
        foreach (var item in rig.Gateway.Items)
            published[item.ExternalId].Should().Be(JsonSerializer.Serialize(item));
    }

    [Fact]
    public async Task Soft_deleting_a_product_is_a_Deactivate_request_per_variant()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Laptop", "i5", "i7");
        rig.Forget();

        await rig.Service.SoftDeleteProductAsync(id);

        rig.Sync.Requests.Should().HaveCount(2).And.OnlyContain(r =>
            r.Action == SyncAction.Deactivate && r.Snapshot!.IsActive == false && r.IdempotencyKey!.StartsWith("Product:Deactivate:"));
    }

    [Fact]
    public async Task Soft_deleting_a_transacted_variant_is_Deactivate_and_its_last_sibling_an_Update()
    {
        var checker = new Mock<IVariantReferenceChecker>();
        checker.Setup(c => c.IsVariantReferencedAsync(It.IsAny<Guid>())).ReturnsAsync(true);
        var rig = Rig.New([checker.Object]);
        var id = await rig.NewProductAsync("Drill", "Corded", "Cordless");
        var variants = await rig.VariantsOf(id);
        rig.Forget();

        await rig.Service.DeleteVariantAsync(variants[1].Uuid);

        rig.Sync.Requests.Select(r => (r.EntityId, r.Action)).Should().Equal(
            (variants[1].Uuid, SyncAction.Deactivate), (variants[0].Uuid, SyncAction.Update));
    }

    [Fact]
    public async Task Hard_deleting_a_variant_is_a_Delete_request_built_from_the_before_delete_snapshot()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Drill", "Corded", "Cordless");
        var variants = await rig.VariantsOf(id);
        rig.Forget();

        var result = await rig.Service.DeleteVariantAsync(variants[1].Uuid);

        result.SoftDeleted.Should().BeFalse();
        rig.Sync.Requests.Select(r => (r.EntityId, r.Action)).Should().Equal(
            (variants[1].Uuid, SyncAction.Delete), (variants[0].Uuid, SyncAction.Update));
        var delete = rig.Sync.Requests[0];
        delete.IdempotencyKey.Should().StartWith($"Product:Delete:{variants[1].Uuid}:");
        var doc = (ItemPayload)delete.Document;
        (doc.IsActive, doc.VariantName, doc.Sku).Should().Be((false, "Cordless", variants[1].Sku));
    }

    [Fact]
    public async Task A_throwing_gateway_on_the_hard_delete_still_sends_the_sibling_and_says_to_retire_by_hand()
    {
        var rig = Rig.New();
        var id = await rig.NewProductAsync("Drill", "Corded", "Cordless");
        var variants = await rig.VariantsOf(id);
        rig.Forget();
        rig.Gateway.FailWhen = ext => ext == variants[1].Uuid.ToString();

        await rig.Service.DeleteVariantAsync(variants[1].Uuid);

        rig.Sync.Requests.Should().HaveCount(2);
        rig.PublisherLog.At(LogLevel.Warning).Should().ContainSingle()
            .Which.Message.Should().Contain(variants[1].Uuid.ToString()).And.Contain("by hand");
    }

    [Theory]
    [InlineData(EntitySyncOutcome.NoTargetConfigured, true,  LogLevel.Debug)]
    [InlineData(EntitySyncOutcome.ModuleNotLicensed,  false, LogLevel.Debug)]
    [InlineData(EntitySyncOutcome.DuplicateSkipped,   true,  LogLevel.Debug)]
    [InlineData(EntitySyncOutcome.ValidationFailed,   false, LogLevel.Information)]
    [InlineData(EntitySyncOutcome.Queued,             false, LogLevel.Warning)]
    public async Task What_the_gateway_answers_is_logged_and_never_fails_the_save(EntitySyncOutcome outcome, bool success, LogLevel level)
    {
        var rig = Rig.New();
        rig.Sync.Answer = new EntitySyncResult { Success = success, Outcome = outcome, ErrorMessage = "why" };

        var id = await rig.NewProductAsync("Fine");

        var variant = (await rig.VariantsOf(id)).Single();
        rig.Gateway.Calls.Should().BeEmpty("the answer replaced the forward");
        rig.PublisherLog.Entries.Should().ContainSingle().Which.Level.Should().Be(level);
        rig.PublisherLog.Entries[0].Message.Should().Contain(variant.Uuid.ToString());
    }

    [Fact]
    public async Task Without_a_router_edits_and_the_hard_delete_go_straight_to_the_QuickBooks_gateway_as_before_A44()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var gateway = new RecordingQuickBooksGateway();
        var source = new VariantQuickBooksSource(db, gateway, new ListLogger<VariantQuickBooksSource>());
        var publisher = new VariantQuickBooksPublisher(db, source, new NullEntitySyncGateway(), new ListLogger<VariantQuickBooksPublisher>());
        var service = new InventoryService(new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object),
            new ProductSearchIndexService(db), new Mock<IBackgroundJobClient>().Object, [], publisher);

        var (id, _) = await service.CreateProductAsync(new CreateProductRequest
        {
            Name = "Drill",
            Variants =
            [
                new CreateProductVariantRequest { VariantName = "Corded", PurchasePrice = 1m, IsDefault = true },
                new CreateProductVariantRequest { VariantName = "Cordless", PurchasePrice = 2m }
            ]
        }, 1);
        var variants = await db.ProductVariants.AsNoTracking().Where(v => v.ProductId == id).OrderBy(v => v.Id).ToListAsync();
        gateway.Items.Should().HaveCount(2);
        var built = await source.BuildProductAsync(id);
        gateway.Items.Select(i => JsonSerializer.Serialize(i)).Should().BeEquivalentTo(built.Select(b => JsonSerializer.Serialize(b)));

        gateway.Items.Clear(); gateway.Calls.Clear(); db.ChangeTracker.Clear();
        await service.DeleteVariantAsync(variants[1].Uuid);

        gateway.Items.Select(i => (i.ExternalId, i.IsActive)).Should().Equal(
            (variants[1].Uuid.ToString(), false), (variants[0].Uuid.ToString(), true));
    }

    [Fact]
    public async Task A_product_import_pushes_a_request_per_imported_product_after_the_commit()
    {
        var h = new SMS.Modules.Inventory.Tests.Import.ImportHarness();
        var gateway = new RecordingQuickBooksGateway();
        var sync = new QuickBooksEntitySyncBridge(gateway);
        var publisher = new VariantQuickBooksPublisher(h.Db,
            new VariantQuickBooksSource(h.Db, gateway, new ListLogger<VariantQuickBooksSource>()), sync, new ListLogger<VariantQuickBooksPublisher>());
        var target = new SMS.Modules.Inventory.Import.ProductImportTarget(h.Db, h.Services, null, publisher);

        var outcomes = await target.ApplyAsync(h.Org, 1,
        [
            SMS.Modules.Inventory.Tests.Import.ImportHarness.Create(2, ("sku", "A-1"), ("name", "Alpha"), ("sale_price", "5")),
            SMS.Modules.Inventory.Tests.Import.ImportHarness.Create(3, ("sku", "B-1"), ("name", "Beta"), ("is_active", "false"))
        ]);

        outcomes.Should().HaveCount(2);
        sync.Requests.Should().HaveCount(2).And.OnlyContain(r => r.EntityType == SyncEntityType.Product);
        sync.Requests.Select(r => (r.Snapshot!.Sku, r.Action)).Should().Equal(("A-1", SyncAction.Update), ("B-1", SyncAction.Deactivate));
        gateway.Items.Select(i => i.Name).Should().Equal("Alpha", "Beta");
    }
}
