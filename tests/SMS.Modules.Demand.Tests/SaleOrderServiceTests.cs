using FluentAssertions;
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
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P3-06/P3-07 §4.1/§4.2/§4.5 — Create/Update/GetById/GetList, document numbering,
/// price auto-resolution, confirm/cancel, timeline and availability preview.</summary>
public class SaleOrderServiceTests
{
    private const int User = 7;
    private static readonly Guid Currency = Guid.NewGuid();

    private sealed record Harness(
        DemandDbContext Db, SaleOrderService Service, Guid OrgId, string DbName,
        Mock<IPricingService> Pricing, Mock<IDocumentNumberGenerator> Numbers,
        Mock<IStockReservationService> Stock, Mock<ITimelineService> Timeline, List<Job> CapturedJobs,
        Mock<IAvailabilityCheckService> AvailabilityCheck, Mock<IPurchaseOrderService> PurchaseOrders,
        Mock<ISaleOrderEmailService> EmailService);

    // Captures every Job handed to IBackgroundJobClient.Create — the real interface member the
    // Enqueue<T>() extension method delegates to, matching TimelineEventWiringTests' own helper.
    private static (Mock<IBackgroundJobClient> Mock, List<Job> Captured) MockJobs()
    {
        var captured = new List<Job>();
        var mock = new Mock<IBackgroundJobClient>();
        mock.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => captured.Add(job))
            .Returns("fake-job-id");
        return (mock, captured);
    }

    private static Harness NewHarness(Guid? baseCurrency = null, string? soNumber = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(baseCurrency ?? Currency);

        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync("SO", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(soNumber ?? "SO-2026-00001");

        var pricing = new Mock<IPricingService>();
        var stock = new Mock<IStockReservationService>();
        var timeline = new Mock<ITimelineService>();
        var (jobsMock, captured) = MockJobs();
        // Confirm's own §4.3 scenario logic is AvailabilityCheckServiceTests' job — this harness
        // just needs Confirm to succeed so SaleOrderService's own orchestration (the header
        // transition, the timeline event) can be tested in isolation from it.
        var availabilityCheck = new Mock<IAvailabilityCheckService>();
        availabilityCheck.Setup(a => a.CheckAndReserveAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((IReadOnlyList<LineReservation>)new List<LineReservation>());
        var purchaseOrders = new Mock<IPurchaseOrderService>();
        var emailService = new Mock<ISaleOrderEmailService>();

        var service = new SaleOrderService(
            db, tenant, orgCurrency.Object, numbers.Object, pricing.Object, stock.Object, timeline.Object,
            jobsMock.Object, availabilityCheck.Object, purchaseOrders.Object, emailService.Object);
        return new Harness(db, service, tenant.OrganizationId, dbName, pricing, numbers, stock, timeline, captured, availabilityCheck, purchaseOrders, emailService);
    }

    private static void SetupPrice(Mock<IPricingService> pricing, Guid variantUuid, decimal price, bool found = true) =>
        pricing.Setup(p => p.ResolveSalePriceAsync(variantUuid, It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalePriceResolution(found, found ? price : null, found ? Currency : null, found ? PriceResolutionTier.DefaultSelling : null, null));

    private static CreateSaleOrderRequest ValidCreate(params CreateSaleOrderLineRequest[] lines) => new()
    {
        PartnerId = Guid.NewGuid(), CurrencyId = Currency, DeliveryMode = "SELF_PICKUP",
        Lines = lines.Length > 0 ? [.. lines] : [new CreateSaleOrderLineRequest { VariantUuid = Guid.NewGuid(), Quantity = 1 }]
    };

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_generates_the_so_number_and_starts_in_draft()
    {
        var h = NewHarness(soNumber: "SO-2026-00042");
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 100m);

        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 2 }), User);

        var model = await h.Service.GetByIdAsync(uuid);
        model!.SoNumber.Should().Be("SO-2026-00042");
        model.Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task GetById_describes_each_line_from_the_catalogue_when_a_resolver_is_available()
    {
        // A29-P6-08: a line stores only the variant id; the screen needs a name.
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 100m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 2 }), User);

        var resolver = new Mock<IProductVariantResolver>();
        resolver.Setup(r => r.DescribeVariantsAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Contains(variant))))
            .ReturnsAsync(new Dictionary<Guid, VariantDescription>
            {
                [variant] = new(variant, Guid.NewGuid(), "DELL-5450-I7", "i7 / 16GB", "Dell Latitude 5450", false, "Piece")
            });
        var describing = new SaleOrderService(
            h.Db, new StaticTenantContext { OrganizationId = h.OrgId }, Mock.Of<IOrganizationCurrencyService>(),
            Mock.Of<IDocumentNumberGenerator>(), Mock.Of<IPricingService>(), Mock.Of<IStockReservationService>(),
            Mock.Of<ITimelineService>(), Mock.Of<IBackgroundJobClient>(), Mock.Of<IAvailabilityCheckService>(),
            Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(), resolver.Object);

        var line = (await describing.GetByIdAsync(uuid))!.Lines.Single();
        line.VariantSku.Should().Be("DELL-5450-I7");
        line.VariantName.Should().Be("i7 / 16GB");
        line.ItemDescription.Should().Be("Dell Latitude 5450 - i7 / 16GB (DELL-5450-I7)");
        line.UnitOfMeasure.Should().Be("Piece");

        // Without a resolver — or for a variant it does not know — the line still reads, just unnamed.
        (await h.Service.GetByIdAsync(uuid))!.Lines.Single().ItemDescription.Should().BeNull();
    }

    [Fact]
    public async Task Create_resolves_the_unit_price_through_pricing_service_never_trusting_a_client_supplied_one()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 250m);

        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 4 }), User);

        var model = await h.Service.GetByIdAsync(uuid);
        model!.Lines.Should().ContainSingle();
        model.Lines[0].UnitPrice.Should().Be(250m);
    }

    [Fact]
    public async Task Create_computes_line_total_with_discount_and_tax()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 100m);

        // 10 * 100 * (1 - 0.10) * (1 + 0.05) = 945
        var uuid = await h.Service.CreateAsync(ValidCreate(
            new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 10, DiscountPercent = 10, TaxPercent = 5 }), User);

        var model = await h.Service.GetByIdAsync(uuid);
        model!.Lines[0].LineTotal.Should().Be(945m);
    }

    [Fact]
    public async Task Create_aggregates_header_totals_from_every_line()
    {
        var h = NewHarness();
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        SetupPrice(h.Pricing, v1, 100m);
        SetupPrice(h.Pricing, v2, 50m);

        // Line 1: 2 * 100 = 200 base, no discount/tax -> total 200.
        // Line 2: 4 * 50 = 200 base, 10% discount -> 180 after discount, no tax -> total 180.
        var uuid = await h.Service.CreateAsync(ValidCreate(
            new CreateSaleOrderLineRequest { VariantUuid = v1, Quantity = 2 },
            new CreateSaleOrderLineRequest { VariantUuid = v2, Quantity = 4, DiscountPercent = 10 }), User);

        var model = await h.Service.GetByIdAsync(uuid);
        model!.Subtotal.Should().Be(400m);
        model.DiscountAmount.Should().Be(20m);
        model.TaxAmount.Should().Be(0m);
        model.GrandTotal.Should().Be(380m);
        model.GrandTotal.Should().Be(model.Subtotal - model.DiscountAmount + model.TaxAmount, "§4.1's own formula");
    }

    [Fact]
    public async Task Create_falls_back_to_the_orgs_base_currency_when_none_supplied()
    {
        var h = NewHarness(baseCurrency: Currency);
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var req = ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 });
        req.CurrencyId = null;

        var uuid = await h.Service.CreateAsync(req, User);

        (await h.Service.GetByIdAsync(uuid))!.CurrencyId.Should().Be(Currency);
    }

    [Fact]
    public async Task Create_fails_with_no_partner()
    {
        var h = NewHarness();
        var req = ValidCreate();
        req.PartnerId = Guid.Empty;

        var act = () => h.Service.CreateAsync(req, User);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Create_fails_with_an_invalid_delivery_mode()
    {
        var h = NewHarness();
        var req = ValidCreate();
        req.DeliveryMode = "BOGUS";

        var act = () => h.Service.CreateAsync(req, User);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Create_fails_for_ship_with_no_shipping_address()
    {
        var h = NewHarness();
        var req = ValidCreate();
        req.DeliveryMode = "SHIP";
        req.ShippingAddressId = null;

        var act = () => h.Service.CreateAsync(req, User);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Create_fails_with_no_lines()
    {
        var h = NewHarness();
        var req = ValidCreate();
        req.Lines = [];

        var act = () => h.Service.CreateAsync(req, User);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Create_fails_when_no_price_can_be_resolved_for_a_line()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 0m, found: false);

        var act = () => h.Service.CreateAsync(
            ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Create_fails_for_a_zero_quantity_line()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);

        var act = () => h.Service.CreateAsync(
            ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 0 }), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── Update ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_replaces_the_lines_and_recomputes_totals_for_a_draft_order()
    {
        var h = NewHarness();
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        SetupPrice(h.Pricing, v1, 100m);
        SetupPrice(h.Pricing, v2, 40m);

        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = v1, Quantity = 1 }), User);

        var updated = await h.Service.UpdateAsync(uuid, new UpdateSaleOrderRequest
        {
            CurrencyId = Currency, DeliveryMode = "SELF_PICKUP",
            Lines = [new CreateSaleOrderLineRequest { VariantUuid = v2, Quantity = 3 }]
        }, User);

        updated.Should().BeTrue();
        var model = await h.Service.GetByIdAsync(uuid);
        model!.Lines.Should().ContainSingle();
        model.Lines[0].VariantUuid.Should().Be(v2);
        model.Subtotal.Should().Be(120m);
    }

    [Fact]
    public async Task Update_fails_for_an_order_that_is_no_longer_draft()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);

        var order = await h.Db.SaleOrders.FirstAsync(x => x.UUID == uuid);
        order.Status = "CONFIRMED";
        await h.Db.SaveChangesAsync();

        var act = () => h.Service.UpdateAsync(uuid, new UpdateSaleOrderRequest
        {
            CurrencyId = Currency, DeliveryMode = "SELF_PICKUP",
            Lines = [new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }]
        }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Update_returns_false_for_an_unknown_order()
    {
        var h = NewHarness();

        var updated = await h.Service.UpdateAsync(Guid.NewGuid(), new UpdateSaleOrderRequest
        {
            CurrencyId = Currency, DeliveryMode = "SELF_PICKUP",
            Lines = [new CreateSaleOrderLineRequest { VariantUuid = Guid.NewGuid(), Quantity = 1 }]
        }, User);

        updated.Should().BeFalse();
    }

    // ── Get ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetById_returns_null_for_an_unknown_order()
    {
        var h = NewHarness();

        (await h.Service.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task List_omits_lines_but_getbyid_includes_them()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);

        var list = await h.Service.GetListAsync(new SaleOrderListFilter());

        list.Data.Should().ContainSingle();
        list.Data[0].Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task List_filters_by_status_partner_and_search()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var partner = Guid.NewGuid();
        var req = ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 });
        req.PartnerId = partner;
        var uuid = await h.Service.CreateAsync(req, User);
        var created = await h.Service.GetByIdAsync(uuid);

        (await h.Service.GetListAsync(new SaleOrderListFilter { Status = "DRAFT" })).Data.Should().ContainSingle();
        (await h.Service.GetListAsync(new SaleOrderListFilter { Status = "CANCELLED" })).Data.Should().BeEmpty();
        (await h.Service.GetListAsync(new SaleOrderListFilter { PartnerId = partner })).Data.Should().ContainSingle();
        (await h.Service.GetListAsync(new SaleOrderListFilter { PartnerId = Guid.NewGuid() })).Data.Should().BeEmpty();
        (await h.Service.GetListAsync(new SaleOrderListFilter { Search = created!.SoNumber })).Data.Should().ContainSingle();
    }

    [Fact]
    public async Task Rules_scoped_to_a_different_organization_are_invisible()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);

        var otherOrg = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        await using var otherDb = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(h.DbName).Options, otherOrg);
        var otherOrgCurrency = new Mock<IOrganizationCurrencyService>();
        var otherNumbers = new Mock<IDocumentNumberGenerator>();
        var otherPricing = new Mock<IPricingService>();
        var otherStock = new Mock<IStockReservationService>();
        var otherTimeline = new Mock<ITimelineService>();
        var (otherJobsMock, _) = MockJobs();
        var otherAvailabilityCheck = new Mock<IAvailabilityCheckService>();
        var otherPurchaseOrders = new Mock<IPurchaseOrderService>();
        var otherEmailService = new Mock<ISaleOrderEmailService>();
        var otherService = new SaleOrderService(
            otherDb, otherOrg, otherOrgCurrency.Object, otherNumbers.Object, otherPricing.Object,
            otherStock.Object, otherTimeline.Object, otherJobsMock.Object, otherAvailabilityCheck.Object,
            otherPurchaseOrders.Object, otherEmailService.Object);

        (await otherService.GetByIdAsync(uuid)).Should().BeNull();
        (await otherService.GetListAsync(new SaleOrderListFilter())).Data.Should().BeEmpty();
    }

    // A29-P5-08 §13.7 — the timeline endpoint must not become the way round the tenant filter: another
    // organization's order id resolves to no trace, so nothing is even asked of the timeline store.
    [Fact]
    public async Task Another_organizations_sale_order_has_no_timeline_and_the_timeline_store_is_never_asked()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);

        var otherOrg = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        await using var otherDb = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(h.DbName).Options, otherOrg);
        var otherTimeline = new Mock<ITimelineService>();
        var (otherJobs, _) = MockJobs();
        var otherService = new SaleOrderService(
            otherDb, otherOrg, Mock.Of<IOrganizationCurrencyService>(), Mock.Of<IDocumentNumberGenerator>(),
            Mock.Of<IPricingService>(), Mock.Of<IStockReservationService>(), otherTimeline.Object, otherJobs.Object,
            Mock.Of<IAvailabilityCheckService>(), Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>());

        (await otherService.GetTimelineAsync(uuid)).Should().BeNull();
        otherTimeline.Verify(t => t.GetTimelineDetailAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task The_sale_order_timeline_is_read_from_the_orders_own_trace()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);
        var traceId = (await h.Db.SaleOrders.AsNoTracking().SingleAsync(o => o.UUID == uuid)).TraceId;
        var detail = new TimelineDetail { TraceId = traceId };
        h.Timeline.Setup(t => t.GetTimelineDetailAsync(traceId)).ReturnsAsync(detail);

        (await h.Service.GetTimelineAsync(uuid)).Should().BeSameAs(detail);
    }

    // ── Confirm / Cancel ──────────────────────────────────────────────────────

    [Fact]
    public async Task Confirm_transitions_a_draft_order_to_confirmed_and_enqueues_a_timeline_event()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);
        h.CapturedJobs.Clear(); // drop the SO_CREATED enqueue from Create so this test only asserts on Confirm's own

        var confirmed = await h.Service.ConfirmAsync(uuid, User);

        confirmed.Should().BeTrue();
        (await h.Service.GetByIdAsync(uuid))!.Status.Should().Be("CONFIRMED");
        // A29-P4-03/P4-07 — Confirm always enqueues the timeline event via Hangfire; the auto-PO
        // job only fires per line with a deficit (none here), and the confirmation email
        // (A29-P4-07) is now called directly through ISaleOrderEmailService rather than enqueued
        // as a bare Hangfire job, so exactly one Hangfire job is expected here.
        h.CapturedJobs.Should().HaveCount(1);
    }

    [Fact]
    public async Task Confirm_fails_for_an_order_that_is_not_draft()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);
        await h.Service.ConfirmAsync(uuid, User);

        var act = () => h.Service.ConfirmAsync(uuid, User);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Confirm_returns_false_for_an_unknown_order()
    {
        var h = NewHarness();

        (await h.Service.ConfirmAsync(Guid.NewGuid(), User)).Should().BeFalse();
    }

    // ── A29-P5-07 §13.3/§13.4 — what Confirm records ─────────────────────────

    private static List<TimelineEvent> TimelineEvents(Harness h) =>
        h.CapturedJobs.Where(j => j.Method.Name == "AppendAsync").Select(j => (TimelineEvent)j.Args[1]).ToList();

    [Fact]
    public async Task Confirm_notes_the_reserved_and_deficit_totals_on_SO_CONFIRMED()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 100 }), User);
        var line = await h.Db.SaleOrderLines.FirstAsync();
        line.DeficitQty = 40m; // the mocked availability check sets nothing itself
        await h.Db.SaveChangesAsync();
        h.AvailabilityCheck.Setup(a => a.CheckAndReserveAsync(uuid, User)).ReturnsAsync(
            (IReadOnlyList<LineReservation>)[new LineReservation(line.UUID, variant, 60m, Guid.NewGuid(), "Central")]);
        h.CapturedJobs.Clear();

        await h.Service.ConfirmAsync(uuid, User);

        var confirmed = TimelineEvents(h).Single(e => e.EventType == "SO_CONFIRMED");
        confirmed.InterfaceCode.Should().Be("SO");
        confirmed.DocumentId.Should().Be(uuid);
        confirmed.Notes.Should().Be("60 reserved, 40 deficit");
    }

    [Fact]
    public async Task Confirm_enqueues_one_SO_STOCK_RESERVED_per_reservation_after_SO_CONFIRMED()
    {
        var h = NewHarness();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        SetupPrice(h.Pricing, a, 10m);
        SetupPrice(h.Pricing, b, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(
            new CreateSaleOrderLineRequest { VariantUuid = a, Quantity = 60 },
            new CreateSaleOrderLineRequest { VariantUuid = b, Quantity = 10 }), User);
        h.AvailabilityCheck.Setup(x => x.CheckAndReserveAsync(uuid, User)).ReturnsAsync(
            (IReadOnlyList<LineReservation>)
            [
                new LineReservation(Guid.NewGuid(), a, 60m, Guid.NewGuid(), "Central"),
                new LineReservation(Guid.NewGuid(), b, 10m, null, null)
            ]);
        h.CapturedJobs.Clear();

        await h.Service.ConfirmAsync(uuid, User);

        var events = TimelineEvents(h);
        events.Select(e => e.EventType).Should().Equal("SO_CONFIRMED", "SO_STOCK_RESERVED", "SO_STOCK_RESERVED");
        events[1].Notes.Should().Be("60 reserved (Central)");
        events[2].Notes.Should().Be("10 reserved");
        events.Should().OnlyContain(e => e.DocumentId == uuid && e.InterfaceCode == "SO" && e.PerformedBy == User);
    }

    [Fact]
    public async Task Confirm_enqueues_no_SO_STOCK_RESERVED_when_nothing_was_reserved()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);
        h.CapturedJobs.Clear();

        await h.Service.ConfirmAsync(uuid, User);

        TimelineEvents(h).Select(e => e.EventType).Should().Equal("SO_CONFIRMED");
    }

    [Fact]
    public async Task Confirm_enqueues_an_auto_po_job_for_every_line_left_with_a_deficit()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);
        // The mocked AvailabilityCheckService does not compute a real deficit, so this test sets
        // one directly on the entity beforehand — Confirm reads it after CheckAndReserveAsync
        // returns, whatever set it.
        var line = await h.Db.SaleOrderLines.FirstAsync();
        line.DeficitQty = 5m;
        await h.Db.SaveChangesAsync();
        h.CapturedJobs.Clear();

        await h.Service.ConfirmAsync(uuid, User);

        h.CapturedJobs.Select(j => j.Method.Name).Should().Contain("CreateForDeficitAsync");
    }

    [Fact]
    public async Task Confirm_sends_the_confirmation_email_even_with_no_deficit_at_all()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);
        h.CapturedJobs.Clear();

        await h.Service.ConfirmAsync(uuid, User);

        // A29-P4-07 — the confirmation email is now a direct call to ISaleOrderEmailService, not a
        // bare Hangfire enqueue (see ISaleOrderEmailService's own remarks on why it enqueues its
        // own dispatch job internally rather than being enqueued itself).
        h.EmailService.Verify(e => e.SendConfirmationAsync(uuid), Times.Once);
        h.CapturedJobs.Select(j => j.Method.Name).Should().NotContain("CreateForDeficitAsync", "no line had a deficit");
    }

    [Fact]
    public async Task Cancel_transitions_a_draft_order_to_cancelled()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);

        var cancelled = await h.Service.CancelAsync(uuid, User, "Customer changed their mind");

        cancelled.Should().BeTrue();
        (await h.Service.GetByIdAsync(uuid))!.Status.Should().Be("CANCELLED");
    }

    [Fact]
    public async Task Cancel_works_from_confirmed_too()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);
        await h.Service.ConfirmAsync(uuid, User);

        (await h.Service.CancelAsync(uuid, User, null)).Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_fails_for_an_already_cancelled_order()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);
        await h.Service.CancelAsync(uuid, User, null);

        var act = () => h.Service.CancelAsync(uuid, User, null);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Cancel_enqueues_a_cancellation_email()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);
        h.CapturedJobs.Clear();

        await h.Service.CancelAsync(uuid, User, "Changed their mind");

        h.CapturedJobs.Select(j => j.Method.Name).Should().Contain("SendCancellationEmailAsync");
    }

    [Fact]
    public async Task Cancel_cancels_every_linked_draft_po()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);

        var po = new SMS.Modules.Demand.Domain.PurchaseOrder
        {
            UUID = Guid.NewGuid(), PoNumber = "PO-2026-00099", SupplierId = Guid.NewGuid(),
            SupplierName = "Test Vendor", Status = "DRAFT", CreatedBy = User
        };
        h.Db.PurchaseOrders.Add(po);
        await h.Db.SaveChangesAsync();
        var line = await h.Db.SaleOrderLines.FirstAsync();
        line.LinkedPoId = po.Id;
        await h.Db.SaveChangesAsync();

        await h.Service.CancelAsync(uuid, User, "No longer needed");

        h.PurchaseOrders.Verify(p => p.CancelAsync(po.UUID, User, It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_never_touches_a_linked_po_that_is_not_draft()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);

        var po = new SMS.Modules.Demand.Domain.PurchaseOrder
        {
            UUID = Guid.NewGuid(), PoNumber = "PO-2026-00100", SupplierId = Guid.NewGuid(),
            SupplierName = "Test Vendor", Status = "APPROVED", CreatedBy = User
        };
        h.Db.PurchaseOrders.Add(po);
        await h.Db.SaveChangesAsync();
        var line = await h.Db.SaleOrderLines.FirstAsync();
        line.LinkedPoId = po.Id;
        await h.Db.SaveChangesAsync();

        await h.Service.CancelAsync(uuid, User, "No longer needed");

        h.PurchaseOrders.Verify(p => p.CancelAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_with_no_linked_po_at_all_never_calls_the_po_service()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);

        await h.Service.CancelAsync(uuid, User, null);

        h.PurchaseOrders.Verify(p => p.CancelAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string>()), Times.Never);
    }

    // ── Timeline ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTimeline_resolves_the_orders_own_trace_id_and_reads_through_the_timeline_service()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 1 }), User);
        var order = await h.Db.SaleOrders.FirstAsync(x => x.UUID == uuid);
        var detail = new TimelineDetail { TraceId = order.TraceId, FirstEventAt = DateTime.UtcNow, LastEventAt = DateTime.UtcNow };
        h.Timeline.Setup(t => t.GetTimelineDetailAsync(order.TraceId)).ReturnsAsync(detail);

        var result = await h.Service.GetTimelineAsync(uuid);

        result.Should().BeSameAs(detail);
    }

    [Fact]
    public async Task GetTimeline_returns_null_for_an_unknown_order()
    {
        var h = NewHarness();

        (await h.Service.GetTimelineAsync(Guid.NewGuid())).Should().BeNull();
    }

    // ── Availability ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAvailability_previews_each_lines_shortfall_without_reserving_anything()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(
            ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 30 }), User);

        var warehouse = Guid.NewGuid();
        h.Stock.Setup(s => s.GetAvailableAsync(
                It.Is<IReadOnlyList<Guid>>(v => v.Contains(variant)), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new VariantAvailability(variant, warehouse, "Central", 50m)]);

        var result = await h.Service.GetAvailabilityAsync(uuid);

        result.Should().ContainSingle();
        result![0].OrderedQty.Should().Be(30m);
        result[0].AvailableQty.Should().Be(50m);
        result[0].DeficitQty.Should().Be(0m);
        h.Stock.Verify(s => s.ReserveAsync(
            It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ReservationRequest>>(), It.IsAny<int>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never, "a preview must never reserve anything");
    }

    [Fact]
    public async Task GetAvailability_reports_a_deficit_when_ordered_exceeds_available()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(
            ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 100 }), User);

        h.Stock.Setup(s => s.GetAvailableAsync(It.IsAny<IReadOnlyList<Guid>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new VariantAvailability(variant, Guid.NewGuid(), "Central", 60m)]);

        var result = await h.Service.GetAvailabilityAsync(uuid);

        result![0].DeficitQty.Should().Be(40m);
    }

    [Fact]
    public async Task GetAvailability_treats_a_variant_with_no_stock_record_as_zero_available()
    {
        var h = NewHarness();
        var variant = Guid.NewGuid();
        SetupPrice(h.Pricing, variant, 10m);
        var uuid = await h.Service.CreateAsync(
            ValidCreate(new CreateSaleOrderLineRequest { VariantUuid = variant, Quantity = 5 }), User);

        h.Stock.Setup(s => s.GetAvailableAsync(It.IsAny<IReadOnlyList<Guid>>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]); // no stock record at all

        var result = await h.Service.GetAvailabilityAsync(uuid);

        result![0].AvailableQty.Should().Be(0m);
        result[0].DeficitQty.Should().Be(5m);
    }

    [Fact]
    public async Task GetAvailability_returns_null_for_an_unknown_order()
    {
        var h = NewHarness();

        (await h.Service.GetAvailabilityAsync(Guid.NewGuid())).Should().BeNull();
    }
}
