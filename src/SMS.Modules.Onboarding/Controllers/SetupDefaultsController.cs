using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Onboarding.Models;
using SMS.Modules.Onboarding.Services.Defaults;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Onboarding.Controllers;

/// <summary>
/// A50 D-4 / tasks 119–122 — the Super Admin store of setup defaults (no organization). Platform super admin only: the code
/// alone is carried by roles any organization can hold, so [RequireSuperAdmin] checks the is_super_admin claim too.
/// </summary>
[ApiController]
[Route("api/system/setup-defaults")]
[RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
[RequireSuperAdmin]
public sealed class SetupDefaultsController : ControllerBase
{
    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ISetupDefaultsService _defaults;

    public SetupDefaultsController(ISetupDefaultsService defaults) => _defaults = defaults;

    private int? UserId => User.GetUserId() is var id && id > 0 ? id : null;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? country, [FromQuery] string? industry, [FromQuery] string? category,
        [FromQuery] bool? active, CancellationToken ct) =>
        Ok(ApiResponse<List<SetupDefaultModel>>.Ok(await _defaults.ListAsync(country, industry, category, active, ct)));

    /// <summary>Categories (seeder / schema / module), countries and industries in use.</summary>
    [HttpGet("meta")]
    public async Task<IActionResult> Meta(CancellationToken ct) =>
        Ok(ApiResponse<SetupDefaultsMetaModel>.Ok(await _defaults.MetaAsync(ct)));

    /// <summary>The row the fallback chain picks for a (country, industry, category) — 404 when none.</summary>
    [HttpGet("resolve")]
    public async Task<IActionResult> Resolve([FromQuery] string? country, [FromQuery] string? industry, [FromQuery] string? category, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new BadRequestException("A category is required.");
        var hit = await _defaults.ResolveAsync(country, industry, category, ct)
                  ?? throw new NotFoundException($"No active default resolves for {SetupKey.Format(SetupKey.Country(country), SetupKey.Industry(industry), SetupKey.Category(category))}.");
        return Ok(ApiResponse<SetupDefaultModel>.Ok(SetupDefaultsService.ToResolvedModel(hit)));
    }

    /// <summary>Full pack-format export (a JSON file download).</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? country, [FromQuery] string? industry, [FromQuery] string? category, CancellationToken ct)
    {
        var pack = await _defaults.ExportAsync(country, industry, category, ct);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(pack, ExportJson));
        return File(bytes, "application/json", $"setup-defaults-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<SetupDefaultModel>.Ok(await _defaults.GetAsync(uuid, ct)));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSetupDefaultRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SetupDefaultModel>.Ok(await _defaults.CreateAsync(request ?? new(), UserId, ct), "Default created."));

    /// <summary>Version +1 when the payload changes (canonical compare). The key is immutable.</summary>
    [HttpPut("{uuid:guid}")]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateSetupDefaultRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SetupDefaultModel>.Ok(await _defaults.UpdateAsync(uuid, request ?? new(), UserId, ct), "Default saved."));

    /// <summary>409 while an organization has applied it (or it is a built-in pack row) — unpublish instead.</summary>
    [HttpDelete("{uuid:guid}")]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _defaults.DeleteAsync(uuid, ct);
        return Ok(ApiResponse.Ok("Default deleted."));
    }

    [HttpPost("{uuid:guid}/publish")]
    public async Task<IActionResult> Publish(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<SetupDefaultModel>.Ok(await _defaults.SetActiveAsync(uuid, true, UserId, ct), "Default published."));

    [HttpPost("{uuid:guid}/unpublish")]
    public async Task<IActionResult> Unpublish(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<SetupDefaultModel>.Ok(await _defaults.SetActiveAsync(uuid, false, UserId, ct), "Default unpublished."));

    [HttpPost("validate")]
    public IActionResult Validate([FromBody] ValidateSetupPayloadRequest request) =>
        Ok(ApiResponse<ValidateSetupPayloadResult>.Ok(_defaults.Validate(request?.DataCategory, request?.DataPayload ?? default)));

    /// <summary>Bulk import of a pack file (or a bare array of items); all or nothing (task 039). 400 with the same body on errors.</summary>
    [HttpPost("import")]
    [RequestSizeLimit(SetupDefaultsPackFormat.MaxBodyBytes)]
    public async Task<IActionResult> Import([FromBody] JsonElement body, [FromQuery] bool dryRun = false, [FromQuery] string mode = SetupImportModes.Upsert,
        [FromQuery] bool skipInvalid = false, CancellationToken ct = default)
    {
        SetupDefaultsPackFile pack;
        try
        {
            pack = body.ValueKind == JsonValueKind.Array
                ? new SetupDefaultsPackFile { Format = SetupDefaultsPackFormat.Name, Defaults = body.Deserialize<List<SetupDefaultsPackItem>>(ExportJson) ?? new() }
                : body.Deserialize<SetupDefaultsPackFile>(ExportJson) ?? new();
        }
        catch (JsonException ex)
        {
            throw new BadRequestException($"The file is not a setup defaults pack: {ex.Message}");
        }

        var result = await _defaults.ImportAsync(pack, dryRun, mode, UserId, ct, skipInvalid);
        if (result.Errors.Count > 0 && !skipInvalid)
            return BadRequest(ApiResponse.Fail($"{result.Errors.Count} item(s) are invalid — nothing was imported.", result));
        return Ok(ApiResponse<SetupDefaultsImportResult>.Ok(result,
            dryRun ? "Dry run: nothing was saved." : result.Invalid > 0 ? $"Imported: {result.Created} created, {result.Updated} updated, {result.Unchanged} unchanged, {result.Invalid} invalid skipped." : $"Imported: {result.Created} created, {result.Updated} updated, {result.Unchanged} unchanged."));
    }
}

