using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

/// <summary>A30 §28.9, §19A.7 — a read of what Material Issue, Finished Goods Receipt and Quality Inspection already recorded, not a stored ledger.</summary>
[ApiController]
[Route("api")]
[RequiresFeature("MODULE_MANUFACTURING")]
public class ProductionLedgerController : ControllerBase
{
    private readonly IProductionLedgerService _service;

    public ProductionLedgerController(IProductionLedgerService service) => _service = service;

    [HttpGet("production-orders/{uuid:guid}/ledger")]
    [RequirePermission(PermissionCodes.PROD_VIEW)]
    public async Task<IActionResult> GetForOrder(Guid uuid)
    {
        var ledger = await _service.GetForOrderAsync(uuid) ?? throw new NotFoundException("Production order", uuid);
        return Ok(ApiResponse<ProductionLedgerModel>.Ok(ledger));
    }

    [HttpGet("production-ledger")]
    [RequirePermission(PermissionCodes.PROD_LEDGER_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] ProductionLedgerListFilter filter) =>
        Ok(ApiResponse<PaginatedResponse<ProductionLedgerEntryModel>>.Ok(await _service.GetListAsync(filter)));

    [HttpGet("production-ledger/summary")]
    [RequirePermission(PermissionCodes.PROD_LEDGER_VIEW)]
    public async Task<IActionResult> GetSummary([FromQuery] ProductionLedgerListFilter filter) =>
        Ok(ApiResponse<ProductionLedgerSummaryModel>.Ok(await _service.GetSummaryAsync(filter)));
}
