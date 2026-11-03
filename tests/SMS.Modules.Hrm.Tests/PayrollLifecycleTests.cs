using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Att;
using SMS.Modules.Hrm.Services.Pay;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Tasks 237–239, 267–272, 287–307, E-8, E-10: lifecycle, G-21, posting, no-target, payment, reversal, date policy, bank file, payslips, privacy.</summary>
public sealed class PayrollLifecycleTests
{
    private static async Task<PayHarness> Ready() => await new PayHarness().ReadyAsync();

    // ── transitions ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Happy_path_create_calculate_review_approve_finalize_post_pay()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m, 45_000m);
        h.Gateway.PostsToGl();
        var runs = h.Runs();

        var run = await h.NewRunAsync(runs);
        run.Status.Should().Be("Draft");
        run.RunNumber.Should().Be("PAY-2026-10");
        run.CurrencyCode.Should().Be("PKR");
        run.AllowedActions.Should().Contain("calculate");
        (await runs.CalculateAsync(h.Admin, run.Uuid)).Status.Should().Be("Calculated");
        (await runs.ReviewAsync(h.Admin, run.Uuid)).Status.Should().Be("UnderReview");
        (await runs.ApproveAsync(h.Admin, run.Uuid, false)).Status.Should().Be("Approved");

        var posted = await runs.FinalizeAsync(h.Admin, run.Uuid);
        posted.Status.Should().Be("Posted");
        posted.PostingDate.Should().Be(new DateOnly(2026, 11, 1));
        posted.Postings.Single(p => p.Kind == "PayrollRun").Status.Should().Be("Posted");
        h.Gateway.Posted.Should().ContainSingle(t => t.DocumentType == FinDocType.PayrollRun);

        var paid = await runs.PayAsync(h.Admin, run.Uuid, new PayrollPayRequest { PaidOn = new DateOnly(2026, 11, 2), Reference = "BANK-1" });
        paid.Status.Should().Be("Paid");
        paid.Postings.Single(p => p.Kind == "PayrollPayment").Status.Should().Be("Posted");
        var payment = h.Gateway.Posted.Single(t => t.DocumentType == FinDocType.PayrollPayment);
        payment.Lines.Should().HaveCount(2);
        payment.Lines[0].AccountRole.Should().Be("DEFAULT_SALARY_PAYABLE");
        payment.Lines[0].Debit.Should().Be(paid.TotalNet);
        payment.Lines[1].AccountRole.Should().Be("DEFAULT_BANK");
        payment.Lines[1].Credit.Should().Be(paid.TotalNet);
        payment.TransactionDate.Should().Be(new DateTime(2026, 11, 2));
    }

    [Theory]
    [InlineData("review")]
    [InlineData("approve")]
    [InlineData("finalize")]
    [InlineData("reopen")]
    [InlineData("reverse")]
    public async Task A_draft_cannot_jump(string action)
    {
        var h = await Ready();
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        Func<Task> act = action switch
        {
            "review" => () => runs.ReviewAsync(h.Admin, run.Uuid),
            "approve" => () => runs.ApproveAsync(h.Admin, run.Uuid, false),
            "finalize" => () => runs.FinalizeAsync(h.Admin, run.Uuid),
            "reopen" => () => runs.ReopenAsync(h.Admin, run.Uuid),
            _ => () => runs.ReverseAsync(h.Admin, run.Uuid, "why"),
        };
        await act.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_STATUS*");
    }

    [Fact]
    public async Task Later_states_refuse_earlier_actions()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 50_000m);
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var recalc = () => runs.CalculateAsync(h.Admin, run.Uuid);
        await recalc.Should().ThrowAsync<ConflictException>();
        await runs.FinalizeAsync(h.Admin, run.Uuid);
        await recalc.Should().ThrowAsync<ConflictException>();
        var cancel = () => runs.CancelAsync(h.Admin, run.Uuid);
        await cancel.Should().ThrowAsync<ConflictException>();
        var again = () => runs.FinalizeAsync(h.Admin, run.Uuid);
        await again.Should().ThrowAsync<ConflictException>();
        h.Gateway.Posted.Should().ContainSingle();
    }

    [Fact]
    public async Task Reopen_and_cancel_and_a_new_number_after_cancel()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 50_000m);
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        (await runs.ReopenAsync(h.Admin, run.Uuid)).Status.Should().Be("Calculated");
        (await runs.CancelAsync(h.Admin, run.Uuid)).Status.Should().Be("Cancelled");
        var next = await h.NewRunAsync(runs);
        next.RunNumber.Should().Be("PAY-2026-10-R1");
    }

    [Fact]
    public async Task Same_or_overlapping_period_is_refused_and_a_group_is_separate()
    {
        var h = await Ready();
        var runs = h.Runs();
        await h.NewRunAsync(runs);
        var same = () => h.NewRunAsync(runs);
        await same.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_RUN_EXISTS*");
        var overlap = () => runs.CreateAsync(h.Admin, new PayrollRunCreate { PeriodStart = new DateOnly(2026, 10, 15), PeriodEnd = new DateOnly(2026, 11, 14), PayDate = new DateOnly(2026, 11, 15) });
        await overlap.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_RUN_EXISTS*");
        var other = await runs.CreateAsync(h.Admin, new PayrollRunCreate { PeriodStart = PayHarness.October, PeriodEnd = PayHarness.OctoberEnd, PayDate = new DateOnly(2026, 11, 1), PayrollGroup = "Factory" });
        other.RunNumber.Should().Be("PAY-2026-10-FACTORY");
        var bad = () => runs.CreateAsync(h.Admin, new PayrollRunCreate { PeriodStart = new DateOnly(2027, 1, 1), PeriodEnd = new DateOnly(2027, 1, 10), PayDate = new DateOnly(2027, 1, 11) });
        await bad.Should().ThrowAsync<BadRequestException>().WithMessage("PAYROLL_PERIOD_INVALID*");
    }

    [Fact]
    public async Task A_payroll_group_only_takes_its_own_people()
    {
        var h = await Ready();
        var plain = await h.StaffAsync("Plain", 50_000m);
        var factory = await h.StaffAsync("Factory", 60_000m);
        var record = await h.Db.EmploymentRecords.FirstAsync(r => r.Employee!.Uuid == factory.Uuid);
        record.PayrollGroup = "Factory";
        await h.Db.SaveChangesAsync();
        var runs = h.Runs();
        var main = await h.NewRunAsync(runs);
        var calc = await runs.CalculateAsync(h.Admin, main.Uuid);
        calc.TotalEmployees.Should().Be(1);
        (await runs.ListLinesAsync(h.Admin, main.Uuid, null, false, 1, 10)).Data.Single().EmployeeUuid.Should().Be(plain.Uuid);
    }

    [Fact]
    public async Task Blocked_lines_stop_approve_unless_excluded_and_none_payable_is_refused()
    {
        var h = await Ready();
        await h.StaffAsync("Good", 50_000m);
        await h.StaffAsync("NoBank", 70_000m, bank: false);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        await runs.ReviewAsync(h.Admin, run.Uuid);
        var refused = () => runs.ApproveAsync(h.Admin, run.Uuid, false);
        await refused.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_HAS_BLOCKED*");
        (await runs.ApproveAsync(h.Admin, run.Uuid, true)).Status.Should().Be("Approved");

        var finalized = await runs.FinalizeAsync(h.Admin, run.Uuid);
        var tx = h.Gateway.Posted.Single();
        tx.Description.Should().Contain("1 employees");
        tx.Lines.Where(l => l.AccountRole == "DEFAULT_SALARY_EXPENSE").Sum(l => l.Debit).Should().Be(50_000m, "the blocked person's pay is not in the journal");
        finalized.TotalGross.Should().Be(50_000m);

        var h2 = await Ready();
        await h2.StaffAsync("OnlyBlocked", 1_000m, bank: false);
        var runs2 = h2.Runs();
        var run2 = await h2.NewRunAsync(runs2);
        await runs2.CalculateAsync(h2.Admin, run2.Uuid);
        await runs2.ReviewAsync(h2.Admin, run2.Uuid);
        var none = () => runs2.ApproveAsync(h2.Admin, run2.Uuid, true);
        await none.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_NO_PAYABLE_LINES*");
    }

    // ── G-21 ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unverified_slab_or_rate_blocks_finalize_and_lists_them_and_nothing_is_consumed()
    {
        var h = await new PayHarness().ReadyAsync(verify: false);
        var e = await h.StaffAsync("Ali", 100_000m);
        await h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "BONUS", Amount = 100m, Reason = "x" });
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var act = () => runs.FinalizeAsync(h.Admin, run.Uuid);
        var error = await act.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_STATUTORY_UNVERIFIED*");
        error.Which.Message.Should().Contain("Test tax table").And.Contain("EOBI").And.Contain("Provident fund");
        error.Which.Message.Should().NotContain("social security", "the standard structure does not use it");

        h.Db.ChangeTracker.Clear();
        (await runs.GetAsync(h.Admin, run.Uuid)).Status.Should().Be("Approved");
        h.Gateway.Posted.Should().BeEmpty();
        (await h.Adjustments.ListAsync(h.Admin, e.Uuid, pending: true)).Should().ContainSingle("nothing was consumed");

        var readiness = await runs.GetReadinessAsync(h.Admin, run.Uuid);
        readiness.UnverifiedStatutory.Should().HaveCount(3);

        // Verified one by one: still blocked until the last.
        var table = (await h.Statutory.ListTaxTablesAsync(h.Admin)).First(t => t.Code == "PK-2027");
        await h.Statutory.VerifyTaxTableAsync(h.Admin, table.Uuid, null);
        await act.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_STATUTORY_UNVERIFIED*");
        h.Db.ChangeTracker.Clear();
        await h.VerifyAllAsync();
        (await runs.FinalizeAsync(h.Admin, run.Uuid)).Status.Should().Be("Finalized");
    }

    [Fact]
    public async Task A_structure_without_statutory_components_is_not_blocked_by_them()
    {
        var h = await new PayHarness().ReadyAsync(verify: false);
        var comps = await h.Components.ListAsync(h.Admin);
        var simple = await h.Structures.CreateAsync(h.Admin, new SalaryStructureUpsert { Code = "SIMPLE", Name = "Simple", Lines = [new() { ComponentUuid = comps.First(c => c.Code == "BASIC").Uuid, Sequence = 1 }] });
        var e = await h.Core.Employees.CreateAsync(h.Admin, new EmployeeUpsert { FirstName = "A", LastName = "B", Employment = h.Core.Hire(h.Structure) });
        await h.Core.Bank.CreateAsync(h.Admin, e.Uuid, new EmployeeBankAccountUpsert { BankName = "HBL", AccountNumber = "1" });
        var draft = await h.Salaries.CreateAsync(h.Admin, e.Uuid, new SalaryAssignmentCreate { StructureUuid = simple.Uuid, EffectiveFrom = new DateOnly(2026, 1, 1), Lines = [new() { ComponentUuid = comps.First(c => c.Code == "BASIC").Uuid, Amount = 10_000m }] });
        await h.Salaries.SubmitAsync(h.Admin, draft.Uuid);
        await h.Salaries.ApproveAsync(h.Admin, draft.Uuid);
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        (await runs.FinalizeAsync(h.Admin, run.Uuid)).Status.Should().Be("Finalized");
    }

    [Fact]
    public async Task A_rate_changed_after_calculation_asks_for_a_recalculation()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var eobi = (await h.Statutory.ListRatesAsync(h.Admin)).First(r => r.RateType == "EOBI");
        await h.Statutory.UpdateRateAsync(h.Admin, eobi.Uuid, new StatutoryRateUpsert { RateType = "EOBI", Name = eobi.Name, EmployeePercent = 2m, EmployerPercent = 5m, FixedBasis = eobi.FixedBasis, EffectiveFrom = eobi.EffectiveFrom });
        await h.Statutory.VerifyRateAsync(h.Admin, eobi.Uuid, "Re-reviewed");
        var act = () => runs.FinalizeAsync(h.Admin, run.Uuid);
        await act.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_STATUTORY_CHANGED*");
        h.Gateway.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_tax_table_changed_after_calculation_asks_for_a_recalculation()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var table = (await h.Statutory.ListTaxTablesAsync(h.Admin)).First(t => t.Code == "PK-2027");
        await h.Statutory.UpdateTaxTableAsync(h.Admin, table.Uuid, new TaxTableUpsert { Code = table.Code, Name = table.Name, EffectiveFrom = table.EffectiveFrom, EffectiveTo = table.EffectiveTo, Slabs = [new TaxSlabModel { FromAmount = 0m, RatePercent = 50m }] });
        await h.Statutory.VerifyTaxTableAsync(h.Admin, table.Uuid, null);
        var act = () => runs.FinalizeAsync(h.Admin, run.Uuid);
        await act.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_STATUTORY_CHANGED*");
    }

    // ── posting matrix ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Posting_matrix_is_balanced_and_summarized()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000.55m, 45_000.33m);
        await h.StaffAsync("Bano", 80_123.45m);
        var e = await h.StaffAsync("Cash", 33_333.33m);
        await h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "BONUS", Amount = 1_000m, Reason = "Eid" });
        await h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "OTHDED", Amount = 123.45m, Reason = "Uniform" });
        h.Loans.Installment = 2_000m;
        h.Providers.Add(new PerEmployeeProvider((await h.Db.Employees.FirstAsync(x => x.Uuid == e.Uuid)).Id, h.Loans));
        h.Gateway.PostsToGl();
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var result = await runs.FinalizeAsync(h.Admin, run.Uuid);
        var tx = h.Gateway.Posted.Single();

        tx.Lines.Sum(l => l.Debit).Should().Be(tx.Lines.Sum(l => l.Credit));
        tx.IdempotencyKey.Should().Be($"hrm:PayrollRun:{run.Uuid}");
        tx.SourceModule.Should().Be("hrm");
        tx.PartnerId.Should().BeNull();
        tx.Lines.Should().OnlyContain(l => l.Debit >= 0m && l.Credit >= 0m && (l.Debit == 0m) != (l.Credit == 0m), "the gateway takes one-sided non-negative lines");
        tx.Lines.Should().NotContain(l => l.AccountRole == "DEFAULT_ROUNDING", "exact components need no rounding");
        tx.Lines.GroupBy(l => (l.AccountRole, l.CostCenter)).Should().OnlyContain(g => g.Count() == 1, "one line per role × cost center");

        tx.Lines.Single(l => l.AccountRole == "DEFAULT_SALARY_PAYABLE").Credit.Should().Be(result.TotalNet);
        tx.Lines.Single(l => l.AccountRole == "DEFAULT_BONUS_EXPENSE").Debit.Should().Be(1_000m);
        tx.Lines.Single(l => l.AccountRole == "DEFAULT_OTHER_DEDUCTION_PAYABLE").Credit.Should().Be(123.45m);
        tx.Lines.Single(l => l.AccountRole == "DEFAULT_EMPLOYEE_LOAN_RECEIVABLE").Credit.Should().Be(2_000m);
        var er = tx.Lines.Single(l => l.AccountRole == "DEFAULT_EMPLOYER_CONTRIB_EXPENSE").Debit;
        er.Should().Be(result.TotalEmployerCost);
        tx.Lines.Single(l => l.AccountRole == "DEFAULT_EOBI_PAYABLE").Credit.Should().Be(370m * 3 + 1_850m * 3, "employee + employer parts");
        result.Postings.Single().TotalDebit.Should().Be(result.Postings.Single().TotalCredit);

        var journal = await runs.GetJournalAsync(h.Admin, run.Uuid);
        journal.TotalDebit.Should().Be(journal.TotalCredit);
        var reconciliation = await runs.GetReconciliationAsync(h.Admin, run.Uuid);
        reconciliation.Balanced.Should().BeTrue();
        reconciliation.RunNet.Should().Be(result.TotalNet);
    }

    [Fact]
    public async Task Liabilities_post_once_on_the_payroll_cost_center_and_the_expense_follows_the_department()
    {
        var h = await Ready();
        var sales = await h.Core.CostCenters.CreateAsync(h.Admin, new CostCenterUpsert { Code = "SALES", Name = "Sales" });
        var field = await h.Core.Departments.CreateAsync(h.Admin, new DepartmentUpsert { Code = "SLS", Name = "Selling", CostCenterUuid = sales.Uuid });
        await h.Core.Settings.UpdateAsync(h.Admin, new HrmSettingsUpdate { PayrollCostCenterMode = "BY_DEPARTMENT" });
        await h.StaffAsync("Ops", 100_000m);
        await h.StaffAsync("Seller", 60_000m, department: field.Uuid);
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        await runs.FinalizeAsync(h.Admin, run.Uuid);
        var tx = h.Gateway.Posted.Single();
        tx.Lines.Single(l => l.AccountRole == "DEFAULT_SALARY_EXPENSE" && l.CostCenter == "SALES").Debit.Should().Be(60_000m);
        tx.Lines.Single(l => l.AccountRole == "DEFAULT_SALARY_EXPENSE" && l.CostCenter == "OPS").Debit.Should().Be(100_000m);
        tx.Lines.Where(l => l.AccountRole == "DEFAULT_EMPLOYER_CONTRIB_EXPENSE").Select(l => l.CostCenter).Should().BeEquivalentTo("SALES", "OPS");
        foreach (var liability in tx.Lines.Where(l => l.Credit > 0m))
            liability.CostCenter.Should().Be("OPS", "liabilities post once on the payroll cost center");
        tx.Lines.Sum(l => l.Debit).Should().Be(tx.Lines.Sum(l => l.Credit));
    }

    [Fact]
    public async Task A_percentage_split_posts_to_each_center_exactly()
    {
        var h = await Ready();
        var second = await h.Core.CostCenters.CreateAsync(h.Admin, new CostCenterUpsert { Code = "CC2", Name = "Second" });
        await h.Core.Settings.UpdateAsync(h.Admin, new HrmSettingsUpdate { PayrollCostCenterMode = "BY_DEPARTMENT" });
        var e = await h.StaffAsync("Split", 100_000.01m);
        var record = await h.Db.EmploymentRecords.FirstAsync(r => r.Employee!.Uuid == e.Uuid);
        var firstCenter = await h.Db.CostCenters.FirstAsync(c => c.Uuid == h.Structure.CostCenter);
        var secondCenter = await h.Db.CostCenters.FirstAsync(c => c.Uuid == second.Uuid);
        h.Db.EmploymentCostSplits.Add(new EmploymentCostSplit { OrganizationId = h.Org, EmploymentRecordId = record.Id, CostCenterId = firstCenter.Id, Percent = 60m });
        h.Db.EmploymentCostSplits.Add(new EmploymentCostSplit { OrganizationId = h.Org, EmploymentRecordId = record.Id, CostCenterId = secondCenter.Id, Percent = 40m });
        await h.Db.SaveChangesAsync();
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        await runs.FinalizeAsync(h.Admin, run.Uuid);
        var tx = h.Gateway.Posted.Single();
        var salary = tx.Lines.Where(l => l.AccountRole == "DEFAULT_SALARY_EXPENSE").ToList();
        salary.Should().HaveCount(2);
        salary.Sum(l => l.Debit).Should().Be(100_000.01m);
        salary.Single(l => l.CostCenter == "CC2").Debit.Should().Be(40_000m);
        tx.Lines.Sum(l => l.Debit).Should().Be(tx.Lines.Sum(l => l.Credit));
        // The line still reads as one BASIC.
        var line = await h.LineAsync(runs, run.Uuid, e.Uuid);
        line.Components.Count(c => c.Code == "BASIC").Should().Be(1);
        PayHarness.Amount(line, "BASIC").Should().Be(100_000.01m);
    }

    [Fact]
    public async Task Changing_the_payroll_cost_center_never_reclassifies_a_run_on_retry()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        h.Gateway.Status = PostingStatus.PartialFailure;
        h.Gateway.Targets = [new TargetResult { TargetCode = "internal_gl", Status = TargetPostingStatus.Failed, Error = "boom" }];
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        await runs.FinalizeAsync(h.Admin, run.Uuid);

        var other = await h.Core.CostCenters.CreateAsync(h.Admin, new CostCenterUpsert { Code = "NEW", Name = "New" });
        await h.Core.Settings.UpdateAsync(h.Admin, new HrmSettingsUpdate { PayrollCostCenterUuid = other.Uuid });
        await runs.RetryPostingAsync(h.Admin, run.Uuid);
        var (first, second) = (h.Gateway.Posted[0], h.Gateway.Posted[1]);
        second.Lines.Select(l => (l.AccountRole, l.Debit, l.Credit, l.CostCenter)).Should().Equal(first.Lines.Select(l => (l.AccountRole, l.Debit, l.Credit, l.CostCenter)), "the same payload, so the gateway's fingerprint holds");
        second.Lines.Should().OnlyContain(l => l.CostCenter == "OPS");
    }

    [Fact]
    public async Task Privacy_no_name_number_or_individual_amount_in_the_payload()
    {
        var h = await Ready();
        var a = await h.StaffAsync("Zubair", 111_111m);
        var b = await h.StaffAsync("Yasmeen", 222_222m);
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        await runs.FinalizeAsync(h.Admin, run.Uuid);
        await runs.PayAsync(h.Admin, run.Uuid, new PayrollPayRequest { PaidOn = new DateOnly(2026, 11, 2), Reference = "R" });
        foreach (var tx in h.Gateway.Posted)
        {
            var text = System.Text.Json.JsonSerializer.Serialize(tx);
            foreach (var secret in new[] { a.FullName, b.FullName, a.EmployeeNumber, b.EmployeeNumber, "Zubair", "Yasmeen" })
                text.Should().NotContain(secret);
            tx.PartnerName.Should().BeNull();
            tx.Metadata?.Document.Should().BeNull();
        }
        h.Gateway.Posted[0].Lines.Should().NotContain(l => l.Debit == 111_111m || l.Debit == 222_222m, "two people share the line");
        h.Gateway.Posted[0].Description.Should().Be($"Payroll {run.RunNumber}, 2026-10-01 to 2026-10-31, 2 employees");
    }

    // ── no target, failure, held ───────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PostingStatus.NoTargetConfigured)]
    [InlineData(PostingStatus.ModuleNotLicensed)]
    public async Task No_target_stays_finalized_is_not_an_error_and_can_be_paid(PostingStatus status)
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        h.Gateway.Status = status;
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var result = await runs.FinalizeAsync(h.Admin, run.Uuid);
        result.Status.Should().Be("Finalized");
        result.PostingNote.Should().Be("No posting required.");
        result.Postings.Single().Status.Should().Be("NoTarget");
        result.Postings.Single().LastError.Should().BeNull();
        result.AllowedActions.Should().Contain("pay");
        result.AllowedActions.Should().NotContain("retry-posting");

        var csv = await runs.ExportJournalAsync(h.Admin, run.Uuid, excel: false);
        Encoding.UTF8.GetString(csv.Content).Should().Contain("DEFAULT_SALARY_EXPENSE");
        var xlsx = await runs.ExportJournalAsync(h.Admin, run.Uuid, excel: true);
        xlsx.Content.Length.Should().BeGreaterThan(100);

        var paid = await runs.PayAsync(h.Admin, run.Uuid, new PayrollPayRequest { PaidOn = new DateOnly(2026, 11, 2), Reference = "R" });
        paid.Status.Should().Be("Paid");
        paid.Postings.Single(p => p.Kind == "PayrollPayment").Status.Should().Be("NoTarget");
    }

    [Fact]
    public async Task Journal_export_needs_a_finalized_run()
    {
        var h = await Ready();
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        var act = () => runs.ExportJournalAsync(h.Admin, run.Uuid, false);
        await act.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_NOT_FINALIZED*");
    }

    [Fact]
    public async Task Target_failure_stays_finalized_and_retry_reaches_posted_with_the_same_key()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        h.Gateway.Status = PostingStatus.PartialFailure;
        h.Gateway.Message = "internal_gl: Period closed";
        h.Gateway.Targets = [new TargetResult { TargetCode = "internal_gl", Status = TargetPostingStatus.Failed, Error = "Period closed" }];
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var failed = await runs.FinalizeAsync(h.Admin, run.Uuid);
        failed.Status.Should().Be("Finalized");
        failed.Postings.Single().Status.Should().Be("Failed");
        failed.PostingNote.Should().Contain("Period closed");
        failed.AllowedActions.Should().Contain("retry-posting").And.NotContain("pay");

        var pay = () => runs.PayAsync(h.Admin, run.Uuid, new PayrollPayRequest { PaidOn = new DateOnly(2026, 11, 2), Reference = "R" });
        await pay.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_NOT_PAYABLE*");
        h.Db.ChangeTracker.Clear();

        h.Gateway.PostsToGl();
        h.Gateway.Message = null;
        var retried = await runs.RetryPostingAsync(h.Admin, run.Uuid);
        retried.Status.Should().Be("Posted");
        retried.Postings.Single().Status.Should().Be("Posted");
        retried.Postings.Single().Attempts.Should().Be(2);
        retried.PostingNote.Should().BeNull();
        h.Gateway.Posted.Select(t => t.IdempotencyKey).Distinct().Should().ContainSingle();

        await runs.RetryPostingAsync(h.Admin, run.Uuid); // nothing left to do
        h.Gateway.Posted.Should().HaveCount(2);
    }

    [Fact]
    public async Task One_target_posted_and_another_failed_moves_to_posted_and_stays_retryable()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        h.Gateway.Status = PostingStatus.PartialFailure;
        h.Gateway.Targets =
        [
            new TargetResult { TargetCode = "internal_gl", Status = TargetPostingStatus.Posted },
            new TargetResult { TargetCode = "quickbooks", Status = TargetPostingStatus.Failed, Error = "No Class" },
        ];
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var result = await runs.FinalizeAsync(h.Admin, run.Uuid);
        result.Status.Should().Be("Posted");
        result.Postings.Single().Status.Should().Be("Failed");
        result.AllowedActions.Should().Contain("retry-posting");
    }

    [Fact]
    public async Task Held_and_queued_stay_finalized_and_validation_failure_is_a_failure()
    {
        PayrollPostingStatusCheck(TargetPostingStatus.Held, "Held");
        PayrollPostingStatusCheck(TargetPostingStatus.Queued, "Pending");
        PayrollRunService.StatusFrom(new PostingResult { Status = PostingStatus.ValidationFailed, Message = "x" }, "PayrollRun").Should().Be("Failed");
        PayrollRunService.StatusFrom(new PostingResult { Status = PostingStatus.DuplicateSkipped, TargetResults = [new TargetResult { TargetCode = "g", Status = TargetPostingStatus.Posted }] }, "PayrollRun").Should().Be("Posted");
        PayrollRunService.StatusFrom(new PostingResult { Status = PostingStatus.Success, TargetResults = [TargetResult.Skipped("g", "never posted")] }, "Void").Should().Be("Posted");
        PayrollRunService.StatusFrom(new PostingResult { Status = PostingStatus.Success, TargetResults = [TargetResult.Skipped("g", "off")] }, "PayrollRun").Should().Be("Failed");

        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        h.Gateway.Status = PostingStatus.Success;
        h.Gateway.Targets = [new TargetResult { TargetCode = "quickbooks", Status = TargetPostingStatus.Held }];
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var result = await runs.FinalizeAsync(h.Admin, run.Uuid);
        result.Status.Should().Be("Finalized");
        result.Postings.Single().Status.Should().Be("Held");
        result.PostingNote.Should().Contain("Held");
    }

    private static void PayrollPostingStatusCheck(TargetPostingStatus target, string expected) =>
        PayrollRunService.StatusFrom(new PostingResult { Status = PostingStatus.Success, TargetResults = [new TargetResult { TargetCode = "q", Status = target }] }, "PayrollRun").Should().Be(expected);

    [Fact]
    public async Task Readiness_blocks_finalize_with_the_missing_roles_and_changes_nothing()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        h.Readiness.Answer = new PayrollPostingReadiness { IsReady = false, NoTarget = false, InternalGlActive = true, InternalGlReady = false, MissingGlRoles = ["DEFAULT_SALARY_PAYABLE"] };
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var act = () => runs.FinalizeAsync(h.Admin, run.Uuid);
        var error = await act.Should().ThrowAsync<ConflictException>().WithMessage("POSTING_READINESS*");
        error.Which.Message.Should().Contain("DEFAULT_SALARY_PAYABLE");
        (await runs.GetAsync(h.Admin, run.Uuid)).Status.Should().Be("Approved");
        h.Gateway.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task Default_readiness_asks_the_gl_seam_for_the_roles_the_run_uses()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        var id = (await h.Db.PayrollRuns.FirstAsync(r => r.Uuid == run.Uuid)).Id;
        var roles = new RecordingRoles { Missing = ["DEFAULT_EOBI_PAYABLE"] };
        var answer = await new DefaultPayrollPostingReadiness(h.Db, roles).CheckAsync(h.Org, id);
        answer.IsReady.Should().BeFalse();
        answer.MissingGlRoles.Should().Equal("DEFAULT_EOBI_PAYABLE");
        roles.Asked.Should().Contain(["DEFAULT_SALARY_EXPENSE", "DEFAULT_SALARY_PAYABLE", "DEFAULT_EOBI_PAYABLE", "DEFAULT_EMPLOYER_CONTRIB_EXPENSE", "DEFAULT_BANK"]);
        (await new DefaultPayrollPostingReadiness(h.Db, new NullPostingRoleReadiness()).CheckAsync(h.Org, id)).IsReady.Should().BeTrue();
    }

    private sealed class RecordingRoles : IPostingRoleReadiness
    {
        public List<string> Missing { get; set; } = [];
        public List<string> Asked { get; } = [];
        public Task<IReadOnlyList<string>> MissingRolesAsync(IReadOnlyCollection<string> roles, CancellationToken ct = default)
        {
            Asked.AddRange(roles);
            return Task.FromResult<IReadOnlyList<string>>(Missing);
        }
    }

    // ── post-date policy ───────────────────────────────────────────────────────────────────────────────────────────

    private static async Task<(PayHarness H, IPayrollRunService Runs, PayrollRunModel Run)> PolicyAsync(string policy)
    {
        var h = await Ready();
        await h.Core.Settings.UpdateAsync(h.Admin, new HrmSettingsUpdate { PostDatePolicy = policy });
        await h.StaffAsync("Ali", 100_000m);
        h.Readiness.Answer = new PayrollPostingReadiness { InternalGlActive = true, NoTarget = false };
        h.Gateway.PostsToGl();
        var runs = h.Runs();
        return (h, runs, await h.ApprovedRunAsync(runs));
    }

    [Fact]
    public async Task Pay_date_with_fallback_uses_the_pay_date_when_open_and_period_end_when_closed()
    {
        var (h, runs, run) = await PolicyAsync("PayDateWithFallback");
        h.Periods.Closed.Add(new DateOnly(2026, 11, 1));
        var result = await runs.FinalizeAsync(h.Admin, run.Uuid);
        result.PostingDate.Should().Be(PayHarness.OctoberEnd);
        result.PostingNote.Should().Contain("closed period");
        h.Gateway.Posted.Single().TransactionDate.Should().Be(new DateTime(2026, 10, 31));

        var (h2, runs2, run2) = await PolicyAsync("PayDateWithFallback");
        (await runs2.FinalizeAsync(h2.Admin, run2.Uuid)).PostingDate.Should().Be(new DateOnly(2026, 11, 1));
    }

    [Fact]
    public async Task Both_closed_fails_and_the_strict_policies_fail_on_their_date()
    {
        var (h, runs, run) = await PolicyAsync("PayDateWithFallback");
        h.Periods.Closed.UnionWith([new DateOnly(2026, 11, 1), PayHarness.OctoberEnd]);
        var act = () => runs.FinalizeAsync(h.Admin, run.Uuid);
        await act.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_PERIOD_CLOSED*");
        h.Db.ChangeTracker.Clear();
        (await runs.GetAsync(h.Admin, run.Uuid)).Status.Should().Be("Approved");

        var (h2, runs2, run2) = await PolicyAsync("PayDate");
        h2.Periods.Closed.Add(new DateOnly(2026, 11, 1));
        var strict = () => runs2.FinalizeAsync(h2.Admin, run2.Uuid);
        await strict.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_PERIOD_CLOSED*");

        var (h3, runs3, run3) = await PolicyAsync("PeriodEnd");
        (await runs3.FinalizeAsync(h3.Admin, run3.Uuid)).PostingDate.Should().Be(PayHarness.OctoberEnd);
    }

    [Fact]
    public async Task With_no_active_gl_the_period_guard_is_not_asked()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        h.Periods.Closed.UnionWith([new DateOnly(2026, 11, 1), PayHarness.OctoberEnd]); // an HR-only tenant has no open periods at all
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var result = await runs.FinalizeAsync(h.Admin, run.Uuid);
        result.Status.Should().Be("Finalized");
        result.PostingDate.Should().Be(new DateOnly(2026, 11, 1));
    }

    // ── consumption and reversal ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Finalize_consumes_inputs_once_and_reversal_releases_them_and_voids_the_journal()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Ali", 100_000m);
        var id = (await h.Db.Employees.FirstAsync(x => x.Uuid == e.Uuid)).Id;
        var adj = await h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "BONUS", Amount = 500m, Reason = "x" });
        h.Loans.Installment = 5_000m;
        h.Providers.Add(h.Loans);
        var attendance = new PayAttendance { Answer = i => new AttendancePayData(i, 31m, 31m, 22m, 0m, 22, 60, 90m, [], true) };
        h.Attendance = attendance;
        h.Gateway.PostsToGl();
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var runId = (await h.Db.PayrollRuns.FirstAsync(r => r.Uuid == run.Uuid)).Id;
        await runs.FinalizeAsync(h.Admin, run.Uuid);

        h.Loans.ConsumedBy.Should().Be(runId);
        h.Loans.ConsumeCalls.Should().Be(1);
        attendance.Stamped.Should().Equal(id);
        (await h.Adjustments.ListAsync(h.Admin, e.Uuid, pending: false)).Single().AppliedInRunUuid.Should().Be(run.Uuid);
        var applied = () => h.Adjustments.DeleteAsync(h.Admin, adj.Uuid);
        await applied.Should().ThrowAsync<ConflictException>().WithMessage("ADJUSTMENT_APPLIED*");
        h.Db.ChangeTracker.Clear();

        var reversed = await runs.ReverseAsync(h.Admin, run.Uuid, "Wrong month");
        reversed.Status.Should().Be("Reversed");
        reversed.ReversalReason.Should().Be("Wrong month");
        reversed.CorrectionRunUuid.Should().NotBeNull();
        var voidRow = reversed.Postings.Single(p => p.Kind == "Void");
        voidRow.Status.Should().Be("Posted");
        reversed.Postings.Single(p => p.Kind == "PayrollRun").Status.Should().Be("Reversed");
        var request = h.Gateway.Voids.Single();
        request.IdempotencyKey.Should().Be($"hrm:PayrollRun:{run.Uuid}:void");
        request.Reason.Should().Be("Wrong month");

        h.Loans.ConsumedBy.Should().BeNull();
        attendance.Released.Should().Be(1);
        (await h.Adjustments.ListAsync(h.Admin, e.Uuid, pending: true)).Should().ContainSingle();

        // The correction: a new Draft, a new number, which takes everything again.
        var correction = await runs.GetAsync(h.Admin, reversed.CorrectionRunUuid!.Value);
        correction.Status.Should().Be("Draft");
        correction.ReversalOfRunUuid.Should().Be(run.Uuid);
        correction.RunNumber.Should().Be("PAY-2026-10-R1");
        await runs.CalculateAsync(h.Admin, correction.Uuid);
        var line = await h.LineAsync(runs, correction.Uuid, e.Uuid);
        PayHarness.Amount(line, "LOAN").Should().Be(5_000m);
        PayHarness.Amount(line, "BONUS").Should().Be(500m);
    }

    [Fact]
    public async Task Reversal_after_no_target_calls_nothing_and_reversing_a_paid_run_is_refused()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        await runs.FinalizeAsync(h.Admin, run.Uuid);
        var reversed = await runs.ReverseAsync(h.Admin, run.Uuid, "Redo");
        reversed.Status.Should().Be("Reversed");
        h.Gateway.Voids.Should().BeEmpty();

        var h2 = await Ready();
        await h2.StaffAsync("Ali", 100_000m);
        var runs2 = h2.Runs();
        var run2 = await h2.ApprovedRunAsync(runs2);
        await runs2.FinalizeAsync(h2.Admin, run2.Uuid);
        await runs2.PayAsync(h2.Admin, run2.Uuid, new PayrollPayRequest { PaidOn = new DateOnly(2026, 11, 2), Reference = "R" });
        var act = () => runs2.ReverseAsync(h2.Admin, run2.Uuid, "Too late");
        await act.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_ALREADY_PAID*");
    }

    [Fact]
    public async Task A_failed_void_can_be_retried()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        h.Gateway.PostsToGl();
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        await runs.FinalizeAsync(h.Admin, run.Uuid);
        h.Gateway.Status = PostingStatus.PartialFailure;
        h.Gateway.Targets = [new TargetResult { TargetCode = "internal_gl", Status = TargetPostingStatus.Failed, Error = "Closed" }];
        var reversed = await runs.ReverseAsync(h.Admin, run.Uuid, "Redo");
        reversed.Postings.Single(p => p.Kind == "Void").Status.Should().Be("Failed");
        h.Gateway.PostsToGl();
        var retried = await runs.RetryPostingAsync(h.Admin, run.Uuid);
        retried.Postings.Single(p => p.Kind == "Void").Status.Should().Be("Posted");
        h.Gateway.Voids.Should().HaveCount(2);
        h.Gateway.Voids.Select(v => v.IdempotencyKey).Distinct().Should().ContainSingle();
        h.Gateway.Voids.Select(v => v.VoidDate).Distinct().Should().ContainSingle("the retry is dated like the first attempt");
    }

    [Fact]
    public async Task Inputs_that_changed_after_calculation_block_finalize_and_roll_back()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Ali", 100_000m);
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        await h.Adjustments.CreateAsync(h.Admin, new PayrollAdjustmentCreate { EmployeeUuid = e.Uuid, ComponentCode = "OTHDED", Amount = 100m, Reason = "Late" });
        var act = () => runs.FinalizeAsync(h.Admin, run.Uuid);
        await act.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_INPUTS_CHANGED*");
        h.Gateway.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task Overtime_approved_after_calculation_is_not_silently_stamped_paid()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        var hours = 60;
        var attendance = new PayAttendance { Answer = i => new AttendancePayData(i, 31m, 31m, 22m, 0m, 22, hours, hours, [], true) };
        h.Attendance = attendance;
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        hours = 120;
        var act = () => runs.FinalizeAsync(h.Admin, run.Uuid);
        await act.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_INPUTS_CHANGED*");
        attendance.Stamped.Should().BeEmpty();
    }

    [Fact]
    public async Task A_line_with_no_overtime_row_stamps_no_overtime()
    {
        var h = await Ready();
        await h.StaffAsync("Ali", 100_000m);
        var hours = 0;
        var attendance = new PayAttendance { Answer = i => new AttendancePayData(i, 31m, 31m, 22m, 0m, 22, hours, hours, [], true) };
        h.Attendance = attendance;
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        hours = 60; // approved after the calculation: left for the next run, unpaid
        (await runs.FinalizeAsync(h.Admin, run.Uuid)).Status.Should().Be("Finalized");
        attendance.Stamped.Should().BeEmpty();
    }

    [Fact]
    public async Task What_a_blocked_line_reserved_goes_back_at_finalize()
    {
        var h = await Ready();
        await h.StaffAsync("Good", 50_000m);
        h.Loans.Installment = 1_000m;
        var bad = await h.StaffAsync("NoBank", 70_000m, bank: false);
        var badId = (await h.Db.Employees.FirstAsync(x => x.Uuid == bad.Uuid)).Id;
        var onlyBlocked = new PerEmployeeProvider(badId, h.Loans);
        h.Providers.Add(onlyBlocked);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);
        h.Loans.ReservedBy.Should().NotBeNull("the calculation reserved it for the blocked person");
        await runs.ReviewAsync(h.Admin, run.Uuid);
        await runs.ApproveAsync(h.Admin, run.Uuid, true);
        await runs.FinalizeAsync(h.Admin, run.Uuid);
        h.Loans.ReservedBy.Should().BeNull();
        h.Loans.ConsumedBy.Should().BeNull();
    }

    private sealed class PerEmployeeProvider(int employeeId, PayLoanProvider inner) : IPayrollInputProvider
    {
        public Task<IReadOnlyList<PayrollInput>> GetInputsAsync(Guid organizationId, int runId, int employee, DateOnly from, DateOnly to, CancellationToken ct = default) =>
            employee == employeeId ? inner.GetInputsAsync(organizationId, runId, employee, from, to, ct) : Task.FromResult<IReadOnlyList<PayrollInput>>([]);
        public Task MarkConsumedAsync(Guid organizationId, int runId, int employee, CancellationToken ct = default) =>
            employee == employeeId ? inner.MarkConsumedAsync(organizationId, runId, employee, ct) : Task.CompletedTask;
        public Task ReleaseAsync(Guid organizationId, int runId, CancellationToken ct = default) => inner.ReleaseAsync(organizationId, runId, ct);
    }

    // ── bank file ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Bank_file_has_the_columns_logs_each_employee_and_does_not_mark_paid()
    {
        var h = await Ready();
        var a = await h.StaffAsync("Ali", 100_000m);
        var b = await h.StaffAsync("Baber", 60_000m);
        var cash = await h.StaffAsync("Cashier", 30_000m, method: "Cash");
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var draftFile = () => runs.GetBankFileAsync(h.Admin, run.Uuid);
        await draftFile.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_NOT_PAYABLE*");
        var finalized = await runs.FinalizeAsync(h.Admin, run.Uuid);

        var file = await runs.GetBankFileAsync(h.Admin, run.Uuid);
        file.ContentType.Should().Be("text/csv");
        var text = Encoding.UTF8.GetString(file.Content);
        text.Should().Contain("Employee Name,Bank,Account Number,IBAN,Amount,Reference");
        text.Should().Contain("1234567890" + "Ali".Length);
        text.Should().Contain("HBL");
        text.Should().Contain(run.RunNumber);
        text.Should().NotContain("Cashier", "a cash employee is not in a bank file");
        var aLine = await h.LineAsync(runs, run.Uuid, a.Uuid);
        text.Should().Contain(aLine.Net.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

        (await runs.GetAsync(h.Admin, run.Uuid)).Status.Should().Be(finalized.Status, "the contract marks Paid on /pay, not on the download");
        var logs = await h.Db.SensitiveAccessLogs.Where(l => l.Purpose == "Bank file" && l.Field == SensitiveFields.BankAccount).ToListAsync();
        logs.Should().HaveCount(2);
    }

    [Fact]
    public async Task Bank_file_needs_all_scope_and_a_vanished_account_refuses_and_csv_cannot_inject()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Ali", 100_000m);
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        await runs.FinalizeAsync(h.Admin, run.Uuid);
        var limited = () => runs.GetBankFileAsync(h.Core.As(50, P.HRM_PAYROLL_PAY, P.HRM_SCOPE_BRANCH), run.Uuid);
        await limited.Should().ThrowAsync<ForbiddenException>();

        var account = await h.Db.EmployeeBankAccounts.FirstAsync(x => x.Employee!.Uuid == e.Uuid);
        account.IsActive = false;
        await h.Db.SaveChangesAsync();
        var missing = () => runs.GetBankFileAsync(h.Admin, run.Uuid);
        await missing.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_BANK_MISSING*");

        PayrollRunService.Csv("=cmd|'/c calc'!A0").Should().StartWith("\"'=");
        PayrollRunService.Csv("a\"b").Should().Be("\"a\"\"b\"");
        PayrollRunService.Csv(null).Should().Be("\"\"");
    }

    // ── payslips ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Payslips_generate_once_with_the_right_figures_and_self_service_sees_only_its_own()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var h = await Ready();
        var e = await h.StaffAsync("Ali", 100_000m, 45_000m);
        var other = await h.StaffAsync("Bano", 60_000m);
        var row = await h.Db.Employees.FirstAsync(x => x.Uuid == e.Uuid);
        row.UserId = 321;
        await h.Db.SaveChangesAsync();
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        var early = () => h.Payslips.GenerateAllAsync(h.Admin, run.Uuid);
        await early.Should().ThrowAsync<ConflictException>().WithMessage("PAYROLL_NOT_FINALIZED*");
        await runs.FinalizeAsync(h.Admin, run.Uuid);

        var result = await h.Payslips.GenerateAllAsync(h.Admin, run.Uuid);
        result.Generated.Should().Be(2);
        (await h.Payslips.GenerateAllAsync(h.Admin, run.Uuid)).Should().BeEquivalentTo(new PayslipBulkResult { Generated = 0, AlreadyGenerated = 2 });
        h.Documents.Files.Should().HaveCount(2);
        h.Documents.Files.Values.Should().OnlyContain(b => b.Length > 500 && b[0] == (byte)'%');

        var line = await h.LineAsync(runs, run.Uuid, e.Uuid);
        var lineId = (await h.Db.PayrollRunLines.FirstAsync(l => l.Uuid == line.Uuid)).Id;
        var doc = await ((PayslipService)h.Payslips).BuildDocumentAsync(h.Org, lineId, default);
        doc.Gross.Should().Be(145_000m);
        doc.Net.Should().Be(line.Net);
        doc.Earnings.Should().Contain(("Basic salary", 100_000m)).And.Contain(("House rent", 45_000m));
        doc.Deductions.Sum(d => d.Amount).Should().Be(line.TotalDeductions);
        doc.YtdNet.Should().Be(line.Net);
        doc.YtdFrom.Should().Be(new DateOnly(2026, 7, 1));

        var self = h.Core.As(321, P.HRM_SELF_SERVICE);
        var mine = await h.Payslips.ListOwnAsync(self, 2026);
        mine.Should().ContainSingle();
        mine[0].Net.Should().Be(line.Net);
        (await h.Payslips.GetPdfAsync(self, mine[0].PayslipUuid)).Content.Should().NotBeEmpty();

        var otherLine = await h.LineAsync(runs, run.Uuid, other.Uuid);
        var otherSlip = await h.Db.Payslips.FirstAsync(p => p.RunLine!.Uuid == otherLine.Uuid);
        var theirs = () => h.Payslips.GetPdfAsync(self, otherSlip.Uuid);
        await theirs.Should().ThrowAsync<NotFoundException>();
        (await h.Payslips.GetForLineAsync(h.Admin, run.Uuid, otherLine.Uuid)).FileName.Should().Contain("Payslip");
    }

    [Fact]
    public async Task A_payslip_row_is_immutable_and_a_reversed_runs_slip_leaves_self_service()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var h = await Ready();
        var e = await h.StaffAsync("Ali", 100_000m);
        var row = await h.Db.Employees.FirstAsync(x => x.Uuid == e.Uuid);
        row.UserId = 321;
        await h.Db.SaveChangesAsync();
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);
        await runs.FinalizeAsync(h.Admin, run.Uuid);
        var self = h.Core.As(321, P.HRM_SELF_SERVICE);
        var slip = (await h.Payslips.ListOwnAsync(self, null)).Single();

        var tracked = await h.Db.Payslips.FirstAsync();
        tracked.FileName = "edited";
        var edit = () => h.Db.SaveChangesAsync();
        await edit.Should().ThrowAsync<InvalidOperationException>();
        h.Db.ChangeTracker.Clear();

        await runs.ReverseAsync(h.Admin, run.Uuid, "Redo");
        (await h.Payslips.ListOwnAsync(self, null)).Should().BeEmpty();
        var read = () => h.Payslips.GetPdfAsync(self, slip.PayslipUuid);
        await read.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task History_carries_the_payslip_once_generated_and_scope_still_holds()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var h = await Ready();
        var e = await h.StaffAsync("Ali", 100_000m);
        var row = await h.Db.Employees.FirstAsync(x => x.Uuid == e.Uuid);
        row.UserId = 321;
        await h.Db.SaveChangesAsync();
        var runs = h.Runs();
        var run = await h.ApprovedRunAsync(runs);

        var before = (await runs.GetEmployeeHistoryAsync(h.Admin, e.Uuid)).Single();
        before.PayslipUuid.Should().BeNull();
        before.LineUuid.Should().Be((await h.LineAsync(runs, run.Uuid, e.Uuid)).Uuid);

        await runs.FinalizeAsync(h.Admin, run.Uuid);
        await h.Payslips.GenerateAllAsync(h.Admin, run.Uuid);
        var item = (await runs.GetEmployeeHistoryAsync(h.Admin, e.Uuid)).Single();
        item.PayslipUuid.Should().NotBeNull();
        (await h.Payslips.GetPdfAsync(h.Admin, item.PayslipUuid!.Value)).Content.Should().NotBeEmpty();
        (await h.Payslips.GetPdfAsync(h.Core.As(321, P.HRM_SELF_SERVICE), item.PayslipUuid.Value)).Content.Should().NotBeEmpty("the employee's own");

        var outOfScope = h.Core.As(77, P.HRM_PAYROLL_VIEW);
        var history = () => runs.GetEmployeeHistoryAsync(outOfScope, e.Uuid);
        await history.Should().ThrowAsync<NotFoundException>();
        var pdf = () => h.Payslips.GetPdfAsync(outOfScope, item.PayslipUuid.Value);
        await pdf.Should().ThrowAsync<NotFoundException>();
        var stranger = () => h.Payslips.GetPdfAsync(h.Core.As(78, P.HRM_SELF_SERVICE), item.PayslipUuid.Value);
        await stranger.Should().ThrowAsync<NotFoundException>();
        var other = new PayHarness(new HrmHarness(dbName: h.Core.DbName));
        var cross = () => other.Payslips.GetPdfAsync(other.Admin, item.PayslipUuid.Value);
        await cross.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task History_lines_and_adjustments_follow_the_hr_scope_and_another_org_sees_nothing()
    {
        var h = await Ready();
        var e = await h.StaffAsync("Ali", 100_000m);
        var runs = h.Runs();
        var run = await h.NewRunAsync(runs);
        await runs.CalculateAsync(h.Admin, run.Uuid);

        var limited = h.Core.As(77, P.HRM_PAYROLL_VIEW);
        (await runs.ListLinesAsync(limited, run.Uuid, null, false, 1, 10)).Data.Should().BeEmpty();
        var history = () => runs.GetEmployeeHistoryAsync(limited, e.Uuid);
        await history.Should().ThrowAsync<NotFoundException>();
        (await runs.GetEmployeeHistoryAsync(h.Admin, e.Uuid)).Should().ContainSingle();
        (await h.Adjustments.ListAsync(limited, null, null)).Should().BeEmpty();

        var other = new PayHarness(new HrmHarness(dbName: h.Core.DbName));
        var otherRuns = other.Runs();
        var cross = () => otherRuns.GetAsync(other.Admin, run.Uuid);
        await cross.Should().ThrowAsync<NotFoundException>();
        var calc = () => otherRuns.CalculateAsync(other.Admin, run.Uuid);
        await calc.Should().ThrowAsync<NotFoundException>();
        (await otherRuns.ListAsync(other.Admin, null, null, null, 1, 25)).Data.Should().BeEmpty();
    }
}
