using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services.Life;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Tasks 349–353, 355–357.</summary>
public sealed class LifeLoanTests
{
    private static readonly DateOnly MarStart = new(2026, 3, 1), MarEnd = new(2026, 3, 31);

    [Fact]
    public async Task Schedule_sums_to_the_principal_exactly_and_the_last_absorbs_rounding()
    {
        var l = await new LifeHarness().InitAsync();
        var e = await l.H.EmployeeAsync(l.B);
        var loan = await l.Loans.CreateAsync(l.H.Admin(), new LoanUpsert { EmployeeUuid = e.Uuid, Type = "Personal", Principal = 1000m, InstallmentCount = 3, StartMonth = new DateOnly(2026, 3, 15) });
        loan.Installments.Select(i => i.Amount).Should().Equal(333.33m, 333.33m, 333.34m);
        loan.Installments.Sum(i => i.Amount).Should().Be(1000m);
        loan.Installments.Select(i => i.DueMonth).Should().Equal(new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1), new DateOnly(2026, 5, 1));
        loan.MonthlyAmount.Should().Be(333.33m);
        loan.Status.Should().Be("Draft");
        loan.LoanNumber.Should().StartWith("LN-");
    }

    [Fact]
    public async Task A_second_open_loan_of_the_same_type_is_refused_another_type_is_not()
    {
        var l = await new LifeHarness().InitAsync();
        var e = await l.H.EmployeeAsync(l.B);
        var req = new LoanUpsert { EmployeeUuid = e.Uuid, Type = "Personal", Principal = 100m, InstallmentCount = 1, StartMonth = MarStart };
        await l.Loans.CreateAsync(l.H.Admin(), req);
        (await FluentActions.Awaiting(() => l.Loans.CreateAsync(l.H.Admin(), req)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("LOAN_ACTIVE_EXISTS");
        req.Type = "Advance";
        await l.Loans.CreateAsync(l.H.Admin(), req);
    }

    [Fact]
    public async Task Disbursement_needs_approval_posts_balanced_and_carries_no_employee_data()
    {
        var l = await new LifeHarness().InitAsync();
        var e = await l.H.EmployeeAsync(l.B, "Zainab", "Malik");
        var admin = l.H.Admin();
        var loan = await l.Loans.CreateAsync(admin, new LoanUpsert { EmployeeUuid = e.Uuid, Type = "Personal", Principal = 5000m, InstallmentCount = 2, StartMonth = MarStart });
        var req = new LoanDisburseRequest { DisbursedOn = MarStart, PaidFromAccount = "Bank", Reference = "Zainab Malik cheque" };
        (await FluentActions.Awaiting(() => l.Loans.DisburseAsync(admin, loan.Uuid, req)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("LOAN_NOT_APPROVED");
        l.Gateway.Posted.Should().BeEmpty();

        await l.Loans.ApproveAsync(admin, loan.Uuid);
        var done = await l.Loans.DisburseAsync(admin, loan.Uuid, req);

        done.Status.Should().Be("Disbursed");
        done.PostingState.Should().Be("Posted");
        done.Outstanding.Should().Be(5000m);
        var tx = l.Gateway.Posted.Single();
        tx.DocumentType.Should().Be(FinDocType.LoanDisbursement);
        tx.SourceModule.Should().Be("hrm");
        tx.Lines.Sum(x => x.Debit).Should().Be(tx.Lines.Sum(x => x.Credit)).And.Be(5000m);
        tx.Lines.Single(x => x.Debit > 0).AccountRole.Should().Be("DEFAULT_EMPLOYEE_LOAN_RECEIVABLE");
        tx.Lines.Single(x => x.Credit > 0).AccountRole.Should().Be("DEFAULT_BANK");
        tx.PartnerId.Should().BeNull();
        var json = LifeHarness.Json(tx);
        json.Should().NotContain("Zainab").And.NotContain("Malik").And.NotContain(e.EmployeeNumber);
    }

    [Fact]
    public async Task An_advance_posts_to_the_advance_role_and_cash_when_paid_in_cash()
    {
        var l = await new LifeHarness().InitAsync();
        var e = await l.H.EmployeeAsync(l.B);
        var admin = l.H.Admin();
        var loan = await l.Loans.CreateAsync(admin, new LoanUpsert { EmployeeUuid = e.Uuid, Type = "Advance", Principal = 100m, InstallmentCount = 1, StartMonth = MarStart });
        await l.Loans.ApproveAsync(admin, loan.Uuid);
        await l.Loans.DisburseAsync(admin, loan.Uuid, new LoanDisburseRequest { DisbursedOn = MarStart, PaidFromAccount = "Cash" });
        var tx = l.Gateway.Posted.Single();
        tx.Lines.Single(x => x.Debit > 0).AccountRole.Should().Be("DEFAULT_EMPLOYEE_ADVANCE");
        tx.Lines.Single(x => x.Credit > 0).AccountRole.Should().Be("DEFAULT_CASH");
    }

    [Theory]
    [InlineData(PostingStatus.NoTargetConfigured, "NoTarget")]
    [InlineData(PostingStatus.ModuleNotLicensed, "NoTarget")]
    [InlineData(PostingStatus.DuplicateSkipped, "Posted")]
    [InlineData(PostingStatus.PartialFailure, "Failed")]
    [InlineData(PostingStatus.ValidationFailed, "Failed")]
    public void D11_posting_results_map_to_states(PostingStatus status, string state) =>
        HrmPostingHelper.Map(new PostingResult { Status = status }).State.Should().Be(state);

    [Fact]
    public async Task A_failed_or_throwing_gateway_leaves_the_loan_disbursed_and_retry_posts_it()
    {
        var l = await new LifeHarness().InitAsync();
        var e = await l.H.EmployeeAsync(l.B);
        var admin = l.H.Admin();
        var loan = await l.Loans.CreateAsync(admin, new LoanUpsert { EmployeeUuid = e.Uuid, Type = "Personal", Principal = 100m, InstallmentCount = 1, StartMonth = MarStart });
        await l.Loans.ApproveAsync(admin, loan.Uuid);
        l.Gateway.Throw = true;
        var failed = await l.Loans.DisburseAsync(admin, loan.Uuid, new LoanDisburseRequest { DisbursedOn = MarStart });
        failed.Status.Should().Be("Disbursed");
        failed.PostingState.Should().Be("Failed");
        l.Gateway.Throw = false;
        (await l.Loans.RetryPostingAsync(admin, loan.Uuid)).PostingState.Should().Be("Posted");
    }

    [Fact]
    public async Task Paying_out_after_the_start_month_moves_the_schedule()
    {
        var l = await new LifeHarness().InitAsync();
        var e = await l.H.EmployeeAsync(l.B);
        var admin = l.H.Admin();
        var loan = await l.Loans.CreateAsync(admin, new LoanUpsert { EmployeeUuid = e.Uuid, Type = "Personal", Principal = 200m, InstallmentCount = 2, StartMonth = MarStart });
        await l.Loans.ApproveAsync(admin, loan.Uuid);
        var done = await l.Loans.DisburseAsync(admin, loan.Uuid, new LoanDisburseRequest { DisbursedOn = new DateOnly(2026, 5, 20) });
        done.Installments.Select(i => i.DueMonth).Should().Equal(new DateOnly(2026, 5, 1), new DateOnly(2026, 6, 1));
    }

    [Fact]
    public async Task A_run_deduction_reduces_the_balance_and_the_last_closes_the_loan()
    {
        var l = await new LifeHarness().InitAsync();
        var emp = await l.H.EmployeeAsync(l.B);
        var id = await l.IdOf(emp.Uuid);
        var loan = await l.DisbursedLoanAsync(emp.Uuid, 200m, 2);
        var march = await l.RunAsync("PAY-03");

        var inputs = await l.LoanProvider.GetInputsAsync(l.H.Org, march.Id, id, MarStart, MarEnd);
        inputs.Should().ContainSingle();
        inputs[0].ComponentCode.Should().Be("LOAN");
        inputs[0].Amount.Should().Be(100m);
        inputs[0].Reason.Should().NotContain(emp.FullName);

        (await l.Loans.GetAsync(l.H.Admin(), loan.Uuid)).Outstanding.Should().Be(200m, "reserved is not deducted");
        await l.LoanProvider.MarkConsumedAsync(l.H.Org, march.Id, id);
        var after = await l.Loans.GetAsync(l.H.Admin(), loan.Uuid);
        after.Outstanding.Should().Be(100m);
        after.Installments[0].Status.Should().Be("Deducted");
        after.Installments[0].RunNumber.Should().Be("PAY-03");
        after.Status.Should().Be("Disbursed");

        var april = await l.RunAsync("PAY-04", month: 4);
        var next = await l.LoanProvider.GetInputsAsync(l.H.Org, april.Id, id, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));
        next.Single().Amount.Should().Be(100m, "the consumed installment never comes back");
        await l.LoanProvider.MarkConsumedAsync(l.H.Org, april.Id, id);
        var closed = await l.Loans.GetAsync(l.H.Admin(), loan.Uuid);
        closed.Status.Should().Be("Closed");
        closed.Outstanding.Should().Be(0m);
    }

    [Fact]
    public async Task The_seeded_LOAN_and_ADV_components_credit_the_receivable_so_a_run_posts_the_recovery()
    {
        var l = await new LifeHarness().InitAsync();
        var c = await l.H.Db.SalaryComponents.IgnoreQueryFilters().Where(x => x.OrganizationId == l.H.Org && (x.Code == "LOAN" || x.Code == "ADV")).ToDictionaryAsync(x => x.Code);
        c["LOAN"].PostingRole.Should().Be("DEFAULT_EMPLOYEE_LOAN_RECEIVABLE");
        c["ADV"].PostingRole.Should().Be("DEFAULT_EMPLOYEE_ADVANCE");
        c["LOAN"].Type.Should().Be(SalaryComponentTypes.Deduction);
    }

    [Fact]
    public async Task Another_live_run_cannot_take_a_reserved_installment_until_it_is_released()
    {
        var l = await new LifeHarness().InitAsync();
        var emp = await l.H.EmployeeAsync(l.B);
        var id = await l.IdOf(emp.Uuid);
        await l.DisbursedLoanAsync(emp.Uuid, 300m, 3);
        var a = await l.RunAsync("A");
        var b = await l.RunAsync("B");

        (await l.LoanProvider.GetInputsAsync(l.H.Org, a.Id, id, MarStart, MarEnd)).Single().Amount.Should().Be(100m);
        (await l.LoanProvider.GetInputsAsync(l.H.Org, a.Id, id, MarStart, MarEnd)).Single().Amount.Should().Be(100m, "a recalculation sees its own reservation, once");
        (await l.LoanProvider.GetInputsAsync(l.H.Org, b.Id, id, MarStart, MarEnd)).Should().BeEmpty("no double deduction");
        await l.LoanProvider.MarkConsumedAsync(l.H.Org, b.Id, id);
        (await l.H.Db.LoanInstallments.IgnoreQueryFilters().CountAsync(i => i.Status == "Deducted")).Should().Be(0, "b holds nothing to consume");

        await l.LoanProvider.ReleaseAsync(l.H.Org, a.Id);
        (await l.LoanProvider.GetInputsAsync(l.H.Org, b.Id, id, MarStart, MarEnd)).Single().Amount.Should().Be(100m);
    }

    [Fact]
    public async Task A_line_left_out_of_a_finalized_run_is_collected_by_the_next_with_arrears()
    {
        var l = await new LifeHarness().InitAsync();
        var emp = await l.H.EmployeeAsync(l.B);
        var id = await l.IdOf(emp.Uuid);
        await l.DisbursedLoanAsync(emp.Uuid, 300m, 3);
        var march = await l.RunAsync("M");
        await l.LoanProvider.GetInputsAsync(l.H.Org, march.Id, id, MarStart, MarEnd);
        await l.SetRunStatusAsync(march, PayrollRunStatuses.Finalized); // Blocked line: never marked consumed.

        var april = await l.RunAsync("A", month: 4);
        var inputs = await l.LoanProvider.GetInputsAsync(l.H.Org, april.Id, id, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30));
        inputs.Single().Amount.Should().Be(200m, "March's installment plus April's");
    }

    [Fact]
    public async Task Reversing_a_run_releases_consumed_installments_and_reopens_the_loan()
    {
        var l = await new LifeHarness().InitAsync();
        var emp = await l.H.EmployeeAsync(l.B);
        var id = await l.IdOf(emp.Uuid);
        var loan = await l.DisbursedLoanAsync(emp.Uuid, 100m, 1);
        var run = await l.RunAsync("R");
        await l.LoanProvider.GetInputsAsync(l.H.Org, run.Id, id, MarStart, MarEnd);
        await l.LoanProvider.MarkConsumedAsync(l.H.Org, run.Id, id);
        (await l.Loans.GetAsync(l.H.Admin(), loan.Uuid)).Status.Should().Be("Closed");

        await l.LoanProvider.ReleaseAsync(l.H.Org, run.Id);
        await l.LoanProvider.ReleaseAsync(l.H.Org, run.Id); // repeat is harmless
        var back = await l.Loans.GetAsync(l.H.Admin(), loan.Uuid);
        back.Status.Should().Be("Disbursed");
        back.Outstanding.Should().Be(100m);
        back.Installments[0].Status.Should().Be("Pending");
    }

    [Fact]
    public async Task Waiving_is_refused_while_a_live_run_holds_the_installment_and_reduces_the_balance_otherwise()
    {
        var l = await new LifeHarness().InitAsync();
        var emp = await l.H.EmployeeAsync(l.B);
        var id = await l.IdOf(emp.Uuid);
        var loan = await l.DisbursedLoanAsync(emp.Uuid, 200m, 2);
        var run = await l.RunAsync("R");
        await l.LoanProvider.GetInputsAsync(l.H.Org, run.Id, id, MarStart, MarEnd);
        var note = "goodwill";
        (await FluentActions.Awaiting(() => l.Loans.WaiveInstallmentAsync(l.H.Admin(), loan.Installments[0].Uuid, note)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("LOAN_INSTALLMENT_NOT_PENDING");
        var waived = await l.Loans.WaiveInstallmentAsync(l.H.Admin(), loan.Installments[1].Uuid, note);
        waived.Outstanding.Should().Be(100m);
    }

    [Fact]
    public async Task Scope_tenant_and_own_approval_are_enforced()
    {
        var l = await new LifeHarness().InitAsync();
        var mine = await l.H.EmployeeAsync(l.B, "Mine", userId: 5);
        var other = await l.H.EmployeeAsync(l.B, "Other");
        var loan = await l.DisbursedLoanAsync(other.Uuid);

        await FluentActions.Awaiting(() => l.Loans.GetAsync(l.H.As(5, P.HRM_LOAN_VIEW), loan.Uuid)).Should().ThrowAsync<NotFoundException>("outside the HR scope");
        (await l.Loans.ListAsync(l.H.As(5, P.HRM_LOAN_VIEW), null, null, 1, 25)).TotalRecords.Should().Be(0);
        (await l.Loans.ListAsync(l.H.Admin(), null, null, 1, 25)).TotalRecords.Should().Be(1);

        var foreign = new LifeHarness(dbName: l.H.DbName);
        await FluentActions.Awaiting(() => foreign.Loans.GetAsync(foreign.H.Admin(), loan.Uuid)).Should().ThrowAsync<NotFoundException>("another organization");

        var own = await l.Loans.CreateAsync(l.H.Admin(), new LoanUpsert { EmployeeUuid = mine.Uuid, Type = "Personal", Principal = 100m, InstallmentCount = 1, StartMonth = MarStart });
        await FluentActions.Awaiting(() => l.Loans.ApproveAsync(l.H.Admin(5), own.Uuid)).Should().ThrowAsync<ForbiddenException>();
    }
}

/// <summary>Tasks 360–363, 365 and the increment 368/370.</summary>
public sealed class LifeSettlementTests
{
    private static readonly DateOnly Last = new(2026, 3, 15);

    private static async Task<(LifeHarness l, EmployeeDetail e, int id)> WorldAsync(bool verify = true)
    {
        var l = await new LifeHarness().InitAsync();
        var e = await l.H.EmployeeAsync(l.B, "Zainab", "Malik");
        var id = await l.IdOf(e.Uuid);
        await l.SalaryAsync(id);
        var row = await l.H.Db.Employees.IgnoreQueryFilters().FirstAsync(x => x.Id == id);
        row.JoinDate = new DateOnly(2024, 3, 16);
        var rate = await l.H.Db.StatutoryRates.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.OrganizationId == l.H.Org && r.RateType == StatutoryRateTypes.Gratuity);
        if (rate is null)
        {
            rate = new StatutoryRate { OrganizationId = l.H.Org, RateType = StatutoryRateTypes.Gratuity, Name = "Gratuity", EmployerPercent = 100m, EffectiveFrom = new DateOnly(2020, 1, 1) };
            l.H.Db.StatutoryRates.Add(rate);
        }
        rate.IsVerified = verify;
        await l.H.Db.SaveChangesAsync();
        return (l, e, id);
    }

    private static async Task AllocateLeaveAsync(LifeHarness l, int id, decimal days)
    {
        var type = await l.H.Db.LeaveTypes.IgnoreQueryFilters().FirstAsync(t => t.OrganizationId == l.H.Org && t.IsPaid);
        l.H.Db.LeaveLedgerEntries.Add(new LeaveLedgerEntry { OrganizationId = l.H.Org, EmployeeId = id, LeaveTypeId = type.Id, Delta = days, Reason = LeaveLedgerReasons.Allocation, Date = new DateOnly(2026, 1, 1), Year = 2026 });
        await l.H.Db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("2020-01-01", "2024-12-31", 5.0)]
    [InlineData("2024-03-16", "2026-03-15", 2.0)]
    [InlineData("2026-01-01", "2026-07-01", 0.4986)] // 182 / 365
    public void Service_years_count_the_last_day(string from, string to, double expected) =>
        FinalSettlementService.ServiceYears(DateOnly.Parse(from), DateOnly.Parse(to)).Should().Be((decimal)expected);

    [Fact]
    public void Gratuity_applies_the_configured_rate_ceiling_and_basis_only()
    {
        var rate = new StatutoryRate { EmployerPercent = 50m };
        FinalSettlementService.Gratuity(100000m, 2m, rate).Should().Be(100000m);
        rate.WageCeiling = 80000m;
        FinalSettlementService.Gratuity(100000m, 2m, rate).Should().Be(80000m);
        rate.FixedBasis = 10000m;
        FinalSettlementService.Gratuity(100000m, 2m, rate).Should().Be(10000m);
    }

    [Fact]
    public async Task Compute_adds_salary_encashment_gratuity_less_loans_and_advances_to_the_cent()
    {
        var (l, e, id) = await WorldAsync();
        await AllocateLeaveAsync(l, id, 6m);
        await l.DisbursedLoanAsync(e.Uuid, 3000m, 3);
        await l.DisbursedLoanAsync(e.Uuid, 500m, 1, LoanTypes.Advance);

        var s = await l.Settlements.ComputeAsync(l.H.Admin(), new SettlementComputeRequest { EmployeeUuid = e.Uuid, LastWorkingDay = Last });

        s.Status.Should().Be("Draft");
        s.ServiceYears.Should().Be(2m);
        s.LastDrawnSalary.Should().Be(100000m);
        decimal Amount(string code) => s.Lines.Single(x => x.Code == code).Amount;
        // Unpaid from the join date: every month from 2024-03-16, then March 1-15 at calendar days. 23.5 months is the full run of months
        // 2024-04..2026-02 plus the two part-months (16/31 and 15/31).
        Amount("REMSAL").Should().Be(Math.Round(100000m * (23m + 16m / 31m + 15m / 31m), 2, MidpointRounding.AwayFromZero));
        Amount("ENCASH").Should().Be(Math.Round(6m * 100000m / 31m, 2, MidpointRounding.AwayFromZero));
        Amount("GRATUITY").Should().Be(200000m);
        Amount("LOAN").Should().Be(3000m);
        Amount("ADV").Should().Be(500m);
        s.TotalEarnings.Should().Be(Amount("REMSAL") + Amount("ENCASH") + Amount("GRATUITY"));
        s.TotalDeductions.Should().Be(3500m);
        s.Net.Should().Be(s.TotalEarnings - 3500m);
    }

    [Fact]
    public async Task Remaining_salary_starts_after_the_last_finalized_run()
    {
        var (l, e, id) = await WorldAsync();
        var feb = await l.RunAsync("FEB", PayrollRunStatuses.Paid, month: 2);
        l.H.Db.PayrollRunLines.Add(new PayrollRunLine { OrganizationId = l.H.Org, RunId = feb.Id, EmployeeId = id });
        await l.H.Db.SaveChangesAsync();
        var s = await l.Settlements.ComputeAsync(l.H.Admin(), new SettlementComputeRequest { EmployeeUuid = e.Uuid, LastWorkingDay = Last });
        s.Lines.Single(x => x.Code == "REMSAL").Amount.Should().Be(Math.Round(100000m * 15m / 31m, 2, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task Only_one_settlement_per_employee_until_it_is_cancelled()
    {
        var (l, e, _) = await WorldAsync();
        var req = new SettlementComputeRequest { EmployeeUuid = e.Uuid, LastWorkingDay = Last };
        var first = await l.Settlements.ComputeAsync(l.H.Admin(), req);
        (await FluentActions.Awaiting(() => l.Settlements.ComputeAsync(l.H.Admin(), req)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("SETTLEMENT_EXISTS");
        await l.Settlements.CancelAsync(l.H.Admin(), first.Uuid);
        await l.Settlements.ComputeAsync(l.H.Admin(), req);
    }

    [Fact]
    public async Task G21_an_unverified_gratuity_rate_blocks_post_and_posts_nothing()
    {
        var (l, e, _) = await WorldAsync(verify: false);
        var s = await l.Settlements.ComputeAsync(l.H.Admin(), new SettlementComputeRequest { EmployeeUuid = e.Uuid, LastWorkingDay = Last });
        s.Notes.Should().Contain("not been reviewed");
        await l.Settlements.ApproveAsync(l.H.Admin(), s.Uuid);
        (await FluentActions.Awaiting(() => l.Settlements.PostAsync(l.H.Admin(), s.Uuid)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("PAYROLL_STATUTORY_UNVERIFIED");
        l.Gateway.Posted.Where(t => t.DocumentType == FinDocType.FinalSettlement).Should().BeEmpty();
    }

    [Fact]
    public async Task Post_is_balanced_by_role_private_clears_the_loans_and_encashes_the_leave()
    {
        var (l, e, id) = await WorldAsync();
        await AllocateLeaveAsync(l, id, 6m);
        var loan = await l.DisbursedLoanAsync(e.Uuid, 3000m, 3);
        l.Gateway.Posted.Clear();
        var s = await l.Settlements.ComputeAsync(l.H.Admin(), new SettlementComputeRequest { EmployeeUuid = e.Uuid, LastWorkingDay = Last });
        await l.Settlements.ApproveAsync(l.H.Admin(), s.Uuid);
        var posted = await l.Settlements.PostAsync(l.H.Admin(), s.Uuid);

        posted.Status.Should().Be("Posted");
        posted.PostingState.Should().Be("Posted");
        var tx = l.Gateway.Posted.Single();
        tx.DocumentType.Should().Be(FinDocType.FinalSettlement);
        tx.Lines.Sum(x => x.Debit).Should().Be(tx.Lines.Sum(x => x.Credit));
        tx.Lines.Where(x => x.Credit > 0).Select(x => x.AccountRole).Should().Contain(["DEFAULT_EMPLOYEE_LOAN_RECEIVABLE", "DEFAULT_SALARY_PAYABLE"]);
        tx.Lines.Where(x => x.Debit > 0).Select(x => x.AccountRole).Should().Contain(["DEFAULT_SALARY_EXPENSE", "DEFAULT_LEAVE_ENCASHMENT_EXPENSE", "DEFAULT_GRATUITY_EXPENSE"]);
        tx.Lines.Single(x => x.AccountRole == "DEFAULT_SALARY_PAYABLE").Credit.Should().Be(posted.Net);
        LifeHarness.Json(tx).Should().NotContain("Zainab").And.NotContain("Malik").And.NotContain(e.EmployeeNumber);

        var after = await l.Loans.GetAsync(l.H.Admin(), loan.Uuid);
        after.Status.Should().Be("Closed");
        after.Outstanding.Should().Be(0m);
        (await l.H.Db.LeaveLedgerEntries.IgnoreQueryFilters().Where(x => x.EmployeeId == id).SumAsync(x => x.Delta)).Should().Be(0m);

        await FluentActions.Awaiting(() => l.Settlements.PostAsync(l.H.Admin(), s.Uuid)).Should().ThrowAsync<ConflictException>("a second post recovers nothing twice");
    }

    [Fact]
    public async Task A_run_deduction_after_compute_makes_the_settlement_stale_and_a_live_run_blocks_it()
    {
        var (l, e, id) = await WorldAsync();
        await l.DisbursedLoanAsync(e.Uuid, 300m, 3);
        var s = await l.Settlements.ComputeAsync(l.H.Admin(), new SettlementComputeRequest { EmployeeUuid = e.Uuid, LastWorkingDay = Last });
        await l.Settlements.ApproveAsync(l.H.Admin(), s.Uuid);

        var run = await l.RunAsync("R");
        await l.LoanProvider.GetInputsAsync(l.H.Org, run.Id, id, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        (await FluentActions.Awaiting(() => l.Settlements.PostAsync(l.H.Admin(), s.Uuid)).Should().ThrowAsync<ConflictException>("the run is still live")).Which.Message.Should().StartWith("SETTLEMENT_LOAN_IN_RUN");

        await l.LoanProvider.MarkConsumedAsync(l.H.Org, run.Id, id);
        (await FluentActions.Awaiting(() => l.Settlements.PostAsync(l.H.Admin(), s.Uuid)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("SETTLEMENT_STALE");
    }

    [Fact]
    public async Task Loan_lines_cannot_be_edited_but_others_can_and_totals_follow()
    {
        var (l, e, _) = await WorldAsync();
        await l.DisbursedLoanAsync(e.Uuid, 300m, 3);
        var s = await l.Settlements.ComputeAsync(l.H.Admin(), new SettlementComputeRequest { EmployeeUuid = e.Uuid, LastWorkingDay = Last });

        var tampered = s.Lines.Select(x => new SettlementLineModel { Code = x.Code, Description = x.Description, Type = x.Type, Amount = x.Code == "LOAN" ? 1m : x.Amount, PostingRole = x.PostingRole, Basis = x.Basis }).ToList();
        (await FluentActions.Awaiting(() => l.Settlements.UpdateAsync(l.H.Admin(), s.Uuid, new SettlementUpdate { Lines = tampered })).Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("SETTLEMENT_LINE_LOCKED");

        var edited = s.Lines.Select(x => new SettlementLineModel { Code = x.Code, Description = x.Description, Type = x.Type, Amount = x.Amount, PostingRole = x.PostingRole, Basis = x.Basis }).ToList();
        edited.Add(new SettlementLineModel { Code = "TAXADJ", Description = "Tax adjustment", Type = "Deduction", Amount = 1000.5m, PostingRole = "DEFAULT_EMPLOYEE_TAX_PAYABLE" });
        var saved = await l.Settlements.UpdateAsync(l.H.Admin(), s.Uuid, new SettlementUpdate { Lines = edited });
        saved.TotalDeductions.Should().Be(1300.5m);
        saved.Net.Should().Be(s.Net - 1000.5m);
    }

    [Fact]
    public async Task Pay_posts_payable_to_bank_once_and_no_target_still_lets_it_finish()
    {
        var (l, e, _) = await WorldAsync();
        l.Gateway.Next = PostingStatus.NoTargetConfigured;
        var s = await l.Settlements.ComputeAsync(l.H.Admin(), new SettlementComputeRequest { EmployeeUuid = e.Uuid, LastWorkingDay = Last });
        await l.Settlements.ApproveAsync(l.H.Admin(), s.Uuid);
        (await l.Settlements.PostAsync(l.H.Admin(), s.Uuid)).PostingState.Should().Be("NoTarget");
        var pay = new LifePayRequest { PaidOn = Last, PaidFromAccount = "Bank" };
        var paid = await l.Settlements.PayAsync(l.H.Admin(), s.Uuid, pay);
        paid.Status.Should().Be("Paid");
        var tx = l.Gateway.Posted.Last();
        tx.DocumentType.Should().Be(FinDocType.PayrollPayment);
        tx.Lines.Single(x => x.Debit > 0).AccountRole.Should().Be("DEFAULT_SALARY_PAYABLE");
        tx.Lines.Single(x => x.Debit > 0).Debit.Should().Be(paid.Net);
        await FluentActions.Awaiting(() => l.Settlements.PayAsync(l.H.Admin(), s.Uuid, pay)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task An_employee_who_owes_more_than_earned_posts_balanced_and_cannot_be_paid()
    {
        var (l, e, id) = await WorldAsync();
        var row = await l.H.Db.Employees.IgnoreQueryFilters().FirstAsync(x => x.Id == id);
        row.JoinDate = new DateOnly(2026, 3, 15);
        await l.H.Db.SaveChangesAsync();
        await l.DisbursedLoanAsync(e.Uuid, 999999m, 1);
        var s = await l.Settlements.ComputeAsync(l.H.Admin(), new SettlementComputeRequest { EmployeeUuid = e.Uuid, LastWorkingDay = Last });
        s.Net.Should().BeNegative();
        await l.Settlements.ApproveAsync(l.H.Admin(), s.Uuid);
        await l.Settlements.PostAsync(l.H.Admin(), s.Uuid);
        var tx = l.Gateway.Posted.Last();
        tx.Lines.Sum(x => x.Debit).Should().Be(tx.Lines.Sum(x => x.Credit));
        (await FluentActions.Awaiting(() => l.Settlements.PayAsync(l.H.Admin(), s.Uuid, new LifePayRequest { PaidOn = Last })).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("SETTLEMENT_NOT_PAYABLE");
    }

    [Fact]
    public async Task Increment_applied_creates_the_new_assignment_and_supersedes_the_old()
    {
        var (l, e, id) = await WorldAsync();
        var admin = l.H.Admin();
        var p = await l.Increments.CreateAsync(admin, new IncrementCreate { EmployeeUuid = e.Uuid, Percent = 10m, EffectiveDate = new DateOnly(2026, 7, 1) });
        p.CurrentBasic.Should().Be(60000m);
        (await FluentActions.Awaiting(() => l.Increments.ApplyAsync(admin, p.Uuid)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("INCREMENT_NOT_APPROVED");
        await l.Increments.ApproveAsync(admin, p.Uuid);
        var done = await l.Increments.ApplyAsync(admin, p.Uuid);

        done.Status.Should().Be("Applied");
        done.NewBasic.Should().Be(66000m);
        done.AppliedAssignmentUuid.Should().NotBeNull();
        var rows = await l.H.Db.SalaryAssignments.IgnoreQueryFilters().Include(a => a.Lines).ThenInclude(x => x.Component).Where(a => a.EmployeeId == id).OrderBy(a => a.EffectiveFrom).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].ApprovalStatus.Should().Be("Superseded");
        rows[0].EffectiveTo.Should().Be(new DateOnly(2026, 6, 30));
        rows[1].ApprovalStatus.Should().Be("Approved");
        rows[1].Lines.Single(x => x.Component!.Code == "BASIC").Amount.Should().Be(66000m);
        rows[1].Lines.Single(x => x.Component!.Code == "HRENT").Amount.Should().Be(40000m);
        rows[1].IncrementProposalId.Should().NotBeNull();
        await FluentActions.Awaiting(() => l.Increments.ApplyAsync(admin, p.Uuid)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Increment_needs_exactly_one_value_and_an_earlier_date_than_a_later_salary()
    {
        var (l, e, _) = await WorldAsync();
        var admin = l.H.Admin();
        await FluentActions.Awaiting(() => l.Increments.CreateAsync(admin, new IncrementCreate { EmployeeUuid = e.Uuid, EffectiveDate = Last })).Should().ThrowAsync<BadRequestException>();
        var p = await l.Increments.CreateAsync(admin, new IncrementCreate { EmployeeUuid = e.Uuid, NewBasic = 70000m, EffectiveDate = new DateOnly(2026, 1, 1) });
        await l.Increments.ApproveAsync(admin, p.Uuid);
        (await FluentActions.Awaiting(() => l.Increments.ApplyAsync(admin, p.Uuid)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("SALARY_OVERLAP");
    }
}
