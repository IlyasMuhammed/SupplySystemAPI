using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Controllers;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Repositories;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Hangfire;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P5-08 §13.5 — the two document-scoped timeline endpoints Demand owns
/// (<c>GET /api/sale-orders/{id}/timeline</c>, <c>GET /api/purchase-orders/{id}/timeline</c>).
/// <c>GET /api/timeline/{traceId}</c> lives in WorkflowEngine and has its own tests there.</summary>
public class TimelineEndpointTests
{
    private static TimelineDetail Detail(Guid traceId) => new()
    {
        TraceId = traceId,
        Events  = [new TimelineEventView("SO_CREATED", "SO", Guid.NewGuid(), "SO-2026-00042", DateTime.UtcNow, 1, "Alice", null)]
    };

    private static PurchaseOrdersController PoController(Mock<IPurchaseOrderService> service) =>
        new(service.Object, Mock.Of<INotificationService>(), Mock.Of<IAuditService>(), Mock.Of<IPoDocumentService>(),
            Mock.Of<IUserQueryService>(), Mock.Of<IEmailSender>(), NullLogger<PurchaseOrdersController>.Instance);

    // ── Purchase-order endpoint ──────────────────────────────────────────────

    [Fact]
    public async Task Purchase_order_timeline_returns_ok_with_the_full_timeline()
    {
        var uuid = Guid.NewGuid();
        var detail = Detail(Guid.NewGuid());
        var service = new Mock<IPurchaseOrderService>();
        service.Setup(s => s.GetTimelineAsync(uuid)).ReturnsAsync(detail);

        var result = await PoController(service).GetTimeline(uuid);

        var response = result.Should().BeOfType<OkObjectResult>().Subject.Value.Should().BeOfType<ApiResponse<TimelineDetail>>().Subject;
        response.Success.Should().BeTrue();
        response.Result.Should().BeSameAs(detail);
    }

    [Fact]
    public async Task Purchase_order_timeline_is_not_found_for_a_po_that_does_not_resolve()
    {
        var service = new Mock<IPurchaseOrderService>();
        service.Setup(s => s.GetTimelineAsync(It.IsAny<Guid>())).ReturnsAsync((TimelineDetail?)null);

        (await PoController(service).GetTimeline(Guid.NewGuid())).Should().BeOfType<NotFoundObjectResult>();
    }

    [Fact]
    public async Task The_purchase_order_service_reads_the_timeline_through_the_po_trace_id_resolver()
    {
        var uuid = Guid.NewGuid();
        var traceId = Guid.NewGuid();
        var detail = Detail(traceId);
        var timeline = new Mock<ITimelineService>();
        timeline.Setup(t => t.ResolveTraceIdAsync("PO", uuid)).ReturnsAsync(traceId);
        timeline.Setup(t => t.GetTimelineDetailAsync(traceId)).ReturnsAsync(detail);
        var service = NewPoService(timeline);

        (await service.GetTimelineAsync(uuid)).Should().BeSameAs(detail);
    }

    [Fact]
    public async Task The_purchase_order_service_returns_null_without_asking_for_a_timeline_when_the_po_does_not_resolve()
    {
        var timeline = new Mock<ITimelineService>();
        timeline.Setup(t => t.ResolveTraceIdAsync("PO", It.IsAny<Guid>())).ReturnsAsync((Guid?)null);
        var service = NewPoService(timeline);

        (await service.GetTimelineAsync(Guid.NewGuid())).Should().BeNull();
        timeline.Verify(t => t.GetTimelineDetailAsync(It.IsAny<Guid>()), Times.Never);
    }

    // IPurchaseOrderRepository is internal, so Moq can't proxy it; GetTimelineAsync never touches it,
    // and the real one over an empty in-memory context is the honest stand-in.
    private static PurchaseOrderService NewPoService(Mock<ITimelineService> timeline)
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext());
        return new(new PurchaseOrderRepository(db, NullLogger<PurchaseOrderRepository>.Instance),
            Mock.Of<IWorkflowActionService>(), Mock.Of<IWorkflowInboxService>(), Mock.Of<IBackgroundJobClient>(), timeline.Object);
    }

    // ── Sale-order endpoint ──────────────────────────────────────────────────

    [Fact]
    public async Task Sale_order_timeline_returns_ok_when_found_and_not_found_otherwise()
    {
        var found = Guid.NewGuid();
        var detail = Detail(Guid.NewGuid());
        var service = new Mock<ISaleOrderService>();
        service.Setup(s => s.GetTimelineAsync(found)).ReturnsAsync(detail);
        service.Setup(s => s.GetTimelineAsync(It.Is<Guid>(g => g != found))).ReturnsAsync((TimelineDetail?)null);
        var controller = new SaleOrdersController(service.Object);

        (await controller.GetTimeline(found)).Should().BeOfType<OkObjectResult>()
            .Which.Value.Should().BeOfType<ApiResponse<TimelineDetail>>().Which.Result.Should().BeSameAs(detail);
        (await controller.GetTimeline(Guid.NewGuid())).Should().BeOfType<NotFoundObjectResult>();
    }

    // ── The §13.5 contract itself ────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(SaleOrdersController), "api/sale-orders")]
    [InlineData(typeof(PurchaseOrdersController), "api/purchase-orders")]
    public void Each_document_exposes_GET_id_timeline_under_its_own_route(Type controller, string baseRoute)
    {
        var route = controller.GetCustomAttributes(typeof(RouteAttribute), true).Cast<RouteAttribute>().Single();
        route.Template.Should().Be(baseRoute);

        var action = controller.GetMethod("GetTimeline")!;
        var get = action.GetCustomAttributes(typeof(HttpGetAttribute), false).Cast<HttpGetAttribute>().Single();
        get.Template.Should().Be("{uuid:guid}/timeline");
    }

    [Fact]
    public void The_sale_order_timeline_requires_the_same_permission_as_viewing_the_order()
    {
        var permission = typeof(SaleOrdersController).GetMethod("GetTimeline")!
            .GetCustomAttributes(typeof(RequirePermissionAttribute), false).Cast<RequirePermissionAttribute>().Single();

        permission.Should().BeEquivalentTo(new RequirePermissionAttribute(PermissionCodes.SALE_ORDER_VIEW));
    }
}
