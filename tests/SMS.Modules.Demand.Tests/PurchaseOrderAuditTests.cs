using System.Security.Claims;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Controllers;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P5-11 §6.2 — "audit log of changes": the service puts what changed on the PO's
/// timeline, and the controller writes one audit row per changed field. Split as well.</summary>
public class PurchaseOrderAuditTests
{
    private const int Editor = 22;

    private sealed record Harness(
        DemandDbContext Db, PurchaseOrderService Service, PurchaseOrdersController Controller,
        Mock<IAuditService> Audit, List<Job> Jobs, Guid PoUuid, Guid Variant, string PoNumber);

    private static async Task<Harness> NewHarness(bool audited = true)
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid() });
        var repo = new PurchaseOrderRepository(db, NullLogger<PurchaseOrderRepository>.Instance);

        var variant = Guid.NewGuid();
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00042", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = 11, TraceId = Guid.NewGuid(),
            Lines = { new SaleOrderLine { VariantUuid = variant, Quantity = 100m, UnitPrice = 40m, LineTotal = 4000m, DeficitQty = 40m, Status = "OPEN" } }
        };
        db.SaleOrders.Add(order);
        await db.SaveChangesAsync();
        var created = await repo.CreateFromSaleOrderDeficitAsync(new SaleOrderDeficitPo(
            "BACK_TO_BACK", "DRAFT", order.TraceId, order.Id, order.Lines.Single().Id, order.SoNumber,
            Guid.NewGuid(), "Vendor A", variant, 40m, 25m, null), 11);
        if (!audited)
        {
            var po = await db.PurchaseOrders.SingleAsync(p => p.UUID == created.Uuid);
            po.Source = "MANUAL"; po.LinkedSoId = null; po.LinkedSoLineId = null;
            await db.SaveChangesAsync();
        }
        db.ChangeTracker.Clear();

        var jobs = new List<Job>();
        var jobsMock = new Mock<IBackgroundJobClient>();
        jobsMock.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Callback<Job, IState>((j, _) => jobs.Add(j)).Returns("id");
        var service = new PurchaseOrderService(repo, Mock.Of<IWorkflowActionService>(), Mock.Of<IWorkflowInboxService>(),
            jobsMock.Object, Mock.Of<ITimelineService>());

        var audit = new Mock<IAuditService>();
        var controller = new PurchaseOrdersController(service, Mock.Of<INotificationService>(), audit.Object,
            Mock.Of<IPoDocumentService>(), Mock.Of<IUserQueryService>(), Mock.Of<IEmailSender>(),
            NullLogger<PurchaseOrdersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Editor.ToString())])) }
            }
        };

        return new Harness(db, service, controller, audit, jobs, created.Uuid, variant, created.PoNumber);
    }

    private static PatchPoRequest Edit(Harness h, decimal qty, decimal price) => new()
    {
        Lines = [new CreatePoLineRequest { VariantUuid = h.Variant, ItemDescription = "Cable", Quantity = qty, UnitPrice = price }]
    };

    private static List<TimelineEvent> Events(Harness h) =>
        h.Jobs.Where(j => j.Method.Name == "AppendAsync").Select(j => (TimelineEvent)j.Args[1]).ToList();

    // ── Timeline ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_amendment_to_a_sale_order_po_says_on_the_timeline_what_changed()
    {
        var h = await NewHarness();

        var changes = await h.Service.UpdateAsync(h.PoUuid, Edit(h, 25m, 20m), Editor);

        changes.Should().HaveCount(2);
        var evt = Events(h).Should().ContainSingle().Subject;
        evt.EventType.Should().Be("PO_AMENDED");
        evt.Notes.Should().Be("Quantity — Cable: 40 → 25; Unit price — Cable: 25 → 20");
    }

    [Fact]
    public async Task An_amendment_to_a_hand_made_po_keeps_its_plain_timeline_entry()
    {
        var h = await NewHarness(audited: false);

        await h.Service.UpdateAsync(h.PoUuid, Edit(h, 25m, 20m), Editor);

        Events(h).Should().ContainSingle().Which.Notes.Should().BeNull();
    }

    [Fact]
    public async Task A_split_puts_an_amendment_on_the_original_and_a_creation_on_the_new_po_both_on_the_same_trace()
    {
        var h = await NewHarness();
        var line = (await h.Db.PurchaseOrderLines.AsNoTracking().SingleAsync()).UUID;

        var result = await h.Service.SplitAsync(h.PoUuid, new SplitPoRequest
        {
            LineUuid = line, Quantity = 15m, SupplierId = Guid.NewGuid(), SupplierName = "Vendor B"
        }, Editor);

        h.Jobs.Where(j => j.Method.Name == "AppendAsync").Should().OnlyContain(j => (Guid)j.Args[0] == result.TraceId);
        var events = Events(h);
        events.Select(e => e.EventType).Should().Equal("PO_AMENDED", "PO_CREATED");
        events[0].DocumentId.Should().Be(h.PoUuid);
        events[0].Notes.Should().Contain("Split:");
        events[1].DocumentId.Should().Be(result.NewPoUuid);
        events[1].Notes.Should().Be($"Split from {h.PoNumber}");
    }

    // ── Audit log rows ───────────────────────────────────────────────────────

    [Fact]
    public async Task Patching_a_sale_order_po_writes_one_audit_row_per_changed_field_with_old_and_new_values()
    {
        var h = await NewHarness();

        await h.Controller.UpdatePurchaseOrder(h.PoUuid, Edit(h, 25m, 20m));

        h.Audit.Verify(a => a.LogAsync(Editor, null, "Demand", "UPDATE", "PurchaseOrder", h.PoUuid, It.IsAny<string>(),
            "Quantity — Cable", "40", "25", null), Times.Once);
        h.Audit.Verify(a => a.LogAsync(Editor, null, "Demand", "UPDATE", "PurchaseOrder", h.PoUuid, It.IsAny<string>(),
            "Unit price — Cable", "25", "20", null), Times.Once);
    }

    [Fact]
    public async Task The_generic_update_row_is_still_written_once()
    {
        var h = await NewHarness();

        await h.Controller.UpdatePurchaseOrder(h.PoUuid, Edit(h, 25m, 20m));

        h.Audit.Verify(a => a.LogAsync(Editor, null, "Demand", "UPDATE", "PurchaseOrder", h.PoUuid, It.IsAny<string>(),
            null, null, null, null), Times.Once);
    }

    [Fact]
    public async Task Patching_a_hand_made_po_writes_only_the_generic_row()
    {
        var h = await NewHarness(audited: false);

        await h.Controller.UpdatePurchaseOrder(h.PoUuid, Edit(h, 25m, 20m));

        h.Audit.Invocations.Should().ContainSingle();
        h.Audit.Verify(a => a.LogAsync(Editor, null, "Demand", "UPDATE", "PurchaseOrder", h.PoUuid, It.IsAny<string>(),
            null, null, null, null), Times.Once);
    }

    [Fact]
    public async Task Splitting_audits_the_original_field_by_field_and_the_creation_of_the_new_po()
    {
        var h = await NewHarness();
        var line = (await h.Db.PurchaseOrderLines.AsNoTracking().SingleAsync()).UUID;

        var response = await h.Controller.SplitPurchaseOrder(h.PoUuid, new SplitPoRequest
        {
            LineUuid = line, Quantity = 15m, SupplierId = Guid.NewGuid(), SupplierName = "Vendor B"
        });

        var result = response.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<ApiResponse<SplitPoResult>>().Subject.Result!;
        h.Audit.Verify(a => a.LogAsync(Editor, null, "Demand", "SPLIT", "PurchaseOrder", h.PoUuid, It.IsAny<string>(),
            It.Is<string?>(f => f != null && f.StartsWith("Quantity — ")), "40", "25", null), Times.Once);
        h.Audit.Verify(a => a.LogAsync(Editor, null, "Demand", "SPLIT", "PurchaseOrder", h.PoUuid, It.IsAny<string>(),
            "Split", null, It.Is<string?>(s => s != null && s.StartsWith("15 → " + result.NewPoNumber)), null), Times.Once);
        h.Audit.Verify(a => a.LogAsync(Editor, null, "Demand", "CREATE", "PurchaseOrder", result.NewPoUuid, It.IsAny<string>(),
            null, null, null, $"Split from {h.PoNumber}"), Times.Once);
    }

    // ── A split PO is the sale order's own ───────────────────────────────────

    [Fact]
    public async Task Cancelling_the_sale_order_cancels_a_split_po_too()
    {
        // The line's back-pointer names only the original PO; the split one is reached by its own link.
        var h = await NewHarness();
        var line = (await h.Db.PurchaseOrderLines.AsNoTracking().SingleAsync()).UUID;
        var split = await h.Service.SplitAsync(h.PoUuid, new SplitPoRequest
        {
            LineUuid = line, Quantity = 15m, SupplierId = Guid.NewGuid(), SupplierName = "Vendor B"
        }, Editor);
        var order = await h.Db.SaleOrders.AsNoTracking().Include(o => o.Lines).SingleAsync();
        var poService = new Mock<IPurchaseOrderService>();
        var saleOrders = new SaleOrderService(
            h.Db, new StaticTenantContext(), Mock.Of<IOrganizationCurrencyService>(), Mock.Of<IDocumentNumberGenerator>(),
            Mock.Of<IPricingService>(), Mock.Of<IStockReservationService>(), Mock.Of<ITimelineService>(),
            Mock.Of<IBackgroundJobClient>(), Mock.Of<IAvailabilityCheckService>(), poService.Object, Mock.Of<ISaleOrderEmailService>());

        await saleOrders.CancelAsync(order.UUID, Editor, "Customer withdrew");

        poService.Verify(p => p.CancelAsync(h.PoUuid, Editor, It.IsAny<string?>()), Times.Once);
        poService.Verify(p => p.CancelAsync(split.NewPoUuid, Editor, It.IsAny<string?>()), Times.Once);
    }
}
