using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Tasks 137, 139, 143–154, 159–163, E-15.</summary>
public sealed class AttLeaveTests
{
    private static readonly DateOnly Jun1 = new(2026, 6, 1); // a Monday

    private static async Task<(AttHarness H, EmployeeDetail E, Guid Annual)> SetupAsync(decimal days = 10)
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        var annual = await h.LeaveTypeAsync("ANNUAL");
        if (days > 0) await h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = e.Uuid, LeaveTypeUuid = annual, Days = days, Year = 2026 });
        return (h, e, annual);
    }

    private static Task<LeaveRequestModel> AskAsync(AttHarness h, Guid employee, Guid type, DateOnly from, DateOnly? to = null, bool half = false, Guid? attachment = null) =>
        h.Leave.RequestLeaveAsync(h.Admin(), new LeaveRequestCreate { EmployeeUuid = employee, LeaveTypeUuid = type, FromDate = from, ToDate = to ?? from, HalfDay = half, AttachmentUuid = attachment });

    private static Task<decimal> BalanceAsync(AttHarness h, Guid employee, Guid type, int year = 2026) => h.Leave.GetBalanceAsync(h.Admin(), employee, type, year);

    [Fact]
    public async Task Balance_is_the_sum_of_the_ledger_through_request_approve_and_cancel()
    {
        var (h, e, annual) = await SetupAsync();
        (await BalanceAsync(h, e.Uuid, annual)).Should().Be(10m);

        var req = await AskAsync(h, e.Uuid, annual, Jun1, Jun1.AddDays(6));
        req.Status.Should().Be(LeaveRequestStatuses.Pending);
        req.Days.Should().Be(5m, "Saturday and Sunday do not count");
        req.RequestNumber.Should().Be("LV-2026-00001");
        req.ApprovalUuid.Should().NotBeNull();
        h.Workflow.LastSubmit!.InterfaceCode.Should().Be(HrmWorkflowCodes.LeaveRequest);
        h.Workflow.LastSubmit.SkipStatusCheck.Should().BeTrue();
        (await BalanceAsync(h, e.Uuid, annual)).Should().Be(10m, "pending does not deduct");
        (await h.Leave.BalancesAsync(h.Admin(), e.Uuid, 2026)).Single(b => b.LeaveTypeCode == "ANNUAL").Pending.Should().Be(5m);

        var approved = await h.Leave.ApproveAsync(h.ApproverCaller(), req.Uuid);
        approved.Status.Should().Be(LeaveRequestStatuses.Approved);
        approved.DecidedByName.Should().Be("Approver");
        (await BalanceAsync(h, e.Uuid, annual)).Should().Be(5m);
        var line = (await h.Leave.BalancesAsync(h.Admin(), e.Uuid, 2026)).Single(b => b.LeaveTypeCode == "ANNUAL");
        (line.Allocated, line.Used, line.Balance, line.Pending).Should().Be((10m, 5m, 5m, 0m));

        // Attendance follows.
        var day = (await h.Attendance.ListDaysAsync(h.Admin(), new AttendanceDayFilter { EmployeeUuid = e.Uuid, From = Jun1, To = Jun1 })).Data.Single();
        day.Status.Should().Be(AttendanceDayStatuses.OnLeave);

        var cancelled = await h.Leave.CancelAsync(h.Admin(), req.Uuid);
        cancelled.Status.Should().Be(LeaveRequestStatuses.Cancelled);
        (await BalanceAsync(h, e.Uuid, annual)).Should().Be(10m);
        (await h.Leave.LedgerAsync(h.Admin(), e.Uuid, annual, 2026)).Select(l => l.Reason).Should().Contain([LeaveLedgerReasons.Consumption, LeaveLedgerReasons.CancellationRestore]);
        (await h.Attendance.ListDaysAsync(h.Admin(), new AttendanceDayFilter { EmployeeUuid = e.Uuid, From = Jun1, To = Jun1 })).Data.Single().Status.Should().Be(AttendanceDayStatuses.Absent);

        await FluentActions.Awaiting(() => h.Leave.CancelAsync(h.Admin(), req.Uuid)).Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_ALREADY_DECIDED*");
    }

    [Fact]
    public async Task Insufficient_balance_is_refused_and_pending_days_are_already_spoken_for()
    {
        var (h, e, annual) = await SetupAsync(days: 3);
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, annual, Jun1, Jun1.AddDays(3))).Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_INSUFFICIENT_BALANCE*");
        (await h.Db.LeaveRequests.CountAsync()).Should().Be(0);

        await AskAsync(h, e.Uuid, annual, Jun1, Jun1.AddDays(1));
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, annual, Jun1.AddDays(7), Jun1.AddDays(8))).Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_INSUFFICIENT_BALANCE*");
        (await AskAsync(h, e.Uuid, annual, Jun1.AddDays(7))).Days.Should().Be(1m);
    }

    [Fact]
    public async Task Approving_rechecks_the_balance()
    {
        var (h, e, annual) = await SetupAsync(days: 2);
        var req = await AskAsync(h, e.Uuid, annual, Jun1, Jun1.AddDays(1));
        await h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = e.Uuid, LeaveTypeUuid = annual, Days = -1, Year = 2026, Adjustment = true });
        await FluentActions.Awaiting(() => h.Leave.ApproveAsync(h.ApproverCaller(), req.Uuid)).Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_INSUFFICIENT_BALANCE*");
    }

    [Fact]
    public async Task Unpaid_leave_needs_no_quota_and_allow_negative_passes()
    {
        var (h, e, annual) = await SetupAsync(days: 0);
        (await AskAsync(h, e.Uuid, await h.LeaveTypeAsync("UNPAID"), Jun1)).Days.Should().Be(1m);

        await h.LeaveTypes.UpdateAsync(h.Admin(), annual, new LeaveTypeUpsert { Code = "ANNUAL", Name = "Annual Leave", IsPaid = true, CarryForwardCap = 14, AllowNegativeBalance = true });
        (await AskAsync(h, e.Uuid, annual, Jun1.AddDays(1))).Status.Should().Be(LeaveRequestStatuses.Pending);
    }

    [Fact]
    public async Task Overlapping_a_pending_or_approved_request_is_refused_a_cancelled_one_is_not()
    {
        var (h, e, annual) = await SetupAsync();
        var first = await AskAsync(h, e.Uuid, annual, Jun1, Jun1.AddDays(2));
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, annual, Jun1.AddDays(2), Jun1.AddDays(3))).Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_OVERLAP*");
        await h.Leave.ApproveAsync(h.ApproverCaller(), first.Uuid);
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, annual, Jun1)).Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_OVERLAP*");
        await h.Leave.CancelAsync(h.Admin(), first.Uuid);
        (await AskAsync(h, e.Uuid, annual, Jun1)).Status.Should().Be(LeaveRequestStatuses.Pending);
    }

    [Fact]
    public async Task Validation_attachment_gender_holidays_half_days_and_year_ends()
    {
        var (h, e, annual) = await SetupAsync();
        var sick = await h.LeaveTypeAsync("SICK");
        await h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = e.Uuid, LeaveTypeUuid = sick, Days = 10, Year = 2026 });

        (await AskAsync(h, e.Uuid, sick, Jun1, Jun1.AddDays(1))).Days.Should().Be(2m, "attachment needed only above 2");
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, sick, Jun1.AddDays(7), Jun1.AddDays(9))).Should().ThrowAsync<BadRequestException>().WithMessage("LEAVE_ATTACHMENT_REQUIRED*");
        (await AskAsync(h, e.Uuid, sick, Jun1.AddDays(7), Jun1.AddDays(9), attachment: Guid.NewGuid())).AttachmentUuid.Should().NotBeNull();

        var maternity = await h.LeaveTypeAsync("MATERNITY");
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, maternity, Jun1)).Should().ThrowAsync<BadRequestException>().WithMessage("LEAVE_GENDER_RESTRICTED*");

        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, annual, new DateOnly(2026, 6, 6), new DateOnly(2026, 6, 7))).Should().ThrowAsync<BadRequestException>().WithMessage("LEAVE_NO_WORKING_DAYS*");
        await h.Holidays.CreateAsync(h.Admin(), new HolidayUpsert { Date = Jun1.AddDays(14), Name = "Declared" });
        (await AskAsync(h, e.Uuid, annual, Jun1.AddDays(14), Jun1.AddDays(15))).Days.Should().Be(1m, "the holiday does not count");

        (await AskAsync(h, e.Uuid, annual, Jun1.AddDays(21), half: true)).Days.Should().Be(0.5m);
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, annual, Jun1.AddDays(22), Jun1.AddDays(23), half: true)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, annual, new DateOnly(2026, 12, 31), new DateOnly(2027, 1, 1))).Should().ThrowAsync<BadRequestException>().WithMessage("LEAVE_SPANS_YEARS*");
    }

    [Fact]
    public async Task When_nobody_can_approve_or_the_engine_refuses_the_request_is_taken_back()
    {
        var (h, e, annual) = await SetupAsync();
        h.Users.Users.Clear();
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, annual, Jun1)).Should().ThrowAsync<ConflictException>().WithMessage("HRM_NO_APPROVER*");
        (await h.Db.LeaveRequests.CountAsync()).Should().Be(0);

        h.Users.Add(AttHarness.Approver, "Approver", P.HRM_LEAVE_APPROVE);
        h.Workflow.RefuseSubmitWith = new UnprocessableEntityException("No workflow definition.");
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, annual, Jun1)).Should().ThrowAsync<UnprocessableEntityException>();
        (await h.Db.LeaveRequests.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_line_manager_is_the_approver_and_only_the_assigned_approver_can_decide()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var annual = await h.LeaveTypeAsync("ANNUAL");
        var boss = await h.HireAsync("Boss", userId: 600);
        h.Users.Add(601, "Staff");
        var e = await h.Employees.CreateAsync(h.Admin(), new EmployeeUpsert
        {
            FirstName = "Staff", LastName = "Test", UserId = 601, JoinDate = new DateOnly(2026, 1, 1),
            Employment = new EmploymentRecordCreate { EffectiveFrom = new DateOnly(2026, 1, 1), DepartmentUuid = h.Department, DesignationUuid = h.Designation, BranchUuid = h.Branch, ManagerUuid = boss.Uuid, ChangeReason = "Hire" }
        });
        await h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = e.Uuid, LeaveTypeUuid = annual, Days = 5, Year = 2026 });

        var staff = h.As(601, P.HRM_SELF_SERVICE);
        var req = await h.Leave.RequestLeaveAsync(staff, new LeaveRequestCreate { LeaveTypeUuid = annual, FromDate = Jun1, ToDate = Jun1 });
        h.Workflow.ApproverOf(req.Uuid).Should().Be(600);

        await FluentActions.Awaiting(() => h.Leave.ApproveAsync(h.ApproverCaller(), req.Uuid)).Should().ThrowAsync<ForbiddenException>("the engine decides who may act");
        (await BalanceAsync(h, e.Uuid, annual)).Should().Be(5m);

        var bossCaller = h.As(600, P.HRM_LEAVE_APPROVE);
        (await h.Leave.ApproveAsync(bossCaller, req.Uuid)).Status.Should().Be(LeaveRequestStatuses.Approved);
        await FluentActions.Awaiting(() => h.Leave.ApproveAsync(bossCaller, req.Uuid)).Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_ALREADY_DECIDED*");
    }

    [Fact]
    public async Task Rejecting_takes_a_note_deducts_nothing_and_releases_the_dates()
    {
        var (h, e, annual) = await SetupAsync();
        var req = await AskAsync(h, e.Uuid, annual, Jun1);
        await FluentActions.Awaiting(() => h.Leave.RejectAsync(h.ApproverCaller(), req.Uuid, "")).Should().ThrowAsync<BadRequestException>();
        var rejected = await h.Leave.RejectAsync(h.ApproverCaller(), req.Uuid, "Busy week");
        rejected.Status.Should().Be(LeaveRequestStatuses.Rejected);
        rejected.DecisionNote.Should().Be("Busy week");
        (await BalanceAsync(h, e.Uuid, annual)).Should().Be(10m);
        (await AskAsync(h, e.Uuid, annual, Jun1)).Status.Should().Be(LeaveRequestStatuses.Pending);
    }

    [Fact]
    public async Task Cancelling_a_pending_request_goes_through_the_engine_and_restores_nothing_because_nothing_was_taken()
    {
        var (h, e, annual) = await SetupAsync();
        var req = await AskAsync(h, e.Uuid, annual, Jun1);
        (await h.Leave.CancelAsync(h.Admin(), req.Uuid)).Status.Should().Be(LeaveRequestStatuses.Cancelled);
        (await h.Leave.LedgerAsync(h.Admin(), e.Uuid, annual, 2026)).Should().ContainSingle();
    }

    [Fact]
    public async Task The_status_handler_serves_the_inbox_path_and_repeats_are_harmless()
    {
        var (h, e, annual) = await SetupAsync();
        var req = await AskAsync(h, e.Uuid, annual, Jun1);
        h.Handler.InterfaceCode.Should().Be("LEAVE_REQUEST");
        (await h.Handler.GetStatusAsync(req.Uuid)).Should().Be("PENDING");

        await h.Handler.UpdateStatusAsync(req.Uuid, "PENDING");
        await h.Handler.UpdateStatusAsync(req.Uuid, "UNDER_REVIEW");
        (await h.Handler.GetStatusAsync(req.Uuid)).Should().Be("PENDING");

        await h.Handler.UpdateStatusAsync(req.Uuid, "APPROVED");
        await h.Handler.UpdateStatusAsync(req.Uuid, "APPROVED");
        await h.Handler.UpdateStatusAsync(req.Uuid, "REJECTED");
        (await h.Handler.GetStatusAsync(req.Uuid)).Should().Be("APPROVED");
        (await BalanceAsync(h, e.Uuid, annual)).Should().Be(9m, "one deduction");

        var second = await AskAsync(h, e.Uuid, annual, Jun1.AddDays(1));
        await h.Handler.UpdateStatusAsync(second.Uuid, "RECALLED");
        (await h.Handler.GetStatusAsync(second.Uuid)).Should().Be("CANCELLED");
        await h.Handler.UpdateStatusAsync(Guid.NewGuid(), "APPROVED");
    }

    [Fact]
    public async Task Carry_forward_caps_expires_the_rest_and_is_idempotent()
    {
        var (h, e, annual) = await SetupAsync(days: 20);
        var result = await h.Leave.CarryForwardAsync(h.Admin(), new LeaveCarryForwardRequest { FromYear = 2026, ToYear = 2027 });
        result.Processed.Should().Be(1);
        result.Carried.Should().Be(14m, "the seeded cap");
        result.Expired.Should().Be(6m);
        (await BalanceAsync(h, e.Uuid, annual)).Should().Be(0m);
        (await BalanceAsync(h, e.Uuid, annual, 2027)).Should().Be(14m);

        var again = await h.Leave.CarryForwardAsync(h.Admin(), new LeaveCarryForwardRequest { FromYear = 2026, ToYear = 2027 });
        (again.Processed, again.Carried, again.Expired).Should().Be((0, 0m, 0m));
        (await BalanceAsync(h, e.Uuid, annual, 2027)).Should().Be(14m);

        await FluentActions.Awaiting(() => h.Leave.CarryForwardAsync(h.Admin(), new LeaveCarryForwardRequest { FromYear = 2026, ToYear = 2028 })).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_type_with_no_cap_does_not_carry_and_a_leave_cancelled_after_the_close_stays_inside_the_cap()
    {
        var (h, e, annual) = await SetupAsync(days: 16);
        var sick = await h.LeaveTypeAsync("SICK");
        await h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = e.Uuid, LeaveTypeUuid = sick, Days = 4, Year = 2026 });
        var req = await AskAsync(h, e.Uuid, annual, Jun1, Jun1.AddDays(1));
        await h.Leave.ApproveAsync(h.ApproverCaller(), req.Uuid);

        await h.Leave.CarryForwardAsync(h.Admin(), new LeaveCarryForwardRequest { FromYear = 2026, ToYear = 2027 });
        (await BalanceAsync(h, e.Uuid, sick, 2027)).Should().Be(0m);
        (await BalanceAsync(h, e.Uuid, annual, 2027)).Should().Be(14m);

        await h.Leave.CancelAsync(h.Admin(), req.Uuid);
        (await BalanceAsync(h, e.Uuid, annual)).Should().Be(2m);
        var late = await h.Leave.CarryForwardAsync(h.Admin(), new LeaveCarryForwardRequest { FromYear = 2026, ToYear = 2027 });
        late.Carried.Should().Be(0m, "14 already carried");
        late.Expired.Should().Be(2m);
        (await BalanceAsync(h, e.Uuid, annual, 2027)).Should().Be(14m);
    }

    [Fact]
    public async Task Bulk_allocation_covers_active_employees_once_and_respects_gender_and_grade()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var admin = h.Admin();
        var grade = await h.Grades.CreateAsync(admin, new GradeUpsert { Code = "G9", Name = "Senior" });
        var a = await h.HireAsync("A", gender: "F");
        var b = await h.HireAsync("B", gender: "M", gradeUuid: grade.Uuid);
        var gone = await h.HireAsync("Gone");
        await h.Employees.ChangeStatusAsync(admin, gone.Uuid, new EmployeeStatusChange { Status = EmployeeStatuses.Terminated, EffectiveDate = new DateOnly(2026, 3, 31), DepartureReason = DepartureReasons.Resignation });

        var annual = await h.LeaveTypeAsync("ANNUAL");
        await h.LeaveTypes.AddPolicyAsync(admin, annual, new LeavePolicyUpsert { GradeUuid = grade.Uuid, Entitlement = 20, AccrualMethod = "Annual", EffectiveFrom = new DateOnly(2026, 1, 1) });

        var result = await h.Leave.BulkAllocateAsync(admin, new LeaveBulkAllocateRequest { LeaveTypeUuid = annual, Year = 2026 });
        (result.Allocated, result.Skipped).Should().Be((2, 0));
        (await BalanceAsync(h, a.Uuid, annual)).Should().Be(14m);
        (await BalanceAsync(h, b.Uuid, annual)).Should().Be(20m, "the grade's own policy");
        (await BalanceAsync(h, gone.Uuid, annual)).Should().Be(0m);

        var again = await h.Leave.BulkAllocateAsync(admin, new LeaveBulkAllocateRequest { LeaveTypeUuid = annual, Year = 2026 });
        (again.Allocated, again.Skipped).Should().Be((0, 2));
        (await BalanceAsync(h, a.Uuid, annual)).Should().Be(14m);

        var maternity = await h.LeaveTypeAsync("MATERNITY");
        var m = await h.Leave.BulkAllocateAsync(admin, new LeaveBulkAllocateRequest { LeaveTypeUuid = maternity, Year = 2026 });
        (m.Allocated, m.Skipped).Should().Be((1, 1));
        (await BalanceAsync(h, a.Uuid, maternity)).Should().Be(90m);
        (await BalanceAsync(h, b.Uuid, maternity)).Should().Be(0m);

        var unpaid = await h.Leave.BulkAllocateAsync(admin, new LeaveBulkAllocateRequest { LeaveTypeUuid = await h.LeaveTypeAsync("UNPAID"), Year = 2026 });
        unpaid.Allocated.Should().Be(0, "accrual None grants nothing");

        // Scope: a caller who sees nobody allocates to nobody.
        (await h.Leave.BulkAllocateAsync(h.As(77, P.HRM_LEAVE_MANAGE), new LeaveBulkAllocateRequest { LeaveTypeUuid = await h.LeaveTypeAsync("CASUAL"), Year = 2026 })).Allocated.Should().Be(0);
    }

    [Fact]
    public async Task Monthly_accrual_tops_up_to_what_is_due_each_run()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var e = await h.HireAsync();
        var admin = h.Admin();
        var type = await h.LeaveTypes.CreateAsync(admin, new LeaveTypeUpsert { Code = "EARNED", Name = "Earned" });
        await h.LeaveTypes.AddPolicyAsync(admin, type.Uuid, new LeavePolicyUpsert { Entitlement = 12, AccrualMethod = "Monthly", EffectiveFrom = new DateOnly(2026, 1, 1) });

        await h.Leave.BulkAllocateAsync(admin, new LeaveBulkAllocateRequest { LeaveTypeUuid = type.Uuid, Year = 2026 });
        (await BalanceAsync(h, e.Uuid, type.Uuid)).Should().Be(6m, "January to June");
        await h.Leave.BulkAllocateAsync(admin, new LeaveBulkAllocateRequest { LeaveTypeUuid = type.Uuid, Year = 2026 });
        (await BalanceAsync(h, e.Uuid, type.Uuid)).Should().Be(6m);

        h.Clock.UtcNow = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        await h.Leave.BulkAllocateAsync(admin, new LeaveBulkAllocateRequest { LeaveTypeUuid = type.Uuid, Year = 2026 });
        (await BalanceAsync(h, e.Uuid, type.Uuid)).Should().Be(8m);
    }

    [Fact]
    public async Task A_second_allocation_is_refused_an_adjustment_is_not()
    {
        var (h, e, annual) = await SetupAsync();
        await FluentActions.Awaiting(() => h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = e.Uuid, LeaveTypeUuid = annual, Days = 5, Year = 2026 }))
            .Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_ALREADY_ALLOCATED*");
        (await h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = e.Uuid, LeaveTypeUuid = annual, Days = -2, Year = 2026, Adjustment = true, Note = "Correction" })).Reason.Should().Be(LeaveLedgerReasons.Adjustment);
        (await BalanceAsync(h, e.Uuid, annual)).Should().Be(8m);
        (await h.Leave.BalancesAsync(h.Admin(), e.Uuid, 2026)).Single(b => b.LeaveTypeCode == "ANNUAL").Adjusted.Should().Be(-2m);
    }

    [Fact]
    public async Task Leave_type_and_policy_configuration()
    {
        var h = await AttHarness.Memory().ReadyAsync();
        var admin = h.Admin();
        (await h.LeaveTypes.ListAsync(admin, false)).Should().HaveCount(6, "the seeded standard types");
        var annualSeed = (await h.LeaveTypes.ListAsync(admin, false)).Single(t => t.Code == "ANNUAL");
        annualSeed.Policies.Single().Entitlement.Should().Be(14m);
        annualSeed.CarryForwardCap.Should().Be(14m);

        var type = await h.LeaveTypes.CreateAsync(admin, new LeaveTypeUpsert { Code = "study", Name = "Study", CarryForwardCap = 3, AttachmentAfterDays = 1, GenderRestriction = "f" });
        type.Code.Should().Be("STUDY");
        type.GenderRestriction.Should().Be("F");
        await FluentActions.Awaiting(() => h.LeaveTypes.CreateAsync(admin, new LeaveTypeUpsert { Code = "STUDY", Name = "X" })).Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_TYPE_CODE_TAKEN*");
        await FluentActions.Awaiting(() => h.LeaveTypes.CreateAsync(admin, new LeaveTypeUpsert { Code = "NEG", Name = "X", CarryForwardCap = -1 })).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => h.LeaveTypes.CreateAsync(admin, new LeaveTypeUpsert { Code = "GEN", Name = "X", GenderRestriction = "Q" })).Should().ThrowAsync<BadRequestException>();

        var p1 = await h.LeaveTypes.AddPolicyAsync(admin, type.Uuid, new LeavePolicyUpsert { Entitlement = 5, EffectiveFrom = new DateOnly(2026, 1, 1) });
        p1.AccrualMethod.Should().Be(LeaveAccrualMethods.Annual);
        await h.LeaveTypes.AddPolicyAsync(admin, type.Uuid, new LeavePolicyUpsert { Entitlement = 7, AccrualMethod = "Monthly", EffectiveFrom = new DateOnly(2027, 1, 1) });
        var policies = (await h.LeaveTypes.ListAsync(admin, false)).Single(t => t.Code == "STUDY").Policies;
        policies.First().EffectiveTo.Should().Be(new DateOnly(2026, 12, 31), "the open policy closes when the next starts");
        await FluentActions.Awaiting(() => h.LeaveTypes.AddPolicyAsync(admin, type.Uuid, new LeavePolicyUpsert { Entitlement = 1, EffectiveFrom = new DateOnly(2026, 6, 1) })).Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_POLICY_OVERLAP*");
        await FluentActions.Awaiting(() => h.LeaveTypes.AddPolicyAsync(admin, type.Uuid, new LeavePolicyUpsert { Entitlement = 1, AccrualMethod = "Sometimes", EffectiveFrom = new DateOnly(2028, 1, 1) })).Should().ThrowAsync<BadRequestException>();
        (await h.LeaveTypes.UpdatePolicyAsync(admin, p1.Uuid, new LeavePolicyUpsert { Entitlement = 6, EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = new DateOnly(2026, 12, 31) })).Entitlement.Should().Be(6m);

        (await h.LeaveTypes.SetActiveAsync(admin, type.Uuid, false)).IsActive.Should().BeFalse();
        (await h.LeaveTypes.ListAsync(admin, false)).Should().NotContain(t => t.Code == "STUDY");

        var e = await h.HireAsync();
        var casual = await h.LeaveTypeAsync("CASUAL");
        await h.Leave.AllocateAsync(admin, new LeaveAllocateRequest { EmployeeUuid = e.Uuid, LeaveTypeUuid = casual, Days = 2, Year = 2026 });
        await AskAsync(h, e.Uuid, casual, Jun1);
        await FluentActions.Awaiting(() => h.LeaveTypes.SetActiveAsync(admin, casual, false)).Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_TYPE_IN_USE*");
        await FluentActions.Awaiting(() => AskAsync(h, e.Uuid, type.Uuid, Jun1.AddDays(1))).Should().ThrowAsync<BadRequestException>().WithMessage("LEAVE_TYPE_INACTIVE*");
    }

    [Fact]
    public async Task Scope_self_service_and_another_organization()
    {
        var name = "att-" + Guid.NewGuid();
        var h = await AttHarness.Memory(name).ReadyAsync();
        var annual = await h.LeaveTypeAsync("ANNUAL");
        var me = await h.HireAsync("Me", userId: 500);
        var other = await h.HireAsync("Other");
        await h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = me.Uuid, LeaveTypeUuid = annual, Days = 5, Year = 2026 });
        await h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = other.Uuid, LeaveTypeUuid = annual, Days = 5, Year = 2026 });

        var self = h.As(500, P.HRM_SELF_SERVICE);
        var mine = await h.Leave.RequestLeaveAsync(self, new LeaveRequestCreate { LeaveTypeUuid = annual, FromDate = Jun1, ToDate = Jun1 });
        var theirs = await AskAsync(h, other.Uuid, annual, Jun1);

        (await h.Leave.OwnRequestsAsync(self)).Single().Uuid.Should().Be(mine.Uuid);
        (await h.Leave.BalancesAsync(self, null, 2026)).Single(b => b.LeaveTypeCode == "ANNUAL").Balance.Should().Be(5m);
        await FluentActions.Awaiting(() => h.Leave.BalancesAsync(self, other.Uuid, 2026)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => h.Leave.GetRequestAsync(self, theirs.Uuid)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => h.Leave.CancelAsync(self, theirs.Uuid)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => h.Leave.RequestLeaveAsync(self, new LeaveRequestCreate { EmployeeUuid = other.Uuid, LeaveTypeUuid = annual, FromDate = Jun1.AddDays(7), ToDate = Jun1.AddDays(7) })).Should().ThrowAsync<NotFoundException>();
        (await h.Leave.GetRequestAsync(self, mine.Uuid)).Uuid.Should().Be(mine.Uuid);

        // Nobody decides their own.
        await FluentActions.Awaiting(() => h.Leave.ApproveAsync(new(h.Org, 500, P.HrmCodes, "Me"), mine.Uuid)).Should().ThrowAsync<ForbiddenException>();

        var outsider = h.As(77, P.HRM_LEAVE_VIEW, P.HRM_LEAVE_APPROVE);
        (await h.Leave.ListRequestsAsync(outsider, new LeaveRequestFilter())).Data.Should().BeEmpty();
        await FluentActions.Awaiting(() => h.Leave.ApproveAsync(outsider, mine.Uuid)).Should().ThrowAsync<NotFoundException>();

        var b = await AttHarness.Memory(name).ReadyAsync();
        await FluentActions.Awaiting(() => b.Leave.GetRequestAsync(b.Admin(), mine.Uuid)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => b.Leave.CancelAsync(b.Admin(), mine.Uuid)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => b.Leave.GetBalanceAsync(b.Admin(), me.Uuid, annual, 2026)).Should().ThrowAsync<NotFoundException>();
        var bEmployee = await b.HireAsync("X");
        await FluentActions.Awaiting(() => AskAsync(b, bEmployee.Uuid, annual, Jun1)).Should().ThrowAsync<NotFoundException>("another organization's leave type");
        (await b.Leave.ListRequestsAsync(b.Admin(), new LeaveRequestFilter())).Data.Should().BeEmpty();
    }

    [Fact]
    public async Task The_calendar_shows_names_and_dates_of_approved_leave_in_scope()
    {
        var (h, e, annual) = await SetupAsync();
        var req = await AskAsync(h, e.Uuid, annual, Jun1);
        (await h.Leave.CalendarAsync(h.Admin(), Jun1, Jun1, null)).Should().BeEmpty("still pending");
        await h.Leave.ApproveAsync(h.ApproverCaller(), req.Uuid);
        var entry = (await h.Leave.CalendarAsync(h.Admin(), Jun1, Jun1, h.Department)).Single();
        entry.EmployeeName.Should().Be("Ali Test");
        typeof(LeaveCalendarEntry).GetProperties().Select(p => p.Name).Should().NotContain(["LeaveTypeName", "Reason"]);
        (await h.Leave.CalendarAsync(h.As(77, P.HRM_SELF_SERVICE), Jun1, Jun1, null)).Should().BeEmpty();
    }
}

/// <summary>What only SQL Server proves: query translation, the app lock, the retrying strategy, the unique indexes.</summary>
[Trait("Category", "SqlServer")]
public sealed class AttLocalDbTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A49Att_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();

    private HrmDbContext NewContext() => new(
        new DbContextOptionsBuilder<HrmDbContext>().UseSqlServer(_connection, sql => sql.EnableRetryOnFailure(3)).Options,
        new StaticTenantContext { OrganizationId = _org });

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task The_whole_cycle_runs_on_SQL_Server_under_the_retrying_strategy()
    {
        await using var db = NewContext();
        var h = await new AttHarness(db, _org).ReadyAsync();
        var admin = h.Admin();
        var e = await h.HireAsync();
        var id = await h.IdOfAsync(e.Uuid);
        var annual = await h.LeaveTypeAsync("ANNUAL");
        var jun1 = new DateOnly(2026, 6, 1);

        // Attendance
        var ev = new AttendanceEventRequest { EmployeeUuid = e.Uuid, EventTimeUtc = AttHarness.Pkt(6, 1, 9), EventType = "IN", Source = "DEVICE", ExternalEventId = "L1" };
        (await h.Attendance.RecordEventAsync(admin, ev)).Duplicate.Should().BeFalse();
        (await h.Attendance.RecordEventAsync(admin, ev)).Duplicate.Should().BeTrue();
        await h.Attendance.RecordEventAsync(admin, new AttendanceEventRequest { EmployeeUuid = e.Uuid, EventTimeUtc = AttHarness.Pkt(6, 1, 20), EventType = "OUT" });
        using var csv = new MemoryStream(System.Text.Encoding.UTF8.GetBytes($"{e.EmployeeNumber},2026-06-02T04:00:00Z,IN,C1\n{e.EmployeeNumber},2026-06-02T13:00:00Z,OUT,C2\n"));
        (await h.Attendance.ImportEventsAsync(admin, csv)).Imported.Should().Be(2);
        var page = await h.Attendance.ListDaysAsync(admin, new AttendanceDayFilter { EmployeeUuid = e.Uuid, DepartmentUuid = h.Department, BranchUuid = h.Branch, From = jun1, To = jun1.AddDays(6) });
        page.TotalRecords.Should().Be(7);
        page.Data.Single(d => d.Date == jun1).OvertimeMinutes.Should().Be(120);
        (await h.Attendance.GetMonthlySummaryAsync(admin, e.Uuid, 2026, 6)).PresentDays.Should().Be(2);
        await h.Attendance.RecomputeAsync(admin, new AttendanceRecomputeRequest { From = jun1, To = jun1.AddDays(2) });

        // Overtime
        var ot = await h.Overtime.SubmitAsync(admin, new OvertimeCreate { EmployeeUuid = e.Uuid, Date = jun1, Multiplier = 1.5m });
        await FluentActions.Awaiting(() => h.Overtime.SubmitAsync(admin, new OvertimeCreate { EmployeeUuid = e.Uuid, Date = jun1, Minutes = 1 })).Should().ThrowAsync<ConflictException>();
        await h.Overtime.ApproveAsync(admin, ot.Uuid);
        (await h.Overtime.ListAsync(admin, "Approved", jun1, jun1, e.Uuid)).Should().ContainSingle();
        var pay = await h.Pay.GetAsync(_org, id, jun1, new DateOnly(2026, 6, 30));
        pay.ApprovedOvertimeMinutes.Should().Be(120);
        pay.WeightedOvertimeMinutes.Should().Be(180m);

        // Leave
        await h.Leave.BulkAllocateAsync(admin, new LeaveBulkAllocateRequest { LeaveTypeUuid = annual, Year = 2026 });
        (await h.Leave.GetBalanceAsync(admin, e.Uuid, annual, 2026)).Should().Be(14m);
        var req = await h.Leave.RequestLeaveAsync(admin, new LeaveRequestCreate { EmployeeUuid = e.Uuid, LeaveTypeUuid = annual, FromDate = new DateOnly(2026, 6, 8), ToDate = new DateOnly(2026, 6, 14) });
        req.Days.Should().Be(5m);
        var second = await h.Leave.RequestLeaveAsync(admin, new LeaveRequestCreate { EmployeeUuid = e.Uuid, LeaveTypeUuid = annual, FromDate = new DateOnly(2026, 6, 15), ToDate = new DateOnly(2026, 6, 15) });
        second.RequestNumber.Should().Be("LV-2026-00002");
        await FluentActions.Awaiting(() => h.Leave.RequestLeaveAsync(admin, new LeaveRequestCreate { EmployeeUuid = e.Uuid, LeaveTypeUuid = annual, FromDate = new DateOnly(2026, 6, 10), ToDate = new DateOnly(2026, 6, 10) }))
            .Should().ThrowAsync<ConflictException>().WithMessage("LEAVE_OVERLAP*");
        await h.Leave.ApproveAsync(h.ApproverCaller(), req.Uuid);
        await h.Leave.RejectAsync(h.ApproverCaller(), second.Uuid, "No");
        (await h.Leave.GetBalanceAsync(admin, e.Uuid, annual, 2026)).Should().Be(9m);
        (await h.Leave.BalancesAsync(admin, e.Uuid, 2026)).Should().Contain(b => b.LeaveTypeCode == "ANNUAL" && b.Used == 5m);
        (await h.Leave.LedgerAsync(admin, e.Uuid, annual, 2026)).Should().HaveCount(2);
        (await h.Leave.ListRequestsAsync(admin, new LeaveRequestFilter { Status = "Approved", From = jun1 })).TotalRecords.Should().Be(1);
        (await h.Leave.CalendarAsync(admin, jun1, new DateOnly(2026, 6, 30), h.Department)).Should().ContainSingle();
        (await h.Attendance.GetMonthlySummaryAsync(admin, e.Uuid, 2026, 6)).LeaveDays.Should().Be(5m);
        var carry = await h.Leave.CarryForwardAsync(admin, new LeaveCarryForwardRequest { FromYear = 2026, ToYear = 2027 });
        carry.Carried.Should().Be(9m);
        (await h.Leave.CarryForwardAsync(admin, new LeaveCarryForwardRequest { FromYear = 2026, ToYear = 2027 })).Processed.Should().Be(0);
        await h.Leave.CancelAsync(admin, req.Uuid);
        (await h.Leave.GetBalanceAsync(admin, e.Uuid, annual, 2026)).Should().Be(5m);

        // Configuration, including the seeded rows' RowVersion and the cross-table projections.
        var types = await h.LeaveTypes.ListAsync(admin, false);
        types.Single(t => t.Code == "ANNUAL").Policies.Should().ContainSingle();
        var shift = await h.Shifts.CreateAsync(admin, new ShiftUpsert { Code = "N", Name = "Night", StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0), BreakMinutes = 30, WeeklyOffDays = 1 });
        await h.Shifts.AssignAsync(admin, shift.Uuid, new ShiftAssignRequest { EmployeeUuids = [e.Uuid], EffectiveFrom = new DateOnly(2026, 6, 20) });
        var holiday = await h.Holidays.CreateAsync(admin, new HolidayUpsert { Date = jun1, Name = "Declared", BranchUuid = h.Branch });
        (await h.Holidays.ListAsync(admin, 2026, h.Branch)).Should().Contain(x => x.Uuid == holiday.Uuid && x.BranchUuid == h.Branch);
        (await h.Holidays.IsHolidayAsync(admin, jun1, h.Branch)).Should().BeTrue();
    }

    [Fact]
    public async Task Two_concurrent_requests_cannot_spend_the_same_days()
    {
        Guid annual, employee;
        Guid department, designation, branch;
        await using (var setup = NewContext())
        {
            var h = await new AttHarness(setup, _org).ReadyAsync();
            var e = await h.HireAsync();
            employee = e.Uuid;
            annual = await h.LeaveTypeAsync("ANNUAL");
            await h.Leave.AllocateAsync(h.Admin(), new LeaveAllocateRequest { EmployeeUuid = employee, LeaveTypeUuid = annual, Days = 1, Year = 2026 });
            (department, designation, branch) = (h.Department, h.Designation, h.Branch);
        }

        async Task<bool> Ask(DateOnly date)
        {
            await using var db = NewContext();
            var h = new AttHarness(db, _org);
            try
            {
                await h.Leave.RequestLeaveAsync(h.Admin(), new LeaveRequestCreate { EmployeeUuid = employee, LeaveTypeUuid = annual, FromDate = date, ToDate = date });
                return true;
            }
            catch (ConflictException ex) when (ex.Message.StartsWith("LEAVE_INSUFFICIENT_BALANCE"))
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Ask(new DateOnly(2026, 6, 1)), Ask(new DateOnly(2026, 6, 2)), Ask(new DateOnly(2026, 6, 3)), Ask(new DateOnly(2026, 6, 4)));
        results.Count(r => r).Should().Be(1);
        department.Should().NotBeEmpty();
        designation.Should().NotBeEmpty();
        branch.Should().NotBeEmpty();
    }
}
