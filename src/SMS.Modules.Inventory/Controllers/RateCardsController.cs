using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Inventory.Services.Exports;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Controllers;

// RC-001 (FSD Addendum 28) — Supplier Rate Card Management.
[ApiController]
[Route("api/rate-cards")]
[RequiresFeature("MODULE_INVENTORY")]
[RequirePermission(PermissionCodes.SUPPLIER_MANAGE)]
public class RateCardsController : ControllerBase
{
    private readonly IVariantSupplierService _svc;
    private readonly IVariantSupplierResolver _resolver;

    public RateCardsController(IVariantSupplierService svc, IVariantSupplierResolver resolver)
    {
        _svc       = svc;
        _resolver  = resolver;
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateVariantSupplierRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    // RC-002's inline-edit grid calls this as PUT; RC-001 originally wired it as PATCH — both map
    // to the same action so neither caller needs to change.
    [HttpPatch("{uuid:guid}")]
    [HttpPut("{uuid:guid}")]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateVariantSupplierRequest req)
    {
        var updated = await _svc.UpdateAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] RateCardListFilter filter)
    {
        var result = await _svc.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<RateCardGridRowModel>>.Ok(result));
    }

    [HttpPatch("{uuid:guid}/preferred")]
    public async Task<IActionResult> SetPreferred(Guid uuid)
    {
        var updated = await _svc.SetPreferredAsync(uuid, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var result = await _svc.GetByIdAsync(uuid);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<VariantSupplierModel>.Ok(result));
    }

    [HttpGet("{uuid:guid}/history")]
    public async Task<IActionResult> GetHistory(Guid uuid, [FromQuery] RateHistoryFilter filter)
    {
        var result = await _svc.GetHistoryAsync(uuid, filter);
        return Ok(ApiResponse<PaginatedResponse<SupplierRateHistoryModel>>.Ok(result));
    }

    // RC-007 — mark-as-reviewed. Does NOT change the rate; clears the stale flag only.
    [HttpPost("{uuid:guid}/mark-reviewed")]
    public async Task<IActionResult> MarkReviewed(Guid uuid)
    {
        var updated = await _svc.MarkReviewedAsync(uuid, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok("Marked as reviewed."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpGet("active-rate")]
    public async Task<IActionResult> GetActiveRate(
        [FromQuery] Guid variantUuid, [FromQuery] Guid supplierUuid, [FromQuery] DateOnly? asOfDate)
    {
        var result = await _resolver.GetActiveRateAsync(
            variantUuid, supplierUuid, asOfDate ?? DateOnly.FromDateTime(DateTime.UtcNow));
        return Ok(ApiResponse<ActiveRateInfo?>.Ok(result));
    }

    // RC-003 — Product Comparison View.
    [HttpGet("compare")]
    public async Task<IActionResult> GetComparison([FromQuery] Guid variantUuid)
    {
        var result = await _svc.GetComparisonAsync(variantUuid);
        return Ok(ApiResponse<List<RateComparisonRowModel>>.Ok(result));
    }

    [HttpGet("compare/export")]
    public async Task<IActionResult> ExportComparison([FromQuery] Guid variantUuid)
    {
        var rows  = await _svc.GetComparisonAsync(variantUuid);
        var bytes = RateComparisonExcelExporter.Export(rows);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"RateComparison-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx");
    }

    // RC-005 — Bulk Rate Adjustment.
    [HttpPost("bulk-adjust/preview")]
    public async Task<IActionResult> PreviewBulkAdjust([FromBody] BulkAdjustRequest req)
    {
        var result = await _svc.PreviewBulkAdjustAsync(req);
        return Ok(ApiResponse<List<BulkAdjustPreviewRowModel>>.Ok(result));
    }

    [HttpPost("bulk-adjust/confirm")]
    public async Task<IActionResult> ConfirmBulkAdjust([FromBody] BulkAdjustRequest req)
    {
        var result = await _svc.ConfirmBulkAdjustAsync(req, User.GetUserId());
        return Ok(ApiResponse<BulkAdjustConfirmResult>.Ok(result, StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPost("bulk-adjust/undo/{bulkOperationId:guid}")]
    public async Task<IActionResult> UndoBulkAdjust(Guid bulkOperationId)
    {
        await _svc.UndoBulkAdjustAsync(bulkOperationId, User.GetUserId());
        return Ok(ApiResponse.Ok("Bulk adjustment undone."));
    }

    [HttpGet("bulk-adjust/recent")]
    public async Task<IActionResult> GetRecentBulkOperations()
    {
        var result = await _svc.GetRecentBulkOperationsAsync();
        return Ok(ApiResponse<List<BulkRateOperationModel>>.Ok(result));
    }

    // ── RC-006: Excel import/export ──────────────────────────────────────────

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] Guid supplierId)
    {
        var rows  = await _svc.GetExportRowsAsync(supplierId);
        var bytes = RateCardExcelExporter.Export(rows);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"RateCard-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx");
    }

    private const long MaxImportFileBytes = 20 * 1024 * 1024; // 20 MB, matches SuppliersController.UploadDocument.

    [HttpPost("import/preview")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> PreviewImport(IFormFile file, [FromForm] Guid supplierId)
    {
        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse.Fail("No file was provided."));
        if (file.Length > MaxImportFileBytes)
            return BadRequest(ApiResponse.Fail("File size must not exceed 20 MB."));

        await using var stream = file.OpenReadStream();
        var result = await _svc.PreviewImportAsync(stream, supplierId);
        return Ok(ApiResponse<List<ImportPreviewRowModel>>.Ok(result));
    }

    [HttpPost("import/confirm")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> ConfirmImport(IFormFile file, [FromForm] Guid supplierId)
    {
        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse.Fail("No file was provided."));
        if (file.Length > MaxImportFileBytes)
            return BadRequest(ApiResponse.Fail("File size must not exceed 20 MB."));

        await using var stream = file.OpenReadStream();
        var result = await _svc.ConfirmImportAsync(stream, supplierId, User.GetUserId());
        return Ok(ApiResponse<ImportConfirmResult>.Ok(result, StaticResponseMessage.recordUpdatedSuccessfully));
    }

    // ── RC-006: Copy rates between suppliers ─────────────────────────────────

    [HttpPost("copy/preview")]
    public async Task<IActionResult> PreviewCopy([FromBody] CopyRatesRequest req)
    {
        var result = await _svc.PreviewCopyAsync(req);
        return Ok(ApiResponse<List<CopyPreviewRowModel>>.Ok(result));
    }

    [HttpPost("copy")]
    public async Task<IActionResult> ConfirmCopy([FromBody] CopyRatesRequest req)
    {
        var result = await _svc.ConfirmCopyAsync(req, User.GetUserId());
        return Ok(ApiResponse<CopyConfirmResult>.Ok(result, StaticResponseMessage.recordCreatedSuccessfully));
    }
}
