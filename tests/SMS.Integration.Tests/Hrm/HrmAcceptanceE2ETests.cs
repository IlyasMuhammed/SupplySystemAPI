using System.Diagnostics;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.JournalEngine;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Modules.Integration.Core.Providers;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Integration.Tests.FinancialPosting.Fp;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 QA, tasks 413 (P2-S acceptance), 414, 415, 416, 417 and the TS-G scenarios. Real host, real GL, real gateway.
/// <b>Every QuickBooks result in this class is against FakeQuickBooksCompany. None is a sandbox pass:</b> no Intuit keys exist here.
/// Run alone: dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~HrmAcceptanceE2ETests
/// </summary>
public sealed class HrmAcceptanceE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string Runs = "/api/hrm/payroll-runs";
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public HrmAcceptanceE2ETests(SapWebApplicationFactory f)
    {
        _f = f;
        f.ShouldBeOnLocalDb();
        _root = new SapKit(f, "A49A");
    }

    [Fact]
    public async Task T413_P2S_service_invoice_receipt_payroll_expense_balanced_TB_and_PnL_with_stock_and_selling_off()
    {
        var fp = await _root.NewOrgAsync("P2S");
        var k = fp.K;
        await _root.Ok(_root.Post($"/api/tenancy/organizations/{fp.OrgId}/apply-profile", new { code = "P2-S" }), "apply P2-S");
        var o = await fp.ReadyAsync(_root);

        var modules = (await k.Ok(k.Get("/api/tenant/modules/enabled"), "enabled")).A("modules").Select(m => m.GetString()).ToList();
        modules.Should().Contain("MODULE_HRM").And.Contain("MODULE_FINANCE").And.Contain("MODULE_CUSTOMERS")
            .And.NotContain("MODULE_DEMAND").And.NotContain("MODULE_WAREHOUSE").And.NotContain("MODULE_LOGISTICS").And.NotContain("MODULE_MANUFACTURING").And.NotContain("MODULE_POS");
        (await k.Get("/api/grns")).Status.Should().Be(HttpStatusCode.Forbidden, "receiving is off");
        (await k.Get("/api/sale-orders")).Status.Should().Be(HttpStatusCode.Forbidden, "selling is off");

        // Service invoice → issue → receipt.
        var customer = await k.CreateCustomerAsync("Consulting client");
        (await k.Post("/api/sales-invoices/service", new { customerUuid = customer.Uuid, currencyCode = "PKR", lines = new[] { new { description = "x", quantity = 1m, unitPrice = 1m, incomeAccountCode = "4000" } } }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "SERVICE_INCOME_ACCOUNT_NOT_SUPPORTED");
        var invoice = await k.Ok(k.Post("/api/sales-invoices/service", new
        {
            customerUuid = customer.Uuid, invoiceDate = "2026-10-05", dueDate = "2026-11-05", currencyCode = "PKR",
            lines = new[] { new { description = "Consulting", quantity = 2m, unitPrice = 150_000m } }
        }), "service invoice");
        var invoiceUuid = invoice.G("invoiceUuid");
        await k.IssueAsync(invoiceUuid);
        var codes = await k.DefaultCodesAsync();
        var invJe = (await _f.JournalsAsync(o.OrgId, invoiceUuid)).Should().ContainSingle().Subject;
        invJe.Balanced.Should().BeTrue(invJe.ToString());
        invJe.Net(codes["DEFAULT_REVENUE"]).Should().Be(-300_000m, invJe.ToString());
        invJe.Net(codes["DEFAULT_RECEIVABLE"]).Should().Be(300_000m);
        await k.RecordPaymentAsync(customer, 300_000m, "BANK_TRANSFER", (invoiceUuid, 300_000m));
        (await k.GetInvoiceAsync(invoiceUuid)).S("status").Should().BeOneOf("PAID", "Paid");

        // Payroll.
        var boss = await o.UserAsync("p2boss", "HRM_SELF_SERVICE", "HRM_EXPENSE_APPROVE");
        var staff = await o.UserAsync("p2staff", "HRM_SELF_SERVICE");
        var bossE = (await o.EmployeeAsync("Boss", login: boss)).G("uuid");
        await o.PaidStaffAsync("Staff", 100_000m, 20_000m, staff, manager: bossE);
        var run = await o.ApprovedRunAsync();
        (await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize")).S("status").Should().Be("Posted");
        await k.Ok(k.Post($"{Runs}/{run}/pay", new { paidOn = "2026-10-31", reference = "OCT", paidFromAccount = "Bank" }), "pay");

        // Expense, separately paid: TS-G9-001, -002.
        var cat = await k.Ok(k.Post("/api/hrm/expense-categories", new { code = "TRV", name = "Travel", accountRole = "DEFAULT_BONUS_EXPENSE" }), "category");
        var claim = await k.Ok(k.Post("/api/hrm/expense-claims", new { settlementMethod = "SeparatePayment", lines = new[] { new { categoryUuid = cat.G("uuid"), expenseDate = "2026-10-10", amount = 5_000m, taxAmount = 0m } } }, staff), "claim");
        await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/submit", null, staff), "submit");
        (await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/approve", null, boss), "approve")).S("postingState").Should().Be("Posted");
        var payer = await o.UserAsync("p2pay", "HRM_EXPENSE_PAY", "HRM_SCOPE_ALL");
        (await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/pay", new { paidOn = "2026-10-20", paidFromAccount = "Bank" }, payer), "pay claim")).S("paymentPostingState").Should().Be("Posted");
        var claimJes = await _f.JournalsAsync(o.OrgId, claim.G("uuid"));
        claimJes.Should().HaveCount(2).And.OnlyContain(j => j.Balanced);
        claimJes.Sum(j => j.Net(HrQa.CodeOf("DEFAULT_EXPENSE_CLAIM_PAYABLE"))).Should().Be(0m, "accrued then cleared");

        // Trial balance, then the P&L as it derives from it.
        var tb = await k.TrialBalanceAsync(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));
        tb.B("isBalanced").Should().BeTrue(tb.GetRawText());
        tb.D("totalDebit").Should().Be(tb.D("totalCredit"));
        var net = HrQa.PeriodNet(tb);
        net[codes["DEFAULT_REVENUE"]].Should().Be(-300_000m);
        net[HrQa.CodeOf("DEFAULT_SALARY_EXPENSE")].Should().Be(120_000m);
        net[HrQa.CodeOf("DEFAULT_EMPLOYER_CONTRIB_EXPENSE")].Should().Be(10_180m);
        net[HrQa.CodeOf("DEFAULT_BONUS_EXPENSE")].Should().Be(5_000m);
        // Bank: + 300,000 receipt - 108,600 net pay - 5,000 claim. Boss has no salary, so no line.
        net[codes["DEFAULT_BANK"]].Should().Be(186_400m);
        // Income 300,000 - cost 135,180 = 164,820, derived by root type from TB rows.
        var accounts = await k.AccountsAsync();
        var pnl = net.Where(n => accounts.TryGetValue(n.Key, out var a) && a.S("rootType") is "Revenue" or "Expense").Sum(n => n.Value);
        pnl.Should().Be(-164_820m);

        // TS-G8-002: the report the spec names. Reported, not assumed: is there a P&L route at all?
        var report = await k.Get("/api/finance/profit-and-loss?fromDate=2026-01-01&toDate=2026-12-31");
        report.Status.Should().Be(HttpStatusCode.OK, "task 413 and TS-G8-002 name a P&L report; route probed: /api/finance/profit-and-loss — " + report);
    }

    // ── QuickBooks: FAKE ─────────────────────────────────────────────────────────

    /// <summary>GL plus fake QuickBooks, with every HR account, the payable and the bank mapped; class maps for all centers. Types stay off + held until asked.</summary>
    private async Task<HrOrg> QboOrgAsync(string prefix)
    {
        var fp = await (await _root.NewOrgAsync(prefix)).ConnectLiveAsync();
        return await fp.ReadyAsync(_root);
    }

    private static async Task MapAccountsAsync(HrOrg o)
    {
        var k = o.K;
        var accounts = await k.Ok(k.Get($"{Qb}/accounts"), "accounts");
        var mappings = accounts.A("accounts").Where(a => a.P("accountUuid").ValueKind != JsonValueKind.Null)
            .Select(a => new { accountUuid = a.G("accountUuid"), qboAccountId = (a.S("code") ?? "").StartsWith('5') ? "3" : (a.S("code") ?? "").StartsWith('2') ? "7" : "5" }).ToArray();
        await k.Ok(k.Put($"{Qb}/accounts", new { mappings }), "map accounts");
    }

    private static async Task MapCentersAsync(HrOrg o)
    {
        var k = o.K;
        await k.Ok(k.Post($"{Qb}/cost-center-mappings/sync"), "sync centers");
        foreach (var m in (await k.Ok(k.Get($"{Qb}/cost-center-mappings"), "map")).Items())
            await k.Ok(k.Put($"{Qb}/cost-center-mappings/{m.S("costCenterCode")}", new { qboClassRef = "CL-" + m.S("costCenterCode"), qboClassName = m.S("costCenterName") }), "map class");
    }

    [Fact]
    public async Task T414_TS_G1_G2_G3_G5_G11_G12_G15_FAKE_payroll_to_GL_to_QuickBooks_with_five_cost_centers()
    {
        var o = await QboOrgAsync("Q414");
        var k = o.K;

        // TS-G15-001: off + held on this connection, and TS-G3-004's reading: GL posts while QuickBooks is off.
        var row = await k.SyncControlRowAsync("PayrollRun");
        (row.B("isEnabled"), row.B("holdWhenDisabled")).Should().Be((false, true), row.GetRawText());

        // TS-G1-002: five departments, five centers, by department.
        await k.Ok(k.Put("/api/hrm/settings", new { payrollCostCenterMode = "BY_DEPARTMENT" }), "by department");
        var people = new List<Guid>();
        for (var i = 1; i <= 5; i++)
        {
            var center = await k.Ok(k.Post("/api/hrm/cost-centers", new { code = $"CC{i}", name = $"Center {i}" }), "center");
            var dept = await k.Ok(k.Post("/api/hrm/departments", new { code = $"D{i}", name = $"Dept {i}", costCenterUuid = center.G("uuid") }), "dept");
            people.Add(await o.PaidStaffAsync($"Uniquename{i}", 100_000m, department: dept.G("uuid")));
        }

        // TS-G3-002 / G5-003: blocked while on and unmapped. Enable first needs ready: refused, naming what is missing.
        var refused = await k.SetSyncControl("PayrollRun", enabled: true, hold: true);
        refused.Status.Should().Be(HttpStatusCode.Conflict, refused.ToString());
        refused.ToString().Should().Contain("HRM_SYNC_NOT_READY");
        await MapAccountsAsync(o);
        var centersMissing = await k.SetSyncControl("PayrollRun", enabled: true, hold: true);
        centersMissing.Status.Should().Be(HttpStatusCode.Conflict, centersMissing.ToString());
        centersMissing.ToString().Should().Contain("CC1");
        await MapCentersAsync(o);
        await k.Ok(k.SetSyncControl("PayrollRun", enabled: true, hold: true), "enable (TS-G15-002)");

        var run = await o.ApprovedRunAsync();
        var runNumber = (await k.Ok(k.Get($"{Runs}/{run}"), "run")).S("runNumber")!;
        var ready = await k.Ok(k.Get($"{Runs}/{run}/posting-readiness"), "readiness");
        (ready.B("quickBooksActive"), ready.B("quickBooksReady")).Should().Be((true, true), ready.GetRawText());
        await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize");
        await _f.RunJobsAsync(3);

        // GL.
        (await _f.JournalsAsync(o.OrgId, run)).Single().Balanced.Should().BeTrue();
        // Fake QuickBooks: JournalEntry, not Bill; balanced; a class per center; no person.
        var je = _f.Company.Records(SyncKind.JournalEntry).Where(r => r.Name != null && r.Name.StartsWith(runNumber)).ToList();
        var sent = (RemoteJournalEntry)je.Should().ContainSingle(string.Join(",", je.Select(x => x.Name))).Subject.Entity!;
        sent.Lines.Where(l => l.IsDebit).Sum(l => l.Amount).Should().Be(sent.Lines.Where(l => !l.IsDebit).Sum(l => l.Amount));
        sent.Lines.Where(l => l.IsDebit).Select(l => l.ClassId).Distinct().Should().BeEquivalentTo(["CL-CC1", "CL-CC2", "CL-CC3", "CL-CC4", "CL-CC5"]);
        sent.Lines.Where(l => !l.IsDebit).Should().OnlyContain(l => l.ClassId == "CL-PAYROLL", "BR-G5-003");
        var payload = JsonSerializer.Serialize(sent, sent.GetType());
        payload.Should().NotContain("Uniquename").And.NotContain("EMP-0");
        sent.PrivateNote.Should().Contain(runNumber).And.Contain("5 employees");
        var stored = await _f.QueryAsync("SELECT m.PayloadJson FROM integration.EntityMaps m JOIN integration.Connections c ON c.Id = m.ConnectionId WHERE c.OrganizationId = @o AND m.Kind = 'JournalEntry'", ("@o", o.OrgId));
        stored.Should().NotBeEmpty();
        stored.Select(s => s["PayloadJson"]?.ToString()).Should().NotContain(p => p != null && p.Contains("Uniquename"), "G-11 in the stored payload");

        // TS-G1-003: the same key again sends nothing.
        var writes = _f.Company.Writes.Count;
        await k.Post($"{Runs}/{run}/retry-posting");
        await _f.RunJobsAsync(2);
        _f.Company.Writes.Count.Should().Be(writes);
        _f.Company.Records(SyncKind.JournalEntry).Count(r => r.Name == sent.DocNumber).Should().Be(1);

        // TS-G12: opposite entry, the original stays.
        var reversed = await k.Ok(k.Post($"{Runs}/{run}/reverse", new { reason = "correction" }), "reverse");
        await _f.RunJobsAsync(3);
        var opposite = (RemoteJournalEntry)_f.Company.Records(SyncKind.JournalEntry).Single(r => r.Name == sent.DocNumber + "-R").Entity!;
        opposite.Lines.Select(l => (l.IsDebit, l.Amount)).Should().BeEquivalentTo(sent.Lines.Select(l => (!l.IsDebit, l.Amount)));
        _f.Company.Records(SyncKind.JournalEntry).Should().Contain(r => r.Name == sent.DocNumber, "BR-G12-002");
        reversed.NG("correctionRunUuid").Should().NotBeNull();
    }

    [Fact]
    public async Task TS_G1_004_FAKE_a_timeout_after_create_does_not_double_post_and_retries_to_sent()
    {
        var o = await QboOrgAsync("Q004");
        var k = o.K;
        await o.PaidStaffAsync("Tariq", 100_000m);
        await MapAccountsAsync(o);
        await MapCentersAsync(o);
        await k.Ok(k.SetSyncControl("PayrollRun", enabled: true, hold: true), "enable");
        var run = await o.ApprovedRunAsync();
        var number = (await k.Ok(k.Get($"{Runs}/{run}"), "run")).S("runNumber")!;

        _f.Company.TimeOutAfterNextCreateOf = SyncKind.JournalEntry;
        await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize");
        _f.Company.TimeOutAfterNextCreateOf = null;
        await _f.RunJobsAsync(4);
        await _f.ExecuteAsync("UPDATE x SET NextAttemptAt = DATEADD(MINUTE, -1, SYSUTCDATETIME()) FROM integration.Outbox x JOIN integration.Connections c ON c.Id = x.ConnectionId WHERE c.OrganizationId = @o", ("@o", o.OrgId));
        await _f.RunJobsAsync(4);
        _f.Company.Records(SyncKind.JournalEntry).Where(r => r.Name != null && r.Name.StartsWith(number)).Should().ContainSingle("a timed-out create is matched, not repeated");
    }

    [Fact]
    public async Task T415_TS_G9_G13_FAKE_expense_claim_and_remittance_to_GL_and_QuickBooks()
    {
        var o = await QboOrgAsync("Q415");
        var k = o.K;
        var boss = await o.UserAsync("q5b", "HRM_SELF_SERVICE", "HRM_EXPENSE_APPROVE");
        var staff = await o.UserAsync("q5s", "HRM_SELF_SERVICE");
        var bossE = (await o.EmployeeAsync("Boss", login: boss)).G("uuid");
        await o.EmployeeAsync("Uniquestaff", login: staff, manager: bossE);
        await MapAccountsAsync(o);
        await MapCentersAsync(o);
        foreach (var t in new[] { "Expense", "ExpensePayment", "StatutoryRemittance" })
            await k.Ok(k.SetSyncControl(t, enabled: true, hold: true), "enable " + t);

        var cat = await k.Ok(k.Post("/api/hrm/expense-categories", new { code = "TRV", name = "Travel", accountRole = "DEFAULT_BONUS_EXPENSE" }), "category");
        var claim = await k.Ok(k.Post("/api/hrm/expense-claims", new
        {
            settlementMethod = "SeparatePayment", description = "Uniquestaff trip",
            lines = new[] { new { categoryUuid = cat.G("uuid"), expenseDate = "2026-10-10", amount = 700m, taxAmount = 0m, description = "Uniquestaff taxi" } }
        }, staff), "claim");
        await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/submit", null, staff), "submit");
        var approved = await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/approve", null, boss), "approve");
        approved.S("postingState").Should().Be("Posted", approved.GetRawText());
        await k.Ok(k.Post($"/api/hrm/expense-claims/{claim.G("uuid")}/pay", new { paidOn = "2026-10-20", paidFromAccount = "Bank" }), "pay");
        var remit = await k.Ok(k.Post("/api/hrm/remittances", new { remittanceType = "EOBI", periodStart = "2026-10-01", periodEnd = "2026-10-31", amount = 300m, paidOn = "2026-10-31", paidFromAccount = "Bank", reference = "E1", force = true }), "remit");
        await _f.RunJobsAsync(3);

        (await _f.JournalsAsync(o.OrgId, claim.G("uuid"))).Should().HaveCount(2).And.OnlyContain(j => j.Balanced);
        var remitJe = (await _f.JournalsAsync(o.OrgId, remit.G("uuid"))).Single();
        remitJe.Net(HrQa.CodeOf("DEFAULT_EOBI_PAYABLE")).Should().Be(300m, remitJe.ToString());

        var all = _f.Company.Records(SyncKind.JournalEntry).Select(r => r.Entity).OfType<RemoteJournalEntry>().Where(e => e.Lines.Any(l => l.Amount is 700m or 300m)).ToList();
        var states = await _f.QueryAsync("SELECT m.Kind, m.ExternalId, m.State, m.RemoteId, m.LastErrorCode, m.LastError FROM integration.EntityMaps m JOIN integration.Connections c ON c.Id = m.ConnectionId WHERE c.OrganizationId = @o AND m.Kind = 'JournalEntry'", ("@o", o.OrgId));
        all.Should().HaveCount(3, "claim, its payment, the remittance reach the fake as journal entries. Sent: " + string.Join(", ", all.Select(e => e.DocNumber + "/" + e.Lines.Count)) + ". Maps: " + string.Join(" | ", states.Select(s => string.Join(",", s.Values))));
        foreach (var e in all)
        {
            e.Lines.Where(l => l.IsDebit).Sum(l => l.Amount).Should().Be(e.Lines.Where(l => !l.IsDebit).Sum(l => l.Amount));
            JsonSerializer.Serialize(e, e.GetType()).Should().NotContain("Uniquestaff", "G-11");
        }
        var maps = await _f.QueryAsync("SELECT m.PayloadJson FROM integration.EntityMaps m JOIN integration.Connections c ON c.Id = m.ConnectionId WHERE c.OrganizationId = @o AND m.Kind = 'JournalEntry'", ("@o", o.OrgId));
        maps.Select(m => m["PayloadJson"]?.ToString()).Should().NotContain(p => p != null && p.Contains("Uniquestaff"));
    }

    [Fact]
    public async Task TS_G14_FAKE_currency_mismatch_blocks()
    {
        var fp = await (await _root.NewOrgAsync("Q014")).ConnectLiveAsync();
        // The fake's home currency is always PKR, so the organization moves.
        var usd = await _root.EnsureCurrencyAsync("USD", "US Dollar", "$");
        await _root.SetOrgBaseCurrencyAsync(fp.OrgId, usd);
        await fp.K.EnsureOrgCurrencyAsync("USD", "US Dollar", "$");
        var o = await fp.ReadyAsync(_root);
        await o.PaidStaffAsync("Umar", 100_000m);
        await MapAccountsAsync(o);
        await MapCentersAsync(o);
        var refused = await o.K.SetSyncControl("PayrollRun", enabled: true, hold: true);
        refused.Status.Should().Be(HttpStatusCode.Conflict, refused.ToString());
        refused.ToString().Should().Contain("HRM_SYNC_NOT_READY");
        refused.Result.P("currencyMismatch").ValueKind.Should().Be(JsonValueKind.Object, refused.ToString());
    }

    [Fact]
    public async Task T417_TS_G16_001_FAKE_1200_line_journal_splits_into_balanced_entries_through_the_real_gateway()
    {
        var o = await QboOrgAsync("Q417");
        var k = o.K;
        await MapAccountsAsync(o);
        var centers = Enumerable.Range(1, 1198).Select(i => $"C{i:D4}").ToList();
        foreach (var c in centers)
            await k.Ok(k.Post("/api/hrm/cost-centers", new { code = c, name = "Unit " + c }), "center");
        await MapCentersAsync(o);
        await k.Ok(k.SetSyncControl("PayrollRun", enabled: true, hold: true), "enable");

        var lines = new List<FinancialTransactionLine>();
        foreach (var (c, i) in centers.Select((c, i) => (c, i)))
            lines.Add(new() { LineNumber = i + 1, AccountRole = "DEFAULT_SALARY_EXPENSE", Debit = 100m + i % 7, CostCenter = c, Label = "Salaries" });
        var total = lines.Sum(l => l.Debit);
        lines.Add(new() { LineNumber = 1199, AccountRole = "DEFAULT_EMPLOYEE_TAX_PAYABLE", Credit = 1_000m, CostCenter = HrmEnableKit.PayrollCostCenter, Label = "Tax" });
        lines.Add(new() { LineNumber = 1200, AccountRole = "DEFAULT_SALARY_PAYABLE", Credit = total - 1_000m, CostCenter = HrmEnableKit.PayrollCostCenter, Label = "Net" });
        var id = Guid.NewGuid();
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.PayrollRun, SourceDocumentId = id, SourceDocumentNumber = "PAY-1200", SourceModule = PostingSourceModules.Hrm,
            TransactionDate = new DateTime(2026, 10, 31), CurrencyCode = "PKR", Description = "Payroll", Lines = lines,
            Metadata = new FinancialDocumentMetadata { Properties = new Dictionary<string, string> { [HrmPostingProperties.Period] = "2026-10", [HrmPostingProperties.Headcount] = "1198" } }
        };

        var clock = Stopwatch.StartNew();
        var result = await _f.PostDirectAsync(o.OrgId, tx);
        result.Status.Should().Be(PostingStatus.Success, result.Describe());
        await _f.RunJobsAsync(6);
        clock.Stop();
        Console.WriteLine($"T417 FAKE: 1200-line post + split + send took {clock.ElapsedMilliseconds} ms");

        var je = _f.Company.Records(SyncKind.JournalEntry).Where(r => r.Name != null && r.Name.StartsWith("PAY-1200")).Select(r => (RemoteJournalEntry)r.Entity!).ToList();
        je.Should().HaveCount(2, string.Join(",", je.Select(e => $"{e.DocNumber}:{e.Lines.Count}")));
        foreach (var e in je) e.Lines.Where(l => l.IsDebit).Sum(l => l.Amount).Should().Be(e.Lines.Where(l => !l.IsDebit).Sum(l => l.Amount), e.DocNumber);
        je.SelectMany(e => e.Lines).Where(l => l.IsDebit).Sum(l => l.Amount).Should().Be(total);
        (await _f.JournalsAsync(o.OrgId, id)).Single().Balanced.Should().BeTrue("the GL keeps one entry");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(2));
    }

    // ── 416 ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task T416_payroll_calculation_for_500_employees_under_30_seconds_on_the_background_path_and_it_posts()
    {
        var o = await _root.ReadyOrgAsync("P500");
        var k = o.K;
        for (var i = 0; i < 500; i++)
            await o.PaidStaffAsync($"Perf{i:D3}", 50_000m + i, 5_000m);

        var run = (await o.NewRunAsync()).G("uuid");
        var clock = Stopwatch.StartNew();
        var calc = await k.Post($"{Runs}/{run}/calculate");
        calc.Status.Should().Be(HttpStatusCode.Accepted, "more than 200 employees goes to the job: " + calc);
        var done = await SapKit.WaitForAsync(() => k.Ok(k.Get($"{Runs}/{run}"), "run"), r => r.S("status") == "Calculated", "the calculation", 300);
        clock.Stop();
        Console.WriteLine($"T416: 500 employees calculated in {clock.Elapsed.TotalSeconds:0.0} s");
        done.I("totalEmployees").Should().Be(500);
        done.I("blockedEmployees").Should().Be(0);
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "task 416");

        await k.Ok(k.Post($"{Runs}/{run}/review"), "review");
        await k.Ok(k.Post($"{Runs}/{run}/approve", new { }), "approve");
        clock.Restart();
        (await k.Ok(k.Post($"{Runs}/{run}/finalize"), "finalize")).S("status").Should().Be("Posted");
        Console.WriteLine($"T416: finalize + post in {clock.Elapsed.TotalSeconds:0.0} s");
        var je = (await _f.JournalsAsync(o.OrgId, run)).Single();
        je.Balanced.Should().BeTrue();
        je.Lines.Count.Should().BeLessThan(20, "summarized, not per employee (FD-2)");
    }
}
