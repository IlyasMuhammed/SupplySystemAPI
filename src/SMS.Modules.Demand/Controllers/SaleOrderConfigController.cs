using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Controllers;

// A29-P3-03 §3.5 — GET is SUPPLY_DEPT_ADMIN | IT_ADMIN (read-only); PUT is SUPPLY_DEPT_ADMIN only.
// Both are enforced purely through the permission codes P3-01 seeded: every role that holds
// SALE_ORDER_CONFIG_READ can read (System Admin, Org Admin and Supply Dept Admin all do), and only
// Supply Dept Admin holds SALE_ORDER_CONFIG_WRITE — there is no separate role-name check needed.
[ApiController]
[Route("api/sale-order-config")]
[RequiresFeature("MODULE_DEMAND")]
public class SaleOrderConfigController : ControllerBase
{
    private readonly ISaleOrderConfigService _service;

    public SaleOrderConfigController(ISaleOrderConfigService service) => _service = service;

    [HttpGet]
    [RequirePermission(PermissionCodes.SALE_ORDER_CONFIG_READ)]
    public async Task<IActionResult> GetConfig()
    {
        var result = await _service.GetConfigAsync();
        return Ok(ApiResponse<SaleOrderConfigModel>.Ok(result));
    }

    [HttpPut]
    [RequirePermission(PermissionCodes.SALE_ORDER_CONFIG_WRITE)]
    public async Task<IActionResult> UpdateConfig([FromBody] UpdateSaleOrderConfigRequest req)
    {
        var result = await _service.UpdateConfigAsync(req, User.GetUserId());
        return Ok(ApiResponse<SaleOrderConfigModel>.Ok(result, StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpGet("audit")]
    [RequirePermission(PermissionCodes.SALE_ORDER_CONFIG_READ)]
    public async Task<IActionResult> GetAudit([FromQuery] SaleOrderConfigAuditFilter filter)
    {
        var result = await _service.GetAuditAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<SaleOrderConfigAuditModel>>.Ok(result));
    }
}
