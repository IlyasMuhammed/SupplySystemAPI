using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Controllers;

[ApiController]
[Route("api/supplier-scorecard")]
public class ScorecardDashboardController : ControllerBase
{
    private readonly IScorecardDashboardService _svc;
    public ScorecardDashboardController(IScorecardDashboardService svc) => _svc = svc;

    [HttpGet]
    [RequirePermission(PermissionCodes.SUPPLIER_VIEW)]
    public async Task<IActionResult> GetRanking([FromQuery] SupplierScorecardRankingFilter filter)
    {
        var (start, end) = ParsePeriod(filter);
        var result = await _svc.GetRankingAsync(start, end);
        return Ok(ApiResponse<SupplierScorecardRankingResponse>.Ok(result));
    }

    [HttpGet("{supplierId:guid}")]
    [RequirePermission(PermissionCodes.SUPPLIER_VIEW)]
    public async Task<IActionResult> GetSupplierDetail(Guid supplierId)
    {
        var detail = await _svc.GetSupplierDetailAsync(supplierId);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SupplierScorecardDetailModel>.Ok(detail));
    }

    // SC-007 — lightweight grade check for the Supplier Master banner and PO creation warning.
    // Absolute route so it lives alongside the other api/suppliers/{id}/... endpoints despite this
    // controller's own [Route] prefix being api/supplier-scorecard.
    [HttpGet("/api/suppliers/{supplierId:guid}/score-summary")]
    [RequirePermission(PermissionCodes.SUPPLIER_VIEW)]
    public async Task<IActionResult> GetScoreSummary(Guid supplierId)
    {
        var result = await _svc.GetScoreSummaryAsync(supplierId);
        return Ok(ApiResponse<SupplierScoreSummaryModel>.Ok(result));
    }

    private static (DateTime Start, DateTime End) ParsePeriod(SupplierScorecardRankingFilter filter)
    {
        var end   = DateTime.TryParse(filter.PeriodEnd, out var e) ? e.Date.AddDays(1) : DateTime.UtcNow.Date.AddDays(1);
        var start = DateTime.TryParse(filter.PeriodStart, out var s) ? s.Date : end.AddMonths(-3); // default: trailing 3 months
        return (start, end);
    }
}
