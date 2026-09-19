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
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P4-07 §5.1/§5.2/§5.3 — each SendXxxAsync method: does it resolve the right
/// recipients, render a non-trivial body, write exactly one SaleOrderIntimations row, and hand the
/// send to ISaleOrderIntimationDispatchJob via Hangfire.</summary>
public class SaleOrderEmailServiceTests
{
    private const int Creator = 11;

    private sealed record Harness(
        DemandDbContext Db, SaleOrderEmailService Service, Guid OrgId,
        Mock<IStockReservationService> Stock, Mock<ISaleOrderConfigService> Config,
        Mock<IOrgChartService> OrgChart, Mock<IUserQueryService> Users,
        Mock<ISupplierNameLookupService> PartnerNames, List<Job> CapturedJobs);

    private static (Mock<IBackgroundJobClient> Mock, List<Job> Captured) MockJobs()
    {
        var captured = new List<Job>();
        var mock = new Mock<IBackgroundJobClient>();
        mock.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => captured.Add(job))
            .Returns("fake-job-id");
        return (mock, captured);
    }

    private static Harness NewHarness(int ttlHours = 72)
    {
        var orgId = Guid.NewGuid();
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = orgId });

        var stock = new Mock<IStockReservationService>();
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { ReservationTtlHours = ttlHours });
        var orgChart = new Mock<IOrgChartService>();
        var users = new Mock<IUserQueryService>();
        var partnerNames = new Mock<ISupplierNameLookupService>();
        // Default: resolves nothing (falls back to "(unknown customer)") — tests that care about a
        // specific name override this explicitly.
        partnerNames.Setup(p => p.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, string>());
        var (jobsMock, captured) = MockJobs();

        var service = new SaleOrderEmailService(
            db, stock.Object, config.Object, orgChart.Object, users.Object, partnerNames.Object,
            jobsMock.Object, NullLogger<SaleOrderEmailService>.Instance);

        return new Harness(db, service, orgId, stock, config, orgChart, users, partnerNames, captured);
    }

    private static async Task<Guid> SeedOrder(
        Harness h, params (Guid VariantUuid, decimal Qty, string? Mode, decimal? Deficit, decimal? Available, Guid? SelectedSupplierId)[] lines) =>
        await SeedOrder(h, intimationDepartmentId: null, lines);

    private static async Task<Guid> SeedOrder(
        Harness h, int? intimationDepartmentId,
        params (Guid VariantUuid, decimal Qty, string? Mode, decimal? Deficit, decimal? Available, Guid? SelectedSupplierId)[] lines)
    {
        var order = new SaleOrder
        {
            SoNumber = $"SO-{Guid.NewGuid():N}"[..12], PartnerId = Guid.NewGuid(),
            OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = Creator,
            IntimationDepartmentId = intimationDepartmentId
        };
        foreach (var (variantUuid, qty, mode, deficit, available, supplierId) in lines)
            order.Lines.Add(new SaleOrderLine
            {
                VariantUuid = variantUuid, Quantity = qty, UnitPrice = 10m, LineTotal = qty * 10m,
                FulfillmentMode = mode, DeficitQty = deficit, AvailableQtyAtConfirm = available,
                SelectedSupplierId = supplierId, Status = "RESERVED"
            });
        h.Db.SaleOrders.Add(order);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return order.UUID;
    }

    // ── SendConfirmationAsync ────────────────────────────────────────────────

    [Fact]
    public async Task SendConfirmation_writes_a_queued_row_and_enqueues_the_dispatch_job()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 10m, "IN_STOCK", 0m, 10m, null));

        await h.Service.SendConfirmationAsync(orderUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.EventType.Should().Be("SO_CONFIRMED");
        row.Status.Should().Be("QUEUED");
        row.Recipients.Should().Contain("creator@x.com");
        row.Subject.Should().Contain("Confirmed");
        row.BodyHtml.Should().Contain("reserved from stock");
        row.HangfireJobId.Should().Be("fake-job-id");

        h.CapturedJobs.Should().ContainSingle(j => j.Method.Name == "DispatchAsync");
    }

    [Fact]
    public async Task SendConfirmation_resolves_both_department_head_and_creator_as_recipients()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(5)).ReturnsAsync(new UserIdentity(99, "Dept Head"));
        h.Users.Setup(u => u.GetUserEmailAsync(99)).ReturnsAsync("head@x.com");

        var order = new SaleOrder
        {
            SoNumber = "SO-DEPT-TEST", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP",
            CreatedBy = Creator, IntimationDepartmentId = 5
        };
        h.Db.SaleOrders.Add(order);
        await h.Db.SaveChangesAsync();

        await h.Service.SendConfirmationAsync(order.UUID);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.Recipients.Should().Contain("creator@x.com").And.Contain("head@x.com");
    }

    [Fact]
    public async Task SendConfirmation_marks_the_row_failed_when_no_recipient_resolves()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync((string?)null);
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 10m, "IN_STOCK", 0m, 10m, null));

        await h.Service.SendConfirmationAsync(orderUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.Status.Should().Be("FAILED");
        row.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        h.CapturedJobs.Should().BeEmpty("nothing was resolvable to send to, so no send was ever queued");
    }

    [Fact]
    public async Task SendConfirmation_does_nothing_for_an_unknown_sale_order()
    {
        var h = NewHarness();

        var act = () => h.Service.SendConfirmationAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
        (await h.Db.SaleOrderIntimations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SendConfirmation_line_table_reflects_each_lines_outcome()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var orderUuid = await SeedOrder(h,
            (Guid.NewGuid(), 10m, "IN_STOCK", 0m, 10m, null),
            (Guid.NewGuid(), 20m, "BACK_TO_BACK", 20m, 0m, null));

        await h.Service.SendConfirmationAsync(orderUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.BodyHtml.Should().Contain("reserved from stock");
        row.BodyHtml.Should().Contain("require procurement");
    }

    // ── A29-P4-08 §5.2 — email template content validation ──────────────────

    [Fact]
    public async Task SendConfirmation_subject_matches_the_5_2_format_exactly()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 10m, "IN_STOCK", 0m, 10m, null));
        var soNumber = (await h.Db.SaleOrders.AsNoTracking().SingleAsync(o => o.UUID == orderUuid)).SoNumber;

        await h.Service.SendConfirmationAsync(orderUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.Subject.Should().Be($"[SMS] Sale Order {soNumber} Confirmed - Availability Summary");
    }

    [Fact]
    public async Task SendConfirmation_includes_the_self_pickup_note_when_delivery_mode_is_self_pickup()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        // SeedOrder's SaleOrder defaults DeliveryMode to SELF_PICKUP.
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 10m, "IN_STOCK", 0m, 10m, null));

        await h.Service.SendConfirmationAsync(orderUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.BodyHtml.Should().Contain("Customer will collect. No shipment required.");
    }

    [Fact]
    public async Task SendConfirmation_omits_the_self_pickup_note_when_delivery_mode_is_ship()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var order = new SaleOrder
        {
            SoNumber = "SO-SHIP-TEST", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = "CONFIRMED", DeliveryMode = "SHIP", CreatedBy = Creator,
            Lines = { new SaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 5m, UnitPrice = 10m, LineTotal = 50m, FulfillmentMode = "IN_STOCK", DeficitQty = 0m, AvailableQtyAtConfirm = 5m, Status = "RESERVED" } }
        };
        h.Db.SaleOrders.Add(order);
        await h.Db.SaveChangesAsync();

        await h.Service.SendConfirmationAsync(order.UUID);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.BodyHtml.Should().NotContain("Customer will collect");
    }

    [Fact]
    public async Task SendConfirmation_html_encodes_a_partner_name_containing_markup()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 10m, "IN_STOCK", 0m, 10m, null));
        var partnerId = (await h.Db.SaleOrders.AsNoTracking().SingleAsync(o => o.UUID == orderUuid)).PartnerId;
        h.PartnerNames.Setup(p => p.GetNamesAsync(It.Is<IReadOnlyList<Guid>>(l => l.Contains(partnerId))))
            .ReturnsAsync(new Dictionary<Guid, string> { [partnerId] = "<script>alert(1)</script> & Co." });

        await h.Service.SendConfirmationAsync(orderUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.BodyHtml.Should().NotContain("<script>");
        row.BodyHtml.Should().Contain("&lt;script&gt;").And.Contain("&amp; Co.");
    }

    [Fact]
    public async Task SendConfirmation_line_table_includes_every_5_2_column_header()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 10m, "IN_STOCK", 0m, 10m, null));

        await h.Service.SendConfirmationAsync(orderUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        foreach (var header in new[] { "Product", "Ordered", "Available", "Deficit", "Mode", "Action taken" })
            row.BodyHtml.Should().Contain(header);
    }

    // ── SendPoCreatedAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task SendPoCreated_writes_a_row_naming_the_po_and_its_lines()
    {
        var h = NewHarness();
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(3)).ReturnsAsync(new UserIdentity(88, "Dept Head"));
        h.Users.Setup(u => u.GetUserEmailAsync(88)).ReturnsAsync("dept@x.com");
        var orderUuid = await SeedOrder(h, intimationDepartmentId: 3, (Guid.NewGuid(), 10m, "BACK_TO_BACK", 10m, 0m, null));

        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), PoNumber = "PO-2026-00099", SupplierId = Guid.NewGuid(),
            SupplierName = "TechSupply Co.", Status = "DRAFT", TotalAmount = 400m, CreatedBy = Creator,
            Lines = { new PurchaseOrderLine { ItemDescription = "4mm cable", Quantity = 40m, UnitPrice = 10m, LineTotal = 400m } }
        };
        h.Db.PurchaseOrders.Add(po);
        await h.Db.SaveChangesAsync();

        await h.Service.SendPoCreatedAsync(orderUuid, po.UUID);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.EventType.Should().Be("PO_CREATED");
        row.BodyHtml.Should().Contain("PO-2026-00099").And.Contain("4mm cable").And.Contain("TechSupply Co.");
        h.CapturedJobs.Should().ContainSingle(j => j.Method.Name == "DispatchAsync");
    }

    [Fact]
    public async Task SendPoCreated_does_nothing_when_the_po_is_unknown()
    {
        var h = NewHarness();
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 10m, "BACK_TO_BACK", 10m, 0m, null));

        await h.Service.SendPoCreatedAsync(orderUuid, Guid.NewGuid());

        (await h.Db.SaleOrderIntimations.CountAsync()).Should().Be(0);
    }

    // ── SendDropShipAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task SendDropShip_names_the_resolved_drop_ship_suppliers()
    {
        var h = NewHarness();
        var supplierId = Guid.NewGuid();
        h.PartnerNames.Setup(p => p.GetNamesAsync(It.Is<IReadOnlyList<Guid>>(l => l.Contains(supplierId))))
            .ReturnsAsync(new Dictionary<Guid, string> { [supplierId] = "DirectShip Ltd." });
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 5m, "DROP_SHIP", 5m, null, supplierId));

        await h.Service.SendDropShipAsync(orderUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.EventType.Should().Be("DROP_SHIP");
        row.BodyHtml.Should().Contain("DirectShip Ltd.");
    }

    // ── SendReservedAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task SendReserved_is_skipped_when_nothing_is_actively_held()
    {
        var h = NewHarness();
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 10m, "IN_STOCK", 0m, 10m, null));
        h.Stock.Setup(s => s.GetBySourceAsync(ReservationSourceType.SalesOrder, orderUuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await h.Service.SendReservedAsync(orderUuid);

        (await h.Db.SaleOrderIntimations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SendReserved_reports_the_configured_ttl()
    {
        var h = NewHarness(ttlHours: 48);
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 10m, "IN_STOCK", 0m, 10m, null));
        h.Stock.Setup(s => s.GetBySourceAsync(ReservationSourceType.SalesOrder, orderUuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ReservationSummary(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 10m, "ACTIVE", null)]);

        await h.Service.SendReservedAsync(orderUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.EventType.Should().Be("RESERVED");
        row.BodyHtml.Should().Contain("48 hour");
    }

    // ── SendPoApprovedAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task SendPoApproved_is_a_no_op_when_the_po_is_not_linked_to_any_so_line()
    {
        var h = NewHarness();
        var po = new PurchaseOrder { UUID = Guid.NewGuid(), PoNumber = "PO-STANDALONE", SupplierId = Guid.NewGuid(), SupplierName = "X", Status = "APPROVED", CreatedBy = Creator };
        h.Db.PurchaseOrders.Add(po);
        await h.Db.SaveChangesAsync();

        await h.Service.SendPoApprovedAsync(po.UUID);

        (await h.Db.SaleOrderIntimations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SendPoApproved_writes_a_row_when_a_line_is_linked()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");

        var po = new PurchaseOrder { UUID = Guid.NewGuid(), PoNumber = "PO-LINKED", SupplierId = Guid.NewGuid(), SupplierName = "Vendor X", Status = "APPROVED", CreatedBy = Creator };
        h.Db.PurchaseOrders.Add(po);
        await h.Db.SaveChangesAsync();

        var order = new SaleOrder
        {
            SoNumber = "SO-LINK-TEST", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = Creator,
            Lines = { new SaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 10m, UnitPrice = 10m, LineTotal = 100m, LinkedPoId = po.Id, Status = "OPEN" } }
        };
        h.Db.SaleOrders.Add(order);
        await h.Db.SaveChangesAsync();

        await h.Service.SendPoApprovedAsync(po.UUID);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.EventType.Should().Be("PO_APPROVED");
        row.SaleOrderId.Should().Be(order.Id);
        row.Recipients.Should().Contain("creator@x.com");
    }

    [Fact]
    public async Task SendPoApproved_finds_the_order_through_the_pos_own_link_even_with_no_back_pointer_on_a_line()
    {
        // A29-P5-11 — a PO split off for another supplier is linked to the order only from its own
        // side; the line's LinkedPoId names the original PO alone.
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 100m, "BACK_TO_BACK", 40m, 0m, null));
        var order = await h.Db.SaleOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.UUID == orderUuid);
        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), PoNumber = "PO-SPLIT", SupplierId = Guid.NewGuid(), SupplierName = "Vendor B", Status = "APPROVED",
            CreatedBy = Creator, LinkedSoId = order.Id, LinkedSoLineId = order.Lines.Single().Id
        };
        h.Db.PurchaseOrders.Add(po);
        await h.Db.SaveChangesAsync();

        await h.Service.SendPoApprovedAsync(po.UUID);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        (row.EventType, row.SaleOrderId).Should().Be(("PO_APPROVED", order.Id));
    }

    // ── SendGrnReceivedAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task SendGrnReceived_writes_a_row_to_the_creator_with_the_receipts_figures()
    {
        // A29-P5-06 — was a placeholder until the GRN-to-SO link existed; now real.
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 100m, "BACK_TO_BACK", 60m, 0m, null));

        await h.Service.SendGrnReceivedAsync(orderUuid, "GRN-2026-00007", receivedQty: 40m, reservedQty: 40m);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.EventType.Should().Be("GRN_RECEIVED");
        row.Recipients.Should().Be("creator@x.com");
        row.Subject.Should().Contain("Goods Received");
        row.BodyHtml.Should().Contain("GRN-2026-00007").And.Contain("40").And.Contain("60 unit(s) are still awaited");
        h.CapturedJobs.Should().ContainSingle(j => j.Method.Name == "DispatchAsync");
    }

    [Fact]
    public async Task SendGrnReceived_says_the_order_is_ready_once_nothing_is_still_awaited()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 100m, "BACK_TO_BACK", 0m, 0m, null));

        await h.Service.SendGrnReceivedAsync(orderUuid, "GRN-2026-00008", receivedQty: 40m, reservedQty: 40m);

        (await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync()).BodyHtml.Should().Contain("ready to fulfil");
    }

    [Fact]
    public async Task SendGrnReceived_does_nothing_for_an_unknown_sale_order()
    {
        var h = NewHarness();

        var act = () => h.Service.SendGrnReceivedAsync(Guid.NewGuid(), "GRN-X", 1m, 1m);

        await act.Should().NotThrowAsync();
        (await h.Db.SaleOrderIntimations.CountAsync()).Should().Be(0);
    }

    // ── SendExpiringAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task SendExpiring_writes_a_row_when_the_reservation_resolves()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var orderUuid = await SeedOrder(h, (Guid.NewGuid(), 10m, "IN_STOCK", 0m, 10m, null));
        var reservationUuid = Guid.NewGuid();
        var expiresAt = DateTime.UtcNow.AddHours(10);
        h.Stock.Setup(s => s.GetByUuidAsync(reservationUuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExpiringReservation(reservationUuid, h.OrgId, ReservationSourceType.SalesOrder, orderUuid, null, Guid.NewGuid(), 10m, expiresAt));

        await h.Service.SendExpiringAsync(reservationUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.EventType.Should().Be("EXPIRING");
        row.BodyHtml.Should().Contain("expires at");
    }

    [Fact]
    public async Task SendExpiring_does_nothing_when_the_reservation_is_not_found()
    {
        var h = NewHarness();
        h.Stock.Setup(s => s.GetByUuidAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExpiringReservation?)null);

        var act = () => h.Service.SendExpiringAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
        (await h.Db.SaleOrderIntimations.CountAsync()).Should().Be(0);
    }

    // ── SendFulfilledAsync (A29-P6-06 §7.6) ──────────────────────────────────

    [Fact]
    public async Task SendFulfilled_writes_a_queued_row_naming_the_completing_delivery_line_by_line()
    {
        var h = NewHarness();
        h.Users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        h.OrgChart.Setup(o => o.GetDepartmentHeadAsync(7)).ReturnsAsync(new UserIdentity(99, "Dept Head"));
        h.Users.Setup(u => u.GetUserEmailAsync(99)).ReturnsAsync("sales-head@x.com");
        var orderUuid = await SeedOrder(h, intimationDepartmentId: 7, (Guid.NewGuid(), 100m, "IN_STOCK", 0m, 100m, null));
        var line = await h.Db.SaleOrderLines.SingleAsync(l => l.SaleOrder.UUID == orderUuid);
        line.FulfilledQty = 100m;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        await h.Service.SendFulfilledAsync(orderUuid, "DLV-2026-00003", 30m);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.EventType.Should().Be("SO_FULFILLED");
        row.Status.Should().Be("QUEUED");
        row.Recipients.Should().Contain("creator@x.com").And.Contain("sales-head@x.com");
        row.Subject.Should().Contain("Fulfilled");
        row.BodyHtml.Should().Contain("DLV-2026-00003").And.Contain("100 of 100 delivered").And.Contain("collected");
        h.CapturedJobs.Should().ContainSingle(j => j.Method.Name == "DispatchAsync");
    }

    [Fact]
    public async Task SendFulfilled_does_nothing_for_an_unknown_order()
    {
        var h = NewHarness();

        var act = () => h.Service.SendFulfilledAsync(Guid.NewGuid(), "DLV-2026-00003", 30m);

        await act.Should().NotThrowAsync();
        (await h.Db.SaleOrderIntimations.CountAsync()).Should().Be(0);
    }
}
