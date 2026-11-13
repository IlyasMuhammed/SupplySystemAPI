using Microsoft.AspNetCore.Mvc;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Onboarding.Checklist;

/// <summary>A50 D-28 / tasks 283–287, 320: the dashboard checklist (docs/onboarding/SAMPLE-DATA.md §1.2).</summary>
[ApiController]
[Route("api/onboarding/checklist")]
public sealed class OnboardingChecklistController : ControllerBase
{
    private readonly IOnboardingChecklistService _checklist;

    public OnboardingChecklistController(IOnboardingChecklistService checklist) => _checklist = checklist;

    private int? UserId => User.GetUserId() is var id && id > 0 ? id : null;

    /// <summary>The checklist, seeded on first read, with the signals evaluated (completed steps are saved).</summary>
    [HttpGet]
    [RequirePermission(P.ONBOARDING_VIEW)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<OnboardingChecklistModel>.Ok(await _checklist.GetStatusAsync(ct)));

    /// <summary>"Refresh now": the same as GET, for FE to call after an action.</summary>
    [HttpPost]
    [RequirePermission(P.ONBOARDING_VIEW)]
    public Task<IActionResult> Refresh(CancellationToken ct) => Get(ct);

    /// <summary>Ticks a step by hand. Unknown code = 404; an already-ticked step is left as it is.</summary>
    [HttpPost("steps/{code}/complete")]
    [RequirePermission(P.ONBOARDING_VIEW)]
    public async Task<IActionResult> Complete(string code, CancellationToken ct) =>
        Ok(ApiResponse<OnboardingChecklistModel>.Ok(await _checklist.CompleteStepAsync(code, UserId, ct), "Step completed."));

    /// <summary>"I'll handle this myself": hides the checklist for good.</summary>
    [HttpPost("dismiss")]
    [RequirePermission(P.ONBOARDING_VIEW)]
    public async Task<IActionResult> Dismiss(CancellationToken ct) =>
        Ok(ApiResponse<OnboardingChecklistModel>.Ok(await _checklist.DismissAsync(UserId, ct), "Checklist dismissed."));
}
