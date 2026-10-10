using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Material.Controllers;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// A36-P1-06..08 / P1-10 — service BOMs (D-4): eligibility (SVC-BOM-01, SVC-BOM-06 unchanged), line source types
/// (SVC-BOM-02..05), TS-04/TS-05, supplier names, versioning, the shared lifecycle and the any-of feature gate.
/// </summary>
public class ServiceBomTests
{
    private const int Author   = 7;
    private const int Reviewer = 8;

    private sealed class Harness
    {
        public MaterialDbContext Material { get; }
        public InventoryDbContext Inventory { get; }
        public BomRepository Repo { get; }
        public Guid Vendor { get; } = Guid.NewGuid();
        public Guid Customer { get; } = Guid.NewGuid();
        public Guid InactiveVendor { get; } = Guid.NewGuid();
        private int _sequence;

        public Harness()
        {
            var tenant = new StaticTenantContext();
            Material = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
            Inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

            var numbers = new Mock<IDocumentNumberGenerator>();
            numbers.Setup(n => n.NextAsync("BOM", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(() => $"BOM-2026-{++_sequence:D5}");

            var partners = new Mock<IPartnerRoleLookup>();
            partners.Setup(p => p.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((PartnerRoleInfo?)null);
            partners.Setup(p => p.GetAsync(Vendor, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PartnerRoleInfo(Vendor, "CoolFix Ltd", false, true, true));
            partners.Setup(p => p.GetAsync(Customer, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PartnerRoleInfo(Customer, "Acme Retail", true, false, true));
            partners.Setup(p => p.GetAsync(InactiveVendor, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PartnerRoleInfo(InactiveVendor, "Old Vendor", false, true, false));

            var names = new Mock<ISupplierNameLookupService>();
            names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
                 .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.Where(i => i == Vendor).ToDictionary(i => i, _ => "CoolFix Ltd"));

            Repo = new BomRepository(Material, Inventory, numbers.Object, partners: partners.Object, supplierNames: names.Object);
        }

        public (Guid Product, Guid Variant) Product(
            string name, string type = ProductType.RawMaterial, bool hasServiceBom = false, string uom = "PCS",
            bool forProduction = true, bool forServices = false, string? supplyMethod = null)
        {
            var product = new Product
            {
                Uuid = Guid.NewGuid(), Sku = $"SKU-{Guid.NewGuid():N}"[..12], Name = name, UomCode = uom,
                ProductType = type, SupplyMethod = supplyMethod ?? ProductTypeRules.DefaultSupplyMethod(type),
                IsStockable = type != ProductType.Service, HasServiceBom = hasServiceBom, IsActive = true, CreatedBy = 1
            };
            var variant = new ProductVariant
            {
                Uuid = Guid.NewGuid(), Sku = $"{product.Sku}-1", VariantName = "Default", IsDefault = true, IsActive = true,
                PurchasePrice = 5m, IsAvailableForProduction = forProduction, IsAvailableForServices = forServices, CreatedBy = 1
            };
            product.Variants.Add(variant);
            Inventory.Products.Add(product);
            Inventory.SaveChanges();
            return (product.Uuid, variant.Uuid);
        }

        /// <summary>A service with service BOM on, a stock part, subcontracted labour (service) and internal labour (HR).</summary>
        public (Guid Service, Guid Part, Guid Subcontract, Guid Labor) Catalog()
        {
            var (service, _) = Product("AC Service", ProductType.Service, hasServiceBom: true, uom: "HR");
            var (_, part)    = Product("Filter", ProductType.Consumable, forProduction: false, forServices: true);
            var (_, sub)     = Product("Gas Refill (outsourced)", ProductType.Service, uom: "EA", forProduction: false, forServices: true);
            var (_, labor)   = Product("Technician Hour", ProductType.Service, uom: "HR", forProduction: false, forServices: true);
            return (service, part, sub, labor);
        }
    }

    private static BomLineRequest Line(Guid variant, string? source = null, Guid? supplier = null, decimal qty = 1m, string? uom = null) =>
        new() { MaterialVariantUuid = variant, Quantity = qty, SourceType = source, SubcontractSupplierUuid = supplier, Uom = uom };

    private static CreateBomRequest Recipe(Guid product, params BomLineRequest[] lines) =>
        new() { ProductUuid = product, BaseQuantity = 1m, Lines = [.. lines] };

    // ── Eligibility (SVC-BOM-01, SVC-BOM-06) ──────────────────────────────────

    [Fact]
    public async Task A_service_with_service_BOM_enabled_gets_a_BOM_through_the_normal_lifecycle()
    {
        var h = new Harness();
        var (service, part, sub, labor) = h.Catalog();

        var uuid = await h.Repo.CreateAsync(Recipe(service,
            Line(part),
            Line(sub, BomLineSourceType.Subcontract, h.Vendor),
            Line(labor, BomLineSourceType.InternalLabor, qty: 2m)), Author);
        await h.Repo.SubmitAsync(uuid, Author);
        await h.Repo.ApproveAsync(uuid, Reviewer);
        await h.Repo.ActivateAsync(uuid, Reviewer);

        var bom = (await h.Repo.GetByUuidAsync(uuid))!;
        bom.Status.Should().Be(BomStatus.Active);
        bom.Lines.Select(l => l.SourceType).Should().Equal(BomLineSourceType.Stock, BomLineSourceType.Subcontract, BomLineSourceType.InternalLabor);
        bom.Lines[2].Uom.Should().Be("HR");
    }

    [Fact]
    public async Task SVC_BOM_01_a_service_without_service_BOM_enabled_is_refused()
    {
        var h = new Harness();
        var (service, _) = h.Product("Consulting", ProductType.Service, hasServiceBom: false, uom: "HR");
        var (_, part) = h.Product("Paper");

        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(part)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This product does not have service BOM enabled");
    }

    [Fact]
    public async Task SVC_BOM_06_manufacturing_rules_are_unchanged()
    {
        var h = new Harness();
        var (laptop, _) = h.Product("Laptop", ProductType.StockItem);
        var (shirt, _)  = h.Product("Printed T-Shirt", ProductType.FinishedGood);
        var (_, ink)    = h.Product("Ink");
        var (_, sofa)   = h.Product("Sofa", forProduction: false, forServices: true);

        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(laptop, Line(ink)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*Laptop is not configured for manufacturing*");
        // A manufacturing input still has to be available for production — "available for services" does not do.
        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(shirt, Line(sofa)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not configured as a BOM input*Available for production*");
        // And its lines stay STOCK.
        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(shirt, Line(ink, BomLineSourceType.InternalLabor)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*only allowed on a service BOM*");

        var uuid = await h.Repo.CreateAsync(Recipe(shirt, Line(ink)), Author);
        (await h.Repo.GetByUuidAsync(uuid))!.Lines.Single().SourceType.Should().Be(BomLineSourceType.Stock);
    }

    [Fact]
    public async Task A_service_BOM_input_must_be_available_for_services_or_production()
    {
        var h = new Harness();
        var (service, _, _, _) = h.Catalog();
        var (_, hidden) = h.Product("Hidden Part", ProductType.Consumable, forProduction: false, forServices: false);

        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(hidden)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*Available for services*");
    }

    // ── SVC-BOM-02 / 03 / 04 (TS-04, TS-05) ───────────────────────────────────

    [Fact]
    public async Task TS04_SVC_BOM_02_a_subcontracted_line_needs_a_supplier()
    {
        var h = new Harness();
        var (service, _, sub, _) = h.Catalog();

        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(sub, BomLineSourceType.Subcontract)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Subcontract supplier is required for subcontracted BOM lines");
    }

    [Fact]
    public async Task TS05_a_subcontracted_line_with_a_vendor_saves_and_reads_back_the_supplier_name()
    {
        var h = new Harness();
        var (service, _, sub, _) = h.Catalog();

        var uuid = await h.Repo.CreateAsync(Recipe(service, Line(sub, " subcontract ", h.Vendor)), Author);

        var line = (await h.Repo.GetByUuidAsync(uuid))!.Lines.Single();
        line.SourceType.Should().Be(BomLineSourceType.Subcontract);
        line.SubcontractSupplierUuid.Should().Be(h.Vendor);
        line.SubcontractSupplierName.Should().Be("CoolFix Ltd");
    }

    [Fact]
    public async Task SVC_BOM_02_the_supplier_must_be_an_active_vendor_of_the_organization()
    {
        var h = new Harness();
        var (service, _, sub, _) = h.Catalog();

        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(sub, BomLineSourceType.Subcontract, h.Customer)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Subcontract supplier must be a vendor of your organization");
        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(sub, BomLineSourceType.Subcontract, Guid.NewGuid())), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Subcontract supplier must be a vendor of your organization");
        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(sub, BomLineSourceType.Subcontract, h.InactiveVendor)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*Old Vendor is inactive*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(BomLineSourceType.Stock)]
    [InlineData(BomLineSourceType.InternalLabor)]
    public async Task SVC_BOM_03_a_supplier_is_only_valid_on_a_subcontracted_line(string? source)
    {
        var h = new Harness();
        var (service, part, _, labor) = h.Catalog();
        var material = source == BomLineSourceType.InternalLabor ? labor : part;

        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(material, source, h.Vendor)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Supplier reference is only valid for subcontracted lines");
    }

    [Fact]
    public async Task SVC_BOM_04_a_subcontracted_material_must_be_a_service()
    {
        var h = new Harness();
        var (service, part, _, _) = h.Catalog();

        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(part, BomLineSourceType.Subcontract, h.Vendor)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Subcontracted line material must be a service-type product");
    }

    // ── SVC-BOM-05 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SVC_BOM_05_internal_labor_must_be_in_hours()
    {
        var h = new Harness();
        var (service, _, sub, labor) = h.Catalog();

        // The material is measured in EA.
        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(sub, BomLineSourceType.InternalLabor)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Internal labor lines must use hours (HR) as unit of measure");
        // The material is in HR, but the line asks for another unit.
        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(labor, BomLineSourceType.InternalLabor, uom: "DAY")), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Internal labor lines must use hours (HR) as unit of measure");
    }

    [Fact]
    public async Task Unknown_source_types_and_service_stock_lines_are_refused()
    {
        var h = new Harness();
        var (service, part, sub, _) = h.Catalog();

        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(part, "DROPSHIP")), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not a BOM line source type*");
        await FluentActions.Invoking(() => h.Repo.CreateAsync(Recipe(service, Line(sub)), Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*is a service and cannot be a stock line*");
    }

    // ── Update, versioning, comparison ────────────────────────────────────────

    [Fact]
    public async Task Update_and_new_version_keep_the_source_and_supplier_and_compare_reports_a_change()
    {
        var h = new Harness();
        var (service, part, sub, _) = h.Catalog();
        var v1 = await h.Repo.CreateAsync(Recipe(service, Line(part), Line(sub, BomLineSourceType.Subcontract, h.Vendor)), Author);
        await h.Repo.SubmitAsync(v1, Author);
        await h.Repo.ApproveAsync(v1, Reviewer);
        await h.Repo.ActivateAsync(v1, Reviewer);

        var v2 = await h.Repo.NewVersionAsync(v1, Author);
        var copied = (await h.Repo.GetByUuidAsync(v2))!.Lines.Single(l => l.MaterialVariantUuid == sub);
        copied.SourceType.Should().Be(BomLineSourceType.Subcontract);
        copied.SubcontractSupplierUuid.Should().Be(h.Vendor);

        // Revise v2: the subcontracted refill is no longer on the recipe; quantity of the part changes.
        await h.Repo.UpdateAsync(v2, new UpdateBomRequest { Lines = [Line(part, qty: 2m)] }, Author);
        var comparison = await h.Repo.CompareAsync(v1, v2);
        comparison.Removed.Should().ContainSingle(l => l.SourceType == BomLineSourceType.Subcontract && l.SubcontractSupplierName == "CoolFix Ltd");
    }

    [Fact]
    public async Task Turning_service_BOM_off_blocks_editing_and_activating_its_BOMs()
    {
        var h = new Harness();
        var (service, part, _, _) = h.Catalog();
        var uuid = await h.Repo.CreateAsync(Recipe(service, Line(part)), Author);
        h.Inventory.Products.Single(p => p.Uuid == service).HasServiceBom = false;
        h.Inventory.SaveChanges();

        await FluentActions.Invoking(() => h.Repo.UpdateAsync(uuid, new UpdateBomRequest { Notes = "x" }, Author))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This product does not have service BOM enabled");
    }

    [Fact]
    public async Task An_active_service_BOM_is_found_by_the_shared_active_BOM_reader()
    {
        // What Inventory's hasActiveServiceBom (D-3) and the service-order planner read.
        var h = new Harness();
        var (service, part, _, _) = h.Catalog();
        var variant = h.Inventory.ProductVariants.Single(v => v.Product.Uuid == service);
        var reader = new BomStructureReader(h.Material, h.Inventory);

        (await reader.GetActiveBomsAsync(variant.OrganizationId, [variant.Uuid])).Should().BeEmpty();

        var uuid = await h.Repo.CreateAsync(Recipe(service, Line(part)), Author);
        await h.Repo.SubmitAsync(uuid, Author);
        await h.Repo.ApproveAsync(uuid, Reviewer);
        await h.Repo.ActivateAsync(uuid, Reviewer);

        (await reader.GetActiveBomsAsync(variant.OrganizationId, [variant.Uuid])).Should().ContainKey(variant.Uuid);
    }

    [Fact]
    public async Task The_catalog_product_reader_exposes_classification_and_service_configuration()
    {
        var h = new Harness();
        var (service, _) = h.Product("Repair", ProductType.Service, hasServiceBom: true, uom: "HR");
        var entity = h.Inventory.Products.Include(p => p.Variants).Single(p => p.Uuid == service);
        entity.ServiceInvoicingPolicy = ServiceInvoicingPolicy.TimeAndMaterial;
        entity.ServiceBillingModel    = ServiceBillingModel.PassThrough;
        entity.EstimatedDurationHours = 1.5m;
        entity.IsSubcontractable      = true;
        entity.Variants.Single().SellingPrice = 40m;
        h.Inventory.SaveChanges();

        var facts = (await CatalogProductReader.GetAsync(h.Inventory, entity.OrganizationId, [service]))[service];

        facts.ProductType.Should().Be(ProductType.Service);
        facts.IsService.Should().BeTrue();
        facts.IsBomEligible.Should().BeTrue();
        facts.HasServiceBom.Should().BeTrue();
        facts.IsSubcontractable.Should().BeTrue();
        facts.ServiceInvoicingPolicy.Should().Be(ServiceInvoicingPolicy.TimeAndMaterial);
        facts.ServiceBillingModel.Should().Be(ServiceBillingModel.PassThrough);
        facts.EstimatedDurationHours.Should().Be(1.5m);
        facts.UomCode.Should().Be("HR");
        facts.DefaultVariantUuid.Should().Be(entity.Variants.Single().Uuid);
        facts.DefaultVariantSellingPrice.Should().Be(40m);
        (await CatalogProductReader.GetAsync(h.Inventory, Guid.NewGuid(), [service])).Should().BeEmpty("another organization's product is absent");
    }

    [Fact]
    public void The_BOM_endpoints_read_on_inventory_and_write_on_BOM_management()
    {
        // A37 D-11 supersedes A36's MANUFACTURING-or-SERVICES gate: BOM_MANAGEMENT is auto-on with either (MOD-08).
        typeof(BomsController).GetCustomAttribute<RequiresFeatureAttribute>()!.AnyOfFeatureCodes
            .Should().Equal("MODULE_INVENTORY");
        typeof(BomsController).GetMethod(nameof(BomsController.Create))!.GetCustomAttribute<RequiresFeatureAttribute>()!
            .AnyOfFeatureCodes.Should().Equal(ModuleCodes.BomManagement);
    }
}
