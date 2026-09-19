using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Rating;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Shipping rules — the standing decisions that route goods to a carrier automatically.
/// </summary>
/// <remarks>
/// Editing is gated by <c>SHIPPING_RULE_MANAGE</c>: a rule is applied without anybody looking at it,
/// which makes changing one a larger act than choosing a carrier for a single consignment.
/// </remarks>
[ApiController]
[Route("api/logistics/shipping-rules")]
[RequiresFeature("MODULE_LOGISTICS")]
public class ShippingRulesController : ControllerBase
{
    private readonly IShippingRuleService _svc;
    public ShippingRulesController(IShippingRuleService svc) => _svc = svc;

    [RequirePermission(PermissionCodes.SHIPPING_RULE_MANAGE)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateShippingRuleRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    /// <summary>Every rule, in the order they are tried. Each carries a one-line summary of itself.</summary>
    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var rules = await _svc.GetAllAsync();
        return Ok(ApiResponse<IReadOnlyList<ShippingRuleModel>>.Ok(rules));
    }

    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var rule = await _svc.GetByUuidAsync(uuid);
        return rule is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ShippingRuleModel>.Ok(rule));
    }

    [RequirePermission(PermissionCodes.SHIPPING_RULE_MANAGE)]
    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchShippingRuleRequest req)
    {
        var updated = await _svc.PatchAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [RequirePermission(PermissionCodes.SHIPPING_RULE_MANAGE)]
    [HttpDelete("{uuid:guid}")]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        var deleted = await _svc.DeleteAsync(uuid, User.GetUserId());
        return deleted
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>
    /// Which rule fires for a consignment, <b>why the earlier ones did not</b>, and what the winner
    /// comes to. Changes nothing — this is what a screen shows before anybody commits.
    /// </summary>
    [RequirePermission(PermissionCodes.SHIPMENT_RATE_VIEW)]
    [HttpGet("evaluate/{consignmentUuid:guid}")]
    public async Task<IActionResult> Evaluate(
        Guid consignmentUuid, [FromQuery] DateTime? shipDate, CancellationToken ct)
    {
        var decision = await _svc.EvaluateAsync(consignmentUuid, shipDate, ct);
        return decision is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ShippingRuleDecisionModel>.Ok(decision));
    }

    /// <summary>Evaluates, then accepts what the rule chose — carrier, service and price.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("apply/{consignmentUuid:guid}")]
    public async Task<IActionResult> Apply(
        Guid consignmentUuid, [FromQuery] DateTime? shipDate, CancellationToken ct)
    {
        var rate = await _svc.ApplyAsync(consignmentUuid, shipDate, User.GetUserId(), ct);
        return rate is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<ConsignmentRateModel>.Ok(rate, "Shipping rule applied."));
    }
}
