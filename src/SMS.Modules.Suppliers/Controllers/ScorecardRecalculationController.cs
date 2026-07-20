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
    private readonly IConfiguration _config;

    public ScorecardRecalculationController(IScorecardRecalculationService svc, IConfiguration config)
    {
        _svc    = svc;
        _config = config;
    }

    // Admin-triggered full recalculation, for the most recently completed period at the configured frequency.
    [HttpPost("recalculate")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> RecalculateAll()
    {
        var (start, end) = CurrentPeriod();
        var count = await _svc.RecalculateAllAsync(start, end, User.GetUserId());
        return Ok(ApiResponse<object>.Ok(new { periodStart = start, periodEnd = end, suppliersRecalculated = count }));
    }

    // Admin-triggered single-supplier recalculation, same period as RecalculateAll.
    [HttpPost("{supplierId:guid}/recalculate")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> RecalculateSupplier(Guid supplierId)
    {
        var (start, end) = CurrentPeriod();
        var scored = await _svc.RecalculateSupplierAsync(supplierId, start, end, User.GetUserId());
        return Ok(scored
            ? ApiResponse<object>.Ok(new { periodStart = start, periodEnd = end }, "Supplier scorecard recalculated.")
            : ApiResponse<object>.Ok(new { periodStart = start, periodEnd = end }, "No scored GRNs found for this supplier in the period; no snapshot created."));
    }

    private (DateTime Start, DateTime End) CurrentPeriod()
    {
        var frequency = _config["SupplierScorecard:RecalculationFrequency"];
        return ScorecardPeriodResolver.ResolvePreviousPeriod(frequency, DateTime.UtcNow);
    }
}
