using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Controllers;

/// <summary>
/// The home dashboard's numbers in one call. Any signed-in user may ask: the service leaves out each section the caller
/// has no permission for, so what comes back is exactly what that user's own list pages would show.
/// </summary>
[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _svc;

    public DashboardController(IDashboardService svc) => _svc = svc;

    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary(CancellationToken ct) =>
        Ok(ApiResponse<DashboardSummaryModel>.Ok(await _svc.GetSummaryAsync(User, ct)));
}
