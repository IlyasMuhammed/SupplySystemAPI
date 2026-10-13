using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>A42 API-CONTRACT "Fiscal years &amp; periods".</summary>
[Route("api/finance")]
public sealed class FiscalYearsController : AccountingControllerBase
{
    private readonly IFiscalYearService _years;

    public FiscalYearsController(ITenantContext tenant, IFiscalYearService years) : base(tenant) => _years = years;

    [HttpGet("fiscal-years")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<FiscalYearModel>>.Ok(await _years.ListAsync(Caller, ct)));

    [HttpGet("fiscal-years/{uuid:guid}")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<FiscalYearModel>.Ok(await _years.GetAsync(Caller, uuid, ct)));

    [HttpGet("fiscal-years/{uuid:guid}/periods")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Periods(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<FiscalPeriodModel>>.Ok(await _years.GetPeriodsAsync(Caller, uuid, ct)));

    [HttpPost("fiscal-years")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Create([FromBody] CreateFiscalYearRequest request, CancellationToken ct) =>
        Ok(ApiResponse<FiscalYearModel>.Ok(await _years.CreateAsync(Caller, request, ct), "Fiscal year created."));

    [HttpPut("fiscal-years/{uuid:guid}")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateFiscalYearRequest request, CancellationToken ct) =>
        Ok(ApiResponse<FiscalYearModel>.Ok(await _years.UpdateAsync(Caller, uuid, request, ct), "Fiscal year updated."));

    [HttpPost("fiscal-years/{uuid:guid}/close")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Close(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<FiscalYearModel>.Ok(await _years.CloseAsync(Caller, uuid, ct), "Fiscal year closed."));

    [HttpPut("fiscal-periods/{uuid:guid}")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> UpdatePeriod(Guid uuid, [FromBody] UpdateFiscalPeriodRequest request, CancellationToken ct) =>
        Ok(ApiResponse<FiscalPeriodModel>.Ok(await _years.UpdatePeriodAsync(Caller, uuid, request, ct), "Fiscal period updated."));
}
