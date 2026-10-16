using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Integration.Core.Matching;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Integration.Controllers.Admin;

/// <summary>
/// A44 D-17 — "Import from QuickBooks" on the Match tab: QuickBooks' customers / vendors / items as remote-only link
/// candidates. Nothing is created in SCM or in QuickBooks from here.
/// </summary>
[ApiController]
[Route("api/integrations/quickbooks/matching")]
[RequiresFeature("MODULE_INTEGRATION")]
public class RemoteEntityController : ControllerBase
{
    private readonly IQuickBooksRemoteEntityService _remote;

    public RemoteEntityController(IQuickBooksRemoteEntityService remote) => _remote = remote;

    /// <summary>Reads every record of the kind from QuickBooks into the remote-only list.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("{kind}/import")]
    public async Task<IActionResult> Import(string kind, CancellationToken ct) =>
        Ok(ApiResponse<RemoteImportResultModel>.Ok(await _remote.ImportAsync(SyncKindNames.Parse(kind), ct)));

    /// <summary>The imported records; <c>linked=false</c> = only those no SCM record is linked to.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_VIEW)]
    [HttpGet("{kind}/remote")]
    public async Task<IActionResult> Remote(string kind, [FromQuery] RemoteEntityQuery query, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<RemoteEntityModel>>.Ok(await _remote.ListAsync(SyncKindNames.Parse(kind), query, ct)));

    /// <summary>Links an SCM record (externalId) to the imported QuickBooks record, as Link in matching does.</summary>
    [RequirePermission(PermissionCodes.INTEGRATION_MANAGE)]
    [HttpPost("{kind}/remote/{remoteId}/link")]
    public async Task<IActionResult> Link(string kind, string remoteId, [FromBody] LinkRemoteEntityRequest request, CancellationToken ct) =>
        Ok(ApiResponse<RemoteEntityModel>.Ok(
            await _remote.LinkAsync(SyncKindNames.Parse(kind), remoteId, request, User.GetUserId(), ct), "Linked."));
}
