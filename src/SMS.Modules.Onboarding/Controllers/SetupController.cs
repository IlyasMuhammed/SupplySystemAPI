using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Onboarding.Models;
using SMS.Modules.Onboarding.Services.Setup;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Onboarding.Controllers;

/// <summary>A50 API-CONTRACT §4 / tasks 108–118 — the org admin's setup wizard and default data dashboard.</summary>
[ApiController]
[Route("api/onboarding")]
public sealed class SetupController : ControllerBase
{
    private readonly ITenantSetupService _setup;

    public SetupController(ITenantSetupService setup) => _setup = setup;

    private int? UserId => User.GetUserId() is var id && id > 0 ? id : null;

    /// <summary>D-7 — the setup status for the first-login guard and the "Setup incomplete" banner. Any signed-in user; null without an organization.</summary>
    [HttpGet("setup/status")]
    [Authorize]
    public async Task<IActionResult> Status(CancellationToken ct) =>
        Ok(ApiResponse<SetupStatusModel?>.Ok(await _setup.GetStatusAsync(User.GetPermissions().Contains(P.SETUP_WIZARD_RUN), ct)));

    /// <summary>Step 1 — the countries that have defaults (GLOBAL last).</summary>
    [HttpGet("setup/countries")]
    [RequirePermission(P.SETUP_WIZARD_RUN)]
    public async Task<IActionResult> Countries(CancellationToken ct) =>
        Ok(ApiResponse<List<SetupOptionModel>>.Ok(await _setup.GetCountriesAsync(ct)));

    /// <summary>Step 1 — the industries with defaults for a country (or GLOBAL); GLOBAL is always offered.</summary>
    [HttpGet("setup/industries")]
    [RequirePermission(P.SETUP_WIZARD_RUN)]
    public async Task<IActionResult> Industries([FromQuery] string? country, CancellationToken ct) =>
        Ok(ApiResponse<List<SetupOptionModel>>.Ok(await _setup.GetIndustriesAsync(country, ct)));

    /// <summary>The resolved payload and what it would add, per category, without seeding anything.</summary>
    [HttpPost("setup/preview")]
    [RequirePermission(P.SETUP_WIZARD_RUN)]
    public async Task<IActionResult> Preview([FromBody] SetupPreviewRequest request, CancellationToken ct) =>
        Ok(ApiResponse<List<SetupPreviewItem>>.Ok(await _setup.PreviewAsync(request ?? new(), ct)));

    /// <summary>"Launch": applies every category in order, one at a time; stops at the first failure (D-11). Idempotent re-run.</summary>
    [HttpPost("setup/apply")]
    [RequirePermission(P.SETUP_WIZARD_RUN)]
    public async Task<IActionResult> Apply([FromBody] SetupApplyRequest request, CancellationToken ct)
    {
        var result = await _setup.ApplyAsync(request ?? new(), UserId, ct);
        return Ok(ApiResponse<SetupApplyResponse>.Ok(result, result.Completed ? "Setup applied." : "Setup stopped — see the categories that need attention."));
    }

    /// <summary>BR-50-003 — skip the wizard: GLOBAL defaults are applied and the status becomes Skipped.</summary>
    [HttpPost("setup/skip")]
    [RequirePermission(P.SETUP_WIZARD_RUN)]
    public async Task<IActionResult> Skip(CancellationToken ct) =>
        Ok(ApiResponse<SetupApplyResponse>.Ok(await _setup.SkipAsync(UserId, ct), "Setup skipped; the general defaults were applied."));

    [HttpGet("setup/state")]
    [RequirePermission(P.SETUP_WIZARD_RUN)]
    public async Task<IActionResult> State(CancellationToken ct) =>
        Ok(ApiResponse<SetupStateModel>.Ok(await _setup.GetStateAsync(ct)));

    /// <summary>BR-50-002 — saves one step's draft (the wizard is resumable).</summary>
    [HttpPost("setup/state/{step:int}")]
    [RequirePermission(P.SETUP_WIZARD_RUN)]
    public async Task<IActionResult> SaveStep(int step, [FromBody] SaveSetupStepRequest request, CancellationToken ct) =>
        Ok(ApiResponse<SetupStateModel>.Ok(await _setup.SaveStepAsync(step, request ?? new(), ct), "Progress saved."));

    /// <summary>The Default Data dashboard: seeded / current / modified per category, last applied, versions.</summary>
    [HttpGet("setup/categories")]
    [RequirePermission(P.SETUP_WIZARD_RUN)]
    public async Task<IActionResult> Categories(CancellationToken ct) =>
        Ok(ApiResponse<SetupCategoriesModel>.Ok(await _setup.GetCategoriesAsync(ct)));

    /// <summary>Merge / Overwrite / Full (BR-50-006..008). A Full reset blocked by references answers 409 with the referenced keys.</summary>
    [HttpPost("setup/categories/{category}/reset")]
    [RequirePermission(P.SETUP_WIZARD_RUN)]
    public async Task<IActionResult> Reset(string category, [FromBody] SetupResetRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _setup.ResetAsync(category, request ?? new(), UserId, ct);
            return Ok(ApiResponse<SetupCategoryResult>.Ok(result, result.Ok ? $"{result.Category} reset ({request?.Mode})." : $"{result.Category} reset failed."));
        }
        catch (SetupResetBlockedException ex)
        {
            return Conflict(ApiResponse.Fail(ex.Message, ex.Check));
        }
    }

    /// <summary>D-5 — the provenance of a category's seeded records (the optional "Seeded" badge).</summary>
    [HttpGet("seeded")]
    [RequirePermission(P.SETUP_WIZARD_RUN)]
    public async Task<IActionResult> Seeded([FromQuery] string? category, CancellationToken ct) =>
        Ok(ApiResponse<List<SeededRecordModel>>.Ok(await _setup.GetSeededAsync(category ?? string.Empty, ct)));
}
