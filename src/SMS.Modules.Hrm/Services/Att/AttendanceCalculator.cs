using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Hrm.Services.Att;

/// <summary>What "now" is, and how UTC events map to the local day of an organization (D-ATT-1). Replaceable in tests.</summary>
internal interface IAttendanceClock
{
    DateTime UtcNow { get; }
    Task<TimeZoneInfo> ZoneForAsync(Guid organizationId, string? countryCode, CancellationToken ct = default);
}

internal sealed class AttendanceClock : IAttendanceClock
{
    private readonly IOrganizationTimeZoneLookup? _zones;
    private readonly ILogger<AttendanceClock>? _log;

    public AttendanceClock(IOrganizationTimeZoneLookup? zones = null, ILogger<AttendanceClock>? log = null)
    {
        _zones = zones;
        _log = log;
    }

    public DateTime UtcNow => DateTime.UtcNow;

    /// <summary>The organization's stored zone. It is free text nobody validates, so an unusable value falls back to the country's zone, with a warning: late and early minutes would otherwise be wrong with no sign of it.</summary>
    public async Task<TimeZoneInfo> ZoneForAsync(Guid organizationId, string? countryCode, CancellationToken ct = default)
    {
        var stored = _zones is null ? null : await _zones.GetTimeZoneAsync(organizationId, ct);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(stored.Trim(), out var zone)) return zone;
            _log?.LogWarning("Organization {OrganizationId} has an unknown time zone '{TimeZone}'; attendance uses the default for country {Country}.", organizationId, stored, countryCode);
        }
        return CountryDefault(countryCode);
    }

    private static TimeZoneInfo CountryDefault(string? countryCode) => string.Equals(countryCode, "PK", StringComparison.OrdinalIgnoreCase) ? Karachi.Value : TimeZoneInfo.Utc;

    private static readonly Lazy<TimeZoneInfo> Karachi = new(() =>
    {
        foreach (var id in new[] { "Asia/Karachi", "Pakistan Standard Time" })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) return zone;
        return TimeZoneInfo.CreateCustomTimeZone("PKT", TimeSpan.FromHours(5), "Pakistan", "Pakistan");
    });
}

/// <summary>Shift arithmetic (task 108).</summary>
internal static class ShiftMath
{
    public static bool IsOvernight(Shift s) => s.EndTime <= s.StartTime;

    public static int SpanMinutes(Shift s)
    {
        var span = (int)(s.EndTime.ToTimeSpan() - s.StartTime.ToTimeSpan()).TotalMinutes;
        return span <= 0 ? span + 24 * 60 : span;
    }

    /// <summary>The working minutes of the shift: its length less the break.</summary>
    public static int TotalMinutes(Shift s) => Math.Max(0, SpanMinutes(s) - s.BreakMinutes);

    public static int FlagOf(DayOfWeek day) => 1 << (int)day;
    public static bool IsWeeklyOff(Shift s, DateOnly date) => (s.WeeklyOffDays & FlagOf(date.DayOfWeek)) != 0;

    public static DateTime StartOn(Shift s, DateOnly date) => date.ToDateTime(s.StartTime);
    public static DateTime EndOn(Shift s, DateOnly date) => (IsOvernight(s) ? date.AddDays(1) : date).ToDateTime(s.EndTime);
}

internal sealed record Punch(DateTime Utc, DateTime Local, string Type);

/// <summary>An approved leave on a date.</summary>
internal sealed record DayLeave(bool IsPaid, bool HalfDay);

internal sealed record DayFacts(Shift? Shift, DateOnly Date, bool IsHoliday, bool IsWeeklyOff, DayLeave? Leave, IReadOnlyList<Punch> Punches, bool InProgress);

/// <summary><see cref="Status"/> null: nothing to say yet (today, no punch so far).</summary>
internal sealed record DayOutcome(DateTime? FirstInUtc, DateTime? LastOutUtc, int WorkedMinutes, int LateMinutes, int EarlyMinutes, int OvertimeMinutes, string? Status);

/// <summary>Tasks 114–120: one day from its facts. Pure, so every rule is tested without a database.</summary>
internal static class AttendanceCalculator
{
    public static DayOutcome Compute(DayFacts f)
    {
        var ordered = f.Punches.OrderBy(p => p.Utc).ToList();
        var first = ordered.FirstOrDefault(p => p.Type != AttendanceEventTypes.Out);
        var last = ordered.LastOrDefault(p => p.Type != AttendanceEventTypes.In && (first is null || p.Utc > first.Utc));

        var span = first is not null && last is not null ? (int)(last.Utc - first.Utc).TotalMinutes : 0;
        var breakMinutes = f.Shift?.BreakMinutes ?? 0;
        var worked = span > breakMinutes ? span - breakMinutes : span;

        DayOutcome Result(int late, int early, int overtime, string? status) => new(first?.Utc, last?.Utc, worked, late, early, overtime, status);

        // Task 120: no late, early or absence on a day nobody was expected. Time worked anyway is all overtime, once past the threshold.
        if (f.IsHoliday || f.IsWeeklyOff)
        {
            var threshold = f.Shift?.OvertimeThresholdMinutes ?? 0;
            return Result(0, 0, f.Shift is not null && worked > threshold ? worked : 0, f.IsHoliday ? AttendanceDayStatuses.Holiday : AttendanceDayStatuses.WeeklyOff);
        }

        if (f.Leave is { HalfDay: false }) return Result(0, 0, 0, AttendanceDayStatuses.OnLeave);

        if (ordered.Count == 0)
        {
            if (f.Leave is not null) return Result(0, 0, 0, AttendanceDayStatuses.OnLeave);
            return Result(0, 0, 0, f.InProgress ? null : AttendanceDayStatuses.Absent);
        }

        if (f.Shift is null) return Result(0, 0, 0, AttendanceDayStatuses.Present);

        // A half-day leave can be either half, so neither lateness nor a short day is held against the employee.
        if (f.Leave is not null) return Result(0, 0, 0, AttendanceDayStatuses.Present);

        var late = 0;
        if (first is not null)
        {
            var minutesAfterStart = (int)(first.Local - ShiftMath.StartOn(f.Shift, f.Date)).TotalMinutes;
            if (minutesAfterStart > f.Shift.GraceInMinutes) late = minutesAfterStart;
        }

        var early = 0;
        if (last is not null)
        {
            var minutesBeforeEnd = (int)(ShiftMath.EndOn(f.Shift, f.Date) - last.Local).TotalMinutes;
            if (minutesBeforeEnd > f.Shift.GraceOutMinutes) early = minutesBeforeEnd;
        }

        var beyond = worked - ShiftMath.TotalMinutes(f.Shift);
        var overtime = beyond > 0 && beyond > f.Shift.OvertimeThresholdMinutes ? beyond : 0;

        string status;
        if (f.InProgress && last is null) status = late > 0 ? AttendanceDayStatuses.Late : AttendanceDayStatuses.Present;
        else if (f.Shift.HalfDayMinutes > 0 && worked < f.Shift.HalfDayMinutes) status = AttendanceDayStatuses.HalfDay;
        else status = late > 0 ? AttendanceDayStatuses.Late : AttendanceDayStatuses.Present;

        return Result(late, early, overtime, status);
    }

    /// <summary>
    /// The work date of a punch. Normally its local date; a punch in the early hours belongs to yesterday when yesterday's shift runs
    /// overnight and it falls within four hours of that shift's end (a late clock-out is still that shift).
    /// </summary>
    public static DateOnly WorkDateOf(DateTime local, Func<DateOnly, Shift?> shiftOn)
    {
        var date = DateOnly.FromDateTime(local);
        var yesterday = date.AddDays(-1);
        if (shiftOn(yesterday) is { } s && ShiftMath.IsOvernight(s) && local <= ShiftMath.EndOn(s, yesterday).AddHours(4)) return yesterday;
        return date;
    }
}

/// <summary>What pay needs to know about one day's outcome, in days. Half-day leave is half unpaid only when the other half was missed or the leave is unpaid.</summary>
internal static class DayPay
{
    public static decimal UnpaidDays(string status, DayLeave? leave) => status switch
    {
        AttendanceDayStatuses.Absent => 1m,
        AttendanceDayStatuses.HalfDay => 0.5m,
        AttendanceDayStatuses.OnLeave => leave switch
        {
            null => 0m,
            { HalfDay: true, IsPaid: true } => 0.5m,
            { HalfDay: true, IsPaid: false } => 1m,
            { IsPaid: true } => 0m,
            _ => 1m
        },
        AttendanceDayStatuses.Present or AttendanceDayStatuses.Late => leave is { HalfDay: true, IsPaid: false } ? 0.5m : 0m,
        _ => 0m
    };

    public static decimal WorkedDays(string status, DayLeave? leave) => status switch
    {
        AttendanceDayStatuses.Present or AttendanceDayStatuses.Late => leave is { HalfDay: true } ? 0.5m : 1m,
        AttendanceDayStatuses.HalfDay => 0.5m,
        _ => 0m
    };
}

/// <summary>
/// Everything the day computation reads, loaded once for a set of employees and a range. Used by recomputation, the monthly summary,
/// the leave day count and the pay seam, so they cannot disagree. Organization explicit; no scope check (the caller has done it).
/// </summary>
internal sealed class AttendanceData
{
    private sealed record RecordRow(int EmployeeId, DateOnly From, DateOnly? To, int? ShiftId, int BranchId);
    private sealed record EmployeeRow(int Id, DateOnly? JoinDate, DateOnly? LeaveDate, string Status);
    private sealed record LeaveRow(int EmployeeId, DateOnly From, DateOnly To, bool HalfDay, bool IsPaid);
    internal sealed record OverrideRow(int EmployeeId, DateOnly Date, int? ShiftId, DateTime? FirstInUtc, DateTime? LastOutUtc, int Worked, int Late, int Early, int Overtime, string Status);

    private readonly Dictionary<int, List<RecordRow>> _records;
    private readonly Dictionary<int, EmployeeRow> _employees;
    private readonly Dictionary<int, Shift> _shifts;
    private readonly Shift? _defaultShift;
    private readonly HashSet<(DateOnly Date, int? Branch)> _holidays;
    private readonly ILookup<int, LeaveRow> _leaves;
    private readonly Dictionary<(int, DateOnly), OverrideRow> _overrides;
    private readonly Dictionary<(int, DateOnly), List<Punch>> _punches = [];
    private readonly HashSet<int> _withPunches = [];

    public DateOnly LocalToday { get; }

    private AttendanceData(List<RecordRow> records, List<EmployeeRow> employees, List<Shift> shifts, HashSet<(DateOnly, int?)> holidays, List<LeaveRow> leaves, List<OverrideRow> overrides, DateOnly localToday)
    {
        _records = records.GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());
        _employees = employees.ToDictionary(e => e.Id);
        _shifts = shifts.ToDictionary(s => s.Id);
        _defaultShift = shifts.FirstOrDefault(s => s.IsDefault && s.IsActive);
        _holidays = holidays;
        _leaves = leaves.ToLookup(l => l.EmployeeId);
        _overrides = overrides.ToDictionary(o => (o.EmployeeId, o.Date));
        LocalToday = localToday;
    }

    public static async Task<AttendanceData> LoadAsync(HrmDbContext db, IAttendanceClock clock, Guid org, IReadOnlyCollection<int> employeeIds, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var ids = employeeIds.Distinct().ToList();
        var country = await db.Settings.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == org).Select(s => s.CountryCode).FirstOrDefaultAsync(ct) ?? "PK";
        var zone = await clock.ZoneForAsync(org, country, ct);
        var margin = from.AddDays(-2);

        var records = await db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && ids.Contains(r.EmployeeId) && r.EffectiveFrom <= to && (r.EffectiveTo == null || r.EffectiveTo >= margin))
            .Select(r => new RecordRow(r.EmployeeId, r.EffectiveFrom, r.EffectiveTo, r.ShiftId, r.BranchId)).ToListAsync(ct);
        var employees = await db.Employees.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == org && ids.Contains(e.Id))
            .Select(e => new EmployeeRow(e.Id, e.JoinDate, e.LeaveDate, e.Status)).ToListAsync(ct);
        var shifts = await db.Shifts.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == org).ToListAsync(ct);
        var holidayRows = await db.Holidays.IgnoreQueryFilters().AsNoTracking()
            .Where(h => h.OrganizationId == org && h.Date >= from && h.Date <= to).Select(h => new { h.Date, h.BranchId }).ToListAsync(ct);
        var leaves = await db.LeaveRequests.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == org && ids.Contains(l.EmployeeId) && l.Status == LeaveRequestStatuses.Approved && l.FromDate <= to && l.ToDate >= from)
            .Select(l => new LeaveRow(l.EmployeeId, l.FromDate, l.ToDate, l.HalfDay, l.LeaveType!.IsPaid)).ToListAsync(ct);
        var overrides = await db.AttendanceDays.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == org && ids.Contains(d.EmployeeId) && d.IsManualOverride && d.Date >= from && d.Date <= to)
            .Select(d => new OverrideRow(d.EmployeeId, d.Date, d.ShiftId, d.FirstInUtc, d.LastOutUtc, d.WorkedMinutes, d.LateMinutes, d.EarlyMinutes, d.OvertimeMinutes, d.Status)).ToListAsync(ct);

        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(clock.UtcNow, DateTimeKind.Utc), zone));
        var data = new AttendanceData(records, employees, shifts, holidayRows.Select(h => (h.Date, h.BranchId)).ToHashSet(), leaves, overrides, localToday);

        var eventsFrom = from.AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var eventsTo = to.AddDays(3).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var events = await db.AttendanceEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == org && ids.Contains(e.EmployeeId) && e.EventTimeUtc >= eventsFrom && e.EventTimeUtc < eventsTo)
            .Select(e => new { e.EmployeeId, e.EventTimeUtc, e.EventType }).ToListAsync(ct);
        foreach (var e in events)
        {
            var utc = DateTime.SpecifyKind(e.EventTimeUtc, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
            var workDate = AttendanceCalculator.WorkDateOf(local, d => data.ShiftOn(e.EmployeeId, d));
            if (workDate < from || workDate > to) continue;
            if (!data._punches.TryGetValue((e.EmployeeId, workDate), out var list)) data._punches[(e.EmployeeId, workDate)] = list = [];
            list.Add(new Punch(utc, local, e.EventType));
            data._withPunches.Add(e.EmployeeId);
        }
        return data;
    }

    private RecordRow? RecordOn(int employeeId, DateOnly date) =>
        _records.TryGetValue(employeeId, out var rows) ? rows.FirstOrDefault(r => r.From <= date && (r.To is null || r.To >= date)) : null;

    /// <summary>The record's shift, else the organization's default, else none.</summary>
    public Shift? ShiftOn(int employeeId, DateOnly date) =>
        RecordOn(employeeId, date)?.ShiftId is { } id && _shifts.TryGetValue(id, out var shift) ? shift : _defaultShift;

    public bool IsEmployed(int employeeId, DateOnly date)
    {
        if (!_employees.TryGetValue(employeeId, out var e) || e.Status == EmployeeStatuses.Candidate) return false;
        if (e.JoinDate is { } join && date < join) return false;
        if (e.LeaveDate is { } leave && date > leave) return false;
        return RecordOn(employeeId, date) is not null;
    }

    public bool IsHoliday(int employeeId, DateOnly date) =>
        _holidays.Contains((date, null)) || (RecordOn(employeeId, date) is { } r && _holidays.Contains((date, r.BranchId)));

    public bool IsWeeklyOff(int employeeId, DateOnly date) => ShiftOn(employeeId, date) is { } s && ShiftMath.IsWeeklyOff(s, date);

    /// <summary>Expected at work: employed, not a holiday, not a weekly off.</summary>
    public bool IsWorkingDay(int employeeId, DateOnly date) => IsEmployed(employeeId, date) && !IsHoliday(employeeId, date) && !IsWeeklyOff(employeeId, date);

    public DayLeave? LeaveOn(int employeeId, DateOnly date)
    {
        var row = _leaves[employeeId].FirstOrDefault(l => l.From <= date && l.To >= date);
        return row is null ? null : new DayLeave(row.IsPaid, row.HalfDay);
    }

    public bool HasAttendanceData(int employeeId) => _withPunches.Contains(employeeId) || _overrides.Keys.Any(k => k.Item1 == employeeId);

    public OverrideRow? OverrideOn(int employeeId, DateOnly date) => _overrides.GetValueOrDefault((employeeId, date));

    /// <summary>The day, or null when it does not exist yet: not employed, in the future, or today with nothing to say. A manual override wins.</summary>
    public DayOutcome? Compute(int employeeId, DateOnly date)
    {
        if (OverrideOn(employeeId, date) is { } o) return new DayOutcome(o.FirstInUtc, o.LastOutUtc, o.Worked, o.Late, o.Early, o.Overtime, o.Status);
        if (date > LocalToday || !IsEmployed(employeeId, date)) return null;
        var outcome = AttendanceCalculator.Compute(new DayFacts(
            ShiftOn(employeeId, date), date, IsHoliday(employeeId, date), IsWeeklyOff(employeeId, date), LeaveOn(employeeId, date),
            _punches.TryGetValue((employeeId, date), out var punches) ? punches : [], date == LocalToday));
        return outcome.Status is null ? null : outcome;
    }
}
