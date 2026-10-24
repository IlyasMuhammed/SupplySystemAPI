using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>A47 API-CONTRACT §2 (CORE) and §3 reverse (RR) — journal entries. Post-setup GL route (D-4 gate).</summary>
[Route("api/finance/journal-entries")]
[RequiresGlSetup]
[JournalErrorFilter]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public sealed class JournalEntriesController : AccountingControllerBase
{
    private readonly IJournalEntryService _entries;

    public JournalEntriesController(ITenantContext tenant, IJournalEntryService entries) : base(tenant) => _entries = entries;

    /// <summary>Lists journal entries (filters: type, period, status, dates, text; paged, sorted). JE-READ.</summary>
    [HttpGet]
    [RequirePermission(P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE, P.GL_JOURNAL_CREATE, P.GL_JOURNAL_EDIT, P.GL_JOURNAL_DELETE, P.GL_JOURNAL_POST, P.GL_JOURNAL_REVERSE)]
    [ProducesResponseType(typeof(ApiResponse<PaginatedResponse<JournalEntryListItemModel>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] JournalEntryQuery query, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<JournalEntryListItemModel>>.Ok(await _entries.ListAsync(Caller, query, ct)));

    /// <summary>One journal entry with its lines, reversal links and action flags (canEdit / canPost / canDelete / canReverse). JE-READ.</summary>
    [HttpGet("{uuid:guid}")]
    [RequirePermission(P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE, P.GL_JOURNAL_CREATE, P.GL_JOURNAL_EDIT, P.GL_JOURNAL_DELETE, P.GL_JOURNAL_POST, P.GL_JOURNAL_REVERSE)]
    [ProducesResponseType(typeof(ApiResponse<JournalEntryDetailModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<JournalEntryDetailModel>.Ok(await _entries.GetByIdAsync(Caller, uuid, ct)));

    /// <summary>Creates a manual entry: a Draft, or Posted with postImmediately / an auto-post type (then also GL_JOURNAL_POST). 400 JE-001..013.</summary>
    [HttpPost]
    [RequirePermission(P.GL_JOURNAL_CREATE)]
    [ProducesResponseType(typeof(ApiResponse<JournalEntryDetailModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateJournalEntryRequest request, CancellationToken ct)
    {
        var entry = await _entries.CreateAsync(Caller, request ?? new CreateJournalEntryRequest(), ct);
        return Ok(ApiResponse<JournalEntryDetailModel>.Ok(entry, entry.Status == Domain.JournalStatuses.Posted ? "Journal entry posted." : "Journal entry saved as draft."));
    }

    /// <summary>Replaces a manual Draft (header and lines); the number, type and key never change. 409 JE-014 when not a manual draft.</summary>
    [HttpPut("{uuid:guid}")]
    [RequirePermission(P.GL_JOURNAL_EDIT)]
    [ProducesResponseType(typeof(ApiResponse<JournalEntryDetailModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateJournalEntryRequest request, CancellationToken ct) =>
        Ok(ApiResponse<JournalEntryDetailModel>.Ok(await _entries.UpdateDraftAsync(Caller, uuid, request ?? new UpdateJournalEntryRequest(), ct), "Journal entry updated."));

    /// <summary>Deletes a manual Draft. 409 JE-014 for posted entries and source-document drafts.</summary>
    [HttpDelete("{uuid:guid}")]
    [RequirePermission(P.GL_JOURNAL_DELETE)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _entries.DeleteDraftAsync(Caller, uuid, ct);
        return Ok(ApiResponse<object>.Ok(new { deleted = true }, "Journal entry deleted."));
    }

    /// <summary>Posts a Draft (period and accounts checked again, balances moved). 400 period/account, 409 JE-014.</summary>
    [HttpPost("{uuid:guid}/post")]
    [RequirePermission(P.GL_JOURNAL_POST)]
    [ProducesResponseType(typeof(ApiResponse<JournalEntryDetailModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Post(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<JournalEntryDetailModel>.Ok(await _entries.PostAsync(Caller, uuid, ct), "Journal entry posted."));

    /// <summary>RR (§3) — the reversal entry, the original's ref, the auto-reconciliation groups and a source-document warning.</summary>
    [HttpPost("{uuid:guid}/reverse")]
    [RequirePermission(P.GL_JOURNAL_REVERSE)]
    [ProducesResponseType(typeof(ApiResponse<JournalReversalResultModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Reverse(Guid uuid, [FromBody] ReverseJournalEntryRequest request, CancellationToken ct) =>
        Ok(ApiResponse<JournalReversalResultModel>.Ok(await _entries.ReverseAsync(Caller, uuid, request ?? new ReverseJournalEntryRequest(), ct), "Journal entry reversed."));
}
