using System.Net;
using System.Text;
using FluentAssertions;
using SMS.Integration.Tests.DataImport;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;
using static SMS.Integration.Tests.GeneralLedger.Gl;

namespace SMS.Integration.Tests.GeneralLedger;

/// <summary>
/// A42 (QA) — external mappings, permissions, the feature switch and tenant isolation over HTTP on the real host (LocalDB):
/// <list type="bullet">
/// <item>T-EXT-01..05 — <see cref="Mappings_are_created_imported_exported_and_guarded"/>.</item>
/// <item>D-14 permissions + FEATURE_GENERAL_LEDGER — <see cref="Permissions_split_reading_account_writes_and_setup"/>,
/// <see cref="The_feature_switch_closes_every_GL_route_but_not_finance"/>.</item>
/// <item>T-COA-11 and per-org isolation of fiscal years, defaults, opening balances, setup status and mappings —
/// <see cref="Another_organization_sees_nothing_and_gets_404"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~GlAccessE2ETests</c>.</para>
/// </summary>
public sealed class GlAccessE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string Mappings = "/api/finance/external-mappings";

    private readonly SapKit _root;
    private readonly SapWebApplicationFactory _f;

    public GlAccessE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A42");
    }

    // ── T-EXT ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mappings_are_created_imported_exported_and_guarded()
    {
        var o = await _root.OrgAsync("EXT");
        var k = o.K;
        await k.ApplyAsync();
        var a = await k.AccountsAsync();

        // T-EXT-01 — create (types/directions case-insensitive, display name by default)
        var m = await k.Ok(k.Post(Mappings, new
        {
            systemType = "quickbooks_online", accountUuid = a["1112"].G("uuid"), externalAccountCode = "35", externalAccountName = "Cash on hand",
            syncDirection = "outbound", connectionConfigJson = "{\"realmId\":\"9130\",\"sandbox\":true}"
        }), "create a QBO mapping");
        (m.S("systemType"), m.S("systemName"), m.S("syncDirection"), m.P("account").S("accountCode"), m.S("externalAccountCode"), m.B("isActive"))
            .Should().Be(("QUICKBOOKS_ONLINE", "QuickBooks Online", "Outbound", "1112", "35", true));
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.ExternalSystemMappings WHERE OrganizationId = @o AND SystemType = 'QUICKBOOKS_ONLINE'",
            ("@o", o.OrgId))).Should().Be(1);

        // T-EXT-02 — one mapping per system + account; group, inactive, credentials refused
        (await k.Post(Mappings, new { systemType = "QUICKBOOKS_ONLINE", accountUuid = a["1112"].G("uuid"), externalAccountCode = "36" }))
            .ShouldFail(HttpStatusCode.Conflict, "duplicate", "This account is already mapped for QuickBooks Online.");
        await k.Ok(k.Post(Mappings, new { systemType = "XERO", accountUuid = a["1112"].G("uuid"), externalAccountCode = "090" }), "another system is fine");
        (await k.Post(Mappings, new { systemType = "XERO", accountUuid = a["111"].G("uuid"), externalAccountCode = "1" }))
            .ShouldFail(HttpStatusCode.BadRequest, "group", "Only Level 4 accounts can be mapped.");
        var temp = await k.Ok(k.CreateAccount(a["111"].G("uuid"), "1116", "Soon deleted", "Cash"), "a leaf to delete");
        await k.Ok(k.Delete($"{Chart}/{temp.G("uuid")}"), "delete it");
        (await k.Post(Mappings, new { systemType = "XERO", accountUuid = temp.G("uuid"), externalAccountCode = "1" }))
            .ShouldFail(HttpStatusCode.BadRequest, "inactive", "Mapped account must be active.");
        foreach (var config in new[] { "{\"clientSecret\":\"x\"}", "{\"auth\":{\"access-token\":\"x\"}}", "{\"list\":[{\"API_KEY\":\"x\"}]}", "{\"password\":1}" })
            (await k.Post(Mappings, new { systemType = "ODOO", accountUuid = a["1111"].G("uuid"), externalAccountCode = "1", connectionConfigJson = config }))
                .ShouldFail(HttpStatusCode.BadRequest, $"credentials {config}", "Connection settings must not contain credentials.");
        (await k.Post(Mappings, new { systemType = "ODOO", accountUuid = a["1111"].G("uuid"), externalAccountCode = "1", connectionConfigJson = "{bad" }))
            .ShouldFail(HttpStatusCode.BadRequest, "invalid json", "Connection settings must be valid JSON.");
        (await k.Post(Mappings, new { systemType = "NETSUITE", accountUuid = a["1111"].G("uuid"), externalAccountCode = "1" }))
            .ShouldFail(HttpStatusCode.BadRequest, "unknown system", "Unknown external system type");
        (await k.Put($"{Mappings}/{m.G("uuid")}", new { externalAccountCode = "35", syncDirection = "None", isActive = true, connectionConfigJson = "{\"token\":\"x\"}" }))
            .ShouldFail(HttpStatusCode.BadRequest, "credentials on update", "Connection settings must not contain credentials.");

        // T-EXT-03 — import 20 lines from CSV (+ bad lines reported, good lines saved)
        var leaves = a.Values.Where(x => !x.B("isGroup") && x.S("accountCode") != "1112" && x.S("accountCode") != "3900")
            .Select(x => x.S("accountCode")!).OrderBy(c => c, StringComparer.Ordinal).Take(20).ToList();
        leaves.Should().HaveCount(20);
        var sb = new StringBuilder("sms_account_code,external_code,external_name,direction\r\n");
        foreach (var (code, i) in leaves.Select((c, i) => (c, i))) sb.Append($"{code},QB-{i + 100},\"Name, {code}\",bidirectional\r\n");
        sb.Append("9999,X,,\r\n");            // line 22: unknown account
        sb.Append("111,X,,\r\n");             // line 23: group
        sb.Append($"{leaves[0]},X,,\r\n");    // line 24: twice in the file
        sb.Append("1116,X,,\r\n");            // line 25: inactive
        sb.Append($"1112,=SUM(A1),Formula,Inbound\r\n"); // line 26: updates the existing mapping
        var import = await k.Ok(k.ImportMappings(Encoding.UTF8.GetBytes(sb.ToString()), "QuickBooks_Online", "QBO Company"), "import");
        (import.I("created"), import.I("updated")).Should().Be((20, 1));
        import.A("errors").Select(e => $"{e.I("line")}:{e.S("message")}").Should().Equal(
            "22:Account 9999 does not exist.", "23:Only Level 4 accounts can be mapped.", $"24:Account {leaves[0]} appears more than once in the file.",
            "25:Mapped account must be active.");
        var qbo = (await k.Ok(k.Get($"{Mappings}?systemType=quickbooks_online"), "list QBO")).Items();
        qbo.Should().HaveCount(21);
        qbo.Select(x => x.P("account").S("accountCode")).Should().BeInAscendingOrder(StringComparer.Ordinal);
        qbo.Should().OnlyContain(x => x.S("systemName") == "QBO Company");
        qbo.Single(x => x.P("account").S("accountCode") == leaves[1]).S("externalAccountName").Should().Be($"Name, {leaves[1]}");
        (await k.ImportMappings(Encoding.UTF8.GetBytes("code,name\r\n1111,x\r\n"), "XERO")).ShouldFail(HttpStatusCode.BadRequest, "columns", "must have the columns");
        (await k.ImportMappings(Encoding.UTF8.GetBytes("sms_account_code,external_code\r\n1111,x\r\n"), "NOPE")).ShouldFail(HttpStatusCode.BadRequest, "type");

        // T-EXT-04 — export: columns, BOM, formula guard; it imports back (all updates)
        var export = await k.DownloadAsync($"{Mappings}/export?systemType=QUICKBOOKS_ONLINE");
        (export.Status, export.FileName).Should().Be((HttpStatusCode.OK, "external-mappings.csv"));
        var lines = Gl.CsvLines(export.Content);
        lines[0].Should().Be("sms_account_code,external_code,external_name,direction,system_type,system_name,is_active");
        lines.Should().HaveCount(22);
        lines.Should().Contain("1112,'=SUM(A1),Formula,Inbound,QUICKBOOKS_ONLINE,QBO Company,true");
        var back = await k.Ok(k.ImportMappings(export.Content, "QUICKBOOKS_ONLINE", "QBO Company"), "re-import the export");
        (back.I("created"), back.I("updated"), back.A("errors").Count).Should().Be((0, 21, 0));
        // D-58 "the export imports back": the formula guard's apostrophe must not end up in the data
        (await k.Ok(k.Get($"{Mappings}?systemType=QUICKBOOKS_ONLINE"), "after re-import")).Items()
            .Single(x => x.P("account").S("accountCode") == "1112").S("externalAccountCode").Should().Be("=SUM(A1)");

        // T-EXT-05 — a mapped account cannot be deleted; deactivate / delete the mapping
        var mapped = qbo.Single(x => x.P("account").S("accountCode") == "1111");
        (await k.Delete($"{Chart}/{a["1111"].G("uuid")}")).ShouldFail(HttpStatusCode.Conflict, "mapped", "This account is mapped to QBO Company. Remove the mapping first.");
        var off = await k.Ok(k.Put($"{Mappings}/{mapped.G("uuid")}", new { externalAccountCode = "QB-1", syncDirection = "None", isActive = false }), "deactivate");
        off.B("isActive").Should().BeFalse();
        await k.Ok(k.Delete($"{Mappings}/{mapped.G("uuid")}"), "delete the mapping");
        await k.Ok(k.Delete($"{Chart}/{a["1111"].G("uuid")}"), "now the account can go");
        (await k.Ok(k.Get(Mappings), "all")).Items().Should().HaveCount(21, "20 QBO + 1 Xero");
    }

    // ── Permissions / feature ────────────────────────────────────────────────────

    [Fact]
    public async Task Permissions_split_reading_account_writes_and_setup()
    {
        var o = await _root.OrgAsync("PRM");
        var k = o.K;
        await k.ApplyAsync();
        var a = await k.AccountsAsync();
        var year = await k.CreateYearOk(Y);
        var mapping = await k.Ok(k.Post(Mappings, new { systemType = "XERO", accountUuid = a["1113"].G("uuid"), externalAccountCode = "090" }), "a mapping");

        var viewer = await k.LoginWithPermissionsAsync("glview", View);
        var accounts = await k.LoginWithPermissionsAsync("glacct", ManageAccounts);
        var nobody = await k.LoginWithPermissionsAsync("nogl", "SALE_ORDER_VIEW", "INVOICE_VIEW");

        string[] reads =
        [
            "/api/finance/templates", "/api/finance/templates/filters", Chart, $"{Chart}/flat", $"{Chart}/{a["1112"].G("uuid")}",
            $"{Chart}/{a["11"].G("uuid")}/subtree", $"{Chart}/export?format=csv", "/api/finance/fiscal-years", $"/api/finance/fiscal-years/{year.G("uuid")}/periods",
            "/api/finance/accounting-defaults", "/api/finance/opening-balances", "/api/finance/opening-balances/summary", "/api/finance/setup/status",
            "/api/finance/setup/history", Mappings, $"{Mappings}/export"
        ];
        var p1 = year.A("periods")[0].G("uuid");
        var setupWrites = new (string Method, string Url, object? Body)[]
        {
            ("POST", $"{Chart}/apply-template", new { templateUuid = (await k.TemplateAsync("US_GENERAL")).G("uuid") }),
            ("POST", $"{Chart}/start-blank", null),
            ("POST", "/api/finance/fiscal-years", new { startDate = Day(Y + 1, 1, 1), endDate = Day(Y + 1, 12, 31) }),
            ("PUT", $"/api/finance/fiscal-periods/{p1}", new { status = "Locked" }),
            ("POST", $"/api/finance/fiscal-years/{year.G("uuid")}/close", null),
            ("PUT", "/api/finance/accounting-defaults/DEFAULT_CASH", new { accountUuid = a["1111"].G("uuid") }),
            ("PUT", "/api/finance/accounting-defaults", new { items = Array.Empty<object>() }),
            ("PUT", "/api/finance/opening-balances/batch", new { entries = Array.Empty<object>() }),
            ("POST", "/api/finance/opening-balances/post", new { skip = true }),
            ("POST", "/api/finance/setup/complete", null),
            ("POST", "/api/finance/setup/reset", null),
            ("POST", Mappings, new { systemType = "XERO", accountUuid = a["1111"].G("uuid"), externalAccountCode = "1" }),
            ("PUT", $"{Mappings}/{mapping.G("uuid")}", new { externalAccountCode = "091", isActive = true }),
            ("DELETE", $"{Mappings}/{mapping.G("uuid")}", null),
            ("DELETE", $"/api/finance/templates/{(await k.TemplateAsync("US_GENERAL")).G("uuid")}", null),
        };
        Task<Api> Send(string method, string url, object? body, HttpClient c) => k.Send(new HttpMethod(method), url, body, c);

        // GL_VIEW: every read, no write
        foreach (var url in reads) (await k.Get(url, viewer)).Status.Should().Be(HttpStatusCode.OK, $"GL_VIEW reads {url}");
        (await k.Ok(k.Post("/api/finance/accounting-defaults/validate", null, viewer), "validate is a read")).Has("isValid").Should().BeTrue();
        (await k.CreateAccount(a["111"].G("uuid"), "1117", "Viewer", "Cash", viewer)).ShouldBeForbidden("GL_VIEW cannot add accounts");
        (await k.Put($"{Chart}/{a["1112"].G("uuid")}", new { accountName = "x" }, viewer)).ShouldBeForbidden("GL_VIEW cannot edit accounts");
        (await k.Delete($"{Chart}/{a["1111"].G("uuid")}", viewer)).ShouldBeForbidden("GL_VIEW cannot delete accounts");
        (await k.UploadCoa(Bytes(ValidCsvLines()), "v.csv", viewer)).ShouldBeForbidden("GL_VIEW cannot upload");
        foreach (var (method, url, body) in setupWrites) (await Send(method, url, body, viewer)).ShouldBeForbidden($"GL_VIEW: {method} {url}");

        // GL_ACCOUNT_MANAGE: reads + account CRUD, nothing of the setup
        foreach (var url in reads) (await k.Get(url, accounts)).Status.Should().Be(HttpStatusCode.OK, $"GL_ACCOUNT_MANAGE reads {url}");
        var added = await k.Ok(k.CreateAccount(a["111"].G("uuid"), "1117", "By the account manager", "Cash", accounts), "add");
        await k.Ok(k.Put($"{Chart}/{added.G("uuid")}", new { accountName = "Renamed by the account manager", accountType = "Cash" }, accounts), "edit");
        await k.Ok(k.Delete($"{Chart}/{added.G("uuid")}", accounts), "delete");
        (await k.UploadCoa(Bytes(ValidCsvLines()), "a.csv", accounts)).ShouldBeForbidden("GL_ACCOUNT_MANAGE cannot upload");
        foreach (var (method, url, body) in setupWrites) (await Send(method, url, body, accounts)).ShouldBeForbidden($"GL_ACCOUNT_MANAGE: {method} {url}");

        // no GL permission (even with INVOICE_VIEW granted after start-up): nothing
        foreach (var url in reads.Append("/api/finance/gl/dashboard")) (await k.Get(url, nobody)).ShouldBeForbidden($"no GL permission: {url}");
        (await k.Get("/api/finance/gl/dashboard", nobody)).Raw.Should().NotContain("FINANCE_SETUP_REQUIRED", "the permission check runs before the setup gate");
        (await k.CreateAccount(a["111"].G("uuid"), "1118", "Nobody", "Cash", nobody)).ShouldBeForbidden("no GL permission");

        // GL_SETUP_MANAGE alone does everything (the org admin above did); and nothing was changed by the refused calls
        (await k.StatusAsync()).S("status").Should().Be("DefaultsSet");
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.FiscalYears WHERE OrganizationId = @o", ("@o", o.OrgId))).Should().Be(1);
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.ExternalSystemMappings WHERE OrganizationId = @o", ("@o", o.OrgId))).Should().Be(1);
        var setup = await k.LoginWithPermissionsAsync("glsetup", ManageSetup);
        await k.Ok(k.Put($"/api/finance/fiscal-periods/{p1}", new { status = "Locked" }, setup), "GL_SETUP_MANAGE locks a period");
        await k.Ok(k.CreateAccount(a["111"].G("uuid"), "1118", "By the setup manager", "Cash", setup), "GL_SETUP_MANAGE also manages accounts");
    }

    [Fact]
    public async Task The_feature_switch_closes_every_GL_route_but_not_finance()
    {
        var o = await _root.OrgAsync("FEAT");
        var k = o.K;
        await k.ApplyAsync();

        var a = await k.AccountsAsync();

        // licence removed from an org that had the feature: A37 D-6 read-only — reads pass, every write is refused
        await _root.LicenceAsync(o.OrgId, (Feature, false));
        await _f.InvalidateAsync(o.OrgId);
        foreach (var url in new[] { "/api/finance/templates", Chart, "/api/finance/fiscal-years", "/api/finance/setup/status", Mappings })
            (await k.Get(url)).Status.Should().Be(HttpStatusCode.OK, $"read-only after the licence is removed: {url}");
        foreach (var (method, url, body) in new (string, string, object?)[]
                 {
                     ("POST", $"{Chart}/start-blank", null),
                     ("POST", Chart, new { parentUuid = a["111"].G("uuid"), accountCode = "1117", accountName = "x", accountType = "Cash" }),
                     ("PUT", $"{Chart}/{a["1112"].G("uuid")}", new { accountName = "x" }),
                     ("DELETE", $"{Chart}/{a["1111"].G("uuid")}", null),
                     ("POST", "/api/finance/fiscal-years", new { startDate = Day(Y, 1, 1), endDate = Day(Y, 12, 31) }),
                     ("PUT", "/api/finance/opening-balances/batch", new { entries = new[] { new { accountUuid = a["1111"].G("uuid"), debitAmount = 1m, creditAmount = 0m } } }),
                     ("POST", "/api/finance/setup/reset", null),
                     ("POST", Mappings, new { systemType = "XERO", accountUuid = a["1111"].G("uuid"), externalAccountCode = "1" }),
                 })
        {
            var r = await k.Send(new HttpMethod(method), url, body);
            r.ShouldBeForbidden($"feature off: {method} {url}");
            r.Body.S("errorCode").Should().Be("MODULE_NOT_LICENSED", url);
            r.Body.P("result").S("module").Should().Be(Feature, url);
        }
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.FiscalYears WHERE OrganizationId = @o", ("@o", o.OrgId))).Should().Be(0);
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.ChartOfAccounts WHERE OrganizationId = @o AND IsActive = 1", ("@o", o.OrgId))).Should().Be(a.Count);
        foreach (var url in new[] { "/api/finance/invoices", "/api/finance/tax-codes", "/api/finance/opening-balance-date" })
            (await k.Get(url)).Status.Should().Be(HttpStatusCode.OK, $"{url} does not depend on the GL feature");

        await _root.LicenceAsync(o.OrgId, (Feature, true));
        await _f.InvalidateAsync(o.OrgId);
        (await k.Ok(k.Get(Chart), "back on")).A("roots").Should().HaveCount(5, "the chart survived the switch");
        await k.CreateYearOk(Y);

        // an organization that never had the feature (Basic plan, D-36): every GL route, reads included, is refused
        var basic = (await _root.OrgAsync("FEATB", plan: "BASIC", currency: false)).K;
        foreach (var url in new[] { "/api/finance/templates", Chart, "/api/finance/fiscal-years", "/api/finance/setup/status", Mappings, "/api/finance/gl/dashboard" })
        {
            var r = await basic.Get(url);
            r.ShouldBeForbidden($"never licensed: {url}");
            r.Body.S("errorCode").Should().Be("MODULE_NOT_LICENSED", url);
        }
        (await basic.Post($"{Chart}/start-blank")).ShouldBeForbidden("never licensed: write");
    }

    // ── T-COA-11 + isolation ─────────────────────────────────────────────────────

    [Fact]
    public async Task Another_organization_sees_nothing_and_gets_404()
    {
        var orgA = await _root.OrgAsync("ISA");
        var orgB = await _root.OrgAsync("ISB");
        var (ka, kb) = (orgA.K, orgB.K);

        // org A: chart, year, a draft, a mapping, setup history
        var accountsA = await ka.ReadyForBalancesAsync();
        Guid A(string code) => accountsA[code].G("uuid");
        var yearA = (await ka.Ok(ka.Get("/api/finance/fiscal-years"), "A's years")).Items().Single();
        await ka.Ok(ka.Batch((A("1111"), 100m, 0m), (A("3100"), 0m, 100m)), "A's drafts");
        var mapA = await ka.Ok(ka.Post(Mappings, new { systemType = "SAGE", accountUuid = A("1112"), externalAccountCode = "1200" }), "A's mapping");

        // B before doing anything: empty
        var treeB = await kb.Ok(kb.Get(Chart), "B's tree");
        (treeB.A("roots").Count, treeB.P("summary").I("totalAccounts")).Should().Be((0, 0));
        (await kb.Ok(kb.Get($"{Chart}/flat"), "B flat")).Items().Should().BeEmpty();
        (await kb.Ok(kb.Get("/api/finance/fiscal-years"), "B years")).Items().Should().BeEmpty();
        (await kb.Ok(kb.Get(Mappings), "B mappings")).Items().Should().BeEmpty();
        Gl.CsvLines((await kb.DownloadAsync($"{Mappings}/export")).Content).Should().HaveCount(1, "header only");
        var obB = await kb.Ok(kb.Get("/api/finance/opening-balances"), "B opening");
        (obB.A("groups").Sum(g => g.A("rows").Count), obB.P("summary").I("entryCount"), obB.IsNull("conversionDate")).Should().Be((0, 0, true));
        var statusB = await kb.StatusAsync();
        (statusB.S("status"), statusB.I("accountsCreatedCount")).Should().Be(("NotStarted", 0));
        (await kb.Ok(kb.Get("/api/finance/setup/history"), "B history")).Items().Should().BeEmpty();
        var defaultsB = await kb.Ok(kb.Get("/api/finance/accounting-defaults"), "B defaults");
        defaultsB.I("assignedRequiredCount").Should().Be(0);
        defaultsB.A("categories").SelectMany(c => c.A("items")).Should().OnlyContain(i => i.IsNull("account"));
        // D-37 lazy init is idempotent: a second read rewrites nothing
        const string stamp = "SELECT COUNT(*) AS N, MAX(ModifiedAt) AS M FROM finance.AccountingDefaults WHERE OrganizationId = @o";
        var first = (await _f.QueryAsync(stamp, ("@o", orgB.OrgId))).Single();
        Convert.ToInt32(first["N"]).Should().Be(16);
        await Task.Delay(50);
        await kb.Ok(kb.Get("/api/finance/accounting-defaults"), "B defaults again");
        await kb.StatusAsync();
        (await _f.QueryAsync(stamp, ("@o", orgB.OrgId))).Single()["M"].Should().Be(first["M"], "nothing is re-saved on later reads");
        Gl.CsvLines((await kb.DownloadAsync($"{Chart}/export?format=csv")).Content).Should().Equal(Gl.CsvHeader);

        // B on A's uuids: 404 everywhere, nothing changed
        (await kb.Get($"{Chart}/{A("1111")}")).ShouldFail(HttpStatusCode.NotFound, "read A's account", "Account not found.");
        (await kb.Get($"{Chart}/{A("11")}/subtree")).ShouldFail(HttpStatusCode.NotFound, "A's subtree");
        (await kb.Get($"{Chart}/{A("1112")}/ancestors")).ShouldFail(HttpStatusCode.NotFound, "A's ancestors");
        (await kb.Put($"{Chart}/{A("1111")}", new { accountName = "Hijacked" })).ShouldFail(HttpStatusCode.NotFound, "edit A's account");
        (await kb.Delete($"{Chart}/{A("1114")}")).ShouldFail(HttpStatusCode.NotFound, "delete A's account");
        (await kb.CreateAccount(A("111"), "1119", "Under A's group", "Cash")).ShouldFail(HttpStatusCode.NotFound, "A's parent", "Parent account not found.");
        (await kb.Get($"{Chart}/suggest-code?parentUuid={A("111")}")).ShouldFail(HttpStatusCode.NotFound, "suggest under A's parent");
        (await kb.Get($"/api/finance/fiscal-years/{yearA.G("uuid")}")).ShouldFail(HttpStatusCode.NotFound, "A's year");
        (await kb.Get($"/api/finance/fiscal-years/{yearA.G("uuid")}/periods")).ShouldFail(HttpStatusCode.NotFound, "A's periods");
        (await kb.Put($"/api/finance/fiscal-years/{yearA.G("uuid")}", new { fiscalYearName = "Hijacked" })).ShouldFail(HttpStatusCode.NotFound, "edit A's year");
        (await kb.Post($"/api/finance/fiscal-years/{yearA.G("uuid")}/close")).ShouldFail(HttpStatusCode.NotFound, "close A's year");
        (await kb.Put($"/api/finance/fiscal-periods/{yearA.A("periods")[0].G("uuid")}", new { status = "Closed" })).ShouldFail(HttpStatusCode.NotFound, "A's period");
        (await kb.SetDefault("DEFAULT_CASH", A("1112"))).ShouldFail(HttpStatusCode.BadRequest, "A's account as B's default", "Account not found.");
        (await kb.Batch((A("1112"), 5m, 0m))).ShouldFail(HttpStatusCode.BadRequest, "A's account in B's opening balances", "Account not found.");
        (await kb.Post(Mappings, new { systemType = "SAGE", accountUuid = A("1113"), externalAccountCode = "1" })).ShouldFail(HttpStatusCode.NotFound, "map A's account", "Account not found.");
        (await kb.Put($"{Mappings}/{mapA.G("uuid")}", new { externalAccountCode = "X", isActive = true })).ShouldFail(HttpStatusCode.NotFound, "edit A's mapping");
        (await kb.Delete($"{Mappings}/{mapA.G("uuid")}")).ShouldFail(HttpStatusCode.NotFound, "delete A's mapping");

        // B's own setup actions never touch A: B applies, posts, resets
        await kb.ApplyAsync("GLOBAL_GENERAL");
        await kb.Ok(kb.Post("/api/finance/opening-balances/post", new { skip = true }), "B skips");
        await kb.Ok(kb.Post("/api/finance/setup/reset"), "B resets");

        var stillA = await ka.AccountsAsync();
        stillA.Keys.Should().BeEquivalentTo(accountsA.Keys);
        stillA["1111"].S("accountName").Should().Be(accountsA["1111"].S("accountName"));
        (await ka.Ok(ka.Get("/api/finance/fiscal-years"), "A's years")).Items().Single().S("fiscalYearName").Should().Be(yearA.S("fiscalYearName"));
        (await ka.Ok(ka.Get("/api/finance/opening-balances/summary"), "A's summary")).I("entryCount").Should().Be(2);
        (await ka.Ok(ka.Get(Mappings), "A's mappings")).Items().Single().S("externalAccountCode").Should().Be("1200");
        (await ka.Ok(ka.Get("/api/finance/accounting-defaults"), "A's defaults")).I("assignedRequiredCount").Should().Be(8);
        var statusA = await ka.StatusAsync();
        (statusA.S("status"), statusA.I("accountsCreatedCount")).Should().Be(("DefaultsSet", accountsA.Count));
        (await ka.Ok(ka.Get("/api/finance/setup/history"), "A's history")).Items().Should().NotContain(h => h.S("summary")!.Contains("Setup reset"));
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.ChartOfAccounts WHERE OrganizationId = @o", ("@o", orgB.OrgId))).Should().Be(0, "B reset its own chart");
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.ChartOfAccounts WHERE OrganizationId = @o", ("@o", orgA.OrgId))).Should().Be(accountsA.Count);

        // A's opening date lock (once posted) does not touch B's date
        await ka.Ok(ka.PostOpening(), "A posts");
        (await ka.SetOpeningDate(Day(Y, 2, 1))).Status.Should().Be(HttpStatusCode.Conflict);
        await kb.Ok(kb.SetOpeningDate(Day(Y, 2, 1)), "B's date is free");
    }
}
