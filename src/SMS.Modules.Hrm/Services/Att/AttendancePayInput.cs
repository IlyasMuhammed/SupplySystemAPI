using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Hrm.Services.Att;

/// <summary>One approved, not yet paid overtime record. Pay = hours × hourly rate × <see cref="Multiplier"/>.</summary>
internal sealed record ApprovedOvertime(DateOnly Date, int Minutes, decimal Multiplier);

/// <summary>
/// API-CONTRACT §13, task 130 and D-ATT-3.
/// <para><see cref="WorkingDays"/> and <see cref="PaidDays"/> are on the payroll's own basis: WorkingDays is the divisor
/// (HrmSettings.WorkingDaysDivisor, else the calendar days of the range), so <c>BASIC × PaidDays / WorkingDays</c> is always the right pro rata,
/// and a full month pays exactly BASIC. Weekly offs and holidays are paid. They are not the count of days at work: that is
/// <see cref="ScheduledWorkingDays"/> and <see cref="WorkedDays"/>.</para>
/// <para><see cref="AttendanceTracked"/> false: attendance is off, or this employee has no punch and no override in the range. Only unpaid
/// leave and days outside employment then reduce pay, so an organization not using attendance is not paid zero. PAY should show it on the line.</para>
/// <para>Overtime includes approved records from before the range that no run has paid: approval can come after the month closed.</para>
/// </summary>
internal sealed record AttendancePayData(
    int EmployeeId, decimal WorkingDays, decimal PaidDays, decimal WorkedDays, decimal UnpaidDays, int ScheduledWorkingDays,
    int ApprovedOvertimeMinutes, decimal WeightedOvertimeMinutes, IReadOnlyList<ApprovedOvertime> Overtime, bool AttendanceTracked)
{
    /// <summary>The OT_HOURS payroll variable, multipliers applied.</summary>
    public decimal WeightedOvertimeHours => Math.Round(WeightedOvertimeMinutes / 60m, 4);
}

/// <summary>What payroll reads from attendance. ATT defines it, PAY consumes it. Organization explicit; no scope check (the run has done it).</summary>
internal interface IAttendancePayInput
{
    Task<AttendancePayData> GetAsync(Guid organizationId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>The same for a whole run at one read per table. Every requested employee is in the result.</summary>
    Task<IReadOnlyDictionary<int, AttendancePayData>> GetManyAsync(Guid organizationId, IReadOnlyCollection<int> employeeIds, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>Stamps the overtime <see cref="GetAsync"/> reported as paid by this run, so it cannot be paid twice. Call in the run's Finalize. Returns the rows stamped.</summary>
    Task<int> MarkOvertimePaidAsync(Guid organizationId, int runId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>A reversed run gives its overtime back to the next one. Returns the rows released.</summary>
    Task<int> ReleaseOvertimeAsync(Guid organizationId, int runId, CancellationToken ct = default);
}

internal sealed class AttendancePayInput : IAttendancePayInput
{
    private readonly HrmDbContext _db;
    private readonly IAttendanceClock _clock;
    private readonly IModuleGate? _gate;

    public AttendancePayInput(HrmDbContext db, IAttendanceClock clock, IModuleGate? gate = null)
    {
        _db = db;
        _clock = clock;
        _gate = gate;
    }

    public async Task<AttendancePayData> GetAsync(Guid organizationId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        (await GetManyAsync(organizationId, [employeeId], from, to, ct))[employeeId];

    private IQueryable<OvertimeRecord> Payable(Guid org, IReadOnlyCollection<int> ids, DateOnly to) =>
        _db.OvertimeRecords.IgnoreQueryFilters().Where(o => o.OrganizationId == org && ids.Contains(o.EmployeeId)
            && o.Status == ApprovalStatuses.Approved && o.PaidInRunId == null && o.Date <= to);

    public async Task<IReadOnlyDictionary<int, AttendancePayData>> GetManyAsync(Guid organizationId, IReadOnlyCollection<int> employeeIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var org = organizationId;
        var ids = employeeIds.Distinct().ToList();
        var divisorSetting = await _db.Settings.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == org).Select(s => s.WorkingDaysDivisor).FirstOrDefaultAsync(ct);
        var calendarDays = to.DayNumber - from.DayNumber + 1;
        decimal basis = divisorSetting is > 0 ? divisorSetting.Value : calendarDays;

        var attendanceOn = _gate is null || await _gate.IsEnabledAsync(org, HrmFeatureCodes.Attendance, ct);
        var data = await AttendanceData.LoadAsync(_db, _clock, org, ids, from, to, ct);
        var overtime = (await Payable(org, ids, to).AsNoTracking().Select(o => new { o.EmployeeId, o.Date, o.Minutes, o.Multiplier }).ToListAsync(ct))
            .ToLookup(o => o.EmployeeId);

        var result = new Dictionary<int, AttendancePayData>();
        foreach (var id in ids)
        {
            var tracked = attendanceOn && data.HasAttendanceData(id);
            var employedDays = 0;
            var scheduled = 0;
            decimal unpaid = 0m, worked = 0m;
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                var hasOverride = data.OverrideOn(id, date) is not null;
                if (!data.IsEmployed(id, date) && !hasOverride) continue;
                employedDays++;
                var isWorkingDay = data.IsWorkingDay(id, date);
                if (isWorkingDay) scheduled++;
                var leave = data.LeaveOn(id, date);

                if (tracked)
                {
                    // Days still ahead (the run may be before month end) are paid: nobody has missed them yet.
                    if (data.Compute(id, date) is { Status: { } status })
                    {
                        if (isWorkingDay || hasOverride) unpaid += DayPay.UnpaidDays(status, leave);
                        worked += DayPay.WorkedDays(status, leave);
                    }
                    else if (isWorkingDay && leave is { IsPaid: false })
                    {
                        unpaid += leave.HalfDay ? 0.5m : 1m;
                    }
                }
                else if (isWorkingDay)
                {
                    if (leave is { IsPaid: false }) unpaid += leave.HalfDay ? 0.5m : 1m;
                    worked += leave is null ? 1m : leave.HalfDay ? 0.5m : 0m;
                }
            }

            var entitled = basis * employedDays / calendarDays;
            var paid = Math.Round(Math.Clamp(entitled - unpaid, 0m, basis), 2);
            var lines = overtime[id].OrderBy(o => o.Date).Select(o => new ApprovedOvertime(o.Date, o.Minutes, o.Multiplier)).ToList();
            result[id] = new AttendancePayData(id, basis, paid, worked, unpaid, scheduled, lines.Sum(o => o.Minutes), lines.Sum(o => o.Minutes * o.Multiplier), lines, tracked);
        }
        return result;
    }

    public async Task<int> MarkOvertimePaidAsync(Guid organizationId, int runId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await Payable(organizationId, [employeeId], to).ToListAsync(ct);
        foreach (var row in rows) row.PaidInRunId = runId;
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }

    public async Task<int> ReleaseOvertimeAsync(Guid organizationId, int runId, CancellationToken ct = default)
    {
        var rows = await _db.OvertimeRecords.IgnoreQueryFilters().Where(o => o.OrganizationId == organizationId && o.PaidInRunId == runId).ToListAsync(ct);
        foreach (var row in rows) row.PaidInRunId = null;
        await _db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
