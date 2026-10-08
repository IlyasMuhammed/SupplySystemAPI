using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Controllers;

/// <summary>
/// The KPI dashboard's sales, fulfilment, manufacturing and receivables KPIs. The page's own gate (REPORT_VIEW or
/// REPORT_EXPORT) lets the caller in; each section then needs that area's view permission, and is null without it.
/// </summary>
[ApiController]
[Route("api/reports/kpis")]
[RequiresFeature("MODULE_REPORTS")]
[RequirePermission(PermissionCodes.REPORT_VIEW, PermissionCodes.REPORT_EXPORT)]
public class OperationsKpiController : ControllerBase
{
    private readonly IOperationsKpiService _svc;

    public OperationsKpiController(IOperationsKpiService svc) => _svc = svc;

    /// <param name="days">The rolling window, 7–365 days (default 90).</param>
    [HttpGet("operations")]
    public async Task<IActionResult> GetOperations([FromQuery] int days = 90, CancellationToken ct = default) =>
        Ok(ApiResponse<OperationsKpiModel>.Ok(await _svc.GetAsync(User, days, ct)));
}
