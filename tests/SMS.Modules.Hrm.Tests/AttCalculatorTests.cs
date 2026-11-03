using FluentAssertions;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Services.Att;
using Xunit;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Tasks 108, 124, 126: the day rules without a database. All times are one zone here (local = utc), which the rules only compare.</summary>
public sealed class AttCalculatorTests
{
    private sealed class FakeZones(string? value) : SMS.Shared.Common.IOrganizationTimeZoneLookup
    {
        public Task<string?> GetTimeZoneAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult(value);
    }

    [Theory]
    [InlineData("Asia/Dubai", "PK", 4)]
    [InlineData(null, "PK", 5)]
    [InlineData("", "PK", 5)]
    [InlineData("Not/AZone", "PK", 5)]
    [InlineData("Not/AZone", "AE", 0)]
    [InlineData(null, "AE", 0)]
    public async Task The_organizations_zone_wins_and_an_unusable_value_falls_back_to_the_country(string? stored, string country, int hours)
    {
        var zone = await new AttendanceClock(new FakeZones(stored)).ZoneForAsync(Guid.NewGuid(), country);
        zone.BaseUtcOffset.Should().Be(TimeSpan.FromHours(hours));
    }

    private static readonly DateOnly Monday = new(2026, 6, 1);

    private static Shift General() => new()
    {
        StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(18, 0), BreakMinutes = 60, GraceInMinutes = 15, GraceOutMinutes = 0, HalfDayMinutes = 240,
        OvertimeThresholdMinutes = 30, WeeklyOffDays = 1 | 64
    };

    private static Punch P(DateOnly d, int h, int m, string type) { var t = d.ToDateTime(new TimeOnly(h, m), DateTimeKind.Utc); return new Punch(t, t, type); }

    private static DayOutcome Day(params Punch[] punches) => AttendanceCalculator.Compute(new DayFacts(General(), Monday, false, false, null, punches, false));
    private static DayOutcome InOut(int inH, int inM, int outH, int outM) => Day(P(Monday, inH, inM, "IN"), P(Monday, outH, outM, "OUT"));

    [Fact]
    public void Shift_time_calculations()
    {
        var s = General();
        ShiftMath.SpanMinutes(s).Should().Be(540);
        ShiftMath.TotalMinutes(s).Should().Be(480);
        ShiftMath.IsOvernight(s).Should().BeFalse();

        var night = new Shift { StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0), BreakMinutes = 30 };
        ShiftMath.IsOvernight(night).Should().BeTrue();
        ShiftMath.SpanMinutes(night).Should().Be(480);
        ShiftMath.TotalMinutes(night).Should().Be(450);
        ShiftMath.EndOn(night, Monday).Should().Be(new DateTime(2026, 6, 2, 6, 0, 0));
    }

    [Fact]
    public void Weekly_off_flags_Sunday_is_1_and_Saturday_is_64()
    {
        var s = General();
        ShiftMath.IsWeeklyOff(s, new DateOnly(2026, 6, 6)).Should().BeTrue("a Saturday");
        ShiftMath.IsWeeklyOff(s, new DateOnly(2026, 6, 7)).Should().BeTrue("a Sunday");
        ShiftMath.IsWeeklyOff(s, Monday).Should().BeFalse();
    }

    [Fact]
    public void On_time_full_day_is_Present_with_nothing_to_report()
    {
        var d = InOut(9, 0, 18, 0);
        d.Status.Should().Be(AttendanceDayStatuses.Present);
        d.WorkedMinutes.Should().Be(480, "nine hours less the hour break");
        (d.LateMinutes, d.EarlyMinutes, d.OvertimeMinutes).Should().Be((0, 0, 0));
    }

    [Fact]
    public void Inside_the_grace_is_not_late_past_it_the_whole_delay_counts()
    {
        InOut(9, 15, 18, 0).LateMinutes.Should().Be(0);
        InOut(9, 15, 18, 0).Status.Should().Be(AttendanceDayStatuses.Present);
        var late = InOut(9, 16, 18, 0);
        late.LateMinutes.Should().Be(16);
        late.Status.Should().Be(AttendanceDayStatuses.Late);
    }

    [Fact]
    public void Leaving_before_the_end_is_early_minutes()
    {
        InOut(9, 0, 17, 30).EarlyMinutes.Should().Be(30);
        InOut(9, 0, 18, 0).EarlyMinutes.Should().Be(0);
    }

    [Fact]
    public void Overtime_needs_to_pass_the_threshold_then_all_of_it_counts()
    {
        InOut(9, 0, 18, 30).OvertimeMinutes.Should().Be(0, "exactly at the threshold of 30");
        InOut(9, 0, 18, 31).OvertimeMinutes.Should().Be(31);
        InOut(9, 0, 20, 0).OvertimeMinutes.Should().Be(120);
    }

    [Fact]
    public void Half_day_below_the_half_day_minutes()
    {
        InOut(9, 0, 13, 59).Status.Should().Be(AttendanceDayStatuses.HalfDay, "239 minutes after the break");
        InOut(9, 0, 14, 0).Status.Should().Be(AttendanceDayStatuses.Present, "240 minutes is not below");
    }

    [Fact]
    public void A_missing_clock_out_on_a_past_day_is_a_half_day_today_it_is_still_in_progress()
    {
        Day(P(Monday, 9, 0, "IN")).Status.Should().Be(AttendanceDayStatuses.HalfDay);
        var today = AttendanceCalculator.Compute(new DayFacts(General(), Monday, false, false, null, [P(Monday, 9, 30, "IN")], true));
        today.Status.Should().Be(AttendanceDayStatuses.Late);
        today.EarlyMinutes.Should().Be(0);
    }

    [Fact]
    public void No_punch_on_a_working_day_is_Absent_unless_the_day_is_not_over()
    {
        Day().Status.Should().Be(AttendanceDayStatuses.Absent);
        AttendanceCalculator.Compute(new DayFacts(General(), Monday, false, false, null, [], true)).Status.Should().BeNull();
    }

    [Fact]
    public void Holiday_and_weekly_off_are_skipped_time_worked_there_is_overtime()
    {
        AttendanceCalculator.Compute(new DayFacts(General(), Monday, true, false, null, [], false)).Status.Should().Be(AttendanceDayStatuses.Holiday);
        AttendanceCalculator.Compute(new DayFacts(General(), Monday, false, true, null, [], false)).Status.Should().Be(AttendanceDayStatuses.WeeklyOff);

        var worked = AttendanceCalculator.Compute(new DayFacts(General(), Monday, false, true, null, [P(Monday, 10, 0, "IN"), P(Monday, 14, 0, "OUT")], false));
        worked.Status.Should().Be(AttendanceDayStatuses.WeeklyOff);
        worked.OvertimeMinutes.Should().Be(180);
        worked.LateMinutes.Should().Be(0);
    }

    [Fact]
    public void Leave_days()
    {
        AttendanceCalculator.Compute(new DayFacts(General(), Monday, false, false, new DayLeave(true, false), [], false)).Status.Should().Be(AttendanceDayStatuses.OnLeave);
        var half = AttendanceCalculator.Compute(new DayFacts(General(), Monday, false, false, new DayLeave(true, true), [P(Monday, 13, 0, "IN"), P(Monday, 18, 0, "OUT")], false));
        half.Status.Should().Be(AttendanceDayStatuses.Present, "a half-day leave excuses the other half");
        half.LateMinutes.Should().Be(0);
    }

    [Fact]
    public void Unknown_punches_pair_up_as_first_and_last()
    {
        var d = Day(P(Monday, 18, 0, "UNKNOWN"), P(Monday, 9, 0, "UNKNOWN"), P(Monday, 13, 0, "UNKNOWN"));
        d.WorkedMinutes.Should().Be(480);
    }

    [Fact]
    public void A_night_shift_punch_in_the_early_hours_belongs_to_last_night()
    {
        var night = new Shift { StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0), HalfDayMinutes = 0 };
        Func<DateOnly, Shift?> shifts = _ => night;
        AttendanceCalculator.WorkDateOf(new DateTime(2026, 6, 2, 6, 10, 0), shifts).Should().Be(Monday);
        AttendanceCalculator.WorkDateOf(new DateTime(2026, 6, 2, 21, 55, 0), shifts).Should().Be(new DateOnly(2026, 6, 2));
        AttendanceCalculator.WorkDateOf(new DateTime(2026, 6, 2, 6, 10, 0), _ => General()).Should().Be(new DateOnly(2026, 6, 2));

        var d = AttendanceCalculator.Compute(new DayFacts(night, Monday, false, false, null,
            [P(Monday, 22, 0, "IN"), P(Monday.AddDays(1), 6, 0, "OUT")], false));
        d.WorkedMinutes.Should().Be(480);
        d.LateMinutes.Should().Be(0);
        d.EarlyMinutes.Should().Be(0);
    }

    [Theory]
    [InlineData(AttendanceDayStatuses.Absent, false, false, false, 1.0)]
    [InlineData(AttendanceDayStatuses.HalfDay, false, false, false, 0.5)]
    [InlineData(AttendanceDayStatuses.Present, false, false, false, 0.0)]
    [InlineData(AttendanceDayStatuses.OnLeave, true, true, false, 0.0)]
    [InlineData(AttendanceDayStatuses.OnLeave, true, false, false, 1.0)]
    [InlineData(AttendanceDayStatuses.OnLeave, true, true, true, 0.5)]
    [InlineData(AttendanceDayStatuses.OnLeave, true, false, true, 1.0)]
    [InlineData(AttendanceDayStatuses.Present, true, false, true, 0.5)]
    [InlineData(AttendanceDayStatuses.Present, true, true, true, 0.0)]
    public void Unpaid_days_of_a_day(string status, bool hasLeave, bool paid, bool half, double expected) =>
        DayPay.UnpaidDays(status, hasLeave ? new DayLeave(paid, half) : null).Should().Be((decimal)expected);

    [Fact]
    public void Entitlement_by_accrual_method_and_grade()
    {
        var general = new LeavePolicy { Entitlement = 12m, AccrualMethod = LeaveAccrualMethods.Annual, EffectiveFrom = new DateOnly(2026, 1, 1) };
        var senior = new LeavePolicy { Entitlement = 24m, GradeId = 7, AccrualMethod = LeaveAccrualMethods.Annual, EffectiveFrom = new DateOnly(2026, 1, 1) };
        var old = new LeavePolicy { Entitlement = 5m, AccrualMethod = LeaveAccrualMethods.Annual, EffectiveFrom = new DateOnly(2024, 1, 1), EffectiveTo = new DateOnly(2025, 12, 31) };
        LeavePolicy[] all = [general, senior, old];
        LeaveEntitlement.PolicyFor(all, 7, 2026).Should().BeSameAs(senior);
        LeaveEntitlement.PolicyFor(all, 3, 2026).Should().BeSameAs(general);
        LeaveEntitlement.PolicyFor(all, null, 2025).Should().BeSameAs(old);
        LeaveEntitlement.PolicyFor(all, null, 2023).Should().BeNull();

        var asOf = new DateOnly(2026, 6, 15);
        LeaveEntitlement.DueBy(general, 2026, asOf, null).Should().Be(12m);
        var monthly = new LeavePolicy { Entitlement = 12m, AccrualMethod = LeaveAccrualMethods.Monthly };
        LeaveEntitlement.DueBy(monthly, 2026, asOf, new DateOnly(2020, 1, 1)).Should().Be(6m);
        LeaveEntitlement.DueBy(monthly, 2026, asOf, new DateOnly(2026, 5, 20)).Should().Be(2m, "May and June");
        LeaveEntitlement.DueBy(monthly, 2025, asOf, null).Should().Be(12m);
        LeaveEntitlement.DueBy(monthly, 2027, asOf, null).Should().Be(0m);
        LeaveEntitlement.DueBy(new LeavePolicy { Entitlement = 12m, AccrualMethod = LeaveAccrualMethods.None }, 2026, asOf, null).Should().Be(0m);
    }

    [Fact]
    public void Carry_forward_is_capped_and_the_rest_lapses()
    {
        LeaveEntitlement.CarryForward(20m, 14m, 0m).Should().Be((14m, 6m));
        LeaveEntitlement.CarryForward(5m, 14m, 0m).Should().Be((5m, 0m));
        LeaveEntitlement.CarryForward(5m, null, 0m).Should().Be((0m, 5m), "no cap set means this type does not carry");
        LeaveEntitlement.CarryForward(5m, 14m, 12m).Should().Be((2m, 3m), "the cap covers what was carried before");
        LeaveEntitlement.CarryForward(-2m, 14m, 0m).Should().Be((0m, 0m));
    }
}
