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
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Inventory.Tests.QuickBooks;

/// <summary>The real inventory service and repository over one in-memory database, with a recording gateway.</summary>
file sealed class Rig
{
    public required InventoryDbContext                      Db;
    public required RecordingQuickBooksGateway              Gateway;
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
        var publisher    = new VariantQuickBooksPublisher(readDb, new VariantQuickBooksSource(readDb, gateway, sourceLog), publisherLog);

        return new Rig
        {
            Db = db, Gateway = gateway, SourceLog = sourceLog, PublisherLog = publisherLog,
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
        var warning = rig.SourceLog.At(LogLevel.Warning).Should().ContainSingle().Subject;
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

        rig.SourceLog.At(LogLevel.Information).Should().ContainSingle();
        rig.SourceLog.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        rig.PublisherLog.Entries.Should().BeEmpty();
    }
}
