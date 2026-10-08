using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A34 (DEM): a sale order service wired with the A33 route resolver plus A34's readiness, calculator, production
/// service, allocation engine and notifications — all fakes. The real <see cref="AvailabilityCheckService"/> runs over a
/// mocked stock ledger, so what confirm reserves (or, for make-to-order lines, does not) is real.
/// </summary>
internal sealed class A34SoHarness
{
    public const int User = 7;
    public static readonly Guid Currency = Guid.NewGuid();

    public required DemandDbContext Db { get; init; }
    public required SaleOrderService Service { get; init; }
    public required StaticTenantContext Tenant { get; init; }
    public required string DbName { get; init; }
    public required A34Routes Routes { get; init; }
    public required A34Tenants Tenants { get; init; }
    public required FakeReadiness Readiness { get; init; }
    public required FakeLeadTimes LeadTimes { get; init; }
    public required FakeLevelDays LevelDays { get; init; }
    public required FakeProduction Production { get; init; }
    public required Mock<IAllocationEngine> Allocation { get; init; }
    public required Mock<IStockReservationService> Stock { get; init; }
    public required Mock<ISaleOrderDeliveryCreator> Creator { get; init; }
    public required Mock<ISaleOrderDeliveryCanceller> Canceller { get; init; }
    public required List<Job> Jobs { get; init; }
    public required List<string> Calls { get; init; }
    public required List<NotificationRequest> Notifications { get; init; }
    public required List<(Guid Variant, decimal Qty, Guid? Line)> Reserved { get; init; }
    public required Dictionary<Guid, decimal> Available { get; init; }
    public required FulfillmentRouteSummary PickAndShip { get; init; }
    public required FulfillmentRouteSummary PickOnly { get; init; }
    public required FulfillmentRouteSummary MfgShip { get; init; }
    public required FulfillmentRouteSummary MfgPickup { get; init; }

    public Guid OrgId => Tenant.OrganizationId;
    public IEnumerable<TimelineEvent> Timeline => Jobs.SelectMany(j => j.Args.OfType<TimelineEvent>());
    public IEnumerable<Job> AutoPoJobs => Jobs.Where(j => j.Type == typeof(IAutoPoCreationJob));

    public static A34SoHarness Create(bool manufacturing = true, bool production = true, Guid? org = null, string? dbName = null,
        A34Routes? routes = null, A34Tenants? tenants = null, FakeProduction? fakeProduction = null)
    {
        dbName ??= Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = org ?? Guid.NewGuid() };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        routes  ??= new A34Routes();
        tenants ??= new A34Tenants();
        tenants.Enable(tenant.OrganizationId, A34.Demand, A34.Logistics, A34.Inventory);
        if (manufacturing) tenants.Enable(tenant.OrganizationId, A34.Manufacturing);

        var pickOnly    = routes.Add(tenant.OrganizationId, "PICK_ONLY", FulfillmentRouteCategory.Stock, "PICK", "GOODS_ISSUE");
        var pickAndShip = routes.Add(tenant.OrganizationId, "PICK_AND_SHIP", FulfillmentRouteCategory.Stock, "PICK", "GOODS_ISSUE", "SHIP");
        var mfgShip     = routes.Add(tenant.OrganizationId, "MFG_PICK_SHIP", FulfillmentRouteCategory.Manufacture, "PICK", "GOODS_ISSUE", "SHIP");
        var mfgPickup   = routes.Add(tenant.OrganizationId, "MFG_COLLECT", FulfillmentRouteCategory.Manufacture, "PICK", "GOODS_ISSUE");
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

        var available = new Dictionary<Guid, decimal>();
        var reserved  = new List<(Guid Variant, decimal Qty, Guid? Line)>();
        var stock = new Mock<IStockReservationService>();
        stock.Setup(s => s.GetBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ReservationSummary>());
        stock.Setup(s => s.GetAvailableAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Guid> ids, Guid? _, CancellationToken _) =>
                (IReadOnlyList<VariantAvailability>)ids.Select(v => new VariantAvailability(v, Guid.Empty, "Central", available.GetValueOrDefault(v))).ToList());
        stock.Setup(s => s.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ReservationRequest>>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Guid _, IReadOnlyList<ReservationRequest> reqs, int _, DateTime? _, CancellationToken _) =>
            {
                calls.Add("reserve");
                reserved.AddRange(reqs.Select(r => (r.VariantUuid, r.Quantity, r.SourceLineUuid)));
                return new ReservationResult(true, reqs.Select(r => new ReservationLineResult(r.VariantUuid, r.SourceLineUuid, r.Quantity, r.Quantity, 0m, 100m, null)).ToList());
            });
        stock.Setup(s => s.ReleaseBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("release")).ReturnsAsync(0);

        var jobs = new List<Job>();
        var jobClient = new Mock<IBackgroundJobClient>();
        jobClient.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job, IState>((j, _) => jobs.Add(j)).Returns("job");

        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { ReservationTtlHours = 72 });
        var availability = new AvailabilityCheckService(db, stock.Object, config.Object);

        var creator = new Mock<ISaleOrderDeliveryCreator>();
        creator.Setup(c => c.CreateForConfirmedOrderAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<SaleOrderLineRoute>>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("create-deliveries"))
            .ReturnsAsync((Guid _, Guid _, IReadOnlyList<SaleOrderLineRoute> lineRoutes, int _, CancellationToken _) =>
                new SaleOrderDeliveryCreationResult(
                    lineRoutes.GroupBy(l => l.RouteUuid).Select((g, i) => new CreatedSaleOrderDelivery(
                        Guid.NewGuid(), $"DLV-2026-{i + 1:00000}", g.Key, "R", "SHIP", null, g.Count())).ToList(), []));
        var canceller = new Mock<ISaleOrderDeliveryCanceller>();
        canceller.Setup(c => c.CancelOpenAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("cancel-deliveries"))
            .ReturnsAsync(new SaleOrderDeliveryCancellationResult([], []));

        var readiness  = new FakeReadiness();
        var leadTimes  = new FakeLeadTimes();
        var levelDays  = new FakeLevelDays();
        fakeProduction ??= new FakeProduction();
        var allocation = new Mock<IAllocationEngine>();
        allocation.Setup(a => a.GetDemandsAsync(It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DemandAllocationSummary>());
        var notifications = new List<NotificationRequest>();
        var notifier = new Mock<INotificationService>();
        notifier.Setup(x => x.TryCreateAsync(It.IsAny<NotificationRequest>())).Callback<NotificationRequest>(notifications.Add).Returns(Task.CompletedTask);
        notifier.Setup(x => x.CreateAsync(It.IsAny<NotificationRequest>())).Callback<NotificationRequest>(notifications.Add).Returns(Task.CompletedTask);

        var resolver = new EffectiveRouteResolver(routes, routes, tenants, readiness);
        var service = new SaleOrderService(
            db, tenant, orgCurrency.Object, numbers.Object, pricing.Object, stock.Object, Mock.Of<ITimelineService>(),
            jobClient.Object, availability, Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(),
            routes: resolver, deliveryCreator: creator.Object, deliveryCanceller: canceller.Object,
            leadTimes: leadTimes, production: production ? fakeProduction : null, allocation: allocation.Object,
            tenants: tenants, notifications: notifier.Object, levelDays: levelDays);

        return new A34SoHarness
        {
            Db = db, Service = service, Tenant = tenant, DbName = dbName, Routes = routes, Tenants = tenants, Readiness = readiness,
            LeadTimes = leadTimes, LevelDays = levelDays, Production = fakeProduction, Allocation = allocation, Stock = stock, Creator = creator,
            Canceller = canceller, Jobs = jobs, Calls = calls, Notifications = notifications, Reserved = reserved, Available = available,
            PickAndShip = pickAndShip, PickOnly = pickOnly, MfgShip = mfgShip, MfgPickup = mfgPickup
        };
    }

    public Task<Guid> DraftAsync(params (Guid Variant, Guid? Route)[] lines) => DraftAsync("SHIP", Guid.NewGuid(), null, lines);

    public Task<Guid> DraftAsync(string mode, Guid? address, DateTime? expected, params (Guid Variant, Guid? Route)[] lines) =>
        Service.CreateAsync(new CreateSaleOrderRequest
        {
            PartnerId = Guid.NewGuid(), CurrencyId = Currency, DeliveryMode = mode, ShippingAddressId = address, ExpectedDeliveryDate = expected,
            Lines = lines.Select(l => new CreateSaleOrderLineRequest { VariantUuid = l.Variant, Quantity = 10m, FulfillmentRouteUuid = l.Route }).ToList()
        }, User);

    public async Task<SaleOrder> ReadAsync(Guid uuid)
    {
        await using var fresh = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(DbName).Options, new StaticTenantContext { OrganizationId = OrgId });
        return await fresh.SaleOrders.Include(o => o.Lines).SingleAsync(o => o.UUID == uuid);
    }

    /// <summary>A variant whose own route is the manufacture route, and which Material says can be made.</summary>
    public Guid MakeToOrderVariant(string name = "Widget — Red")
    {
        var variant = Guid.NewGuid();
        Routes.VariantRoutes[(OrgId, variant)] = MfgShip.Uuid;
        Readiness.Ready(OrgId, variant, name);
        return variant;
    }
}
