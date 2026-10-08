using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services.LeadTimes;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Controllers;

/// <summary>
/// A34 C3/C4 — lead-time defaults, a variant's lead-time components and the calculator (API-CONTRACT §4.4–§4.6).
/// Inventory, so the calculator also works for organizations that don't manufacture. Gates per D-24 / contract §2;
/// errors through GlobalExceptionMiddleware (400 rules, 404 another organization's variant).
/// </summary>
[ApiController]
[RequiresFeature("MODULE_INVENTORY")]
public class LeadTimesController : ControllerBase
{
    private readonly ILeadTimeDefaultsService _defaults;
    private readonly IVariantLeadTimeService _variants;
    private readonly ILeadTimeCalculator _calculator;
    private readonly IManufacturingLeadTimeCalculator _manufacturing;
    private readonly ITenantContext _tenant;

    public LeadTimesController(
        ILeadTimeDefaultsService defaults, IVariantLeadTimeService variants, ILeadTimeCalculator calculator,
        IManufacturingLeadTimeCalculator manufacturing, ITenantContext tenant)
    {
        _defaults      = defaults;
        _variants      = variants;
        _calculator    = calculator;
        _manufacturing = manufacturing;
        _tenant        = tenant;
    }

    /// <summary>
    /// C4 — the lead time of a variant for a quantity (BR-C4-01, T-C4-01..05): components, total, earliest delivery,
    /// and with a requested date the latest start. For unsaved forms; saved lines use the document's own endpoint.
    /// Always the caller's own organization (a super admin's included).
    /// </summary>
    [HttpPost("api/lead-time/calculate")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_ORDER_CREATE, PermissionCodes.SALE_ORDER_EDIT,
                       PermissionCodes.SALE_INQUIRY_VIEW, PermissionCodes.SALE_INQUIRY_EDIT, PermissionCodes.SALE_QUOTATION_VIEW,
                       PermissionCodes.SALE_QUOTATION_EDIT, PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> Calculate([FromBody] LeadTimeCalculateRequest request, CancellationToken ct)
    {
        var result = await _calculator.CalculateAsync(_tenant.OrganizationId,
            new LeadTimeRequest(request.VariantUuid, request.Quantity, request.RouteUuid, request.RequestedDate), ct);
        return Ok(ApiResponse<LeadTimeResult>.Ok(result));
    }

    /// <summary>C4 — the BOM-aware manufacturing lead time as a per-level tree ("Recalculate from BOM"; never writes).</summary>
    [HttpPost("api/lead-time/calculate-manufacturing")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_ORDER_CREATE, PermissionCodes.SALE_ORDER_EDIT,
                       PermissionCodes.SALE_INQUIRY_VIEW, PermissionCodes.SALE_INQUIRY_EDIT, PermissionCodes.SALE_QUOTATION_VIEW,
                       PermissionCodes.SALE_QUOTATION_EDIT, PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> CalculateManufacturing([FromBody] LeadTimeCalculateRequest request, CancellationToken ct)
    {
        var result = await _manufacturing.CalculateManufacturingAsync(_tenant.OrganizationId, request.VariantUuid, request.Quantity, ct);
        return Ok(ApiResponse<ManufacturingLeadTimeNodeModel>.Ok(result));
    }

    /// <summary>The organization's defaults, or the system defaults with <c>isSaved: false</c>.</summary>
    [HttpGet("api/lead-time/defaults")]
    [RequirePermission(PermissionCodes.LEAD_TIME_DEFAULTS_MANAGE, PermissionCodes.INVENTORY_VIEW,
                       PermissionCodes.STOCK_MANAGE, PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetDefaults(CancellationToken ct) =>
        Ok(ApiResponse<LeadTimeDefaultsModel>.Ok(await _defaults.GetAsync(ct)));

    /// <summary>Upsert of the organization's defaults (T-C3-06).</summary>
    [HttpPut("api/lead-time/defaults")]
    [RequirePermission(PermissionCodes.LEAD_TIME_DEFAULTS_MANAGE)]
    public async Task<IActionResult> UpdateDefaults([FromBody] UpdateLeadTimeDefaultsRequest request, CancellationToken ct) =>
        Ok(ApiResponse<LeadTimeDefaultsModel>.Ok(
            await _defaults.UpdateAsync(request, User.GetUserId(), ct), "Lead-time defaults saved."));

    /// <summary>The variant's 8 components with sources and visibility (T-C3-01..05).</summary>
    [HttpGet("api/variants/{uuid:guid}/lead-times")]
    [RequirePermission(PermissionCodes.LEAD_TIME_DEFAULTS_MANAGE, PermissionCodes.INVENTORY_VIEW,
                       PermissionCodes.STOCK_MANAGE, PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetVariantLeadTimes(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<VariantLeadTimesModel>.Ok(await _variants.GetAsync(uuid, ct)));

    /// <summary>Replaces the variant's 8 overrides (null = use the default).</summary>
    [HttpPut("api/variants/{uuid:guid}/lead-times")]
    [RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> UpdateVariantLeadTimes(
        Guid uuid, [FromBody] UpdateVariantLeadTimesRequest request, CancellationToken ct) =>
        Ok(ApiResponse<VariantLeadTimesModel>.Ok(await _variants.UpdateAsync(uuid, request, ct), "Lead times saved."));
}
