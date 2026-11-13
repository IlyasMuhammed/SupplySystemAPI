using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Onboarding.SampleData;

/// <summary>A50 D-27 / tasks 268–278, 321: sample data (docs/onboarding/SAMPLE-DATA.md §1.1).</summary>
[ApiController]
[Route("api/onboarding")]
public sealed class SampleDataController : ControllerBase
{
    private readonly ISampleDataService _samples;

    public SampleDataController(ISampleDataService samples) => _samples = samples;

    private int? UserId => User.GetUserId() is var id && id > 0 ? id : null;

    /// <summary>What a load would create, whether demo data exists, and whether a load is allowed now.</summary>
    [HttpGet("sample-data")]
    [RequirePermission(P.SAMPLE_DATA_MANAGE)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<SampleDataStatusModel>.Ok(await _samples.GetStatusAsync(ct)));

    /// <summary>Creates the demo records through each module's own services. 409 when demo data already exists.</summary>
    [HttpPost("sample-data/load")]
    [RequirePermission(P.SAMPLE_DATA_MANAGE)]
    public async Task<IActionResult> Load(CancellationToken ct)
    {
        var result = await _samples.LoadAsync(UserId, ct);
        return Ok(ApiResponse<SampleDataLoadResult>.Ok(result, $"{result.Total} sample records created."));
    }

    /// <summary>Removes the demo records in reverse order; one that real data now uses is kept and listed.</summary>
    [HttpDelete("sample-data")]
    [RequirePermission(P.SAMPLE_DATA_MANAGE)]
    public async Task<IActionResult> Delete(CancellationToken ct)
    {
        var result = await _samples.CleanupAsync(UserId, ct);
        var message = result.Kept.Count == 0
            ? $"{result.Total} sample records removed."
            : $"{result.Total} sample records removed; {result.Kept.Count} kept because real data uses them.";
        return Ok(ApiResponse<SampleDataCleanupResult>.Ok(result, message));
    }

    /// <summary>The "Demo" badge: the uuids of this organization's demo records (of one type when given). Any signed-in user.</summary>
    [HttpGet("demo/refs")]
    [Authorize]
    public async Task<IActionResult> Refs([FromQuery] string? entityType, CancellationToken ct) =>
        Ok(ApiResponse<List<string>>.Ok(await _samples.GetRefsAsync(entityType, ct)));
}
