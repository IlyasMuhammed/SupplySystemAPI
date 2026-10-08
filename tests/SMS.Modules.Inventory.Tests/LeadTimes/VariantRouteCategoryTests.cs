using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Repositories;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Inventory.Tests.LeadTimeKit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A34-PA-06/07/11 (T-C2-01, T-C2-02 adapted, D-3, D-4, D-9; API-CONTRACT §4.1/§4.2): a MANUFACTURE route may only go
/// to a variant of a MANUFACTURE product in an organization with MODULE_MANUFACTURING; product flags are never written
/// from a route; the variant model carries the route category, the "make to order" flag and the lead-time overrides.
/// The bulk assign's narrowing runs on SQL Server in <see cref="VariantRouteCategoryBulkTests"/>.
/// </summary>
public class VariantRouteCategoryTests
{
    private static readonly Guid OrgA = Guid.NewGuid();

    private static VariantFulfillmentRouteService Service(string name, FakeFulfillmentRouteLookup routes, FakeTenantSnapshots? tenants) =>
        new(Db(name, OrgA), routes, tenants);

    [Fact]
    public async Task T_C2_01_a_manufacture_route_goes_to_a_manufactured_variant_and_leaves_the_product_flags_alone()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var mfg = routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var v = await VariantAsync(Db(name, OrgA), SupplyMethod.Manufacture);

        var result = await Service(name, routes, FakeTenantSnapshots.With(OrgA, "MODULE_MANUFACTURING")).SetRouteAsync(v.Uuid, mfg.Uuid);

        result.FulfillmentRouteUuid.Should().Be(mfg.Uuid);
        var product = await Db(name, OrgA).Products.SingleAsync();
        product.SupplyMethod.Should().Be(SupplyMethod.Manufacture);
        product.IsManufacturable.Should().BeTrue("D-3: a MANUFACTURE route always implies isManufacturable");
    }

    [Fact]
    public async Task D_3_a_manufacture_route_on_a_purchased_product_is_refused_and_nothing_changes()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var mfg = routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var v = await VariantAsync(Db(name, OrgA), SupplyMethod.Purchase);

        var act = () => Service(name, routes, FakeTenantSnapshots.With(OrgA, "MODULE_MANUFACTURING")).SetRouteAsync(v.Uuid, mfg.Uuid);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Be(
            "Set the product's supply method to MANUFACTURE first: only manufactured products can use the make-to-order route 'MFG_PICK_SHIP'.");
        (await Db(name, OrgA).ProductVariants.SingleAsync()).FulfillmentRouteUuid.Should().BeNull();
        (await Db(name, OrgA).Products.SingleAsync()).SupplyMethod.Should().Be(SupplyMethod.Purchase, "D-3: never written from a route");
    }

    [Fact]
    public async Task D_9_a_manufacture_route_is_refused_when_the_org_lacks_MODULE_MANUFACTURING()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var mfg = routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var v = await VariantAsync(Db(name, OrgA), SupplyMethod.Manufacture);

        var act = () => Service(name, routes, FakeTenantSnapshots.With(OrgA, "MODULE_INVENTORY", "MODULE_LOGISTICS")).SetRouteAsync(v.Uuid, mfg.Uuid);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Be(
            "Manufacturing is not enabled for your organization, so the make-to-order route 'MFG_PICK_SHIP' can't be assigned.");
        (await Db(name, OrgA).ProductVariants.SingleAsync()).FulfillmentRouteUuid.Should().BeNull();
    }

    [Fact]
    public async Task T_C2_02_adapted_clearing_or_switching_to_a_stock_route_always_works_and_keeps_the_flags()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var mfg = routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var stock = routes.Add(OrgA, "PICK_AND_SHIP");
        var v = await VariantAsync(Db(name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid);
        var noManufacturing = FakeTenantSnapshots.With(OrgA, "MODULE_INVENTORY");

        await Service(name, routes, noManufacturing).SetRouteAsync(v.Uuid, stock.Uuid);
        (await Db(name, OrgA).ProductVariants.SingleAsync()).FulfillmentRouteUuid.Should().Be(stock.Uuid);
        await Service(name, routes, noManufacturing).SetRouteAsync(v.Uuid, null);
        (await Db(name, OrgA).ProductVariants.SingleAsync()).FulfillmentRouteUuid.Should().BeNull();

        var product = await Db(name, OrgA).Products.SingleAsync();
        product.SupplyMethod.Should().Be(SupplyMethod.Manufacture);
        product.IsManufacturable.Should().BeTrue("D-3: flags unchanged (BOMs kept, D-4)");
    }

    [Fact]
    public async Task A_stock_route_on_a_purchased_product_is_unaffected_by_the_new_rules()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var stock = routes.Add(OrgA, "PICK_AND_SHIP");
        var v = await VariantAsync(Db(name, OrgA), SupplyMethod.Purchase);

        await Service(name, routes, FakeTenantSnapshots.With(OrgA, "MODULE_INVENTORY")).SetRouteAsync(v.Uuid, stock.Uuid);

        (await Db(name, OrgA).ProductVariants.SingleAsync()).FulfillmentRouteUuid.Should().Be(stock.Uuid);
    }

    [Fact]
    public async Task Bulk_assign_of_a_manufacture_route_is_refused_without_MODULE_MANUFACTURING()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var mfg = routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var seeded = await RouteSeed.SeedAsync(Db(name, OrgA));

        var act = () => Service(name, routes, FakeTenantSnapshots.With(OrgA, "MODULE_INVENTORY"))
            .AssignByCategoryAsync(mfg.Uuid, new AssignRouteByCategoryRequest { CategoryId = seeded.CategoryId });

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("Manufacturing is not enabled");
    }

    // ── §4.1 the variant model (D-4) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_product_detail_carries_each_variants_route_category_make_to_order_flag_and_overrides()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var mfg = routes.Add(OrgA, "MFG_PICK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var v = await VariantAsync(Db(name, OrgA), SupplyMethod.Manufacture, route: mfg.Uuid, configure: x =>
        {
            x.LeadTimeDays = 1; x.ManufacturingLeadTimeDays = 2; x.ManufacturingBufferDays = 3; x.QualityInspectionDays = 4;
            x.InternalTransferDays = 5; x.PickPackDays = 6; x.ShippingLeadTimeDays = 7; x.SalesBufferDays = 8;
        });
        await using var db = Db(name, OrgA);
        var product = await db.ProductVariants.Where(x => x.Uuid == v.Uuid).Select(x => x.Product).SingleAsync();
        var stockRoute = routes.Add(OrgA, "PICK_ONLY");
        db.ProductVariants.Add(new ProductVariant { Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = "S2", VariantName = "B", IsActive = true, FulfillmentRouteUuid = stockRoute.Uuid, SortOrder = 1 });
        db.ProductVariants.Add(new ProductVariant { Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = "S3", VariantName = "C", IsActive = true, SortOrder = 2 });
        await db.SaveChangesAsync();
        var service = new InventoryService(
            new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object), new ProductSearchIndexService(db),
            new Mock<IBackgroundJobClient>().Object, [], routeLookup: routes);

        var detail = await service.GetProductByIdAsync(product.Id);

        var made = detail!.Variants.Single(x => x.Uuid == v.Uuid);
        made.FulfillmentRouteCategory.Should().Be(FulfillmentRouteCategory.Manufacture);
        made.IsMakeToOrder.Should().BeTrue();
        new int?[] { made.SupplierLeadTimeDays, made.ManufacturingLeadTimeDays, made.ManufacturingBufferDays, made.QualityInspectionDays,
                     made.InternalTransferDays, made.PickPackDays, made.ShippingLeadTimeDays, made.SalesBufferDays }
            .Should().Equal(1, 2, 3, 4, 5, 6, 7, 8);
        var stocked = detail.Variants.Single(x => x.Sku == "S2");
        stocked.FulfillmentRouteCategory.Should().Be(FulfillmentRouteCategory.Stock);
        stocked.IsMakeToOrder.Should().BeFalse();
        var unrouted = detail.Variants.Single(x => x.Sku == "S3");
        unrouted.FulfillmentRouteCategory.Should().BeNull();
        unrouted.IsMakeToOrder.Should().BeFalse();
        unrouted.PickPackDays.Should().BeNull();
    }

    [Fact]
    public void The_ungated_variant_PATCH_request_has_no_lead_time_fields()
    {
        typeof(CreateProductVariantRequest).GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.EndsWith("Days", StringComparison.Ordinal), "§4.1: edited only through the gated lead-times PUT");
    }
}

/// <summary>Stands in for Tenancy's <see cref="ITenantSnapshotProvider"/>: each organization's enabled feature codes.</summary>
internal sealed class FakeTenantSnapshots : ITenantSnapshotProvider
{
    private readonly Dictionary<Guid, TenantSnapshot> _snapshots = [];

    public static FakeTenantSnapshots With(Guid org, params string[] features)
    {
        var fake = new FakeTenantSnapshots();
        fake._snapshots[org] = new TenantSnapshot(true, features.ToHashSet());
        return fake;
    }

    public Task<TenantSnapshot?> GetSnapshotAsync(Guid organizationId) =>
        Task.FromResult(_snapshots.GetValueOrDefault(organizationId));

    public void Invalidate(Guid organizationId) { }
}
