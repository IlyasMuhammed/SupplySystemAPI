using System.Text.Json;
using FluentAssertions;
using Hangfire;
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

/// <summary>
/// A37 (OPSA) — D-10 product capability fields and the module-conditional detail blocks (PRD-CAP-03), D-16 ModifiedAt
/// stamping, and the §6 catalog sync (strictly-after filter, limit / hasMore / nextSince).
/// </summary>
public class ModuleRegistryProductTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static (InventoryDbContext Db, InventoryRepository Repo) NewRepo()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        return (db, new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object));
    }

    private static InventoryService ServiceWith(InventoryDbContext db, IModuleGate? gate, IBomStructureReader? reader = null) =>
        new(new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object), new ProductSearchIndexService(db),
            new Mock<IBackgroundJobClient>().Object, [], tenantContext: new StaticTenantContext(), bomReader: reader, modules: gate);

    private static IModuleGate Gate(bool manufacturing, bool services)
    {
        var gate = new Mock<IModuleGate>();
        gate.Setup(g => g.IsEnabledAsync(It.IsAny<Guid>(), ModuleCodes.Manufacturing, It.IsAny<CancellationToken>())).ReturnsAsync(manufacturing);
        gate.Setup(g => g.IsEnabledAsync(It.IsAny<Guid>(), ModuleCodes.Services, It.IsAny<CancellationToken>())).ReturnsAsync(services);
        return gate.Object;
    }

    private static CreateProductRequest Service(string name, string? category = null, bool? siteVisit = null) => new()
    {
        Name = name, ProductType = ProductType.Service, PurchasePrice = 0m, UomCode = "HR",
        ServiceCategory = category, RequiresSiteVisit = siteVisit
    };

    // ── D-10 service category / site visit ───────────────────────────────────

    [Fact]
    public async Task A_service_keeps_its_category_and_site_visit_and_reads_as_serviceable()
    {
        var (_, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(Service("AC Install", " installation ", true), 1);

        var detail = (await repo.GetProductByIdAsync(id))!;

        detail.ServiceCategory.Should().Be(ServiceCategory.Installation);
        detail.RequiresSiteVisit.Should().BeTrue();
        detail.IsServiceable.Should().BeTrue();
    }

    [Fact]
    public async Task Category_and_site_visit_are_refused_on_a_non_service_product()
    {
        var (_, repo) = NewRepo();

        await FluentActions.Invoking(() => repo.CreateProductAsync(
                new CreateProductRequest { Name = "Bolt", PurchasePrice = 1m, ServiceCategory = ServiceCategory.Repair }, 1))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Service category is only applicable to service products");
        var (bolt, _) = await repo.CreateProductAsync(new CreateProductRequest { Name = "Bolt", PurchasePrice = 1m }, 1);
        await FluentActions.Invoking(() => repo.PatchProductAsync(bolt, new PatchProductRequest { RequiresSiteVisit = true }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Site visit is only applicable to service products");
        await FluentActions.Invoking(() => repo.PatchProductAsync(bolt, new PatchProductRequest { ServiceCategory = "REPAIR" }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Service category is only applicable to service products");

        (await repo.GetProductByIdAsync(bolt))!.IsServiceable.Should().BeFalse();
        (await repo.PatchProductAsync(bolt, new PatchProductRequest { ServiceCategory = null, RequiresSiteVisit = false }))
            .Should().BeTrue("null and false are not values worth refusing");
    }

    [Fact]
    public async Task An_unknown_category_is_refused()
    {
        var (_, repo) = NewRepo();
        await FluentActions.Invoking(() => repo.CreateProductAsync(Service("X", "PLUMBING"), 1))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not a service category*");
    }

    [Fact]
    public async Task A_patch_overlays_clears_with_explicit_null_and_a_type_change_drops_both()
    {
        var (_, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(Service("Survey", ServiceCategory.Consulting, true), 1);

        await repo.PatchProductAsync(id, JsonSerializer.Deserialize<PatchProductRequest>("""{"name":"Site Survey"}""", Web)!);
        (await repo.GetProductByIdAsync(id))!.ServiceCategory.Should().Be(ServiceCategory.Consulting, "omitted = unchanged");

        await repo.PatchProductAsync(id, JsonSerializer.Deserialize<PatchProductRequest>("""{"serviceCategory":null}""", Web)!);
        var cleared = (await repo.GetProductByIdAsync(id))!;
        cleared.ServiceCategory.Should().BeNull();
        cleared.RequiresSiteVisit.Should().BeTrue();

        await repo.PatchProductAsync(id, new PatchProductRequest { ProductType = ProductType.Consumable, SupplyMethod = SupplyMethod.Purchase, IsStockable = true });
        (await repo.GetProductByIdAsync(id))!.RequiresSiteVisit.Should().BeFalse();
    }

    // ── PRD-CAP-03 — conditional keys in the actual JSON ─────────────────────

    [Fact]
    public async Task With_both_modules_off_the_settings_keys_are_absent_not_null()
    {
        var (db, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(Service("Repair", ServiceCategory.Repair), 1);

        var detail = (await ServiceWith(db, Gate(false, false)).GetProductByIdAsync(id))!;
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(detail, Web));

        json.RootElement.TryGetProperty("productionSettings", out _).Should().BeFalse();
        json.RootElement.TryGetProperty("serviceSettings", out _).Should().BeFalse();
        json.RootElement.GetProperty("isServiceable").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("serviceCategory").GetString().Should().Be("REPAIR", "flat fields stay");
        json.RootElement.TryGetProperty("organizationId", out _).Should().BeFalse("internal");
    }

    [Fact]
    public async Task Each_block_appears_only_with_its_module()
    {
        var (db, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(Service("Repair", ServiceCategory.Repair, true), 1);

        using var servicesOnly = JsonDocument.Parse(JsonSerializer.Serialize(
            (await ServiceWith(db, Gate(false, true)).GetProductByIdAsync(id))!, Web));
        servicesOnly.RootElement.TryGetProperty("productionSettings", out _).Should().BeFalse();
        var svc = servicesOnly.RootElement.GetProperty("serviceSettings");
        svc.GetProperty("serviceCategory").GetString().Should().Be("REPAIR");
        svc.GetProperty("requiresSiteVisit").GetBoolean().Should().BeTrue();

        using var manufacturingOnly = JsonDocument.Parse(JsonSerializer.Serialize(
            (await ServiceWith(db, Gate(true, false)).GetProductByIdAsync(id))!, Web));
        manufacturingOnly.RootElement.TryGetProperty("serviceSettings", out _).Should().BeFalse();
        manufacturingOnly.RootElement.GetProperty("productionSettings").GetProperty("supplyMethod").GetString()
            .Should().Be(SupplyMethod.Service);
    }

    [Fact]
    public async Task Without_a_module_gate_in_the_host_both_blocks_are_present()
    {
        var (db, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(new CreateProductRequest { Name = "Bolt", PurchasePrice = 1m }, 1);

        var detail = (await ServiceWith(db, gate: null).GetProductByIdAsync(id))!;

        detail.ProductionSettings.Should().NotBeNull();
        detail.ServiceSettings.Should().NotBeNull();
    }

    [Fact]
    public async Task Production_settings_carry_the_active_BOM_and_the_lead_time()
    {
        var (db, repo) = NewRepo();
        var (id, _) = await repo.CreateProductAsync(new CreateProductRequest
        {
            Name = "Table", PurchasePrice = 0m, ProductType = ProductType.FinishedGood, SupplyMethod = SupplyMethod.Manufacture,
            LeadTimeDays = 4
        }, 1);
        var variant = db.ProductVariants.Single(v => v.ProductId == id);
        var bomUuid = Guid.NewGuid();
        var reader = new Mock<IBomStructureReader>();
        reader.Setup(r => r.GetActiveBomsAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new Dictionary<Guid, BomStructure> { [variant.Uuid] = new(bomUuid, "BOM-2026-00009", 2, 1m, []) });

        var settings = (await ServiceWith(db, Gate(true, false), reader.Object).GetProductByIdAsync(id))!.ProductionSettings!;

        settings.ActiveBomUuid.Should().Be(bomUuid);
        settings.ActiveBomNumber.Should().Be("BOM-2026-00009");
        settings.ManufacturingLeadTimeDays.Should().Be(4, "no variant override: the product's days");

        variant.ManufacturingLeadTimeDays = 7;
        await db.SaveChangesAsync();
        (await ServiceWith(db, Gate(true, false), reader.Object).GetProductByIdAsync(id))!
            .ProductionSettings!.ManufacturingLeadTimeDays.Should().Be(7, "the default variant's override wins");
    }

    // ── D-16 ModifiedAt ───────────────────────────────────────────────────────

    [Fact]
    public async Task ModifiedAt_is_stamped_on_insert_and_moves_on_every_update()
    {
        var (db, repo) = NewRepo();
        var before = DateTime.UtcNow.AddSeconds(-1);
        var (id, _) = await repo.CreateProductAsync(new CreateProductRequest { Name = "Bolt", PurchasePrice = 1m }, 1);
        var product = db.Products.Single(p => p.Id == id);
        var variant = db.ProductVariants.Single(v => v.ProductId == id);
        var category = new ProductCategory { Name = "Hardware", Code = "HW" };
        var warehouse = new Domain.Warehouse { Code = "W1", Name = "Main" };
        db.AddRange(category, warehouse);
        await db.SaveChangesAsync();

        foreach (var row in new IHasModifiedAt[] { product, variant, category, warehouse })
            row.ModifiedAt.Should().BeAfter(before);

        var stamped = (product.ModifiedAt, variant.ModifiedAt, category.ModifiedAt, warehouse.ModifiedAt);
        await Task.Delay(20);
        await repo.PatchProductAsync(id, new PatchProductRequest { Brand = "Acme" });
        variant.SellingPrice = 2m; category.Name = "Tools"; warehouse.Name = "Main DC";
        await db.SaveChangesAsync();

        product.ModifiedAt.Should().BeAfter(stamped.Item1);
        variant.ModifiedAt.Should().BeAfter(stamped.Item2);
        category.ModifiedAt.Should().BeAfter(stamped.Item3);
        warehouse.ModifiedAt.Should().BeAfter(stamped.Item4);
        product.ModifiedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    // ── D-12 (OPSB's needs from Inventory) ───────────────────────────────────

    private static InventoryDbContext Db(string name, Guid org) => new(
        new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options, new StaticTenantContext { OrganizationId = org });

    [Fact]
    public async Task RTE_01_a_route_whose_module_is_off_cannot_be_assigned_singly_or_in_bulk()
    {
        var name = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();
        var routes = new FakeFulfillmentRouteLookup();
        var off = routes.Add(org, "MAKE_TO_ORDER", category: FulfillmentRouteCategory.Manufacture, isAvailable: false);
        var seeded = await RouteSeed.SeedAsync(Db(name, org));

        await FluentActions.Invoking(() => new VariantFulfillmentRouteService(Db(name, org), routes).SetRouteAsync(seeded.VariantUuid, off.Uuid))
            .Should().ThrowAsync<BadRequestException>().WithMessage(FulfillmentRouteAvailability.ManufacturingOffMessage);
        await FluentActions.Invoking(() => new VariantFulfillmentRouteService(Db(name, org), routes)
                .AssignByCategoryAsync(off.Uuid, new AssignRouteByCategoryRequest { CategoryId = seeded.CategoryId }))
            .Should().ThrowAsync<BadRequestException>().WithMessage(FulfillmentRouteAvailability.ManufacturingOffMessage);
    }

    [Fact]
    public async Task D_27_a_products_active_variants_and_routes_by_id_or_uuid_and_absent_for_another_org()
    {
        var name = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();
        var route = Guid.NewGuid();
        var seeded = await RouteSeed.SeedAsync(Db(name, org), route: route, extraVariantWithoutRoute: true);
        await using var db = Db(name, org);
        IProductVariantRoutes reader = new VariantFulfillmentRoutes(db);
        var uuid = db.Products.Single(p => p.Id == seeded.ProductId).Uuid;

        var byId = await reader.GetForProductAsync(org, seeded.ProductId, null);
        byId!.Select(v => (v.VariantUuid, v.RouteUuid)).Should().Equal((seeded.VariantUuid, route), (seeded.OtherVariantUuid!.Value, null));
        (await reader.GetForProductAsync(org, null, uuid)).Should().BeEquivalentTo(byId);
        (await reader.GetForProductAsync(Guid.NewGuid(), seeded.ProductId, null)).Should().BeNull();
    }

    // ── §6 catalog sync ───────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_returns_only_rows_modified_strictly_after_since()
    {
        var (db, _) = NewRepo();
        var first = new ProductCategory { Name = "A", Code = "A" };
        db.ProductCategories.Add(first);
        await db.SaveChangesAsync();
        await Task.Delay(20);
        db.ProductCategories.Add(new ProductCategory { Name = "B", Code = "B" });
        await db.SaveChangesAsync();

        var sync = new CatalogSyncService(db);
        (await sync.GetChangesAsync(null, null)).Categories.Select(c => c.Code).Should().Equal("A", "B");
        var delta = await sync.GetChangesAsync(first.ModifiedAt, null);

        delta.Categories.Select(c => c.Code).Should().Equal(["B"], "a row stamped exactly at since is not sent again");
        delta.HasMore.Should().BeFalse();
        delta.NextSince.Should().Be(delta.ServerTime);
        delta.TaxCodes.Should().BeEmpty("no Lookups reader in this host");
    }

    [Fact]
    public async Task Sync_pages_by_limit_with_hasMore_and_nextSince()
    {
        var (db, repo) = NewRepo();
        foreach (var name in new[] { "P1", "P2", "P3" })
        {
            await repo.CreateProductAsync(new CreateProductRequest { Name = name, PurchasePrice = 1m }, 1);
            await Task.Delay(20);
        }
        var sync = new CatalogSyncService(db);

        var page1 = await sync.GetChangesAsync(null, 2);
        page1.Products.Select(p => p.Name).Should().Equal("P1", "P2");
        page1.HasMore.Should().BeTrue();
        page1.NextSince.Should().Be(page1.Products[1].ModifiedAt);

        var page2 = await sync.GetChangesAsync(page1.NextSince, 2);
        page2.Products.Select(p => p.Name).Should().Equal("P3");
        page2.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Sync_reads_tax_codes_and_units_through_the_lookup_reader()
    {
        var (db, _) = NewRepo();
        var stamp = DateTime.UtcNow;
        var reader = new Mock<ISyncLookupReader>();
        reader.Setup(r => r.GetChangedAsync(ISyncLookupReader.Uoms, null, 500, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new SyncLookupPage([new(Guid.NewGuid(), "Piece (PCS)", null, true, 1, true, stamp)], true));
        reader.Setup(r => r.GetChangedAsync(ISyncLookupReader.TaxCodes, null, 500, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new SyncLookupPage([], false));

        var result = await new CatalogSyncService(db, reader.Object).GetChangesAsync(null, null);

        result.Uoms.Should().ContainSingle(u => u.Name == "Piece (PCS)");
        result.HasMore.Should().BeTrue();
        result.NextSince.Should().Be(stamp);
    }
}
