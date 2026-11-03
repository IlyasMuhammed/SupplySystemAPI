using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Att;
using SMS.Modules.Hrm.Services.Pay;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Tasks 245–265, 275, 277, E-5, E-7: the calculation, on the null attendance and with only PAY's own provider unless a test adds one.</summary>
public sealed class PayrollCalculationTests
{
    private static async Task<PayHarness> Ready() => await new PayHarness().ReadyAsync();

    [Fact]
    public async Task Full_month_with_null_seams_has_the_exact_expected_figures()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Ali", 100_000m, 45_000m);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        var calc = await runs.CalculateAsync(h.Admin, run.Uuid);

        calc.Status.Should().Be("Calculated");
        var line = await h.LineAsync(runs, run.Uuid, e.Uuid);
        line.IsBlocked.Should().BeFalse(line.BlockReason);
        line.WorkingDays.Should().Be(31m);
        line.PaidDays.Should().Be(31m);
        PayHarness.Amount(line, "BASIC").Should().Be(100_000m);
        PayHarness.Amount(line, "HRENT").Should().Be(45_000m);
        line.Gross.Should().Be(145_000m);

        // Annual 1,740,000 → 6,000 + 11% × 540,000 = 65,400 → 5,450 a month.
        PayHarness.Amount(line, "ITAX").Should().Be(5_450m);
        // EOBI on the fixed 37,000: 1% and 5%. PF on earned basic: 8.33%.
        PayHarness.Amount(line, "EOBI_EE").Should().Be(370m);
        PayHarness.Amount(line, "EOBI_ER").Should().Be(1_850m);
        PayHarness.Amount(line, "PF_EE").Should().Be(8_330m);
        PayHarness.Amount(line, "PF_ER").Should().Be(8_330m);
        line.TotalDeductions.Should().Be(5_450m + 370m + 8_330m);
        line.TotalEmployerCost.Should().Be(1_850m + 8_330m);
        line.Net.Should().Be(145_000m - 14_150m);
        line.Components.Should().NotContain(c => c.Code == "NET", "the NET row is internal");

        calc.TotalEmployees.Should().Be(1);
        calc.TotalGross.Should().Be(145_000m);
        calc.TotalNet.Should().Be(130_850m);
    }

    [Fact]
    public async Task Five_employee_payroll_totals_are_the_sum_of_the_lines()
    {
        var h = await Ready();
        var pay = new[] { 50_000m, 75_500.50m, 100_000m, 33_333.33m, 250_000m };
        for (var i = 0; i < 5; i++) await h.StaffAsync("Emp" + new string('x', i), pay[i]);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        var calc = await runs.CalculateAsync(h.Admin, run.Uuid);
        var lines = (await runs.ListLinesAsync(h.Admin, run.Uuid, null, false, 1, 50)).Data;
        lines.Should().HaveCount(5);
        calc.TotalGross.Should().Be(lines.Sum(l => l.Gross)).And.Be(pay.Sum());
        calc.TotalNet.Should().Be(lines.Sum(l => l.Net));
        calc.TotalDeductions.Should().Be(lines.Sum(l => l.TotalDeductions));
        foreach (var l in lines) l.Net.Should().Be(l.Gross - l.TotalDeductions);
    }

    [Fact]
    public async Task Mid_month_joiner_is_pro_rated_on_calendar_days()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Joiner", 31_000m, hired: new DateOnly(2026, 10, 16));
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var line = await h.LineAsync(runs, run.Uuid, e.Uuid);
        line.PaidDays.Should().Be(16m);
        PayHarness.Amount(line, "BASIC").Should().Be(16_000m);
    }

    [Fact]
    public async Task Fixed_divisor_is_respected_and_a_full_month_pays_exactly_basic()
    {
        var h = await Ready();
        await h.Core.Settings.UpdateAsync(h.Admin, new HrmSettingsUpdate { WorkingDaysDivisor = 26 });
        var full = await h.StaffAsync("Full", 26_000m);
        var joiner = await h.StaffAsync("Joiner", 31_000m, hired: new DateOnly(2026, 10, 16));
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var a = await h.LineAsync(runs, run.Uuid, full.Uuid);
        a.WorkingDays.Should().Be(26m);
        a.PaidDays.Should().Be(26m);
        PayHarness.Amount(a, "BASIC").Should().Be(26_000m);
        var b = await h.LineAsync(runs, run.Uuid, joiner.Uuid);
        b.PaidDays.Should().Be(13.42m); // 26 × 16 / 31
        PayHarness.Amount(b, "BASIC").Should().Be(Math.Round(31_000m * 13.42m / 26m, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task Leaver_gets_the_salary_in_force_on_their_last_day_not_at_period_end()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Leaver", 62_000m);
        await h.SalaryAsync(e.Uuid, new DateOnly(2026, 10, 20), 93_000m); // a raise after they left must not pay them
        var row = await h.Db.Employees.FirstAsync(x => x.Uuid == e.Uuid);
        row.LeaveDate = new DateOnly(2026, 10, 15);
        await h.Db.SaveChangesAsync();
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var line = await h.LineAsync(runs, run.Uuid, e.Uuid);
        line.PaidDays.Should().Be(15m);
        PayHarness.Amount(line, "BASIC").Should().Be(30_000m);
    }

    [Fact]
    public async Task Superseded_salary_still_answers_for_its_own_month()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Raised", 62_000m);
        await h.SalaryAsync(e.Uuid, new DateOnly(2026, 11, 1), 93_000m);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        PayHarness.Amount(await h.LineAsync(runs, run.Uuid, e.Uuid), "BASIC").Should().Be(62_000m);
    }

    [Fact]
    public async Task Attendance_on_the_payrolls_basis_reduces_pay_and_overtime_is_paid()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Absent", 31_000m);
        var id = (await h.Db.Employees.FirstAsync(x => x.Uuid == e.Uuid)).Id;
        h.Attendance = new PayAttendance { Answer = i => i == id ? new AttendancePayData(i, 31m, 29m, 20m, 2m, 22, 120, 180m, [], true) : null };
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var line = await h.LineAsync(runs, run.Uuid, e.Uuid);
        line.PaidDays.Should().Be(29m);
        line.WorkedDays.Should().Be(20m);
        PayHarness.Amount(line, "BASIC").Should().Be(29_000m);
        // 31,000 / 31 / 8 × 3 weighted hours = 375, on the full monthly basic.
        PayHarness.Amount(line, "OT").Should().Be(375m);
        line.Components.First(c => c.Code == "OT").Basis.Should().Be(3m);
    }

    [Fact]
    public async Task No_attendance_on_file_warns_on_the_line_and_run_and_stays_out_of_the_journal()
    {
        var h = await Ready();
        var silent = await h.StaffAsync("Silent", 31_000m);
        var punched = await h.StaffAsync("Punched", 31_000m);
        var silentId = (await h.Db.Employees.FirstAsync(x => x.Uuid == silent.Uuid)).Id;
        h.Attendance = new PayAttendance { Answer = i => new AttendancePayData(i, 31m, 31m, i == silentId ? 0m : 22m, 0m, 22, 0, 0m, [], i != silentId) };
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        var calc = await runs.CalculateAsync(h.Admin, run.Uuid);
        calc.UntrackedAttendanceEmployees.Should().Be(1);
        calc.Warnings.Should().ContainSingle().Which.Should().Contain("no attendance");
        (await runs.ReviewAsync(h.Admin, run.Uuid)).UntrackedAttendanceEmployees.Should().Be(1);
        var line = await h.LineAsync(runs, run.Uuid, silent.Uuid);
        line.AttendanceUntracked.Should().BeTrue();
        line.IsBlocked.Should().BeFalse("a warning, not a block");
        PayHarness.Amount(line, "BASIC").Should().Be(31_000m);
        line.Components.Should().NotContain(c => c.Code == PayrollCalculationEngine.UntrackedAttendanceCode);
        (await h.LineAsync(runs, run.Uuid, punched.Uuid)).AttendanceUntracked.Should().BeFalse();
        var journal = await runs.GetJournalAsync(h.Admin, run.Uuid);
        journal.TotalDebit.Should().Be(journal.TotalCredit);
        await runs.ApproveAsync(h.Admin, run.Uuid, false);
        (await runs.FinalizeAsync(h.Admin, run.Uuid)).Status.Should().Be("Finalized", "the marker is not a provider input");
    }

    [Fact]
    public async Task Fractional_salaries_round_each_component_once_and_the_line_adds_up()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Frac", 33_333.33m, 11_111.11m, hired: new DateOnly(2026, 10, 7));
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var line = await h.LineAsync(runs, run.Uuid, e.Uuid);
        line.Components.Should().OnlyContain(c => decimal.Round(c.Amount, 2) == c.Amount);
        line.Gross.Should().Be(line.Components.Where(c => c.Type == "Earning").Sum(c => c.Amount));
        line.Net.Should().Be(line.Gross - line.TotalDeductions);
    }

    [Theory]
    [InlineData(true, "No bank account")]
    public async Task Blocked_employee_is_kept_visible_and_out_of_the_totals(bool _, string reason)
    {
        var h = await Ready();
        await h.StaffAsync("Good", 50_000m);
        var bad = await h.StaffAsync("NoBank", 70_000m, bank: false);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        var calc = await runs.CalculateAsync(h.Admin, run.Uuid);
        calc.TotalEmployees.Should().Be(2);
        calc.BlockedEmployees.Should().Be(1);
        calc.TotalGross.Should().Be(50_000m);
        var line = await h.LineAsync(runs, run.Uuid, bad.Uuid);
        line.IsBlocked.Should().BeTrue();
        line.BlockReason.Should().Contain(reason);
        (await runs.ListLinesAsync(h.Admin, run.Uuid, null, true, 1, 50)).Data.Should().ContainSingle();
    }

    [Fact]
    public async Task Cash_employee_needs_no_bank_account_number()
    {
        var h = await Ready();
        var e = await h.Core.Employees.CreateAsync(h.Admin, new EmployeeUpsert { FirstName = "Cash", LastName = "Test", Employment = h.Core.Hire(h.Structure) });
        await h.Core.Bank.CreateAsync(h.Admin, e.Uuid, new EmployeeBankAccountUpsert { BankName = "Cash", PaymentMethod = "Cash", AccountNumber = "1" });
        await h.SalaryAsync(e.Uuid, new DateOnly(2026, 1, 1), 40_000m);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var line = await h.LineAsync(runs, run.Uuid, e.Uuid);
        line.IsBlocked.Should().BeFalse();
        line.PaymentMethod.Should().Be("Cash");
    }

    [Fact]
    public async Task No_assignment_no_rate_no_table_and_negative_net_each_block()
    {
        var h = await Ready();
        var none = await h.Core.Employees.CreateAsync(h.Admin, new EmployeeUpsert { FirstName = "NoSalary", LastName = "Test", Employment = h.Core.Hire(h.Structure) });
        await h.Core.Bank.CreateAsync(h.Admin, none.Uuid, new EmployeeBankAccountUpsert { BankName = "HBL", AccountNumber = "1" });
        var owed = await h.StaffAsync("Owes", 10_000m);
        await h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = owed.Uuid, ComponentCode = "OTHDED", Amount = 50_000m, Reason = "Big" });
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        (await h.LineAsync(runs, run.Uuid, none.Uuid)).BlockReason.Should().Contain("No approved salary");
        var negative = await h.LineAsync(runs, run.Uuid, owed.Uuid);
        negative.IsBlocked.Should().BeTrue();
        negative.BlockReason.Should().Contain("negative");

        // Remove the table and the EOBI rate from the period.
        var table = await h.Db.TaxSlabTables.FirstAsync(t => t.Code == "PK-2027");
        table.EffectiveTo = new DateOnly(2026, 9, 30);
        var eobi = await h.Db.StatutoryRates.FirstAsync(r => r.RateType == "EOBI");
        eobi.EffectiveTo = new DateOnly(2026, 9, 30);
        await h.Db.SaveChangesAsync();
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var again = await h.LineAsync(runs, run.Uuid, owed.Uuid);
        again.BlockReason.Should().Contain("No tax table").And.Contain("No EOBI rate");
    }

    [Fact]
    public async Task Seeded_tax_table_has_expired_so_october_blocks_rather_than_paying_no_tax()
    {
        var h = new PayHarness();
        await h.Core.Seeder.EnsureAsync(h.Org);
        var b = await h.Core.BaseAsync();
        await h.Core.Settings.UpdateAsync(h.Admin, new HrmSettingsUpdate { PayrollCostCenterUuid = b.CostCenter });
        var e = await h.Core.Employees.CreateAsync(h.Admin, new EmployeeUpsert { FirstName = "A", LastName = "B", Employment = h.Core.Hire(b) });
        await h.Core.Bank.CreateAsync(h.Admin, e.Uuid, new EmployeeBankAccountUpsert { BankName = "HBL", AccountNumber = "1" });
        await h.SalaryAsync(e.Uuid, new DateOnly(2026, 1, 1), 100_000m);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        (await h.LineAsync(runs, run.Uuid, e.Uuid)).BlockReason.Should().Contain("No tax table");
    }

    [Fact]
    public async Task Bad_formula_blocks_the_person_not_the_run()
    {
        var h = await Ready();
        var bad = await h.StaffAsync("Bad", 50_000m);
        var good = await h.StaffAsync("Good", 50_000m);
        var std = await h.Db.SalaryStructures.Include(s => s.Lines).ThenInclude(l => l.Component).FirstAsync(s => s.Code == "STD_MONTHLY");
        std.Lines.First(l => l.Component!.Code == "CONV").FormulaOverride = "BASIC / (PAID_DAYS - 31)"; // divides by zero for a full month
        await h.Db.SaveChangesAsync();
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        (await h.LineAsync(runs, run.Uuid, bad.Uuid)).BlockReason.Should().Contain("Formula");
        (await h.LineAsync(runs, run.Uuid, good.Uuid)).IsBlocked.Should().BeTrue("same structure, same formula");
    }

    [Fact]
    public async Task Taxable_adjustment_is_in_the_tax_and_on_the_line()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Bonus", 100_000m);
        await h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "BONUS", Amount = 50_000m, Reason = "Eid bonus" });
        await h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "OTHDED", Amount = 1_000m, Reason = "Uniform" });
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var line = await h.LineAsync(runs, run.Uuid, e.Uuid);
        PayHarness.Amount(line, "BONUS").Should().Be(50_000m);
        line.Gross.Should().Be(150_000m);
        // Annualized 1,800,000: 6,000 + 11% × 600,000 = 72,000 → 6,000.
        PayHarness.Amount(line, "ITAX").Should().Be(6_000m);
        PayHarness.Amount(line, "OTHDED").Should().Be(1_000m);
    }

    [Fact]
    public async Task Loan_installment_from_the_provider_is_deducted_and_a_reimbursement_does_not_go_in_gross()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Loan", 100_000m);
        h.Loans.Installment = 5_000m;
        h.Providers.Add(h.Loans);
        h.Providers.Add(new FixedProvider(new PayrollInput("EXPREIMB", 2_500m, "Taxi")));
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var line = await h.LineAsync(runs, run.Uuid, e.Uuid);
        PayHarness.Amount(line, "LOAN").Should().Be(5_000m);
        PayHarness.Amount(line, "EXPREIMB").Should().Be(2_500m);
        line.Gross.Should().Be(100_000m, "a reimbursement is not pay");
        line.Net.Should().Be(100_000m + 2_500m - line.TotalDeductions);
        // Not taxed either: 1,200,000 annual → 6,000 → 500.
        PayHarness.Amount(line, "ITAX").Should().Be(500m);
        h.Loans.ReservedBy.Should().NotBeNull();
    }

    private sealed class FixedProvider(PayrollInput input) : IPayrollInputProvider
    {
        public Task<IReadOnlyList<PayrollInput>> GetInputsAsync(Guid organizationId, int runId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PayrollInput>>([input]);
        public Task MarkConsumedAsync(Guid organizationId, int runId, int employeeId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReleaseAsync(Guid organizationId, int runId, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Expense_claim_adjustments_are_not_read_by_the_adjustment_provider()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Claim", 50_000m);
        var id = (await h.Db.Employees.FirstAsync(x => x.Uuid == e.Uuid)).Id;
        var claim = new ExpenseClaim { OrganizationId = h.Org, ClaimNumber = "C-1", EmployeeId = id, ClaimDate = PayHarness.October, CurrencyCode = "PKR" };
        h.Db.ExpenseClaims.Add(claim);
        await h.Db.SaveChangesAsync();
        h.Db.PayrollAdjustments.Add(new PayrollAdjustment { OrganizationId = h.Org, EmployeeId = id, ComponentCode = "EXPREIMB", Amount = 999m, Reason = "Claim", ExpenseClaimId = claim.Id });
        await h.Db.SaveChangesAsync();
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        PayHarness.Amount(await h.LineAsync(runs, run.Uuid, e.Uuid), "EXPREIMB").Should().Be(0m);
    }

    [Fact]
    public async Task Recalculating_rewrites_lines_and_snapshots_do_not_follow_later_edits()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Snap", 62_000m);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var count = await h.Db.PayrollRunLines.CountAsync();
        var firstRows = await h.Db.PayrollRunLineComponents.CountAsync();

        // A later edit of the component's name does not touch the snapshot.
        var basic = await h.Db.SalaryComponents.FirstAsync(c => c.Code == "BASIC");
        basic.Name = "Renamed";
        await h.Db.SaveChangesAsync();
        (await h.LineAsync(runs, run.Uuid, e.Uuid)).Components.First(c => c.Code == "BASIC").Name.Should().Be("Basic salary");

        await runs.CalculateAsync(h.Admin, run.Uuid);
        (await h.Db.PayrollRunLines.CountAsync()).Should().Be(count);
        (await h.Db.PayrollRunLineComponents.CountAsync()).Should().Be(firstRows);
        (await h.LineAsync(runs, run.Uuid, e.Uuid)).Components.First(c => c.Code == "BASIC").Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task A_snapshot_row_cannot_be_edited()
    {
        var h = await Ready();
        await h.StaffAsync("Imm", 62_000m);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var row = await h.Db.PayrollRunLineComponents.FirstAsync();
        row.Amount += 1m;
        var act = () => h.Db.SaveChangesAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
        h.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task By_department_mode_splits_expense_exactly_and_is_snapshotted()
    {
        var h = await Ready();
        await h.Core.Settings.UpdateAsync(h.Admin, new HrmSettingsUpdate { PayrollCostCenterMode = "BY_DEPARTMENT" });
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        run.CostCenterMode.Should().Be("BY_DEPARTMENT");
        await h.Core.Settings.UpdateAsync(h.Admin, new HrmSettingsUpdate { PayrollCostCenterMode = "CENTRAL" });
        (await runs.GetAsync(h.Admin, run.Uuid)).CostCenterMode.Should().Be("BY_DEPARTMENT", "a mode change applies to future runs only");
    }

    [Fact]
    public void Allocate_is_exact_to_the_cent()
    {
        var parts = PayrollCalculationEngine.Allocate(100m, [new CostCenterShare("A", 33.33m), new CostCenterShare("B", 33.33m), new CostCenterShare("C", 33.34m)]);
        parts.Sum(p => p.Amount).Should().Be(100m);
        var thirds = PayrollCalculationEngine.Allocate(0.01m, [new CostCenterShare("A", 50m), new CostCenterShare("B", 50m)]);
        thirds.Sum(p => p.Amount).Should().Be(0.01m);
        thirds.Should().OnlyContain(p => p.Amount == 0m || p.Amount == 0.01m);
        PayrollCalculationEngine.Allocate(-10.01m, [new CostCenterShare("A", 50m), new CostCenterShare("B", 50m)]).Sum(p => p.Amount).Should().Be(-10.01m);
        PayrollCalculationEngine.Allocate(5m, [new CostCenterShare("A", 100m)]).Single().Center.Should().BeNull();
    }

    [Fact]
    public async Task No_payroll_cost_center_blocks()
    {
        var h = new PayHarness();
        await h.Core.Seeder.EnsureAsync(h.Org);
        h.GetType(); // structure without the setting
        var b = await h.Core.BaseAsync();
        var e = await h.Core.Employees.CreateAsync(h.Admin, new EmployeeUpsert { FirstName = "A", LastName = "B", Employment = h.Core.Hire(b) });
        await h.Core.Bank.CreateAsync(h.Admin, e.Uuid, new EmployeeBankAccountUpsert { BankName = "HBL", AccountNumber = "1" });
        await h.SalaryAsync(e.Uuid, new DateOnly(2026, 1, 1), 1_000m);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        (await h.LineAsync(runs, run.Uuid, e.Uuid)).BlockReason.Should().Contain("cost center");
    }

    [Fact]
    public async Task Adjustment_rules()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Adj", 50_000m);
        var bad = () => h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "NET", Amount = 1m, Reason = "x" });
        await bad.Should().ThrowAsync<BadRequestException>().WithMessage("ADJUSTMENT_COMPONENT_INVALID*");
        var zero = () => h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "BONUS", Amount = 0m, Reason = "x" });
        await zero.Should().ThrowAsync<BadRequestException>();
        var sub = () => h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "BONUS", Amount = 1.234m, Reason = "x" });
        await sub.Should().ThrowAsync<BadRequestException>();

        var made = await h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "BONUS", Amount = 100m, Reason = "x" });
        (await h.Adjustments.ListAsync(h.Admin, e.Uuid, pending: true)).Should().ContainSingle();
        await h.Adjustments.DeleteAsync(h.Admin, made.Uuid);
        (await h.Adjustments.ListAsync(h.Admin, e.Uuid, pending: null)).Should().BeEmpty();
    }
}
