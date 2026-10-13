using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>A42 API-CONTRACT "External mappings".</summary>
[Route("api/finance/external-mappings")]
public sealed class ExternalMappingsController : AccountingControllerBase
{
    private readonly IExternalMappingService _mappings;

    public ExternalMappingsController(ITenantContext tenant, IExternalMappingService mappings) : base(tenant) => _mappings = mappings;

    [HttpGet]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> List([FromQuery] string? systemType, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<ExternalMappingModel>>.Ok(await _mappings.ListAsync(Caller, systemType, ct)));

    [HttpPost]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Create([FromBody] CreateExternalMappingRequest request, CancellationToken ct) =>
        Ok(ApiResponse<ExternalMappingModel>.Ok(await _mappings.CreateAsync(Caller, request, ct), "Mapping created."));

    [HttpPut("{uuid:guid}")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateExternalMappingRequest request, CancellationToken ct) =>
        Ok(ApiResponse<ExternalMappingModel>.Ok(await _mappings.UpdateAsync(Caller, uuid, request, ct), "Mapping updated."));

    [HttpDelete("{uuid:guid}")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _mappings.DeleteAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("Mapping deleted."));
    }

    /// <summary>multipart: <c>file</c> (CSV sms_account_code,external_code,external_name,direction), <c>systemType</c>, <c>systemName</c>.</summary>
    [HttpPost("import")]
    [RequirePermission(ManageSetup)]
    [RequestSizeLimit(ExternalMappingService.MaxImportBytes + 64 * 1024)]
    public async Task<IActionResult> Import(IFormFile? file, [FromForm] string? systemType, [FromForm] string? systemName, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(ApiResponse.Fail("Choose a CSV file to import."));
        if (file.Length > ExternalMappingService.MaxImportBytes) return BadRequest(ApiResponse.Fail("The file is larger than 5 MB."));
        await using var stream = file.OpenReadStream();
        var result = await _mappings.ImportAsync(Caller, stream, systemType, systemName, ct);
        var message = $"{result.Created} created, {result.Updated} updated" + (result.Errors.Count > 0 ? $", {result.Errors.Count} lines refused." : ".");
        return Ok(ApiResponse<MappingImportResult>.Ok(result, message));
    }

    [HttpGet("export")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Export([FromQuery] string? systemType, CancellationToken ct) =>
        File(await _mappings.ExportAsync(Caller, systemType, ct), "text/csv", "external-mappings.csv");
}
