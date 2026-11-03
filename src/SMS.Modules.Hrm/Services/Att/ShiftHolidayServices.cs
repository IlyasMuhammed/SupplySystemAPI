using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Att;

// API-CONTRACT §6.1. Every query filters on the caller's organization explicitly.

public interface IShiftService
{
    Task<IReadOnlyList<ShiftModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default);
    Task<ShiftModel> CreateAsync(HrmCaller caller, ShiftUpsert request, CancellationToken ct = default);
    Task<ShiftModel> UpdateAsync(HrmCaller caller, Guid uuid, ShiftUpsert request, CancellationToken ct = default);
    Task<ShiftModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default);
    Task<ShiftAssignResult> AssignAsync(HrmCaller caller, Guid uuid, ShiftAssignRequest request, CancellationToken ct = default);
}

public interface IHolidayService
{
    Task<IReadOnlyList<HolidayModel>> ListAsync(HrmCaller caller, int? year, Guid? branchUuid, CancellationToken ct = default);
    Task<HolidayModel> CreateAsync(HrmCaller caller, HolidayUpsert request, CancellationToken ct = default);
    Task<HolidayModel> UpdateAsync(HrmCaller caller, Guid uuid, HolidayUpsert request, CancellationToken ct = default);
    Task DeleteAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    /// <summary>Task 108: a holiday for the whole organization, or for this branch.</summary>
    Task<bool> IsHolidayAsync(HrmCaller caller, DateOnly date, Guid? branchUuid, CancellationToken ct = default);
}

internal sealed class ShiftService : HrmServiceBase, IShiftService
{
    private readonly IHrmScopeService _scope;
    private readonly IEmploymentRecordService _employment;
    private readonly IEmploymentRecordFactory _factory;
    private readonly IAttendanceRecalculator _recalc;

    public ShiftService(HrmDbContext db, IHrmScopeService scope, IEmploymentRecordService employment, IEmploymentRecordFactory factory, IAttendanceRecalculator recalc) : base(db)
    {
        _scope = scope;
        _employment = employment;
        _factory = factory;
        _recalc = recalc;
    }

    private static ShiftModel Map(Shift s) => new()
    {
        Uuid = s.Uuid, Code = s.Code, Name = s.Name, StartTime = s.StartTime, EndTime = s.EndTime, BreakMinutes = s.BreakMinutes, GraceInMinutes = s.GraceInMinutes,
        GraceOutMinutes = s.GraceOutMinutes, HalfDayMinutes = s.HalfDayMinutes, OvertimeThresholdMinutes = s.OvertimeThresholdMinutes, WeeklyOffDays = s.WeeklyOffDays,
        IsDefault = s.IsDefault, IsActive = s.IsActive
    };

    private Task<Shift?> FindAsync(Guid org, Guid uuid, CancellationToken ct) =>
        Db.Shifts.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.OrganizationId == org && s.Uuid == uuid, ct);

    public async Task<IReadOnlyList<ShiftModel>> ListAsync(HrmCaller caller, bool includeInactive, CancellationToken ct = default)
    {
        var rows = await Db.Shifts.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == caller.OrganizationId && (includeInactive || s.IsActive)).OrderBy(s => s.Name).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    private static void Apply(Shift row, ShiftUpsert r)
    {
        row.Code = HrmValidate.Code(r.Code);
        row.Name = HrmValidate.Name(r.Name);
        row.StartTime = r.StartTime ?? throw new BadRequestException("The start time is required.");
        row.EndTime = r.EndTime ?? throw new BadRequestException("The end time is required.");
        if (row.StartTime == row.EndTime) throw new BadRequestException("A shift cannot start and end at the same time.");
        if (r.BreakMinutes < 0 || r.GraceInMinutes < 0 || r.GraceOutMinutes < 0 || r.HalfDayMinutes < 0 || r.OvertimeThresholdMinutes < 0)
            throw new BadRequestException("Minutes cannot be negative.");
        if (r.WeeklyOffDays is < 0 or >= 127) throw new BadRequestException("Weekly off days are flags, Sunday = 1 to Saturday = 64, and cannot be all seven.");
        row.BreakMinutes = r.BreakMinutes;
        row.GraceInMinutes = r.GraceInMinutes;
        row.GraceOutMinutes = r.GraceOutMinutes;
        row.HalfDayMinutes = r.HalfDayMinutes;
        row.OvertimeThresholdMinutes = r.OvertimeThresholdMinutes;
        row.WeeklyOffDays = r.WeeklyOffDays;
        if (row.BreakMinutes >= ShiftMath.SpanMinutes(row)) throw new BadRequestException("The break must be shorter than the shift.");
        if (row.HalfDayMinutes > ShiftMath.TotalMinutes(row)) throw new BadRequestException("The half-day minutes cannot exceed the working minutes of the shift.");
    }

    private async Task CheckCodeAsync(Guid org, string code, int id, CancellationToken ct)
    {
        if (await Db.Shifts.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org && s.Code == code && s.Id != id, ct))
            throw HrmErrors.Conflict("SHIFT_CODE_TAKEN", $"A shift with code {code} already exists.");
    }

    private async Task ClearOtherDefaultsAsync(Guid org, int id, CancellationToken ct)
    {
        var others = await Db.Shifts.IgnoreQueryFilters().Where(s => s.OrganizationId == org && s.IsDefault && s.Id != id).ToListAsync(ct);
        foreach (var o in others) o.IsDefault = false;
    }

    public async Task<ShiftModel> CreateAsync(HrmCaller caller, ShiftUpsert request, CancellationToken ct = default)
    {
        var row = new Shift();
        Apply(row, request);
        await CheckCodeAsync(caller.OrganizationId, row.Code, 0, ct);
        if (request.IsDefault) await ClearOtherDefaultsAsync(caller.OrganizationId, 0, ct);
        row.IsDefault = request.IsDefault;
        Stamp(row, caller, isNew: true);
        Db.Shifts.Add(row);
        await SaveAsync(ct);
        return Map(row);
    }

    public async Task<ShiftModel> UpdateAsync(HrmCaller caller, Guid uuid, ShiftUpsert request, CancellationToken ct = default)
    {
        var row = await FindAsync(caller.OrganizationId, uuid, ct) ?? throw new NotFoundException("Shift", uuid);
        Apply(row, request);
        await CheckCodeAsync(caller.OrganizationId, row.Code, row.Id, ct);
        if (request.IsDefault) await ClearOtherDefaultsAsync(caller.OrganizationId, row.Id, ct);
        row.IsDefault = request.IsDefault;
        Stamp(row, caller);
        await SaveAsync(ct, "This shift");
        return Map(row);
    }

    public async Task<ShiftModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var row = await FindAsync(caller.OrganizationId, uuid, ct) ?? throw new NotFoundException("Shift", uuid);
        if (!isActive && row.IsActive)
        {
            var count = await LiveRecords(caller.OrganizationId).CountAsync(r => r.ShiftId == row.Id, ct);
            if (count > 0) throw HrmErrors.Conflict("SHIFT_IN_USE", $"{count} employee(s) work this shift; move them first.");
        }
        row.IsActive = isActive;
        if (!isActive) row.IsDefault = false;
        Stamp(row, caller);
        await SaveAsync(ct, "This shift");
        return Map(row);
    }

    public async Task<ShiftAssignResult> AssignAsync(HrmCaller caller, Guid uuid, ShiftAssignRequest request, CancellationToken ct = default)
    {
        var shift = await FindAsync(caller.OrganizationId, uuid, ct) ?? throw new NotFoundException("Shift", uuid);
        if (!shift.IsActive) throw new BadRequestException("This shift is inactive.");
        var from = request.EffectiveFrom ?? throw new BadRequestException("The effective date is required.");
        var uuids = request.EmployeeUuids.Distinct().ToList();
        if (uuids.Count == 0) throw new BadRequestException("Pick at least one employee.");

        // All or nothing: one refused employee must not leave half a team moved.
        var employeeIds = new List<int>();
        await Db.InTransactionAsync(async () =>
        {
            employeeIds.Clear();
            foreach (var employeeUuid in uuids)
            {
                var employee = await _scope.RequireEmployeeAsync(caller, employeeUuid, ct);
                employeeIds.Add(employee.Id);
                var latest = (await _employment.ListAsync(caller, employeeUuid, ct)).FirstOrDefault()
                    ?? throw HrmErrors.Conflict("EMPLOYMENT_NONE", $"{employee.FullName} has no employment record yet.");
                if (latest.EffectiveFrom == from)
                {
                    // Same start date: a second record cannot start then, so this is a correction of the record's shift.
                    var record = await Db.EmploymentRecords.IgnoreQueryFilters()
                        .FirstAsync(r => r.OrganizationId == caller.OrganizationId && r.Uuid == latest.Uuid, ct);
                    record.ShiftId = shift.Id;
                    Stamp(record, caller);
                    await SaveAsync(ct, "This employment record");
                    continue;
                }
                await _factory.AddAsync(caller, employee, new EmploymentRecordCreate
                {
                    EffectiveFrom = from, EmploymentType = latest.EmploymentType, DepartmentUuid = latest.DepartmentUuid, DesignationUuid = latest.DesignationUuid,
                    BranchUuid = latest.BranchUuid, GradeUuid = latest.GradeUuid, ManagerUuid = latest.ManagerUuid, ShiftUuid = shift.Uuid,
                    ProbationEndDate = latest.ProbationEndDate, ConfirmationDate = latest.ConfirmationDate, ContractEndDate = latest.ContractEndDate,
                    NoticePeriodDays = latest.NoticePeriodDays, CostCenterUuid = latest.CostCenterUuid, PayrollGroup = latest.PayrollGroup,
                    ChangeReason = EmploymentChangeReasons.Other, Notes = $"Shift changed to {shift.Name}",
                    CostSplits = latest.CostSplits.Select(s => new CostSplitModel { CostCenterUuid = s.CostCenterUuid, Percent = s.Percent }).ToList()
                }, ct);
            }
        }, ct);

        await _recalc.RecomputeAsync(caller.OrganizationId, employeeIds, from, from.AddDays(92), ct: ct);
        return new ShiftAssignResult { Assigned = employeeIds.Count };
    }
}

internal sealed class HolidayService : HrmServiceBase, IHolidayService
{
    private readonly IAttendanceRecalculator _recalc;

    public HolidayService(HrmDbContext db, IAttendanceRecalculator recalc) : base(db) => _recalc = recalc;

    private async Task<HolidayModel> MapAsync(Holiday h, CancellationToken ct)
    {
        var branch = h.BranchId is { } b ? await Db.Branches.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == b).Select(x => (Guid?)x.Uuid).FirstOrDefaultAsync(ct) : null;
        return new HolidayModel { Uuid = h.Uuid, Date = h.Date, Name = h.Name, CountryCode = h.CountryCode, BranchUuid = branch, IsEstimated = h.IsEstimated };
    }

    public async Task<IReadOnlyList<HolidayModel>> ListAsync(HrmCaller caller, int? year, Guid? branchUuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var branchId = await OptionalIdAsync(Db.Branches, org, branchUuid, "Branch", ct);
        var query = Db.Holidays.IgnoreQueryFilters().AsNoTracking().Where(h => h.OrganizationId == org);
        if (year is { } y)
        {
            var from = new DateOnly(y, 1, 1);
            var to = new DateOnly(y, 12, 31);
            query = query.Where(h => h.Date >= from && h.Date <= to);
        }
        if (branchId is { } b) query = query.Where(h => h.BranchId == null || h.BranchId == b);
        return await query.OrderBy(h => h.Date).ThenBy(h => h.Name).Select(h => new HolidayModel
        {
            Uuid = h.Uuid, Date = h.Date, Name = h.Name, CountryCode = h.CountryCode, BranchUuid = h.Branch == null ? null : h.Branch.Uuid, IsEstimated = h.IsEstimated
        }).ToListAsync(ct);
    }

    private async Task ApplyAsync(Holiday row, Guid org, HolidayUpsert r, CancellationToken ct)
    {
        var date = r.Date ?? throw new BadRequestException("The date is required.");
        // Confirming the real date of a moon-dependent holiday: it is no longer an estimate.
        if (row.Id != 0 && row.Date != date) row.IsEstimated = false;
        row.Date = date;
        row.Name = HrmValidate.Name(r.Name);
        row.CountryCode = HrmValidate.Optional(r.CountryCode, 3, "Country code")?.ToUpperInvariant() ?? (row.Id != 0 ? row.CountryCode
            : await Db.Settings.IgnoreQueryFilters().AsNoTracking().Where(s => s.OrganizationId == org).Select(s => s.CountryCode).FirstOrDefaultAsync(ct) ?? "PK");
        row.BranchId = await OptionalIdAsync(Db.Branches, org, r.BranchUuid, "Branch", ct);
        if (await Db.Holidays.IgnoreQueryFilters().AnyAsync(h => h.OrganizationId == org && h.Date == row.Date && h.Name == row.Name && h.BranchId == row.BranchId && h.Id != row.Id, ct))
            throw HrmErrors.Conflict("HOLIDAY_DUPLICATE", $"{row.Name} is already a holiday on {row.Date:yyyy-MM-dd}.");
    }

    /// <summary>Days already computed for these dates follow the calendar. Days not yet computed read it when they are.</summary>
    private async Task RecomputeComputedDaysAsync(Guid org, IReadOnlyCollection<DateOnly> dates, CancellationToken ct)
    {
        foreach (var date in dates.Distinct())
        {
            var employees = await Db.AttendanceDays.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.OrganizationId == org && d.Date == date).Select(d => d.EmployeeId).ToListAsync(ct);
            if (employees.Count > 0) await _recalc.RecomputeAsync(org, employees, date, date, ct: ct);
        }
    }

    public async Task<HolidayModel> CreateAsync(HrmCaller caller, HolidayUpsert request, CancellationToken ct = default)
    {
        var row = new Holiday();
        await ApplyAsync(row, caller.OrganizationId, request, ct);
        Stamp(row, caller, isNew: true);
        Db.Holidays.Add(row);
        await SaveAsync(ct);
        await RecomputeComputedDaysAsync(caller.OrganizationId, [row.Date], ct);
        return await MapAsync(row, ct);
    }

    public async Task<HolidayModel> UpdateAsync(HrmCaller caller, Guid uuid, HolidayUpsert request, CancellationToken ct = default)
    {
        var row = await Db.Holidays.IgnoreQueryFilters().FirstOrDefaultAsync(h => h.OrganizationId == caller.OrganizationId && h.Uuid == uuid, ct)
            ?? throw new NotFoundException("Holiday", uuid);
        var oldDate = row.Date;
        await ApplyAsync(row, caller.OrganizationId, request, ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This holiday");
        await RecomputeComputedDaysAsync(caller.OrganizationId, [oldDate, row.Date], ct);
        return await MapAsync(row, ct);
    }

    public async Task DeleteAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await Db.Holidays.IgnoreQueryFilters().FirstOrDefaultAsync(h => h.OrganizationId == caller.OrganizationId && h.Uuid == uuid, ct)
            ?? throw new NotFoundException("Holiday", uuid);
        var date = row.Date;
        Db.Holidays.Remove(row);
        await SaveAsync(ct, "This holiday");
        await RecomputeComputedDaysAsync(caller.OrganizationId, [date], ct);
    }

    public async Task<bool> IsHolidayAsync(HrmCaller caller, DateOnly date, Guid? branchUuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var branchId = await OptionalIdAsync(Db.Branches, org, branchUuid, "Branch", ct);
        return await Db.Holidays.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(h => h.OrganizationId == org && h.Date == date && (h.BranchId == null || h.BranchId == branchId), ct);
    }
}
