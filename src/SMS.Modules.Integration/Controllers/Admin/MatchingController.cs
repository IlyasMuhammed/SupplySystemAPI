using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Integration.Core.Matching;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Integration.QuickBooks;
using SMS.Shared.Pagination;

namespace SMS.Modules.Integration.Controllers.Admin;

/// <summary>
/// Matching the records the gateway holds against what the accountant already has in QuickBooks
/// (plan §5.1, QBI-22). Nothing is created in QuickBooks from here. <c>/matching/complete</c> lives
/// with the setup endpoints, not here.
/// </summary>
[ApiController]
[Route("api/integrations/quickbooks/matching")]
[RequiresFeature("MODULE_INTEGRATION")]
public class MatchingController : ControllerBase
{
    private readonly IQuickBooksMatchingService _matching;

    public MatchingController(IQuickBooksMatchingService matching) => _matching = matching;

    /// <summary>Reads the kind's records from QuickBooks and proposes a match for each unlinked record. Replaces open proposals.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("{kind}/scan")]
    public async Task<IActionResult> Scan(string kind, CancellationToken ct)
    {
        var result = await _matching.ScanAsync(SyncKindNames.Parse(kind), ct);
        return Ok(ApiResponse<MatchScanResultModel>.Ok(result));
    }

    /// <summary>The kind's proposals, optionally only those with one decision (Pending | Link | CreateNew | Skip).</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("{kind}")]
    public async Task<IActionResult> List(string kind, [FromQuery] string? decision, CancellationToken ct)
    {
        var result = await _matching.ListAsync(SyncKindNames.Parse(kind), decision, ct);
        return Ok(ApiResponse<List<MatchCandidateModel>>.Ok(result));
    }

    /// <summary>Link / Create new / Skip, for one or many proposals.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("{kind}/confirm")]
    public async Task<IActionResult> Confirm(string kind, [FromBody] ConfirmMatchesRequest request, CancellationToken ct)
    {
        var result = await _matching.ConfirmAsync(SyncKindNames.Parse(kind), request, User.GetUserId(), ct);
        return Ok(ApiResponse<List<MatchCandidateModel>>.Ok(result, "Decisions saved."));
    }
}
