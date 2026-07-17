using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.WorkflowEngine.Controllers;

[ApiController]
[Route("api/timeline")]
[Authorize]
public class TimelineController : ControllerBase
{
    private readonly ITimelineService _timelineSvc;

    public TimelineController(ITimelineService timelineSvc)
        => _timelineSvc = timelineSvc;

    // ── GET /api/timeline/by-document?interface={code}&documentId={id} ────────
    // Routed before {traceId} so "by-document" is never captured as a traceId segment.
    [HttpGet("by-document")]
    public async Task<IActionResult> GetByDocument(
        [FromQuery(Name = "interface")] string interfaceCode,
        [FromQuery] Guid documentId)
    {
        if (string.IsNullOrWhiteSpace(interfaceCode) || documentId == Guid.Empty)
            return BadRequest(ApiResponse.Fail("Both interface and documentId are required."));

        var traceId = await _timelineSvc.ResolveTraceIdAsync(interfaceCode, documentId);
        if (traceId is null)
            return NotFound(ApiResponse.Fail("No trace_id could be resolved for the given document."));

        var detail = await _timelineSvc.GetTimelineDetailAsync(traceId.Value);
        if (detail is null)
            return NotFound(ApiResponse.Fail("No timeline exists for the resolved trace_id."));

        return Ok(ApiResponse<TimelineDetail>.Ok(detail));
    }

    // ── GET /api/timeline/{traceId} ─────────────────────────────────────────────
    [HttpGet("{traceId:guid}")]
    public async Task<IActionResult> GetByTraceId(Guid traceId)
    {
        var detail = await _timelineSvc.GetTimelineDetailAsync(traceId);
        if (detail is null)
            return NotFound(ApiResponse.Fail("No timeline exists for the given trace_id."));

        return Ok(ApiResponse<TimelineDetail>.Ok(detail));
    }
}
