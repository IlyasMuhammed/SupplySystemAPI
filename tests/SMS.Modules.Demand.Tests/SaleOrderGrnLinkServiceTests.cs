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
using SMS.WorkflowEngine.Models;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P5-06 §6.4 / A29-P5-07 §13.3 — the sale order side of a GRN against a back-to-back
/// PO, with the reservation ledger mocked so each decision (how much, whether at all, what the line
/// becomes, what gets recorded) is pinned on its own. The real-ledger end-to-end run lives in
/// Warehouse's GrnApprovalTests, where the GRN approval that triggers all this actually happens.</summary>
public class SaleOrderGrnLinkServiceTests
{
    private const int Approver = 0;
    private static readonly Guid Warehouse = Guid.NewGuid();

    private sealed record Harness(
        DemandDbContext Db, SaleOrderGrnLinkService Service, Guid OrgId,
        Mock<IStockReservationService> Stock, Mock<ISaleOrderEmailService> Email, List<Job> CapturedJobs);

    private sealed record Seeded(Guid PoUuid, Guid OrderUuid, Guid LineUuid, Guid VariantUuid, Guid TraceId);

    private static (Mock<IBackgroundJobClient> Mock, List<Job> Captured) MockJobs()
    {
        var captured = new List<Job>();
        var mock = new Mock<IBackgroundJobClient>();
        mock.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => captured.Add(job))
            .Returns("fake-job-id");
        return (mock, captured);
    }

    private static Harness NewHarness(decimal available = 1000m, bool reserveSucceeds = true)
    {
        var orgId = Guid.NewGuid();
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = orgId });

        var stock = new Mock<IStockReservationService>();
        stock.Setup(s => s.GetAvailableAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<Guid> ids, Guid? wh, CancellationToken _) => Task.FromResult<IReadOnlyList<VariantAvailability>>(
                ids.Select(id => new VariantAvailability(id, wh, "Central", available)).ToList()));
        stock.Setup(s => s.ReserveAsync(
                It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ReservationRequest>>(),
                It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Returns((string _, Guid _, IReadOnlyList<ReservationRequest> reqs, int _, DateTime? _, CancellationToken _) =>
                Task.FromResult(new ReservationResult(reserveSucceeds, reqs.Select(r => new ReservationLineResult(
                    r.VariantUuid, r.SourceLineUuid, r.Quantity,
                    reserveSucceeds ? r.Quantity : 0m, reserveSucceeds ? 0m : r.Quantity, available, null)).ToList())));

        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { ReservationTtlHours = 72 });
        var email = new Mock<ISaleOrderEmailService>();
        var (jobs, captured) = MockJobs();

        var service = new SaleOrderGrnLinkService(
            db, stock.Object, config.Object, email.Object, jobs.Object, NullLogger<SaleOrderGrnLinkService>.Instance);

        return new Harness(db, service, orgId, stock, email, captured);
    }

    private static async Task<Seeded> SeedLinked(
        Harness h, decimal deficit = 40m, string orderStatus = "CONFIRMED", string lineStatus = "OPEN", bool linked = true)
    {
        var variant = Guid.NewGuid();
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00042", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = orderStatus, DeliveryMode = "SELF_PICKUP", CreatedBy = 11,
            TraceId = Guid.NewGuid(),
            Lines =
            {
                new SaleOrderLine
                {
                    VariantUuid = variant, Quantity = 100m, UnitPrice = 10m, LineTotal = 1000m,
                    FulfillmentMode = "BACK_TO_BACK", DeficitQty = deficit, Status = lineStatus
                }
            }
        };
        h.Db.SaleOrders.Add(order);
        await h.Db.SaveChangesAsync();
        var line = order.Lines.Single();

        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), PoNumber = "PO-2026-00099", SupplierId = Guid.NewGuid(), SupplierName = "Vendor",
            Status = "SENT", Source = linked ? "BACK_TO_BACK" : "MANUAL",
            LinkedSoId = linked ? order.Id : null, LinkedSoLineId = linked ? line.Id : null,
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        h.Db.PurchaseOrders.Add(po);
        await h.Db.SaveChangesAsync();
        if (linked)
        {
            line.LinkedPoId = po.Id;
            await h.Db.SaveChangesAsync();
        }
        h.Db.ChangeTracker.Clear();

        return new Seeded(po.UUID, order.UUID, line.UUID, variant, order.TraceId);
    }

    private static GrnReceipt Receipt(Seeded s, decimal qty, Guid? variant = null, string grnNumber = "GRN-2026-00007") =>
        new(s.PoUuid, Guid.NewGuid(), grnNumber, Warehouse, [new GrnReceiptLine(variant ?? s.VariantUuid, qty)], Approver);

    private static Task<SaleOrderLine> Line(Harness h, Seeded s) =>
        h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == s.LineUuid);

    private static List<TimelineEvent> TimelineEvents(Harness h) =>
        h.CapturedJobs.Where(j => j.Method.Name == "AppendAsync").Select(j => (TimelineEvent)j.Args[1]).ToList();

    // ── Linking ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_receipt_for_a_po_no_sale_order_is_waiting_on_does_nothing()
    {
        var h = NewHarness();
        var s = await SeedLinked(h, linked: false);

        var result = await h.Service.ReserveForGrnAsync(Receipt(s, 40m));

        result.Linked.Should().BeFalse();
        h.Stock.Verify(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ReservationRequest>>(),
            It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
        h.CapturedJobs.Should().BeEmpty();
        h.Email.Verify(e => e.SendGrnReceivedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);
    }

    // ── How much gets reserved ───────────────────────────────────────────────

    [Fact]
    public async Task Reserves_the_received_quantity_for_the_linked_line_and_marks_it_reserved_once_covered()
    {
        var h = NewHarness();
        var s = await SeedLinked(h, deficit: 40m);

        var result = await h.Service.ReserveForGrnAsync(Receipt(s, 40m));

        result.Should().BeEquivalentTo(new { Linked = true, SaleOrderUuid = s.OrderUuid, ReservedQty = 40m, RemainingDeficit = 0m, Note = (string?)null });
        var line = await Line(h, s);
        line.Status.Should().Be("RESERVED");
        line.DeficitQty.Should().Be(0m);
        h.Stock.Verify(x => x.ReserveAsync(
            ReservationSourceType.SalesOrder, s.OrderUuid,
            It.Is<IReadOnlyList<ReservationRequest>>(r => r.Count == 1 && r[0].VariantUuid == s.VariantUuid
                && r[0].WarehouseUuid == Warehouse && r[0].Quantity == 40m && r[0].SourceLineUuid == s.LineUuid),
            Approver, It.Is<DateTime?>(d => d != null), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_partial_receipt_reserves_what_arrived_and_leaves_the_line_open()
    {
        var h = NewHarness();
        var s = await SeedLinked(h, deficit: 40m);

        var result = await h.Service.ReserveForGrnAsync(Receipt(s, 25m));

        result.ReservedQty.Should().Be(25m);
        result.RemainingDeficit.Should().Be(15m);
        var line = await Line(h, s);
        line.Status.Should().Be("OPEN", "§6.4: the line stays OPEN until fully received");
        line.DeficitQty.Should().Be(15m);
    }

    [Fact]
    public async Task Never_reserves_more_than_the_line_still_lacks()
    {
        // §3.4 lets the team order 100 when the SO needs 40 — the other 60 are free stock.
        var h = NewHarness();
        var s = await SeedLinked(h, deficit: 40m);

        var result = await h.Service.ReserveForGrnAsync(Receipt(s, 100m));

        result.ReservedQty.Should().Be(40m);
        result.RemainingDeficit.Should().Be(0m);
        h.Stock.Verify(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(),
            It.Is<IReadOnlyList<ReservationRequest>>(r => r[0].Quantity == 40m),
            It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_split_line_already_reserved_from_stock_keeps_its_status_and_just_closes_its_deficit()
    {
        var h = NewHarness();
        var s = await SeedLinked(h, deficit: 40m, lineStatus: "RESERVED");

        await h.Service.ReserveForGrnAsync(Receipt(s, 40m));

        var line = await Line(h, s);
        line.Status.Should().Be("RESERVED");
        line.DeficitQty.Should().Be(0m);
    }

    // ── When the stock is not all there any more ─────────────────────────────

    [Fact]
    public async Task Reserves_only_what_is_still_free_when_some_was_taken_between_posting_and_reserving()
    {
        var h = NewHarness(available: 10m);
        var s = await SeedLinked(h, deficit: 40m);

        var result = await h.Service.ReserveForGrnAsync(Receipt(s, 40m));

        result.ReservedQty.Should().Be(10m);
        result.RemainingDeficit.Should().Be(30m);
        result.Note.Should().Contain("Only part");
        (await Line(h, s)).Status.Should().Be("OPEN");
    }

    [Fact]
    public async Task Reserves_nothing_when_none_of_the_received_stock_is_free_and_leaves_the_line_alone()
    {
        var h = NewHarness(available: 0m);
        var s = await SeedLinked(h, deficit: 40m);

        var result = await h.Service.ReserveForGrnAsync(Receipt(s, 40m));

        result.Linked.Should().BeTrue();
        result.ReservedQty.Should().Be(0m);
        result.RemainingDeficit.Should().Be(40m);
        result.Note.Should().Contain("None of the received stock");
        var line = await Line(h, s);
        line.Status.Should().Be("OPEN");
        line.DeficitQty.Should().Be(40m);
        h.Email.Verify(e => e.SendGrnReceivedAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<decimal>(), It.IsAny<decimal>()), Times.Never);
    }

    [Fact]
    public async Task A_refused_reservation_reserves_nothing_and_says_so()
    {
        var h = NewHarness(reserveSucceeds: false);
        var s = await SeedLinked(h, deficit: 40m);

        var result = await h.Service.ReserveForGrnAsync(Receipt(s, 40m));

        result.ReservedQty.Should().Be(0m);
        result.Note.Should().Contain("taken before");
        (await Line(h, s)).DeficitQty.Should().Be(40m);
    }

    // ── When there is nothing to reserve for ─────────────────────────────────

    [Theory]
    [InlineData("CANCELLED")]
    [InlineData("CLOSED")]
    [InlineData("DRAFT")]
    public async Task A_receipt_for_an_order_that_is_not_open_reserves_nothing(string status)
    {
        var h = NewHarness();
        var s = await SeedLinked(h, deficit: 40m, orderStatus: status);

        var result = await h.Service.ReserveForGrnAsync(Receipt(s, 40m));

        result.Linked.Should().BeTrue();
        result.ReservedQty.Should().Be(0m);
        result.Note.Should().Contain(status);
        h.Stock.Verify(x => x.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ReservationRequest>>(),
            It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_partially_fulfilled_order_still_gets_its_reservation()
    {
        var h = NewHarness();
        var s = await SeedLinked(h, deficit: 40m, orderStatus: "PARTIALLY_FULFILLED");

        (await h.Service.ReserveForGrnAsync(Receipt(s, 40m))).ReservedQty.Should().Be(40m);
    }

    [Fact]
    public async Task A_receipt_that_holds_none_of_the_lines_variant_reserves_nothing()
    {
        // The PO was edited after it was raised (§6.2) and now brings something else.
        var h = NewHarness();
        var s = await SeedLinked(h, deficit: 40m);

        var result = await h.Service.ReserveForGrnAsync(Receipt(s, 40m, variant: Guid.NewGuid()));

        result.ReservedQty.Should().Be(0m);
        result.Note.Should().Contain("holds none");
    }

    [Fact]
    public async Task A_line_whose_deficit_is_already_covered_reserves_nothing()
    {
        var h = NewHarness();
        var s = await SeedLinked(h, deficit: 0m);

        var result = await h.Service.ReserveForGrnAsync(Receipt(s, 40m));

        result.ReservedQty.Should().Be(0m);
        result.Note.Should().Contain("already covered");
    }

    // ── What gets recorded (§13.3 / §13.4) ───────────────────────────────────

    [Fact]
    public async Task Enqueues_GRN_FOR_SO_PO_then_SO_STOCK_RESERVED_on_the_orders_trace_with_the_org_explicit()
    {
        var h = NewHarness();
        var s = await SeedLinked(h, deficit: 40m);
        var receipt = Receipt(s, 40m);

        await h.Service.ReserveForGrnAsync(receipt);

        var jobs = h.CapturedJobs.Where(j => j.Method.Name == "AppendAsync").ToList();
        jobs.Should().HaveCount(2);
        jobs.Should().OnlyContain(j => (Guid)j.Args[0] == s.TraceId && j.Args.Count == 5 && (Guid)j.Args[4] == h.OrgId);

        var events = TimelineEvents(h);
        events[0].EventType.Should().Be("GRN_FOR_SO_PO");
        events[0].InterfaceCode.Should().Be("GRN");
        events[0].DocumentId.Should().Be(receipt.GrnUuid);
        events[0].DocumentNumber.Should().Be("GRN-2026-00007");
        events[0].Notes.Should().Be("40 received, 40 reserved for SO-2026-00042");
        events[1].EventType.Should().Be("SO_STOCK_RESERVED");
        events[1].InterfaceCode.Should().Be("SO");
        events[1].DocumentId.Should().Be(s.OrderUuid);
        events[1].Notes.Should().Be("40 from GRN GRN-2026-00007");
    }

    [Fact]
    public async Task Records_the_receipt_but_no_reservation_event_when_nothing_could_be_reserved()
    {
        var h = NewHarness(available: 0m);
        var s = await SeedLinked(h, deficit: 40m);

        await h.Service.ReserveForGrnAsync(Receipt(s, 40m));

        var events = TimelineEvents(h);
        events.Should().ContainSingle().Which.EventType.Should().Be("GRN_FOR_SO_PO");
        events[0].Notes.Should().Contain("40 received, 0 reserved").And.Contain("None of the received stock");
    }

    [Fact]
    public async Task Emails_the_creator_with_the_receipts_figures_once_something_was_reserved()
    {
        var h = NewHarness();
        var s = await SeedLinked(h, deficit: 40m);

        await h.Service.ReserveForGrnAsync(Receipt(s, 25m));

        h.Email.Verify(e => e.SendGrnReceivedAsync(s.OrderUuid, "GRN-2026-00007", 25m, 25m), Times.Once);
    }

    // ── A29-P5-07 §13.3 ──────────────────────────────────────────────────────

    [Fact]
    public void The_seven_13_3_event_types_are_all_named_once()
    {
        SaleOrderTimelineEventTypes.All.Should().BeEquivalentTo(
        [
            "SO_CREATED", "SO_CONFIRMED", "PO_CREATED_FROM_SO", "GRN_FOR_SO_PO",
            "SO_STOCK_RESERVED", "SO_FULFILLED", "SO_INVOICED"
        ]);
        SaleOrderTimelineEventTypes.All.Should().OnlyHaveUniqueItems();
    }
}
