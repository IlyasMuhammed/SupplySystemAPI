using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;

namespace SMS.Integration.Tests.GeneralLedger;

/// <summary>
/// A42 (QA) — what the General Ledger E2E classes share on top of <see cref="SapKit"/>, Mods and PreOrder: the chart of accounts
/// (template apply, CSV/JSON files, multipart uploads), fiscal years, opening balances, mappings, and the few things only SQL can
/// do here (a non-opening journal entry — A42 has no endpoint that writes one, D-10).
/// </summary>
internal static class Gl
{
    public const string Feature = "FEATURE_GENERAL_LEDGER";
    public const string View = "GL_VIEW", ManageAccounts = "GL_ACCOUNT_MANAGE", ManageSetup = "GL_SETUP_MANAGE";
    public const string Chart = "/api/finance/chart-of-accounts";

    public static readonly string CsvHeader = "account_code,account_name,parent_code,level,root_type,is_group,account_type,description";

    /// <summary>The current calendar year: fiscal years and the opening date are placed in it.</summary>
    public static int Y => DateTime.UtcNow.Year;

    public static string Day(int year, int month, int day) => new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ── Templates / chart ────────────────────────────────────────────────────────

    public static async Task<JsonElement> TemplateAsync(this SapKit k, string code) =>
        (await k.Ok(k.Get("/api/finance/templates"), "list templates")).Items().Single(t => t.S("templateCode") == code);

    public static async Task<JsonElement> ApplyAsync(this SapKit k, string code = "US_GENERAL") =>
        await k.Ok(k.Post($"{Chart}/apply-template", new { templateUuid = (await k.TemplateAsync(code)).G("uuid") }), $"apply {code}");

    /// <summary>Every account of the organization (active and inactive) by code — the active one when a code was reused.</summary>
    public static async Task<Dictionary<string, JsonElement>> AccountsAsync(this SapKit k, HttpClient? client = null) =>
        (await k.Ok(k.Get($"{Chart}/flat", client), "flat chart")).Items()
            .GroupBy(a => a.S("accountCode")!)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.B("isActive")).First());

    public static async Task<Guid> AccountAsync(this SapKit k, string code) => (await k.AccountsAsync())[code].G("uuid");

    public static Task<Api> CreateAccount(this SapKit k, Guid parent, string code, string name, string? type = null, HttpClient? client = null) =>
        k.Post(Chart, new { parentUuid = parent, accountCode = code, accountName = name, accountType = type }, client);

    public static string Row(string code, string name, string? parent, int level, string root, bool group, string? type = null, string? desc = null) =>
        $"{code},{name},{parent},{level},{root},{(group ? "true" : "false")},{type},{desc}";

    /// <summary>A valid 20-row chart: each root with one L2 → L3 → L4 path (codes 1/11/111/1111 …). 23 accounts once 39/390/3900 are added.</summary>
    public static List<string> ValidCsvLines()
    {
        var lines = new List<string> { CsvHeader };
        foreach (var (digit, root, rootName, type) in new[]
                 {
                     ("1", "Asset", "Assets", "Cash"), ("2", "Liability", "Liabilities", "Payable"), ("3", "Equity", "Equity", "Equity"),
                     ("4", "Revenue", "Revenue", "Revenue"), ("5", "Expense", "Expenses", "Expense")
                 })
        {
            lines.Add(Row(digit, rootName, null, 1, root, true));
            lines.Add(Row($"{digit}1", $"{rootName} group", digit, 2, root, true));
            lines.Add(Row($"{digit}11", $"{rootName} subgroup", $"{digit}1", 3, root, true));
            lines.Add(Row($"{digit}111", $"{rootName} leaf", $"{digit}11", 4, root, false, type));
        }
        return lines;
    }

    public static byte[] Bytes(IEnumerable<string> lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");

    public static Task<Api> UploadCoa(this SapKit k, byte[] content, string fileName, HttpClient? client = null) =>
        k.Multipart($"{Chart}/upload", content, fileName, null, client);

    public static Task<Api> ImportMappings(this SapKit k, byte[] content, string systemType, string? systemName = null, HttpClient? client = null) =>
        k.Multipart("/api/finance/external-mappings/import", content, "mappings.csv",
            new Dictionary<string, string?> { ["systemType"] = systemType, ["systemName"] = systemName }, client);

    public static async Task<Api> Multipart(this SapKit k, string url, byte[] content, string fileName, Dictionary<string, string?>? fields,
        HttpClient? client = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "application/json" : "text/csv");
        form.Add(file, "file", fileName);
        foreach (var (name, value) in fields ?? [])
            if (value is not null) form.Add(new StringContent(value), name);
        using var resp = await (client ?? k.Admin).PostAsync(url, form);
        var raw = await resp.Content.ReadAsStringAsync();
        JsonElement parsed;
        try { parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw).RootElement.Clone(); }
        catch (JsonException) { parsed = JsonDocument.Parse("{}").RootElement.Clone(); }
        return new Api(resp.StatusCode, parsed, raw);
    }

    /// <summary>The upload refusal's errors as "line|path:rule".</summary>
    public static List<string> Rules(this Api api) =>
        api.Body.A("errors").Select(e => $"{(e.Has("line") && !e.IsNull("line") ? e.I("line").ToString(CultureInfo.InvariantCulture) : e.Has("path") ? e.S("path") : null)}:{e.S("rule")}").ToList();

    /// <summary>A property that may be omitted or null.</summary>
    public static string? Opt(this JsonElement e, string name) => e.Has(name) ? e.S(name) : null;

    // ── Setup ────────────────────────────────────────────────────────────────────

    public static async Task<JsonElement> StatusAsync(this SapKit k, HttpClient? client = null) =>
        await k.Ok(k.Get("/api/finance/setup/status", client), "setup status");

    public static Task<Api> CreateYear(this SapKit k, string start, string end, string? name = null, string? currency = null) =>
        k.Post("/api/finance/fiscal-years", new { fiscalYearName = name, startDate = start, endDate = end, reportingCurrencyCode = currency });

    public static async Task<JsonElement> CreateYearOk(this SapKit k, int year) =>
        await k.Ok(k.CreateYear(Day(year, 1, 1), Day(year, 12, 31)), $"fiscal year {year}");

    public static Task<Api> SetDefault(this SapKit k, string key, Guid? account) =>
        k.Put($"/api/finance/accounting-defaults/{key}", new { accountUuid = account });

    public static Task<Api> Batch(this SapKit k, params (Guid Account, decimal Debit, decimal Credit)[] entries) =>
        k.Put("/api/finance/opening-balances/batch", new { entries = entries.Select(e => new { accountUuid = e.Account, debitAmount = e.Debit, creditAmount = e.Credit }).ToArray() });

    public static Task<Api> PostOpening(this SapKit k, bool acknowledge = false, bool skip = false) =>
        k.Post("/api/finance/opening-balances/post", new { acknowledgeDifference = acknowledge, skip });

    public static Task<Api> SetOpeningDate(this SapKit k, string? date) => k.Put("/api/finance/opening-balance-date", new { date });

    /// <summary>US_GENERAL applied, the year <see cref="Y"/>, the opening date 31 January — ready to enter opening balances.</summary>
    public static async Task<Dictionary<string, JsonElement>> ReadyForBalancesAsync(this SapKit k)
    {
        await k.ApplyAsync();
        await k.CreateYearOk(Y);
        await k.Ok(k.SetOpeningDate(Day(Y, 1, 31)), "set the opening date");
        return await k.AccountsAsync();
    }

    /// <summary>A posted journal entry that is not the opening balance (A43 will write these; A42 has no endpoint).</summary>
    public static Task<int> InsertManualJournalAsync(this SapWebApplicationFactory f, Guid org) =>
        f.ExecuteAsync(
            "INSERT INTO finance.JournalEntries (Uuid, OrganizationId, EntryNumber, EntryType, EntryDate, Reference, Description, Status, " +
            "TotalDebit, TotalCredit, CurrencyCode, PostedAt, PostedBy, CreatedAt, CreatedBy, ModifiedAt) " +
            "VALUES (NEWID(), @o, 'JE-QA-MANUAL', 'MANUAL', CAST(SYSUTCDATETIME() AS date), 'QA', 'QA manual journal', 'Posted', 0, 0, 'PKR', " +
            "SYSUTCDATETIME(), NULL, SYSUTCDATETIME(), NULL, SYSUTCDATETIME())", ("@o", org));

    public static async Task<int> RowCountAsync(this SapWebApplicationFactory f, string sql, params (string, object)[] p) =>
        Convert.ToInt32((await f.QueryAsync(sql, p))[0].Values.First());

    /// <summary>Lines of a CSV file without its BOM.</summary>
    public static List<string> CsvLines(byte[] content)
    {
        content.Take(3).Should().Equal(new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8 with BOM");
        return Encoding.UTF8.GetString(content, 3, content.Length - 3).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    /// <summary>The factory must be on LocalDB — never SMSGlobal / Azure from appsettings.json.</summary>
    public static void ShouldBeOnLocalDb(this SapWebApplicationFactory f)
    {
        var cs = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(f.Services)["Data:mainOrg"];
        cs.Should().Contain("(localdb)", "the GL E2E tests may only run against LocalDB");
        cs.Should().Contain(f.DatabaseName);
        cs.Should().NotContain("database.windows.net");
        cs.Should().NotContainEquivalentOf("SMSGlobal");
    }

    public static void ShouldBeForbidden(this Api api, string because) => api.Status.Should().Be(HttpStatusCode.Forbidden, $"{because} — {api}");
}
