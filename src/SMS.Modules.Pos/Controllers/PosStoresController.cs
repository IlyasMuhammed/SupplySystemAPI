using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Modules.Pos.Models.PosPermissions;

namespace SMS.Modules.Pos.Controllers;

/// <summary>A38 API-CONTRACT §2 — stores, their terminals and staff.</summary>
[ApiController]
[Route("api/pos/stores")]
[RequiresFeature(ModuleCodes.Pos)]
public class PosStoresController : ControllerBase
{
    private readonly IPosStoreService _stores;
    public PosStoresController(IPosStoreService stores) => _stores = stores;

    [HttpGet]
    [RequirePermission(P.StoreView)]
    public async Task<IActionResult> List([FromQuery] PosStoreListFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<PosStoreModel>>.Ok(await _stores.ListStoresAsync(filter, ct)));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(P.StoreView)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PosStoreModel>.Ok(await _stores.GetStoreAsync(uuid, ct)));

    [HttpPost]
    [RequirePermission(P.StoreManage)]
    public async Task<IActionResult> Create([FromBody] PosStoreUpsertRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosStoreModel>.Ok(await _stores.CreateStoreAsync(req, User.GetUserId(), ct), "Store created."));

    [HttpPut("{uuid:guid}")]
    [RequirePermission(P.StoreManage)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] PosStoreUpsertRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosStoreModel>.Ok(await _stores.UpdateStoreAsync(uuid, req, User.GetUserId(), ct), "Store updated."));

    [HttpPatch("{uuid:guid}/status")]
    [RequirePermission(P.StoreManage)]
    public async Task<IActionResult> SetStatus(Guid uuid, [FromBody] PosStatusRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosStoreModel>.Ok(await _stores.SetStoreStatusAsync(uuid, req.IsActive, User.GetUserId(), ct),
            req.IsActive ? "Store activated." : "Store deactivated."));

    [HttpGet("{uuid:guid}/terminals")]
    [RequirePermission(P.TerminalView)]
    public async Task<IActionResult> Terminals(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosTerminalModel>>.Ok(await _stores.ListTerminalsAsync(uuid, ct)));

    [HttpPost("{uuid:guid}/terminals")]
    [RequirePermission(P.TerminalManage)]
    [BlockDuringGrace]
    public async Task<IActionResult> CreateTerminal(Guid uuid, [FromBody] PosTerminalCreateRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosTerminalModel>.Ok(await _stores.CreateTerminalAsync(uuid, req, User.GetUserId(), ct), "Terminal created."));

    [HttpGet("{uuid:guid}/staff")]
    [RequirePermission(P.StoreView)]
    public async Task<IActionResult> Staff(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosStaffMemberModel>>.Ok(await _stores.ListStaffAsync(uuid, ct)));

    [HttpGet("{uuid:guid}/staff/candidates")]
    [RequirePermission(P.StoreManage)]
    public async Task<IActionResult> Candidates(Guid uuid, [FromQuery] string? search, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosStaffCandidateModel>>.Ok(await _stores.ListCandidatesAsync(uuid, search, ct)));

    [HttpPut("{uuid:guid}/staff/{userId:int}")]
    [RequirePermission(P.StoreManage)]
    public async Task<IActionResult> UpsertStaff(Guid uuid, int userId, [FromBody] PosStaffUpsertRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosStaffMemberModel>.Ok(await _stores.UpsertStaffAsync(uuid, userId, req, User.GetUserId(), ct), "Staff member saved."));

    [HttpDelete("{uuid:guid}/staff/{userId:int}")]
    [RequirePermission(P.StoreManage)]
    public async Task<IActionResult> RemoveStaff(Guid uuid, int userId, CancellationToken ct)
    {
        await _stores.RemoveStaffAsync(uuid, userId, ct);
        return Ok(ApiResponse.Ok("Staff member removed."));
    }
}

/// <summary>A38 API-CONTRACT §2 + §8 admin triggers — terminals by UUID.</summary>
[ApiController]
[Route("api/pos/terminals")]
[RequiresFeature(ModuleCodes.Pos)]
public class PosTerminalsController : ControllerBase
{
    private readonly IPosStoreService _stores;
    public PosTerminalsController(IPosStoreService stores) => _stores = stores;

    [HttpPut("{uuid:guid}")]
    [RequirePermission(P.TerminalManage)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] PosTerminalUpdateRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosTerminalModel>.Ok(await _stores.UpdateTerminalAsync(uuid, req, ct), "Terminal updated."));

    [HttpPost("{uuid:guid}/bind")]
    [RequirePermission(P.TerminalManage, P.ShiftOpen)]
    public async Task<IActionResult> Bind(Guid uuid, [FromBody] PosBindRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosTerminalConfigModel>.Ok(await _stores.BindAsync(uuid, req.DeviceUuid, PosCaller.From(User), ct), "Terminal bound to this device."));

    [HttpPost("{uuid:guid}/unbind")]
    [RequirePermission(P.TerminalManage)]
    public async Task<IActionResult> Unbind(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PosTerminalModel>.Ok(await _stores.UnbindAsync(uuid, ct), "Terminal unbound."));

    [HttpPost("{uuid:guid}/unlock")]
    [RequirePermission(P.ShiftOpen)]
    public async Task<IActionResult> Unlock(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PosTerminalModel>.Ok(await _stores.UnlockAsync(uuid, ct), "Terminal unlocked."));

    [HttpGet("by-device/{deviceUuid:guid}")]
    [RequirePermission(P.StoreView, P.StoreManage, P.TerminalView, P.TerminalManage, P.ShiftOpen, P.ShiftClose, P.ShiftReconcile, P.ShiftView,
        P.OrderCreate, P.OrderView, P.OrderVoid, P.OrderReturn, P.OrderDiscount, P.CashMovement, P.ProductSettings, P.CustomerSettings, P.ReportsView)]
    public async Task<IActionResult> ByDevice(Guid deviceUuid, CancellationToken ct) =>
        await _stores.GetByDeviceAsync(deviceUuid, ct) is { } config
            ? Ok(ApiResponse<PosTerminalConfigModel>.Ok(config))
            : NotFound(ApiResponse.Fail("This device is not bound to a terminal."));

    [HttpGet("{uuid:guid}/status")]
    [RequirePermission(P.TerminalView)]
    public async Task<IActionResult> Status(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PosTerminalStatusModel>.Ok(await _stores.GetStatusAsync(uuid, ct)));

    [HttpGet("available")]
    [RequirePermission(P.StoreView, P.StoreManage, P.TerminalView, P.TerminalManage, P.ShiftOpen, P.ShiftClose, P.ShiftReconcile, P.ShiftView,
        P.OrderCreate, P.OrderView, P.OrderVoid, P.OrderReturn, P.OrderDiscount, P.CashMovement, P.ProductSettings, P.CustomerSettings, P.ReportsView)]
    public async Task<IActionResult> Available(CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosTerminalModel>>.Ok(await _stores.ListAvailableAsync(PosCaller.From(User), ct)));

    [HttpPost("{uuid:guid}/force-sync")]
    [RequirePermission(P.TerminalManage)]
    public async Task<IActionResult> ForceSync(Guid uuid, [FromBody] PosForceSyncRequest req, CancellationToken ct)
    {
        await _stores.ForceSyncAsync(uuid, req?.SyncType, ct);
        return Ok(ApiResponse.Ok("Sync requested."));
    }

    [HttpPost("{uuid:guid}/force-logout")]
    [RequirePermission(P.TerminalManage)]
    public async Task<IActionResult> ForceLogout(Guid uuid, [FromBody] PosForceLogoutRequest req, CancellationToken ct)
    {
        await _stores.ForceLogoutAsync(uuid, req?.Reason, ct);
        return Ok(ApiResponse.Ok("Terminal signed out."));
    }
}
