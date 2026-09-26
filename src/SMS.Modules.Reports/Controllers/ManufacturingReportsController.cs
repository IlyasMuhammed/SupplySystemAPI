using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Reports.Controllers;

/// <summary>
/// A30-P5-02..06 §31. Gated the same way <see cref="ProductLedgerReportsController"/> gates the sales
/// side's own cost-sensitive reports: REPORT_VIEW for being a report at all, plus the domain
/// permission that already gates the same numbers everywhere else in this module — PROD_LEDGER_VIEW,
/// the "narrower cross-order and summary view" A30-P4 already defined for exactly this shape of read.
/// No PDF/Excel export on any of these (unlike the sales reports' own precedent) — an explicit scope
/// cut alongside not building the other twenty-three reports FSD §31 lists; see the task register's
/// own DONE note for A30-P5-02..06.
/// </summary>
[ApiController]
[Route("api/reports/manufacturing")]
[RequiresFeature("MODULE_MANUFACTURING")]
[RequirePermission(PermissionCodes.REPORT_VIEW)]
[RequirePermission(PermissionCodes.PROD_LEDGER_VIEW)]
public class ManufacturingReportsController : ControllerBase
{
    private readonly IManufacturingReportService _svc;

    public ManufacturingReportsController(IManufacturingReportService svc) => _svc = svc;

    /// <summary>R4/R5/R16 — planned/produced/accepted/rejected totals, yield, on-time completion and average cycle time across production orders in range.</summary>
    [HttpGet("production-efficiency")]
    public async Task<IActionResult> GetProductionEfficiency([FromQuery] ManufacturingReportFilter filter) =>
        Ok(ApiResponse<ProductionEfficiencyReport>.Ok(await _svc.GetProductionEfficiencyAsync(filter)));

    /// <summary>R6/R7 — quality inspection outcomes and rejection rates across orders in range, broken down by output product.</summary>
    [HttpGet("quality-scrap")]
    public async Task<IActionResult> GetQualityScrap([FromQuery] ManufacturingReportFilter filter) =>
        Ok(ApiResponse<QualityScrapReport>.Ok(await _svc.GetQualityScrapAsync(filter)));

    /// <summary>R14 — every confirmed production material issue, one row per issue (not per line), across production orders.</summary>
    [HttpGet("material-issues")]
    public async Task<IActionResult> GetMaterialIssueRegister([FromQuery] ManufacturingRegisterFilter filter) =>
        Ok(ApiResponse<PaginatedResponse<ProductionMaterialIssueRegisterItem>>.Ok(await _svc.GetMaterialIssueRegisterAsync(filter)));

    /// <summary>R15 — every finished goods receipt across production orders.</summary>
    [HttpGet("finished-goods-receipts")]
    public async Task<IActionResult> GetFinishedGoodsReceiptRegister([FromQuery] ManufacturingRegisterFilter filter) =>
        Ok(ApiResponse<PaginatedResponse<FinishedGoodsReceiptRegisterItem>>.Ok(await _svc.GetFinishedGoodsReceiptRegisterAsync(filter)));

    /// <summary>R24 — production orders whose own AcceptedQuantity, material issues or quality inspection do not tie out to what the underlying documents actually recorded.</summary>
    [HttpGet("ledger-reconciliation")]
    public async Task<IActionResult> GetLedgerReconciliation([FromQuery] ManufacturingReportFilter filter) =>
        Ok(ApiResponse<LedgerReconciliationReport>.Ok(await _svc.GetLedgerReconciliationAsync(filter)));

    /// <summary>R26/R27/R28 — the dependency tree for one production order's whole chain (every ancestor and descendant raised for a shortage), each node's status and cycle time.</summary>
    [HttpGet("chained/{productionOrderUuid:guid}")]
    public async Task<IActionResult> GetChainedManufacturing(Guid productionOrderUuid)
    {
        var result = await _svc.GetChainedManufacturingAsync(productionOrderUuid);
        return result is null ? NotFound() : Ok(ApiResponse<ChainedManufacturingReport>.Ok(result));
    }
}
