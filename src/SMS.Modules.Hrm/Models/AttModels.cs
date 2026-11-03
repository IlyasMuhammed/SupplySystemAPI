namespace SMS.Modules.Hrm.Models;

// API-CONTRACT §6 — shifts, holidays, attendance, overtime, leave. uuid only; camelCase JSON.

public sealed class ShiftModel
{
    public Guid     Uuid             { get; set; }
    public string   Code             { get; set; } = "";
    public string   Name             { get; set; } = "";
    public TimeOnly StartTime        { get; set; }
    public TimeOnly EndTime          { get; set; }
    public int      BreakMinutes     { get; set; }
    public int      GraceInMinutes   { get; set; }
    public int      GraceOutMinutes  { get; set; }
    public int      HalfDayMinutes   { get; set; }
    public int      OvertimeThresholdMinutes { get; set; }
    public int      WeeklyOffDays    { get; set; }
    public bool     IsDefault        { get; set; }
    public bool     IsActive         { get; set; }
}

public sealed class ShiftUpsert
{
    public string?   Code             { get; set; }
    public string?   Name             { get; set; }
    public TimeOnly? StartTime        { get; set; }
    public TimeOnly? EndTime          { get; set; }
    public int       BreakMinutes     { get; set; }
    public int       GraceInMinutes   { get; set; }
    public int       GraceOutMinutes  { get; set; }
    public int       HalfDayMinutes   { get; set; }
    public int       OvertimeThresholdMinutes { get; set; }
    public int       WeeklyOffDays    { get; set; }
    public bool      IsDefault        { get; set; }
}

public sealed class ShiftAssignRequest
{
    public List<Guid> EmployeeUuids { get; set; } = [];
    public DateOnly?  EffectiveFrom { get; set; }
}

public sealed class ShiftAssignResult { public int Assigned { get; set; } }

public sealed class HolidayModel
{
    public Guid     Uuid        { get; set; }
    public DateOnly Date        { get; set; }
    public string   Name        { get; set; } = "";
    public string   CountryCode { get; set; } = "";
    public Guid?    BranchUuid  { get; set; }
    public bool     IsEstimated { get; set; }
}

public sealed class HolidayUpsert
{
    public DateOnly? Date        { get; set; }
    public string?   Name        { get; set; }
    public string?   CountryCode { get; set; }
    public Guid?     BranchUuid  { get; set; }
}

public sealed class AttendanceEventRequest
{
    public Guid      EmployeeUuid    { get; set; }
    public DateTime? EventTimeUtc    { get; set; }
    public string?   EventType       { get; set; }
    public string?   Source          { get; set; }
    public string?   ExternalEventId { get; set; }
    public string?   Note            { get; set; }
}

public sealed class AttendanceSelfEventRequest { public string? EventType { get; set; } }

public sealed class AttendanceEventResult
{
    public Guid Uuid      { get; set; }
    public bool Duplicate { get; set; }
}

public sealed class AttendanceImportError
{
    public int    Row     { get; set; }
    public string Message { get; set; } = "";
}

public sealed class AttendanceImportResult
{
    public int Total      { get; set; }
    public int Imported   { get; set; }
    public int Duplicates { get; set; }
    public List<AttendanceImportError> Errors { get; set; } = [];
}

public sealed class AttendanceDayModel
{
    public Guid      Uuid            { get; set; }
    public Guid      EmployeeUuid    { get; set; }
    public string    EmployeeName    { get; set; } = "";
    public DateOnly  Date            { get; set; }
    public string?   ShiftName       { get; set; }
    public DateTime? FirstInUtc      { get; set; }
    public DateTime? LastOutUtc      { get; set; }
    public int       WorkedMinutes   { get; set; }
    public int       LateMinutes     { get; set; }
    public int       EarlyMinutes    { get; set; }
    public int       OvertimeMinutes { get; set; }
    public string    Status          { get; set; } = "";
    public bool      IsManualOverride { get; set; }
    public string?   OverrideNote    { get; set; }
}

public sealed class AttendanceDayFilter
{
    public Guid?     EmployeeUuid   { get; set; }
    public Guid?     DepartmentUuid { get; set; }
    public Guid?     BranchUuid     { get; set; }
    public DateOnly? From           { get; set; }
    public DateOnly? To             { get; set; }
    public string?   Status         { get; set; }
    public int       Page           { get; set; } = 1;
    public int       PageSize       { get; set; } = 25;
}

public sealed class AttendanceRecomputeRequest
{
    public Guid?     EmployeeUuid { get; set; }
    public DateOnly? From         { get; set; }
    public DateOnly? To           { get; set; }
}

public sealed class AttendanceRecomputeResult
{
    public int Employees { get; set; }
    public int Days      { get; set; }
}

public sealed class AttendanceDayOverride
{
    public string? Status       { get; set; }
    public string? OverrideNote { get; set; }
}

public sealed class AttendanceSummary
{
    public int     WorkingDays     { get; set; }
    /// <summary>Attended days: Present and Late. <see cref="LateDays"/> is the part of them that was late.</summary>
    public int     PresentDays     { get; set; }
    public int     LateDays        { get; set; }
    public int     HalfDays        { get; set; }
    public int     AbsentDays      { get; set; }
    public decimal LeaveDays       { get; set; }
    public decimal PaidLeaveDays   { get; set; }
    public decimal UnpaidLeaveDays { get; set; }
    public int     Holidays        { get; set; }
    public int     WeeklyOffs      { get; set; }
    public int     WorkedMinutes   { get; set; }
    public int     OvertimeMinutes { get; set; }
    public int     ApprovedOvertimeMinutes { get; set; }
}

public sealed class OvertimeModel
{
    public Guid      Uuid           { get; set; }
    public Guid      EmployeeUuid   { get; set; }
    public string    EmployeeName   { get; set; } = "";
    public DateOnly  Date           { get; set; }
    public int       Minutes        { get; set; }
    public decimal   Multiplier     { get; set; }
    public string    Status         { get; set; } = "";
    public string?   Reason         { get; set; }
    public string?   DecidedByName  { get; set; }
    public DateTime? DecidedAt      { get; set; }
    public string?   DecisionNote   { get; set; }
    public bool      Paid           { get; set; }
}

public sealed class OvertimeCreate
{
    public Guid      EmployeeUuid { get; set; }
    public DateOnly? Date         { get; set; }
    public int?      Minutes      { get; set; }
    public decimal?  Multiplier   { get; set; }
    public string?   Reason       { get; set; }
}

public sealed class DecisionNoteRequest { public string? Note { get; set; } }

public sealed class LeavePolicyModel
{
    public Guid      Uuid          { get; set; }
    public Guid?     GradeUuid     { get; set; }
    public decimal   Entitlement   { get; set; }
    public string    AccrualMethod { get; set; } = "";
    public DateOnly  EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo   { get; set; }
}

public sealed class LeavePolicyUpsert
{
    public Guid?     GradeUuid     { get; set; }
    public decimal   Entitlement   { get; set; }
    public string?   AccrualMethod { get; set; }
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo   { get; set; }
}

public sealed class LeaveTypeModel
{
    public Guid     Uuid                 { get; set; }
    public string   Code                 { get; set; } = "";
    public string   Name                 { get; set; } = "";
    public bool     IsPaid               { get; set; }
    public decimal? CarryForwardCap      { get; set; }
    public int?     AttachmentAfterDays  { get; set; }
    public string?  GenderRestriction    { get; set; }
    public bool     AllowNegativeBalance { get; set; }
    public bool     IsActive             { get; set; }
    public List<LeavePolicyModel> Policies { get; set; } = [];
}

public sealed class LeaveTypeUpsert
{
    public string?  Code                 { get; set; }
    public string?  Name                 { get; set; }
    public bool     IsPaid               { get; set; } = true;
    public decimal? CarryForwardCap      { get; set; }
    public int?     AttachmentAfterDays  { get; set; }
    public string?  GenderRestriction    { get; set; }
    public bool     AllowNegativeBalance { get; set; }
}

public sealed class LeaveBalanceModel
{
    public Guid    LeaveTypeUuid { get; set; }
    public string  LeaveTypeCode { get; set; } = "";
    public string  LeaveTypeName { get; set; } = "";
    public decimal Allocated     { get; set; }
    public decimal Used          { get; set; }
    public decimal Carried       { get; set; }
    public decimal Adjusted      { get; set; }
    public decimal Balance       { get; set; }
    public decimal Pending       { get; set; }
}

public sealed class LeaveLedgerModel
{
    public Guid     Uuid          { get; set; }
    public Guid     LeaveTypeUuid { get; set; }
    public string   LeaveTypeCode { get; set; } = "";
    public decimal  Delta         { get; set; }
    public string   Reason        { get; set; } = "";
    public DateOnly Date          { get; set; }
    public int      Year          { get; set; }
    public Guid?    ReferenceUuid { get; set; }
    public string?  Note          { get; set; }
}

public sealed class LeaveAllocateRequest
{
    public Guid    EmployeeUuid  { get; set; }
    public Guid    LeaveTypeUuid { get; set; }
    public decimal Days          { get; set; }
    public int     Year          { get; set; }
    public string? Note          { get; set; }
    /// <summary>Change a quota already given: an Adjustment entry, positive or negative. Without it a second allocation is 409 LEAVE_ALREADY_ALLOCATED.</summary>
    public bool    Adjustment    { get; set; }
}

public sealed class LeaveBulkAllocateRequest
{
    public Guid LeaveTypeUuid { get; set; }
    public int  Year          { get; set; }
}

public sealed class LeaveBulkAllocateResult
{
    public int Allocated { get; set; }
    public int Skipped   { get; set; }
}

public sealed class LeaveCarryForwardRequest
{
    public int FromYear { get; set; }
    public int ToYear   { get; set; }
}

public sealed class LeaveCarryForwardResult
{
    /// <summary>Employee and leave type pairs that had a balance to close.</summary>
    public int     Processed { get; set; }
    /// <summary>Days carried into the new year.</summary>
    public decimal Carried   { get; set; }
    /// <summary>Days that lapsed.</summary>
    public decimal Expired   { get; set; }
}

public sealed class LeaveRequestModel
{
    public Guid      Uuid           { get; set; }
    public string    RequestNumber  { get; set; } = "";
    public Guid      EmployeeUuid   { get; set; }
    public string    EmployeeName   { get; set; } = "";
    public Guid      LeaveTypeUuid  { get; set; }
    public string    LeaveTypeName  { get; set; } = "";
    public DateOnly  FromDate       { get; set; }
    public DateOnly  ToDate         { get; set; }
    public bool      HalfDay        { get; set; }
    public decimal   Days           { get; set; }
    public string?   Reason         { get; set; }
    public string    Status         { get; set; } = "";
    public Guid?     AttachmentUuid { get; set; }
    public Guid?     ApprovalUuid   { get; set; }
    public string?   DecidedByName  { get; set; }
    public DateTime? DecidedAt      { get; set; }
    public string?   DecisionNote   { get; set; }
}

public sealed class LeaveRequestCreate
{
    public Guid?     EmployeeUuid   { get; set; }
    public Guid      LeaveTypeUuid  { get; set; }
    public DateOnly? FromDate       { get; set; }
    public DateOnly? ToDate         { get; set; }
    public bool      HalfDay        { get; set; }
    public string?   Reason         { get; set; }
    public Guid?     AttachmentUuid { get; set; }
}

public sealed class LeaveRequestFilter
{
    public string?   Status       { get; set; }
    public Guid?     EmployeeUuid { get; set; }
    public DateOnly? From         { get; set; }
    public DateOnly? To           { get; set; }
    public int       Page         { get; set; } = 1;
    public int       PageSize     { get; set; } = 25;
}

/// <summary>The team view: names and dates only, never the leave type or the reason.</summary>
public sealed class LeaveCalendarEntry
{
    public Guid     EmployeeUuid { get; set; }
    public string   EmployeeName { get; set; } = "";
    public DateOnly FromDate     { get; set; }
    public DateOnly ToDate       { get; set; }
    public bool     HalfDay      { get; set; }
}
