using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// A customer's shipping addresses (A29 §7.7), so a sale order has a <c>logistics.addresses</c> row to point at.
/// </summary>
/// <remarks>
/// Gated as sale-order work rather than delivery work, because it is what the order form needs: reading is
/// SALE_ORDER_VIEW and saving one is SALE_ORDER_CREATE. An address is a snapshot; there is no edit and no delete.
/// </remarks>
[ApiController]
[Route("api/addresses")]
[RequiresFeature("MODULE_LOGISTICS")]
public class AddressesController : ControllerBase
{
    private readonly IAddressBookService _svc;

    public AddressesController(IAddressBookService svc) => _svc = svc;

    /// <summary>Saves an address for the customer named in <c>consigneeUuid</c>.</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.SALE_ORDER_CREATE)]
    public async Task<IActionResult> Create([FromBody] AddressRequest req)
    {
        var address = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<AddressModel>.Ok(address, StaticResponseMessage.recordCreatedSuccessfully));
    }

    /// <summary>One address.</summary>
    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var address = await _svc.GetAsync(uuid);
        return address is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<AddressModel>.Ok(address));
    }

    /// <summary>The customer's addresses, newest first, each place once. <c>consigneeUuid</c> is the customer's UUID.</summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.SALE_ORDER_VIEW)]
    public async Task<IActionResult> List([FromQuery] Guid consigneeUuid)
    {
        if (consigneeUuid == Guid.Empty)
            return BadRequest(ApiResponse.Fail("Name the customer: consigneeUuid is required."));

        return Ok(ApiResponse<IReadOnlyList<AddressModel>>.Ok(await _svc.ListForConsigneeAsync(consigneeUuid)));
    }
}
