using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>A47 API-CONTRACT §1 (CORE) — journal types. Reads JE-READ, writes GL_SETUP_MANAGE. Post-setup GL route (D-4 gate).</summary>
[Route("api/finance/journal-types")]
[RequiresGlSetup]
[JournalErrorFilter]
[ProducesResponseType(StatusCodes.Status400BadRequest)]
[ProducesResponseType(StatusCodes.Status403Forbidden)]
[ProducesResponseType(StatusCodes.Status404NotFound)]
[ProducesResponseType(StatusCodes.Status409Conflict)]
public sealed class JournalTypesController : AccountingControllerBase
{
    private readonly IJournalTypeService _types;

    public JournalTypesController(ITenantContext tenant, IJournalTypeService types) : base(tenant) => _types = types;

    /// <summary>The organization's journal types (activeOnly to hide deactivated ones). JE-READ.</summary>
    [HttpGet]
    [RequirePermission(P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE, P.GL_JOURNAL_CREATE, P.GL_JOURNAL_EDIT, P.GL_JOURNAL_DELETE, P.GL_JOURNAL_POST, P.GL_JOURNAL_REVERSE)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<JournalTypeModel>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] bool activeOnly = false, CancellationToken ct = default) =>
        Ok(ApiResponse<IReadOnlyList<JournalTypeModel>>.Ok(await _types.ListAsync(Caller, activeOnly, ct)));

    /// <summary>One journal type. JE-READ.</summary>
    [HttpGet("{uuid:guid}")]
    [RequirePermission(P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE, P.GL_JOURNAL_CREATE, P.GL_JOURNAL_EDIT, P.GL_JOURNAL_DELETE, P.GL_JOURNAL_POST, P.GL_JOURNAL_REVERSE)]
    [ProducesResponseType(typeof(ApiResponse<JournalTypeModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<JournalTypeModel>.Ok(await _types.GetAsync(Caller, uuid, ct)));

    /// <summary>Creates a custom journal type (code and sequence prefix unique per organization). GL_SETUP_MANAGE.</summary>
    [HttpPost]
    [RequirePermission(P.GL_SETUP_MANAGE)]
    [ProducesResponseType(typeof(ApiResponse<JournalTypeModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Create([FromBody] CreateJournalTypeRequest request, CancellationToken ct) =>
        Ok(ApiResponse<JournalTypeModel>.Ok(await _types.CreateAsync(Caller, request ?? new CreateJournalTypeRequest(), ct), "Journal type created."));

    /// <summary>Updates a journal type (system types keep code and kind; a prefix is fixed once numbered). GL_SETUP_MANAGE.</summary>
    [HttpPut("{uuid:guid}")]
    [RequirePermission(P.GL_SETUP_MANAGE)]
    [ProducesResponseType(typeof(ApiResponse<JournalTypeModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateJournalTypeRequest request, CancellationToken ct) =>
        Ok(ApiResponse<JournalTypeModel>.Ok(await _types.UpdateAsync(Caller, uuid, request ?? new UpdateJournalTypeRequest(), ct), "Journal type updated."));

    /// <summary>Deactivates a journal type (system journals then fall back to MISC, D-12). GL_SETUP_MANAGE.</summary>
    [HttpPatch("{uuid:guid}/deactivate")]
    [RequirePermission(P.GL_SETUP_MANAGE)]
    [ProducesResponseType(typeof(ApiResponse<JournalTypeModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Deactivate(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<JournalTypeModel>.Ok(await _types.SetActiveAsync(Caller, uuid, false, ct), "Journal type deactivated."));

    /// <summary>Activates a journal type again. GL_SETUP_MANAGE.</summary>
    [HttpPatch("{uuid:guid}/activate")]
    [RequirePermission(P.GL_SETUP_MANAGE)]
    [ProducesResponseType(typeof(ApiResponse<JournalTypeModel>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Activate(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<JournalTypeModel>.Ok(await _types.SetActiveAsync(Caller, uuid, true, ct), "Journal type activated."));

    /// <summary>Deletes an unused custom journal type. GL_SETUP_MANAGE.</summary>
    [HttpDelete("{uuid:guid}")]
    [RequirePermission(P.GL_SETUP_MANAGE)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _types.DeleteAsync(Caller, uuid, ct);
        return Ok(ApiResponse<object>.Ok(new { deleted = true }, "Journal type deleted."));
    }
}
