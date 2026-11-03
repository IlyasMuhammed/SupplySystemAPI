using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services.Att;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Controllers;

// API-CONTRACT §6. Leave counts working days, so shifts and holidays open with either the attendance or the leave sub-feature.

/// <summary>API-CONTRACT §6.1.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Attendance, HrmFeatureCodes.Leave)]
[Route("api/hrm/shifts")]
public sealed class ShiftsController : HrmControllerBase
{
    private readonly IShiftService _svc;
    public ShiftsController(ITenantContext tenant, IShiftService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List shifts")]
    [HttpGet, RequirePermission(P.HRM_ATTENDANCE_VIEW, P.HRM_SHIFT_MANAGE, P.HRM_EMPLOYEE_EDIT)]
    public async Task<IActionResult> List([FromQuery] bool includeInactive, CancellationToken ct) => Done(await _svc.ListAsync(Caller, includeInactive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a shift")]
    [HttpPost, RequirePermission(P.HRM_SHIFT_MANAGE)]
    public async Task<IActionResult> Create([FromBody] ShiftUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Shift created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a shift")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_SHIFT_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] ShiftUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Shift saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Activate or deactivate a shift")]
    [HttpPatch("{uuid:guid}/active"), RequirePermission(P.HRM_SHIFT_MANAGE)]
    public async Task<IActionResult> SetActive(Guid uuid, [FromBody] SetActiveRequest req, CancellationToken ct) => Done(await _svc.SetActiveAsync(Caller, uuid, req.IsActive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Assign a shift to employees from a date")]
    [HttpPost("{uuid:guid}/assign"), RequirePermission(P.HRM_SHIFT_MANAGE)]
    public async Task<IActionResult> Assign(Guid uuid, [FromBody] ShiftAssignRequest req, CancellationToken ct) => Done(await _svc.AssignAsync(Caller, uuid, req, ct), "Shift assigned.");
}

/// <summary>API-CONTRACT §6.1.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Attendance, HrmFeatureCodes.Leave)]
[Route("api/hrm/holidays")]
public sealed class HolidaysController : HrmControllerBase
{
    private readonly IHolidayService _svc;
    public HolidaysController(ITenantContext tenant, IHolidayService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List holidays for a year and branch")]
    [HttpGet, RequirePermission(P.HRM_SELF_SERVICE, P.HRM_ATTENDANCE_VIEW, P.HRM_LEAVE_VIEW, P.HRM_LEAVE_APPROVE, P.HRM_LEAVE_MANAGE, P.HRM_SHIFT_MANAGE, P.HRM_EMPLOYEE_VIEW, P.HRM_ORG_VIEW, P.HRM_PAYROLL_VIEW)]
    public async Task<IActionResult> List([FromQuery] int? year, [FromQuery] Guid? branchUuid, CancellationToken ct) => Done(await _svc.ListAsync(Caller, year, branchUuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a holiday")]
    [HttpPost, RequirePermission(P.HRM_SHIFT_MANAGE)]
    public async Task<IActionResult> Create([FromBody] HolidayUpsert req, CancellationToken ct) => Done(await _svc.CreateAsync(Caller, req, ct), "Holiday added.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a holiday (clears the estimated flag when the date changes)")]
    [HttpPut("{uuid:guid}"), RequirePermission(P.HRM_SHIFT_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] HolidayUpsert req, CancellationToken ct) => Done(await _svc.UpdateAsync(Caller, uuid, req, ct), "Holiday saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Delete a holiday")]
    [HttpDelete("{uuid:guid}"), RequirePermission(P.HRM_SHIFT_MANAGE)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        await _svc.DeleteAsync(Caller, uuid, ct);
        return Ok(ApiResponse.Ok("Holiday removed."));
    }
}

/// <summary>API-CONTRACT §6.2.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Attendance)]
[Route("api/hrm/attendance")]
public sealed class AttendanceController : HrmControllerBase
{
    private const long MaxImportBytes = 10 * 1024 * 1024;
    private readonly IAttendanceService _svc;
    public AttendanceController(ITenantContext tenant, IAttendanceService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("Record an attendance event for an employee")]
    [HttpPost("events"), RequirePermission(P.HRM_ATTENDANCE_MANAGE)]
    public async Task<IActionResult> Record([FromBody] AttendanceEventRequest req, CancellationToken ct) => Done(await _svc.RecordEventAsync(Caller, req, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Record the caller's own attendance event")]
    [HttpPost("events/self"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> RecordOwn([FromBody] AttendanceSelfEventRequest req, CancellationToken ct) => Done(await _svc.RecordOwnEventAsync(Caller, req, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Import attendance events from a CSV file")]
    [HttpPost("events/import"), RequirePermission(P.HRM_ATTENDANCE_MANAGE), Consumes("multipart/form-data"), RequestSizeLimit(MaxImportBytes + 64 * 1024)]
    public async Task<IActionResult> Import(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(ApiResponse.Fail("Choose a CSV file."));
        if (file.Length > MaxImportBytes) return BadRequest(ApiResponse.Fail("The file is larger than 10 MB."));
        await using var stream = file.OpenReadStream();
        return Done(await _svc.ImportEventsAsync(Caller, stream, ct));
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("List attendance days in scope")]
    [HttpGet("days"), RequirePermission(P.HRM_ATTENDANCE_VIEW, P.HRM_ATTENDANCE_MANAGE)]
    public async Task<IActionResult> Days([FromQuery] AttendanceDayFilter filter, CancellationToken ct) => Done(await _svc.ListDaysAsync(Caller, filter, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("List the caller's own attendance days")]
    [HttpGet("days/me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> OwnDays([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) => Done(await _svc.OwnDaysAsync(Caller, from, to, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Recompute attendance days for a date range")]
    [HttpPost("days/recompute"), RequirePermission(P.HRM_ATTENDANCE_MANAGE)]
    public async Task<IActionResult> Recompute([FromBody] AttendanceRecomputeRequest req, CancellationToken ct) => Done(await _svc.RecomputeAsync(Caller, req, ct), "Attendance recomputed.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Override an attendance day's status")]
    [HttpPut("days/{uuid:guid}"), RequirePermission(P.HRM_ATTENDANCE_MANAGE)]
    public async Task<IActionResult> Override(Guid uuid, [FromBody] AttendanceDayOverride req, CancellationToken ct) => Done(await _svc.OverrideDayAsync(Caller, uuid, req, ct), "Attendance day changed.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Monthly attendance summary for an employee")]
    [HttpGet("summary"), RequirePermission(P.HRM_ATTENDANCE_VIEW, P.HRM_ATTENDANCE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Summary([FromQuery] Guid? employeeUuid, [FromQuery] int year, [FromQuery] int month, CancellationToken ct) =>
        Done(await _svc.GetMonthlySummaryAsync(Caller, employeeUuid, year, month, ct));
}

/// <summary>API-CONTRACT §6.3.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Attendance)]
[Route("api/hrm/overtime")]
public sealed class OvertimeController : HrmControllerBase
{
    private readonly IOvertimeService _svc;
    public OvertimeController(ITenantContext tenant, IOvertimeService svc) : base(tenant) => _svc = svc;

    [Microsoft.AspNetCore.Http.EndpointSummary("List overtime records")]
    [HttpGet, RequirePermission(P.HRM_ATTENDANCE_VIEW, P.HRM_ATTENDANCE_MANAGE, P.HRM_OVERTIME_APPROVE)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? employeeUuid, CancellationToken ct) =>
        Done(await _svc.ListAsync(Caller, status, from, to, employeeUuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Submit an overtime record")]
    [HttpPost, RequirePermission(P.HRM_ATTENDANCE_MANAGE)]
    public async Task<IActionResult> Submit([FromBody] OvertimeCreate req, CancellationToken ct) => Done(await _svc.SubmitAsync(Caller, req, ct), "Overtime submitted.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Approve overtime")]
    [HttpPost("{uuid:guid}/approve"), RequirePermission(P.HRM_OVERTIME_APPROVE)]
    public async Task<IActionResult> Approve(Guid uuid, CancellationToken ct) => Done(await _svc.ApproveAsync(Caller, uuid, ct), "Overtime approved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Reject overtime")]
    [HttpPost("{uuid:guid}/reject"), RequirePermission(P.HRM_OVERTIME_APPROVE)]
    public async Task<IActionResult> Reject(Guid uuid, [FromBody] DecisionNoteRequest req, CancellationToken ct) => Done(await _svc.RejectAsync(Caller, uuid, req.Note, ct), "Overtime rejected.");
}

/// <summary>API-CONTRACT §6.4.</summary>
[ApiController]
[RequiresFeature(ModuleCodes.Hrm)]
[RequiresFeature(HrmFeatureCodes.Leave)]
[Route("api/hrm/leave")]
public sealed class LeaveController : HrmControllerBase
{
    private readonly ILeaveTypeService _types;
    private readonly ILeaveService _leave;

    public LeaveController(ITenantContext tenant, ILeaveTypeService types, ILeaveService leave) : base(tenant)
    {
        _types = types;
        _leave = leave;
    }

    [Microsoft.AspNetCore.Http.EndpointSummary("List leave types with their policies")]
    [HttpGet("types"), RequirePermission(P.HRM_LEAVE_VIEW, P.HRM_LEAVE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Types([FromQuery] bool includeInactive, CancellationToken ct) => Done(await _types.ListAsync(Caller, includeInactive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Create a leave type")]
    [HttpPost("types"), RequirePermission(P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> CreateType([FromBody] LeaveTypeUpsert req, CancellationToken ct) => Done(await _types.CreateAsync(Caller, req, ct), "Leave type created.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update a leave type")]
    [HttpPut("types/{uuid:guid}"), RequirePermission(P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> UpdateType(Guid uuid, [FromBody] LeaveTypeUpsert req, CancellationToken ct) => Done(await _types.UpdateAsync(Caller, uuid, req, ct), "Leave type saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Activate or deactivate a leave type")]
    [HttpPatch("types/{uuid:guid}/active"), RequirePermission(P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> SetTypeActive(Guid uuid, [FromBody] SetActiveRequest req, CancellationToken ct) => Done(await _types.SetActiveAsync(Caller, uuid, req.IsActive, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Add an entitlement policy to a leave type")]
    [HttpPost("types/{uuid:guid}/policies"), RequirePermission(P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> AddPolicy(Guid uuid, [FromBody] LeavePolicyUpsert req, CancellationToken ct) => Done(await _types.AddPolicyAsync(Caller, uuid, req, ct), "Policy added.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Update an entitlement policy")]
    [HttpPut("policies/{uuid:guid}"), RequirePermission(P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> UpdatePolicy(Guid uuid, [FromBody] LeavePolicyUpsert req, CancellationToken ct) => Done(await _types.UpdatePolicyAsync(Caller, uuid, req, ct), "Policy saved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Leave balances of an employee")]
    [HttpGet("balances"), RequirePermission(P.HRM_LEAVE_VIEW, P.HRM_LEAVE_APPROVE, P.HRM_LEAVE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Balances([FromQuery] Guid? employeeUuid, [FromQuery] int? year, CancellationToken ct) => Done(await _leave.BalancesAsync(Caller, employeeUuid, year, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("The caller's own leave balances")]
    [HttpGet("balances/me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> OwnBalances([FromQuery] int? year, CancellationToken ct) => Done(await _leave.BalancesAsync(Caller, null, year, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Leave ledger entries of an employee")]
    [HttpGet("ledger"), RequirePermission(P.HRM_LEAVE_VIEW, P.HRM_LEAVE_APPROVE, P.HRM_LEAVE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Ledger([FromQuery] Guid? employeeUuid, [FromQuery] Guid? leaveTypeUuid, [FromQuery] int? year, CancellationToken ct) =>
        Done(await _leave.LedgerAsync(Caller, employeeUuid, leaveTypeUuid, year, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Allocate or adjust leave for one employee")]
    [HttpPost("allocate"), RequirePermission(P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> Allocate([FromBody] LeaveAllocateRequest req, CancellationToken ct) => Done(await _leave.AllocateAsync(Caller, req, ct), "Leave allocated.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Allocate a leave type's entitlement to all eligible employees")]
    [HttpPost("allocate/bulk"), RequirePermission(P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> BulkAllocate([FromBody] LeaveBulkAllocateRequest req, CancellationToken ct) => Done(await _leave.BulkAllocateAsync(Caller, req, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Carry leave balances into the next year")]
    [HttpPost("carry-forward"), RequirePermission(P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> CarryForward([FromBody] LeaveCarryForwardRequest req, CancellationToken ct) => Done(await _leave.CarryForwardAsync(Caller, req, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("List leave requests in scope")]
    [HttpGet("requests"), RequirePermission(P.HRM_LEAVE_VIEW, P.HRM_LEAVE_APPROVE, P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> Requests([FromQuery] LeaveRequestFilter filter, CancellationToken ct) => Done(await _leave.ListRequestsAsync(Caller, filter, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("List the caller's own leave requests")]
    [HttpGet("requests/me"), RequirePermission(P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> OwnRequests(CancellationToken ct) => Done(await _leave.OwnRequestsAsync(Caller, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Get a leave request")]
    [HttpGet("requests/{uuid:guid}"), RequirePermission(P.HRM_LEAVE_VIEW, P.HRM_LEAVE_APPROVE, P.HRM_LEAVE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> GetRequest(Guid uuid, CancellationToken ct) => Done(await _leave.GetRequestAsync(Caller, uuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Team calendar of approved leave")]
    [HttpGet("calendar"), RequirePermission(P.HRM_LEAVE_VIEW, P.HRM_LEAVE_APPROVE, P.HRM_LEAVE_MANAGE, P.HRM_SELF_SERVICE)]
    public async Task<IActionResult> Calendar([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? departmentUuid, CancellationToken ct) =>
        Done(await _leave.CalendarAsync(Caller, from, to, departmentUuid, ct));

    [Microsoft.AspNetCore.Http.EndpointSummary("Submit a leave request to its approval workflow")]
    [HttpPost("requests"), RequirePermission(P.HRM_SELF_SERVICE, P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> Request([FromBody] LeaveRequestCreate req, CancellationToken ct) => Done(await _leave.RequestLeaveAsync(Caller, req, ct), "Leave requested.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Approve a leave request")]
    [HttpPost("requests/{uuid:guid}/approve"), RequirePermission(P.HRM_LEAVE_APPROVE)]
    public async Task<IActionResult> Approve(Guid uuid, CancellationToken ct) => Done(await _leave.ApproveAsync(Caller, uuid, ct), "Leave approved.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Reject a leave request")]
    [HttpPost("requests/{uuid:guid}/reject"), RequirePermission(P.HRM_LEAVE_APPROVE)]
    public async Task<IActionResult> Reject(Guid uuid, [FromBody] DecisionNoteRequest req, CancellationToken ct) => Done(await _leave.RejectAsync(Caller, uuid, req.Note, ct), "Leave rejected.");

    [Microsoft.AspNetCore.Http.EndpointSummary("Cancel a leave request and restore the ledger")]
    [HttpPost("requests/{uuid:guid}/cancel"), RequirePermission(P.HRM_SELF_SERVICE, P.HRM_LEAVE_MANAGE)]
    public async Task<IActionResult> Cancel(Guid uuid, CancellationToken ct) => Done(await _leave.CancelAsync(Caller, uuid, ct), "Leave cancelled.");
}
