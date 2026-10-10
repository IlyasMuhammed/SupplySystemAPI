using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

/// <summary>A36 — service orders (API-CONTRACT §3). Every action answers with the full <see cref="ServiceOrderDetailModel"/>.</summary>
[ApiController]
[Route("api/service-orders")]
[RequiresFeature("MODULE_SERVICES")]
public class ServiceOrdersController : ControllerBase
{
    private readonly IServiceOrderService _orders;

    public ServiceOrdersController(IServiceOrderService orders) => _orders = orders;

    private IActionResult Detail(ServiceOrderDetailModel detail, string message) =>
        Ok(ApiResponse<ServiceOrderDetailModel>.Ok(detail, message));

    [HttpPost]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_CREATE)]
    public async Task<IActionResult> Create([FromBody] CreateServiceOrderRequest req) =>
        Ok(ApiResponse<Guid>.Ok(await _orders.CreateAsync(req, User.GetUserId()), "Service order created."));

    [HttpGet]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] ServiceOrderListFilter filter) =>
        Ok(ApiResponse<PaginatedResponse<ServiceOrderListItemModel>>.Ok(await _orders.GetListAsync(filter)));

    /// <summary>#13 — kept ahead of the {uuid} routes (the guid constraint keeps them apart anyway).</summary>
    [HttpGet("dashboard")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_VIEW)]
    public async Task<IActionResult> GetDashboard() =>
        Ok(ApiResponse<ServiceDashboardModel>.Ok(await _orders.GetDashboardAsync(User.GetUserId())));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid) =>
        Ok(ApiResponse<ServiceOrderDetailModel>.Ok(await _orders.GetAsync(uuid) ?? throw new NotFoundException("Service order", uuid)));

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_EDIT)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateServiceOrderRequest req) =>
        Detail(await _orders.UpdateAsync(uuid, req, User.GetUserId()), "Service order updated.");

    [HttpPost("{uuid:guid}/plan")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_EDIT)]
    public async Task<IActionResult> Plan(Guid uuid) =>
        Detail(await _orders.PlanAsync(uuid, User.GetUserId()), "Service order planned.");

    [HttpPost("{uuid:guid}/start")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_EDIT)]
    public async Task<IActionResult> Start(Guid uuid) =>
        Detail(await _orders.StartAsync(uuid, User.GetUserId()), "Service started.");

    [HttpPost("{uuid:guid}/complete")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_COMPLETE)]
    public async Task<IActionResult> Complete(Guid uuid, [FromBody] CompleteServiceOrderRequest req) =>
        Detail(await _orders.CompleteAsync(uuid, req, User.GetUserId()), "Service completed.");

    [HttpPost("{uuid:guid}/cancel")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_CANCEL)]
    public async Task<IActionResult> Cancel(Guid uuid, [FromBody] CancelServiceOrderRequest req) =>
        Detail(await _orders.CancelAsync(uuid, req?.Reason, User.GetUserId()), "Service order cancelled.");

    [HttpPost("{uuid:guid}/close")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_EDIT)]
    public async Task<IActionResult> Close(Guid uuid) =>
        Detail(await _orders.CloseAsync(uuid, User.GetUserId()), "Service order closed.");

    [HttpGet("{uuid:guid}/materials")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_VIEW)]
    public async Task<IActionResult> GetMaterials(Guid uuid) =>
        Ok(ApiResponse<IReadOnlyList<ServiceMaterialModel>>.Ok(await _orders.GetMaterialsAsync(uuid)));

    [HttpPost("{uuid:guid}/materials")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_EDIT)]
    public async Task<IActionResult> AddMaterial(Guid uuid, [FromBody] AddAdhocMaterialRequest req) =>
        Detail(await _orders.AddMaterialAsync(uuid, req, User.GetUserId()), "Material added.");

    [HttpDelete("{uuid:guid}/materials/{smrUuid:guid}")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_EDIT)]
    public async Task<IActionResult> RemoveMaterial(Guid uuid, Guid smrUuid) =>
        Detail(await _orders.RemoveMaterialAsync(uuid, smrUuid, User.GetUserId()), "Material removed.");

    [HttpPost("{uuid:guid}/materials/allocate")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_EDIT)]
    public async Task<IActionResult> Allocate(Guid uuid) =>
        Detail(await _orders.AllocateAsync(uuid, User.GetUserId()), "Available materials reserved.");

    [HttpGet("{uuid:guid}/ledger")]
    [RequirePermission(PermissionCodes.SERVICE_ORDER_VIEW)]
    public async Task<IActionResult> GetLedger(Guid uuid) =>
        Ok(ApiResponse<ServiceLedgerModel>.Ok(await _orders.GetLedgerAsync(uuid)));
}
