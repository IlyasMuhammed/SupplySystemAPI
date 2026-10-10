using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>Inventory's product type: the variants listed here are services (per organization).</summary>
internal sealed class FakeServiceVariants : IServiceVariantClassifier
{
    public readonly HashSet<(Guid Org, Guid Variant)> Services = [];

    public Task<IReadOnlySet<Guid>> ServiceVariantsAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlySet<Guid>>(variantUuids.Where(v => Services.Contains((organizationId, v))).ToHashSet());
}

/// <summary>Material's service orders: one live order per (sale order, line), DRAFT on create.</summary>
internal sealed class FakeServiceOrders : IServiceOrderDemandService
{
    public sealed class So
    {
        public required Guid SaleOrder; public required Guid Line; public required string Number;
        public Guid Uuid = Guid.NewGuid(); public string Status = "DRAFT"; public decimal Quantity;
    }

    public readonly List<So> Orders = [];
    public readonly List<(Guid SaleOrder, Guid Line, string Number, Guid Customer, Guid Variant, decimal Qty, DateTime? Date, Guid? Warehouse, int User, Guid? Trace)> EnsureCalls = [];
    public readonly List<(Guid SaleOrder, string Reason)> CancelCalls = [];
    public Exception? ThrowOnEnsure { get; set; }
    private int _n;

    private static SaleOrderServiceOrderRef Ref(So s) => new(s.Uuid, s.Number, s.Line, s.Status, s.Quantity);

    public Task<SaleOrderServiceOrderRef> EnsureForSaleOrderLineAsync(
        Guid saleOrderUuid, Guid saleOrderLineUuid, string saleOrderNumber, Guid customerUuid, Guid variantUuid, decimal quantity,
        DateTime? scheduledDate, Guid? warehouseUuid, int userId, Guid? traceId = null, CancellationToken ct = default)
    {
        EnsureCalls.Add((saleOrderUuid, saleOrderLineUuid, saleOrderNumber, customerUuid, variantUuid, quantity, scheduledDate, warehouseUuid, userId, traceId));
        if (ThrowOnEnsure is not null) throw ThrowOnEnsure;
        var existing = Orders.FirstOrDefault(o => o.SaleOrder == saleOrderUuid && o.Line == saleOrderLineUuid && o.Status != "CANCELLED");
        if (existing is not null) return Task.FromResult(Ref(existing));
        var created = new So { SaleOrder = saleOrderUuid, Line = saleOrderLineUuid, Number = $"SVC-2026-{++_n:00000}", Quantity = quantity };
        Orders.Add(created);
        return Task.FromResult(Ref(created));
    }

    public Task<IReadOnlyList<SaleOrderServiceOrderRef>> GetForSaleOrderAsync(Guid organizationId, Guid saleOrderUuid, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SaleOrderServiceOrderRef>>(Orders.Where(o => o.SaleOrder == saleOrderUuid).Select(Ref).ToList());

    public Task<SaleOrderServiceCancellation> CancelForSaleOrderAsync(Guid saleOrderUuid, string reason, int userId, CancellationToken ct = default)
    {
        CancelCalls.Add((saleOrderUuid, reason));
        var cancelled = new List<SaleOrderServiceOrderRef>();
        var running = new List<SaleOrderServiceOrderRef>();
        foreach (var o in Orders.Where(o => o.SaleOrder == saleOrderUuid && o.Status != "CANCELLED"))
        {
            if (o.Status is "DRAFT" or "PLANNED" or "READY") { o.Status = "CANCELLED"; cancelled.Add(Ref(o)); }
            else running.Add(Ref(o));
        }
        return Task.FromResult(new SaleOrderServiceCancellation(cancelled, running));
    }
}

/// <summary>
/// A36 D-10 / D-11 (DEM: P5-05, P5-06, P5-08; TS-17/18/19 at service level) — service lines on sale orders: route-exempt,
/// never reserved, no delivery and no auto-PO; a service order per line after confirm (idempotent, retried on the detail);
/// the line and the order fulfilled from COMPLETED/CLOSED service orders; the cancel cascade; the feature switch.
/// </summary>
public class A36ServiceLineTests
{
    private const int User = A34SoHarness.User;

    private sealed record Ctx(A34SoHarness H, FakeServiceVariants Variants, FakeServiceOrders Services, Guid ServiceVariant);

    private static Ctx Create(bool servicesFeature = true, bool logistics = true)
    {
        var variants = new FakeServiceVariants();
        var services = new FakeServiceOrders();
        var tenants = new A34Tenants();
        var h = A34SoHarness.Create(tenants: tenants, serviceVariants: variants, serviceOrders: services);
        if (servicesFeature) tenants.Enable(h.OrgId, SaleOrderServiceLines.ServicesFeature);
        if (!logistics) tenants.Disable(h.OrgId, A34.Logistics);
        var serviceVariant = Guid.NewGuid();
        variants.Services.Add((h.OrgId, serviceVariant));
        return new Ctx(h, variants, services, serviceVariant);
    }

    private static SaleOrderServiceFulfillmentListener Listener(A34SoHarness h, FakeServiceOrders services, out DemandDbContext db)
    {
        db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(h.DbName).Options,
            new StaticTenantContext { OrganizationId = h.OrgId });
        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(IServiceOrderDemandService))).Returns(services);
        var jobs = new Mock<IBackgroundJobClient>();
        jobs.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job, IState>((j, _) => h.Jobs.Add(j)).Returns("job");
        return new SaleOrderServiceFulfillmentListener(db, provider.Object, jobs.Object, Mock.Of<ISaleOrderEmailService>(),
            NullLogger<SaleOrderServiceFulfillmentListener>.Instance);
    }

    private static async Task<(Guid So, Guid StockLine, Guid ServiceLine)> ConfirmedMixedAsync(Ctx c)
    {
        var stock = Guid.NewGuid();
        c.H.Available[stock] = 100m;
        var so = await c.H.DraftAsync((stock, null), (c.ServiceVariant, null));
        await c.H.Service.ConfirmWithResultAsync(so, User);
        var lines = (await c.H.ReadAsync(so)).Lines;
        return (so, lines.Single(l => l.VariantUuid == stock).UUID, lines.Single(l => l.VariantUuid == c.ServiceVariant).UUID);
    }

    [Fact]
    public async Task TS_17_a_mixed_order_delivers_the_stock_line_and_raises_one_service_order_for_the_service_line()
    {
        var c = Create();
        var (so, stockLine, serviceLine) = await ConfirmedMixedAsync(c);

        // Only the stock line went to the delivery creator; the service line was neither reserved nor bought.
        var sent = (IReadOnlyList<SaleOrderLineRoute>)c.H.Creator.Invocations.Single().Arguments[2];
        sent.Select(r => r.SoLineUuid).Should().Equal(stockLine);
        c.H.Reserved.Should().OnlyContain(r => r.Line == stockLine);
        c.H.AutoPoJobs.Should().BeEmpty();

        var saved = await c.H.ReadAsync(so);
        var line = saved.Lines.Single(l => l.UUID == serviceLine);
        line.FulfillmentMode.Should().Be("SERVICE");
        line.DeficitQty.Should().Be(0m);
        line.FulfillmentRouteUuid.Should().BeNull();
        line.Status.Should().Be("OPEN");

        var call = c.Services.EnsureCalls.Should().ContainSingle().Subject;
        call.SaleOrder.Should().Be(so);
        call.Line.Should().Be(serviceLine);
        call.Number.Should().Be(saved.SoNumber);
        call.Customer.Should().Be(saved.PartnerId);
        call.Variant.Should().Be(c.ServiceVariant);
        call.Qty.Should().Be(10m);
        call.Warehouse.Should().BeNull();
        call.User.Should().Be(User);
        call.Trace.Should().Be(saved.TraceId);
        c.H.Timeline.Should().Contain(e => e.EventType == "SO_SERVICE_ORDERS_CREATED");

        // The detail: isService, serviceOrders[]; reading it again raises nothing more (idempotent).
        var model = (await c.H.Service.GetByIdAsync(so))!;
        model.Lines.Single(l => l.Uuid == serviceLine).IsService.Should().BeTrue();
        model.Lines.Single(l => l.Uuid == stockLine).IsService.Should().BeFalse();
        model.Lines.Single(l => l.Uuid == serviceLine).ReservableQty.Should().Be(0m);
        var svc = model.ServiceOrders.Should().ContainSingle().Subject;
        svc.ServiceNumber.Should().Be("SVC-2026-00001");
        svc.SoLineUuid.Should().Be(serviceLine);
        svc.LineNumber.Should().Be(2);
        svc.Status.Should().Be("DRAFT");
        svc.Quantity.Should().Be(10m);
        c.Services.EnsureCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_draft_service_line_is_route_exempt_and_never_blocks_confirm()
    {
        var c = Create();
        c.H.Routes.Defaults.Remove(c.H.OrgId);   // no org default: a stock line would be ROUTE_MISSING
        var so = await c.H.DraftAsync((c.ServiceVariant, null));

        var model = (await c.H.Service.GetByIdAsync(so))!;

        model.ConfirmBlockers.Should().BeEmpty();
        var line = model.Lines.Single();
        line.IsService.Should().BeTrue();
        line.RouteBlocker.Should().BeNull();
        line.EffectiveRouteUuid.Should().BeNull();
        model.ServiceOrders.Should().BeEmpty();
        c.Services.EnsureCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task TS_18_a_service_only_order_creates_no_delivery_and_is_fulfilled_when_its_service_order_completes()
    {
        var c = Create();
        var so = await c.H.DraftAsync((c.ServiceVariant, null));
        var result = (await c.H.Service.ConfirmWithResultAsync(so, User))!;

        result.Status.Should().Be("CONFIRMED");
        result.Deliveries.Should().BeEmpty();
        result.ServiceOrderCreationFailed.Should().BeFalse();
        c.H.Creator.Invocations.Should().BeEmpty();
        var lineUuid = (await c.H.ReadAsync(so)).Lines.Single().UUID;

        var listener = Listener(c.H, c.Services, out _);
        c.Services.Orders.Single().Status = "IN_PROGRESS";
        await listener.OnServiceOrderChangedAsync(c.H.OrgId, so, lineUuid, User);
        (await c.H.ReadAsync(so)).Status.Should().Be("CONFIRMED", "a running service order fulfils nothing");

        c.Services.Orders.Single().Status = "COMPLETED";
        await Listener(c.H, c.Services, out _).OnServiceOrderChangedAsync(c.H.OrgId, so, lineUuid, User);

        var saved = await c.H.ReadAsync(so);
        saved.Status.Should().Be("FULFILLED");
        saved.Lines.Single().FulfilledQty.Should().Be(10m);
        saved.Lines.Single().Status.Should().Be("FULFILLED");
        c.H.Timeline.Should().Contain(e => e.EventType == "SO_FULFILLED");

        // A replay (e.g. COMPLETED → CLOSED) changes nothing.
        c.Services.Orders.Single().Status = "CLOSED";
        await Listener(c.H, c.Services, out _).OnServiceOrderChangedAsync(c.H.OrgId, so, lineUuid, User);
        (await c.H.ReadAsync(so)).Status.Should().Be("FULFILLED");
        c.H.Timeline.Count(e => e.EventType == "SO_FULFILLED").Should().Be(1);
    }

    [Fact]
    public async Task TS_19_a_mixed_order_is_fulfilled_only_once_the_delivery_and_the_service_are_both_done()
    {
        var c = Create();
        var (so, stockLine, serviceLine) = await ConfirmedMixedAsync(c);

        // A partial service order first: PARTIALLY_FULFILLED, counted by quantity.
        c.Services.Orders.Single().Quantity = 4m;
        c.Services.Orders.Single().Status = "COMPLETED";
        await Listener(c.H, c.Services, out _).OnServiceOrderChangedAsync(c.H.OrgId, so, serviceLine, User);
        var saved = await c.H.ReadAsync(so);
        saved.Status.Should().Be("PARTIALLY_FULFILLED");
        saved.Lines.Single(l => l.UUID == serviceLine).Status.Should().Be("PARTIALLY_FULFILLED");
        saved.Lines.Single(l => l.UUID == serviceLine).FulfilledQty.Should().Be(4m);

        // The rest of the service: the line is done, but the stock line is not delivered yet.
        c.Services.Orders.Add(new FakeServiceOrders.So { SaleOrder = so, Line = serviceLine, Number = "SVC-2026-00099", Quantity = 6m, Status = "CLOSED" });
        await Listener(c.H, c.Services, out _).OnServiceOrderChangedAsync(c.H.OrgId, so, serviceLine, User);
        (await c.H.ReadAsync(so)).Status.Should().Be("PARTIALLY_FULFILLED");

        // The delivery completes (Logistics credits the line, then tells the fulfilment service): now FULFILLED.
        await using (var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(c.H.DbName).Options,
                         new StaticTenantContext { OrganizationId = c.H.OrgId }))
        {
            var line = await db.SaleOrderLines.SingleAsync(l => l.UUID == stockLine);
            line.FulfilledQty = 10m;
            line.Status = "FULFILLED";
            await db.SaveChangesAsync();
            var fulfilment = new SaleOrderFulfillmentService(db, Mock.Of<ISaleOrderEmailService>(), Mock.Of<IBackgroundJobClient>(),
                NullLogger<SaleOrderFulfillmentService>.Instance);
            var outcome = await fulfilment.RecordDeliveryCompletedAsync(
                new DeliveryCompletion(so, Guid.NewGuid(), "DLV-2026-00001", [new DeliveredLine(stockLine, 10m)], User));
            outcome.Status.Should().Be("FULFILLED");
        }
    }

    [Fact]
    public async Task A_delivery_alone_does_not_fulfil_a_mixed_order_whose_service_is_pending()
    {
        var c = Create();
        var (so, stockLine, _) = await ConfirmedMixedAsync(c);

        await using var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(c.H.DbName).Options,
            new StaticTenantContext { OrganizationId = c.H.OrgId });
        var line = await db.SaleOrderLines.SingleAsync(l => l.UUID == stockLine);
        line.FulfilledQty = 10m;
        await db.SaveChangesAsync();
        var outcome = await new SaleOrderFulfillmentService(db, Mock.Of<ISaleOrderEmailService>(), Mock.Of<IBackgroundJobClient>(),
                NullLogger<SaleOrderFulfillmentService>.Instance)
            .RecordDeliveryCompletedAsync(new DeliveryCompletion(so, Guid.NewGuid(), "DLV-2026-00001", [new DeliveredLine(stockLine, 10m)], User));

        outcome.Status.Should().Be("PARTIALLY_FULFILLED");
    }

    [Fact]
    public async Task Cancelling_the_order_cancels_its_unstarted_service_orders_and_reports_running_ones()
    {
        var c = Create();
        var (so, _, serviceLine) = await ConfirmedMixedAsync(c);
        c.Services.Orders.Add(new FakeServiceOrders.So { SaleOrder = so, Line = serviceLine, Number = "SVC-2026-00050", Quantity = 1m, Status = "IN_PROGRESS" });

        var result = (await c.H.Service.CancelWithResultAsync(so, User, "Customer withdrew"))!;

        c.Services.CancelCalls.Should().ContainSingle().Which.Should().Be((so, "Customer withdrew"));
        result.CancelledServiceOrders.Select(s => s.ServiceNumber).Should().Equal("SVC-2026-00001");
        result.RunningServiceOrders.Select(s => s.ServiceNumber).Should().Equal("SVC-2026-00050");
        result.RunningServiceOrders.Single().LineNumber.Should().Be(2);
        var note = c.H.Timeline.Single(e => e.EventType == "SO_SERVICE_ORDERS_CANCELLED").Notes!;
        note.Should().Contain("SVC-2026-00001").And.Contain("SVC-2026-00050 (IN_PROGRESS)");
        (await c.H.ReadAsync(so)).Status.Should().Be("CANCELLED");
    }

    [Fact]
    public async Task Cancelling_a_draft_asks_Material_for_nothing()
    {
        var c = Create();
        var so = await c.H.DraftAsync((c.ServiceVariant, null));

        await c.H.Service.CancelWithResultAsync(so, User, null);

        c.Services.CancelCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Without_MODULE_SERVICES_no_service_order_is_raised_and_the_service_line_still_gets_no_delivery()
    {
        var c = Create(servicesFeature: false);
        var (so, stockLine, serviceLine) = await ConfirmedMixedAsync(c);

        c.Services.EnsureCalls.Should().BeEmpty();
        var sent = (IReadOnlyList<SaleOrderLineRoute>)c.H.Creator.Invocations.Single().Arguments[2];
        sent.Select(r => r.SoLineUuid).Should().Equal(stockLine);
        c.H.Reserved.Should().OnlyContain(r => r.Line == stockLine);

        var model = (await c.H.Service.GetByIdAsync(so))!;
        model.Lines.Single(l => l.Uuid == serviceLine).IsService.Should().BeTrue();
        model.ServiceOrders.Should().BeEmpty();
        c.Services.EnsureCalls.Should().BeEmpty("the detail does not retry without the feature either");
    }

    [Fact]
    public async Task With_routes_off_the_service_line_is_still_not_reserved_and_still_gets_its_service_order()
    {
        var c = Create(logistics: false);
        var (so, stockLine, serviceLine) = await ConfirmedMixedAsync(c);

        c.H.Creator.Invocations.Should().BeEmpty();
        c.H.Reserved.Should().OnlyContain(r => r.Line == stockLine);
        c.Services.EnsureCalls.Should().ContainSingle().Which.Line.Should().Be(serviceLine);
        (await c.H.ReadAsync(so)).Lines.Single(l => l.UUID == serviceLine).FulfillmentMode.Should().Be("SERVICE");
    }

    [Fact]
    public async Task A_failed_creation_is_reported_on_confirm_and_retried_by_the_next_detail_load()
    {
        var c = Create();
        c.Services.ThrowOnEnsure = new InvalidOperationException("Material is down");
        var so = await c.H.DraftAsync((c.ServiceVariant, null));

        var result = (await c.H.Service.ConfirmWithResultAsync(so, User))!;

        result.Status.Should().Be("CONFIRMED");
        result.ServiceOrderCreationFailed.Should().BeTrue();
        result.ServiceOrderMessage.Should().NotBeNullOrWhiteSpace();
        c.H.Timeline.Should().Contain(e => e.EventType == "SO_SERVICE_ORDERS_FAILED");

        c.Services.ThrowOnEnsure = null;
        var model = (await c.H.Service.GetByIdAsync(so))!;

        model.ServiceOrders.Should().ContainSingle();
        c.Services.EnsureCalls.Should().HaveCount(2);

        // A service order cancelled by hand afterwards is not raised again behind anyone's back.
        c.Services.Orders.Single().Status = "CANCELLED";
        await c.H.Service.GetByIdAsync(so);
        c.Services.EnsureCalls.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_service_line_cannot_be_reserved_by_hand()
    {
        var c = Create();
        var (so, _, serviceLine) = await ConfirmedMixedAsync(c);
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(x => x.GetConfigAsync()).ReturnsAsync(new SMS.Modules.Demand.Models.SaleOrderConfigModel { ReservationTtlHours = 72 });
        var reservations = new SaleOrderReservationService(c.H.Db, c.H.Tenant, c.H.Stock.Object, config.Object, Mock.Of<IBackgroundJobClient>());

        var act = () => reservations.ReserveLineAsync(so, serviceLine, new SMS.Modules.Demand.Models.ReserveSaleOrderLineRequest(), User);

        await act.Should().ThrowAsync<SMS.Shared.Exceptions.BadRequestException>().WithMessage("*service*");
    }
}
