using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Controllers;

// FSD Addendum 26 (PV-002) — dynamic attribute catalog, category linking, and per-variant values.
[ApiController]
//[Authorize]
[RequiresFeature("MODULE_INVENTORY")]
public class AttributesController : ControllerBase
{
    private readonly IInventoryService _service;
    public AttributesController(IInventoryService service) => _service = service;

    // ── Attribute Definitions ────────────────────────────────────────────────────

    [HttpGet("api/attributes")]
    public async Task<IActionResult> GetAttributes()
    {
        var list = await _service.GetAttributesAsync();
        return Ok(ApiResponse<List<AttributeDefinitionModel>>.Ok(list));
    }

    [HttpPost("api/attributes")]
    public async Task<IActionResult> CreateAttribute([FromBody] CreateAttributeDefinitionRequest req)
    {
        try
        {
            var uuid = await _service.CreateAttributeAsync(req);
            return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
        }
        catch (ConflictException ex) { return Conflict(ApiResponse.Fail(ex.Message)); }
        catch (BadRequestException ex) { return BadRequest(ApiResponse.Fail(ex.Message)); }
    }

    [HttpPut("api/attributes/{uuid:guid}")]
    public async Task<IActionResult> UpdateAttribute(Guid uuid, [FromBody] UpdateAttributeDefinitionRequest req)
    {
        try
        {
            var updated = await _service.UpdateAttributeAsync(uuid, req);
            if (!updated) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
            return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
        }
        catch (BadRequestException ex) { return BadRequest(ApiResponse.Fail(ex.Message)); }
    }

    [HttpDelete("api/attributes/{uuid:guid}")]
    public async Task<IActionResult> DeleteAttribute(Guid uuid)
    {
        var result = await _service.DeleteAttributeAsync(uuid);
        if (!result.Deleted && result.ReferencedCategoryCount == 0 && result.ReferencedVariantValueCount == 0)
            return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        if (!result.Deleted)
            return Conflict(ApiResponse.Fail("Attribute is in use and cannot be deleted.", result));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }

    // ── Category Attributes ──────────────────────────────────────────────────────

    [HttpGet("api/categories/{categoryId:int}/attributes")]
    public async Task<IActionResult> GetCategoryAttributes(int categoryId)
    {
        var list = await _service.GetCategoryAttributesAsync(categoryId);
        return Ok(ApiResponse<List<CategoryAttributeModel>>.Ok(list));
    }

    [HttpPost("api/categories/{categoryId:int}/attributes")]
    public async Task<IActionResult> LinkCategoryAttribute(int categoryId, [FromBody] CreateCategoryAttributeRequest req)
    {
        try
        {
            await _service.LinkCategoryAttributeAsync(categoryId, req);
            return Ok(ApiResponse.Ok(StaticResponseMessage.recordCreatedSuccessfully));
        }
        catch (NotFoundException ex) { return NotFound(ApiResponse.Fail(ex.Message)); }
        catch (ConflictException ex) { return Conflict(ApiResponse.Fail(ex.Message)); }
    }

    [HttpDelete("api/categories/{categoryId:int}/attributes/{attributeUuid:guid}")]
    public async Task<IActionResult> UnlinkCategoryAttribute(int categoryId, Guid attributeUuid)
    {
        var removed = await _service.UnlinkCategoryAttributeAsync(categoryId, attributeUuid);
        if (!removed) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }

    [HttpPut("api/categories/{categoryId:int}/attributes")]
    public async Task<IActionResult> SetCategoryAttributes(int categoryId, [FromBody] SetCategoryAttributesRequest req)
    {
        try
        {
            var result = await _service.SetCategoryAttributesAsync(categoryId, req);
            return Ok(ApiResponse<List<CategoryAttributeModel>>.Ok(result, StaticResponseMessage.recordUpdatedSuccessfully));
        }
        catch (NotFoundException ex) { return NotFound(ApiResponse.Fail(ex.Message)); }
    }

    // ── Variant Attribute Values ─────────────────────────────────────────────────

    [HttpGet("api/variants/{variantUuid:guid}/attributes")]
    public async Task<IActionResult> GetVariantAttributeValues(Guid variantUuid)
    {
        var list = await _service.GetVariantAttributeValuesAsync(variantUuid);
        return Ok(ApiResponse<List<VariantAttributeValueModel>>.Ok(list));
    }

    [HttpPut("api/variants/{variantUuid:guid}/attributes")]
    public async Task<IActionResult> SetVariantAttributeValues(Guid variantUuid, [FromBody] SetVariantAttributeValuesRequest req)
    {
        try
        {
            var updated = await _service.SetVariantAttributeValuesAsync(variantUuid, req);
            if (!updated) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
            return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
        }
        catch (NotFoundException ex) { return NotFound(ApiResponse.Fail(ex.Message)); }
        catch (BadRequestException ex) { return BadRequest(ApiResponse.Fail(ex.Message)); }
    }
}
