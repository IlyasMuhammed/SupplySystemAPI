using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Controllers;

/// <summary>A42 API-CONTRACT "Accounting defaults".</summary>
[Route("api/finance/accounting-defaults")]
public sealed class AccountingDefaultsController : AccountingControllerBase
{
    private readonly IDefaultsService _defaults;

    public AccountingDefaultsController(ITenantContext tenant, IDefaultsService defaults) : base(tenant) => _defaults = defaults;

    [HttpGet]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<AccountingDefaultsModel>.Ok(await _defaults.GetAsync(Caller, ct)));

    [HttpPut("{defaultKey}")]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> Assign(string defaultKey, [FromBody] AssignDefaultRequest request, CancellationToken ct) =>
        Ok(ApiResponse<AccountingDefaultModel>.Ok(await _defaults.AssignAsync(Caller, defaultKey, request?.AccountUuid, ct), "Default saved."));

    /// <summary>400 with <c>result.problems</c> listing every refused item.</summary>
    [HttpPut]
    [RequirePermission(ManageSetup)]
    public async Task<IActionResult> SaveAll([FromBody] SaveDefaultsRequest request, CancellationToken ct)
    {
        try
        {
            return Ok(ApiResponse<AccountingDefaultsModel>.Ok(await _defaults.SaveAllAsync(Caller, request, ct), "Defaults saved."));
        }
        catch (DefaultsValidationException ex)
        {
            return BadRequest(ApiResponse.Fail(ex.Message, new { problems = ex.Problems }));
        }
    }

    [HttpPost("validate")]
    [RequirePermission(View, ManageAccounts, ManageSetup)]
    public async Task<IActionResult> Validate(CancellationToken ct) =>
        Ok(ApiResponse<DefaultsValidationModel>.Ok(await _defaults.ValidateAsync(Caller, ct)));
}
