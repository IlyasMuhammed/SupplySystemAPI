using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Hrm.Services.Att;

// API-CONTRACT §6.2, §6.3.

public interface IAttendanceService
{
    Task<AttendanceEventResult> RecordEventAsync(HrmCaller caller, AttendanceEventRequest request, CancellationToken ct = default);
    Task<AttendanceEventResult> RecordOwnEventAsync(HrmCaller caller, AttendanceSelfEventRequest request, CancellationToken ct = default);
    /// <summary>CSV: employeeNumber,eventTimeUtc,eventType,externalEventId. A bad row is reported, the rest still load; re-importing a file changes nothing.</summary>
    Task<AttendanceImportResult> ImportEventsAsync(HrmCaller caller, Stream csv, CancellationToken ct = default);
    Task<PaginatedResponse<AttendanceDayModel>> ListDaysAsync(HrmCaller caller, AttendanceDayFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<AttendanceDayModel>> OwnDaysAsync(HrmCaller caller, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<AttendanceRecomputeResult> RecomputeAsync(HrmCaller caller, AttendanceRecomputeRequest request, CancellationToken ct = default);
    Task<AttendanceDayModel> OverrideDayAsync(HrmCaller caller, Guid uuid, AttendanceDayOverride request, CancellationToken ct = default);
    Task<AttendanceSummary> GetMonthlySummaryAsync(HrmCaller caller, Guid? employeeUuid, int year, int month, CancellationToken ct = default);
}

public interface IOvertimeService
{
    Task<IReadOnlyList<OvertimeModel>> ListAsync(HrmCaller caller, string? status, DateOnly? from, DateOnly? to, Guid? employeeUuid, CancellationToken ct = default);
    Task<OvertimeModel> SubmitAsync(HrmCaller caller, OvertimeCreate request, CancellationToken ct = default);
    Task<OvertimeModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<OvertimeModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default);
}

/// <summary>Writes hrm.AttendanceDays from the events. Takes no workflow dependency: the leave status handler uses it.</summary>
internal interface IAttendanceRecalculator
{
    /// <summary>Dates after today are ignored; a manual override is never touched. <paramref name="onlyMissing"/>: only dates with no row. Returns rows written.</summary>
    Task<int> RecomputeAsync(Guid organizationId, IReadOnlyCollection<int> employeeIds, DateOnly from, DateOnly to, bool onlyMissing = false, CancellationToken ct = default);
}

internal sealed class AttendanceRecalculator : IAttendanceRecalculator
{
    private readonly HrmDbContext _db;
    private readonly IAttendanceClock _clock;

    public AttendanceRecalculator(HrmDbContext db, IAttendanceClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<int> RecomputeAsync(Guid organizationId, IReadOnlyCollection<int> employeeIds, DateOnly from, DateOnly to, bool onlyMissing = false, CancellationToken ct = default)
    {
        if (employeeIds.Count == 0 || from > to) return 0;
        try
        {
            return await WriteAsync(organizationId, employeeIds, from, to, onlyMissing, ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
                                        || ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Another request computed one of these days first: drop what failed and write over what it wrote.
            foreach (var entry in _db.ChangeTracker.Entries<AttendanceDay>().Where(e => e.State == EntityState.Added).ToList()) entry.State = EntityState.Detached;
            return await WriteAsync(organizationId, employeeIds, from, to, onlyMissing, ct);
        }
    }

    private async Task<int> WriteAsync(Guid org, IReadOnlyCollection<int> employeeIds, DateOnly from, DateOnly to, bool onlyMissing, CancellationToken ct)
    {
        var data = await AttendanceData.LoadAsync(_db, _clock, org, employeeIds, from, to, ct);
        if (to > data.LocalToday) to = data.LocalToday;
        if (from > to) return 0;

        var ids = employeeIds.Distinct().ToList();
        var existing = await _db.AttendanceDays.IgnoreQueryFilters()
            .Where(d => d.OrganizationId == org && ids.Contains(d.EmployeeId) && d.Date >= from && d.Date <= to).ToListAsync(ct);
        var byKey = existing.ToDictionary(d => (d.EmployeeId, d.Date));

        var written = 0;
        foreach (var employeeId in ids)
        {
            for (var date = from; date <= to; date = date.AddDays(1))
            {
                byKey.TryGetValue((employeeId, date), out var row);
                if (row is { IsManualOverride: true } || (onlyMissing && row is not null)) continue;

                var outcome = data.Compute(employeeId, date);
                if (outcome is null)
                {
                    if (row is not null) _db.AttendanceDays.Remove(row);
                    continue;
                }
                if (row is null)
                {
                    row = new AttendanceDay { OrganizationId = org, EmployeeId = employeeId, Date = date };
                    _db.AttendanceDays.Add(row);
                }
                row.ShiftId = data.ShiftOn(employeeId, date)?.Id;
                row.FirstInUtc = outcome.FirstInUtc;
                row.LastOutUtc = outcome.LastOutUtc;
                row.WorkedMinutes = outcome.WorkedMinutes;
                row.LateMinutes = outcome.LateMinutes;
                row.EarlyMinutes = outcome.EarlyMinutes;
                row.OvertimeMinutes = outcome.OvertimeMinutes;
                row.Status = outcome.Status!;
                row.ComputedAt = _clock.UtcNow;
                written++;
            }
        }
        await _db.SaveChangesAsync(ct);
        return written;
    }
}

internal sealed class AttendanceService : HrmServiceBase, IAttendanceService
{
    private const int MaxRangeDays = 92, MaxImportRows = 50_000;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

    private readonly IHrmScopeService _scope;
    private readonly IAttendanceRecalculator _recalc;
    private readonly IAttendanceClock _clock;

    public AttendanceService(HrmDbContext db, IHrmScopeService scope, IAttendanceRecalculator recalc, IAttendanceClock clock) : base(db)
    {
        _scope = scope;
        _recalc = recalc;
        _clock = clock;
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    /// <summary>Punch day and the day before: an early-hours punch may belong to last night's shift.</summary>
    private Task RecomputeAroundAsync(Guid org, int employeeId, DateTime utc, CancellationToken ct)
    {
        var date = DateOnly.FromDateTime(utc);
        return _recalc.RecomputeAsync(org, [employeeId], date.AddDays(-1), date.AddDays(1), ct: ct);
    }

    private async Task<AttendanceEventResult> AddEventAsync(HrmCaller caller, int employeeId, DateTime utc, string type, string source, string? externalId, string? note, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        if (externalId is not null)
        {
            var known = await FindExternalAsync(org, source, externalId, ct);
            if (known is { } uuid) return new AttendanceEventResult { Uuid = uuid, Duplicate = true };
        }
        var row = new AttendanceEvent { EmployeeId = employeeId, EventTimeUtc = utc, EventType = type, Source = source, ExternalEventId = externalId, Note = note };
        Stamp(row, caller, isNew: true);
        Db.AttendanceEvents.Add(row);
        try
        {
            await Db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (externalId is not null && IsDuplicate(ex))
        {
            // The same device event arrived twice at once: the unique index let one through.
            Db.Entry(row).State = EntityState.Detached;
            var uuid = await FindExternalAsync(org, source, externalId, ct);
            if (uuid is null) throw;
            return new AttendanceEventResult { Uuid = uuid.Value, Duplicate = true };
        }
        await RecomputeAroundAsync(org, employeeId, utc, ct);
        return new AttendanceEventResult { Uuid = row.Uuid, Duplicate = false };
    }

    private async Task<Guid?> FindExternalAsync(Guid org, string source, string externalId, CancellationToken ct) =>
        await Db.AttendanceEvents.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == org && e.Source == source && e.ExternalEventId == externalId).Select(e => (Guid?)e.Uuid).FirstOrDefaultAsync(ct);

    public async Task<AttendanceEventResult> RecordEventAsync(HrmCaller caller, AttendanceEventRequest request, CancellationToken ct = default)
    {
        var employee = await _scope.RequireEmployeeAsync(caller, request.EmployeeUuid, ct);
        var utc = ToUtc(request.EventTimeUtc ?? throw new BadRequestException("The event time is required."));
        if (utc > _clock.UtcNow + ClockSkew) throw HrmErrors.Bad("ATTENDANCE_EVENT_FUTURE", "An attendance event cannot be in the future.");
        var type = HrmValidate.OneOf(request.EventType ?? AttendanceEventTypes.Unknown, AttendanceEventTypes.All, "eventType");
        var source = HrmValidate.OneOf(request.Source ?? AttendanceSources.Manual, AttendanceSources.All, "source");
        return await AddEventAsync(caller, employee.Id, utc, type, source,
            HrmValidate.Optional(request.ExternalEventId, 100, "External event id"), HrmValidate.Optional(request.Note, 500, "Note"), ct);
    }

    public async Task<AttendanceEventResult> RecordOwnEventAsync(HrmCaller caller, AttendanceSelfEventRequest request, CancellationToken ct = default)
    {
        var employee = await _scope.RequireOwnEmployeeAsync(caller, ct);
        var type = HrmValidate.OneOf(request.EventType, AttendanceEventTypes.All, "eventType");
        return await AddEventAsync(caller, employee.Id, _clock.UtcNow, type, AttendanceSources.Web, null, null, ct);
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { cells.Add(cell.ToString().Trim()); cell.Clear(); }
            else cell.Append(c);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    private sealed record ImportRow(int EmployeeId, DateTime Utc, string Type, string ExternalId);

    public async Task<AttendanceImportResult> ImportEventsAsync(HrmCaller caller, Stream csv, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var result = new AttendanceImportResult();
        var scope = await _scope.GetAsync(caller, ct);
        // Only people the caller may see: anyone else reads as an unknown number, like a 404.
        var numbers = (await _scope.Apply(Db.Employees.AsNoTracking(), caller, scope).Select(e => new { e.EmployeeNumber, e.Id }).ToListAsync(ct))
            .ToDictionary(e => e.EmployeeNumber, e => e.Id, StringComparer.OrdinalIgnoreCase);

        var rows = new List<ImportRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StreamReader(csv);
        var lineNumber = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (lineNumber == 1 && line.TrimStart('﻿').StartsWith("employeeNumber", StringComparison.OrdinalIgnoreCase)) continue;
            if (++result.Total > MaxImportRows) throw HrmErrors.Bad("ATTENDANCE_IMPORT_TOO_LARGE", $"A file can hold at most {MaxImportRows} rows.");

            void Fail(string message) => result.Errors.Add(new AttendanceImportError { Row = lineNumber, Message = message });
            var cells = SplitCsv(line);
            if (cells.Count < 2) { Fail("Expected employeeNumber,eventTimeUtc,eventType,externalEventId."); continue; }
            if (!numbers.TryGetValue(cells[0], out var employeeId)) { Fail($"Unknown employee number '{cells[0]}'."); continue; }
            if (!DateTimeOffset.TryParse(cells[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            { Fail($"Invalid time '{cells[1]}'."); continue; }
            var utc = time.UtcDateTime;
            if (utc > _clock.UtcNow + ClockSkew) { Fail("The event time is in the future."); continue; }
            var type = cells.Count > 2 && cells[2].Length > 0 ? AttendanceEventTypes.All.FirstOrDefault(t => t.Equals(cells[2], StringComparison.OrdinalIgnoreCase)) : AttendanceEventTypes.Unknown;
            if (type is null) { Fail($"Unknown eventType '{cells[2]}'. Allowed: {string.Join(", ", AttendanceEventTypes.All)}."); continue; }
            // A device file with no event ids still imports once: the id is derived from who and when.
            var externalId = cells.Count > 3 && cells[3].Length > 0 ? cells[3] : $"AUTO:{cells[0].ToUpperInvariant()}:{utc:yyyyMMddHHmmss}";
            if (externalId.Length > 100) { Fail("The external event id is longer than 100 characters."); continue; }

            if (!seen.Add(externalId)) { result.Duplicates++; continue; }
            rows.Add(new ImportRow(employeeId, utc, type, externalId));
        }

        foreach (var chunk in rows.Chunk(500))
        {
            var keys = chunk.Select(r => r.ExternalId).ToList();
            var existing = await Db.AttendanceEvents.IgnoreQueryFilters().AsNoTracking()
                .Where(e => e.OrganizationId == org && e.Source == AttendanceSources.Import && keys.Contains(e.ExternalEventId!)).Select(e => e.ExternalEventId!).ToListAsync(ct);
            var known = existing.ToHashSet(StringComparer.Ordinal);
            var fresh = new List<AttendanceEvent>();
            foreach (var r in chunk)
            {
                if (known.Contains(r.ExternalId)) { result.Duplicates++; continue; }
                var row = new AttendanceEvent { EmployeeId = r.EmployeeId, EventTimeUtc = r.Utc, EventType = r.Type, Source = AttendanceSources.Import, ExternalEventId = r.ExternalId };
                Stamp(row, caller, isNew: true);
                fresh.Add(row);
            }
            Db.AttendanceEvents.AddRange(fresh);
            try
            {
                await Db.SaveChangesAsync(ct);
                result.Imported += fresh.Count;
            }
            catch (DbUpdateException ex) when (IsDuplicate(ex))
            {
                // Two imports of one file at once: fall back to row by row so only the true duplicates drop.
                foreach (var row in fresh) Db.Entry(row).State = EntityState.Detached;
                foreach (var row in fresh)
                {
                    Db.AttendanceEvents.Add(row);
                    try
                    {
                        await Db.SaveChangesAsync(ct);
                        result.Imported++;
                    }
                    catch (DbUpdateException inner) when (IsDuplicate(inner))
                    {
                        Db.Entry(row).State = EntityState.Detached;
                        result.Duplicates++;
                    }
                }
            }
        }

        foreach (var group in rows.GroupBy(r => r.EmployeeId))
            await _recalc.RecomputeAsync(org, [group.Key], DateOnly.FromDateTime(group.Min(r => r.Utc)).AddDays(-1), DateOnly.FromDateTime(group.Max(r => r.Utc)).AddDays(1), ct: ct);
        return result;
    }

    private static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var start = from ?? (to is { } t ? new DateOnly(t.Year, t.Month, 1) : new DateOnly(today.Year, today.Month, 1));
        var end = to ?? start.AddMonths(1).AddDays(-1);
        if (end < start) throw new BadRequestException("The end date is before the start date.");
        if (end.DayNumber - start.DayNumber >= MaxRangeDays) throw new BadRequestException($"A range can cover at most {MaxRangeDays} days.");
        return (start, end);
    }

    private IQueryable<AttendanceDayModel> Project(IQueryable<AttendanceDay> days) => days.Select(d => new AttendanceDayModel
    {
        Uuid = d.Uuid, EmployeeUuid = d.Employee!.Uuid, EmployeeName = d.Employee.FirstName + " " + d.Employee.LastName, Date = d.Date,
        ShiftName = d.Shift == null ? null : d.Shift.Name, FirstInUtc = d.FirstInUtc, LastOutUtc = d.LastOutUtc, WorkedMinutes = d.WorkedMinutes, LateMinutes = d.LateMinutes,
        EarlyMinutes = d.EarlyMinutes, OvertimeMinutes = d.OvertimeMinutes, Status = d.Status, IsManualOverride = d.IsManualOverride, OverrideNote = d.OverrideNote
    });

    public async Task<PaginatedResponse<AttendanceDayModel>> ListDaysAsync(HrmCaller caller, AttendanceDayFilter filter, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var (from, to) = Range(filter.From, filter.To, HrmValidate.Today);
        var status = filter.Status is null ? null : HrmValidate.OneOf(filter.Status, AttendanceDayStatuses.All, "status");
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        var scope = await _scope.GetAsync(caller, ct);
        var employees = _scope.Apply(Db.Employees.AsNoTracking(), caller, scope);
        if (filter.EmployeeUuid is { } employeeUuid) employees = employees.Where(e => e.Uuid == employeeUuid);
        if (filter.DepartmentUuid is { } dep) employees = employees.Where(e => e.EmploymentRecords.Any(r => r.EffectiveFrom <= to && (r.EffectiveTo == null || r.EffectiveTo >= from) && r.Department!.Uuid == dep));
        if (filter.BranchUuid is { } bra) employees = employees.Where(e => e.EmploymentRecords.Any(r => r.EffectiveFrom <= to && (r.EffectiveTo == null || r.EffectiveTo >= from) && r.Branch!.Uuid == bra));
        var ids = await employees.Select(e => e.Id).ToListAsync(ct);

        // A day with no punch has no event to create it: an absence would not exist until someone looked (D-ATT-4).
        await _recalc.RecomputeAsync(org, ids, from, to, onlyMissing: true, ct);

        var query = Db.AttendanceDays.IgnoreQueryFilters().AsNoTracking().Where(d => d.OrganizationId == org && ids.Contains(d.EmployeeId) && d.Date >= from && d.Date <= to);
        if (status is not null) query = query.Where(d => d.Status == status);
        var total = await query.CountAsync(ct);
        var data = await Project(query.OrderByDescending(d => d.Date).ThenBy(d => d.Employee!.EmployeeNumber)).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PaginatedResponse<AttendanceDayModel>
        {
            Data = data, TotalRecords = total, Page = page, PageSize = pageSize, TotalPages = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<IReadOnlyList<AttendanceDayModel>> OwnDaysAsync(HrmCaller caller, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var employee = await _scope.RequireOwnEmployeeAsync(caller, ct);
        var org = caller.OrganizationId;
        var (start, end) = Range(from, to, HrmValidate.Today);
        await _recalc.RecomputeAsync(org, [employee.Id], start, end, onlyMissing: true, ct);
        return await Project(Db.AttendanceDays.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.OrganizationId == org && d.EmployeeId == employee.Id && d.Date >= start && d.Date <= end).OrderBy(d => d.Date)).ToListAsync(ct);
    }

    public async Task<AttendanceRecomputeResult> RecomputeAsync(HrmCaller caller, AttendanceRecomputeRequest request, CancellationToken ct = default)
    {
        var from = request.From ?? throw new BadRequestException("The start date is required.");
        var to = request.To ?? throw new BadRequestException("The end date is required.");
        Range(from, to, HrmValidate.Today);
        List<int> ids;
        if (request.EmployeeUuid is { } uuid) ids = [(await _scope.RequireEmployeeAsync(caller, uuid, ct)).Id];
        else
        {
            var scope = await _scope.GetAsync(caller, ct);
            ids = await _scope.Apply(Db.Employees.AsNoTracking(), caller, scope).Select(e => e.Id).ToListAsync(ct);
        }
        var days = await _recalc.RecomputeAsync(caller.OrganizationId, ids, from, to, ct: ct);
        return new AttendanceRecomputeResult { Employees = ids.Count, Days = days };
    }

    public async Task<AttendanceDayModel> OverrideDayAsync(HrmCaller caller, Guid uuid, AttendanceDayOverride request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await Db.AttendanceDays.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.OrganizationId == org && d.Uuid == uuid, ct)
            ?? throw new NotFoundException("Attendance day", uuid);
        if (!await _scope.CanAccessAsync(caller, row.EmployeeId, ct)) throw new NotFoundException("Attendance day", uuid);
        row.Status = HrmValidate.OneOf(request.Status, AttendanceDayStatuses.All, "status");
        row.OverrideNote = HrmValidate.Name(request.OverrideNote, 500, "A note explaining the change");
        row.IsManualOverride = true;
        Stamp(row, caller);
        await SaveAsync(ct, "This attendance day");
        return await Project(Db.AttendanceDays.IgnoreQueryFilters().AsNoTracking().Where(d => d.Id == row.Id)).FirstAsync(ct);
    }

    public async Task<AttendanceSummary> GetMonthlySummaryAsync(HrmCaller caller, Guid? employeeUuid, int year, int month, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2100 || month is < 1 or > 12) throw new BadRequestException("Give a valid year and month.");
        var org = caller.OrganizationId;
        var employeeId = await ResolveEmployeeAsync(caller, employeeUuid, ct);
        var from = new DateOnly(year, month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var data = await AttendanceData.LoadAsync(Db, _clock, org, [employeeId], from, to, ct);

        var summary = new AttendanceSummary();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            if (data.IsEmployed(employeeId, date))
            {
                if (data.IsWorkingDay(employeeId, date)) summary.WorkingDays++;
            }
            if (data.Compute(employeeId, date) is not { Status: { } status } day) continue;
            summary.WorkedMinutes += day.WorkedMinutes;
            summary.OvertimeMinutes += day.OvertimeMinutes;
            var leave = data.LeaveOn(employeeId, date);
            switch (status)
            {
                case AttendanceDayStatuses.Present: summary.PresentDays++; break;
                case AttendanceDayStatuses.Late: summary.PresentDays++; summary.LateDays++; break;
                case AttendanceDayStatuses.HalfDay: summary.HalfDays++; break;
                case AttendanceDayStatuses.Absent: summary.AbsentDays++; break;
                case AttendanceDayStatuses.Holiday: summary.Holidays++; break;
                case AttendanceDayStatuses.WeeklyOff: summary.WeeklyOffs++; break;
            }
            if (leave is not null && status is not (AttendanceDayStatuses.Holiday or AttendanceDayStatuses.WeeklyOff))
            {
                var days = leave.HalfDay ? 0.5m : 1m;
                summary.LeaveDays += days;
                if (leave.IsPaid) summary.PaidLeaveDays += days;
                else summary.UnpaidLeaveDays += days;
            }
        }
        summary.ApprovedOvertimeMinutes = await Db.OvertimeRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.OrganizationId == org && o.EmployeeId == employeeId && o.Status == ApprovalStatuses.Approved && o.Date >= from && o.Date <= to).SumAsync(o => o.Minutes, ct);
        return summary;
    }

    /// <summary>Own record with self-service; anyone else needs the view code and the scope.</summary>
    private async Task<int> ResolveEmployeeAsync(HrmCaller caller, Guid? employeeUuid, CancellationToken ct)
    {
        var own = await _scope.OwnEmployeeIdAsync(caller, ct);
        if (employeeUuid is null)
        {
            if (own is null) throw new NotFoundException("HRM_NO_EMPLOYEE: Your user is not linked to an employee record.");
            return own.Value;
        }
        var employee = caller.HasAny(PermissionCodes.HRM_ATTENDANCE_VIEW, PermissionCodes.HRM_ATTENDANCE_MANAGE)
            ? await _scope.RequireEmployeeAsync(caller, employeeUuid.Value, ct)
            : await _scope.RequireOwnEmployeeAsync(caller, ct);
        if (employee.Uuid != employeeUuid) throw new ForbiddenException("You can only see your own attendance.");
        return employee.Id;
    }
}

internal sealed class OvertimeService : HrmServiceBase, IOvertimeService
{
    private readonly IHrmScopeService _scope;
    private readonly IAttendanceClock _clock;

    public OvertimeService(HrmDbContext db, IHrmScopeService scope, IAttendanceClock clock) : base(db)
    {
        _scope = scope;
        _clock = clock;
    }

    private IQueryable<OvertimeModel> Project(IQueryable<OvertimeRecord> rows) => rows.Select(o => new OvertimeModel
    {
        Uuid = o.Uuid, EmployeeUuid = o.Employee!.Uuid, EmployeeName = o.Employee.FirstName + " " + o.Employee.LastName, Date = o.Date, Minutes = o.Minutes, Multiplier = o.Multiplier,
        Status = o.Status, Reason = o.Reason, DecidedByName = o.DecidedByName, DecidedAt = o.DecidedAt, DecisionNote = o.DecisionNote, Paid = o.PaidInRunId != null
    });

    private Task<OvertimeModel> ModelAsync(int id, CancellationToken ct) =>
        Project(Db.OvertimeRecords.IgnoreQueryFilters().AsNoTracking().Where(o => o.Id == id)).FirstAsync(ct);

    public async Task<IReadOnlyList<OvertimeModel>> ListAsync(HrmCaller caller, string? status, DateOnly? from, DateOnly? to, Guid? employeeUuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var scope = await _scope.GetAsync(caller, ct);
        var ids = _scope.Apply(Db.Employees.AsNoTracking(), caller, scope);
        if (employeeUuid is { } uuid) ids = ids.Where(e => e.Uuid == uuid);
        var employeeIds = await ids.Select(e => e.Id).ToListAsync(ct);

        var query = Db.OvertimeRecords.IgnoreQueryFilters().AsNoTracking().Where(o => o.OrganizationId == org && employeeIds.Contains(o.EmployeeId));
        if (status is not null)
        {
            var value = HrmValidate.OneOf(status, ApprovalStatuses.All, "status");
            query = query.Where(o => o.Status == value);
        }
        if (from is { } f) query = query.Where(o => o.Date >= f);
        if (to is { } t) query = query.Where(o => o.Date <= t);
        return await Project(query.OrderByDescending(o => o.Date)).Take(500).ToListAsync(ct);
    }

    public async Task<OvertimeModel> SubmitAsync(HrmCaller caller, OvertimeCreate request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var employee = await _scope.RequireEmployeeAsync(caller, request.EmployeeUuid, ct);
        var date = request.Date ?? throw new BadRequestException("The date is required.");
        var data = await AttendanceData.LoadAsync(Db, _clock, org, [employee.Id], date, date, ct);
        if (date > data.LocalToday) throw new BadRequestException("Overtime cannot be recorded for a future date.");

        var minutes = request.Minutes ?? data.Compute(employee.Id, date)?.OvertimeMinutes ?? 0;
        if (minutes is <= 0 or > 24 * 60) throw HrmErrors.Bad("OVERTIME_NO_MINUTES", "Give the overtime minutes (1 to 1440): attendance shows none for that day.");
        var multiplier = request.Multiplier ?? 1m;
        if (multiplier is <= 0 or > 10) throw new BadRequestException("The multiplier must be above 0 and at most 10.");

        if (await Db.OvertimeRecords.IgnoreQueryFilters().AnyAsync(o => o.OrganizationId == org && o.EmployeeId == employee.Id && o.Date == date, ct))
            throw HrmErrors.Conflict("OVERTIME_DUPLICATE", $"Overtime for {date:yyyy-MM-dd} is already recorded for this employee.");

        var row = new OvertimeRecord
        {
            EmployeeId = employee.Id, Date = date, Minutes = minutes, Multiplier = multiplier, Status = ApprovalStatuses.Pending,
            Reason = HrmValidate.Optional(request.Reason, 500, "Reason")
        };
        Stamp(row, caller, isNew: true);
        Db.OvertimeRecords.Add(row);
        try
        {
            await SaveAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            Db.Entry(row).State = EntityState.Detached;
            throw HrmErrors.Conflict("OVERTIME_DUPLICATE", $"Overtime for {date:yyyy-MM-dd} is already recorded for this employee.");
        }
        return await ModelAsync(row.Id, ct);
    }

    private async Task<OvertimeRecord> PendingAsync(HrmCaller caller, Guid uuid, CancellationToken ct)
    {
        var row = await Db.OvertimeRecords.IgnoreQueryFilters().FirstOrDefaultAsync(o => o.OrganizationId == caller.OrganizationId && o.Uuid == uuid, ct)
            ?? throw new NotFoundException("Overtime record", uuid);
        if (!await _scope.CanAccessAsync(caller, row.EmployeeId, ct)) throw new NotFoundException("Overtime record", uuid);
        if (row.Status != ApprovalStatuses.Pending) throw HrmErrors.Conflict("OVERTIME_ALREADY_DECIDED", $"This overtime is already {row.Status.ToLowerInvariant()}.");
        if (await _scope.OwnEmployeeIdAsync(caller, ct) == row.EmployeeId) throw new ForbiddenException("You cannot decide your own overtime.");
        return row;
    }

    private async Task<OvertimeModel> DecideAsync(HrmCaller caller, OvertimeRecord row, string status, string? note, CancellationToken ct)
    {
        row.Status = status;
        row.DecisionNote = note;
        row.DecidedBy = caller.UserIdOrNull;
        row.DecidedByName = caller.UserName;
        row.DecidedAt = _clock.UtcNow;
        Stamp(row, caller);
        await SaveAsync(ct, "This overtime record");
        return await ModelAsync(row.Id, ct);
    }

    public async Task<OvertimeModel> ApproveAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) =>
        await DecideAsync(caller, await PendingAsync(caller, uuid, ct), ApprovalStatuses.Approved, null, ct);

    public async Task<OvertimeModel> RejectAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default)
    {
        var row = await PendingAsync(caller, uuid, ct);
        return await DecideAsync(caller, row, ApprovalStatuses.Rejected, HrmValidate.Name(note, 500, "A note"), ct);
    }
}
