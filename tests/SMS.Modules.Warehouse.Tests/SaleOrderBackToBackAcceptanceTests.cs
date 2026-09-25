using System.Diagnostics;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Events;
using SMS.Modules.Warehouse.Models;
using SMS.Modules.Warehouse.Repositories;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;
using InventoryWarehouse = SMS.Modules.Inventory.Domain.Warehouse;

namespace SMS.Modules.Warehouse.Tests;

/// <summary>
/// A29-P5-09 — Phase 5's acceptance criteria (TC-05, 06, 07, 13, 14, 15) run end to end through the
/// real services, not each piece in isolation: real sale order service, availability check and
/// reservation ledger, the auto-PO job with real supplier selection, PO service and GRN approval
/// handler. What is stood in for is only what sits outside the addendum — the workflow engine, user
/// and supplier lookups, and the timeline's own storage (WorkflowEngine has its own tests for that) —
/// and Hangfire itself: enqueued jobs are queued and run on demand, exactly as a worker would run
/// them later, so "non-blocking" can be observed rather than assumed.
/// </summary>
public class SaleOrderBackToBackAcceptanceTests
{
    private const int Seller = 11;
    private const int Approver = 21;

    // ── Stand-ins ────────────────────────────────────────────────────────────

    private sealed class NoLedger : IInventoryLedgerService
    {
        public Task CreateEntryAsync(LedgerEntryCommand command, IDbContextTransaction? transaction = null) => Task.CompletedTask;
        public Task<decimal> GetCurrentBalanceAsync(int productId, int warehouseId) => Task.FromResult(0m);
        public Task<LedgerPagedResult> GetLedgerAsync(LedgerFilterDto filter) => Task.FromResult(new LedgerPagedResult());
    }

    private sealed class RecordingTimeline : ITimelineAppendJob
    {
        public List<(Guid TraceId, TimelineEvent Event, Guid? Org)> Appended { get; } = [];
        public Task AppendAsync(Guid traceId, TimelineEvent newEvent, string? chainRootType = null, string? chainRootRef = null)
        { Appended.Add((traceId, newEvent, null)); return Task.CompletedTask; }
        public Task AppendAsync(Guid traceId, TimelineEvent newEvent, string? chainRootType, string? chainRootRef, Guid organizationId)
        { Appended.Add((traceId, newEvent, organizationId)); return Task.CompletedTask; }
    }

    // Hangfire, minus the waiting: enqueued jobs sit in a queue until Drain runs them, oldest first,
    // including any they enqueue in turn.
    private sealed class JobQueue : IBackgroundJobClient
    {
        private readonly Queue<Job> _queue = new();
        public int Pending => _queue.Count;
        public string Create(Job job, IState state) { _queue.Enqueue(job); return $"job-{_queue.Count}"; }
        public bool ChangeState(string jobId, IState state, string expectedState) => true;

        public async Task DrainAsync(Func<Type, object> resolve)
        {
            while (_queue.Count > 0)
            {
                var job = _queue.Dequeue();
                if (job.Method.Invoke(resolve(job.Type), job.Args.ToArray()) is Task t) await t;
            }
        }
    }

    // ── The whole wiring ─────────────────────────────────────────────────────

    private sealed class Scenario
    {
        public Guid Org { get; } = Guid.NewGuid();
        public Guid Customer { get; } = Guid.NewGuid();
        public Guid Currency { get; } = Guid.NewGuid();
        public Guid Variant { get; } = Guid.NewGuid();
        public Guid WarehouseUuid { get; } = Guid.NewGuid();
        public Guid SupplierA { get; } = Guid.NewGuid();
        public Guid SupplierB { get; } = Guid.NewGuid();
        public Guid SupplierC { get; } = Guid.NewGuid();

        public DemandDbContext Demand = null!;
        public InventoryDbContext Inv = null!;
        public WarehouseDbContext Wh = null!;
        public JobQueue Jobs { get; } = new();
        public RecordingTimeline Timeline { get; } = new();
        public Mock<INotificationService> Notifications { get; } = new();
        public Mock<IWorkflowActionService> Workflow { get; } = new();
        public Mock<IVariantSupplierService> Rates { get; } = new();

        public SaleOrderService SaleOrders = null!;
        public PurchaseOrderService PurchaseOrders = null!;
        public GrnService Grns = null!;
        public GrnRepository GrnRepo = null!;
        public GrnStatusHandler GrnHandler = null!;
        public AutoPoCreationJob AutoPoJob = null!;
        public SaleOrderIntimationDispatchJob DispatchJob = null!;

        public object Resolve(Type type) =>
            type == typeof(ITimelineAppendJob) ? Timeline
            : type == typeof(IAutoPoCreationJob) ? AutoPoJob
            : type == typeof(ISaleOrderIntimationDispatchJob) ? DispatchJob
            : throw new NotSupportedException($"No job registered for {type.Name}");

        public Task Drain() => Jobs.DrainAsync(Resolve);

        public static Scenario Build(
            string selectionMode = "BEST_MATCH", string approvalMode = "DRAFT_ONLY", decimal onHand = 60m)
        {
            var s = new Scenario();
            var tenant = new StaticTenantContext { OrganizationId = s.Org };
            var dbName = Guid.NewGuid().ToString();

            s.Demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
            s.Inv = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options, tenant);
            s.Wh = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
            s.SeedCatalogue(onHand);

            var stock = new StockReservationService(s.Inv);

            var config = new Mock<ISaleOrderConfigService>();
            config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel
            {
                AutoPoEnabled = true, SupplierSelectionMode = selectionMode, AutoPoApprovalMode = approvalMode,
                ReservationTtlHours = 72, IntimationDepartmentId = 5, EmailIntimationEnabled = true
            });

            var users = new Mock<IUserQueryService>();
            users.Setup(u => u.GetUserEmailAsync(It.IsAny<int>())).ReturnsAsync("user@x.com");
            var orgChart = new Mock<IOrgChartService>();
            orgChart.Setup(o => o.GetDepartmentHeadAsync(5)).ReturnsAsync(new UserIdentity(88, "Dept Head"));
            var names = new Mock<ISupplierNameLookupService>();
            names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new Dictionary<Guid, string>
            {
                [s.Customer] = "ACME Traders", [s.SupplierA] = "TechSupply Co.", [s.SupplierB] = "Budget Wires", [s.SupplierC] = "Prime Cables"
            });
            s.Notifications.Setup(n => n.SendEmailAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);

            // One vendor at 25 unless a test says otherwise (TC-06 sets several).
            s.Rates.Setup(r => r.GetDefaultSupplierIdAsync(s.Variant)).ReturnsAsync((Guid?)null);
            s.Rates.Setup(r => r.GetComparisonAsync(s.Variant))
                .ReturnsAsync(new List<RateComparisonRowModel> { Row(s.SupplierA, "TechSupply Co.", 25m, "A", 5) });

            var poRepo = new PurchaseOrderRepository(s.Demand, NullLogger<PurchaseOrderRepository>.Instance, s.Inv);
            s.PurchaseOrders = new PurchaseOrderService(
                poRepo, s.Workflow.Object, Mock.Of<IWorkflowInboxService>(), s.Jobs, Mock.Of<ITimelineService>());

            var email = new SaleOrderEmailService(
                s.Demand, stock, config.Object, orgChart.Object, users.Object, names.Object, s.Jobs, NullLogger<SaleOrderEmailService>.Instance);
            var selection = new SupplierSelectionService(
                config.Object, s.Rates.Object, orgChart.Object, s.Notifications.Object, Mock.Of<IProductVariantResolver>(),
                NullLogger<SupplierSelectionService>.Instance);
            var autoPo = new AutoPurchaseOrderService(
                s.Demand, poRepo, s.PurchaseOrders, config.Object, names.Object, s.Jobs, NullLogger<AutoPurchaseOrderService>.Instance);
            s.AutoPoJob = new AutoPoCreationJob(
                s.Demand, config.Object, selection, autoPo, email, NullLogger<AutoPoCreationJob>.Instance);
            s.DispatchJob = new SaleOrderIntimationDispatchJob(
                s.Demand, s.Notifications.Object, NullLogger<SaleOrderIntimationDispatchJob>.Instance);

            var pricing = new Mock<IPricingService>();
            pricing.Setup(p => p.ResolveSalePriceAsync(s.Variant, It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SalePriceResolution(true, 40m, s.Currency, PriceResolutionTier.DefaultSelling, null));
            var numbers = new Mock<IDocumentNumberGenerator>();
            numbers.Setup(n => n.NextAsync("SO", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>())).ReturnsAsync("SO-2026-00042");
            s.SaleOrders = new SaleOrderService(
                s.Demand, tenant, Mock.Of<IOrganizationCurrencyService>(), numbers.Object, pricing.Object, stock,
                Mock.Of<ITimelineService>(), s.Jobs, new AvailabilityCheckService(s.Demand, stock, config.Object),
                s.PurchaseOrders, email);

            var link = new SaleOrderGrnLinkService(s.Demand, stock, config.Object, email, s.Jobs, NullLogger<SaleOrderGrnLinkService>.Instance);
            s.GrnHandler = new GrnStatusHandler(
                s.Wh, s.Demand, s.Inv, new EfGrnInventoryPoster(s.Inv, new NoLedger()),
                new IGrnEventPublisher[] { new SaleOrderReservationGrnEventPublisher(s.Wh, link) },
                NullLogger<GrnStatusHandler>.Instance);
            s.GrnRepo = new GrnRepository(s.Wh, s.Demand);
            s.Grns = new GrnService(s.GrnRepo, s.Workflow.Object, Mock.Of<IDocumentStatusService>(), s.Jobs);

            // The workflow engine's part, reduced to what it does to a document's status when the last
            // approver approves: PO -> APPROVED, GRN -> APPROVED through GrnStatusHandler (which posts
            // the stock and fans out to the publishers).
            s.Workflow.Setup(w => w.ApproveByDocumentAsync("PO", It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()))
                .Returns(async (string _, Guid id, int _, string? _) =>
                {
                    var po = await s.Demand.PurchaseOrders.FirstAsync(p => p.UUID == id);
                    po.Status = "APPROVED";
                    await s.Demand.SaveChangesAsync();
                    return Guid.NewGuid();
                });
            s.Workflow.Setup(w => w.ApproveByDocumentAsync("GRN", It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<string?>()))
                .Returns(async (string _, Guid id, int _, string? _) =>
                {
                    await s.GrnHandler.UpdateStatusAsync(id, "APPROVED");
                    return Guid.NewGuid();
                });

            return s;
        }

        private void SeedCatalogue(decimal onHand)
        {
            var product = new Product { Uuid = Guid.NewGuid(), Sku = "CBL", Name = "4mm cable", UomCode = "MTR", Status = "ACTIVE", IsActive = true, CreatedBy = 1 };
            Inv.Products.Add(product);
            var wh = new InventoryWarehouse { Uuid = WarehouseUuid, Code = "WH1", Name = "Central", IsActive = true, CreatedBy = 1 };
            Inv.Warehouses.Add(wh);
            Inv.SaveChanges();
            var variant = new ProductVariant
            {
                Uuid = Variant, ProductId = product.Id, Sku = "CBL-4MM", VariantName = "Default", IsDefault = true,
                IsActive = true, PurchasePrice = 25m, CreatedBy = 1
            };
            Inv.ProductVariants.Add(variant);
            Inv.SaveChanges();
            if (onHand > 0)
            {
                Inv.InventoryItems.Add(new InventoryItem { Uuid = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = wh.Id, QtyOnHand = onHand });
                Inv.SaveChanges();
            }
        }

        // ── Steps ────────────────────────────────────────────────────────────

        public Task<Guid> CreateOrder(decimal qty = 100m) => SaleOrders.CreateAsync(new CreateSaleOrderRequest
        {
            PartnerId = Customer, CurrencyId = Currency, DeliveryMode = "SELF_PICKUP",
            IntimationDepartmentId = 5,
            Lines = [new CreateSaleOrderLineRequest { VariantUuid = Variant, Quantity = qty }]
        }, Seller);

        public async Task<Guid> CreateAndConfirm(decimal qty = 100m)
        {
            var uuid = await CreateOrder(qty);
            await SaleOrders.ConfirmAsync(uuid, Seller);
            return uuid;
        }

        public Task<SaleOrder> Order(Guid uuid) =>
            Demand.SaleOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.UUID == uuid);

        public Task<PurchaseOrder> OnlyPo() =>
            Demand.PurchaseOrders.AsNoTracking().Include(p => p.Lines).SingleAsync();

        public async Task SendPo(Guid poUuid)
        {
            await PurchaseOrders.ApproveAsync(poUuid, Approver);
            await PurchaseOrders.SendAsync(poUuid, contactMobile: null, Seller);
        }

        // A GRN for the whole PO, accepted in full, approved through the real handler.
        public async Task<Guid> ReceiveAndApprove(Guid poUuid, decimal qty)
        {
            var grnUuid = await Grns.CreateAsync(new CreateGrnRequest { PoUuid = poUuid, WarehouseUuid = WarehouseUuid, ReceivedAt = DateTime.UtcNow }, Seller);
            var grn = await Wh.Grns.Include(g => g.Lines).SingleAsync(g => g.UUID == grnUuid);
            await GrnRepo.UpdateLineAsync(grnUuid, grn.Lines.Single().UUID,
                new UpdateGrnLineRequest { QtyReceived = qty, QtyAccepted = qty, QtyRejected = 0, UnitCost = 25m }, Seller);
            grn = await Wh.Grns.SingleAsync(g => g.UUID == grnUuid);
            grn.Status = "PENDING_APPROVAL"; // the workflow engine's step, before the approver acts
            await Wh.SaveChangesAsync();
            await Grns.ApproveAsync(grnUuid, Approver);
            return grnUuid;
        }

        public IEnumerable<string> Types => Timeline.Appended.Select(a => a.Event.EventType);
    }

    // ── TC-05 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC05_60_available_SO_for_100_confirm_reserves_60_raises_a_draft_PO_for_40_and_the_email_shows_both()
    {
        var s = Scenario.Build(onHand: 60m);

        var uuid = await s.CreateAndConfirm(100m);
        await s.Drain();

        var order = await s.Order(uuid);
        var line = order.Lines.Single();
        (line.FulfillmentMode, line.Status, line.DeficitQty).Should().Be(("SPLIT", "RESERVED", 40m));

        var hold = await s.Inv.StockReservations.AsNoTracking().SingleAsync();
        (hold.SourceType, hold.ReservedQty, hold.Status).Should().Be(("SALES_ORDER", 60m, "ACTIVE"));

        var po = await s.OnlyPo();
        (po.Status, po.Source).Should().Be(("DRAFT", "BACK_TO_BACK"));
        po.Lines.Single().Quantity.Should().Be(40m);
        po.Lines.Single().ItemDescription.Should().Contain("4mm cable");

        var confirmation = await s.Demand.SaleOrderIntimations.AsNoTracking().SingleAsync(i => i.EventType == "SO_CONFIRMED");
        confirmation.BodyHtml.Should().Contain("60").And.Contain("reserved from stock").And.Contain("require procurement");
        (await s.Demand.SaleOrderIntimations.AsNoTracking().AnyAsync(i => i.EventType == "PO_CREATED")).Should().BeTrue();
        (await s.Demand.SaleOrderIntimations.AsNoTracking().Select(i => i.Status).Distinct().ToListAsync())
            .Should().Equal("SENT");
    }

    // ── TC-06 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC06_best_match_with_several_vendors_raises_the_po_to_the_optimal_one()
    {
        // A: price 30, grade A -> 5*0.6 + (1/3)*0.4 = 3.13   B: price 20, grade D -> 2*0.6 + 1*0.4 = 1.60
        // C: price 25, grade B -> 4*0.6 + (1/2)*0.4 = 2.60   -> A wins despite being the dearest.
        var s = Scenario.Build();
        s.Rates.Setup(r => r.GetComparisonAsync(s.Variant)).ReturnsAsync(new List<RateComparisonRowModel>
        {
            Row(s.SupplierB, "Budget Wires", 20m, "D", 5),
            Row(s.SupplierC, "Prime Cables", 25m, "B", 5),
            Row(s.SupplierA, "TechSupply Co.", 30m, "A", 5)
        });

        await s.CreateAndConfirm(100m);
        await s.Drain();

        var po = await s.OnlyPo();
        po.SupplierId.Should().Be(s.SupplierA);
        po.AutoSelectedSupplierId.Should().Be(s.SupplierA);
        po.AutoGeneratedPrice.Should().Be(30m);
    }

    private static RateComparisonRowModel Row(Guid supplier, string name, decimal price, string? grade, int lead) => new()
    {
        Uuid = Guid.NewGuid(), SupplierId = supplier, SupplierName = name, VendorUnitCost = price, ScorecardGrade = grade, LeadTimeDays = lead
    };

    // ── TC-07 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC07_DRAFT_ONLY_po_is_not_sent_until_manual_action_and_edits_are_tracked_against_what_the_system_chose()
    {
        var s = Scenario.Build(approvalMode: "DRAFT_ONLY");
        await s.CreateAndConfirm(100m);
        await s.Drain();
        var poUuid = (await s.OnlyPo()).UUID;

        // Left alone: still a DRAFT, never submitted or sent.
        (await s.OnlyPo()).Status.Should().Be("DRAFT");
        s.Workflow.Verify(w => w.SubmitAsync(It.IsAny<SubmitDocumentCommand>()), Times.Never);

        // The supply team orders 50 instead of 40, and negotiates the price down.
        var changes = await s.PurchaseOrders.UpdateAsync(poUuid, new PatchPoRequest
        {
            Lines = [new CreatePoLineRequest { VariantUuid = s.Variant, ItemDescription = "4mm cable", Quantity = 50m, UnitPrice = 22m }]
        }, Seller);

        changes.Select(c => c.Field).Should().Contain(f => f.StartsWith("Quantity")).And.Contain(f => f.StartsWith("Unit price"));
        var po = await s.OnlyPo();
        po.Status.Should().Be("DRAFT", "editing a draft does not send it");
        (po.Lines.Single().Quantity, po.Lines.Single().UnitPrice, po.TotalAmount).Should().Be((50m, 22m, 1100m));
        (po.AutoGeneratedQty, po.AutoGeneratedPrice).Should().Be((40m, 25m), "what the system chose is kept");
        var tracked = (await s.PurchaseOrders.GetByIdAsync(poUuid))!.AutoGenerated!;
        (tracked.QtyModified, tracked.PriceModified, tracked.SupplierModified).Should().Be((true, true, false));
        (await s.Order((await s.Demand.SaleOrders.AsNoTracking().SingleAsync()).UUID)).Lines.Single().Margin
            .Should().Be(18m, "the sale order line follows the PO: sells at 40, now costs 22");
        s.Workflow.Verify(w => w.SubmitAsync(It.IsAny<SubmitDocumentCommand>()), Times.Never);
    }

    // ── TC-13 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC13_confirming_with_a_deficit_auto_creates_a_po_whose_trace_id_is_the_sale_orders_and_PO_CREATED_FROM_SO_is_in_the_timeline()
    {
        var s = Scenario.Build();

        var uuid = await s.CreateAndConfirm(100m);
        await s.Drain();

        var order = await s.Order(uuid);
        var po = await s.OnlyPo();
        po.TraceId.Should().Be(order.TraceId);
        var created = s.Timeline.Appended.Should().ContainSingle(a => a.Event.EventType == "PO_CREATED_FROM_SO").Subject;
        created.TraceId.Should().Be(order.TraceId);
        created.Event.DocumentId.Should().Be(po.UUID);
        created.Event.Notes.Should().Be("40 units of CBL-4MM → TechSupply Co.");
        created.Org.Should().Be(s.Org, "§13.7: the org is handed to the job explicitly");
    }

    // ── TC-14 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC14_SPLIT_to_PO_approved_to_GRN_leaves_a_chronological_timeline_of_seven_or_more_events_on_one_trace_linking_every_document()
    {
        var s = Scenario.Build();

        var uuid = await s.CreateAndConfirm(100m);
        await s.Drain();
        var po = await s.OnlyPo();
        await s.SendPo(po.UUID);
        var grnUuid = await s.ReceiveAndApprove(po.UUID, 40m);
        await s.Drain();

        var order = await s.Order(uuid);

        // Every document's events sit on the sale order's own trace.
        s.Timeline.Appended.Should().OnlyContain(a => a.TraceId == order.TraceId);

        var events = s.Timeline.Appended.Select(a => a.Event).ToList();
        events.Should().HaveCountGreaterThanOrEqualTo(7);
        events.Select(e => e.EventType).Distinct().Should().Contain(
        [
            "SO_CREATED", "SO_CONFIRMED", "SO_STOCK_RESERVED", "PO_CREATED_FROM_SO",
            "PO_APPROVED", "GRN_FOR_SO_PO", "GRN_APPROVED"
        ]);
        events.Select(e => e.EventType).Should().ContainInOrder(
            "SO_CREATED", "SO_CONFIRMED", "SO_STOCK_RESERVED", "PO_CREATED_FROM_SO", "PO_APPROVED", "GRN_FOR_SO_PO");

        // Chronological, and each of the three documents is a linked subject.
        events.Select(e => e.OccurredAt).Should().BeInAscendingOrder();
        events.Select(e => e.InterfaceCode).Distinct().Should().Contain(["SO", "PO", "GRN"]);
        events.Single(e => e.EventType == "GRN_FOR_SO_PO").DocumentId.Should().Be(grnUuid);
        events.Where(e => e.EventType == "SO_STOCK_RESERVED").Select(e => e.Notes)
            .Should().Equal("60 reserved (Central)", "40 from GRN " + (await s.Wh.Grns.AsNoTracking().SingleAsync()).GrnNumber);

        // And the result the chain adds up to: everything reserved for the order.
        var item = await s.Inv.InventoryItems.AsNoTracking().SingleAsync();
        (item.QtyOnHand, item.QtyReserved).Should().Be((100m, 100m));
        var line = order.Lines.Single();
        (line.Status, line.DeficitQty).Should().Be(("RESERVED", 0m));
        (line.Margin, line.MarginPercent).Should().Be((15m, 37.5m));
        (await s.Demand.SaleOrderIntimations.AsNoTracking().Select(i => i.EventType).ToListAsync())
            .Should().Contain(["SO_CONFIRMED", "PO_CREATED", "GRN_RECEIVED"]);
    }

    // ── TC-15 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC15_confirm_returns_in_under_2_seconds_having_done_nothing_slow_inline_and_the_timeline_event_lands_within_5()
    {
        var s = Scenario.Build();
        var uuid = await s.CreateOrder(100m);
        s.Timeline.Appended.Clear();
        var clock = Stopwatch.StartNew();

        await s.SaleOrders.ConfirmAsync(uuid, Seller);

        var responded = clock.Elapsed;
        responded.Should().BeLessThan(TimeSpan.FromSeconds(2));

        // Non-blocking: at the moment Confirm returns, the PO does not exist and nothing has been
        // written to the timeline — both are queued work, not part of the request.
        (await s.Demand.PurchaseOrders.CountAsync()).Should().Be(0);
        s.Timeline.Appended.Should().BeEmpty();
        s.Jobs.Pending.Should().BeGreaterThan(0);

        await s.Drain();

        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        s.Types.Should().Contain("SO_CONFIRMED");
        (await s.Demand.PurchaseOrders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_failing_timeline_write_can_never_undo_a_confirmed_order()
    {
        // §13.6 "timeline writes always via Hangfire, never in the main transaction": the order is
        // committed before any job runs, so a job that throws leaves it confirmed.
        var s = Scenario.Build();
        var uuid = await s.CreateOrder(100m);

        await s.SaleOrders.ConfirmAsync(uuid, Seller);
        var jobs = s.Jobs;

        (await s.Order(uuid)).Status.Should().Be("CONFIRMED");
        jobs.Pending.Should().BeGreaterThan(0);
        await ((Func<Task>)(() => jobs.DrainAsync(_ => throw new InvalidOperationException("timeline store down"))))
            .Should().ThrowAsync<InvalidOperationException>();
        (await s.Order(uuid)).Status.Should().Be("CONFIRMED");
    }
}
