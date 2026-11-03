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

/// <summary>Tasks 385–394, 398–401.</summary>
public sealed class LifeExpenseTests
{
    private static readonly DateOnly Day = new(2026, 3, 10);

    private static async Task<(LifeHarness l, EmployeeDetail e, int id, ExpenseCategoryModel cat)> WorldAsync(decimal? threshold = null)
    {
        var l = await new LifeHarness().InitAsync();
        var e = await l.H.EmployeeAsync(l.B, "Zainab", "Malik", userId: 7);
        var cat = await l.Categories.CreateAsync(l.H.Admin(), new ExpenseCategoryUpsert { Code = "TAXI", Name = "Taxi", AccountCode = "6310", RequiresReceipt = true, ReceiptThreshold = threshold ?? 1000m });
        return (l, e, await l.IdOf(e.Uuid), cat);
    }

    private static async Task<ExpenseClaimModel> ClaimAsync(LifeHarness l, ExpenseCategoryModel cat, string settlement, string mode = "OwnAccount", decimal amount = 500m, decimal tax = 0m, string? taxCode = null)
    {
        var claim = await l.Claims.CreateAsync(l.H.As(7, P.HRM_SELF_SERVICE), new ExpenseClaimUpsert
        {
            SettlementMethod = settlement, PaymentMode = mode, ClaimDate = Day,
            Lines = [new ExpenseLineUpsert { CategoryUuid = cat.Uuid, ExpenseDate = Day, Amount = amount, TaxAmount = tax, TaxCode = taxCode, Description = "Zainab Malik taxi" }]
        });
        l.CurrentClaim = claim.Uuid;
        return claim;
    }

    private static async Task<ExpenseClaimModel> ApprovedAsync(LifeHarness l, ExpenseCategoryModel cat, string settlement, string mode = "OwnAccount", decimal amount = 500m, decimal tax = 0m, string? taxCode = null)
    {
        var claim = await ClaimAsync(l, cat, settlement, mode, amount, tax, taxCode);
        await l.Claims.SubmitAsync(l.H.As(7, P.HRM_SELF_SERVICE), claim.Uuid);
        return await l.Claims.ApproveAsync(l.H.Admin(), claim.Uuid);
    }

    [Fact]
    public async Task Receipts_are_enforced_above_the_threshold_only()
    {
        var (l, _, _, cat) = await WorldAsync(1000m);
        var me = l.H.As(7, P.HRM_SELF_SERVICE);
        var small = await ClaimAsync(l, cat, "SeparatePayment", amount: 1000m);
        (await l.Claims.SubmitAsync(me, small.Uuid)).Status.Should().Be("Submitted");
        var big = await ClaimAsync(l, cat, "SeparatePayment", amount: 1000.01m);
        (await FluentActions.Awaiting(() => l.Claims.SubmitAsync(me, big.Uuid)).Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("EXPENSE_RECEIPT_REQUIRED");
    }

    [Fact]
    public async Task An_empty_claim_cannot_be_submitted_and_no_approver_returns_it_to_draft()
    {
        var (l, _, _, cat) = await WorldAsync();
        var me = l.H.As(7, P.HRM_SELF_SERVICE);
        var empty = await l.Claims.CreateAsync(me, new ExpenseClaimUpsert());
        (await FluentActions.Awaiting(() => l.Claims.SubmitAsync(me, empty.Uuid)).Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("EXPENSE_NO_LINES");

        var claim = await ClaimAsync(l, cat, "SeparatePayment");
        l.Approvals.NoApprover = true;
        await FluentActions.Awaiting(() => l.Claims.SubmitAsync(me, claim.Uuid)).Should().ThrowAsync<ConflictException>();
        (await l.Claims.GetAsync(me, claim.Uuid)).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task A_rate_based_category_computes_the_amount()
    {
        var (l, _, _, _) = await WorldAsync();
        var km = await l.Categories.CreateAsync(l.H.Admin(), new ExpenseCategoryUpsert { Code = "MILE", Name = "Mileage", AccountCode = "6311", RatePerUnit = 12.5m, UnitName = "km" });
        var claim = await l.Claims.CreateAsync(l.H.As(7, P.HRM_SELF_SERVICE), new ExpenseClaimUpsert { Lines = [new ExpenseLineUpsert { CategoryUuid = km.Uuid, ExpenseDate = Day, Quantity = 10m, Amount = 99999m }] });
        claim.TotalAmount.Should().Be(125m);
    }

    [Fact]
    public async Task TS_G9_001_approval_posts_Dr_expense_Cr_payable_balanced_and_private()
    {
        var (l, e, _, cat) = await WorldAsync();
        var done = await ApprovedAsync(l, cat, "SeparatePayment", amount: 500m, tax: 50m, taxCode: "GST");
        done.Status.Should().Be("Approved");
        done.PostingState.Should().Be("Posted");
        done.DecidedByName.Should().Be("HR Admin");

        var tx = l.Gateway.Posted.Single();
        tx.DocumentType.Should().Be(FinDocType.Expense);
        tx.SourceModule.Should().Be("hrm");
        tx.Lines.Sum(x => x.Debit).Should().Be(tx.Lines.Sum(x => x.Credit)).And.Be(500m);
        var expense = tx.Lines.Single(x => x.AccountCode == "6310");
        expense.Debit.Should().Be(450m);
        expense.CostCenter.Should().Be("OPS", "the employee's department's cost center");
        tx.Lines.Single(x => x.AccountRole == "DEFAULT_TAX_RECEIVABLE").Debit.Should().Be(50m);
        tx.Lines.Single(x => x.Credit > 0).AccountRole.Should().Be("DEFAULT_EXPENSE_CLAIM_PAYABLE");
        LifeHarness.Json(tx).Should().NotContain("Zainab").And.NotContain("Malik").And.NotContain(e.EmployeeNumber);
    }

    [Fact]
    public async Task An_unmapped_category_stops_the_route_but_an_inbox_approval_stands_as_failed_and_retries()
    {
        var (l, _, _, _) = await WorldAsync();
        var cat = await l.Categories.CreateAsync(l.H.Admin(), new ExpenseCategoryUpsert { Code = "RAW", Name = "Unmapped" });
        var claim = await ClaimAsync(l, cat, "SeparatePayment");
        await l.Claims.SubmitAsync(l.H.As(7, P.HRM_SELF_SERVICE), claim.Uuid);
        (await FluentActions.Awaiting(() => l.Claims.ApproveAsync(l.H.Admin(), claim.Uuid)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("EXPENSE_CATEGORY_UNMAPPED");

        await l.Decisions.UpdateStatusAsync(claim.Uuid, "APPROVED"); // the workflow inbox
        var failed = await l.Claims.GetAsync(l.H.Admin(), claim.Uuid);
        failed.Status.Should().Be("Approved");
        failed.PostingState.Should().Be("Failed");
        l.Gateway.Posted.Should().BeEmpty();
        (await FluentActions.Awaiting(() => l.Claims.PayAsync(l.H.Admin(), claim.Uuid, new LifePayRequest { PaidOn = Day })).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("EXPENSE_NOT_PAYABLE");

        await l.Categories.UpdateAsync(l.H.Admin(), cat.Uuid, new ExpenseCategoryUpsert { Code = "RAW", Name = "Unmapped", AccountRole = "DEFAULT_SALARY_EXPENSE" });
        l.H.Db.ChangeTracker.Clear();
        (await l.Claims.RetryPostingAsync(l.H.Admin(), claim.Uuid)).PostingState.Should().Be("Posted");
    }

    [Theory]
    [InlineData(PostingStatus.NoTargetConfigured)]
    [InlineData(PostingStatus.ModuleNotLicensed)]
    public async Task D11_no_target_approval_and_payment_still_finish(PostingStatus status)
    {
        var (l, _, _, cat) = await WorldAsync();
        l.Gateway.Next = status;
        var done = await ApprovedAsync(l, cat, "SeparatePayment");
        done.PostingState.Should().Be("NoTarget");
        var paid = await l.Claims.PayAsync(l.H.Admin(), done.Uuid, new LifePayRequest { PaidOn = Day });
        paid.Status.Should().Be("Paid");
        paid.PaymentPostingState.Should().Be("NoTarget");
    }

    [Fact]
    public async Task TS_G9_002_separate_payment_posts_Dr_payable_Cr_bank_and_cannot_be_paid_twice()
    {
        var (l, _, _, cat) = await WorldAsync();
        var done = await ApprovedAsync(l, cat, "SeparatePayment");
        l.Gateway.Posted.Clear();
        var pay = new LifePayRequest { PaidOn = Day, PaidFromAccount = "Bank", Reference = "T1" };
        var paid = await l.Claims.PayAsync(l.H.Admin(), done.Uuid, pay);
        paid.Status.Should().Be("Paid");
        paid.PaymentPostingState.Should().Be("Posted");
        var tx = l.Gateway.Posted.Single();
        tx.DocumentType.Should().Be(FinDocType.ExpensePayment);
        tx.Lines.Single(x => x.Debit > 0).AccountRole.Should().Be("DEFAULT_EXPENSE_CLAIM_PAYABLE");
        tx.Lines.Single(x => x.Credit > 0).AccountRole.Should().Be("DEFAULT_BANK");
        (await FluentActions.Awaiting(() => l.Claims.PayAsync(l.H.Admin(), done.Uuid, pay)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("EXPENSE_NOT_PAYABLE");
        l.Gateway.Posted.Should().ContainSingle("no second payment posting");
    }

    [Fact]
    public async Task TS_G9_003_with_payroll_goes_on_the_run_once_and_the_run_marks_it_paid()
    {
        var (l, e, id, cat) = await WorldAsync();
        var done = await ApprovedAsync(l, cat, "WithPayroll");
        (await FluentActions.Awaiting(() => l.Claims.PayAsync(l.H.Admin(), done.Uuid, new LifePayRequest { PaidOn = Day })).Should().ThrowAsync<ConflictException>("settled through payroll")).Which.Message.Should().StartWith("EXPENSE_NOT_PAYABLE");

        var adjustment = await l.H.Db.PayrollAdjustments.IgnoreQueryFilters().SingleAsync();
        adjustment.ComponentCode.Should().Be("EXPREIMB");
        adjustment.Amount.Should().Be(500m);
        (await l.H.Db.SalaryComponents.IgnoreQueryFilters().SingleAsync(c => c.OrganizationId == l.H.Org && c.Code == "EXPREIMB")).PostingRole.Should().Be("DEFAULT_EXPENSE_CLAIM_PAYABLE");

        var a = await l.RunAsync("A");
        var b = await l.RunAsync("B");
        var (from, to) = (new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        var inputs = await l.ExpenseProvider.GetInputsAsync(l.H.Org, a.Id, id, from, to);
        inputs.Single().ComponentCode.Should().Be("EXPREIMB");
        inputs.Single().Amount.Should().Be(500m);
        (await l.ExpenseProvider.GetInputsAsync(l.H.Org, a.Id, id, from, to)).Should().ContainSingle("recalculation is safe");
        (await l.ExpenseProvider.GetInputsAsync(l.H.Org, b.Id, id, from, to)).Should().BeEmpty("no double pay");

        await l.ExpenseProvider.MarkConsumedAsync(l.H.Org, a.Id, id);
        var paid = await l.Claims.GetAsync(l.H.Admin(), done.Uuid);
        paid.Status.Should().Be("Paid");
        paid.PaidInRunNumber.Should().Be("A");
        paid.PaidOn.Should().Be(a.PayDate);
        (await l.ExpenseProvider.GetInputsAsync(l.H.Org, b.Id, id, from, to)).Should().BeEmpty("already paid");

        await l.ExpenseProvider.ReleaseAsync(l.H.Org, a.Id);
        (await l.Claims.GetAsync(l.H.Admin(), done.Uuid)).Status.Should().Be("Approved");
        (await l.ExpenseProvider.GetInputsAsync(l.H.Org, b.Id, id, from, to)).Should().ContainSingle("a reversed run gives it back");
    }

    [Fact]
    public async Task Company_account_credits_the_bank_and_is_paid_with_nothing_to_reimburse()
    {
        var (l, _, _, cat) = await WorldAsync();
        var done = await ApprovedAsync(l, cat, "WithPayroll", "CompanyAccount");
        done.Status.Should().Be("Paid");
        l.Gateway.Posted.Single().Lines.Single(x => x.Credit > 0).AccountRole.Should().Be("DEFAULT_BANK");
        (await l.H.Db.PayrollAdjustments.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Rejection_and_a_repeated_engine_callback_change_nothing_more()
    {
        var (l, _, _, cat) = await WorldAsync();
        var claim = await ClaimAsync(l, cat, "SeparatePayment");
        await l.Claims.SubmitAsync(l.H.As(7, P.HRM_SELF_SERVICE), claim.Uuid);
        var rejected = await l.Claims.RejectAsync(l.H.Admin(), claim.Uuid, "No trip");
        rejected.Status.Should().Be("Rejected");
        rejected.DecisionNote.Should().Be("No trip");
        await l.Decisions.UpdateStatusAsync(claim.Uuid, "APPROVED");
        (await l.Claims.GetAsync(l.H.Admin(), claim.Uuid)).Status.Should().Be("Rejected");
        l.Gateway.Posted.Should().BeEmpty();
    }

    [Fact]
    public async Task Owners_see_their_own_others_need_a_role_and_scope_and_nobody_approves_their_own()
    {
        var (l, _, _, cat) = await WorldAsync();
        var claim = await ClaimAsync(l, cat, "SeparatePayment");
        await l.Claims.GetAsync(l.H.As(7, P.HRM_SELF_SERVICE), claim.Uuid);
        await FluentActions.Awaiting(() => l.Claims.GetAsync(l.H.As(8, P.HRM_SELF_SERVICE), claim.Uuid)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => l.Claims.GetAsync(l.H.As(8, P.HRM_EXPENSE_VIEW), claim.Uuid)).Should().ThrowAsync<NotFoundException>("no scope over this employee");
        await l.Claims.SubmitAsync(l.H.As(7, P.HRM_SELF_SERVICE), claim.Uuid);
        await FluentActions.Awaiting(() => l.Claims.ApproveAsync(l.H.As(7, P.HRM_SELF_SERVICE, P.HRM_EXPENSE_APPROVE, P.HRM_SCOPE_ALL), claim.Uuid)).Should().ThrowAsync<ForbiddenException>();
    }

    [Theory]
    [InlineData("IncomeTax", "DEFAULT_EMPLOYEE_TAX_PAYABLE", "ITAX")]
    [InlineData("EOBI", "DEFAULT_EOBI_PAYABLE", "EOBI_EE")]
    [InlineData("PF", "DEFAULT_PF_PAYABLE", "PF_EE")]
    [InlineData("SS", "DEFAULT_SOCIAL_SECURITY_PAYABLE", "SS_ER")]
    public async Task TS_G13_001_remittance_posts_Dr_payable_Cr_bank_with_no_partner_and_tracks_the_balance(string type, string role, string component)
    {
        var (l, _, id, _) = await WorldAsync();
        var run = await l.RunAsync("R", PayrollRunStatuses.Finalized);
        var line = new PayrollRunLine { OrganizationId = l.H.Org, RunId = run.Id, EmployeeId = id };
        l.H.Db.PayrollRunLines.Add(line);
        await l.H.Db.SaveChangesAsync();
        l.H.Db.PayrollRunLineComponents.Add(new PayrollRunLineComponent { OrganizationId = l.H.Org, LineId = line.Id, Code = component, Name = component, Amount = 1000m });
        await l.H.Db.SaveChangesAsync();
        var admin = l.H.Admin();
        var req = new RemittanceCreate { RemittanceType = type, PeriodStart = run.PeriodStart, PeriodEnd = run.PeriodEnd, Amount = 600m, PaidOn = Day, Reference = "CHQ1", AuthorityName = "Authority" };

        var row = await l.Remittances.CreateAsync(admin, req);
        row.PostingState.Should().Be("Posted");
        var tx = l.Gateway.Posted.Single();
        tx.DocumentType.Should().Be(FinDocType.StatutoryRemittance);
        tx.PartnerId.Should().BeNull();
        tx.Lines.Single(x => x.Debit > 0).AccountRole.Should().Be(role);
        tx.Lines.Single(x => x.Debit > 0).Debit.Should().Be(600m);
        tx.Lines.Single(x => x.Credit > 0).AccountRole.Should().Be("DEFAULT_BANK");
        LifeHarness.Json(tx).Should().NotContain("Authority");

        var track = (await l.Remittances.TrackerAsync(admin, 2026, type)).Single();
        (track.Calculated, track.Paid, track.Balance).Should().Be((1000m, 600m, 400m));

        (await FluentActions.Awaiting(() => l.Remittances.CreateAsync(admin, req)).Should().ThrowAsync<ConflictException>()).Which.Message.Should().StartWith("REMITTANCE_DUPLICATE");
        req.Reference = "CHQ2";
        (await FluentActions.Awaiting(() => l.Remittances.CreateAsync(admin, req)).Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("REMITTANCE_OVERPAID");
        req.Force = true;
        await l.Remittances.CreateAsync(admin, req);
    }
}

/// <summary>Tasks 374, 376, 379.</summary>
public sealed class LifeLetterTests
{
    private static async Task<(LifeHarness l, EmployeeDetail e, int id)> WorldAsync()
    {
        var l = await new LifeHarness().InitAsync();
        var e = await l.H.EmployeeAsync(l.B, "Zainab", "Malik", userId: 7);
        return (l, e, await l.IdOf(e.Uuid));
    }

    private static async Task<Guid> TemplateAsync(LifeHarness l, string code) =>
        await l.H.Db.LetterTemplates.IgnoreQueryFilters().Where(t => t.OrganizationId == l.H.Org && t.Code == code).Select(t => t.Uuid).FirstAsync();

    [Fact]
    public void Merge_replaces_every_placeholder()
    {
        var text = LetterService.Merge("{EmployeeName} at {OrganizationName}, {EmployeeName}", new Dictionary<string, string> { ["EmployeeName"] = "Ali", ["OrganizationName"] = "Acme" });
        text.Should().Be("Ali at Acme, Ali");
    }

    [Fact]
    public async Task An_unknown_placeholder_is_refused_on_create_and_update()
    {
        var (l, _, _) = await WorldAsync();
        var bad = new LetterTemplateUpsert { Code = "X", Name = "X", Subject = "S", Body = "Hello {Nickname}" };
        (await FluentActions.Awaiting(() => l.Letters.CreateTemplateAsync(l.H.Admin(), bad)).Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("LETTER_PLACEHOLDER_UNKNOWN");
        var ok = await l.Letters.CreateTemplateAsync(l.H.Admin(), new LetterTemplateUpsert { Code = "X", Name = "X", Subject = "S", Body = "Hello {EmployeeName}" });
        await FluentActions.Awaiting(() => l.Letters.UpdateTemplateAsync(l.H.Admin(), ok.Uuid, bad)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Issuing_asks_the_organization_lookup_for_this_org_and_stores_a_real_pdf()
    {
        var (l, e, _) = await WorldAsync();
        var row = await l.Letters.IssueAsync(l.H.Admin(), new LetterIssueRequest { EmployeeUuid = e.Uuid, TemplateUuid = await TemplateAsync(l, LetterTemplateCodes.ExperienceCertificate) });
        l.OrgName.Asked.Should().Contain(l.H.Org);
        row.LetterNumber.Should().StartWith("LTR-");
        row.IssuedByName.Should().Be("HR Admin");
        var pdf = await l.Letters.GetPdfAsync(l.H.Admin(), row.Uuid);
        System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public async Task With_no_organization_name_a_template_using_it_needs_the_fallback()
    {
        var (l, e, _) = await WorldAsync();
        l.OrgName.Name = null;
        var template = await TemplateAsync(l, LetterTemplateCodes.ExperienceCertificate);
        (await FluentActions.Awaiting(() => l.Letters.IssueAsync(l.H.Admin(), new LetterIssueRequest { EmployeeUuid = e.Uuid, TemplateUuid = template })).Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("LETTER_ORGANIZATION_REQUIRED");
        await l.Letters.IssueAsync(l.H.Admin(), new LetterIssueRequest { EmployeeUuid = e.Uuid, TemplateUuid = template, OrganizationName = "Typed Name" });
    }

    [Fact]
    public async Task Salary_needs_the_salary_permission()
    {
        var (l, e, id) = await WorldAsync();
        await l.SalaryAsync(id);
        var template = await TemplateAsync(l, LetterTemplateCodes.SalaryCertificate);
        var req = new LetterIssueRequest { EmployeeUuid = e.Uuid, TemplateUuid = template };
        await FluentActions.Awaiting(() => l.Letters.IssueAsync(l.H.As(50, P.HRM_LETTER_ISSUE, P.HRM_SCOPE_ALL), req)).Should().ThrowAsync<ForbiddenException>();
        await l.Letters.IssueAsync(l.H.As(50, P.HRM_LETTER_ISSUE, P.HRM_SCOPE_ALL, P.HRM_SALARY_VIEW), req);
    }

    [Fact]
    public async Task An_employee_reads_their_own_letter_nobody_else_without_the_role()
    {
        var (l, e, _) = await WorldAsync();
        var row = await l.Letters.IssueAsync(l.H.Admin(), new LetterIssueRequest { EmployeeUuid = e.Uuid, TemplateUuid = await TemplateAsync(l, LetterTemplateCodes.ExperienceCertificate) });
        (await l.Letters.MineAsync(l.H.As(7, P.HRM_SELF_SERVICE))).Should().ContainSingle();
        await l.Letters.GetPdfAsync(l.H.As(7, P.HRM_SELF_SERVICE), row.Uuid);
        await FluentActions.Awaiting(() => l.Letters.GetPdfAsync(l.H.As(8, P.HRM_SELF_SERVICE), row.Uuid)).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_tax_certificate_totals_finalized_pay_says_unverified_and_is_idempotent()
    {
        var (l, e, id) = await WorldAsync();
        var run = await l.RunAsync("R", PayrollRunStatuses.Finalized, 2026, 8);
        var draft = await l.RunAsync("D", PayrollRunStatuses.Calculated, 2026, 9);
        foreach (var r in new[] { run, draft })
        {
            var line = new PayrollRunLine { OrganizationId = l.H.Org, RunId = r.Id, EmployeeId = id };
            l.H.Db.PayrollRunLines.Add(line);
            await l.H.Db.SaveChangesAsync();
            l.H.Db.PayrollRunLineComponents.AddRange(
                new PayrollRunLineComponent { OrganizationId = l.H.Org, LineId = line.Id, Code = "BASIC", Name = "Basic", Amount = 100000m, IsTaxable = true },
                new PayrollRunLineComponent { OrganizationId = l.H.Org, LineId = line.Id, Code = "ITAX", Name = "Tax", Amount = 5000m });
            await l.H.Db.SaveChangesAsync();
        }
        var req = new TaxCertificateGenerate { EmployeeUuid = e.Uuid, TaxYear = 2026 };
        var first = (await l.Tax.GenerateAsync(l.H.Admin(), req)).Single();
        first.TotalIncome.Should().Be(100000m, "the draft run does not count");
        first.TotalTax.Should().Be(5000m);
        first.IsUnverified.Should().BeTrue("the seeded rates are unverified");
        var files = l.Store.Files.Count;
        var again = (await l.Tax.GenerateAsync(l.H.Admin(), req)).Single();
        again.Uuid.Should().Be(first.Uuid);
        l.Store.Files.Should().HaveCount(files, "the same figures keep the same PDF");
        System.Text.Encoding.ASCII.GetString((await l.Tax.GetPdfAsync(l.H.Admin(), first.Uuid)).Content, 0, 5).Should().Be("%PDF-");
        (await l.Tax.MineAsync(l.H.As(7, P.HRM_SELF_SERVICE))).Should().ContainSingle();
        await FluentActions.Awaiting(() => l.Tax.GetPdfAsync(l.H.As(8, P.HRM_SELF_SERVICE), first.Uuid)).Should().ThrowAsync<NotFoundException>();
    }
}

