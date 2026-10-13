using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.DataImport;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;

namespace SMS.Integration.Tests.GeneralLedger;

/// <summary>
/// A42 (QA) — templates, chart of accounts uploads and the chart itself over HTTP on the real host (LocalDB):
/// <list type="bullet">
/// <item>T-TPL-01..05 — <see cref="Templates_list_download_preview_and_are_read_only"/>.</item>
/// <item>T-UPL-01..10 + all errors at once — <see cref="Uploads_validate_every_rule_and_report_every_problem"/>,
/// <see cref="A_json_upload_round_trips_a_template_with_its_defaults"/>, <see cref="Two_thousand_and_one_rows_are_refused_R10"/>.</item>
/// <item>T-COA-01..10 — <see cref="Applying_US_GENERAL_builds_paths_levels_and_roots"/>, <see cref="Accounts_are_added_and_deleted_by_the_rules"/>,
/// <see cref="Export_round_trips_and_subtree_lists_descendants"/>, <see cref="A_blank_chart_is_built_level_by_level"/>.</item>
/// <item>T-COA-11 tenant isolation — GlAccessE2ETests.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~GlChartE2ETests</c>.</para>
/// </summary>
public sealed class GlChartE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;
    private readonly SapWebApplicationFactory _f;

    public GlChartE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A42");
    }

    // ── T-TPL ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Templates_list_download_preview_and_are_read_only()
    {
        var k = (await _root.OrgAsync("TPL")).K;

        // T-TPL-01 — USA: the 4 US templates, all system
        var us = (await k.Ok(k.Get("/api/finance/templates?countryCode=USA"), "USA templates")).Items();
        us.Select(t => t.S("templateCode")).Should().BeEquivalentTo("US_GENERAL", "US_RETAIL", "US_MANUFACTURING", "US_SERVICES");
        us.Should().OnlyContain(t => t.B("isSystem") && t.S("countryCode") == "USA");
        (await k.Ok(k.Get("/api/finance/templates?countryCode=GLOBAL"), "global")).Items().Select(t => t.S("templateCode"))
            .Should().BeEquivalentTo("GLOBAL_GENERAL", "GLOBAL_SERVICES", "GLOBAL_NONPROFIT");
        (await k.Ok(k.Get("/api/finance/templates"), "all")).Items().Should().HaveCount(16);
        var filters = await k.Ok(k.Get("/api/finance/templates/filters"), "filters");
        filters.A("countries").Select(c => c.S("code")).Should().Contain(new[] { "USA", "PAK", "GBR" });

        var general = us.Single(t => t.S("templateCode") == "US_GENERAL");
        var uuid = general.G("uuid");
        var count = general.I("accountCount");
        count.Should().BeGreaterThan(60);

        // T-TPL-02 — CSV download: file name, BOM, header, the 5 roots
        var csv = await k.DownloadAsync($"/api/finance/templates/{uuid}/download?format=csv");
        csv.Status.Should().Be(HttpStatusCode.OK);
        csv.FileName.Should().Be("US_GENERAL.csv");
        var lines = Gl.CsvLines(csv.Content);
        lines[0].Should().Be(Gl.CsvHeader);
        lines.Should().HaveCount(count + 1, "one line per template account");
        var roots = lines.Skip(1).Select(l => l.Split(',')).Where(c => c[3] == "1").Select(c => $"{c[0]}:{c[4]}:{c[2]}:{c[5]}").ToList();
        roots.Should().Equal("1:Asset::true", "2:Liability::true", "3:Equity::true", "4:Revenue::true", "5:Expense::true");

        // T-TPL-03 — JSON download: the §5.4 tree with the 5 roots and the default markers
        var json = await k.DownloadAsync($"/api/finance/templates/{uuid}/download?format=json");
        json.FileName.Should().Be("US_GENERAL.json");
        using (var doc = JsonDocument.Parse(json.Content))
        {
            var accounts = doc.RootElement.GetProperty("accounts").EnumerateArray().ToList();
            accounts.Select(a => $"{a.GetProperty("code").GetString()}:{a.GetProperty("root_type").GetString()}:{a.GetProperty("level").GetInt32()}")
                .Should().Equal("1:Asset:1", "2:Liability:1", "3:Equity:1", "4:Revenue:1", "5:Expense:1");
            doc.RootElement.GetProperty("template_code").GetString().Should().Be("US_GENERAL");
            json.Content.Should().NotBeEmpty();
            Encoding.UTF8.GetString(json.Content).Should().Contain("\"default_account_key\": \"DEFAULT_RECEIVABLE\"");
        }
        (await k.Get($"/api/finance/templates/{uuid}/download?format=xml")).ShouldFail(HttpStatusCode.BadRequest, "format", "Format must be csv or json.");
        var blank = await k.DownloadAsync("/api/finance/templates/blank?format=csv");
        (blank.FileName, Gl.CsvLines(blank.Content).Single()).Should().Be(("coa-template.csv", Gl.CsvHeader));

        // T-TPL-04 — a system template is read-only
        (await k.Delete($"/api/finance/templates/{uuid}")).ShouldFail(HttpStatusCode.Forbidden, "system template", "System templates are read-only.");
        (await k.Ok(k.Get("/api/finance/templates?countryCode=USA"), "still listed")).Items().Should().HaveCount(4);

        // T-TPL-05 — preview: flat, every account, ordered by path; detail: the tree
        var preview = (await k.Ok(k.Get($"/api/finance/templates/{uuid}/preview"), "preview")).Items();
        preview.Should().HaveCount(count);
        preview.Count(p => p.I("level") == 1).Should().Be(5);
        preview.Count(p => p.B("isDefaultAccount")).Should().Be(16, "US_GENERAL marks every default");
        preview.Single(p => p.S("accountCode") == "1112").S("parentCode").Should().Be("111");
        (await k.Ok(k.Get($"/api/finance/templates/{uuid}"), "detail")).A("accounts").Should().HaveCount(5);

        // a template still waiting for its accounts cannot be applied
        var retail = us.Single(t => t.S("templateCode") == "US_RETAIL");
        retail.I("accountCount").Should().Be(0);
        (await k.Post($"{Gl.Chart}/apply-template", new { templateUuid = retail.G("uuid") })).ShouldFail(HttpStatusCode.Conflict, "no items",
            "Accounts for this template are coming. Choose another template or upload a file.");
    }

    // ── T-UPL ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Uploads_validate_every_rule_and_report_every_problem()
    {
        var o = await _root.OrgAsync("UPL");
        var k = o.K;

        // T-UPL-01 — a valid CSV creates the chart (+ 39/390/3900, D-9)
        var ok = await k.Ok(k.UploadCoa(Gl.Bytes(Gl.ValidCsvLines()), "legacy.csv"), "valid csv");
        (ok.I("accountsCreated"), ok.I("defaultsAssigned"), ok.S("status")).Should().Be((23, 1, "AccountsCreated"));
        var rows = await _f.QueryAsync("SELECT AccountCode, MaterializedPath, Level, IsSystemAccount FROM finance.ChartOfAccounts WHERE OrganizationId = @o AND IsActive = 1",
            ("@o", o.OrgId));
        rows.Should().HaveCount(23);
        rows.Single(r => (string)r["AccountCode"]! == "3900")["MaterializedPath"].Should().Be("/3/39/390/3900");
        rows.Single(r => (string)r["AccountCode"]! == "4111")["MaterializedPath"].Should().Be("/4/41/411/4111");
        rows.Where(r => (bool)r["IsSystemAccount"]!).Select(r => (string)r["AccountCode"]!).Should().BeEquivalentTo("1", "2", "3", "4", "5", "3900");

        // R1 — wrong header, other extension, no file
        var badHeader = await k.UploadCoa(Gl.Bytes(["code,name", "1,Assets"]), "c.csv");
        badHeader.ShouldFail(HttpStatusCode.BadRequest, "header", "Invalid file format.");
        badHeader.Rules().Should().Equal("1:R1");
        (await k.UploadCoa(Gl.Bytes(Gl.ValidCsvLines()), "c.txt")).Rules().Should().Equal(":R1");
        (await k.UploadCoa([], "c.csv")).ShouldFail(HttpStatusCode.BadRequest, "empty", "Choose a CSV or JSON file.");
        var huge = new byte[5 * 1024 * 1024 + 1];
        Array.Fill(huge, (byte)' ');
        (await k.UploadCoa(huge, "huge.csv")).ShouldFail(HttpStatusCode.BadRequest, "over 5 MB", "The file must be 5 MB or smaller.");

        // T-UPL-02 — R2 missing root
        var noEquity = await k.UploadCoa(Gl.Bytes(Gl.ValidCsvLines().Where(l => !l.Contains(",Equity,"))), "c.csv");
        noEquity.Status.Should().Be(HttpStatusCode.BadRequest);
        noEquity.Body.A("errors").Should().ContainSingle(e => e.S("rule") == "R2").Which.S("message").Should()
            .Be("Missing root type: Equity. All five root types (Asset, Liability, Equity, Revenue, Expense) are required.");

        // T-UPL-03..09, 11, 12 + "all errors at once": every bad line reported, sorted, nothing imported
        var lines = Gl.ValidCsvLines();
        var first = lines.Count + 1;
        lines.AddRange(
        [
            Gl.Row("1111", "Duplicate", "111", 4, "Asset", false, "Cash"),            // R3
            Gl.Row("6", "Sixth root", null, 1, "Asset", true),                         // R6
            Gl.Row("1199", "Level five", "111", 5, "Asset", false),                    // R4
            Gl.Row("1112", "Orphan", "999", 4, "Asset", false, "Cash"),               // R5 (no parent)
            Gl.Row("113", "Level 3 under level 1", "1", 3, "Asset", true),            // R5 (parent not one level above)
            Gl.Row("1113", "Leaf as group", "111", 4, "Asset", true),                 // R7
            Gl.Row("112", "Group as leaf", "11", 3, "Asset", false),                  // R8
            Gl.Row("1114", "Wrong root", "111", 4, "Liability", false, "Payable"),    // R9
            Gl.Row("11-5", "Bad characters", "111", 4, "Asset", false, "Cash"),       // R11
            Gl.Row("1116", "", "111", 4, "Asset", false, "Cash"),                     // R12
            Gl.Row("1117", "Type on asset", "111", 4, "Asset", false, "Payable"),     // R9 (D-23)
        ]);
        var many = await k.UploadCoa(Gl.Bytes(lines), "many.csv");
        many.Status.Should().Be(HttpStatusCode.BadRequest, many.ToString());
        many.Body.B("success").Should().BeFalse();
        var errors = many.Body.A("errors");
        many.Message.Should().Be($"The file has {errors.Count} problems. Nothing was imported.");
        var rules = many.Rules();
        rules.Should().Contain(new[]
        {
            $"{first}:R3", $"{first + 1}:R6", $"{first + 2}:R4", $"{first + 3}:R5", $"{first + 4}:R5", $"{first + 5}:R7", $"{first + 6}:R8",
            $"{first + 7}:R9", $"{first + 8}:R11", $"{first + 9}:R12", $"{first + 10}:R9"
        });
        errors.Select(e => e.I("line")).Should().BeInAscendingOrder("sorted by line");
        errors.Single(e => e.I("line") == first).S("message").Should().Be($"Duplicate account code: 1111 at line {first}.");
        errors.Single(e => e.I("line") == first + 4).S("message").Should().Be("Account 113 references parent 1 which is not found or not at level 2.");
        errors.Where(e => e.I("line") == first + 7).Select(e => e.S("message")).Should()
            .Contain("Account 1114 must start with 2, the code of its root type (Liability).");
        errors.Single(e => e.I("line") == first + 10).S("message").Should().Be("Account type Payable is not valid for Asset accounts.");
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.ChartOfAccounts WHERE OrganizationId = @o AND IsActive = 1", ("@o", o.OrgId)))
            .Should().Be(23, "a refused upload changes nothing");

        // JSON problems carry the node path
        var badJson = Encoding.UTF8.GetBytes("""
            { "accounts": [ { "code": "1", "name": "Assets", "root_type": "Asset", "level": 1, "is_group": true,
                "children": [ { "code": "21", "name": "Wrong digit", "root_type": "Asset", "level": 2, "is_group": true } ] } ] }
            """);
        var jr = await k.UploadCoa(badJson, "bad.json");
        jr.Status.Should().Be(HttpStatusCode.BadRequest);
        jr.Body.A("errors").Should().Contain(e => e.S("path") == "accounts[0].children[0]" && e.S("rule") == "R9");
        jr.Body.A("errors").Count(e => e.S("rule") == "R2").Should().Be(4);

        // T-UPL-10 / R13 — once a non-opening journal is posted the chart cannot be replaced (upload 400, apply 409)
        await _f.InsertManualJournalAsync(o.OrgId);
        var r13 = await k.UploadCoa(Gl.Bytes(Gl.ValidCsvLines()), "again.csv");
        r13.Status.Should().Be(HttpStatusCode.BadRequest);
        r13.Body.A("errors").Should().ContainSingle().Which.S("rule").Should().Be("R13");
        r13.Message.Should().Be("Chart of Accounts cannot be replaced after journal entries have been posted. Add or modify individual accounts instead.");
        (await k.Post($"{Gl.Chart}/apply-template", new { templateUuid = (await k.TemplateAsync("US_GENERAL")).G("uuid") }))
            .ShouldFail(HttpStatusCode.Conflict, "apply after journals", "Chart of Accounts cannot be replaced after journal entries have been posted.");
        (await k.Post($"{Gl.Chart}/start-blank")).ShouldFail(HttpStatusCode.Conflict, "start-blank after journals", "cannot be replaced");
        // individual accounts can still be added
        await k.Ok(k.CreateAccount(await k.AccountAsync("111"), "1112", "Petty cash", "Cash"), "add an account after journals");
    }

    [Fact]
    public async Task A_json_upload_round_trips_a_template_with_its_defaults()
    {
        var o = await _root.OrgAsync("UPJ");
        var k = o.K;
        var uuid = (await k.TemplateAsync("US_GENERAL")).G("uuid");
        var json = (await k.DownloadAsync($"/api/finance/templates/{uuid}/download?format=json")).Content;

        var r = await k.Ok(k.UploadCoa(json, "US_GENERAL.json"), "upload the template's JSON");
        (r.I("accountsCreated"), r.I("defaultsAssigned"), r.S("status")).Should().Be(((await k.TemplateAsync("US_GENERAL")).I("accountCount"), 16, "AccountsCreated"));
        var status = await k.StatusAsync();
        status.IsNull("templateUuid").Should().BeTrue("an upload is not a template apply");
        (await k.Ok(k.Get("/api/finance/accounting-defaults"), "defaults")).I("assignedRequiredCount").Should().Be(8);

        // a flat JSON list with parent_code works too
        var flat = Encoding.UTF8.GetBytes(
            "{ \"accounts\": [" + string.Join(",", Gl.ValidCsvLines().Skip(1).Select(l => l.Split(',')).Select(c =>
                $"{{\"code\":\"{c[0]}\",\"name\":\"{c[1]}\",\"parent_code\":{(c[2].Length == 0 ? "null" : $"\"{c[2]}\"")},\"level\":{c[3]},\"root_type\":\"{c[4]}\",\"is_group\":{c[5]}" +
                (c[6].Length > 0 ? $",\"account_type\":\"{c[6]}\"" : "") + "}")) + "] }");
        (await k.Ok(k.UploadCoa(flat, "flat.json"), "flat json")).I("accountsCreated").Should().Be(23);
    }

    [Fact]
    public async Task Two_thousand_and_one_rows_are_refused_R10()
    {
        var k = (await _root.OrgAsync("R10")).K;
        var lines = Gl.ValidCsvLines();                    // 20 accounts
        for (var i = 0; lines.Count - 1 < 2001; i++) lines.Add(Gl.Row($"1{i + 1000:D5}", $"Leaf {i}", "111", 4, "Asset", false, "Cash"));
        lines.Should().HaveCount(2002);
        var big = await k.UploadCoa(Gl.Bytes(lines), "big.csv");
        big.ShouldFail(HttpStatusCode.BadRequest, "2001 accounts", "Upload contains 2001 accounts. Maximum allowed is 2000.");
        big.Body.A("errors").Should().ContainSingle().Which.S("rule").Should().Be("R10");

        // 1997 rows + 39/390/3900 = 2000: accepted
        lines.RemoveRange(1998, 4);
        (await k.Ok(k.UploadCoa(Gl.Bytes(lines), "max.csv"), "1997 rows")).I("accountsCreated").Should().Be(2000);
        (await k.CreateAccount(await k.AccountAsync("111"), "1999999", "One too many", "Cash"))
            .ShouldFail(HttpStatusCode.Conflict, "the 2001st account", "A tenant can have at most 2000 accounts.");
    }

    // ── T-COA ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Applying_US_GENERAL_builds_paths_levels_and_roots()
    {
        var o = await _root.OrgAsync("COA");
        var k = o.K;
        var template = await k.TemplateAsync("US_GENERAL");
        var applied = await k.ApplyAsync();
        (applied.I("accountsCreated"), applied.I("defaultsAssigned"), applied.S("status")).Should().Be((template.I("accountCount"), 16, "AccountsCreated"));

        var rows = await _f.QueryAsync(
            "SELECT a.AccountCode, a.MaterializedPath, a.Level, a.RootType, a.IsGroup, a.IsSystemAccount, p.MaterializedPath AS ParentPath, p.Level AS ParentLevel, " +
            "p.RootType AS ParentRoot FROM finance.ChartOfAccounts a LEFT JOIN finance.ChartOfAccounts p ON p.Id = a.ParentAccountId WHERE a.OrganizationId = @o",
            ("@o", o.OrgId));
        rows.Should().HaveCount(template.I("accountCount"));
        var roots = rows.Where(r => Convert.ToInt32(r["Level"]) == 1).ToList();
        roots.Select(r => $"{r["AccountCode"]}:{r["RootType"]}:{r["MaterializedPath"]}:{r["IsSystemAccount"]}:{r["ParentPath"] is null}").Should().BeEquivalentTo(
            "1:Asset:/1:True:True", "2:Liability:/2:True:True", "3:Equity:/3:True:True", "4:Revenue:/4:True:True", "5:Expense:/5:True:True");
        foreach (var r in rows.Where(r => Convert.ToInt32(r["Level"]) > 1))
        {
            var code = (string)r["AccountCode"]!;
            r["MaterializedPath"].Should().Be($"{r["ParentPath"]}/{code}", code);
            Convert.ToInt32(r["Level"]).Should().Be(Convert.ToInt32(r["ParentLevel"]) + 1, code);
            r["RootType"].Should().Be(r["ParentRoot"], code);
            ((bool)r["IsGroup"]!).Should().Be(Convert.ToInt32(r["Level"]) < 4, code);
            code.Should().StartWith(((string)r["MaterializedPath"]!)[1..2], "D-8: the root digit");
        }
        rows.Single(r => (string)r["AccountCode"]! == "1112")["MaterializedPath"].Should().Be("/1/11/111/1112");
        rows.Single(r => (string)r["AccountCode"]! == "3900")["IsSystemAccount"].Should().Be(true);

        // the tree: 5 roots, summary, delete reasons on every row
        var tree = await k.Ok(k.Get(Gl.Chart), "tree");
        tree.A("roots").Select(r => r.S("accountCode")).Should().Equal("1", "2", "3", "4", "5");
        tree.P("summary").I("totalAccounts").Should().Be(template.I("accountCount"));
        tree.P("summary").I("postableAccounts").Should().Be(rows.Count(r => !(bool)r["IsGroup"]!));
        var assets = tree.A("roots")[0];
        (assets.B("canDelete"), assets.S("deleteBlockedReason")).Should().Be((false, "Cannot delete a system account."));
        var detail = await k.Ok(k.Get($"{Gl.Chart}/{await k.AccountAsync("1112")}"), "detail");
        (detail.P("parent").S("accountCode"), detail.I("childrenCount"), detail.S("deleteBlockedReason"))
            .Should().Be(("111", 0, "This account is assigned as Cash Account. Assign another account first."));
        var ancestors = (await k.Ok(k.Get($"{Gl.Chart}/{await k.AccountAsync("1112")}/ancestors"), "ancestors")).Items();
        ancestors.Select(a => a.S("accountCode")).Should().Equal("1", "11", "111");
        (await k.Ok(k.Get($"{Gl.Chart}/suggest-code?parentUuid={await k.AccountAsync("111")}"), "suggest")).S("code").Should().Be("1116");

        // the setup status remembers the template
        var status = await k.StatusAsync();
        (status.S("status"), status.S("templateName"), status.I("accountsCreatedCount")).Should()
            .Be(("AccountsCreated", template.S("templateName"), template.I("accountCount")));

        // re-applying replaces the chart (allowed before the fiscal year)
        var again = await k.ApplyAsync("PK_GENERAL");
        again.I("accountsCreated").Should().Be((await k.TemplateAsync("PK_GENERAL")).I("accountCount"));
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.ChartOfAccounts WHERE OrganizationId = @o", ("@o", o.OrgId)))
            .Should().Be(again.I("accountsCreated"), "the previous chart is gone");

        // once a fiscal year exists the chart can no longer be replaced
        await k.CreateYearOk(Gl.Y);
        (await k.Post($"{Gl.Chart}/apply-template", new { templateUuid = template.G("uuid") })).ShouldFail(HttpStatusCode.Conflict, "after the year",
            "The chart can only be replaced before the fiscal year is set and before opening balances are posted.");
        (await k.UploadCoa(Gl.Bytes(Gl.ValidCsvLines()), "late.csv")).ShouldFail(HttpStatusCode.Conflict, "upload after the year",
            "The chart can only be replaced before the fiscal year is set and before opening balances are posted.");
    }

    [Fact]
    public async Task Accounts_are_added_and_deleted_by_the_rules()
    {
        var o = await _root.OrgAsync("CRUD");
        var k = o.K;
        await k.ApplyAsync();
        var a = await k.AccountsAsync();

        // add a level 4 under a level 3: path, level, root from the parent
        var added = await k.Ok(k.CreateAccount(a["111"].G("uuid"), "1116", "Cash in Transit", "Cash"), "add L4 under L3");
        (added.S("materializedPath"), added.I("level"), added.S("rootType"), added.B("isGroup"), added.S("parentCode"), added.I("sortOrder"))
            .Should().Be(("/1/11/111/1116", 4, "Asset", false, "111", a["1115"].I("sortOrder") + 10));
        (await _f.QueryAsync("SELECT MaterializedPath, Level FROM finance.ChartOfAccounts WHERE Uuid = @u", ("@u", added.G("uuid"))))
            .Single()["MaterializedPath"].Should().Be("/1/11/111/1116");
        // a level 3 group under a level 2, then a level 4 under it
        var group = await k.Ok(k.CreateAccount(a["11"].G("uuid"), "115", "Deposits"), "add L3 under L2");
        (group.I("level"), group.B("isGroup"), group.S("materializedPath")).Should().Be((3, true, "/1/11/115"));
        await k.Ok(k.CreateAccount(group.G("uuid"), "1151", "Rent deposit", "OtherCurrentAsset"), "add L4 under the new L3");

        // refusals
        (await k.CreateAccount(a["1112"].G("uuid"), "11121", "Under a leaf", "Cash")).ShouldFail(HttpStatusCode.BadRequest, "under a leaf",
            "Accounts can only be added under a group account (levels 1–3).");
        (await k.CreateAccount(a["111"].G("uuid"), "2119", "Wrong root digit", "Cash")).ShouldFail(HttpStatusCode.BadRequest, "wrong root digit",
            "Account code must start with 1, the code of Asset accounts.");
        (await k.CreateAccount(a["111"].G("uuid"), "1117", "Wrong type", "Payable")).ShouldFail(HttpStatusCode.BadRequest, "wrong root type",
            "Account type Payable is not valid for Asset accounts.");
        (await k.CreateAccount(a["11"].G("uuid"), "116", "Typed group", "Cash")).ShouldFail(HttpStatusCode.BadRequest, "group with a type",
            "A group account cannot have an account type.");
        (await k.CreateAccount(a["111"].G("uuid"), "1112", "Duplicate", "Cash")).ShouldFail(HttpStatusCode.Conflict, "duplicate", "Account code 1112 already exists.");
        (await k.CreateAccount(Guid.NewGuid(), "1118", "No parent", "Cash")).ShouldFail(HttpStatusCode.NotFound, "no parent", "Parent account not found.");

        // updates: 3900 cannot be renamed; a leaf can
        (await k.Put($"{Gl.Chart}/{a["3900"].G("uuid")}", new { accountName = "Renamed", accountType = "Equity" }))
            .ShouldFail(HttpStatusCode.Conflict, "3900 rename", "Opening Balance Equity (3900) cannot be renamed.");
        (await k.Ok(k.Put($"{Gl.Chart}/{added.G("uuid")}", new { accountName = "Cash in transit (bank)", accountType = "Bank" }), "rename"))
            .S("accountType").Should().Be("Bank");

        // delete rules, in order (D-48 / D-61), as 409 + the same text on the row
        async Task Refused(string code, string reason)
        {
            var uuid = (await k.AccountsAsync())[code].G("uuid");
            (await k.Delete($"{Gl.Chart}/{uuid}")).ShouldFail(HttpStatusCode.Conflict, $"delete {code}", reason);
            (await k.Ok(k.Get($"{Gl.Chart}/{uuid}"), "detail")).S("deleteBlockedReason").Should().Be(reason);
        }
        await Refused("1", "Cannot delete a system account.");
        await Refused("3900", "Cannot delete a system account.");
        await Refused("111", "Cannot delete an account that has child accounts.");
        await Refused("1112", "This account is assigned as Cash Account. Assign another account first.");
        await k.Ok(k.Batch((a["1111"].G("uuid"), 50m, 0m)), "a draft opening balance");
        await Refused("1111", "This account has an opening balance. Remove it first.");
        await k.Ok(k.Post("/api/finance/external-mappings", new { systemType = "XERO", accountUuid = a["1114"].G("uuid"), externalAccountCode = "090" }), "map 1114");
        await Refused("1114", "This account is mapped to Xero. Remove the mapping first.");
        // ("Cannot delete an account with posted transactions." — after posting: GlSetupE2ETests)

        // a leaf without anything goes (soft delete), and its code can be reused
        await k.Ok(k.Delete($"{Gl.Chart}/{added.G("uuid")}"), "delete a plain leaf");
        var gone = await k.Ok(k.Get($"{Gl.Chart}/{added.G("uuid")}"), "the deleted account");
        (gone.B("isActive"), gone.S("deleteBlockedReason")).Should().Be((false, "This account is already deleted."));
        (await k.Put($"{Gl.Chart}/{added.G("uuid")}", new { accountName = "x" })).ShouldFail(HttpStatusCode.Conflict, "update deleted",
            "This account was deleted and cannot be changed.");
        (await k.Ok(k.Get(Gl.Chart), "tree")).ToString().Should().NotContain("Cash in transit (bank)");
        await k.Ok(k.CreateAccount(a["111"].G("uuid"), "1116", "Cash in Transit again", "Cash"), "reuse the deleted code");
        // a zero draft goes with its account
        await k.Ok(k.Batch((a["1111"].G("uuid"), 0m, 0m)), "clear the draft");
        await k.Ok(k.Delete($"{Gl.Chart}/{a["1111"].G("uuid")}"), "delete 1111 once its draft is gone");
    }

    [Fact]
    public async Task Export_round_trips_and_subtree_lists_descendants()
    {
        var o = await _root.OrgAsync("EXP");
        var k = o.K;
        await k.ApplyAsync();
        await k.Ok(k.CreateAccount(await k.AccountAsync("111"), "1116", "Cash, \"quoted\" name", "Cash"), "a name that needs quoting");
        var before = (await k.AccountsAsync()).Values.Where(x => x.B("isActive"))
            .Select(x => $"{x.S("accountCode")}|{x.S("accountName")}|{x.S("materializedPath")}|{x.S("accountType")}").OrderBy(x => x).ToList();

        // CSV export: file, BOM, header, quoting; re-upload gives the same chart (CSV has no default markers: only 3900's)
        var csv = await k.DownloadAsync($"{Gl.Chart}/export?format=csv");
        (csv.Status, csv.FileName).Should().Be((HttpStatusCode.OK, "chart-of-accounts.csv"));
        var lines = Gl.CsvLines(csv.Content);
        lines[0].Should().Be(Gl.CsvHeader);
        lines.Should().Contain("1116,\"Cash, \"\"quoted\"\" name\",111,4,Asset,false,Cash,");
        var up = await k.Ok(k.UploadCoa(csv.Content, "chart-of-accounts.csv"), "re-upload the CSV export");
        (up.I("accountsCreated"), up.I("defaultsAssigned")).Should().Be((before.Count, 1));
        var after = (await k.AccountsAsync()).Values.Where(x => x.B("isActive"))
            .Select(x => $"{x.S("accountCode")}|{x.S("accountName")}|{x.S("materializedPath")}|{x.S("accountType")}").OrderBy(x => x).ToList();
        after.Should().Equal(before);

        // JSON export carries the default markers: re-upload re-assigns them
        await k.ApplyAsync();
        var json = await k.DownloadAsync($"{Gl.Chart}/export?format=json");
        json.FileName.Should().Be("chart-of-accounts.json");
        (await k.Ok(k.UploadCoa(json.Content, "chart-of-accounts.json"), "re-upload the JSON export")).I("defaultsAssigned").Should().Be(16);

        // subtree: active descendants in path order
        var sub = (await k.Ok(k.Get($"{Gl.Chart}/{await k.AccountAsync("11")}/subtree"), "subtree of 11")).Items();
        sub.Select(x => x.S("materializedPath")).Should().BeInAscendingOrder(StringComparer.Ordinal);
        sub.Should().OnlyContain(x => x.S("materializedPath")!.StartsWith("/1/11/"));
        sub.Select(x => x.S("accountCode")).Should().Contain(new[] { "111", "1111", "1112", "112", "1121", "113", "114", "1143" }).And.NotContain("11").And.NotContain("12");
        (await k.Ok(k.Get($"{Gl.Chart}/{await k.AccountAsync("1112")}/subtree"), "leaf subtree")).Items().Should().BeEmpty();
        (await k.Get($"{Gl.Chart}/export?format=pdf")).ShouldFail(HttpStatusCode.BadRequest, "format", "Format must be csv or json.");
    }

    [Fact]
    public async Task A_blank_chart_is_built_level_by_level()
    {
        var o = await _root.OrgAsync("BLK");
        var k = o.K;
        var blank = await k.Ok(k.Post($"{Gl.Chart}/start-blank"), "start blank");
        (blank.I("accountsCreated"), blank.I("defaultsAssigned"), blank.S("status")).Should().Be((8, 1, "AccountsCreated"));
        var a = await k.AccountsAsync();
        a.Keys.Should().BeEquivalentTo("1", "2", "3", "4", "5", "39", "390", "3900");

        var l2 = await k.Ok(k.CreateAccount(a["1"].G("uuid"), "11", "Current Assets"), "L2");
        var l3 = await k.Ok(k.CreateAccount(l2.G("uuid"), "111", "Cash and Bank"), "L3");
        var l4 = await k.Ok(k.CreateAccount(l3.G("uuid"), "1111", "Cash", "Cash"), "L4");
        (l2.I("level"), l3.I("level"), l4.I("level"), l4.S("materializedPath")).Should().Be((2, 3, 4, "/1/11/111/1111"));
        (await k.Ok(k.Get($"{Gl.Chart}/suggest-code?parentUuid={l3.G("uuid")}"), "suggest")).S("code").Should().Be("1112");

        var defaults = await k.Ok(k.Get("/api/finance/accounting-defaults"), "defaults");
        (defaults.I("requiredCount"), defaults.I("assignedRequiredCount")).Should().Be((8, 1), "only Opening Balance Equity so far");
        (await k.SetDefault("DEFAULT_CASH", l3.G("uuid"))).ShouldFail(HttpStatusCode.BadRequest, "a group", "Only Level 4 accounts can be assigned as defaults.");
        (await k.Ok(k.SetDefault("DEFAULT_CASH", l4.G("uuid")), "assign a leaf")).P("account").S("accountCode").Should().Be("1111");
        (await k.SetDefault("DEFAULT_OPENING_BALANCE", l4.G("uuid"))).ShouldFail(HttpStatusCode.BadRequest, "locked",
            "The Opening Balance Equity default is assigned automatically to account 3900 and cannot be changed.");

        // save-all is all-or-nothing (D-52)
        var all = await k.Put("/api/finance/accounting-defaults", new
        {
            items = new object[]
            {
                new { defaultKey = "DEFAULT_BANK", accountUuid = l4.G("uuid") }, new { defaultKey = "DEFAULT_PAYABLE", accountUuid = l3.G("uuid") },
                new { defaultKey = "DEFAULT_NOPE", accountUuid = l4.G("uuid") }
            }
        });
        all.ShouldFail(HttpStatusCode.BadRequest, "two bad items", "2 accounting defaults are invalid.");
        all.Result.A("problems").Select(p => p.S("defaultKey")).Should().BeEquivalentTo("DEFAULT_PAYABLE", "DEFAULT_NOPE");
        (await k.Ok(k.Get("/api/finance/accounting-defaults"), "defaults")).A("categories").SelectMany(c => c.A("items"))
            .Single(i => i.S("defaultKey") == "DEFAULT_BANK").IsNull("account").Should().BeTrue("nothing was saved");

        var history = (await k.Ok(k.Get("/api/finance/setup/history"), "history")).Items();
        history.Should().Contain(h => h.S("summary") == "Blank chart of accounts started: 8 system accounts created.");
    }
}
