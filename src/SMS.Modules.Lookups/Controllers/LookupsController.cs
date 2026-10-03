using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Lookups.Controllers;

[ApiController]
[Route("api/lookups")]
public class LookupsController : ControllerBase
{
    private readonly ILookupsService _service;
    private readonly IEnumerable<ILookupReferenceChecker> _checkers;

    public LookupsController(ILookupsService service, IEnumerable<ILookupReferenceChecker> checkers)
    {
        _service = service;
        _checkers = checkers;
    }

    // ── Existing general-purpose endpoints ───────────────────────────────────
    // Reads are open to any authenticated user (no permission attribute) — this is reference data
    // (countries/cities/currencies/payment terms/lookup values) that ordinary business forms
    // elsewhere in the app (Suppliers, Warehouses, ...) need to populate their dropdowns, not an
    // admin-only concern. Only mutations are permission-gated.

    [HttpGet("all")]
    public IActionResult GetAll() => Ok(ApiResponse<DropDownLookupResponse>.Ok(_service.GetAllDropdowns()));

    [HttpGet("cities")]
    public IActionResult GetCities() => Ok(ApiResponse<object>.Ok(_service.GetCities()));

    [HttpGet("cities/by-country/{countryId:guid}")]
    public IActionResult GetCitiesByCountry(Guid countryId) =>
        Ok(ApiResponse<object>.Ok(_service.GetCitiesByCountry(countryId)));

    [HttpGet("countries")]
    public IActionResult GetCountries() => Ok(ApiResponse<object>.Ok(_service.GetCountries()));

    [HttpGet("currencies")]
    public IActionResult GetCurrencies() => Ok(ApiResponse<object>.Ok(_service.GetCurrencies()));

    [HttpGet("payment-terms")]
    public IActionResult GetPaymentTerms() => Ok(ApiResponse<object>.Ok(_service.GetPaymentTerms()));

    [HttpGet("delivery-terms")]
    public IActionResult GetDeliveryTerms() => Ok(ApiResponse<object>.Ok(_service.GetDeliveryTerms()));

    [HttpGet("lookup-types")]
    public IActionResult GetLookupTypes() => Ok(ApiResponse<object>.Ok(_service.GetLookupTypes()));

    // Adding a new Country/City only appends to the shared catalog — lower risk than editing or
    // removing an existing entry another org may already depend on, so it gets its own narrower
    // permission instead of the general SYSTEM_CONFIGURE gate below.
    [HttpPost("cities")]
    [RequirePermission(PermissionCodes.LOCATION_MANAGE)]
    public IActionResult CreateCity([FromBody] CreateCityRequest req) =>
        Ok(ApiResponse<Guid>.Ok(_service.CreateCity(req), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPost("countries")]
    [RequirePermission(PermissionCodes.LOCATION_MANAGE)]
    public IActionResult CreateCountry([FromBody] CreateCountryRequest req) =>
        Ok(ApiResponse<Guid>.Ok(_service.CreateCountry(req), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPost("currencies")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult CreateCurrency([FromBody] CreateCurrencyRequest req) =>
        Ok(ApiResponse<Guid>.Ok(_service.CreateCurrency(req), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPost("payment-terms")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult CreatePaymentTerms([FromBody] CreatePaymentTermRequest req) =>
        Ok(ApiResponse<Guid>.Ok(_service.CreatePaymentTerms(req), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPost("delivery-terms")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult CreateDeliveryTerms([FromBody] CreateDeliveryTermRequest req) =>
        Ok(ApiResponse<Guid>.Ok(_service.CreateDeliveryTerms(req), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPost("lookup-types")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult CreateLookupType([FromBody] CreateLookupTypeRequest req) =>
        Ok(ApiResponse<Guid>.Ok(_service.CreateLookupType(req), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPost("lookup-values")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult CreateLookupValue([FromBody] CreateLookupValueRequest req) =>
        Ok(ApiResponse<Guid>.Ok(_service.CreateLookupValue(req), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPut("cities/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult UpdateCity(Guid id, [FromBody] CreateCityRequest req) =>
        _service.UpdateCity(id, req)
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpPut("countries/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult UpdateCountry(Guid id, [FromBody] CreateCountryRequest req) =>
        _service.UpdateCountry(id, req)
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpPut("currencies/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult UpdateCurrency(Guid id, [FromBody] CreateCurrencyRequest req) =>
        _service.UpdateCurrency(id, req, _checkers)
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpPut("payment-terms/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult UpdatePaymentTerm(Guid id, [FromBody] CreatePaymentTermRequest req) =>
        _service.UpdatePaymentTerm(id, req)
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpPut("delivery-terms/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult UpdateDeliveryTerm(Guid id, [FromBody] CreateDeliveryTermRequest req) =>
        _service.UpdateDeliveryTerm(id, req)
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpDelete("cities/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult DeleteCity(Guid id) =>
        _service.DeleteCity(id) ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully)) : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpDelete("countries/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult DeleteCountry(Guid id) =>
        _service.DeleteCountry(id) ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully)) : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpDelete("currencies/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult DeleteCurrency(Guid id) =>
        _service.DeleteCurrency(id, _checkers) ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully)) : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpDelete("payment-terms/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult DeletePaymentTerm(Guid id) =>
        _service.DeletePaymentTerm(id) ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully)) : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpDelete("delivery-terms/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult DeleteDeliveryTerm(Guid id) =>
        _service.DeleteDeliveryTerm(id) ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully)) : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpDelete("lookup-values/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult DeleteLookupValue(Guid id) =>
        _service.DeleteLookupValue(id) ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully)) : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpDelete("lookup-types/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public IActionResult DeleteLookupType(Guid id) =>
        _service.DeleteLookupType(id) ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully)) : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    // ── Slug-based Admin CRUD ─────────────────────────────────────────────────

    [HttpGet("{type}")]
    public async Task<IActionResult> GetByType(string type)
    {
        var values = await _service.GetValuesByTypeAsync(type);
        return Ok(ApiResponse<List<LookupValueModel>>.Ok(values));
    }

    [HttpPost("{type}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> CreateByType(string type, [FromBody] CreateLookupValueByTypeRequest req)
    {
        var id = await _service.CreateValueByTypeAsync(type, req);
        return Ok(ApiResponse<Guid>.Ok(id, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPatch("{type}/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> PatchByType(string type, Guid id, [FromBody] PatchLookupValueRequest req)
    {
        var updated = await _service.PatchValueAsync(type, id, req);
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpDelete("{type}/{id:guid}")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> DeleteByType(string type, Guid id)
    {
        var deleted = await _service.SoftDeleteValueAsync(type, id, _checkers);
        return deleted
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}
