using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>A42 API-CONTRACT "Dashboard" — the one post-setup GL endpoint of A42, gated by the setup (D-4).</summary>
[Route("api/finance/gl")]
public sealed class GlDashboardController : AccountingControllerBase
{
    private readonly IDashboardService _dashboard;

    public GlDashboardController(ITenantContext tenant, IDashboardService dashboard) : base(tenant) => _dashboard = dashboard;

    [HttpGet("dashboard")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    [RequiresGlSetup]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<GlDashboardModel>.Ok(await _dashboard.GetAsync(Caller, ct)));
}
