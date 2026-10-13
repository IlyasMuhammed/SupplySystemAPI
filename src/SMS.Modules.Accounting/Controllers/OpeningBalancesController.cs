using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>A42 API-CONTRACT "Opening balances". The conversion date itself is Finance's api/finance/opening-balance-date.</summary>
[Route("api/finance/opening-balances")]
public sealed class OpeningBalancesController : AccountingControllerBase
{
    private readonly IOpeningBalanceService _opening;

    public OpeningBalancesController(ITenantContext tenant, IOpeningBalanceService opening) : base(tenant) => _opening = opening;

    [HttpGet]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<OpeningBalancesModel>.Ok(await _opening.GetAsync(Caller, ct)));

    [HttpGet("summary")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Summary(CancellationToken ct) =>
        Ok(ApiResponse<OpeningSummaryModel>.Ok(await _opening.SummaryAsync(Caller, ct)));

    /// <summary>400 with <c>result.errors</c> listing every refused entry.</summary>
    [HttpPut("batch")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> SaveBatch([FromBody] SaveOpeningBalancesRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(ApiResponse<OpeningBalancesModel>.Ok(await _opening.SaveBatchAsync(Caller, request, ct), "Opening balances saved."));
        }
        catch (OpeningBalanceValidationException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, new { errors = ex.Errors }));
        }
    }

    [HttpPost("post")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Post([FromBody] PostOpeningBalancesRequest? request, CancellationToken ct)
    {
        try
        {
            var body = request ?? new PostOpeningBalancesRequest();
            var result = await _opening.PostAsync(Caller, body, ct);
            return Ok(ApiResponse<PostOpeningBalancesResult>.Ok(result, body.Skip ? "Opening balances skipped." : "Opening balances posted."));
        }
        catch (OpeningBalanceValidationException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, new { errors = ex.Errors }));
        }
    }
}
