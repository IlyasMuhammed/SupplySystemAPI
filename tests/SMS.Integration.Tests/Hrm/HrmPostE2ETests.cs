using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.DataImport;
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
/// A49 (POST) on the real host (LocalDB) with only Intuit faked. <b>TS-G1-* here ran against <c>FakeQuickBooksCompany</c>, never a QuickBooks
/// sandbox:</b> there are no Intuit keys in this environment.
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~HrmPostE2ETests</c>.
/// </summary>
public sealed class HrmPostE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public HrmPostE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A49");
    }

    [Fact]
    public async Task E6_the_enablement_path_licence_then_422_then_wizard_then_on()
    {
        var o = await _root.OrgAsync("POSTEN", currency: false);
        var k = o.K;

        // Not licensed: told so, nothing created, HRM refused.
        (await k.Ok(k.Get("/api/hrm/setup/status"), "status")).B("licensed").Should().BeFalse();
        (await k.Post("/api/hrm/setup/payroll-cost-center", new { newCostCenter = new { code = "SAL", name = "Salaries" } })).Status.Should().Be(HttpStatusCode.Forbidden);

        // The super admin's licence does not switch it on without a cost center.
        await _root.LicenceAsync(o.OrgId, ("MODULE_HRM", true));
        (await k.Get("/api/hrm/branches")).Status.Should().Be(HttpStatusCode.Forbidden, "licensed, still off");

        var refused = await k.Post("/api/tenant/modules/MODULE_HRM/enable", new { });
        refused.Status.Should().Be(HttpStatusCode.UnprocessableEntity, refused.ToString());
        refused.ToString().Should().Contain("PAYROLL_COST_CENTER_REQUIRED");

        var status = await k.Ok(k.Get("/api/hrm/setup/status"), "status");
        (status.B("licensed"), status.B("enabled")).Should().Be((true, false));
        status.A("costCenters").Should().BeEmpty();

        var settings = await k.Ok(k.Post("/api/hrm/setup/payroll-cost-center", new { newCostCenter = new { code = "sal", name = "Salaries" }, mode = "BY_DEPARTMENT" }), "wizard");
        settings.S("payrollCostCenterCode").Should().Be("SAL");

        await k.Ok(k.Post("/api/tenant/modules/MODULE_HRM/enable", new { }), "enable");
        (await k.Ok(k.Get("/api/hrm/setup/status"), "status")).B("enabled").Should().BeTrue();
        (await k.Get("/api/hrm/branches")).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TS_G1_G12_G15_a_payroll_journal_from_held_to_sent_to_reversed_with_the_posting_log_following()
    {
        var o = await (await _root.NewOrgAsync("POSTJE")).ConnectLiveAsync();
        var k = o.K;
        await _root.EnableHrmAsync(o.O);

        // G-15: the types show now MODULE_HRM is on, and this connection has them off + held.
        var rows = await k.SyncControlAllAsync();
        var run = rows.Single(r => r.S("documentType") == "PayrollRun");
        (run.B("isEnabled"), run.B("holdWhenDisabled")).Should().Be((false, true), J.Short(run));

        var id = Guid.NewGuid();
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.PayrollRun, SourceDocumentId = id, SourceDocumentNumber = "PAY-E2E-1", SourceModule = PostingSourceModules.Hrm,
            TransactionDate = Today, CurrencyCode = "PKR", Description = "Payroll",
            Lines =
            [
                new() { LineNumber = 1, AccountRole = "DEFAULT_PURCHASE", Debit = 900m, CostCenter = HrmEnableKit.PayrollCostCenter, Label = "Somebody's salary" },
                new() { LineNumber = 2, AccountRole = "DEFAULT_BANK", Credit = 900m }
            ],
            Metadata = new FinancialDocumentMetadata { Properties = new Dictionary<string, string> { [HrmPostingProperties.Period] = "2026-10", [HrmPostingProperties.Headcount] = "3" } }
        };

        // Held, whatever the mappings say: stored, nothing sent.
        var held = await _f.PostDirectAsync(o.OrgId, tx);
        held.Target(PostingTargetCodes.QuickBooks)!.Status.Should().Be(TargetPostingStatus.Held, held.Describe());
        await _f.RunJobsAsync(2);
        _f.Company.Records(SyncKind.JournalEntry).Should().NotContain(r => r.Name == "PAY-E2E-1");

        // BR-G15-002: refused until it can post.
        var notReady = await k.SetSyncControl("PayrollRun", enabled: true, hold: true);
        notReady.Status.Should().Be(HttpStatusCode.Conflict, notReady.ToString());
        notReady.ToString().Should().Contain("HRM_SYNC_NOT_READY");

        // G-5: the accountant maps the class, through the routes the mapping tab uses.
        var map = await k.Ok(k.Get($"{Qb}/cost-center-mappings"), "cost center map");
        map.Items().Should().Contain(m => m.S("costCenterCode") == HrmEnableKit.PayrollCostCenter && !m.B("isMapped"));
        (await k.Ok(k.Get($"{Qb}/cost-center-mappings/unmapped"), "unmapped")).Items().Should().ContainSingle();
        await k.Ok(k.Put($"{Qb}/cost-center-mappings/{HrmEnableKit.PayrollCostCenter}", new { qboClassRef = "CL-1", qboClassName = "Payroll" }), "map the class");

        // ...and the accounts.
        await k.CompleteGlSetupAsync();
        var codes = await k.DefaultCodesAsync();
        var accounts = await k.Ok(k.Get($"{Qb}/accounts"), "accounts");
        var mappings = new[] { ("DEFAULT_PURCHASE", "3"), ("DEFAULT_BANK", "5"), ("DEFAULT_SALARY_EXPENSE", "3") }
            .Where(m => codes.ContainsKey(m.Item1))
            .Select(m => new { accountUuid = accounts.A("accounts").First(a => a.S("code") == codes[m.Item1]).G("accountUuid"), qboAccountId = m.Item2 })
            .ToArray();
        await k.Ok(k.Put($"{Qb}/accounts", new { mappings }), "map the accounts");

        // The role the type always names has no account yet: still refused, naming it.
        var stillNot = await k.SetSyncControl("PayrollRun", enabled: true, hold: true);
        stillNot.Status.Should().Be(HttpStatusCode.Conflict, "DEFAULT_SALARY_PAYABLE is unmapped");

        // Assign the HR roles the type always names (HR keys are only ever assigned by hand) and map the new accounts.
        var byCode = accounts.A("accounts").ToDictionary(a => a.S("code")!, a => a.G("accountUuid"));
        await k.Ok(k.Put("/api/finance/accounting-defaults/DEFAULT_SALARY_EXPENSE", new { accountUuid = byCode[codes["DEFAULT_PURCHASE"]] }), "salary expense");
        await k.Ok(k.Put("/api/finance/accounting-defaults/DEFAULT_SALARY_PAYABLE", new { accountUuid = byCode[codes["DEFAULT_PAYABLE"]] }), "salary payable");
        await k.Ok(k.Put($"{Qb}/accounts", new { mappings = new[] { new { accountUuid = byCode[codes["DEFAULT_PAYABLE"]], qboAccountId = "7" } } }), "map payable");
        await k.Ok(k.SetSyncControl("PayrollRun", enabled: true, hold: true), "turn the journal on");
        await _f.RunJobsAsync(3);

        // Sent: a summarized, classified journal entry; no employee data.
        var sent = (RemoteJournalEntry)_f.Company.Records(SyncKind.JournalEntry).Single(r => r.Name == "PAY-E2E-1").Entity!;
        sent.PrivateNote.Should().Be("PAY-E2E-1, 2026-10, 3 employees");
        sent.Lines.Should().OnlyContain(l => l.ClassId == "CL-1" && l.Description == "Payroll");

        // The posting log's rows follow the outbox (refresher).
        var log = await k.Ok(k.Get($"{Posting}/by-source/{id}"), "by-source");
        J.Short(log).Should().Contain("Posted");

        // A posted run reverses as a new, opposite entry; the original stays.
        var reversed = await _f.VoidDirectAsync(o.OrgId, new VoidRequest
        {
            SourceDocumentId = id, SourceModule = PostingSourceModules.Hrm, DocumentType = FinDocType.PayrollRun, SourceDocumentNumber = "PAY-E2E-1", VoidDate = Today
        });
        reversed.Target(PostingTargetCodes.QuickBooks)!.Status.Should().Be(TargetPostingStatus.Queued, reversed.Describe());
        await _f.RunJobsAsync(3);

        var opposite = (RemoteJournalEntry)_f.Company.Records(SyncKind.JournalEntry).Single(r => r.Name == "PAY-E2E-1-R").Entity!;
        opposite.Lines.Select(l => (l.IsDebit, l.Amount)).Should().BeEquivalentTo(sent.Lines.Select(l => (!l.IsDebit, l.Amount)));
        _f.Company.Records(SyncKind.JournalEntry).Should().Contain(r => r.Name == "PAY-E2E-1", "BR-G12-002");
        J.Short(await k.Ok(k.Get($"{Posting}/by-source/{id}"), "by-source")).Should().NotContain("Queued");
    }

    [Fact]
    public async Task G8_TS_G8_001_002_a_P_PK_organization_with_only_payroll_data_reports_a_balanced_trial_balance()
    {
        var fp = await _root.NewOrgAsync("POSTG8");
        var k = fp.K;
        await _root.Ok(_root.Post($"/api/tenancy/organizations/{fp.OrgId}/apply-profile", new { code = "P-PK" }), "apply P-PK");
        await k.Ok(k.Post("/api/hrm/setup/payroll-cost-center", new { newCostCenter = new { code = HrmEnableKit.PayrollCostCenter, name = "Payroll" } }), "wizard");
        await k.Ok(k.Post("/api/tenant/modules/MODULE_HRM/enable", new { }), "enable HRM");

        // Off: gone. On: Finance and the GL work with nothing sold, bought or stocked.
        var enabled = await k.Ok(k.Get("/api/tenant/modules/enabled"), "enabled");
        var modules = enabled.A("modules").Select(m => m.GetString()).ToList();
        modules.Should().Contain("MODULE_HRM").And.Contain("MODULE_FINANCE").And.NotContain("MODULE_DEMAND").And.NotContain("MODULE_INVENTORY").And.NotContain("MODULE_INTEGRATION");
        (await k.Get("/api/hrm/settings")).Status.Should().Be(HttpStatusCode.OK);

        await k.EnableGlAsync();
        var setup = await k.Ok(k.Get("/api/finance/setup/status"), "setup");
        setup.S("status").Should().Be("Complete", "A-4: HR roles never move it");
        setup.A("hrRolesMissing").Select(r => r.GetString()).Should().Contain("DEFAULT_SALARY_EXPENSE");

        var codes = await k.DefaultCodesAsync();
        var defaults = await k.Ok(k.Get("/api/finance/accounting-defaults"), "defaults");
        var uuidOf = defaults.A("categories").SelectMany(c => c.A("items")).Where(i => i.P("account").ValueKind == JsonValueKind.Object)
            .ToDictionary(i => i.S("defaultKey")!, i => i.P("account").G("uuid"));
        await k.Ok(k.Put("/api/finance/accounting-defaults/DEFAULT_SALARY_EXPENSE", new { accountUuid = uuidOf["DEFAULT_PURCHASE"] }), "salary expense");
        await k.Ok(k.Put("/api/finance/accounting-defaults/DEFAULT_SALARY_PAYABLE", new { accountUuid = uuidOf["DEFAULT_PAYABLE"] }), "salary payable");

        var posted = await _f.PostDirectAsync(fp.OrgId, new FinancialTransaction
        {
            DocumentType = FinDocType.PayrollRun, SourceDocumentId = Guid.NewGuid(), SourceDocumentNumber = "PAY-G8", SourceModule = PostingSourceModules.Hrm,
            TransactionDate = new DateTime(Gl.Y, 6, 30), CurrencyCode = "PKR",
            Lines =
            [
                new() { LineNumber = 1, AccountRole = "DEFAULT_SALARY_EXPENSE", Debit = 500m, CostCenter = HrmEnableKit.PayrollCostCenter },
                new() { LineNumber = 2, AccountRole = "DEFAULT_SALARY_PAYABLE", Credit = 500m, CostCenter = HrmEnableKit.PayrollCostCenter }
            ]
        });
        posted.Status.Should().Be(PostingStatus.Success, posted.Describe());
        posted.TargetResults.Should().ContainSingle().Which.TargetCode.Should().Be(PostingTargetCodes.InternalGl, "P-PK has no QuickBooks");

        var tb = await k.TrialBalanceAsync(new DateTime(Gl.Y, 1, 1), new DateTime(Gl.Y, 12, 31));
        tb.B("isBalanced").Should().BeTrue(J.Short(tb));
        tb.D("totalDebit").Should().Be(500m);
        tb.TbRow(codes["DEFAULT_PURCHASE"]).D("periodDebit").Should().Be(500m);
    }

    [Fact]
    public async Task G19_a_P3_organization_has_no_target_and_that_is_not_an_error()
    {
        var o = await _root.OrgAsync("POSTP3", currency: false);
        await _root.Ok(_root.Post($"/api/tenancy/organizations/{o.OrgId}/apply-profile", new { code = "P3" }), "apply P3");
        await o.K.Ok(o.K.Post("/api/hrm/setup/payroll-cost-center", new { newCostCenter = new { code = HrmEnableKit.PayrollCostCenter, name = "Payroll" } }), "wizard");
        await o.K.Ok(o.K.Post("/api/tenant/modules/MODULE_HRM/enable", new { }), "enable HRM");

        var tx = Synthetic(FinDocType.PayrollRun, 100m, lines: [("DEFAULT_SALARY_EXPENSE", 100m, 0m), ("DEFAULT_SALARY_PAYABLE", 0m, 100m)]);
        var result = await _f.PostDirectAsync(o.OrgId, tx);
        result.Status.Should().BeOneOf(new[] { PostingStatus.NoTargetConfigured, PostingStatus.ModuleNotLicensed }, result.Describe());
    }

    [Fact]
    public async Task E1_setup_routes_seed_nothing_for_an_unlicensed_organization()
    {
        var o = await _root.OrgAsync("POSTSEED", currency: false);
        async Task<int> Rows(string table) => Convert.ToInt32((await _f.QueryAsync($"SELECT COUNT(*) AS N FROM hrm.{table} WHERE OrganizationId = @o", ("@o", o.OrgId))).Single()["N"]);

        (await o.K.Ok(o.K.Get("/api/hrm/setup/status"), "status")).B("licensed").Should().BeFalse();
        (await o.K.Post("/api/hrm/setup/payroll-cost-center", new { newCostCenter = new { code = "X", name = "X" } })).Status.Should().Be(HttpStatusCode.Forbidden);
        (await Rows("HrmSettings")).Should().Be(0);
        (await Rows("LeaveTypes")).Should().Be(0);
        (await Rows("CostCenters")).Should().Be(0);

        await _root.LicenceAsync(o.OrgId, ("MODULE_HRM", true));
        (await o.K.Ok(o.K.Get("/api/hrm/setup/status"), "status")).B("licensed").Should().BeTrue();
        (await Rows("HrmSettings")).Should().Be(1, "seeded once licensed");
    }

    [Fact]
    public async Task E13_a_service_invoice_posts_to_its_income_account_and_reaches_quickbooks_once_the_item_is_chosen()
    {
        var o = await (await _root.NewOrgAsync("POSTSVC")).ConnectLiveAsync();
        var k = o.K;
        await k.EnableGlAsync();
        var codes = await k.DefaultCodesAsync();
        var (_, _, customer, _) = await k.StockedWorldAsync("POSTSVC");

        var invoice = await k.Ok(k.Post("/api/sales-invoices/service", new
        {
            customerUuid = customer.Uuid, invoiceDate = new DateTime(Gl.Y, 6, 30).ToString("yyyy-MM-dd"),
            lines = new object[] { new { description = "Consulting", quantity = 2m, unitPrice = 500m, incomeAccountCode = codes["DEFAULT_REVENUE"] } }
        }), "service invoice");
        var id = invoice.G("uuid");
        await k.Ok(k.Post($"/api/sales-invoices/{id}/issue"), "issue");
        (await _f.JournalsAsync(o.OrgId, id)).Should().ContainSingle().Which.Net(codes["DEFAULT_REVENUE"]).Should().Be(-1000m);

        await _f.RunJobsAsync(2);
        var blocked = await _f.MapAsync(o.OrgId, id, "SalesInvoice");
        (blocked.State, blocked.LastErrorCode).Should().Be(("Blocked", "SERVICE_ITEM_MISSING"), blocked.ToString());

        await k.Ok(k.PutDefaults(("ServiceItemRef", "ITEM-77")), "service item");
        await _f.RunJobsAsync(4);
        var map = await _f.MapAsync(o.OrgId, id, "SalesInvoice");
        map.State.Should().Be("Synced", map.ToString());
        ((RemoteInvoice)_f.Remote(SyncKind.SalesInvoice, map.RemoteId)!.Entity!).Lines.Single().ItemId.Should().Be("ITEM-77");
    }

    [Fact]
    public async Task E13_the_guarded_migration_is_idempotent_on_LocalDB()
    {
        const string History = "DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE '%A49_ServiceInvoiceIncomeAccount'";
        async Task<bool> HasColumn() => (await _f.QueryAsync("SELECT COL_LENGTH('finance.sales_invoice_lines', 'IncomeAccountCode') AS N")).Single()["N"] is not (null or DBNull);
        Task MigrateAsync() => _f.RunInScopeAsync<SMS.Modules.Finance.Data.FinanceDbContext>(db => Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.MigrateAsync(db.Database));

        (await HasColumn()).Should().BeTrue();

        // The history row lost, the column there (the drifted shared database): nothing breaks.
        await _f.ExecuteAsync(History);
        await MigrateAsync();
        (await HasColumn()).Should().BeTrue();

        // The column missing: added.
        await _f.ExecuteAsync(History);
        await _f.ExecuteAsync("ALTER TABLE finance.sales_invoice_lines DROP COLUMN IncomeAccountCode");
        await MigrateAsync();
        (await HasColumn()).Should().BeTrue();
    }

    [Fact]
    public async Task E14_cnic_and_bank_numbers_are_encrypted_masked_and_gated_through_the_real_pipeline()
    {
        var o = await _root.OrgAsync("POSTIMP", currency: false);
        var k = o.K;
        await _root.EnableHrmAsync(o);
        const string Cnic = "42101-1234567-1", Bank = "0099887766554433";
        var csv = "name,identification_id,bank_name,bank_account_id,secret_note\n"
                + $"Sara Khan,{Cnic},HBL,{Bank},{Bank}\n"
                + $"Ali Raza,{Cnic},HBL,{Bank},x\n"
                + "Madonna,,,,\n";
        var session = await k.UploadOk("HRM_EMPLOYEE", System.Text.Encoding.UTF8.GetBytes(csv), "people.csv");
        var id = session.G("uuid");
        var validated = await k.ValidateOk(id);

        // At rest.
        var rows = await _f.QueryAsync("SELECT r.RawJson, r.ValuesJson, r.ErrorsJson FROM import.rows r JOIN import.sessions s ON s.Id = r.SessionId WHERE s.Uuid = @u", ("@u", id));
        rows.Should().HaveCount(3);
        var stored = string.Join("|", rows.SelectMany(r => r.Values).Select(v => v?.ToString()));
        stored.Should().NotContain(Bank).And.NotContain("1234567");

        // No read echoes them.
        var preview = await k.Ok(k.Get($"/api/import/sessions/{id}/preview"), "preview");
        var errors = await k.Ok(k.Get($"/api/import/sessions/{id}/errors"), "errors");
        foreach (var text in new[] { JsonSerializer.Serialize(validated), JsonSerializer.Serialize(preview), JsonSerializer.Serialize(errors) })
            text.Should().NotContain(Bank).And.NotContain("1234567");
        JsonSerializer.Serialize(preview).Should().Contain("*****").And.Contain("Sara Khan");
        JsonSerializer.Serialize(errors).Should().Contain("NAME_NEEDS_TWO_WORDS").And.Contain("DUPLICATE");

        foreach (var report in new[] { "errors", "results" })
        {
            var api = await k.Get($"/api/import/sessions/{id}/{report}/download");
            api.Status.Should().Be(HttpStatusCode.OK);
        }

        // IMPORT_MANAGE alone reads nothing.
        var manager = await k.LoginWithPermissionsAsync("impmgr", "IMPORT_MANAGE", "IMPORT_UPLOAD", "IMPORT_EXECUTE", "IMPORT_PROFILE_VIEW");
        foreach (var url in new[] { "preview", "errors", "errors/download", "results/download" })
            (await k.Get($"/api/import/sessions/{id}/{url}", manager)).ShouldBeForbidden(url);

        // The one good row still imports, decrypted for the target only.
        await k.ExecuteOk(id);
        var imported = await k.Ok(k.Get($"/api/hrm/employees?cnic={Cnic}"), "search by CNIC");
        imported.A("data").Should().ContainSingle().Which.S("fullName").Should().Be("Sara Khan");
    }

    [Fact]
    public async Task G15_hrm_rows_are_hidden_while_the_module_is_off()
    {
        var o = await (await _root.NewOrgAsync("POSTHID")).ConnectLiveAsync();
        (await o.K.SyncControlAllAsync()).Select(r => r.S("documentType")).Should().NotContain("PayrollRun");
    }

    [Fact]
    public async Task E16_profiles_list_and_apply_only_for_a_super_admin()
    {
        var o = await _root.OrgAsync("POSTPF", currency: false);
        var list = await o.K.Ok(o.K.Get("/api/tenant/module-profiles"), "profiles");
        JsonSerializer.Serialize(list).Should().Contain("P2-S").And.Contain("P-PK");

        (await o.K.Post($"/api/tenancy/organizations/{o.OrgId}/apply-profile", new { code = "P3" })).ShouldBeForbidden("an org admin is not a super admin");

        var applied = await _root.Ok(_root.Post($"/api/tenancy/organizations/{o.OrgId}/apply-profile", new { code = "P3" }), "apply P3");
        applied.A("licensedNotEnabled").Single().S("featureCode").Should().Be("MODULE_HRM");

        var enabled = await o.K.Ok(o.K.Get("/api/tenant/modules/enabled"), "enabled");
        enabled.A("modules").Select(m => m.GetString()).Should().NotContain("MODULE_DEMAND").And.NotContain("MODULE_FINANCE").And.NotContain("MODULE_HRM");
    }
}
