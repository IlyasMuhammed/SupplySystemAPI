using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Finance.Controllers;

// A29-P8-05 §11.4 — the product ledger's HTTP surface: a variant's history, where it stands, and what
// each product earned against what it cost. All three are read-only and gated by PRODUCT_LEDGER_VIEW —
// cost and margin are among the most sensitive numbers a company has, so the generic REPORT_VIEW that
// opens the other reports is not enough for the profitability report either.

/// <summary>What a variant cost and where it stands.</summary>
[ApiController]
[Route("api/product-ledger")]
[RequiresFeature("MODULE_FINANCE")]
public class ProductLedgerController : ControllerBase
{
    private readonly IProductLedgerQueryService _svc;

    public ProductLedgerController(IProductLedgerQueryService svc) => _svc = svc;

    /// <summary>A page of the variant's ledger, newest first. <c>variantId</c> is the variant's UUID.</summary>
    [HttpGet("{variantId:guid}")]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    public async Task<IActionResult> GetLedger(Guid variantId, [FromQuery] ProductLedgerFilter filter)
    {
        var result = await _svc.GetLedgerAsync(variantId, filter);
        return Ok(ApiResponse<PaginatedResponse<ProductLedgerEntryModel>>.Ok(result));
    }

    /// <summary>Total purchased, total sold and its cost, current stock value and weighted-average cost.</summary>
    [HttpGet("{variantId:guid}/summary")]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    public async Task<IActionResult> GetSummary(Guid variantId)
    {
        var result = await _svc.GetSummaryAsync(variantId);
        return Ok(ApiResponse<ProductLedgerSummaryModel>.Ok(result));
    }
}

/// <summary>Revenue against cost of goods sold, by product. Lives under <c>api/reports</c> where reports are found.</summary>
[ApiController]
[Route("api/reports")]
[RequiresFeature("MODULE_FINANCE")]
public class ProductProfitabilityController : ControllerBase
{
    private readonly IProductLedgerQueryService _svc;

    public ProductProfitabilityController(IProductLedgerQueryService svc) => _svc = svc;

    [HttpGet("product-profitability")]
    [RequirePermission(PermissionCodes.PRODUCT_LEDGER_VIEW)]
    public async Task<IActionResult> GetProductProfitability([FromQuery] ProductProfitabilityFilter filter)
    {
        var result = await _svc.GetProfitabilityAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<ProductProfitabilityItemModel>>.Ok(result));
    }
}
