using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Modules.Pos.Models.PosPermissions;

namespace SMS.Modules.Pos.Controllers;

/// <summary>A38 API-CONTRACT §4 — shifts and cash movements.</summary>
[ApiController]
[Route("api/pos/shifts")]
[RequiresFeature(ModuleCodes.Pos)]
public class PosShiftsController : ControllerBase
{
    private readonly IPosShiftService _shifts;
    public PosShiftsController(IPosShiftService shifts) => _shifts = shifts;

    private PosCaller Caller => PosCaller.From(User);
    private IActionResult Shift(PosShiftModel shift, string message) => Ok(ApiResponse<PosShiftModel>.Ok(shift, message));

    [HttpPost("/api/pos/terminals/{terminalUuid:guid}/shifts")]
    [RequirePermission(P.ShiftOpen)]
    [BlockDuringGrace]
    public async Task<IActionResult> Open(Guid terminalUuid, [FromBody] PosOpenShiftRequest req, CancellationToken ct) =>
        Shift(await _shifts.OpenAsync(terminalUuid, req, Caller, ct), "Shift opened.");

    [HttpGet("/api/pos/terminals/{terminalUuid:guid}/shifts/current")]
    [RequirePermission(P.OrderCreate, P.ShiftView)]
    public async Task<IActionResult> Current(Guid terminalUuid, CancellationToken ct) =>
        await _shifts.GetCurrentAsync(terminalUuid, ct) is { } shift
            ? Ok(ApiResponse<PosShiftModel>.Ok(shift))
            : NotFound(ApiResponse.Fail("The terminal has no active shift."));

    [HttpGet]
    [RequirePermission(P.ShiftView)]
    public async Task<IActionResult> List([FromQuery] PosShiftListFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<PosShiftModel>>.Ok(await _shifts.ListAsync(filter, ct)));

    [HttpGet("{uuid:guid}")]
    [RequirePermission(P.ShiftView, P.OrderCreate)]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PosShiftDetailModel>.Ok(await _shifts.GetAsync(uuid, Caller, ct)));

    [HttpGet("{uuid:guid}/summary")]
    [RequirePermission(P.ShiftView, P.OrderCreate)]
    public async Task<IActionResult> Summary(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PosShiftSummaryModel>.Ok(await _shifts.GetSummaryAsync(uuid, Caller, ct)));

    [HttpPatch("{uuid:guid}/start-close")]
    [RequirePermission(P.ShiftClose)]
    public async Task<IActionResult> StartClose(Guid uuid, CancellationToken ct) =>
        Shift(await _shifts.StartCloseAsync(uuid, Caller, ct), "Shift closing started.");

    [HttpPatch("{uuid:guid}/resume")]
    [RequirePermission(P.ShiftClose)]
    public async Task<IActionResult> Resume(Guid uuid, CancellationToken ct) =>
        Shift(await _shifts.ResumeAsync(uuid, Caller, ct), "Shift resumed.");

    [HttpPatch("{uuid:guid}/close")]
    [RequirePermission(P.ShiftClose)]
    public async Task<IActionResult> Close(Guid uuid, [FromBody] PosCloseShiftRequest req, CancellationToken ct) =>
        Shift(await _shifts.CloseAsync(uuid, req, Caller, ct), "Shift closed.");

    [HttpPatch("{uuid:guid}/reconcile")]
    [RequirePermission(P.ShiftReconcile)]
    public async Task<IActionResult> Reconcile(Guid uuid, [FromBody] PosNoteRequest req, CancellationToken ct) =>
        Shift(await _shifts.ReconcileAsync(uuid, req?.Note, Caller, ct), "Shift reconciled.");

    [HttpPatch("{uuid:guid}/note")]
    [RequirePermission(P.ShiftReconcile)]
    public async Task<IActionResult> Note(Guid uuid, [FromBody] PosNoteRequest req, CancellationToken ct) =>
        Shift(await _shifts.AddNoteAsync(uuid, req?.Note, Caller, ct), "Note added.");

    [HttpPost("{uuid:guid}/cash-movements")]
    [RequirePermission(P.CashMovement)]
    public async Task<IActionResult> AddCashMovement(Guid uuid, [FromBody] PosCashMovementRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosCashMovementModel>.Ok(await _shifts.AddCashMovementAsync(uuid, req, Caller, ct), "Cash movement recorded."));

    [HttpGet("{uuid:guid}/cash-movements")]
    [RequirePermission(P.ShiftView, P.OrderCreate)]
    public async Task<IActionResult> CashMovements(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<IReadOnlyList<PosCashMovementModel>>.Ok(await _shifts.ListCashMovementsAsync(uuid, Caller, ct)));
}

/// <summary>A38 API-CONTRACT §3 — PIN sign-in on a terminal and cashier sessions.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Pos)]
public class PosSessionsController : ControllerBase
{
    private readonly IPosSessionService _sessions;
    public PosSessionsController(IPosSessionService sessions) => _sessions = sessions;

    /// <summary>Signed in with the device's token (same organization); answers with the cashier's own token pair.</summary>
    [HttpPost("api/pos/auth/pin")]
    [RequirePermission(P.OrderCreate, P.ShiftOpen, P.TerminalManage)] // the device user; stops any org user guessing PINs
    public async Task<IActionResult> PinSignIn([FromBody] PosPinSignInRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosPinSignInModel>.Ok(await _sessions.PinSignInAsync(req, ct), "Signed in."));

    [HttpPost("api/pos/sessions")]
    [RequirePermission(P.OrderCreate)]
    public async Task<IActionResult> Start([FromBody] PosSessionStartRequest req, CancellationToken ct) =>
        Ok(ApiResponse<PosCashierSessionModel>.Ok(await _sessions.StartAsync(req, PosCaller.From(User), ct), "Session started."));

    [HttpPatch("api/pos/sessions/{uuid:guid}/end")]
    [RequirePermission(P.OrderCreate)]
    public async Task<IActionResult> End(Guid uuid, CancellationToken ct) =>
        Ok(ApiResponse<PosCashierSessionModel>.Ok(await _sessions.EndAsync(uuid, PosCaller.From(User), ct), "Session ended."));
}
