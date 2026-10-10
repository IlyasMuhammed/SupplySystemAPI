using FluentAssertions;
using Hangfire;
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

/// <summary>
/// A36-P1-01..04 / P1-10 — a product's service configuration (D-2): SVC-P-01..04, 06, 07, TS-01..03, and the read-only
/// hasActiveServiceBom (D-3: SVC-P-05 is a warning, not a refusal).
/// </summary>
public class ServiceProductTests
{
    private static (InventoryDbContext Db, InventoryRepository Repo) NewRepo()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        return (db, new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object));
    }

    private static CreateProductRequest Service(string name, string? policy = null, string? billing = null,
        decimal? hours = null, bool? hasBom = null, bool? subcontract = null, decimal? sellingPrice = null) => new()
    {
        Name = name, ProductType = ProductType.Service, PurchasePrice = 0m, SellingPrice = sellingPrice, UomCode = "HR",
        ServiceInvoicingPolicy = policy, ServiceBillingModel = billing, EstimatedDurationHours = hours,
        HasServiceBom = hasBom, IsSubcontractable = subcontract
    };

    private static async Task<ProductDetailModel> CreateAsync(InventoryRepository repo, CreateProductRequest req)
    {
        var (id, _) = await repo.CreateProductAsync(req, 1);
        return (await repo.GetProductByIdAsync(id))!;
    }

    // ── Create (TS-01) ────────────────────────────────────────────────────────

    [Fact]
    public async Task TS01_a_service_saves_and_reads_back_its_configuration()
    {
        var (_, repo) = NewRepo();

        var product = await CreateAsync(repo,
            Service("AC Repair", ServiceInvoicingPolicy.FixedPrice, ServiceBillingModel.PassThrough, 2.5m, hasBom: true, subcontract: true));

        product.ServiceInvoicingPolicy.Should().Be(ServiceInvoicingPolicy.FixedPrice);
        product.ServiceBillingModel.Should().Be(ServiceBillingModel.PassThrough);
        product.EstimatedDurationHours.Should().Be(2.5m);
        product.HasServiceBom.Should().BeTrue();
        product.IsSubcontractable.Should().BeTrue();
    }

    [Fact]
    public async Task Codes_are_accepted_in_any_case()
    {
        var (_, repo) = NewRepo();

        var product = await CreateAsync(repo, Service("Install", " cost_plus ", "inclusive"));

        product.ServiceInvoicingPolicy.Should().Be(ServiceInvoicingPolicy.CostPlus);
        product.ServiceBillingModel.Should().Be(ServiceBillingModel.Inclusive);
    }

    [Fact]
    public async Task A_service_with_no_configuration_is_fine_and_a_stock_item_carries_none()
    {
        var (_, repo) = NewRepo();

        var service = await CreateAsync(repo, Service("Consulting"));
        var bolt    = await CreateAsync(repo, new CreateProductRequest { Name = "Bolt", PurchasePrice = 1m, HasServiceBom = false });

        service.ServiceInvoicingPolicy.Should().BeNull();
        service.HasServiceBom.Should().BeFalse();
        bolt.ServiceInvoicingPolicy.Should().BeNull();
        bolt.HasServiceBom.Should().BeFalse("false is not a value worth refusing on a non-service");
    }

    [Fact]
    public async Task Unknown_codes_are_refused()
    {
        var (_, repo) = NewRepo();

        await FluentActions.Invoking(() => repo.CreateProductAsync(Service("A", policy: "HOURLY"), 1))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not an invoicing policy*");
        await FluentActions.Invoking(() => repo.CreateProductAsync(Service("B", billing: "MIXED"), 1))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not a billing model*");
    }

    // ── SVC-P-01/02/04/07 — service only (TS-02) ──────────────────────────────

    public static TheoryData<CreateProductRequest, string> NonServiceRefusals => new()
    {
        { new() { Name = "P1", PurchasePrice = 1m, ServiceInvoicingPolicy = ServiceInvoicingPolicy.FixedPrice }, "Invoicing policy is only applicable to service products" },
        { new() { Name = "P2", PurchasePrice = 1m, ServiceBillingModel = ServiceBillingModel.Inclusive },        "Billing model is only applicable to service products" },
        { new() { Name = "P4", PurchasePrice = 1m, HasServiceBom = true },                                       "Service BOM is only applicable to service products" },
        { new() { Name = "P7", PurchasePrice = 1m, IsSubcontractable = true },                                   "Subcontract flag is only applicable to service products" },
    };

    [Theory]
    [MemberData(nameof(NonServiceRefusals))]
    public async Task Service_fields_are_refused_on_a_non_service_product(CreateProductRequest req, string message)
    {
        var (db, repo) = NewRepo();

        await FluentActions.Invoking(() => repo.CreateProductAsync(req, 1))
            .Should().ThrowAsync<BadRequestException>().WithMessage(message);
        db.Products.Should().BeEmpty();
    }

    [Fact]
    public async Task TS02_turning_on_service_BOM_for_a_stock_item_is_refused()
    {
        var (_, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(new CreateProductRequest { Name = "Bolt", PurchasePrice = 1m }, 1);

        await FluentActions.Invoking(() => repo.PatchProductAsync(id, new PatchProductRequest { HasServiceBom = true }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Service BOM is only applicable to service products");
    }

    // ── SVC-P-03 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    public async Task Estimated_duration_must_be_positive(decimal hours)
    {
        var (_, repo) = NewRepo();

        await FluentActions.Invoking(() => repo.CreateProductAsync(Service("Repair", hours: hours), 1))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Estimated duration must be a positive number");
        await FluentActions.Invoking(() => repo.CreateProductAsync(
                new CreateProductRequest { Name = "Bolt", PurchasePrice = 1m, EstimatedDurationHours = hours }, 1))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Estimated duration must be a positive number");
    }

    // ── SVC-P-06 — hourly rate = default variant's selling price (D-2) ────────

    [Fact]
    public async Task Time_and_material_needs_an_hourly_rate()
    {
        var (_, repo) = NewRepo();

        await FluentActions.Invoking(() => repo.CreateProductAsync(Service("Callout", ServiceInvoicingPolicy.TimeAndMaterial), 1))
            .Should().ThrowAsync<BadRequestException>()
            .WithMessage("Hourly rate (selling price of the default variant) is required for Time & Material services");
        await FluentActions.Invoking(() => repo.CreateProductAsync(Service("Callout 2", ServiceInvoicingPolicy.TimeAndMaterial, sellingPrice: 0m), 1))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Hourly rate*");

        var ok = await CreateAsync(repo, Service("Callout 3", ServiceInvoicingPolicy.TimeAndMaterial, sellingPrice: 45m));
        ok.ServiceInvoicingPolicy.Should().Be(ServiceInvoicingPolicy.TimeAndMaterial);
    }

    [Fact]
    public async Task Time_and_material_reads_the_default_of_explicit_variants()
    {
        var (_, repo) = NewRepo();
        var req = Service("Maintenance", ServiceInvoicingPolicy.TimeAndMaterial);
        req.PurchasePrice = null;
        req.Variants =
        [
            new() { VariantName = "Standard", PurchasePrice = 0m, SellingPrice = 0m, IsDefault = false },
            new() { VariantName = "Default",  PurchasePrice = 0m, SellingPrice = 60m, IsDefault = true }
        ];

        var product = await CreateAsync(repo, req);

        product.ServiceInvoicingPolicy.Should().Be(ServiceInvoicingPolicy.TimeAndMaterial);
    }

    [Fact]
    public async Task Switching_an_existing_service_to_time_and_material_checks_its_default_variant()
    {
        var (_, repo) = NewRepo();
        var (cheap, _) = await repo.CreateProductAsync(Service("Unpriced", ServiceInvoicingPolicy.FixedPrice), 1);
        var (priced, _) = await repo.CreateProductAsync(Service("Priced", ServiceInvoicingPolicy.FixedPrice, sellingPrice: 30m), 1);

        await FluentActions.Invoking(() => repo.PatchProductAsync(cheap,
                new PatchProductRequest { ServiceInvoicingPolicy = ServiceInvoicingPolicy.TimeAndMaterial }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Hourly rate*");

        await repo.PatchProductAsync(priced, new PatchProductRequest { ServiceInvoicingPolicy = ServiceInvoicingPolicy.TimeAndMaterial });
        (await repo.GetProductByIdAsync(priced))!.ServiceInvoicingPolicy.Should().Be(ServiceInvoicingPolicy.TimeAndMaterial);
    }

    // ── Patch overlay and type changes ────────────────────────────────────────

    [Fact]
    public async Task A_patch_overlays_what_it_sends_and_keeps_the_rest()
    {
        var (_, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(
            Service("Survey", ServiceInvoicingPolicy.CostPlus, ServiceBillingModel.Inclusive, 1m, hasBom: true), 1);

        await repo.PatchProductAsync(id, new PatchProductRequest { ServiceBillingModel = ServiceBillingModel.PassThrough, Name = "Site Survey" });

        var product = (await repo.GetProductByIdAsync(id))!;
        product.ServiceInvoicingPolicy.Should().Be(ServiceInvoicingPolicy.CostPlus);
        product.ServiceBillingModel.Should().Be(ServiceBillingModel.PassThrough);
        product.EstimatedDurationHours.Should().Be(1m);
        product.HasServiceBom.Should().BeTrue();
    }

    [Fact]
    public async Task A_patch_clears_with_explicit_null_and_false_as_the_UI_sends_them()
    {
        var (_, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(
            Service("Survey", ServiceInvoicingPolicy.CostPlus, ServiceBillingModel.Inclusive, 1m, hasBom: true, subcontract: true), 1);

        // The UI's body: every service field present, cleared ones as null/false.
        var body = System.Text.Json.JsonSerializer.Deserialize<PatchProductRequest>(
            """{"serviceInvoicingPolicy":null,"serviceBillingModel":null,"estimatedDurationHours":null,"hasServiceBom":false,"isSubcontractable":false}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        await repo.PatchProductAsync(id, body);

        var product = (await repo.GetProductByIdAsync(id))!;
        product.ServiceInvoicingPolicy.Should().BeNull();
        product.ServiceBillingModel.Should().BeNull();
        product.EstimatedDurationHours.Should().BeNull();
        product.HasServiceBom.Should().BeFalse();
        product.IsSubcontractable.Should().BeFalse();
    }

    [Fact]
    public async Task A_patch_body_without_the_service_fields_keeps_them()
    {
        var (_, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(Service("Survey", ServiceInvoicingPolicy.CostPlus, hours: 2m), 1);

        var body = System.Text.Json.JsonSerializer.Deserialize<PatchProductRequest>("""{"name":"Site Survey"}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        await repo.PatchProductAsync(id, body);

        var product = (await repo.GetProductByIdAsync(id))!;
        product.ServiceInvoicingPolicy.Should().Be(ServiceInvoicingPolicy.CostPlus);
        product.EstimatedDurationHours.Should().Be(2m);
    }

    [Fact]
    public async Task A_non_service_patch_with_null_and_false_service_fields_is_accepted()
    {
        var (_, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(new CreateProductRequest { Name = "Bolt", PurchasePrice = 1m }, 1);

        var ok = await repo.PatchProductAsync(id, new PatchProductRequest
        {
            Name = "Hex Bolt", ServiceInvoicingPolicy = null, ServiceBillingModel = null, EstimatedDurationHours = null,
            HasServiceBom = false, IsSubcontractable = false
        });

        ok.Should().BeTrue();
        (await repo.GetProductByIdAsync(id))!.Name.Should().Be("Hex Bolt");
    }

    [Fact]
    public async Task A_product_that_stops_being_a_service_loses_its_service_configuration()
    {
        var (_, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(
            Service("Kit", ServiceInvoicingPolicy.FixedPrice, ServiceBillingModel.Inclusive, 3m, hasBom: true, subcontract: true), 1);

        await repo.PatchProductAsync(id, new PatchProductRequest { ProductType = ProductType.Consumable, SupplyMethod = SupplyMethod.Purchase, IsStockable = true });

        var product = (await repo.GetProductByIdAsync(id))!;
        product.ProductType.Should().Be(ProductType.Consumable);
        product.ServiceInvoicingPolicy.Should().BeNull();
        product.ServiceBillingModel.Should().BeNull();
        product.EstimatedDurationHours.Should().BeNull();
        product.HasServiceBom.Should().BeFalse();
        product.IsSubcontractable.Should().BeFalse();
    }

    [Fact]
    public async Task The_manufacturing_config_endpoint_also_clears_it_on_a_type_change()
    {
        var (_, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(Service("Kit", ServiceInvoicingPolicy.FixedPrice, hasBom: true), 1);

        await repo.SetManufacturingConfigAsync(id, new ManufacturingConfigRequest
        {
            ProductType = ProductType.Consumable, SupplyMethod = SupplyMethod.Purchase
        });

        var product = (await repo.GetProductByIdAsync(id))!;
        product.ServiceInvoicingPolicy.Should().BeNull();
        product.HasServiceBom.Should().BeFalse();
    }

    // ── hasActiveServiceBom (D-3 / TS-03) ─────────────────────────────────────

    private static InventoryService ServiceWith(InventoryDbContext db, IBomStructureReader? reader) =>
        new(new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object), new ProductSearchIndexService(db),
            new Mock<IBackgroundJobClient>().Object, [], bomReader: reader);

    private static BomStructure AnyBom() => new(Guid.NewGuid(), "BOM-2026-00001", 1, 1m, []);

    [Fact]
    public async Task TS03_service_BOM_can_be_enabled_without_any_BOM_and_reads_as_no_active_BOM()
    {
        // D-3: SVC-P-05 is circular with SVC-BOM-01, so saving is allowed and the detail warns instead.
        var (db, repo) = NewRepo();
        var reader = new Mock<IBomStructureReader>();
        reader.Setup(r => r.GetActiveBomsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new Dictionary<Guid, BomStructure>());
        var (id, _) = await repo.CreateProductAsync(Service("Boiler Service", hasBom: true), 1);

        var detail = (await ServiceWith(db, reader.Object).GetProductByIdAsync(id))!;

        detail.HasServiceBom.Should().BeTrue();
        detail.HasActiveServiceBom.Should().BeFalse();
    }

    [Fact]
    public async Task A_service_whose_variant_has_an_active_BOM_reads_as_having_one()
    {
        var (db, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(Service("Boiler Service", hasBom: true), 1);
        var variant = db.ProductVariants.Single(v => v.ProductId == id);
        var reader = new Mock<IBomStructureReader>();
        reader.Setup(r => r.GetActiveBomsAsync(variant.OrganizationId,
                  It.Is<IReadOnlyCollection<Guid>>(u => u.Contains(variant.Uuid)), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new Dictionary<Guid, BomStructure> { [variant.Uuid] = AnyBom() });

        (await ServiceWith(db, reader.Object).GetProductByIdAsync(id))!.HasActiveServiceBom.Should().BeTrue();
    }

    [Fact]
    public async Task Without_service_BOM_enabled_or_without_Material_the_flag_is_false_and_nothing_is_asked()
    {
        var (db, repo) = NewRepo();
        var (plain, _) = await repo.CreateProductAsync(Service("Consulting"), 1);
        var (withBom, _) = await repo.CreateProductAsync(Service("Repair", hasBom: true), 1);
        var reader = new Mock<IBomStructureReader>(MockBehavior.Strict);

        (await ServiceWith(db, reader.Object).GetProductByIdAsync(plain))!.HasActiveServiceBom.Should().BeFalse();
        (await ServiceWith(db, null).GetProductByIdAsync(withBom))!.HasActiveServiceBom.Should().BeFalse();
        reader.VerifyNoOtherCalls();
    }
}
