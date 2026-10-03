using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A33 Phase C (DEM): the sale order line's fulfillment route, the effective-route resolver (BR-C3-03, D-4, D-5),
/// the confirmation gate (BR-C3-02, T-C3-05..08), the delivery preview (BR-C3-05, T-C3-09), the D-16 snapshot, the calls
/// to Logistics' delivery creator (D-1, after the confirm commits) and canceller (D-15), the D-12 sweep (REV-01),
/// "open sale order lines" usage (L-7) and own-organization reads. Logistics, Inventory and Tenancy are fakes here.
/// </summary>
public class SaleOrderFulfillmentRouteTests
{
    private const int User = 7;
    private static readonly Guid Currency = Guid.NewGuid();

    // ── fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeRouteLookup : IFulfillmentRouteLookup
    {
        public readonly Dictionary<Guid, (Guid Org, FulfillmentRouteSummary Route)> Routes = [];
        public readonly Dictionary<Guid, FulfillmentRouteDefaults> Defaults = [];
        public int GetCalls;
        public int DefaultsCalls;

        public FulfillmentRouteSummary Add(Guid org, string code, params string[] steps)
        {
            var route = new FulfillmentRouteSummary(Guid.NewGuid(), code, code.Replace('_', ' '), true, false, false,
                steps.Contains(FulfillmentStepCode.Pack), steps.Contains(FulfillmentStepCode.Ship), steps);
            Routes[route.Uuid] = (org, route);
            return route;
        }

        public void Deactivate(Guid uuid) => Routes[uuid] = (Routes[uuid].Org, Routes[uuid].Route with { IsActive = false });

        public Task<IReadOnlyDictionary<Guid, FulfillmentRouteSummary>> GetAsync(
            Guid organizationId, IReadOnlyCollection<Guid> routeUuids, CancellationToken ct = default)
        {
            GetCalls++;
            IReadOnlyDictionary<Guid, FulfillmentRouteSummary> found = Routes
                .Where(r => r.Value.Org == organizationId && routeUuids.Contains(r.Key))
                .ToDictionary(r => r.Key, r => r.Value.Route);
            return Task.FromResult(found);
        }

        public Task<FulfillmentRouteDefaults> GetOrgDefaultsAsync(Guid organizationId, CancellationToken ct = default)
        {
            DefaultsCalls++;
            return Task.FromResult(Defaults.GetValueOrDefault(organizationId) ?? new FulfillmentRouteDefaults(null, null));
        }

        public Task<IReadOnlyList<FulfillmentRouteSummary>> ListActiveAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FulfillmentRouteSummary>>(
                Routes.Values.Where(r => r.Org == organizationId && r.Route.IsActive).Select(r => r.Route).ToList());
    }

    private sealed class FakeVariantRoutes : IVariantFulfillmentRoutes
    {
        public readonly Dictionary<(Guid Org, Guid Variant), Guid> Routes = [];
        public int Calls;

        public Task<IReadOnlyDictionary<Guid, Guid>> GetRouteUuidsAsync(
            Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
        {
            Calls++;
            IReadOnlyDictionary<Guid, Guid> found = Routes
                .Where(r => r.Key.Org == organizationId && variantUuids.Contains(r.Key.Variant))
                .ToDictionary(r => r.Key.Variant, r => r.Value);
            return Task.FromResult(found);
        }
    }

    private sealed class FakeTenants : ITenantSnapshotProvider
    {
        public readonly HashSet<Guid> WithLogistics = [];

        public Task<TenantSnapshot?> GetSnapshotAsync(Guid organizationId)
        {
            var features = new HashSet<string> { "MODULE_DEMAND" };
            if (WithLogistics.Contains(organizationId)) features.Add("MODULE_LOGISTICS");
            return Task.FromResult<TenantSnapshot?>(new TenantSnapshot(true, features));
        }

        public void Invalidate(Guid organizationId) { }
    }

    // ── harness ─────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public required DemandDbContext Db { get; init; }
        public required SaleOrderService Service { get; init; }
        public required Guid OrgId { get; init; }
        public required string DbName { get; init; }
        public required FakeRouteLookup Routes { get; init; }
        public required FakeVariantRoutes VariantRoutes { get; init; }
        public required FakeTenants Tenants { get; init; }
        public required Mock<ISaleOrderDeliveryCreator> Creator { get; init; }
        public required Mock<ISaleOrderDeliveryCanceller> Canceller { get; init; }
        public required Mock<IAvailabilityCheckService> Availability { get; init; }
        public required Mock<IStockReservationService> Stock { get; init; }
        public required Mock<IPricingService> Pricing { get; init; }
        public required List<Job> Jobs { get; init; }
        public required List<string> Calls { get; init; }
        public required FulfillmentRouteSummary PickOnly { get; init; }
        public required FulfillmentRouteSummary PickAndShip { get; init; }
        public required FulfillmentRouteSummary PickPackShip { get; init; }

        public IEnumerable<string> TimelineTypes => Jobs.SelectMany(j => j.Args.OfType<TimelineEvent>()).Select(e => e.EventType);
        public IEnumerable<TimelineEvent> Timeline => Jobs.SelectMany(j => j.Args.OfType<TimelineEvent>());
    }

    private static Harness NewHarness(
        bool logistics = true, string? dbName = null, Guid? org = null, bool superAdmin = false,
        FakeRouteLookup? routes = null, FakeVariantRoutes? variantRoutes = null, FakeTenants? tenants = null)
    {
        dbName ??= Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = org ?? Guid.NewGuid(), IsSuperAdmin = superAdmin };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        routes        ??= new FakeRouteLookup();
        variantRoutes ??= new FakeVariantRoutes();
        tenants       ??= new FakeTenants();
        if (logistics) tenants.WithLogistics.Add(tenant.OrganizationId);

        var pickOnly     = routes.Add(tenant.OrganizationId, "PICK_ONLY", "PICK", "GOODS_ISSUE");
        var pickAndShip  = routes.Add(tenant.OrganizationId, "PICK_AND_SHIP", "PICK", "GOODS_ISSUE", "SHIP");
        var pickPackShip = routes.Add(tenant.OrganizationId, "PICK_PACK_SHIP", "PICK", "PACK", "GOODS_ISSUE", "SHIP");
        routes.Defaults[tenant.OrganizationId] = new FulfillmentRouteDefaults(pickAndShip, pickOnly);

        var calls = new List<string>();
        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Currency);
        var numbers = new Mock<IDocumentNumberGenerator>();
        var n = 0;
        numbers.Setup(x => x.NextAsync("SO", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"SO-2026-{++n:00000}");
        var pricing = new Mock<IPricingService>();
        pricing.Setup(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalePriceResolution(true, 10m, Currency, PriceResolutionTier.DefaultSelling, null));
        var stock = new Mock<IStockReservationService>();
        stock.Setup(s => s.GetBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReservationSummary>());
        stock.Setup(s => s.ReleaseBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("release")).ReturnsAsync(0);
        var jobs = new List<Job>();
        var jobClient = new Mock<IBackgroundJobClient>();
        jobClient.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job, IState>((j, _) => jobs.Add(j)).Returns("job");
        var availability = new Mock<IAvailabilityCheckService>();
        availability.Setup(a => a.CheckAndReserveAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .Callback(() => calls.Add("reserve"))
            .ReturnsAsync((IReadOnlyList<LineReservation>)new List<LineReservation>());

        var creator = new Mock<ISaleOrderDeliveryCreator>();
        creator.Setup(c => c.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, Guid _, IReadOnlyList<SaleOrderLineRoute> lineRoutes, int _, CancellationToken _) =>
                new SaleOrderDeliveryCreationResult(
                    lineRoutes.GroupBy(l => l.RouteUuid).Select((g, i) => new CreatedSaleOrderDelivery(
                        Guid.NewGuid(), $"DLV-2026-{i + 1:00000}", g.Key, "R", "SHIP", null, g.Count())).ToList(),
                    []));
        var canceller = new Mock<ISaleOrderDeliveryCanceller>();
        canceller.Setup(c => c.CancelOpenAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("cancel-deliveries"))
            .ReturnsAsync(new SaleOrderDeliveryCancellationResult([], []));

        var resolver = new EffectiveRouteResolver(routes, variantRoutes, tenants);
        var service = new SaleOrderService(
            db, tenant, orgCurrency.Object, numbers.Object, pricing.Object, stock.Object, Mock.Of<ITimelineService>(),
            jobClient.Object, availability.Object, Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(),
            routes: resolver, deliveryCreator: creator.Object, deliveryCanceller: canceller.Object);

        return new Harness
        {
            Db = db, Service = service, OrgId = tenant.OrganizationId, DbName = dbName, Routes = routes,
            VariantRoutes = variantRoutes, Tenants = tenants, Creator = creator, Canceller = canceller,
            Availability = availability, Stock = stock, Pricing = pricing, Jobs = jobs, Calls = calls,
            PickOnly = pickOnly, PickAndShip = pickAndShip, PickPackShip = pickPackShip
        };
    }

    private static async Task SeedConfigAsync(Harness h, Action<SaleOrderConfig> change)
    {
        var config = new SaleOrderConfig();
        change(config);
        h.Db.SaleOrderConfigs.Add(config);
        await h.Db.SaveChangesAsync();
    }

    private static (Guid Variant, Guid? Route) L(Guid variant, Guid? route = null) => (variant, route);

    private static Task<Guid> DraftAsync(Harness h, params (Guid Variant, Guid? Route)[] lines) =>
        DraftAsync(h, "SHIP", Guid.NewGuid(), lines);

    private static Task<Guid> DraftAsync(Harness h, string mode, Guid? address, params (Guid Variant, Guid? Route)[] lines) =>
        h.Service.CreateAsync(new CreateSaleOrderRequest
        {
            PartnerId = Guid.NewGuid(), CurrencyId = Currency, DeliveryMode = mode, ShippingAddressId = address,
            Lines = lines.Select(l => new CreateSaleOrderLineRequest { VariantUuid = l.Variant, Quantity = 2m, FulfillmentRouteUuid = l.Route }).ToList()
        }, User);

    private static async Task<SaleOrder> ReadAsync(Harness h, Guid uuid)
    {
        await using var fresh = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(h.DbName).Options, new StaticTenantContext { OrganizationId = h.OrgId });
        return await fresh.SaleOrders.Include(o => o.Lines).SingleAsync(o => o.UUID == uuid);
    }

    // ── resolution (BR-C3-03), read on the DRAFT detail ───────────────────────

    [Fact]
    public async Task T_C3_01_a_line_with_no_override_takes_its_variants_route()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        h.VariantRoutes.Routes[(h.OrgId, variant)] = h.PickPackShip.Uuid;
        var so = await DraftAsync(h, L(variant));

        var line = (await h.Service.GetByIdAsync(so))!.Lines.Single();

        line.RouteSource.Should().Be("VARIANT");
        line.EffectiveRouteUuid.Should().Be(h.PickPackShip.Uuid);
        line.EffectiveRouteCode.Should().Be("PICK_PACK_SHIP");
        line.EffectiveRouteSteps.Should().Equal("PICK", "PACK", "GOODS_ISSUE", "SHIP");
        line.FulfillmentRouteUuid.Should().BeNull("no override was chosen");
        line.RouteBlocker.Should().BeNull();
    }

    [Fact]
    public async Task T_C3_02_a_line_override_beats_the_variants_route()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        h.VariantRoutes.Routes[(h.OrgId, variant)] = h.PickPackShip.Uuid;
        var so = await DraftAsync(h, L(variant, h.PickAndShip.Uuid));

        var line = (await h.Service.GetByIdAsync(so))!.Lines.Single();

        line.RouteSource.Should().Be("LINE_OVERRIDE");
        line.FulfillmentRouteUuid.Should().Be(h.PickAndShip.Uuid);
        line.EffectiveRouteUuid.Should().Be(h.PickAndShip.Uuid);
        line.EffectiveRouteName.Should().Be("PICK AND SHIP");
    }

    [Fact]
    public async Task T_C3_03_with_no_variant_route_the_org_default_of_the_orders_class_applies()
    {
        var h = NewHarness();
        var shipped   = await DraftAsync(h, "SHIP", Guid.NewGuid(), L(Guid.NewGuid()));
        var collected = await DraftAsync(h, "SELF_PICKUP", null, L(Guid.NewGuid()));

        var shipLine   = (await h.Service.GetByIdAsync(shipped))!.Lines.Single();
        var pickupLine = (await h.Service.GetByIdAsync(collected))!.Lines.Single();

        shipLine.RouteSource.Should().Be("ORG_DEFAULT");
        shipLine.EffectiveRouteUuid.Should().Be(h.PickAndShip.Uuid, "a SHIP order takes the default route with SHIP (L-1)");
        pickupLine.RouteSource.Should().Be("ORG_DEFAULT");
        pickupLine.EffectiveRouteUuid.Should().Be(h.PickOnly.Uuid, "a SELF_PICKUP order takes the default route without SHIP (L-1)");
    }

    [Fact]
    public async Task T_C3_04_with_nothing_to_inherit_the_line_resolves_to_none_and_blocks()
    {
        var h = NewHarness();
        h.Routes.Defaults[h.OrgId] = new FulfillmentRouteDefaults(null, null);
        var so = await DraftAsync(h, L(Guid.NewGuid()));

        var model = (await h.Service.GetByIdAsync(so))!;

        model.RoutesEnabled.Should().BeTrue();
        model.Lines.Single().RouteSource.Should().Be("NONE");
        model.Lines.Single().EffectiveRouteUuid.Should().BeNull();
        model.Lines.Single().RouteBlocker.Should().Be("ROUTE_MISSING");
        model.ConfirmBlockers.Should().ContainSingle().Which.Code.Should().Be("ROUTE_MISSING");
    }

    [Fact]
    public async Task The_resolver_reads_variant_routes_and_routes_once_however_many_lines()
    {
        var h = NewHarness();
        var variants = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        foreach (var v in variants.Take(3)) h.VariantRoutes.Routes[(h.OrgId, v)] = h.PickPackShip.Uuid;
        var so = await DraftAsync(h, variants.Select((v, i) => L(v, i == 5 ? h.PickOnly.Uuid : null)).ToArray());
        h.Routes.GetCalls = 0; h.VariantRoutes.Calls = 0; h.Routes.DefaultsCalls = 0;

        await h.Service.GetByIdAsync(so);

        h.VariantRoutes.Calls.Should().Be(1);
        h.Routes.GetCalls.Should().Be(1);
        h.Routes.DefaultsCalls.Should().BeLessThanOrEqualTo(1);
    }

    // ── BR-C3-01: the override must be an active route of the caller's organization ──

    [Fact]
    public async Task BR_C3_01_an_override_must_be_a_known_active_route_of_the_callers_organization()
    {
        var h = NewHarness();
        var foreign  = h.Routes.Add(Guid.NewGuid(), "THEIRS", "PICK", "GOODS_ISSUE");
        var inactive = h.Routes.Add(h.OrgId, "OLD_ROUTE", "PICK", "GOODS_ISSUE", "SHIP");
        h.Routes.Deactivate(inactive.Uuid);

        foreach (var bad in new[] { Guid.NewGuid(), foreign.Uuid, inactive.Uuid })
        {
            var act = () => DraftAsync(h, L(Guid.NewGuid(), bad));
            (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("route");
        }
        (await h.Db.SaleOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task BR_C3_01_the_same_rule_holds_on_update_and_an_override_round_trips()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        var so = await DraftAsync(h, L(variant));

        var bad = () => h.Service.UpdateAsync(so, new UpdateSaleOrderRequest
        {
            CurrencyId = Currency, DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines = [new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1m, FulfillmentRouteUuid = Guid.NewGuid() }]
        }, User);
        await bad.Should().ThrowAsync<BadRequestException>();

        await h.Service.UpdateAsync(so, new UpdateSaleOrderRequest
        {
            CurrencyId = Currency, DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines = [new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1m, FulfillmentRouteUuid = h.PickPackShip.Uuid }]
        }, User);

        (await ReadAsync(h, so)).Lines.Single().FulfillmentRouteUuid.Should().Be(h.PickPackShip.Uuid);
        (await h.Service.GetByIdAsync(so))!.Lines.Single().FulfillmentRouteUuid.Should().Be(h.PickPackShip.Uuid);
    }

    // ── route-only line update (lead: a route change must not re-price the draft) ──

    [Fact]
    public async Task Changing_one_lines_route_touches_only_that_route_and_never_reprices()
    {
        var h = NewHarness();
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var so = await DraftAsync(h, L(a), L(b));
        var before = await ReadAsync(h, so);
        var line = before.Lines.OrderBy(l => l.Id).First();
        // Prices have moved since the draft was taken: a full PUT would pick that up, a route change must not.
        h.Pricing.Setup(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalePriceResolution(true, 99m, Currency, PriceResolutionTier.DefaultSelling, null));

        var changed = await h.Service.UpdateLineRouteAsync(so, line.UUID, h.PickPackShip.Uuid, User);

        changed!.Uuid.Should().Be(line.UUID);
        changed.FulfillmentRouteUuid.Should().Be(h.PickPackShip.Uuid);
        changed.RouteSource.Should().Be("LINE_OVERRIDE");
        changed.EffectiveRouteCode.Should().Be("PICK_PACK_SHIP");
        changed.RouteBlocker.Should().BeNull();
        var after = await ReadAsync(h, so);
        after.GrandTotal.Should().Be(before.GrandTotal);
        after.Lines.Select(l => (l.UUID, l.UnitPrice, l.LineTotal, l.Quantity))
            .Should().BeEquivalentTo(before.Lines.Select(l => (l.UUID, l.UnitPrice, l.LineTotal, l.Quantity)));
        after.Lines.Single(l => l.UUID != line.UUID).FulfillmentRouteUuid.Should().BeNull("the other line is untouched");
        h.Pricing.Verify(p => p.ResolveSalePriceAsync(It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2), "only the two lines' creation priced anything");

        var inherited = await h.Service.UpdateLineRouteAsync(so, line.UUID, null, User);

        inherited!.FulfillmentRouteUuid.Should().BeNull();
        inherited.RouteSource.Should().Be("ORG_DEFAULT");
        inherited.EffectiveRouteUuid.Should().Be(h.PickAndShip.Uuid);
    }

    [Fact]
    public async Task A_route_only_update_follows_BR_C3_01_and_BR_C3_04_and_stays_in_the_callers_organization()
    {
        var h = NewHarness();
        var so = await DraftAsync(h, L(Guid.NewGuid()));
        var lineUuid = (await ReadAsync(h, so)).Lines.Single().UUID;
        var foreign  = h.Routes.Add(Guid.NewGuid(), "THEIRS", "PICK", "GOODS_ISSUE");
        var inactive = h.Routes.Add(h.OrgId, "OLD_ROUTE", "PICK", "GOODS_ISSUE");
        h.Routes.Deactivate(inactive.Uuid);

        foreach (var bad in new[] { Guid.NewGuid(), foreign.Uuid, inactive.Uuid })
        {
            var act = () => h.Service.UpdateLineRouteAsync(so, lineUuid, bad, User);
            await act.Should().ThrowAsync<BadRequestException>();
        }
        (await h.Service.UpdateLineRouteAsync(so, Guid.NewGuid(), h.PickOnly.Uuid, User)).Should().BeNull("not a line of this order");

        var admin = NewHarness(dbName: h.DbName, superAdmin: true, routes: h.Routes, variantRoutes: h.VariantRoutes, tenants: h.Tenants);
        (await admin.Service.UpdateLineRouteAsync(so, lineUuid, null, User)).Should().BeNull("another organization's order is not found");

        await h.Service.ConfirmWithResultAsync(so, User);
        var locked = () => h.Service.UpdateLineRouteAsync(so, lineUuid, h.PickOnly.Uuid, User);
        (await locked.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("DRAFT");
        (await ReadAsync(h, so)).Lines.Single().FulfillmentRouteUuid.Should().Be(h.PickAndShip.Uuid, "the snapshot stays");
    }

    // ── D-5, D-4, D-11 ────────────────────────────────────────────────────────

    [Fact]
    public async Task D5_a_drop_ship_line_needs_no_route_and_gets_no_delivery()
    {
        var h = NewHarness();
        h.Routes.Defaults[h.OrgId] = new FulfillmentRouteDefaults(null, null);
        await SeedConfigAsync(h, c => { c.DropShipEnabled = true; c.DefaultFulfillmentMode = FulfillmentModes.DropShip; });
        var so = await DraftAsync(h, L(Guid.NewGuid()));

        var model = (await h.Service.GetByIdAsync(so))!;
        model.Lines.Single().RouteSource.Should().Be("NONE");
        model.Lines.Single().RouteBlocker.Should().BeNull();
        model.ConfirmBlockers.Should().BeEmpty();

        var result = await h.Service.ConfirmWithResultAsync(so, User);

        result!.Status.Should().Be("CONFIRMED");
        h.Creator.Verify(c => c.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        (await ReadAsync(h, so)).Lines.Single().FulfillmentRouteUuid.Should().BeNull();
    }

    [Fact]
    public async Task D4_a_line_routed_to_ship_needs_a_shipping_address()
    {
        var h = NewHarness();
        var so = await DraftAsync(h, "SELF_PICKUP", null, L(Guid.NewGuid(), h.PickAndShip.Uuid), L(Guid.NewGuid()));

        var model = (await h.Service.GetByIdAsync(so))!;
        model.ConfirmBlockers.Should().ContainSingle().Which.Code.Should().Be("SHIPPING_ADDRESS_REQUIRED");

        var act = () => h.Service.ConfirmWithResultAsync(so, User);
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("shipping address");
        (await ReadAsync(h, so)).Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task D4_a_route_without_ship_is_refused_while_customer_pickup_is_off()
    {
        var h = NewHarness();
        await SeedConfigAsync(h, c => c.SelfPickupEnabled = false);
        var so = await DraftAsync(h, L(Guid.NewGuid()), L(Guid.NewGuid(), h.PickOnly.Uuid));

        var model = (await h.Service.GetByIdAsync(so))!;
        model.Lines[1].RouteBlocker.Should().Be("SELF_PICKUP_DISABLED");
        model.ConfirmBlockers.Should().ContainSingle(b => b.Code == "SELF_PICKUP_DISABLED").Which.LineNumber.Should().Be(2);

        var act = () => h.Service.ConfirmWithResultAsync(so, User);
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("Line 2:");
    }

    [Fact]
    public async Task D11_without_the_logistics_module_there_is_no_route_no_gate_and_no_delivery()
    {
        var h = NewHarness(logistics: false);
        h.Routes.Defaults[h.OrgId] = new FulfillmentRouteDefaults(null, null);
        var so = await DraftAsync(h, L(Guid.NewGuid()));

        var model = (await h.Service.GetByIdAsync(so))!;
        model.RoutesEnabled.Should().BeFalse();
        model.ConfirmBlockers.Should().BeEmpty();
        model.Lines.Single().RouteSource.Should().Be("NONE");
        model.Lines.Single().RouteBlocker.Should().BeNull();

        var result = await h.Service.ConfirmWithResultAsync(so, User);

        result!.Status.Should().Be("CONFIRMED");
        result.Deliveries.Should().BeEmpty();
        h.Creator.VerifyNoOtherCalls();
        (await ReadAsync(h, so)).DeliveryCreationPendingSince.Should().BeNull();
    }

    [Fact]
    public async Task D11_a_route_override_is_refused_when_the_organization_has_no_routes()
    {
        var h = NewHarness(logistics: false);

        var act = () => DraftAsync(h, L(Guid.NewGuid(), h.PickOnly.Uuid));

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── confirm (T-C3-05..08, D-16, D-1) ──────────────────────────────────────

    [Fact]
    public async Task T_C3_05_confirming_snapshots_each_lines_route_then_creates_the_deliveries_after_the_commit()
    {
        var h = NewHarness();
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        h.VariantRoutes.Routes[(h.OrgId, a)] = h.PickPackShip.Uuid;
        var so = await DraftAsync(h, L(a), L(b, h.PickOnly.Uuid), L(c));

        string? statusSeenByCreator = null;
        DateTime? pendingSeenByCreator = null;
        IReadOnlyList<SaleOrderLineRoute>? sent = null;
        h.Creator.Setup(x => x.CreateForConfirmedOrderAsync(h.OrgId, so, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), User, It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid _, IReadOnlyList<SaleOrderLineRoute> lr, int _, CancellationToken _) =>
            {
                var committed = await ReadAsync(h, so);
                statusSeenByCreator  = committed.Status;
                pendingSeenByCreator = committed.DeliveryCreationPendingSince;
                sent = lr;
                return new SaleOrderDeliveryCreationResult(
                    [new CreatedSaleOrderDelivery(Guid.NewGuid(), "DLV-2026-00001", h.PickPackShip.Uuid, "PICK_PACK_SHIP", "SHIP", null, 1)],
                    [new SkippedSaleOrderLine(Guid.NewGuid(), "Nothing outstanding")]);
            });

        var result = await h.Service.ConfirmWithResultAsync(so, User);

        result!.Status.Should().Be("CONFIRMED");
        result.Deliveries.Should().ContainSingle().Which.DeliveryNumber.Should().Be("DLV-2026-00001");
        result.SkippedLines.Should().ContainSingle().Which.Reason.Should().Be("Nothing outstanding");
        result.DeliveryCreationFailed.Should().BeFalse();
        statusSeenByCreator.Should().Be("CONFIRMED", "the creator runs only after the confirm has committed (REV-02)");
        pendingSeenByCreator.Should().NotBeNull("the D-12 flag is stamped in the confirm's own commit");

        var saved = await ReadAsync(h, so);
        var lines = saved.Lines.OrderBy(l => l.Id).ToList();
        lines.Select(l => l.FulfillmentRouteUuid).Should().Equal(h.PickPackShip.Uuid, h.PickOnly.Uuid, h.PickAndShip.Uuid);
        lines.Select(l => l.RouteSource).Should().Equal("VARIANT", "LINE_OVERRIDE", "ORG_DEFAULT");
        lines.Select(l => l.FulfillmentRouteCode).Should().Equal("PICK_PACK_SHIP", "PICK_ONLY", "PICK_AND_SHIP");
        sent!.Select(x => (x.SoLineUuid, x.RouteUuid)).Should().BeEquivalentTo(lines.Select(l => (l.UUID, l.FulfillmentRouteUuid!.Value)));
        saved.DeliveryCreationPendingSince.Should().BeNull("the creator returned, so the sweep has nothing to do");
        h.TimelineTypes.Should().Contain("SO_DELIVERIES_CREATED");
    }

    [Fact]
    public async Task T_C3_06_a_line_with_no_route_refuses_the_confirm_before_anything_is_reserved()
    {
        var h = NewHarness();
        h.Routes.Defaults[h.OrgId] = new FulfillmentRouteDefaults(null, null);
        var so = await DraftAsync(h, L(Guid.NewGuid(), h.PickOnly.Uuid), L(Guid.NewGuid()));

        var act = () => h.Service.ConfirmWithResultAsync(so, User);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Be(
            "Cannot confirm: line 2 has no fulfillment route. Assign a route on each line or set a default route on the product variant.");
        (await ReadAsync(h, so)).Status.Should().Be("DRAFT");
        h.Calls.Should().NotContain("reserve");
        h.Creator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task T_C3_07_a_line_whose_route_was_deactivated_after_saving_refuses_the_confirm_and_names_it_first()
    {
        var h = NewHarness();
        h.Routes.Defaults[h.OrgId] = new FulfillmentRouteDefaults(null, null);
        var custom = h.Routes.Add(h.OrgId, "COURIER_X", "PICK", "GOODS_ISSUE", "SHIP");
        var so = await DraftAsync(h, L(Guid.NewGuid()), L(Guid.NewGuid()), L(Guid.NewGuid(), custom.Uuid), L(Guid.NewGuid()));
        h.Routes.Deactivate(custom.Uuid); // R-8: deactivated after the line was saved — the gate re-checks

        var act = () => h.Service.ConfirmWithResultAsync(so, User);

        var message = (await act.Should().ThrowAsync<BadRequestException>()).Which.Message;
        message.Split('\n').Should().Equal(
            "Line 3: fulfillment route 'COURIER_X' is inactive.",
            "Cannot confirm: lines 1, 2, 4 have no fulfillment route. Assign a route on each line or set a default route on the product variant.");
        (await ReadAsync(h, so)).Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task T_C3_08_the_line_route_is_locked_once_the_order_is_confirmed()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        var so = await DraftAsync(h, L(variant));
        await h.Service.ConfirmWithResultAsync(so, User);

        var act = () => h.Service.UpdateAsync(so, new UpdateSaleOrderRequest
        {
            CurrencyId = Currency, DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines = [new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 2m, FulfillmentRouteUuid = h.PickOnly.Uuid }]
        }, User);

        await act.Should().ThrowAsync<BadRequestException>();
        (await ReadAsync(h, so)).Lines.Single().FulfillmentRouteUuid.Should().Be(h.PickAndShip.Uuid);
    }

    [Fact]
    public async Task D16_a_confirmed_order_reads_its_snapshot_not_todays_variant_route()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        h.VariantRoutes.Routes[(h.OrgId, variant)] = h.PickOnly.Uuid;
        var so = await DraftAsync(h, "SELF_PICKUP", null, L(variant));
        await h.Service.ConfirmWithResultAsync(so, User);
        h.VariantRoutes.Routes[(h.OrgId, variant)] = h.PickPackShip.Uuid; // changed afterwards

        var model = (await h.Service.GetByIdAsync(so))!;

        model.ConfirmBlockers.Should().BeEmpty();
        var line = model.Lines.Single();
        line.EffectiveRouteUuid.Should().Be(h.PickOnly.Uuid);
        line.FulfillmentRouteUuid.Should().Be(h.PickOnly.Uuid);
        line.RouteSource.Should().Be("VARIANT");
        line.EffectiveRouteSteps.Should().Equal("PICK", "GOODS_ISSUE");
    }

    [Fact]
    public async Task D1_with_auto_create_switched_off_confirming_creates_no_delivery()
    {
        var h = NewHarness();
        await SeedConfigAsync(h, c => c.AutoCreateDeliveriesOnConfirm = false);
        var so = await DraftAsync(h, L(Guid.NewGuid()));

        var result = await h.Service.ConfirmWithResultAsync(so, User);

        result!.Deliveries.Should().BeEmpty();
        result.DeliveryCreationFailed.Should().BeFalse();
        h.Creator.VerifyNoOtherCalls();
        var saved = await ReadAsync(h, so);
        saved.DeliveryCreationPendingSince.Should().BeNull();
        saved.Lines.Single().FulfillmentRouteUuid.Should().Be(h.PickAndShip.Uuid, "the snapshot is taken whatever the setting");
    }

    [Fact]
    public async Task D1_a_failed_delivery_creation_leaves_the_order_confirmed_and_pending_for_the_sweep()
    {
        var h = NewHarness();
        var so = await DraftAsync(h, L(Guid.NewGuid()));
        h.Creator.Setup(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Logistics is down"));

        var result = await h.Service.ConfirmWithResultAsync(so, User);

        result!.Status.Should().Be("CONFIRMED");
        result.DeliveryCreationFailed.Should().BeTrue();
        result.DeliveryMessage.Should().NotBeNullOrWhiteSpace();
        var saved = await ReadAsync(h, so);
        saved.Status.Should().Be("CONFIRMED");
        saved.DeliveryCreationPendingSince.Should().NotBeNull("the D-12 sweep retries it");
        h.TimelineTypes.Should().Contain("SO_DELIVERY_CREATION_FAILED");
    }

    [Fact]
    public async Task The_bool_confirm_still_works_for_existing_callers()
    {
        var h = NewHarness();
        var so = await DraftAsync(h, L(Guid.NewGuid()));

        (await h.Service.ConfirmAsync(so, User)).Should().BeTrue();
        (await h.Service.ConfirmAsync(Guid.NewGuid(), User)).Should().BeFalse();
    }

    // ── preview (BR-C3-05, T-C3-09) ───────────────────────────────────────────

    [Fact]
    public async Task T_C3_09_the_unsaved_forms_preview_groups_lines_by_route()
    {
        var h = NewHarness();
        var a = Guid.NewGuid();
        h.VariantRoutes.Routes[(h.OrgId, a)] = h.PickPackShip.Uuid;

        var preview = await h.Service.PreviewDeliveriesAsync(new SaleOrderDeliveryPreviewRequest
        {
            DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines =
            [
                new() { VariantUuid = a, Quantity = 500m },
                new() { VariantUuid = Guid.NewGuid(), Quantity = 600m, FulfillmentRouteUuid = h.PickOnly.Uuid },
                new() { VariantUuid = Guid.NewGuid(), Quantity = 100m },
                new() { VariantUuid = Guid.NewGuid(), Quantity = 50m, FulfillmentRouteUuid = h.PickOnly.Uuid }
            ]
        });

        preview.RoutesEnabled.Should().BeTrue();
        preview.CanConfirm.Should().BeTrue();
        preview.DeliveryCount.Should().Be(3);
        preview.Groups.Should().HaveCount(3);
        var pickOnly = preview.Groups.Single(g => g.RouteUuid == h.PickOnly.Uuid);
        pickOnly.LineNumbers.Should().Equal(2, 4);
        pickOnly.DeliveryMode.Should().Be("SELF_PICKUP", "the delivery's mode comes from its route (D-4)");
        pickOnly.StepsText.Should().Be("Pick → Goods Issue");
        preview.Groups.Single(g => g.RouteUuid == h.PickPackShip.Uuid).DeliveryMode.Should().Be("SHIP");
        preview.Lines.Select(l => l.RouteSource).Should().Equal("VARIANT", "LINE_OVERRIDE", "ORG_DEFAULT", "LINE_OVERRIDE");
        (await h.Db.SaleOrders.CountAsync()).Should().Be(0, "a preview persists nothing");
    }

    [Fact]
    public async Task A_saved_orders_preview_lists_unroutable_lines_as_blockers_outside_every_group()
    {
        var h = NewHarness();
        h.Routes.Defaults[h.OrgId] = new FulfillmentRouteDefaults(null, null);
        var so = await DraftAsync(h, L(Guid.NewGuid(), h.PickAndShip.Uuid), L(Guid.NewGuid(), h.PickAndShip.Uuid), L(Guid.NewGuid()));

        var preview = (await h.Service.GetDeliveryPreviewAsync(so))!;

        preview.CanConfirm.Should().BeFalse();
        preview.Groups.Should().ContainSingle().Which.LineNumbers.Should().Equal(1, 2);
        preview.Lines.Should().HaveCount(3);
        preview.Lines[2].LineUuid.Should().NotBeNull();
        preview.Lines[2].RouteBlocker.Should().Be("ROUTE_MISSING");
        preview.Blockers.Should().ContainSingle().Which.Code.Should().Be("ROUTE_MISSING");
    }

    [Fact]
    public async Task Own_org_another_organizations_order_has_no_preview_even_for_a_super_admin()
    {
        var owner = NewHarness();
        var so = await DraftAsync(owner, L(Guid.NewGuid()));
        var admin = NewHarness(dbName: owner.DbName, superAdmin: true, routes: owner.Routes, variantRoutes: owner.VariantRoutes, tenants: owner.Tenants);

        (await admin.Service.GetDeliveryPreviewAsync(so)).Should().BeNull();
        var act = () => admin.Service.PreviewDeliveriesAsync(new SaleOrderDeliveryPreviewRequest
        {
            SaleOrderUuid = so, DeliveryMode = "SHIP", Lines = [new() { VariantUuid = Guid.NewGuid(), Quantity = 1m }]
        });
        await act.Should().ThrowAsync<NotFoundException>();
        (await admin.Service.ConfirmWithResultAsync(so, User)).Should().BeNull();
        (await admin.Service.CancelWithResultAsync(so, User, null)).Should().BeNull();
        (await admin.Service.MarkDeliveriesCreatedAsync(so)).Should().BeFalse();
    }

    [Fact]
    public async Task Own_org_a_route_of_another_organization_never_resolves()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        var theirs = h.Routes.Add(Guid.NewGuid(), "THEIRS", "PICK", "GOODS_ISSUE");
        h.VariantRoutes.Routes[(h.OrgId, variant)] = theirs.Uuid; // a bad pointer must still read as unknown

        var line = (await h.Service.GetByIdAsync(await DraftAsync(h, L(variant))))!.Lines.Single();

        line.RouteSource.Should().Be("VARIANT");
        line.RouteBlocker.Should().Be("ROUTE_UNKNOWN");
        line.EffectiveRouteCode.Should().BeNull();
    }

    // ── cancel (D-15) ────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_a_confirmed_order_cancels_its_open_deliveries_before_releasing_its_holds()
    {
        var h = NewHarness();
        var so = await DraftAsync(h, L(Guid.NewGuid()));
        h.Creator.Setup(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("down")); // leaves the D-12 flag set; cancel must clear it
        await h.Service.ConfirmWithResultAsync(so, User);
        h.Canceller.Setup(c => c.CancelOpenAsync(h.OrgId, so, "Customer withdrew", User, It.IsAny<CancellationToken>()))
            .Callback(() => h.Calls.Add("cancel-deliveries"))
            .ReturnsAsync(new SaleOrderDeliveryCancellationResult(
                [new SaleOrderDeliveryRef(Guid.NewGuid(), "DLV-2026-00001", "CANCELLED")],
                [new SaleOrderDeliveryRef(Guid.NewGuid(), "DLV-2026-00002", "GOODS_ISSUED")]));

        var result = await h.Service.CancelWithResultAsync(so, User, "Customer withdrew");

        result!.CancelledDeliveries.Should().ContainSingle().Which.DeliveryNumber.Should().Be("DLV-2026-00001");
        result.IssuedDeliveries.Should().ContainSingle().Which.Status.Should().Be("GOODS_ISSUED");
        h.Calls.Where(c => c is "cancel-deliveries" or "release").Should().Equal("cancel-deliveries", "release");
        var saved = await ReadAsync(h, so);
        saved.Status.Should().Be("CANCELLED");
        saved.DeliveryCreationPendingSince.Should().BeNull();
        h.Timeline.Should().Contain(e => e.EventType == "SO_DELIVERIES_CANCELLED" && e.Notes!.Contains("DLV-2026-00002"));
    }

    [Fact]
    public async Task A_canceller_failure_leaves_the_order_as_it_was()
    {
        var h = NewHarness();
        var so = await DraftAsync(h, L(Guid.NewGuid()));
        await h.Service.ConfirmWithResultAsync(so, User);
        h.Canceller.Setup(c => c.CancelOpenAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConflictException("Delivery DLV-1 is being changed."));

        var act = () => h.Service.CancelWithResultAsync(so, User, null);

        await act.Should().ThrowAsync<ConflictException>();
        (await ReadAsync(h, so)).Status.Should().Be("CONFIRMED");
        h.Calls.Should().NotContain("release");
    }

    [Fact]
    public async Task The_bool_cancel_still_works_for_existing_callers()
    {
        var h = NewHarness();
        var so = await DraftAsync(h, L(Guid.NewGuid()));

        (await h.Service.CancelAsync(so, User, null)).Should().BeTrue();
        h.Canceller.VerifyNoOtherCalls(); // a DRAFT order never had deliveries
    }

    // ── D-12 sweep (REV-01) ───────────────────────────────────────────────────

    private static SaleOrderDeliverySweepJob Sweep(Harness h, out DemandDbContext db)
    {
        db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(h.DbName).Options,
            new StaticTenantContext { OrganizationId = Guid.Empty, IsSuperAdmin = true });
        var jobs = new Mock<IBackgroundJobClient>();
        jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job, IState>((j, _) => h.Jobs.Add(j)).Returns("job");
        return new SaleOrderDeliverySweepJob(db, jobs.Object, NullLogger<SaleOrderDeliverySweepJob>.Instance,
            new EffectiveRouteResolver(h.Routes, h.VariantRoutes, h.Tenants), h.Creator.Object);
    }

    private static async Task<Guid> PendingConfirmedAsync(Harness h, TimeSpan age)
    {
        var so = await DraftAsync(h, L(Guid.NewGuid()));
        h.Creator.Setup(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), so, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("down"));
        await h.Service.ConfirmWithResultAsync(so, User);
        var order = await h.Db.SaleOrders.SingleAsync(o => o.UUID == so);
        order.DeliveryCreationPendingSince = DateTime.UtcNow - age;
        await h.Db.SaveChangesAsync();
        h.Creator.Setup(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), so, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleOrderDeliveryCreationResult([], []));
        return so;
    }

    [Fact]
    public async Task D12_the_sweep_creates_only_for_pending_orders_older_than_ten_minutes_in_orgs_that_still_want_it()
    {
        var h = NewHarness();
        var due    = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(11));
        var recent = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(2));
        var cancelled = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(30));
        (await h.Db.SaleOrders.SingleAsync(o => o.UUID == cancelled)).Status = "CANCELLED";
        await h.Db.SaveChangesAsync();

        // A second organization whose admin has since switched auto-create off, and a third that lost Logistics.
        var off = NewHarness(dbName: h.DbName, routes: h.Routes, variantRoutes: h.VariantRoutes, tenants: h.Tenants);
        var offSo = await PendingConfirmedAsync(off, TimeSpan.FromMinutes(20));
        await SeedConfigAsync(off, c => c.AutoCreateDeliveriesOnConfirm = false);
        var gone = NewHarness(dbName: h.DbName, routes: h.Routes, variantRoutes: h.VariantRoutes, tenants: h.Tenants);
        var goneSo = await PendingConfirmedAsync(gone, TimeSpan.FromMinutes(20));
        h.Tenants.WithLogistics.Remove(gone.OrgId);
        h.Creator.Invocations.Clear(); off.Creator.Invocations.Clear(); gone.Creator.Invocations.Clear();
        off.Creator.Setup(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleOrderDeliveryCreationResult([], []));

        var job = Sweep(h, out var db);
        var processed = await job.RunAsync();

        processed.Should().Be(3, "the due order is created; the two of orgs that no longer auto-create stop waiting (REV-05)");
        (await ReadAsync(off, offSo)).DeliveryCreationPendingSince.Should().BeNull();
        (await ReadAsync(gone, goneSo)).DeliveryCreationPendingSince.Should().BeNull();
        h.Creator.Verify(x => x.CreateForConfirmedOrderAsync(h.OrgId, due, It.Is<IReadOnlyList<SaleOrderLineRoute>>(l => l.Count == 1), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        h.Creator.Verify(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), recent, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Creator.Verify(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), cancelled, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Creator.Verify(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), offSo, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        h.Creator.Verify(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), goneSo, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        (await ReadAsync(h, due)).DeliveryCreationPendingSince.Should().BeNull();
        (await ReadAsync(h, recent)).DeliveryCreationPendingSince.Should().NotBeNull();
        HangfireTenantScope.OrganizationId.Should().BeNull("the job leaves no tenant behind");
    }

    [Fact]
    public async Task D12_a_sweep_that_fails_again_keeps_the_order_pending_but_one_refused_for_good_is_cleared()
    {
        var h = NewHarness();
        var failing = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(15));
        var refused = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(15));
        h.Creator.Setup(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), failing, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("still down"));
        h.Creator.Setup(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), refused, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BadRequestException("Only a CONFIRMED order can get deliveries."));

        await Sweep(h, out _).RunAsync();

        (await ReadAsync(h, failing)).DeliveryCreationPendingSince.Should().NotBeNull();
        (await ReadAsync(h, refused)).DeliveryCreationPendingSince.Should().BeNull();
    }

    [Fact]
    public async Task REV05_orders_the_sweep_skips_or_keeps_failing_cannot_starve_the_rest_of_the_queue()
    {
        // Oldest first: two orders of an org that has since switched auto-create off, then one whose creator keeps
        // failing, then the one that can actually be created. With a batch of 2 the first two used to fill every run.
        var h = NewHarness();
        var off = NewHarness(dbName: h.DbName, routes: h.Routes, variantRoutes: h.VariantRoutes, tenants: h.Tenants);
        var offA = await PendingConfirmedAsync(off, TimeSpan.FromMinutes(60));
        var offB = await PendingConfirmedAsync(off, TimeSpan.FromMinutes(59));
        await SeedConfigAsync(off, c => c.AutoCreateDeliveriesOnConfirm = false);
        var failing = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(40));
        var good    = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(30));
        h.Creator.Setup(x => x.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), failing, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("this order's data trips the creator every time"));
        h.Creator.Invocations.Clear(); // the confirms' own (failed) calls

        for (var run = 0; run < 3; run++)
        {
            var job = Sweep(h, out _);
            job.BatchSize = 2;
            await job.RunAsync();
        }

        h.Creator.Verify(x => x.CreateForConfirmedOrderAsync(h.OrgId, good, It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        (await ReadAsync(h, good)).DeliveryCreationPendingSince.Should().BeNull();
        (await ReadAsync(off, offA)).DeliveryCreationPendingSince.Should().BeNull("the org no longer wants auto-created deliveries: only the button creates them now");
        (await ReadAsync(off, offB)).DeliveryCreationPendingSince.Should().BeNull();
        var stillFailing = (await ReadAsync(h, failing)).DeliveryCreationPendingSince;
        stillFailing.Should().NotBeNull("a retryable failure stays pending");
        stillFailing!.Value.Should().BeAfter(DateTime.UtcNow.AddMinutes(-5), "it goes to the back of the queue");
    }

    [Fact]
    public async Task REV06_after_confirm_the_preview_splits_a_line_held_in_two_warehouses_as_the_creator_does()
    {
        var h = NewHarness();
        var so = await DraftAsync(h, L(Guid.NewGuid()), L(Guid.NewGuid()));
        await h.Service.ConfirmWithResultAsync(so, User);
        var lines = (await ReadAsync(h, so)).Lines.OrderBy(l => l.Id).ToList();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        h.Stock.Setup(s => s.GetBySourceAsync(ReservationSourceType.SalesOrder, so, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReservationSummary>
            {
                new(Guid.NewGuid(), lines[0].VariantUuid, a, 1.2m, "ACTIVE", lines[0].UUID),
                new(Guid.NewGuid(), lines[0].VariantUuid, b, 0.8m, "ACTIVE", lines[0].UUID),
                new(Guid.NewGuid(), lines[1].VariantUuid, a, 2m,   "ACTIVE", lines[1].UUID)
            });

        var preview = (await h.Service.GetDeliveryPreviewAsync(so))!;

        preview.DeliveryCount.Should().Be(2, "one route, two ship-from warehouses (C-4)");
        preview.Groups.Single(g => g.WarehouseUuid == a).LineNumbers.Should().Equal(1, 2);
        preview.Groups.Single(g => g.WarehouseUuid == b).LineNumbers.Should().Equal(1);
    }

    [Fact]
    public async Task Recovery_marks_the_order_as_no_longer_pending()
    {
        var h = NewHarness();
        var so = await PendingConfirmedAsync(h, TimeSpan.FromMinutes(1));

        (await h.Service.MarkDeliveriesCreatedAsync(so)).Should().BeTrue();

        (await ReadAsync(h, so)).DeliveryCreationPendingSince.Should().BeNull();
    }

    // ── L-7: open sale order lines still on a route ───────────────────────────

    [Fact]
    public async Task Route_usage_counts_the_stored_route_on_lines_of_open_orders_of_that_organization_only()
    {
        var h = NewHarness();
        var custom = h.Routes.Add(h.OrgId, "COURIER_X", "PICK", "GOODS_ISSUE", "SHIP");
        await DraftAsync(h, L(Guid.NewGuid(), custom.Uuid), L(Guid.NewGuid(), custom.Uuid), L(Guid.NewGuid()));   // 2 overrides
        var confirmed = await DraftAsync(h, L(Guid.NewGuid(), custom.Uuid));
        await h.Service.ConfirmWithResultAsync(confirmed, User);                                                // 1 snapshot
        var cancelled = await DraftAsync(h, L(Guid.NewGuid(), custom.Uuid));
        await h.Service.CancelWithResultAsync(cancelled, User, null);                                           // not open
        var other = NewHarness(dbName: h.DbName, routes: h.Routes, variantRoutes: h.VariantRoutes, tenants: h.Tenants);
        h.Routes.Routes[custom.Uuid] = (other.OrgId, custom);
        await DraftAsync(other, L(Guid.NewGuid(), custom.Uuid));                                               // other org
        h.Routes.Routes[custom.Uuid] = (h.OrgId, custom);

        var usage = await new SaleOrderRouteUsage(h.Db).CountUsageAsync(h.OrgId, custom.Uuid);

        usage.Count.Should().Be(3);
        usage.Description.Should().Be("open sale order lines");
    }
}
