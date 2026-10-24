using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Import.Models;
using SMS.Modules.Import.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Import.Controllers;

/// <summary>A40 API-CONTRACT "Profiles" — import profiles, templates and the migration workbook.</summary>
[ApiController]
[Route("api/import")]
[RequiresFeature(ModuleCodes.DataImport)]
public class ImportProfilesController : ControllerBase
{
    private readonly IImportProfileService _profiles;
    private readonly IImportWorkbookService _workbooks;

    public ImportProfilesController(IImportProfileService profiles, IImportWorkbookService workbooks)
    {
        _profiles = profiles;
        _workbooks = workbooks;
    }

    [HttpGet("profiles")]
    [RequirePermission(P.IMPORT_PROFILE_VIEW)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<ImportProfileModel>>.Ok(await _profiles.ListAsync(ct)));

    [HttpGet("profiles/{entityType}")]
    [RequirePermission(P.IMPORT_PROFILE_VIEW)]
    public async Task<IActionResult> Get(string entityType, CancellationToken ct) =>
        Ok(ApiResponse<ImportProfileDetailModel>.Ok(await _profiles.GetAsync(entityType, ct)));

    [HttpPut("profiles/{entityType}/settings")]
    [RequirePermission(P.IMPORT_PROFILE_MANAGE)]
    public async Task<IActionResult> UpdateSettings(string entityType, [FromBody] ImportProfileSettingsRequest req, CancellationToken ct) =>
        Ok(ApiResponse<ImportProfileDetailModel>.Ok(
            await _profiles.UpdateSettingsAsync(entityType, req ?? new(), ImportCaller.From(User), ct), "Import settings saved."));

    [HttpGet("profiles/{entityType}/template")]
    [RequirePermission(P.IMPORT_UPLOAD)]
    public async Task<IActionResult> Template(string entityType, CancellationToken ct)
    {
        var file = await _workbooks.TemplateAsync(entityType, ct);
        return File(file.Content, ImportFile.XlsxContentType, file.FileName);
    }

    [HttpGet("migration-workbook")]
    [RequirePermission(P.IMPORT_UPLOAD)]
    public async Task<IActionResult> MigrationWorkbook(CancellationToken ct)
    {
        var file = await _workbooks.MigrationWorkbookAsync(ct);
        return File(file.Content, ImportFile.XlsxContentType, file.FileName);
    }
}

/// <summary>A40 API-CONTRACT "Sessions" — upload, mapping, validation, execution, progress and reports.</summary>
[ApiController]
[Route("api/import")]
[RequiresFeature(ModuleCodes.DataImport)]
public class ImportSessionsController : ControllerBase
{
    private readonly IImportSessionService _sessions;
    private readonly IImportExecutionService _execution;
    private readonly IImportWorkbookService _workbooks;

    public ImportSessionsController(IImportSessionService sessions, IImportExecutionService execution, IImportWorkbookService workbooks)
    {
        _sessions = sessions;
        _execution = execution;
        _workbooks = workbooks;
    }

    private ImportCaller Caller => ImportCaller.From(User);

    [HttpPost("sessions")]
    [RequirePermission(P.IMPORT_UPLOAD)]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 12 * 1024 * 1024)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Upload(IFormFile? file, [FromForm] ImportUploadRequest req, CancellationToken ct)
    {
        if (file is null || file.Length == 0) throw new BadRequestException(ImportMessages.NoDataRows);
        await using var stream = file.OpenReadStream();
        var session = await _sessions.UploadAsync(stream, file.FileName, file.Length, req, Caller, ct);
        return StatusCode(StatusCodes.Status201Created, ApiResponse<ImportSessionDetail>.Ok(session, "File uploaded."));
    }

    [HttpGet("sessions")]
    [RequirePermission(P.IMPORT_UPLOAD, P.IMPORT_MANAGE)]
    public async Task<IActionResult> List([FromQuery] ImportSessionFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<ImportSessionListItem>>.Ok(await _sessions.ListAsync(filter, Caller, ct)));

    [HttpGet("sessions/{uuid:guid}")]
    [RequirePermission(P.IMPORT_UPLOAD, P.IMPORT_MANAGE)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<ImportSessionDetail>.Ok(await _sessions.GetAsync(uuid, Caller, ct)));

    [HttpGet("sessions/{uuid:guid}/preview")]
    [RequirePermission(P.IMPORT_UPLOAD, P.IMPORT_MANAGE)]
    public async Task<IActionResult> Preview(Guid uuid, [FromQuery] int rows = 20, CancellationToken ct = default) =>
        Ok(ApiResponse<ImportPreviewModel>.Ok(await _sessions.PreviewAsync(uuid, rows, Caller, ct)));

    [HttpGet("sessions/{uuid:guid}/mappings")]
    [RequirePermission(P.IMPORT_UPLOAD, P.IMPORT_MANAGE)]
    public async Task<IActionResult> Mappings(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<ImportColumnMappingModel>>.Ok(await _sessions.GetMappingsAsync(uuid, Caller, ct)));

    [HttpPut("sessions/{uuid:guid}/mappings")]
    [RequirePermission(P.IMPORT_UPLOAD)]
    public async Task<IActionResult> UpdateMappings(Guid uuid, [FromBody] ImportMappingsRequest req, CancellationToken ct) =>
        Ok(ApiResponse<ImportSessionDetail>.Ok(await _sessions.UpdateMappingsAsync(uuid, req ?? new(), Caller, ct), "Mappings saved."));

    [HttpGet("mapping-presets")]
    [RequirePermission(P.IMPORT_UPLOAD)]
    public async Task<IActionResult> Presets([FromQuery] string? entityType, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<ImportPresetModel>>.Ok(await _sessions.ListPresetsAsync(entityType, Caller, ct)));

    [HttpPost("sessions/{uuid:guid}/mappings/save")]
    [RequirePermission(P.IMPORT_UPLOAD)]
    public async Task<IActionResult> SavePreset(Guid uuid, [FromBody] ImportSavePresetRequest req, CancellationToken ct) =>
        Ok(ApiResponse<ImportSessionDetail>.Ok(await _sessions.SavePresetAsync(uuid, req?.PresetName, Caller, ct), "Mapping preset saved."));

    [HttpPost("sessions/{uuid:guid}/validate")]
    [RequirePermission(P.IMPORT_EXECUTE)]
    public async Task<IActionResult> Validate(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<ImportValidationResult>.Ok(await _sessions.ValidateAsync(uuid, Caller, ct), "Validation finished."));

    [HttpPost("sessions/{uuid:guid}/execute")]
    [RequirePermission(P.IMPORT_EXECUTE)]
    public async Task<IActionResult> Execute(Guid uuid, CancellationToken ct)
    {
        var result = await _execution.ExecuteAsync(uuid, Caller, ct);
        return Ok(ApiResponse<ImportExecuteResult>.Ok(result, result.IsBackground ? "Import started in the background." : "Import finished."));
    }

    [HttpPost("sessions/{uuid:guid}/cancel")]
    [RequirePermission(P.IMPORT_EXECUTE, P.IMPORT_MANAGE)]
    public async Task<IActionResult> Cancel(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<ImportSessionDetail>.Ok(await _sessions.CancelAsync(uuid, Caller, ct), "Import cancelled."));

    [HttpGet("sessions/{uuid:guid}/progress")]
    [RequirePermission(P.IMPORT_UPLOAD, P.IMPORT_MANAGE)]
    public async Task<IActionResult> Progress(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<ImportProgressModel>.Ok(await _sessions.ProgressAsync(uuid, Caller, ct)));

    [HttpGet("sessions/{uuid:guid}/errors")]
    [RequirePermission(P.IMPORT_UPLOAD, P.IMPORT_MANAGE)]
    public async Task<IActionResult> Errors(Guid uuid, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Ok(ApiResponse<PaginatedResponse<ImportErrorRowModel>>.Ok(await _sessions.ErrorsAsync(uuid, page, pageSize, Caller, ct)));

    [HttpGet("sessions/{uuid:guid}/errors/download")]
    [RequirePermission(P.IMPORT_UPLOAD, P.IMPORT_MANAGE)]
    public async Task<IActionResult> ErrorReport(Guid uuid, CancellationToken ct)
    {
        var file = await _workbooks.ErrorReportAsync(uuid, Caller, ct);
        return File(file.Content, ImportFile.XlsxContentType, file.FileName);
    }

    [HttpGet("sessions/{uuid:guid}/results/download")]
    [RequirePermission(P.IMPORT_UPLOAD, P.IMPORT_MANAGE)]
    public async Task<IActionResult> ResultsReport(Guid uuid, CancellationToken ct)
    {
        var file = await _workbooks.ResultsReportAsync(uuid, Caller, ct);
        return File(file.Content, ImportFile.XlsxContentType, file.FileName);
    }
}
