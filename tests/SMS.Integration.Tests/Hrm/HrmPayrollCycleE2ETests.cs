using System.Net;
using System.Text;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.JournalEngine;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 QA, task 307 and the payroll side of TS-G3, G10, G12, G19, G21: real host, real internal GL, real gateway, real permissions.
/// Figures by hand: gross 120,000 = basic 100,000 + rent 20,000. Tax: 1,440,000 annual → 6,000 + 11% of 240,000 = 32,400 / 12 = 2,700.
/// EOBI 1% and 5% of 37,000 = 370 and 1,850. PF 8.33% of 100,000 = 8,330 twice. Net 120,000 - 2,700 - 370 - 8,330 = 108,600.
/// Debit 120,000 salary + 10,180 employer = 130,180 = credit 2,700 tax + 2,220 EOBI + 16,660 PF + 108,600 net.
/// Run alone: dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~HrmPayrollCycleE2ETests
/// </summary>
public sealed class HrmPayrollCycleE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public HrmPayrollCycleE2ETests(SapWebApplicationFactory f)
    {
        _f = f;
        f.ShouldBeOnLocalDb();
        _root = new SapKit(f, "A49Q");
    }

    private const string Runs = "/api/hrm/payroll-runs";

    [Fact]
    public async Task T307_create_calculate_approve_finalize_post_pay_with_real_journals_and_hand_computed_figures()
    {
        var o = await _root.ReadyOrgAsync("CYC");
        var k = o.K;
        var staffLogin = await o.UserAsync("cycstaff", "HRM_SELF_SERVICE");
        var otherLogin = await o.UserAsync("cycother", "HRM_SELF_SERVICE");
        var ali = await o.PaidStaffAsync("Ali", 100_000m, 20_000m, staffLogin);
        var other = await o.PaidStaffAsync("Zubair", 100_000m, 20_000m, otherLogin);

        // Real permissions: each step by the role that owns it, and refused to the rest.
        var runner = await o.UserAsync("cycrun", "HRM_PAYROLL_RUN", "HRM_PAYROLL_VIEW", "HRM_SCOPE_ALL");
        var approver = await o.UserAsync("cycapp", "HRM_PAYROLL_APPROVE", "HRM_PAYROLL_VIEW", "HRM_SCOPE_ALL");
        var finalizer = await o.UserAsync("cycfin", "HRM_PAYROLL_FINALIZE", "HRM_PAYROLL_VIEW", "HRM_SCOPE_ALL");
        var payer = await o.UserAsync("cycpay", "HRM_PAYROLL_PAY", "HRM_PAYROLL_VIEW", "HRM_SCOPE_ALL");

        (await k.Post(Runs, new { periodStart = "2026-10-01", periodEnd = "2026-10-31", payDate = "2026-10-31" }, staffLogin)).ShouldBeForbidden("self service runs no payroll");
        var run = (await k.Ok(k.Post(Runs, new { periodStart = "2026-10-01", periodEnd = "2026-10-31", payDate = "2026-10-31" }, runner), "create")).G("uuid");

        var calc = await k.Ok(k.Post($"{Runs}/{run}/calculate", null, runner), "calculate");
        calc.S("status").Should().Be("Calculated");
        calc.I("totalEmployees").Should().Be(2);
        calc.D("totalGross").Should().Be(240_000m);
        calc.D("totalNet").Should().Be(217_200m);
        calc.D("totalEmployerCost").Should().Be(20_360m);

        var line = (await o.LinesAsync(run)).Single(l => l.G("employeeUuid") == ali);
        (line.Amount("ITAX"), line.Amount("EOBI_EE"), line.Amount("EOBI_ER"), line.Amount("PF_EE"), line.Amount("PF_ER"))
            .Should().Be((2_700m, 370m, 1_850m, 8_330m, 8_330m), line.GetRawText());
        line.D("net").Should().Be(108_600m);

        (await k.Post($"{Runs}/{run}/finalize", null, finalizer)).Status.Should().Be(HttpStatusCode.Conflict, "not approved yet");
        (await k.Post($"{Runs}/{run}/review", null, runner)).ShouldBeForbidden("the runner does not approve");
        await k.Ok(k.Post($"{Runs}/{run}/review", null, approver), "review");
        await k.Ok(k.Post($"{Runs}/{run}/approve", new { }, approver), "approve");

        var readiness = await k.Ok(k.Get($"{Runs}/{run}/posting-readiness", finalizer), "readiness");
        readiness.B("internalGlActive").Should().BeTrue(readiness.GetRawText());
        readiness.B("isReady").Should().BeTrue(readiness.GetRawText());

        var final = await k.Ok(k.Post($"{Runs}/{run}/finalize", null, finalizer), "finalize");
        final.S("status").Should().Be("Posted", final.GetRawText());

        // The journal: real, posted, balanced, on the right accounts, source hrm.
        var journals = await _f.JournalsAsync(o.OrgId, run);
        var je = journals.Should().ContainSingle(journals.Count.ToString()).Subject;
        je.Balanced.Should().BeTrue(je.ToString());
        je.Status.Should().Be("Posted");
        je.Debit.Should().Be(260_360m);
        je.Net(HrQa.CodeOf("DEFAULT_SALARY_EXPENSE")).Should().Be(240_000m, je.ToString());
        je.Net(HrQa.CodeOf("DEFAULT_EMPLOYER_CONTRIB_EXPENSE")).Should().Be(20_360m, je.ToString());
        je.Net(HrQa.CodeOf("DEFAULT_EMPLOYEE_TAX_PAYABLE")).Should().Be(-5_400m);
        je.Net(HrQa.CodeOf("DEFAULT_EOBI_PAYABLE")).Should().Be(-4_440m);
        je.Net(HrQa.CodeOf("DEFAULT_PF_PAYABLE")).Should().Be(-33_320m);
        je.Net(HrQa.CodeOf("DEFAULT_SALARY_PAYABLE")).Should().Be(-217_200m);

        // TS-G2-003 and G-11 at the gateway's own record.
        var postings = await _f.QueryAsync("SELECT * FROM finance.FinancialPostings WHERE OrganizationId = @o", ("@o", o.OrgId));
        var row = postings.Single(p => (string?)p["DocumentType"] == "PayrollRun");
        row["SourceModule"].Should().Be("hrm");
        var stored = new List<Dictionary<string, object?>>(postings);
        stored.AddRange(await _f.QueryAsync("SELECT * FROM finance.JournalEntries WHERE Id = @i", ("@i", je.Id)));
        stored.AddRange(await _f.QueryAsync("SELECT * FROM finance.JournalEntryLines WHERE JournalEntryId = @i", ("@i", je.Id)));
        stored.SelectMany(r => r.Values).Select(v => v?.ToString())
            .Should().NotContain(v => v != null && (v.Contains("Ali ") || v.Contains("Zubair") || v.Contains("EMP-0")), "G-11");

        // Retry never doubles.
        (await k.Post($"{Runs}/{run}/retry-posting", null, finalizer)).Status.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
        (await _f.JournalsAsync(o.OrgId, run)).Should().HaveCount(1, "the retry reuses the key");

        // Journal, exports, reconciliation.
        (await k.Ok(k.Get($"{Runs}/{run}/journal", finalizer), "journal")).GetRawText().Should().NotContain("Ali");
        var csv = await k.DownloadAsync($"{Runs}/{run}/journal-export/csv", finalizer);
        csv.Status.Should().Be(HttpStatusCode.OK);
        Encoding.UTF8.GetString(csv.Bytes).Should().Contain("DEFAULT_SALARY_EXPENSE");
        var xlsx = await k.DownloadAsync($"{Runs}/{run}/journal-export/xlsx", finalizer);
        (xlsx.Status, xlsx.Bytes[0], xlsx.Bytes[1]).Should().Be((HttpStatusCode.OK, (byte)'P', (byte)'K'));
        var recon = await k.Ok(k.Get($"{Runs}/{run}/reconciliation", finalizer), "reconciliation");
        recon.ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Undefined);

        // Bank file: S3, one access log per employee.
        var bank = await k.DownloadAsync($"{Runs}/{run}/bank-file", payer);
        bank.Status.Should().Be(HttpStatusCode.OK);
        Encoding.UTF8.GetString(bank.Bytes).Should().Contain("00112233445566");
        var logs = await _f.QueryAsync("SELECT COUNT(*) AS N FROM hrm.SensitiveAccessLogs WHERE OrganizationId = @o AND Purpose = 'Bank file'", ("@o", o.OrgId));
        Convert.ToInt32(logs[0]["N"]).Should().Be(2);
        (await k.Get($"{Runs}/{run}/bank-file", finalizer)).ShouldBeForbidden("no pay code");

        // Pay: Dr payable / Cr bank.
        (await k.Post($"{Runs}/{run}/pay", new { paidOn = "2026-10-31", reference = "BATCH-1" }, finalizer)).ShouldBeForbidden("no pay code");
        var paid = await k.Ok(k.Post($"{Runs}/{run}/pay", new { paidOn = "2026-10-31", reference = "BATCH-1", paidFromAccount = "Bank" }, payer), "pay");
        paid.S("status").Should().Be("Paid", paid.GetRawText());
        var all = await _f.JournalsAsync(o.OrgId, run);
        var codes = await k.DefaultCodesAsync();
        var pay = all.Single(j => j.Id != je.Id);
        pay.Balanced.Should().BeTrue(pay.ToString());
        pay.Net(HrQa.CodeOf("DEFAULT_SALARY_PAYABLE")).Should().Be(217_200m, pay.ToString());
        pay.Net(codes["DEFAULT_BANK"]).Should().Be(-217_200m, pay.ToString());
        (await k.Post($"{Runs}/{run}/pay", new { paidOn = "2026-10-31", reference = "BATCH-2" }, payer)).Status.Should().Be(HttpStatusCode.Conflict, "never paid twice");
        (await k.Post($"{Runs}/{run}/reverse", new { reason = "too late" })).Status.Should().Be(HttpStatusCode.Conflict, "PAYROLL_ALREADY_PAID");

        // Trial balance is balanced and shows it.
        var tb = await k.TrialBalanceAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        tb.B("isBalanced").Should().BeTrue(tb.GetRawText());

        // Payslips through their own routes, generated once.
        await k.Ok(k.Post($"{Runs}/{run}/payslips", null, runner), "generate payslips");
        var mine = (await k.Ok(k.Get("/api/hrm/payslips/me?year=2026", staffLogin), "my payslips")).Items();
        mine.Should().ContainSingle();
        var slip = mine[0].G("payslipUuid");
        mine[0].D("net").Should().Be(108_600m);
        var pdf = await k.DownloadAsync($"/api/hrm/payslips/{slip}/pdf", staffLogin);
        pdf.Status.Should().Be(HttpStatusCode.OK);
        pdf.ContentType.Should().Be("application/pdf");
        var text = HrQa.PdfText(pdf.Bytes);
        text.Should().Contain("Ali");
        (await k.DownloadAsync($"/api/hrm/payslips/{slip}/pdf", otherLogin)).Status.Should().Be(HttpStatusCode.NotFound, "another employee's payslip");
        var lineUuid = (await o.LinesAsync(run)).Single(l => l.G("employeeUuid") == other).G("uuid");
        (await k.DownloadAsync($"{Runs}/{run}/lines/{lineUuid}/payslip", finalizer)).Status.Should().Be(HttpStatusCode.OK);
        (await k.DownloadAsync($"{Runs}/{run}/lines/{lineUuid}/payslip", staffLogin)).Status.Should().Be(HttpStatusCode.Forbidden);

        // The attachment route refuses a payslip outright.
        var attach = (await _f.QueryAsync("SELECT TOP 1 AttachmentUuid FROM hrm.Payslips WHERE OrganizationId = @o", ("@o", o.OrgId))).Single()["AttachmentUuid"];
        (await k.Get($"/api/attachments/{attach}/content")).Status.Should().Be(HttpStatusCode.Forbidden);

        // History.
        var history = (await k.Ok(k.Get($"/api/hrm/employees/{ali}/payroll-history", finalizer), "history")).Items();
        history.Should().ContainSingle().Which.NG("payslipUuid").Should().NotBeNull();
    }

    [Fact]
    public async Task G21_seeded_statutory_data_is_unverified_named_and_blocks_finalize_until_marked()
    {
        var o = await _root.ReadyOrgAsync("G21", verifyStatutory: false);
        var k = o.K;
        var seeded = (await k.Ok(k.Get("/api/hrm/statutory-rates"), "rates")).Items();
        seeded.Should().OnlyContain(r => !r.B("isVerified") && r.S("name")!.Contains("UNVERIFIED"));
        var tables = (await k.Ok(k.Get("/api/hrm/tax-tables"), "tables")).Items();
        tables.Single(t => t.S("code") == "PK-SALARIED-2026").S("name").Should().Contain("UNVERIFIED");
        (await k.Ok(k.Get("/api/hrm/holidays?year=2026"), "holidays")).Items().Where(h => h.S("name")!.Contains("estimated")).Should().OnlyContain(h => h.B("isEstimated"));

        await o.PaidStaffAsync("Gul", 100_000m);
        var run = await o.ApprovedRunAsync();

        var blocked = await k.Post($"{Runs}/{run}/finalize");
        blocked.Status.Should().Be(HttpStatusCode.Conflict, blocked.ToString());
        blocked.ToString().Should().Contain("PAYROLL_STATUTORY_UNVERIFIED").And.Contain("UNVERIFIED");
        (await _f.JournalsAsync(o.OrgId, run)).Should().BeEmpty();

        // Only the verify code flips it.
        var noVerify = await o.UserAsync("g21", "HRM_PAYROLL_CONFIG");
        var rate = seeded[0].G("uuid");
        (await k.Post($"/api/hrm/statutory-rates/{rate}/verify", new { note = "x" }, noVerify)).ShouldBeForbidden("needs HRM_STATUTORY_VERIFY");

        await o.VerifyStatutoryAsync();
        (await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize")).S("status").Should().Be("Posted");
    }

    [Fact]
    public async Task G21_the_seeded_tax_table_covers_the_current_period()
    {
        // No QA table here: what a fresh organization has when it runs payroll today.
        var fp = await _root.NewOrgAsync("G21B");
        var k = fp.K;
        await _root.EnableHrmAsync(fp.O, HrQa.AllFeatures);
        var tables = (await k.Ok(k.Get("/api/hrm/tax-tables"), "tables")).Items();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        tables.Should().Contain(t => DateOnly.Parse(t.S("effectiveFrom")!) <= today && (t.S("effectiveTo") == null || DateOnly.Parse(t.S("effectiveTo")!) >= today),
            "a fresh organization's first payroll this month must not be blocked by 'No tax table is in force': " + string.Join(", ", tables.Select(t => $"{t.S("code")} {t.S("effectiveFrom")}..{t.S("effectiveTo")}")));
    }

    [Fact]
    public async Task TS_G3_001_an_unmapped_role_blocks_finalize_naming_it_and_mapping_it_lets_it_through()
    {
        var o = await _root.ReadyOrgAsync("G3", mapRoles: false);
        var k = o.K;
        await o.MapHrRolesAsync("DEFAULT_SALARY_EXPENSE");
        await o.PaidStaffAsync("Hina", 100_000m);
        var run = await o.ApprovedRunAsync();

        var readiness = await k.Ok(k.Get($"{Runs}/{run}/posting-readiness"), "readiness");
        readiness.B("isReady").Should().BeFalse();
        readiness.A("missingGlRoles").Select(r => r.GetString()).Should().Contain("DEFAULT_SALARY_EXPENSE");
        var blocked = await k.Post($"{Runs}/{run}/finalize");
        blocked.Status.Should().Be(HttpStatusCode.Conflict, blocked.ToString());
        blocked.ToString().Should().Contain("POSTING_READINESS").And.Contain("DEFAULT_SALARY_EXPENSE");
        (await k.Ok(k.Get($"{Runs}/{run}"), "run")).S("status").Should().Be("Approved", "refused means nothing moved");

        // TS-G4: the setup status names it beside a Complete status.
        var setup = await k.Ok(k.Get("/api/finance/setup/status"), "setup");
        setup.S("status").Should().Be("Complete");
        setup.A("hrRolesMissing").Select(r => r.GetString()).Should().Contain("DEFAULT_SALARY_EXPENSE");

        var flat = (await k.Ok(k.Get(Gl.Chart + "/flat"), "chart")).Items();
        var sibling = flat.First(a => a.S("rootType") == "Expense" && !a.B("isGroup") && !a.IsNull("parentUuid"));
        var created = await k.Ok(k.CreateAccount(sibling.G("parentUuid"), "5901", "Salaries", "Expense"), "account");
        await k.Ok(k.SetDefault("DEFAULT_SALARY_EXPENSE", created.G("uuid")), "assign");
        (await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize")).S("status").Should().Be("Posted");
    }

    [Fact]
    public async Task TS_G12_reversal_makes_an_opposite_journal_a_new_draft_and_releases_what_it_took()
    {
        var o = await _root.ReadyOrgAsync("G12");
        var k = o.K;
        var e = await o.PaidStaffAsync("Rana", 100_000m);
        var loan = await k.Ok(k.Post("/api/hrm/loans", new { employeeUuid = e, type = "Personal", principal = 3_000m, installmentCount = 3, startMonth = "2026-10-01" }), "loan");
        await k.Ok(k.Post($"/api/hrm/loans/{loan.G("uuid")}/approve"), "loan approve");
        var disbursed = await k.Ok(k.Post($"/api/hrm/loans/{loan.G("uuid")}/disburse", new { disbursedOn = "2026-10-01", paidFromAccount = "Bank" }), "disburse");
        disbursed.S("postingState").Should().Be("Posted", disbursed.GetRawText());

        var run = await o.ApprovedRunAsync();
        var final = await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize");
        final.S("status").Should().Be("Posted", final.GetRawText());
        (await o.LinesAsync(run)).Single().Amount("LOAN").Should().Be(1_000m);
        var je = (await _f.JournalsAsync(o.OrgId, run)).Single();
        je.Net(HrQa.CodeOf("DEFAULT_EMPLOYEE_LOAN_RECEIVABLE")).Should().Be(-1_000m, je.ToString());
        var after = await k.Ok(k.Get($"/api/hrm/loans/{loan.G("uuid")}"), "loan");
        after.D("outstanding").Should().Be(2_000m);

        (await k.Post($"{Runs}/{run}/reverse", new { reason = "" })).Status.Should().Be(HttpStatusCode.BadRequest, "a reason is required");
        var reversed = await k.Ok(k.Post($"{Runs}/{run}/reverse", new { reason = "Wrong figures" }), "reverse");
        reversed.S("status").Should().Be("Reversed", reversed.GetRawText());
        reversed.NG("correctionRunUuid").Should().NotBeNull();

        var journals = await _f.JournalsAsync(o.OrgId, run);
        journals.Should().HaveCount(2, string.Join(" | ", journals));
        var opposite = journals.Single(j => j.Id != je.Id);
        opposite.Balanced.Should().BeTrue();
        foreach (var (code, _, _) in je.Lines) opposite.Net(code).Should().Be(-je.Net(code), "exact opposite on " + code);
        (await _f.JournalsAsync(o.OrgId, run)).Single(j => j.Id == je.Id).Status.Should().Be("Reversed", "the original remains");

        // Released.
        var released = await k.Ok(k.Get($"/api/hrm/loans/{loan.G("uuid")}"), "loan");
        released.D("outstanding").Should().Be(3_000m, released.GetRawText());

        // The correction runs and posts to a new journal; the loan is taken again, once.
        var correction = reversed.G("correctionRunUuid");
        await k.Ok(k.Post($"{Runs}/{correction}/calculate"), "calculate correction");
        await k.Ok(k.Post($"{Runs}/{correction}/review"), "review");
        await k.Ok(k.Post($"{Runs}/{correction}/approve", new { }), "approve");
        (await k.Ok(k.Post($"{Runs}/{correction}/finalize"), "finalize")).S("status").Should().Be("Posted");
        (await o.LinesAsync(correction)).Single().Amount("LOAN").Should().Be(1_000m);
        (await _f.JournalsAsync(o.OrgId, correction)).Single().Balanced.Should().BeTrue();

        var tb = await k.TrialBalanceAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        tb.B("isBalanced").Should().BeTrue();
    }

    [Fact]
    public async Task TS_G10_pay_date_closed_falls_back_to_period_end_both_closed_blocks()
    {
        var o = await _root.ReadyOrgAsync("G10");
        var k = o.K;
        await o.PaidStaffAsync("Sana", 100_000m);

        // Sept run paid in Oct: close Oct, the journal lands on 30 Sept.
        var octP = await k.PeriodOfAsync(new DateTime(2026, 10, 5));
        await k.Ok(k.SetPeriodStatus(octP, "Closed"), "close October");
        var run = await o.ApprovedRunAsync(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), new DateOnly(2026, 10, 5));
        var final = await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize");
        final.S("status").Should().Be("Posted", final.GetRawText());
        (await _f.JournalsAsync(o.OrgId, run)).Single().Date.Should().Be(new DateTime(2026, 9, 30));

        // Aug run paid in closed Oct, Aug closed too: both closed. (A closed period cannot be reopened.)
        var augP = await k.PeriodOfAsync(new DateTime(2026, 8, 5));
        await k.Ok(k.SetPeriodStatus(augP, "Closed"), "close Aug");
        var run2 = await o.ApprovedRunAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), new DateOnly(2026, 10, 5));
        var blocked = await k.Post($"{Runs}/{run2}/finalize");
        blocked.Status.Should().Be(HttpStatusCode.Conflict, blocked.ToString());
        blocked.ToString().Should().Contain("PAYROLL_PERIOD_CLOSED");
    }

    [Fact]
    public async Task TS_G19_no_target_stays_finalized_exports_pays_then_GL_on_retry_posts_it()
    {
        var o = await _root.ReadyOrgAsync("G19", gl: false);
        var k = o.K;
        await o.PaidStaffAsync("Nida", 100_000m);
        var run = await o.ApprovedRunAsync();

        var final = await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize");
        final.S("status").Should().Be("Finalized", final.GetRawText());
        final.A("postings").Should().Contain(p => p.S("status") == "NoTarget", final.GetRawText());
        final.S("postingNote").Should().NotContainEquivalentOf("fail");

        var csv = await k.DownloadAsync($"{Runs}/{run}/journal-export/csv");
        csv.Status.Should().Be(HttpStatusCode.OK);
        var text = Encoding.UTF8.GetString(csv.Bytes);
        text.Should().Contain("DEFAULT_SALARY_EXPENSE").And.Contain("100000");
        var xlsx = await k.DownloadAsync($"{Runs}/{run}/journal-export/xlsx");
        (xlsx.Status, xlsx.Bytes[0]).Should().Be((HttpStatusCode.OK, (byte)'P'));

        // TS-G19-004: GL arrives; the same run posts by retry.
        await k.EnableGlAsync();
        await o.MapHrRolesAsync();
        var retried = await k.Ok(k.Post($"{Runs}/{run}/retry-posting"), "retry");
        retried.S("status").Should().Be("Posted", retried.GetRawText());
        (await _f.JournalsAsync(o.OrgId, run)).Single().Balanced.Should().BeTrue();
    }

    [Fact]
    public async Task G19_no_target_pay_is_allowed_from_finalized()
    {
        var o = await _root.ReadyOrgAsync("G19P", gl: false);
        var k = o.K;
        await o.PaidStaffAsync("Asma", 100_000m);
        var run = await o.ApprovedRunAsync();
        await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize");
        var paid = await k.Ok(k.Post($"{Runs}/{run}/pay", new { paidOn = "2026-10-31", reference = "R1" }), "pay");
        paid.S("status").Should().Be("Paid", paid.GetRawText());
    }

    [Fact]
    public async Task H5_mid_month_joiner_leaver_blocked_line_and_run_uniqueness_over_http()
    {
        var o = await _root.ReadyOrgAsync("H5");
        var k = o.K;
        var joiner = await o.PaidStaffAsync("Joiner", 31_000m, 0m, from: "2026-10-17");
        var leaver = await o.PaidStaffAsync("Leaver", 31_000m);
        await k.Ok(k.Patch($"/api/hrm/employees/{leaver}/status", new { status = "Terminated", effectiveDate = "2026-10-10", departureReason = "Resignation" }), "terminate");
        var nosalary = await o.EmployeeAsync("NoSalary");
        await o.BankAsync(nosalary.G("uuid"));
        var nobank = await o.EmployeeAsync("NoBank");
        await o.SalaryAsync(nobank.G("uuid"), 50_000m);

        var run = (await o.NewRunAsync()).G("uuid");
        (await k.Post("/api/hrm/payroll-runs", new { periodStart = "2026-10-01", periodEnd = "2026-10-31", payDate = "2026-10-31" })).Status.Should().Be(HttpStatusCode.Conflict, "PAYROLL_RUN_EXISTS");
        (await k.Post("/api/hrm/payroll-runs", new { periodStart = "2026-11-01", periodEnd = "2026-11-10", payDate = "2026-11-10" })).Status.Should().Be(HttpStatusCode.BadRequest, "PAYROLL_PERIOD_INVALID");

        await k.Ok(k.Post($"{Runs}/{run}/calculate"), "calculate");
        var lines = await o.LinesAsync(run);
        var j = lines.Single(l => l.G("employeeUuid") == joiner);
        j.Amount("BASIC").Should().Be(15_000m, "15 of 31 days, " + j.GetRawText());
        var l2 = lines.Single(l => l.G("employeeUuid") == leaver);
        l2.Amount("BASIC").Should().Be(10_000m, "10 of 31 days, " + l2.GetRawText());
        lines.Single(l => l.G("employeeUuid") == nosalary.G("uuid")).B("isBlocked").Should().BeTrue();
        lines.Single(l => l.G("employeeUuid") == nobank.G("uuid")).B("isBlocked").Should().BeTrue();

        await k.Ok(k.Post($"{Runs}/{run}/review"), "review");
        var refused = await k.Post($"{Runs}/{run}/approve", new { });
        refused.Status.Should().Be(HttpStatusCode.Conflict, refused.ToString());
        refused.ToString().Should().Contain("PAYROLL_HAS_BLOCKED");
        await k.Ok(k.Post($"{Runs}/{run}/approve", new { excludeBlocked = true }), "approve");
        var final = await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize");
        final.S("status").Should().Be("Posted", final.GetRawText());
        (await _f.JournalsAsync(o.OrgId, run)).Single().Balanced.Should().BeTrue();
    }
}
