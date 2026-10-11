using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Modules.Pos.Models.PosPermissions;

namespace SMS.Modules.Pos.Controllers;

/// <summary>A38 API-CONTRACT §5 — order sync, reads, void, return; customer credit.</summary>
[ApiController]
[Route("api/pos/orders")]
[RequiresFeature(ModuleCodes.Pos)]
[PosRuleResultFilter]
public class PosOrdersController : ControllerBase
{
    private readonly IPosOrderService _orders;
    public PosOrdersController(IPosOrderService orders) => _orders = orders;

    private PosCaller Caller => PosCaller.From(User);

    // Grace (POS switched off): sales already made — offline, or on a shift that was open at switch-off — must still land.
    // New sales stop because no new shift can be opened during grace (BlockDuringGrace on shift open).
    [HttpPost("sync")]
    [RequirePermission(P.OrderCreate)]
    [AllowDuringGrace]
    public async Task<IActionResult> Sync([FromBody] PosSyncOrderRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosSyncResultModel>.Ok(await _orders.SyncAsync(req, Caller, ct), "Order synced."));

    [HttpPost("sync/batch")]
    [RequirePermission(P.OrderCreate)]
    [AllowDuringGrace]
    public async Task<IActionResult> SyncBatch([FromBody] PosSyncBatchRequest req, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosSyncResultModel>>.Ok(await _orders.SyncBatchAsync(req, Caller, ct)));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(P.OrderView, P.OrderCreate)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PosOrderModel>.Ok(await _orders.GetAsync(uuid, Caller, ct)));

    [HttpGet("by-local/{localId}")]
    [RequirePermission(P.OrderCreate)]
    public async Task<IActionResult> GetByLocal(string localId, CancellationToken ct) =>
        Ok(ApiResponse<PosOrderModel>.Ok(await _orders.GetByLocalIdAsync(localId, Caller, ct)));

    [HttpGet]
    [RequirePermission(P.OrderView)]
    public async Task<IActionResult> List([FromQuery] PosOrderListFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<PosOrderListItemModel>>.Ok(await _orders.ListAsync(filter, ct)));

    [HttpPatch("{uuid:guid}/void")]
    [RequirePermission(P.OrderVoid)]
    public async Task<IActionResult> Void(Guid uuid, [FromBody] PosVoidRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosOrderModel>.Ok(await _orders.VoidAsync(uuid, req?.Reason, Caller, ct), "Order voided."));

    [HttpPost("{uuid:guid}/return")]
    [RequirePermission(P.OrderReturn)]
    [RequiresFeature(PosFeatureCodes.Returns)]
    public async Task<IActionResult> Return(Guid uuid, [FromBody] PosReturnRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosOrderModel>.Ok(await _orders.ReturnAsync(uuid, req, Caller, ct), "Return recorded."));

    [HttpGet("/api/pos/customers/{customerUuid:guid}/credit")]
    [RequirePermission(P.OrderCreate)]
    public async Task<IActionResult> Credit(Guid customerUuid, CancellationToken ct) =>
        Ok(ApiResponse<PosCustomerCreditModel>.Ok(await _orders.GetCreditAsync(customerUuid, ct)));
}

/// <summary>A38 API-CONTRACT §6 — POS settings of products and customers.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Pos)]
public class PosSettingsController : ControllerBase
{
    private readonly IPosSettingsService _settings;
    public PosSettingsController(IPosSettingsService settings) => _settings = settings;

    private int UserId => User.GetUserId();

    [HttpGet("api/pos/products/{productUuid:guid}/settings")]
    [RequirePermission(P.ProductSettings)]
    public async Task<IActionResult> GetProduct(Guid productUuid, CancellationToken ct) =>
        Ok(ApiResponse<PosProductSettingsModel>.Ok(await _settings.GetProductAsync(productUuid, ct)));

    [HttpPut("api/pos/products/{productUuid:guid}/settings")]
    [RequirePermission(P.ProductSettings)]
    public async Task<IActionResult> PutProduct(Guid productUuid, [FromBody] PosProductSettingsModel req, CancellationToken ct) =>
        Ok(ApiResponse<PosProductSettingsModel>.Ok(await _settings.PutProductAsync(productUuid, req, UserId, ct), "POS settings saved."));

    [HttpPost("api/pos/products/settings/bulk")]
    [RequirePermission(P.ProductSettings)]
    public async Task<IActionResult> BulkProducts([FromBody] PosProductSettingsBulkRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosBulkResultModel>.Ok(await _settings.BulkProductsAsync(req, UserId, ct), "POS settings saved."));

    [HttpGet("api/pos/customers/{customerUuid:guid}/settings")]
    [RequirePermission(P.CustomerSettings)]
    public async Task<IActionResult> GetCustomer(Guid customerUuid, CancellationToken ct) =>
        Ok(ApiResponse<PosCustomerSettingsModel>.Ok(await _settings.GetCustomerAsync(customerUuid, ct)));

    [HttpPut("api/pos/customers/{customerUuid:guid}/settings")]
    [RequirePermission(P.CustomerSettings)]
    public async Task<IActionResult> PutCustomer(Guid customerUuid, [FromBody] PosCustomerSettingsModel req, CancellationToken ct) =>
        Ok(ApiResponse<PosCustomerSettingsModel>.Ok(await _settings.PutCustomerAsync(customerUuid, req, UserId, ct), "POS settings saved."));
}

/// <summary>A38 API-CONTRACT §7 — PWA sync pulls.</summary>
[ApiController]
[Route("api/pos/sync")]
[RequiresFeature(ModuleCodes.Pos)]
[RequirePermission(P.OrderCreate)]
public class PosSyncController : ControllerBase
{
    private readonly IPosSyncService _sync;
    public PosSyncController(IPosSyncService sync) => _sync = sync;

    [HttpGet("config")]
    public async Task<IActionResult> Config([FromQuery] Guid terminalUuid, CancellationToken ct) =>
        Ok(ApiResponse<PosSyncConfigModel>.Ok(await _sync.ConfigAsync(terminalUuid, ct)));

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog([FromQuery] Guid terminalUuid, [FromQuery] DateTimeOffset? since, [FromQuery] int limit, CancellationToken ct) =>
        Ok(ApiResponse<PosCatalogPageModel>.Ok(await _sync.CatalogAsync(terminalUuid, since?.UtcDateTime, limit, ct)));

    [HttpGet("stock")]
    public async Task<IActionResult> Stock([FromQuery] Guid terminalUuid, CancellationToken ct) =>
        Ok(ApiResponse<PosStockSnapshotModel>.Ok(await _sync.StockAsync(terminalUuid, ct)));

    [HttpGet("customers")]
    public async Task<IActionResult> Customers([FromQuery] Guid terminalUuid, [FromQuery] DateTimeOffset? since, [FromQuery] int limit, CancellationToken ct) =>
        Ok(ApiResponse<PosCustomersPageModel>.Ok(await _sync.CustomersAsync(terminalUuid, since?.UtcDateTime, limit, ct)));

    [HttpGet("users")]
    public async Task<IActionResult> Users([FromQuery] Guid terminalUuid, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosSyncUserModel>>.Ok(
            await _sync.UsersAsync(terminalUuid, Request.Headers[PosSyncService.DeviceHeader].FirstOrDefault(), ct)));
}
