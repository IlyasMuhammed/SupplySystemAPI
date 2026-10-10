using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Logistics.Controllers;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Booking;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Logistics.Settlement;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.ModuleRegistry;

/// <summary>
/// A37 (OPSB) — module-aware routes (D-12, RTE-01..03), GET products/{id}/routes, the D-9 job gate on Logistics' jobs,
/// the D-18 impact provider and the §1.3 sub-feature attributes. docs/module-registry/.
/// </summary>
public class ModuleRegistryLogisticsTests
{
    private const int User = 7;

    /// <summary>Everything on except the (organization, code) pairs switched off.</summary>
    internal sealed class FakeGate : IModuleGate
    {
        public readonly HashSet<(Guid, string)> Off = [];
        public FakeGate SwitchOff(Guid org, string code) { Off.Add((org, code)); return this; }
        public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) =>
            Task.FromResult(!Off.Contains((organizationId, featureCode)));
    }

    private sealed class FakeVariants(Guid org, Guid product, IReadOnlyList<ProductVariantRoute> rows) : IProductVariantRoutes
    {
        public Task<IReadOnlyList<ProductVariantRoute>?> GetForProductAsync(
            Guid organizationId, int? productId, Guid? productUuid, CancellationToken ct = default) =>
            Task.FromResult(organizationId == org && (productUuid == product || productId == 42) ? rows : null);
    }

    private sealed class RecordingScheduler : IConsignmentBookingScheduler
    {
        public List<(Guid, Guid)> Enqueued { get; } = [];
        public void Enqueue(Guid consignmentUuid, Guid organizationId) => Enqueued.Add((consignmentUuid, organizationId));
        public void ScheduleRetry(Guid consignmentUuid, Guid organizationId, TimeSpan delay) { }
    }

    private sealed record Harness(FulfillmentRouteService Service, LogisticsDbContext Db, StaticTenantContext Tenant, FakeGate Gate)
    {
        public Guid Org => Tenant.OrganizationId;
        public FulfillmentRoute Route(string code) => Db.FulfillmentRoutes.Single(r => r.Code == code);
    }

    private static async Task<Harness> NewAsync(bool manufacturing, IProductVariantRoutes? variants = null, Guid? org = null)
    {
        var (db, tenant, _) = LogisticsTestDb.New(org);
        var gate = new FakeGate();
        if (!manufacturing) gate.SwitchOff(tenant.OrganizationId, ModuleCodes.Manufacturing);
        await new FulfillmentRouteSeeder(db).EnsureSeededAsync(tenant.OrganizationId);
        return new Harness(new FulfillmentRouteService(db, tenant, [], gate, null, variants), db, tenant, gate);
    }

    private static CreateFulfillmentRouteRequest NewRoute(string code, string category) => new()
    {
        Code = code, Name = code, RouteCategory = category,
        Steps = [new() { StepCode = "PICK", StepOrder = 1 }, new() { StepCode = "GOODS_ISSUE", StepOrder = 2 }]
    };

    // ── RTE-01: availability on the list, refusal on create / category change ─────────────────────────

    [Fact]
    public async Task RTE_01_manufacture_routes_are_unavailable_while_manufacturing_is_off()
    {
        var h = await NewAsync(manufacturing: false);

        var list = await h.Service.GetListAsync(includeInactive: false);

        list.Where(r => r.RouteCategory == "MANUFACTURE").Should().HaveCount(2).And
            .OnlyContain(r => !r.IsAvailable && r.UnavailableReason == "Manufacturing is switched off");
        list.Where(r => r.RouteCategory == "STOCK").Should().OnlyContain(r => r.IsAvailable && r.UnavailableReason == null);
        (await h.Service.GetByUuidAsync(h.Route("MFG_PICK_SHIP").UUID))!.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task RTE_01_everything_is_available_while_manufacturing_is_on()
    {
        var h = await NewAsync(manufacturing: true);
        (await h.Service.GetListAsync(includeInactive: true)).Should().OnlyContain(r => r.IsAvailable);
    }

    [Fact]
    public async Task RTE_01_creating_a_manufacture_route_while_manufacturing_is_off_is_refused()
    {
        var h = await NewAsync(manufacturing: false);

        var act = () => h.Service.CreateAsync(NewRoute("MFG_X", "MANUFACTURE"), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("Manufacturing is switched off for your organization.");
        (await h.Service.CreateAsync(NewRoute("STOCK_X", "STOCK"), User)).IsAvailable.Should().BeTrue("stock routes are unaffected");
    }

    [Fact]
    public async Task RTE_01_changing_a_route_to_manufacture_while_off_is_refused_but_editing_an_existing_one_is_not()
    {
        var h = await NewAsync(manufacturing: false);
        var stock = await h.Service.CreateAsync(NewRoute("STOCK_X", "STOCK"), User);

        var change = () => h.Service.UpdateAsync(stock.Uuid,
            new UpdateFulfillmentRouteRequest { Name = "X", DisplayOrder = 1, RouteCategory = "MANUFACTURE" }, User);
        await change.Should().ThrowAsync<BadRequestException>().WithMessage("Manufacturing is switched off for your organization.");

        var mfg = h.Route("MFG_PICK_SHIP");
        var renamed = await h.Service.UpdateAsync(mfg.UUID,
            new UpdateFulfillmentRouteRequest { Name = "Renamed", DisplayOrder = mfg.DisplayOrder }, User);
        renamed!.Name.Should().Be("Renamed");
        renamed.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task RTE_01_the_lookup_marks_summaries_for_other_modules()
    {
        var h = await NewAsync(manufacturing: false);
        var lookup = new FulfillmentRouteLookup(h.Db, h.Gate);

        var all = await lookup.ListActiveAsync(h.Org);
        all.Single(r => r.Code == "MFG_PICK_SHIP").Should().Match<FulfillmentRouteSummary>(s => !s.IsAvailable && s.UnavailableReason != null);
        all.Single(r => r.Code == "PICK_AND_SHIP").IsAvailable.Should().BeTrue();

        var some = await lookup.GetAsync(h.Org, [h.Route("MFG_PICK_PACK_SHIP").UUID]);
        some.Values.Single().IsAvailable.Should().BeFalse();

        (await new FulfillmentRouteLookup(h.Db).ListActiveAsync(h.Org)).Should().OnlyContain(s => s.IsAvailable,
            "a host without the gate or a snapshot treats every module as on");
    }

    // ── GET /api/products/{id}/routes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Product_routes_show_configured_and_effective_routes_with_the_fallback_warning()
    {
        var org = Guid.NewGuid();
        var product = Guid.NewGuid();
        var (made, stocked, bare) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var rows = new List<ProductVariantRoute>();
        var variants = new FakeVariants(org, product, rows);
        var h = await NewAsync(manufacturing: false, variants, org);
        rows.Add(new(made,    "Made",    "SKU-M", h.Route("MFG_PICK_SHIP").UUID));
        rows.Add(new(stocked, "Stocked", "SKU-S", h.Route("PICK_PACK_SHIP").UUID));
        rows.Add(new(bare,    "Bare",    "SKU-B", null));

        var result = (await h.Service.GetProductRoutesAsync(null, product))!;
        var byUuid = await h.Service.GetProductRoutesAsync(42, null);

        byUuid.Should().HaveCount(3, "the int id reaches the same product");
        var m = result.Single(r => r.VariantUuid == made);
        m.IsAvailable.Should().BeFalse();
        m.Category.Should().Be("MANUFACTURE");
        m.EffectiveRouteName.Should().Be("Pick & Ship", "RTE-02: the SHIP default stands in");
        m.Warning.Should().Be("'MFG_PICK_SHIP' is not available (Manufacturing is switched off); orders use the default route 'PICK_AND_SHIP'.");

        var s = result.Single(r => r.VariantUuid == stocked);
        (s.IsAvailable, s.EffectiveRouteUuid, s.Warning).Should().Be((true, h.Route("PICK_PACK_SHIP").UUID, (string?)null));

        var b = result.Single(r => r.VariantUuid == bare);
        (b.RouteUuid, b.IsAvailable, b.EffectiveRouteUuid, b.Warning).Should().Be(((Guid?)null, true, h.Route("PICK_AND_SHIP").UUID, (string?)null));
    }

    [Fact]
    public async Task Product_routes_warn_when_there_is_no_default_to_fall_back_to()
    {
        var org = Guid.NewGuid();
        var product = Guid.NewGuid();
        var rows = new List<ProductVariantRoute>();
        var h = await NewAsync(manufacturing: false, new FakeVariants(org, product, rows), org);
        rows.Add(new(Guid.NewGuid(), "Made", "SKU-M", h.Route("MFG_PICK_SHIP").UUID));
        await h.Service.ClearDefaultAsync(h.Route("PICK_AND_SHIP").UUID, User);

        var row = (await h.Service.GetProductRoutesAsync(null, product))!.Single();

        row.EffectiveRouteUuid.Should().BeNull();
        row.Warning.Should().Contain("there is no default route");
    }

    [Fact]
    public async Task Product_routes_are_404_for_another_organizations_product_or_without_a_variant_reader()
    {
        var h = await NewAsync(manufacturing: true, new FakeVariants(Guid.NewGuid(), Guid.NewGuid(), []));
        (await h.Service.GetProductRoutesAsync(null, Guid.NewGuid())).Should().BeNull();

        var bare = await NewAsync(manufacturing: true);
        (await bare.Service.GetProductRoutesAsync(1, null)).Should().BeNull();
    }

    // ── D-18 impact ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Impact_counts_open_deliveries_and_consignments_of_the_organization_only()
    {
        var (db, tenant, _) = LogisticsTestDb.New();
        var org = tenant.OrganizationId;
        DeliveryOrder Delivery(Guid o, string status, bool deleted = false) => new()
        {
            UUID = Guid.NewGuid(), OrganizationId = o, DeliveryNumber = $"DLV-{Random.Shared.Next(99_999):D5}",
            Direction = "OUTBOUND", SourceType = "MANUAL", Status = status, IsDelete = deleted
        };
        Consignment Consignment(Guid o, string status) => new()
        {
            UUID = Guid.NewGuid(), OrganizationId = o, ConsignmentNumber = $"SHP-{Random.Shared.Next(99_999):D5}", Status = status
        };
        db.DeliveryOrders.AddRange(Delivery(org, "DRAFT"), Delivery(org, "PICKING"), Delivery(org, "DELIVERED"),
            Delivery(org, "CANCELLED"), Delivery(org, "RELEASED", deleted: true), Delivery(Guid.NewGuid(), "DRAFT"));
        db.Consignments.AddRange(Consignment(org, "IN_TRANSIT"), Consignment(org, "DELIVERED"), Consignment(Guid.NewGuid(), "BOOKED"));
        await db.SaveChangesAsync();

        var impact = new LogisticsModuleImpact(db);
        var items = await impact.GetInProgressAsync(org);

        impact.ModuleCode.Should().Be("MODULE_LOGISTICS");
        items.Should().BeEquivalentTo([new ModuleImpactItem("Open deliveries", 2), new ModuleImpactItem("Open consignments", 1)]);
    }

    // ── D-9 job gate ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Booking_sweep_skips_stalled_consignments_of_an_organization_without_logistics()
    {
        var (db, tenant, _) = LogisticsTestDb.New(isSuperAdmin: true);
        var (on, off) = (Guid.NewGuid(), Guid.NewGuid());
        var old = DateTime.UtcNow.AddHours(-2);
        Consignment Stalled(Guid o) => new()
        {
            UUID = Guid.NewGuid(), OrganizationId = o, ConsignmentNumber = $"SHP-{Random.Shared.Next(99_999):D5}",
            Status = "BOOKING", CreatedDate = old, ModifiedDate = old
        };
        var (kept, skipped) = (Stalled(on), Stalled(off));
        db.Consignments.AddRange(kept, skipped);
        await db.SaveChangesAsync();

        var registry = new CourierProviderRegistry([new ManualCourierProvider()]);
        var scheduler = new RecordingScheduler();
        var job = new ConsignmentBookingSweepJob(db, new CarrierCommandLedger(db, tenant, registry), registry, scheduler,
            NullLogger<ConsignmentBookingSweepJob>.Instance, new FakeGate().SwitchOff(off, ModuleCodes.Logistics));

        var (_, requeued) = await job.SweepAsync();

        requeued.Should().Be(1);
        scheduler.Enqueued.Should().Equal((kept.UUID, on));
    }

    [Fact]
    public async Task Freight_sweep_runs_per_enabled_organization_when_one_is_switched_off()
    {
        var (a, b, off) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var seen = new List<Guid?>();
        var accruals = new Mock<IFreightAccrualService>();
        accruals.Setup(s => s.SweepAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => seen.Add(HangfireTenantScope.OrganizationId))
            .ReturnsAsync(new FreightAccrualSweepResult(0, 0, []));
        var cod = new Mock<ICodReconciliationService>();
        var directory = new Mock<IOrganizationDirectory>();
        directory.Setup(d => d.GetOrganizationIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([a, off, b]);

        await new FreightAccrualSweepJob(accruals.Object, cod.Object, new StaticTenantContext(), NullLogger<FreightAccrualSweepJob>.Instance,
            new FakeGate().SwitchOff(off, ModuleCodes.Logistics), directory.Object).RunAsync();

        seen.Should().Equal(a, b);
        HangfireTenantScope.OrganizationId.Should().BeNull("the scope is cleared after each organization");
    }

    [Fact]
    public async Task Freight_sweep_runs_once_unscoped_when_every_organization_has_logistics_or_there_is_no_gate()
    {
        var seen = new List<Guid?>();
        var accruals = new Mock<IFreightAccrualService>();
        accruals.Setup(s => s.SweepAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => seen.Add(HangfireTenantScope.OrganizationId))
            .ReturnsAsync(new FreightAccrualSweepResult(0, 0, []));
        var directory = new Mock<IOrganizationDirectory>();
        directory.Setup(d => d.GetOrganizationIdsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Guid.NewGuid(), Guid.NewGuid()]);
        var log = NullLogger<FreightAccrualSweepJob>.Instance;

        await new FreightAccrualSweepJob(accruals.Object, Mock.Of<ICodReconciliationService>(), new StaticTenantContext(), log,
            new FakeGate(), directory.Object).RunAsync();
        await new FreightAccrualSweepJob(accruals.Object, Mock.Of<ICodReconciliationService>(), new StaticTenantContext(), log).RunAsync();

        seen.Should().HaveCount(2).And.OnlyContain(o => o == null);
    }

    [Fact]
    public async Task The_gate_helpers_treat_a_missing_gate_as_everything_on()
    {
        IModuleGate? none = null;
        (await none.IsOnAsync(Guid.NewGuid(), ModuleCodes.Logistics)).Should().BeTrue();
        (await none.SkippedAmongAsync([Guid.NewGuid()], ModuleCodes.Logistics, NullLogger.Instance, "job")).Should().BeEmpty();

        var off = Guid.NewGuid();
        var gate = new FakeGate().SwitchOff(off, ModuleCodes.Logistics);
        (await gate.SkippedAmongAsync([off, Guid.NewGuid(), off], ModuleCodes.Logistics, NullLogger.Instance, "job"))
            .Should().BeEquivalentTo([off]);
    }

    // ── §1.3 sub-feature gates ───────────────────────────────────────────────────────────────────────

    public static TheoryData<Type, string, string> Gates => new()
    {
        { typeof(PickListsController),     "MODULE_LOGISTICS", "FEATURE_PICK_LISTS" },
        { typeof(ConsignmentsController),  "MODULE_LOGISTICS", "FEATURE_SHIPMENT_TRACKING" },
        { typeof(TrackingLinksController), "MODULE_LOGISTICS", "FEATURE_SHIPMENT_TRACKING" },
        { typeof(SMS.Modules.Warehouse.Controllers.SrosController), "MODULE_WAREHOUSE", "FEATURE_PURCHASE_RETURNS" },
        { typeof(SMS.Modules.Demand.Controllers.QuotationsController), "MODULE_DEMAND", "FEATURE_RFQ_MANAGEMENT" },
    };

    [Theory]
    [MemberData(nameof(Gates))]
    public void Sub_feature_gates_sit_next_to_the_module_gate(Type controller, string module, string feature) =>
        controller.GetCustomAttributes<RequiresFeatureAttribute>().Select(a => a.FeatureCode)
            .Should().BeEquivalentTo([module, feature]);

    [Theory]
    [InlineData(typeof(DeliveriesController))]
    [InlineData(typeof(PublicTrackingController))]
    [InlineData(typeof(CarrierWebhooksController))]
    [InlineData(typeof(SMS.Modules.Demand.Controllers.RfqPortalController))]
    public void Deliveries_public_tracking_webhooks_and_the_rfq_portal_carry_no_sub_feature_gate(Type controller) =>
        controller.GetCustomAttributes<RequiresFeatureAttribute>().Select(a => a.FeatureCode)
            .Should().NotContain(c => c.StartsWith("FEATURE_"));

    [Fact]
    public void Product_routes_endpoint_is_logistics_gated_and_needs_route_view()
    {
        typeof(ProductRoutesController).GetCustomAttributes<RequiresFeatureAttribute>().Single().FeatureCode.Should().Be("MODULE_LOGISTICS");
        var get = typeof(ProductRoutesController).GetMethod(nameof(ProductRoutesController.GetByUuid))!;
        get.GetCustomAttribute<RequirePermissionAttribute>()!.AnyOf.Should().Contain("FULFILLMENT_ROUTE_VIEW");
        get.GetCustomAttribute<Microsoft.AspNetCore.Mvc.HttpGetAttribute>()!.Template.Should().Be("api/products/{uuid:guid}/routes");
    }
}
