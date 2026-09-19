using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Settlement;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Tying each line of a carrier's bill to the movement it charges for.
/// </summary>
[ApiController]
[Route("api/logistics/invoice-matching")]
[RequiresFeature("MODULE_LOGISTICS")]
public class InvoiceMatchingController : ControllerBase
{
    private readonly IInvoiceMatchingService _matching;
    private readonly IThreeWayMatchService   _threeWay;

    public InvoiceMatchingController(IInvoiceMatchingService matching, IThreeWayMatchService threeWay)
    {
        _matching = matching;
        _threeWay = threeWay;
    }

    /// <summary>
    /// Compares what the carrier billed against what it was expected to bill, records the result,
    /// and moves the bill to MATCHED or DISPUTED.
    /// </summary>
    /// <remarks>
    /// Expected is what the carrier agreed at booking where it said anything, and the quote
    /// otherwise — the same precedence pricing used, for the same reason.
    /// </remarks>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("invoices/{invoiceUuid:guid}/three-way")]
    public async Task<IActionResult> ThreeWayMatch(Guid invoiceUuid, CancellationToken ct)
    {
        var result = await _threeWay.MatchAsync(invoiceUuid, User.GetUserId(), ct);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ThreeWayMatchModel>.Ok(result));
    }

    /// <summary>The same comparison, recording nothing. What a screen shows before anybody commits.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_VIEW)]
    [HttpGet("invoices/{invoiceUuid:guid}/three-way")]
    public async Task<IActionResult> PreviewThreeWayMatch(Guid invoiceUuid, CancellationToken ct)
    {
        var result = await _threeWay.PreviewAsync(invoiceUuid, ct);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ThreeWayMatchModel>.Ok(result));
    }

    /// <summary>
    /// Matches every line on a bill that is not already settled. Idempotent — re-running it never
    /// undoes a decision somebody made by hand.
    /// </summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("invoices/{invoiceUuid:guid}")]
    public async Task<IActionResult> Match(Guid invoiceUuid, CancellationToken ct)
    {
        var result = await _matching.MatchInvoiceAsync(invoiceUuid, User.GetUserId(), ct);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<InvoiceMatchResultModel>.Ok(result));
    }

    /// <summary>What a bill's lines are tied to now. Changes nothing.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_VIEW)]
    [HttpGet("invoices/{invoiceUuid:guid}")]
    public async Task<IActionResult> GetMatches(Guid invoiceUuid, CancellationToken ct)
    {
        var result = await _matching.GetMatchesAsync(invoiceUuid, ct);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<InvoiceMatchResultModel>.Ok(result));
    }

    /// <summary>
    /// Everything still needing a person, largest charge first — the lines nothing matched are
    /// where a wrong charge goes unnoticed.
    /// </summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_VIEW)]
    [HttpGet("queue")]
    public async Task<IActionResult> GetQueue([FromQuery] UnmatchedLineFilter filter, CancellationToken ct)
    {
        var queue = await _matching.GetQueueAsync(filter, ct);
        return Ok(ApiResponse<PaginatedResponse<InvoiceLineMatchModel>>.Ok(queue));
    }

    /// <summary>Consignments this line could plausibly belong to, and why each is offered.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_VIEW)]
    [HttpGet("lines/{lineUuid:guid}/candidates")]
    public async Task<IActionResult> GetCandidates(Guid lineUuid, CancellationToken ct)
    {
        var candidates = await _matching.GetCandidatesAsync(lineUuid, ct);
        return Ok(ApiResponse<IReadOnlyList<MatchCandidateModel>>.Ok(candidates));
    }

    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("lines/{lineUuid:guid}/match")]
    public async Task<IActionResult> MatchLine(
        Guid lineUuid, [FromBody] MatchLineRequest req, CancellationToken ct)
    {
        var matched = await _matching.MatchLineAsync(lineUuid, req, User.GetUserId(), ct);
        return matched
            ? Ok(ApiResponse.Ok("Line matched."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>Marks a line as not a movement charge at all, so it leaves the queue.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("lines/{lineUuid:guid}/exclude")]
    public async Task<IActionResult> ExcludeLine(
        Guid lineUuid, [FromBody] ExcludeLineRequest req, CancellationToken ct)
    {
        var excluded = await _matching.ExcludeLineAsync(lineUuid, req, User.GetUserId(), ct);
        return excluded
            ? Ok(ApiResponse.Ok("Line set aside."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("lines/{lineUuid:guid}/unmatch")]
    public async Task<IActionResult> UnmatchLine(
        Guid lineUuid, [FromBody] ExcludeLineRequest req, CancellationToken ct)
    {
        var unmatched = await _matching.UnmatchLineAsync(lineUuid, req, User.GetUserId(), ct);
        return unmatched
            ? Ok(ApiResponse.Ok("Line returned to the queue."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}
