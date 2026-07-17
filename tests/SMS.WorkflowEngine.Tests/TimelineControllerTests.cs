using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Moq;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Controllers;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.WorkflowEngine.Tests;

file static class ControllerBuild
{
    internal static TimelineDetail Detail(Guid traceId) => new()
    {
        TraceId       = traceId,
        ChainRootType = "PR",
        ChainRootRef  = "PR-2026-00001",
        FirstEventAt  = DateTime.UtcNow.AddMinutes(-10),
        LastEventAt   = DateTime.UtcNow,
        Events =
        [
            new TimelineEvent("PR_CREATED", "PR", Guid.NewGuid(), "PR-2026-00001", DateTime.UtcNow.AddMinutes(-10), 1, null),
            new TimelineEvent("PR_APPROVED", "PR", Guid.NewGuid(), "PR-2026-00001", DateTime.UtcNow, 2, null)
        ]
    };
}

// ── GET /api/timeline/{traceId} ─────────────────────────────────────────────

public class TimelineController_GetByTraceId_Tests
{
    [Fact]
    public async Task Returns_Ok_With_Full_Timeline_When_Found()
    {
        var traceId = Guid.NewGuid();
        var detail  = ControllerBuild.Detail(traceId);

        var svc = new Mock<ITimelineService>();
        svc.Setup(s => s.GetTimelineDetailAsync(traceId)).ReturnsAsync(detail);

        var controller = new TimelineController(svc.Object);

        var result = await controller.GetByTraceId(traceId);

        var ok       = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<ApiResponse<TimelineDetail>>().Subject;
        response.Success.Should().BeTrue();
        response.Result!.TraceId.Should().Be(traceId);
        response.Result.Events.Should().HaveCount(2);
        response.Result.TotalEventCount.Should().Be(2);
    }

    [Fact]
    public async Task Returns_NotFound_When_TraceId_Unknown()
    {
        var svc = new Mock<ITimelineService>();
        svc.Setup(s => s.GetTimelineDetailAsync(It.IsAny<Guid>())).ReturnsAsync((TimelineDetail?)null);

        var controller = new TimelineController(svc.Object);

        var result = await controller.GetByTraceId(Guid.NewGuid());

        result.Should().BeOfType<NotFoundObjectResult>();
    }
}

// ── GET /api/timeline/by-document?interface={code}&documentId={id} ─────────

public class TimelineController_GetByDocument_Tests
{
    [Fact]
    public async Task Resolves_TraceId_Then_Returns_Full_Timeline()
    {
        var documentId = Guid.NewGuid();
        var traceId    = Guid.NewGuid();
        var detail     = ControllerBuild.Detail(traceId);

        var svc = new Mock<ITimelineService>();
        svc.Setup(s => s.ResolveTraceIdAsync("PO", documentId)).ReturnsAsync(traceId);
        svc.Setup(s => s.GetTimelineDetailAsync(traceId)).ReturnsAsync(detail);

        var controller = new TimelineController(svc.Object);

        var result = await controller.GetByDocument("PO", documentId);

        var ok       = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<ApiResponse<TimelineDetail>>().Subject;
        response.Result!.TraceId.Should().Be(traceId);
    }

    [Fact]
    public async Task Returns_NotFound_When_Document_Cannot_Be_Resolved()
    {
        var svc = new Mock<ITimelineService>();
        svc.Setup(s => s.ResolveTraceIdAsync(It.IsAny<string>(), It.IsAny<Guid>())).ReturnsAsync((Guid?)null);

        var controller = new TimelineController(svc.Object);

        var result = await controller.GetByDocument("PO", Guid.NewGuid());

        result.Should().BeOfType<NotFoundObjectResult>();
        svc.Verify(s => s.GetTimelineDetailAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Returns_BadRequest_When_Interface_Missing()
    {
        var svc = new Mock<ITimelineService>();
        var controller = new TimelineController(svc.Object);

        var result = await controller.GetByDocument("", Guid.NewGuid());

        result.Should().BeOfType<BadRequestObjectResult>();
    }
}
