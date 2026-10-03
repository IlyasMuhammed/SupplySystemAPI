using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Integration.Controllers.Admin;

/// <summary>The sync dashboard and its actions (plan §5.1). Kinds are SyncKind names, any case.</summary>
[ApiController]
[Route("api/integrations/quickbooks")]
[RequiresFeature("MODULE_INTEGRATION")]
public class SyncController : ControllerBase
{
    private readonly IQuickBooksSyncAdminService _sync;

    public SyncController(IQuickBooksSyncAdminService sync) => _sync = sync;

    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("sync/summary")]
    public async Task<IActionResult> Summary(CancellationToken ct) =>
        Ok(ApiResponse<SyncSummaryModel>.Ok(await _sync.GetSummaryAsync(ct)));

    /// <summary>Paged; search matches the label, the caller's id and the QuickBooks document number.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("sync/items")]
    public async Task<IActionResult> Items([FromQuery] SyncItemQuery query, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<SyncItemModel>>.Ok(await _sync.GetItemsAsync(query, ct)));

    /// <summary>Every attempt for one record, newest first (request/response bodies redacted).</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("sync/items/{id:guid}/log")]
    public async Task<IActionResult> Log(Guid id, CancellationToken ct) =>
        Ok(ApiResponse<List<SyncLogModel>>.Ok(await _sync.GetLogAsync(id, ct)));

    /// <summary>Sends the record again now, with a fresh attempt count.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_SYNC)]
    [HttpPost("sync/items/{id:guid}/retry")]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct) =>
        Ok(ApiResponse<SyncItemModel>.Ok(await _sync.RetryAsync(id, ct)));

    /// <summary>LinkRemote | MarkResolved | Requeue — for records a person has to settle.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("sync/items/{id:guid}/resolve")]
    public async Task<IActionResult> Resolve(Guid id, [FromBody] ResolveSyncItemRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SyncItemModel>.Ok(await _sync.ResolveAsync(id, request, User.GetUserId(), ct)));

    /// <summary>"Push now": marks the records as wanted and asks SCM to send them.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_SYNC)]
    [HttpPost("sync/push")]
    public async Task<IActionResult> Push([FromBody] ManualPushRequest request, CancellationToken ct) =>
        Ok(ApiResponse<ManualPushResult>.Ok(await _sync.PushAsync(request, ct)));

    /// <summary>"Sync all": asks SCM to send every record of the kind.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("sync/backfill/{kind}")]
    public async Task<IActionResult> Backfill(string kind, CancellationToken ct) =>
        Ok(ApiResponse<BackfillResult>.Ok(await _sync.BackfillAsync(SyncKindNames.Parse(kind), ct)));

    /// <summary>Batch status for the sync badges.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpPost("status/lookup")]
    public async Task<IActionResult> StatusLookup([FromBody] StatusLookupRequest request, CancellationToken ct) =>
        Ok(ApiResponse<StatusLookupResult>.Ok(await _sync.LookupStatusAsync(request, ct)));
}
