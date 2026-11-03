using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Tasks 104–105, 108, 112–125, 129–132: services over an in-memory database, tenant and scope included.</summary>
public sealed class AttAttendanceTests
{
    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));
    private static readonly DateOnly Jun1 = new(2026, 6, 1);

    private static async Task<AttendanceDayModel> DayAsync(AttHarness h, Guid employee, DateOnly date)
    {
        var page = await h.Attendance.ListDaysAsync(h.Admin(), new AttendanceDayFilter { EmployeeUuid = employee, From = date, To = date });
        return page.Data.Single();
    }

    [Fact]
    public async Task Recording_the_same_device_event_twice_is_ignored_and_returns_the_original()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        var req = new AttendanceEventRequest { EmployeeUuid = e.Uuid, EventTimeUtc = AttHarness.Pkt(6, 1, 9), EventType = "IN", Source = "DEVICE", ExternalEventId = "D1" };
        var first = await h.Attendance.RecordEventAsync(h.Admin(), req);
        var second = await h.Attendance.RecordEventAsync(h.Admin(), req);
        first.Duplicate.Should().BeFalse();
        second.Duplicate.Should().BeTrue();
        second.Uuid.Should().Be(first.Uuid);
        (await h.Db.AttendanceEvents.CountAsync()).Should().Be(1);

        // The key is per source.
        (await h.Attendance.RecordEventAsync(h.Admin(), new AttendanceEventRequest { EmployeeUuid = e.Uuid, EventTimeUtc = AttHarness.Pkt(6, 1, 9), Source = "IMPORT", ExternalEventId = "D1" }))
            .Duplicate.Should().BeFalse();
    }

    [Fact]
    public async Task An_event_computes_its_day_in_the_organizations_local_time()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        await h.PunchAsync(e.Uuid, 6, 1, (9, 40), (19, 45));
        var day = await DayAsync(h, e.Uuid, Jun1);
        day.Status.Should().Be(AttendanceDayStatuses.Late);
        day.LateMinutes.Should().Be(40, "04:40 UTC is 09:40 in Karachi");
        day.WorkedMinutes.Should().Be(545);
        day.OvertimeMinutes.Should().Be(65);
        day.ShiftName.Should().Be("General Shift");
    }

    [Fact]
    public async Task Future_events_are_refused_and_bad_enums_answer_400()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        await FluentActions.Awaiting(() => h.Attendance.RecordEventAsync(h.Admin(), new AttendanceEventRequest { EmployeeUuid = e.Uuid, EventTimeUtc = h.Clock.UtcNow.AddHours(1) }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("ATTENDANCE_EVENT_FUTURE*");
        await FluentActions.Awaiting(() => h.Attendance.RecordEventAsync(h.Admin(), new AttendanceEventRequest { EmployeeUuid = e.Uuid, EventTimeUtc = h.Clock.UtcNow, EventType = "NOPE" }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Unknown eventType*");
    }

    [Fact]
    public async Task Listing_days_creates_the_absences_nobody_punched_for_and_skips_holidays_and_off_days()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        await h.Holidays.CreateAsync(h.Admin(), new HolidayUpsert { Date = new DateOnly(2026, 6, 2), Name = "Test Day" });
        var page = await h.Attendance.ListDaysAsync(h.Admin(), new AttendanceDayFilter { EmployeeUuid = e.Uuid, From = Jun1, To = new DateOnly(2026, 6, 7) });
        page.Data.OrderBy(d => d.Date).Select(d => d.Status).Should().Equal(
            AttendanceDayStatuses.Absent, AttendanceDayStatuses.Holiday, AttendanceDayStatuses.Absent, AttendanceDayStatuses.Absent, AttendanceDayStatuses.Absent,
            AttendanceDayStatuses.WeeklyOff, AttendanceDayStatuses.WeeklyOff);
    }

    [Fact]
    public async Task No_day_exists_before_joining_or_in_the_future_and_today_is_not_absent_yet()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync(from: new DateOnly(2026, 6, 29));
        var page = await h.Attendance.ListDaysAsync(h.Admin(), new AttendanceDayFilter { EmployeeUuid = e.Uuid, From = new DateOnly(2026, 6, 28), To = new DateOnly(2026, 7, 2) });
        page.Data.Select(d => d.Date).Should().Equal(new DateOnly(2026, 6, 29));
        page.Data[0].Status.Should().Be(AttendanceDayStatuses.Absent);
    }

    [Fact]
    public async Task A_manual_override_survives_recomputation_and_new_events()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        var day = await DayAsync(h, e.Uuid, Jun1);
        day.Status.Should().Be(AttendanceDayStatuses.Absent);

        await FluentActions.Awaiting(() => h.Attendance.OverrideDayAsync(h.Admin(), day.Uuid, new AttendanceDayOverride { Status = "Present" }))
            .Should().ThrowAsync<BadRequestException>();
        var changed = await h.Attendance.OverrideDayAsync(h.Admin(), day.Uuid, new AttendanceDayOverride { Status = "Present", OverrideNote = "Device down" });
        changed.IsManualOverride.Should().BeTrue();

        await h.Attendance.RecomputeAsync(h.Admin(), new AttendanceRecomputeRequest { EmployeeUuid = e.Uuid, From = Jun1, To = Jun1 });
        await h.PunchAsync(e.Uuid, 6, 1, (12, 0), (13, 0));
        var after = await DayAsync(h, e.Uuid, Jun1);
        after.Status.Should().Be(AttendanceDayStatuses.Present);
        after.OverrideNote.Should().Be("Device down");
    }

    [Fact]
    public async Task Csv_import_reports_bad_rows_loads_the_rest_and_a_second_run_changes_nothing()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        var n = e.EmployeeNumber;
        var csv = $"employeeNumber,eventTimeUtc,eventType,externalEventId\n{n},2026-06-01T04:00:00Z,IN,X1\n{n},2026-06-01T13:00:00Z,OUT,X2\nNOBODY,2026-06-01T13:00:00Z,OUT,X3\n{n},not-a-time,OUT,X4\n{n},2026-06-01T13:00:00Z,SIDEWAYS,X5\n{n},2030-01-01T00:00:00Z,IN,X6\n{n},2026-06-02T04:00:00Z,IN,\n{n},2026-06-01T04:00:00Z,IN,X1\n";

        var first = await h.Attendance.ImportEventsAsync(h.Admin(), Csv(csv));
        first.Total.Should().Be(8);
        first.Imported.Should().Be(3);
        first.Duplicates.Should().Be(1, "X1 twice in the file");
        first.Errors.Select(x => x.Row).Should().Equal(4, 5, 6, 7);
        (await DayAsync(h, e.Uuid, Jun1)).Status.Should().Be(AttendanceDayStatuses.Present);

        var second = await h.Attendance.ImportEventsAsync(h.Admin(), Csv(csv));
        second.Imported.Should().Be(0);
        second.Duplicates.Should().Be(4, "X1 twice, X2 and the row with a derived id");
        (await h.Db.AttendanceEvents.CountAsync()).Should().Be(3);
    }

    [Fact]
    public async Task Import_sees_only_employees_in_the_callers_scope()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        var outsider = h.As(77, P.HRM_ATTENDANCE_MANAGE);
        var result = await h.Attendance.ImportEventsAsync(outsider, Csv($"{e.EmployeeNumber},2026-06-01T04:00:00Z,IN,S1\n"));
        result.Imported.Should().Be(0);
        result.Errors.Single().Message.Should().Contain("Unknown employee number");
    }

    [Fact]
    public async Task Monthly_summary_adds_up_the_days()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        await h.Holidays.CreateAsync(h.Admin(), new HolidayUpsert { Date = new DateOnly(2026, 6, 10), Name = "Test Day" });
        await h.PunchAsync(e.Uuid, 6, 1, (9, 0), (18, 0));
        await h.PunchAsync(e.Uuid, 6, 2, (10, 0), (18, 0));
        await h.PunchAsync(e.Uuid, 6, 3, (9, 0), (12, 0));
        await h.PunchAsync(e.Uuid, 6, 4, (9, 0), (20, 0));

        var s = await h.Attendance.GetMonthlySummaryAsync(h.Admin(), e.Uuid, 2026, 6);
        s.WorkingDays.Should().Be(21, "22 weekdays in June 2026 less the holiday");
        s.PresentDays.Should().Be(3);
        s.LateDays.Should().Be(1);
        s.HalfDays.Should().Be(1);
        s.Holidays.Should().Be(1);
        s.WeeklyOffs.Should().Be(8);
        s.AbsentDays.Should().Be(21 - 4 - 1, "everything else but today, the 30th, which is not over");
        s.OvertimeMinutes.Should().Be(120);
    }

    [Fact]
    public async Task Tenant_and_scope_hold_on_every_read_and_write()
    {
        var name = "att-" + Guid.NewGuid();
        var a = await AttHarness.Memory(name).ReadyAsync();
        var b = await AttHarness.Memory(name).ReadyAsync();
        var theirs = await b.HireAsync("Theirs");
        var mine = await a.HireAsync("Mine");
        await b.PunchAsync(theirs.Uuid, 6, 1, (9, 0), (18, 0));

        await FluentActions.Awaiting(() => a.Attendance.RecordEventAsync(a.Admin(), new AttendanceEventRequest { EmployeeUuid = theirs.Uuid, EventTimeUtc = a.Clock.UtcNow }))
            .Should().ThrowAsync<NotFoundException>();
        (await a.Attendance.ListDaysAsync(a.Admin(), new AttendanceDayFilter { EmployeeUuid = theirs.Uuid, From = Jun1, To = Jun1 })).Data.Should().BeEmpty();
        var theirDay = (await b.Attendance.ListDaysAsync(b.Admin(), new AttendanceDayFilter { EmployeeUuid = theirs.Uuid, From = Jun1, To = Jun1 })).Data.Single();
        await FluentActions.Awaiting(() => a.Attendance.OverrideDayAsync(a.Admin(), theirDay.Uuid, new AttendanceDayOverride { Status = "Absent", OverrideNote = "x" }))
            .Should().ThrowAsync<NotFoundException>();

        var outsider = a.As(77, P.HRM_ATTENDANCE_VIEW);
        (await a.Attendance.ListDaysAsync(outsider, new AttendanceDayFilter { From = Jun1, To = Jun1 })).Data.Should().BeEmpty("no HR scope");
        await FluentActions.Awaiting(() => a.Attendance.GetMonthlySummaryAsync(outsider, mine.Uuid, 2026, 6)).Should().ThrowAsync<NotFoundException>();

        var selfOnly = a.As(78, P.HRM_SELF_SERVICE);
        await FluentActions.Awaiting(() => a.Attendance.GetMonthlySummaryAsync(selfOnly, mine.Uuid, 2026, 6)).Should().ThrowAsync<NotFoundException>().WithMessage("HRM_NO_EMPLOYEE*");
    }

    [Fact]
    public async Task Self_service_punches_and_reads_only_its_own_record()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var me = await h.HireAsync("Me", userId: 500);
        var other = await h.HireAsync("Other");
        var caller = h.As(500, P.HRM_SELF_SERVICE);
        (await h.Attendance.RecordOwnEventAsync(caller, new AttendanceSelfEventRequest { EventType = "IN" })).Duplicate.Should().BeFalse();
        (await h.Db.AttendanceEvents.SingleAsync()).Source.Should().Be(AttendanceSources.Web);
        (await h.Attendance.OwnDaysAsync(caller, new DateOnly(2026, 6, 30), new DateOnly(2026, 6, 30))).Single().EmployeeUuid.Should().Be(me.Uuid);
        (await h.Attendance.GetMonthlySummaryAsync(caller, null, 2026, 6)).WorkingDays.Should().Be(22);
        await FluentActions.Awaiting(() => h.Attendance.GetMonthlySummaryAsync(caller, other.Uuid, 2026, 6)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => h.Attendance.RecordOwnEventAsync(h.As(501, P.HRM_SELF_SERVICE), new AttendanceSelfEventRequest { EventType = "IN" }))
            .Should().ThrowAsync<NotFoundException>().WithMessage("HRM_NO_EMPLOYEE*");
    }

    [Fact]
    public async Task Shift_crud_default_is_single_and_in_use_refuses_deactivation()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var admin = h.Admin();
        var shift = await h.Shifts.CreateAsync(admin, new ShiftUpsert { Code = "morn", Name = "Morning", StartTime = new TimeOnly(7, 0), EndTime = new TimeOnly(15, 0), BreakMinutes = 30, WeeklyOffDays = 1, IsDefault = true });
        shift.Code.Should().Be("MORN");
        (await h.Shifts.ListAsync(admin, false)).Count(s => s.IsDefault).Should().Be(1);

        await FluentActions.Awaiting(() => h.Shifts.CreateAsync(admin, new ShiftUpsert { Code = "MORN", Name = "X", StartTime = new TimeOnly(7, 0), EndTime = new TimeOnly(15, 0) }))
            .Should().ThrowAsync<ConflictException>().WithMessage("SHIFT_CODE_TAKEN*");
        await FluentActions.Awaiting(() => h.Shifts.CreateAsync(admin, new ShiftUpsert { Code = "BAD", Name = "X", StartTime = new TimeOnly(7, 0), EndTime = new TimeOnly(7, 0) }))
            .Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.Shifts.CreateAsync(admin, new ShiftUpsert { Code = "BAD", Name = "X", StartTime = new TimeOnly(7, 0), EndTime = new TimeOnly(15, 0), WeeklyOffDays = 127 }))
            .Should().ThrowAsync<BadRequestException>();

        var e = await h.HireAsync(shiftUuid: shift.Uuid);
        await FluentActions.Awaiting(() => h.Shifts.SetActiveAsync(admin, shift.Uuid, false)).Should().ThrowAsync<ConflictException>().WithMessage("SHIFT_IN_USE*");
        e.Uuid.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Assigning_a_shift_adds_a_dated_record_and_recomputes_from_that_date()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var admin = h.Admin();
        var e = await h.HireAsync();
        await h.PunchAsync(e.Uuid, 6, 15, (7, 0), (16, 0));
        var before = await DayAsync(h, e.Uuid, new DateOnly(2026, 6, 15));
        before.LateMinutes.Should().Be(0);
        before.EarlyMinutes.Should().Be(120);

        var early = await h.Shifts.CreateAsync(admin, new ShiftUpsert { Code = "EARLY", Name = "Early", StartTime = new TimeOnly(7, 0), EndTime = new TimeOnly(16, 0), BreakMinutes = 60, WeeklyOffDays = 1 });
        (await h.Shifts.AssignAsync(admin, early.Uuid, new ShiftAssignRequest { EmployeeUuids = [e.Uuid], EffectiveFrom = new DateOnly(2026, 6, 15) })).Assigned.Should().Be(1);

        var records = await h.Db.EmploymentRecords.IgnoreQueryFilters().Where(r => r.EmployeeId == h.Db.Employees.First(x => x.Uuid == e.Uuid).Id).OrderBy(r => r.EffectiveFrom).ToListAsync();
        records.Should().HaveCount(2);
        records[0].EffectiveTo.Should().Be(new DateOnly(2026, 6, 14));
        records[1].ChangeReason.Should().Be(EmploymentChangeReasons.Other);
        var after = await DayAsync(h, e.Uuid, new DateOnly(2026, 6, 15));
        after.EarlyMinutes.Should().Be(0);
        after.ShiftName.Should().Be("Early");

        // Same start date again corrects the record instead of failing on the overlap.
        await h.Shifts.AssignAsync(admin, (await h.Shifts.ListAsync(admin, false)).First(s => s.Code == "GENERAL").Uuid, new ShiftAssignRequest { EmployeeUuids = [e.Uuid], EffectiveFrom = new DateOnly(2026, 6, 15) });
        (await h.Db.EmploymentRecords.IgnoreQueryFilters().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Assigning_outside_scope_is_404()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        var shift = (await h.Shifts.ListAsync(h.Admin(), false)).First();
        await FluentActions.Awaiting(() => h.Shifts.AssignAsync(h.As(77, P.HRM_SHIFT_MANAGE), shift.Uuid, new ShiftAssignRequest { EmployeeUuids = [e.Uuid], EffectiveFrom = Jun1 }))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Holiday_crud_lookup_and_estimate_flag()
    {
        var h = AttHarness.Memory();
        await h.Seeder.EnsureAsync(h.Org);
        (await h.Holidays.ListAsync(h.Admin(), 2026, null)).Should().NotBeEmpty("the country pack seeds 2026");
        await h.ReadyAsync();
        var admin = h.Admin();
        var date = new DateOnly(2026, 8, 14);
        var row = await h.Holidays.CreateAsync(admin, new HolidayUpsert { Date = date, Name = "Independence" });
        row.CountryCode.Should().Be("PK");
        await FluentActions.Awaiting(() => h.Holidays.CreateAsync(admin, new HolidayUpsert { Date = date, Name = "Independence" })).Should().ThrowAsync<ConflictException>().WithMessage("HOLIDAY_DUPLICATE*");
        (await h.Holidays.IsHolidayAsync(admin, date, null)).Should().BeTrue();
        (await h.Holidays.IsHolidayAsync(admin, date.AddDays(1), null)).Should().BeFalse();

        var branchOnly = await h.Holidays.CreateAsync(admin, new HolidayUpsert { Date = date.AddDays(10), Name = "Local", BranchUuid = h.Branch });
        (await h.Holidays.IsHolidayAsync(admin, date.AddDays(10), h.Branch)).Should().BeTrue();
        (await h.Holidays.IsHolidayAsync(admin, date.AddDays(10), null)).Should().BeFalse();
        branchOnly.BranchUuid.Should().Be(h.Branch);

        var stored = await h.Db.Holidays.IgnoreQueryFilters().FirstAsync(x => x.Uuid == row.Uuid);
        stored.IsEstimated = true;
        await h.Db.SaveChangesAsync();
        (await h.Holidays.UpdateAsync(admin, row.Uuid, new HolidayUpsert { Date = date, Name = "Renamed" })).IsEstimated.Should().BeTrue("the date did not move");
        (await h.Holidays.UpdateAsync(admin, row.Uuid, new HolidayUpsert { Date = date.AddDays(1), Name = "Renamed" })).IsEstimated.Should().BeFalse();
        await h.Holidays.DeleteAsync(admin, row.Uuid);
        (await h.Holidays.ListAsync(admin, 2026, null)).Should().NotContain(x => x.Uuid == row.Uuid);
    }

    [Fact]
    public async Task A_new_holiday_turns_a_computed_absence_into_a_holiday_and_removing_it_turns_it_back()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        (await DayAsync(h, e.Uuid, Jun1)).Status.Should().Be(AttendanceDayStatuses.Absent);
        var holiday = await h.Holidays.CreateAsync(h.Admin(), new HolidayUpsert { Date = Jun1, Name = "Declared" });
        (await DayAsync(h, e.Uuid, Jun1)).Status.Should().Be(AttendanceDayStatuses.Holiday);
        await h.Holidays.DeleteAsync(h.Admin(), holiday.Uuid);
        (await DayAsync(h, e.Uuid, Jun1)).Status.Should().Be(AttendanceDayStatuses.Absent);
    }

    // ── Overtime ──

    [Fact]
    public async Task Overtime_flow_defaults_minutes_from_the_day_and_decides_once()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        await h.PunchAsync(e.Uuid, 6, 1, (9, 0), (20, 0));
        var row = await h.Overtime.SubmitAsync(h.Admin(), new OvertimeCreate { EmployeeUuid = e.Uuid, Date = Jun1, Multiplier = 1.5m, Reason = "Stock count" });
        row.Minutes.Should().Be(120);
        row.Status.Should().Be(ApprovalStatuses.Pending);

        await FluentActions.Awaiting(() => h.Overtime.SubmitAsync(h.Admin(), new OvertimeCreate { EmployeeUuid = e.Uuid, Date = Jun1, Minutes = 10 })).Should().ThrowAsync<ConflictException>().WithMessage("OVERTIME_DUPLICATE*");
        await FluentActions.Awaiting(() => h.Overtime.SubmitAsync(h.Admin(), new OvertimeCreate { EmployeeUuid = e.Uuid, Date = Jun1.AddDays(1) })).Should().ThrowAsync<BadRequestException>().WithMessage("OVERTIME_NO_MINUTES*");
        await FluentActions.Awaiting(() => h.Overtime.RejectAsync(h.Admin(), row.Uuid, " ")).Should().ThrowAsync<BadRequestException>();

        var approved = await h.Overtime.ApproveAsync(h.Admin(), row.Uuid);
        approved.Status.Should().Be(ApprovalStatuses.Approved);
        approved.DecidedByName.Should().Be("HR Admin");
        await FluentActions.Awaiting(() => h.Overtime.RejectAsync(h.Admin(), row.Uuid, "no")).Should().ThrowAsync<ConflictException>().WithMessage("OVERTIME_ALREADY_DECIDED*");

        (await h.Overtime.ListAsync(h.Admin(), "Approved", Jun1, Jun1, e.Uuid)).Should().ContainSingle();
        (await h.Overtime.ListAsync(h.As(77, P.HRM_OVERTIME_APPROVE), null, null, null, null)).Should().BeEmpty("no HR scope");
    }

    [Fact]
    public async Task Nobody_decides_their_own_overtime_and_outside_scope_is_404()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var me = await h.HireAsync("Me", userId: 500);
        var row = await h.Overtime.SubmitAsync(h.Admin(), new OvertimeCreate { EmployeeUuid = me.Uuid, Date = Jun1, Minutes = 60 });
        await FluentActions.Awaiting(() => h.Overtime.ApproveAsync(new(h.Org, 500, P.HrmCodes, "Me"), row.Uuid)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => h.Overtime.ApproveAsync(h.As(77, P.HRM_OVERTIME_APPROVE), row.Uuid)).Should().ThrowAsync<NotFoundException>();
    }
}
