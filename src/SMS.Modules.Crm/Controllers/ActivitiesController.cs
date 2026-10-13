using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Crm.Models;
using SMS.Modules.Crm.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Files;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Crm.Controllers;

/// <summary>A41 API-CONTRACT §5 — activities and their documents (§14.3).</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Crm)]
public sealed class ActivitiesController : CrmControllerBase
{
    // The multipart envelope counts against the request limit (see AttachmentsController).
    private const long MultipartAllowance = 64 * 1024;

    private readonly IActivityService _activities;

    public ActivitiesController(ITenantContext tenant, IActivityService activities) : base(tenant) => _activities = activities;

    [HttpGet("api/crm/activities")]
    [RequirePermission(P.CRM_VIEW_ACTIVITIES)]
    public async Task<IActionResult> List([FromQuery] ActivityListFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<ActivityModel>>.Ok(await _activities.ListAsync(Caller, filter, ct)));

    [HttpGet("api/crm/activities/pending")]
    [RequirePermission(P.CRM_VIEW_ACTIVITIES)]
    public async Task<IActionResult> Pending([FromQuery] PendingActivityFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PendingActivityItem>>.Ok(await _activities.GetPendingFollowUpsAsync(Caller, filter, ct)));

    [HttpGet("api/crm/activities/{uuid:guid}")]
    [RequirePermission(P.CRM_VIEW_ACTIVITIES)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<ActivityModel>.Ok(await _activities.GetByIdAsync(Caller, uuid, ct)));

    [HttpPost("api/crm/activities")]
    [RequirePermission(P.CRM_CREATE_ACTIVITY)]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Create([FromBody] ActivityUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<ActivityModel>.Ok(await _activities.CreateAsync(Caller, req, ct), "Activity logged."));

    [HttpPut("api/crm/activities/{uuid:guid}")]
    [RequirePermission(P.CRM_EDIT_ACTIVITY)]
    [RequestSizeLimit(2 * 1024 * 1024)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] ActivityUpsert req, CancellationToken ct) =>
        Ok(ApiResponse<ActivityModel>.Ok(await _activities.UpdateAsync(Caller, uuid, req, ct), "Activity saved."));

    [HttpPatch("api/crm/activities/{uuid:guid}/complete")]
    [RequirePermission(P.CRM_EDIT_ACTIVITY)]
    public async Task<IActionResult> Complete(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<ActivityModel>.Ok(await _activities.CompleteAsync(Caller, uuid, ct), "Activity completed."));

    [HttpDelete("api/crm/activities/{uuid:guid}")]
    [RequirePermission(P.CRM_DELETE_ACTIVITY)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _activities.DeleteAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("Activity deleted."));
    }

    /// <summary>Register 1H — multipart: file + documentType + direction + notes (+ documentName). Kept in the database (D-14).</summary>
    [HttpPost("api/crm/activities/{uuid:guid}/documents")]
    [RequirePermission(P.CRM_CREATE_ACTIVITY)]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(UploadRules.MaxFileBytes + MultipartAllowance)]
    public async Task<IActionResult> Attach(Guid uuid, IFormFile? file, [FromForm] AttachDocumentRequest req, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(ApiResponse.Fail("No file was provided."));
        if (UploadRules.Refusal(file.FileName, file.Length, file.ContentType) is { } refusal) return BadRequest(ApiResponse.Fail(refusal));
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var doc = await _activities.AttachAsync(Caller, uuid, new CrmUpload(file.FileName, file.ContentType, ms.ToArray()), req, ct);
        return Ok(ApiResponse<ActivityDocumentModel>.Ok(doc, "Document attached."));
    }

    [HttpGet("api/crm/documents/{uuid:guid}/content")]
    [RequirePermission(P.CRM_VIEW_ACTIVITIES)]
    public async Task<IActionResult> Content(Guid uuid, CancellationToken ct)
    {
        var file = await _activities.GetDocumentContentAsync(Caller, uuid, ct);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return File(file.Content, file.ContentType, file.FileName);
    }

    [HttpDelete("api/crm/documents/{uuid:guid}")]
    [RequirePermission(P.CRM_EDIT_ACTIVITY)]
    public async Task<IActionResult> DeleteDocument(Guid uuid, CancellationToken ct)
    {
        await _activities.DeleteDocumentAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("Document removed."));
    }
}
