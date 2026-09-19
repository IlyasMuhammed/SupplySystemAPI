using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Controllers;

// P1-05 (Addendum 29 §1.6/§1.7). The new partner-type-aware surface — create/update the flags,
// list/filter by type, soft delete — backed by IBusinessPartnerService (P1-04). This does NOT
// replace SuppliersController: that one keeps owning the full vendor onboarding workflow (status
// state machine, contacts, bank details, documents), unchanged, per §1.7's backward-compatibility
// requirement. See the task notes for why /api/suppliers itself is not re-routed here.
[ApiController]
[Route("api/partners")]
[RequiresFeature("MODULE_SUPPLIERS")]
public class PartnersController : ControllerBase
{
    private readonly IBusinessPartnerService _service;

    public PartnersController(IBusinessPartnerService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> GetPartners(
        [FromQuery] string? type,
        [FromQuery] bool? isVendor,
        [FromQuery] bool? isCustomer,
        [FromQuery] bool? isCarrier,
        [FromQuery] bool? isServiceProvider,
        [FromQuery] bool? active,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var filter = new BusinessPartnerFilter
        {
            Type = type,
            IsVendor = isVendor,
            IsCustomer = isCustomer,
            IsCarrier = isCarrier,
            IsServiceProvider = isServiceProvider,
            Active = active,
            Search = search,
            Page = page,
            PageSize = pageSize
        };
        var result = await _service.GetAllAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<BusinessPartnerModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetPartnerById(Guid uuid)
    {
        var partner = await _service.GetByIdAsync(uuid);
        return partner is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<BusinessPartnerModel>.Ok(partner));
    }

    [HttpPost]
    public async Task<IActionResult> CreatePartner([FromBody] BusinessPartnerModel model)
    {
        var userId = User.GetUserId();
        var uuid = await _service.CreateAsync(model, userId);
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("{uuid:guid}")]
    public async Task<IActionResult> UpdatePartner(Guid uuid, [FromBody] BusinessPartnerModel model)
    {
        var userId = User.GetUserId();
        await _service.UpdateAsync(uuid, model, userId);
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpDelete("{uuid:guid}")]
    public async Task<IActionResult> DeletePartner(Guid uuid)
    {
        var userId = User.GetUserId();
        await _service.DeleteAsync(uuid, userId);
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }
}

// ── Backward-compat aliases (§1.6/§1.7) ─────────────────────────────────────────
//
// /api/suppliers is deliberately NOT aliased here. It already exists — SuppliersController — and
// already IS the vendor-only surface these aliases are meant to provide; re-routing it to
// PartnersController would shadow that controller's full CRUD/workflow (status state machine,
// contacts, bank details, documents, approve/reject/blacklist/suspend), which nothing in this task
// asks to remove, and ASP.NET Core route registration order for two controllers both claiming
// "api/suppliers" is exactly the kind of thing that breaks silently. What DOES need to be true —
// that /api/suppliers returns only is_vendor=1 rows now that BusinessPartners can hold pure
// customers/carriers/service-providers too — is fixed at the query level instead: see the
// IsVendor filter added to SuppliersRepository.GetSuppliersAsync in this same task. The three
// aliases below are genuinely new routes, so they raise no such conflict.

[ApiController]
[Route("api/customers")]
[RequiresFeature("MODULE_SUPPLIERS")]
public class CustomersAliasController : ControllerBase
{
    private readonly IBusinessPartnerService _service;
    public CustomersAliasController(IBusinessPartnerService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> GetCustomers() =>
        Ok(ApiResponse<List<BusinessPartnerModel>>.Ok(await _service.GetCustomersAsync()));
}

[ApiController]
[Route("api/carriers")]
[RequiresFeature("MODULE_SUPPLIERS")]
public class CarriersAliasController : ControllerBase
{
    private readonly IBusinessPartnerService _service;
    public CarriersAliasController(IBusinessPartnerService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> GetCarriers() =>
        Ok(ApiResponse<List<BusinessPartnerModel>>.Ok(await _service.GetCarriersAsync()));
}

[ApiController]
[Route("api/service-providers")]
[RequiresFeature("MODULE_SUPPLIERS")]
public class ServiceProvidersAliasController : ControllerBase
{
    private readonly IBusinessPartnerService _service;
    public ServiceProvidersAliasController(IBusinessPartnerService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> GetServiceProviders() =>
        Ok(ApiResponse<List<BusinessPartnerModel>>.Ok(await _service.GetServiceProvidersAsync()));
}
