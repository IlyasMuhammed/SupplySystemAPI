using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Controllers;

// A29-P2-04 §2.2/§2.4 — Pricing Rules CRUD + a resolve endpoint that previews what
// IPricingService.ResolveSalePriceAsync would actually pick for a given variant/partner/qty/date.
[ApiController]
[Route("api/pricing-rules")]
[RequiresFeature("MODULE_INVENTORY")]
public class PricingRulesController : ControllerBase
{
    private readonly IPricingRuleService _svc;
    private readonly IPricingService _resolver;

    public PricingRulesController(IPricingRuleService svc, IPricingService resolver)
    {
        _svc      = svc;
        _resolver = resolver;
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> Create([FromBody] CreatePricingRuleRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdatePricingRuleRequest req)
    {
        var updated = await _svc.UpdateAsync(uuid, req);
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpDelete("{uuid:guid}")]
    [RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        var deleted = await _svc.DeleteAsync(uuid);
        return deleted
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var result = await _svc.GetByIdAsync(uuid);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<PricingRuleModel>.Ok(result));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] PricingRuleListFilter filter)
    {
        var result = await _svc.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<PricingRuleModel>>.Ok(result));
    }

    // Price preview — what ResolveSalePriceAsync would pick right now, without creating or
    // changing anything. Named to match IPricingService.ResolveSalePriceAsync's own parameters
    // (Uuid-keyed, like every cross-module reference in this codebase) rather than the spec's
    // literal variantId/partnerId, which in this schema would mean the internal int id.
    [HttpGet("resolve")]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> Resolve(
        [FromQuery] Guid variantUuid, [FromQuery] Guid? partnerUuid,
        [FromQuery] decimal qty, [FromQuery] DateTime? date)
    {
        var result = await _resolver.ResolveSalePriceAsync(
            variantUuid, partnerUuid, qty, date ?? DateTime.UtcNow.Date);
        return Ok(ApiResponse<SalePriceResolution>.Ok(result));
    }
}
