using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Controllers;

[ApiController]
[Route("api/supplier-scorecard")]
public class ScorecardRecalculationController : ControllerBase
{
    private readonly IScorecardRecalculationService _svc;
    private readonly ISupplierScoringService _scoring;
    private readonly IConfiguration _config;

    public ScorecardRecalculationController(IScorecardRecalculationService svc, ISupplierScoringService scoring, IConfiguration config)
    {
        _svc     = svc;
        _scoring = scoring;
        _config  = config;
    }

    // Scores any APPROVED GRN that has no GrnScoreDetails row yet (e.g. approved before the
    // approval-triggered scoring hook was wired up, or during an outage of it). Run this once,
    // then POST /recalculate to roll the newly-scored GRNs up into a snapshot.
    [HttpPost("backfill-grn-scores")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> BackfillGrnScores([FromQuery] bool force = false)
    {
        var count = await _scoring.BackfillMissingScoresAsync(force);
        return Ok(ApiResponse<object>.Ok(new { grnsScored = count }));
    }

    // Admin-triggered full recalculation. Defaults to the most recently completed period at the
    // configured frequency, matching the scheduled job; pass periodStart/periodEnd to recalculate an
    // arbitrary range instead (e.g. to catch up several historical periods at once).
    [HttpPost("recalculate")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> RecalculateAll([FromQuery] string? periodStart = null, [FromQuery] string? periodEnd = null)
    {
        var (start, end) = ResolvePeriod(periodStart, periodEnd);
        var count = await _svc.RecalculateAllAsync(start, end, User.GetUserId());
        return Ok(ApiResponse<object>.Ok(new { periodStart = start, periodEnd = end, suppliersRecalculated = count }));
    }

    // Admin-triggered single-supplier recalculation, same period resolution as RecalculateAll.
    [HttpPost("{supplierId:guid}/recalculate")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> RecalculateSupplier(Guid supplierId, [FromQuery] string? periodStart = null, [FromQuery] string? periodEnd = null)
    {
        var (start, end) = ResolvePeriod(periodStart, periodEnd);
        var scored = await _svc.RecalculateSupplierAsync(supplierId, start, end, User.GetUserId());
        return Ok(scored
            ? ApiResponse<object>.Ok(new { periodStart = start, periodEnd = end }, "Supplier scorecard recalculated.")
            : ApiResponse<object>.Ok(new { periodStart = start, periodEnd = end }, "No scored GRNs found for this supplier in the period; no snapshot created."));
    }

    private (DateTime Start, DateTime End) ResolvePeriod(string? periodStart, string? periodEnd)
    {
        if (DateTime.TryParse(periodStart, out var start) && DateTime.TryParse(periodEnd, out var end))
            return (start.Date, end.Date.AddDays(1));

        var frequency = _config["SupplierScorecard:RecalculationFrequency"];
        return ScorecardPeriodResolver.ResolvePreviousPeriod(frequency, DateTime.UtcNow);
    }
}
