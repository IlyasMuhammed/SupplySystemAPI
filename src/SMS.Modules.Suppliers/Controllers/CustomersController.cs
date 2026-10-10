using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Controllers;

/// <summary>
/// A37 D-13 — the customer master (docs/module-registry/API-CONTRACT.md §5), a facade over suppliers.BusinessPartners
/// with IsCustomer: the same rows sale orders, invoices, receivables and QuickBooks already use.
/// </summary>
[ApiController]
[Route("api/customers")]
[RequiresFeature(ModuleCodes.Customers)]
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _service;
    public CustomersController(ICustomerService service) => _service = service;

    [HttpGet]
    [RequirePermission(PermissionCodes.CUSTOMER_VIEW)]
    public async Task<IActionResult> GetCustomers([FromQuery] string? search, [FromQuery] string? type, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, [FromQuery] string? sortField = null, [FromQuery] string? sortOrder = null,
        CancellationToken ct = default)
    {
        var filter = new CustomerListFilter
        {
            Search = search, Type = type, Status = status, Page = page, PageSize = pageSize, SortField = sortField, SortOrder = sortOrder
        };
        return Ok(ApiResponse<PaginatedResponse<CustomerListItem>>.Ok(await _service.ListAsync(filter, ct)));
    }

    [HttpGet("search")]
    [RequirePermission(PermissionCodes.CUSTOMER_VIEW)]
    public async Task<IActionResult> Search([FromQuery] string? q, [FromQuery] int limit = 10, CancellationToken ct = default) =>
        Ok(ApiResponse<List<CustomerListItem>>.Ok(await _service.SearchAsync(q, limit, ct)));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.CUSTOMER_VIEW)]
    public async Task<IActionResult> GetCustomer(Guid uuid, CancellationToken ct = default) =>
        await _service.GetAsync(uuid, ct) is { } customer
            ? Ok(ApiResponse<CustomerDetail>.Ok(customer))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpGet("{uuid:guid}/balance")]
    [RequirePermission(PermissionCodes.CUSTOMER_VIEW)]
    public async Task<IActionResult> GetBalance(Guid uuid, CancellationToken ct = default) =>
        await _service.GetBalanceAsync(uuid, ct) is { } balance
            ? Ok(ApiResponse<CustomerBalanceModel>.Ok(balance))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [HttpPost]
    [RequirePermission(PermissionCodes.CUSTOMER_CREATE)]
    public async Task<IActionResult> CreateCustomer([FromBody] CustomerUpsert request, CancellationToken ct = default) =>
        Ok(ApiResponse<Guid>.Ok(await _service.CreateAsync(request, User.GetUserId(), ct), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.CUSTOMER_EDIT)]
    public async Task<IActionResult> UpdateCustomer(Guid uuid, [FromBody] CustomerUpsert request, CancellationToken ct = default)
    {
        await _service.UpdateAsync(uuid, request, User.GetUserId(), ct);
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    [HttpPatch("{uuid:guid}/status")]
    [RequirePermission(PermissionCodes.CUSTOMER_DEACTIVATE)]
    public async Task<IActionResult> SetStatus(Guid uuid, [FromBody] CustomerStatusRequest request, CancellationToken ct = default)
    {
        await _service.SetStatusAsync(uuid, request.IsActive, User.GetUserId(), ct);
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }
}

/// <summary>A37 D-16 — offline/edge clients pull customers changed since their last sync (API-CONTRACT §6).</summary>
[ApiController]
[Route("api/sync/customers")]
[RequiresFeature(ModuleCodes.Customers)]
public class CustomerSyncController : ControllerBase
{
    private readonly ICustomerService _service;
    public CustomerSyncController(ICustomerService service) => _service = service;

    [HttpGet]
    [RequirePermission(PermissionCodes.CUSTOMER_VIEW)]
    public async Task<IActionResult> GetChanges([FromQuery] DateTime? since, [FromQuery] int limit = 500, CancellationToken ct = default) =>
        Ok(ApiResponse<CustomerSyncResponse>.Ok(await _service.SyncAsync(since, limit, ct)));
}
