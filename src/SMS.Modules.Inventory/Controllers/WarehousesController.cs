using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Controllers;

// Never [AllowAnonymous]: with no signed-in user the tenant filter is bypassed (TenantContext treats an
// anonymous request as unscoped), so an anonymous call would read and change every organization's warehouses.
[ApiController]
[Authorize]
[RequiresFeature("MODULE_INVENTORY")]
public class WarehousesController : ControllerBase
{
    private readonly IInventoryService _service;
    public WarehousesController(IInventoryService service) => _service = service;

    [HttpGet("api/warehouses")]
  //  [RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetWarehouses()
    {
        var list = await _service.GetWarehousesAsync();
        return Ok(ApiResponse<List<WarehouseModel>>.Ok(list));
    }

    [HttpPost("api/warehouses")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> CreateWarehouse([FromBody] CreateWarehouseRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(ApiResponse.Fail("Warehouse name and code are required."));
        var id = await _service.CreateWarehouseAsync(req, User.GetUserId());
        return Ok(ApiResponse<int>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPatch("api/warehouses/{id:int}")]
    public async Task<IActionResult> UpdateWarehouse(int id, [FromBody] PatchWarehouseRequest req)
    {
        var updated = await _service.UpdateWarehouseAsync(id, req);
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpDelete("api/warehouses/{id:int}")]
    public async Task<IActionResult> DeleteWarehouse(int id)
    {
        var deleted = await _service.DeleteWarehouseAsync(id);
        return deleted
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    // ── Structure ─────────────────────────────────────────────────────────────

    [HttpGet("api/warehouses/{warehouseId:int}/structure")]
    public async Task<IActionResult> GetWarehouseStructure(int warehouseId)
    {
        var result = await _service.GetWarehouseStructureAsync(warehouseId);
        return Ok(ApiResponse<WarehouseStructureModel>.Ok(result));
    }

    // ── Zones ─────────────────────────────────────────────────────────────────

    [HttpPost("api/warehouses/{warehouseId:int}/zones")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> CreateZone(int warehouseId, [FromBody] CreateZoneRequest req)
    {
        var id = await _service.CreateZoneAsync(warehouseId, req);
        return Ok(ApiResponse<int>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("api/zones/{id:int}")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> UpdateZone(int id, [FromBody] UpdateZoneRequest req)
    {
        var ok = await _service.UpdateZoneAsync(id, req);
        return ok
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPatch("api/zones/{id:int}/deactivate")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> DeactivateZone(int id)
    {
        var r = await _service.DeactivateZoneAsync(id);
        if (!r.Found) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        if (!r.Succeeded)
            return Conflict(ApiResponse.Fail(
                $"Cannot deactivate: {r.ChildCount} active {r.ChildType} must be deactivated first.",
                new StructureConflictResult { ChildCount = r.ChildCount, ChildType = r.ChildType }));
        return Ok(ApiResponse.Ok("Zone deactivated."));
    }

    // ── Racks ─────────────────────────────────────────────────────────────────

    [HttpPost("api/zones/{zoneId:int}/racks")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> CreateRack(int zoneId, [FromBody] CreateRackRequest req)
    {
        var id = await _service.CreateRackAsync(zoneId, req);
        return Ok(ApiResponse<int>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("api/racks/{id:int}")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> UpdateRack(int id, [FromBody] UpdateRackRequest req)
    {
        var ok = await _service.UpdateRackAsync(id, req);
        return ok
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPatch("api/racks/{id:int}/deactivate")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> DeactivateRack(int id)
    {
        var r = await _service.DeactivateRackAsync(id);
        if (!r.Found) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        if (!r.Succeeded)
            return Conflict(ApiResponse.Fail(
                $"Cannot deactivate: {r.ChildCount} active {r.ChildType} must be deactivated first.",
                new StructureConflictResult { ChildCount = r.ChildCount, ChildType = r.ChildType }));
        return Ok(ApiResponse.Ok("Rack deactivated."));
    }

    // ── Shelves ───────────────────────────────────────────────────────────────

    [HttpPost("api/racks/{rackId:int}/shelves")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> CreateShelf(int rackId, [FromBody] CreateShelfRequest req)
    {
        var id = await _service.CreateShelfAsync(rackId, req);
        return Ok(ApiResponse<int>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("api/shelves/{id:int}")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> UpdateShelf(int id, [FromBody] UpdateShelfRequest req)
    {
        var ok = await _service.UpdateShelfAsync(id, req);
        return ok
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPatch("api/shelves/{id:int}/deactivate")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> DeactivateShelf(int id)
    {
        var r = await _service.DeactivateShelfAsync(id);
        if (!r.Found) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        if (!r.Succeeded)
            return Conflict(ApiResponse.Fail(
                $"Cannot deactivate: {r.ChildCount} active {r.ChildType} must be deactivated first.",
                new StructureConflictResult { ChildCount = r.ChildCount, ChildType = r.ChildType }));
        return Ok(ApiResponse.Ok("Shelf deactivated."));
    }

    // ── Bins ──────────────────────────────────────────────────────────────────

    [HttpPost("api/warehouses/zones/{zoneId:int}/bins")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> CreateBin(int zoneId, [FromBody] CreateBinRequest req)
    {
        var id = await _service.CreateBinAsync(zoneId, req);
        return Ok(ApiResponse<int>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPost("api/shelves/{shelfId:int}/bins")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> CreateStructuredBin(int shelfId, [FromBody] CreateBinRequest req)
    {
        var id = await _service.CreateStructuredBinAsync(shelfId, req);
        return Ok(ApiResponse<int>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("api/bins/{id:int}")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> UpdateBin(int id, [FromBody] UpdateBinRequest req)
    {
        var ok = await _service.UpdateBinAsync(id, req);
        return ok
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPatch("api/bins/{id:int}/deactivate")]
    //[RequirePermission(PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> DeactivateBin(int id)
    {
        var r = await _service.DeactivateBinAsync(id);
        if (!r.Found) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse.Ok("Bin deactivated."));
    }

    [HttpGet("api/warehouses/{warehouseId:int}/stock")]
    //[RequirePermission(PermissionCodes.INVENTORY_VIEW)]
    public async Task<IActionResult> GetWarehouseStock(
        int warehouseId,
        [FromQuery] int? categoryId,
        [FromQuery] bool belowReorderOnly = false,
        [FromQuery] bool includeZeroStock = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var filter = new StockLevelFilter
        {
            CategoryId = categoryId,
            BelowReorderOnly = belowReorderOnly,
            IncludeZeroStock = includeZeroStock,
            Page = page,
            PageSize = pageSize
        };
        var result = await _service.GetWarehouseStockAsync(warehouseId, filter);
        return Ok(ApiResponse<PaginatedResponse<StockLevelModel>>.Ok(result));
    }
}
