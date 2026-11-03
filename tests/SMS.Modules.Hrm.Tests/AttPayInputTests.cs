using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Tasks 130, 132: the seam PAY reads. June 2026: 30 calendar days, 22 weekdays; "now" is the 30th.</summary>
public sealed class AttPayInputTests
{
    private static readonly DateOnly From = new(2026, 6, 1), To = new(2026, 6, 30);

    /// <summary>Every weekday of June up to the 29th worked in full, except those listed.</summary>
    private static async Task PunchMonthAsync(AttHarness h, Guid employee, params int[] except)
    {
        for (var day = 1; day <= 29; day++)
        {
            var date = new DateOnly(2026, 6, day);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || except.Contains(day)) continue;
            await h.PunchAsync(employee, 6, day, (9, 0), (18, 0));
        }
    }

    private static async Task<(AttHarness H, EmployeeDetail E, int Id)> SetupAsync()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        return (h, e, await h.IdOfAsync(e.Uuid));
    }

    [Fact]
    public async Task A_full_month_pays_the_whole_divisor_on_the_calendar_basis()
    {
        var (h, e, id) = await SetupAsync();
        await PunchMonthAsync(h, e.Uuid);
        var pay = await h.Pay.GetAsync(h.Org, id, From, To);
        pay.AttendanceTracked.Should().BeTrue();
        pay.WorkingDays.Should().Be(30m);
        pay.PaidDays.Should().Be(30m, "weekly offs are paid");
        pay.ScheduledWorkingDays.Should().Be(22);
        pay.WorkedDays.Should().Be(21m, "the 30th is still in progress");
        pay.UnpaidDays.Should().Be(0m);
    }

    [Fact]
    public async Task A_fixed_divisor_is_the_basis_and_absences_come_off_it()
    {
        var (h, e, id) = await SetupAsync();
        var settings = await h.Db.Settings.IgnoreQueryFilters().FirstAsync(s => s.OrganizationId == h.Org);
        settings.WorkingDaysDivisor = 26;
        await h.Db.SaveChangesAsync();
        await PunchMonthAsync(h, e.Uuid, except: [3, 4]);
        var pay = await h.Pay.GetAsync(h.Org, id, From, To);
        pay.WorkingDays.Should().Be(26m);
        pay.PaidDays.Should().Be(24m);
    }

    [Fact]
    public async Task Absence_half_day_and_unpaid_leave_reduce_pay_paid_leave_does_not()
    {
        var (h, e, id) = await SetupAsync();
        await PunchMonthAsync(h, e.Uuid, except: [2, 3, 4, 5]);
        await h.PunchAsync(e.Uuid, 6, 3, (9, 0), (12, 0));
        await h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = e.Uuid, LeaveTypeUuid = await h.LeaveTypeAsync("CASUAL"), Days = 5, Year = 2026 });
        var paidLeave = await h.Leave.RequestLeaveAsync(h.Admin(), new LeaveRequestCreate { EmployeeUuid = e.Uuid, LeaveTypeUuid = await h.LeaveTypeAsync("CASUAL"), FromDate = new DateOnly(2026, 6, 4), ToDate = new DateOnly(2026, 6, 4) });
        await h.Leave.ApproveAsync(h.ApproverCaller(), paidLeave.Uuid);
        var unpaidLeave = await h.Leave.RequestLeaveAsync(h.Admin(), new LeaveRequestCreate { EmployeeUuid = e.Uuid, LeaveTypeUuid = await h.LeaveTypeAsync("UNPAID"), FromDate = new DateOnly(2026, 6, 5), ToDate = new DateOnly(2026, 6, 5) });
        await h.Leave.ApproveAsync(h.ApproverCaller(), unpaidLeave.Uuid);

        var pay = await h.Pay.GetAsync(h.Org, id, From, To);
        pay.UnpaidDays.Should().Be(2.5m, "absent the 2nd, half the 3rd, unpaid leave the 5th");
        pay.PaidDays.Should().Be(27.5m);
    }

    [Fact]
    public async Task With_attendance_off_or_no_data_nobody_is_paid_zero_but_unpaid_leave_still_counts()
    {
        var (h, e, id) = await SetupAsync();
        var noData = await h.Pay.GetAsync(h.Org, id, From, To);
        noData.AttendanceTracked.Should().BeFalse();
        noData.PaidDays.Should().Be(30m);

        var unpaid = await h.Leave.RequestLeaveAsync(h.Admin(), new LeaveRequestCreate { EmployeeUuid = e.Uuid, LeaveTypeUuid = await h.LeaveTypeAsync("UNPAID"), FromDate = new DateOnly(2026, 6, 8), ToDate = new DateOnly(2026, 6, 9) });
        await h.Leave.ApproveAsync(h.ApproverCaller(), unpaid.Uuid);
        (await h.Pay.GetAsync(h.Org, id, From, To)).PaidDays.Should().Be(28m);

        await PunchMonthAsync(h, e.Uuid, except: [15]);
        h.Gate.Off.Add(HrmFeatureCodes.Attendance);
        var off = await h.Pay.GetAsync(h.Org, id, From, To);
        off.AttendanceTracked.Should().BeFalse();
        off.PaidDays.Should().Be(28m, "the absence on the 15th is not read while the feature is off");
    }

    [Fact]
    public async Task A_mid_month_joiner_is_paid_from_joining()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync(from: new DateOnly(2026, 6, 16));
        var pay = await h.Pay.GetAsync(h.Org, await h.IdOfAsync(e.Uuid), From, To);
        pay.PaidDays.Should().Be(15m);
        pay.ScheduledWorkingDays.Should().Be(11);
    }

    [Fact]
    public async Task Days_ahead_of_today_are_paid_when_the_run_is_early()
    {
        var (h, e, id) = await SetupAsync();
        h.Clock.UtcNow = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc);
        await h.PunchAsync(e.Uuid, 6, 1, (9, 0), (18, 0));
        var pay = await h.Pay.GetAsync(h.Org, id, From, To);
        pay.AttendanceTracked.Should().BeTrue();
        pay.UnpaidDays.Should().Be(6m, "2nd to 5th, 8th, 9th; the 10th is not over");
        pay.PaidDays.Should().Be(24m);
    }

    [Fact]
    public async Task Only_approved_overtime_feeds_payroll_and_it_is_paid_once_until_released()
    {
        var (h, e, id) = await SetupAsync();
        var admin = h.Admin();
        var approved = await h.Overtime.SubmitAsync(admin, new OvertimeCreate { EmployeeUuid = e.Uuid, Date = new DateOnly(2026, 6, 1), Minutes = 60, Multiplier = 1.5m });
        await h.Overtime.ApproveAsync(admin, approved.Uuid);
        await h.Overtime.SubmitAsync(admin, new OvertimeCreate { EmployeeUuid = e.Uuid, Date = new DateOnly(2026, 6, 2), Minutes = 45 });
        var rejected = await h.Overtime.SubmitAsync(admin, new OvertimeCreate { EmployeeUuid = e.Uuid, Date = new DateOnly(2026, 6, 3), Minutes = 30 });
        await h.Overtime.RejectAsync(admin, rejected.Uuid, "Not authorised");
        var backlog = await h.Overtime.SubmitAsync(admin, new OvertimeCreate { EmployeeUuid = e.Uuid, Date = new DateOnly(2026, 5, 29), Minutes = 30 });
        await h.Overtime.ApproveAsync(admin, backlog.Uuid);

        var pay = await h.Pay.GetAsync(h.Org, id, From, To);
        pay.ApprovedOvertimeMinutes.Should().Be(90, "60 this month and 30 approved late from May; pending and rejected do not count");
        pay.WeightedOvertimeMinutes.Should().Be(120m);
        pay.WeightedOvertimeHours.Should().Be(2m);
        pay.Overtime.Select(o => o.Date).Should().Equal(new DateOnly(2026, 5, 29), new DateOnly(2026, 6, 1));

        // A real run row is what the FK would point at; in-memory has no FK, and the seam only stamps the id.
        (await h.Pay.MarkOvertimePaidAsync(h.Org, 4242, id, From, To)).Should().Be(2);
        (await h.Pay.GetAsync(h.Org, id, From, To)).ApprovedOvertimeMinutes.Should().Be(0, "cannot be paid twice");
        (await h.Overtime.ListAsync(admin, "Approved", null, null, e.Uuid)).Should().OnlyContain(o => o.Paid);

        (await h.Pay.ReleaseOvertimeAsync(h.Org, 4242)).Should().Be(2);
        (await h.Pay.GetAsync(h.Org, id, From, To)).ApprovedOvertimeMinutes.Should().Be(90);
    }

    [Fact]
    public async Task The_seam_is_explicit_about_the_organization()
    {
        var name = "att-" + Guid.NewGuid();
        var a = await AttHarness.Memory(name).ReadyAsync();
        var b = await AttHarness.Memory(name).ReadyAsync();
        var theirs = await b.HireAsync();
        var theirId = await b.IdOfAsync(theirs.Uuid);
        var row = await b.Overtime.SubmitAsync(b.Admin(), new OvertimeCreate { EmployeeUuid = theirs.Uuid, Date = new DateOnly(2026, 6, 1), Minutes = 60 });
        await b.Overtime.ApproveAsync(b.Admin(), row.Uuid);

        var read = await a.Pay.GetAsync(a.Org, theirId, From, To);
        read.ApprovedOvertimeMinutes.Should().Be(0);
        read.PaidDays.Should().Be(0m, "not an employee of this organization");
        (await a.Pay.MarkOvertimePaidAsync(a.Org, 1, theirId, From, To)).Should().Be(0);
        (await b.Pay.GetAsync(b.Org, theirId, From, To)).ApprovedOvertimeMinutes.Should().Be(60);
    }

    [Fact]
    public async Task Many_returns_every_employee_asked_for()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var one = await h.IdOfAsync((await h.HireAsync("One")).Uuid);
        var two = await h.IdOfAsync((await h.HireAsync("Two", from: new DateOnly(2026, 6, 16))).Uuid);
        var all = await h.Pay.GetManyAsync(h.Org, [one, two], From, To);
        all[one].PaidDays.Should().Be(30m);
        all[two].PaidDays.Should().Be(15m);
    }
}
