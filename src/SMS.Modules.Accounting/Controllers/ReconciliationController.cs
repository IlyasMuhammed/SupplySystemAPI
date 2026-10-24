using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>
/// A47 C8 — API-CONTRACT §4 (RR): open items, suggestions, reconcile, one group, unreconcile. Reads GL_RECONCILE_VIEW or
/// GL_RECONCILE_EXECUTE; writes GL_RECONCILE_EXECUTE (D-15). Post-setup GL route; coded errors (RC-0xx) through
/// <see cref="JournalErrorFilterAttribute"/>.
/// </summary>
[Route("api/finance/reconciliation")]
[RequiresGlSetup]
[JournalErrorFilter]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public sealed class ReconciliationController : AccountingControllerBase
{
    private readonly IReconciliationService _reconciliation;

    public ReconciliationController(ITenantContext tenant, IReconciliationService reconciliation) : base(tenant) =>
        _reconciliation = reconciliation;

    /// <summary>SR-FE-1 — the reconciliation groups (Full and Partial), newest first, paged.</summary>
    [HttpGet("groups")]
    [RequirePermission(P.GL_RECONCILE_VIEW, P.GL_RECONCILE_EXECUTE)]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<ReconcileGroupListItemModel>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Groups([FromQuery] ReconcileGroupQuery query, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<ReconcileGroupListItemModel>>.Ok(await _reconciliation.ListGroupsAsync(Caller, query ?? new ReconcileGroupQuery(), ct)));

    /// <summary>The open (not fully reconciled) posted lines of an account (accountUuid required), oldest first.</summary>
    [HttpGet("open-items")]
    [RequirePermission(P.GL_RECONCILE_VIEW, P.GL_RECONCILE_EXECUTE)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<OpenItemModel>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> OpenItems([FromQuery] OpenItemsQuery query, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<OpenItemModel>>.Ok(await _reconciliation.GetOpenItemsAsync(Caller, query ?? new OpenItemsQuery(), ct)));

    /// <summary>Opposite-side open lines of the account that offset the selected line(s), exact matches first (TS-RC-007).</summary>
    [HttpPost("suggest")]
    [RequirePermission(P.GL_RECONCILE_VIEW, P.GL_RECONCILE_EXECUTE)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ReconcileSuggestionModel>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Suggest([FromBody] ReconcileSuggestRequest request, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<ReconcileSuggestionModel>>.Ok(await _reconciliation.SuggestMatchesAsync(Caller, request ?? new ReconcileSuggestRequest(), ct)));

    /// <summary>Reconciles ≥ 2 posted lines of one account: Full when they net to zero, else Partial.</summary>
    [HttpPost("reconcile")]
    [RequirePermission(P.GL_RECONCILE_EXECUTE)]
    [ProducesResponseType(typeof(ApiResponse<ReconcileGroupModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reconcile([FromBody] ReconcileRequest request, CancellationToken ct)
    {
        var group = await _reconciliation.ReconcileAsync(Caller, request ?? new ReconcileRequest(), ct);
        return Ok(ApiResponse<ReconcileGroupModel>.Ok(group, group.ReconcileType == Domain.ReconcileTypes.Full
            ? $"Lines reconciled as {group.GroupNumber}."
            : $"Lines partially reconciled as {group.GroupNumber}."));
    }

    /// <summary>One reconciliation group with its lines.</summary>
    [HttpGet("{groupUuid:guid}")]
    [RequirePermission(P.GL_RECONCILE_VIEW, P.GL_RECONCILE_EXECUTE)]
    [ProducesResponseType(typeof(ApiResponse<ReconcileGroupModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid groupUuid, CancellationToken ct) =>
        Ok(ApiResponse<ReconcileGroupModel>.Ok(await _reconciliation.GetGroupAsync(Caller, groupUuid, ct)));

    /// <summary>Undoes a reconciliation: residuals restored, links cleared, the group deleted.</summary>
    [HttpPost("{groupUuid:guid}/unreconcile")]
    [RequirePermission(P.GL_RECONCILE_EXECUTE)]
    [ProducesResponseType(typeof(ApiResponse<UnreconcileResultModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Unreconcile(Guid groupUuid, CancellationToken ct)
    {
        var result = await _reconciliation.UnreconcileAsync(Caller, groupUuid, ct);
        return Ok(ApiResponse<UnreconcileResultModel>.Ok(result, $"Reconciliation {result.GroupNumber} undone."));
    }
}
