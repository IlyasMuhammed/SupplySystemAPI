using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;

namespace SMS.Integration.Tests.JournalEngine;

/// <summary>An A47 test organization: the A44 org (GL setup Complete, internal accounting on), its chart, defaults and journal types.</summary>
internal sealed record JeOrg(FpOrg Fp, Dictionary<string, JsonElement> Accounts, Dictionary<string, string> Defaults)
{
    public SapKit K => Fp.K;
    public Guid OrgId => Fp.OrgId;
    public SapWebApplicationFactory F => Fp.F;

    /// <summary>The uuid of the account assigned to an A42 default key (DEFAULT_CASH …).</summary>
    public Guid Acc(string defaultKey) => Accounts[Defaults[defaultKey]].G("uuid");

    public string Code(string defaultKey) => Defaults[defaultKey];
}

/// <summary>One journal line of a request: account uuid, debit, credit (+ optional partner / currency).</summary>
internal sealed record JeLine(Guid Account, decimal Debit, decimal Credit, Guid? Partner = null, string? PartnerType = null,
    string? Currency = null, decimal? Rate = null, string? Description = null);

/// <summary>
/// A47 (QA) — what the JournalEngine E2E classes share on top of <see cref="Fp"/>, <see cref="Gl"/> and <see cref="SapKit"/>.
/// Routes, bodies and messages follow docs/journal-engine/API-CONTRACT.md; JSON is read case-insensitively.
/// </summary>
internal static class Je
{
    // ── routes (API-CONTRACT) ────────────────────────────────────────────────────
    public const string Types     = "/api/finance/journal-types";
    public const string Entries   = "/api/finance/journal-entries";
    public const string Recon     = "/api/finance/reconciliation";
    public const string Tb        = "/api/finance/trial-balance";
    public const string Accounts  = "/api/finance/trial-balance/accounts";
    public const string Close     = "/api/finance/period-close";
    public const string YearClose = "/api/finance/year-end-close";

    // ── D-15 permission codes ────────────────────────────────────────────────────
    public const string View = "GL_VIEW", Create = "GL_JOURNAL_CREATE", Edit = "GL_JOURNAL_EDIT", Del = "GL_JOURNAL_DELETE",
        Post = "GL_JOURNAL_POST", Reverse = "GL_JOURNAL_REVERSE", ReconView = "GL_RECONCILE_VIEW", ReconExec = "GL_RECONCILE_EXECUTE",
        Reports = "GL_REPORTS_VIEW", CloseView = "GL_PERIOD_CLOSE_VIEW", CloseExec = "GL_PERIOD_CLOSE_EXECUTE", CloseAdmin = "GL_PERIOD_CLOSE_ADMIN",
        Setup = "GL_SETUP_MANAGE";

    public static readonly string[] D15 = [View, Create, Edit, Del, Post, Reverse, ReconView, ReconExec, Reports, CloseView, CloseExec, CloseAdmin];

    public static DateTime Today => DateTime.UtcNow.Date;
    public static int Y => Gl.Y;
    public static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public static string TodayIso => Iso(Today);

    // ── organization ─────────────────────────────────────────────────────────────

    /// <summary>A new org with the A42 setup Complete (year = this calendar year, opening skipped) and internal accounting on.</summary>
    public static async Task<JeOrg> NewJeOrgAsync(this SapKit root, string prefix, bool gl = true, bool autoPost = true, bool qbo = false)
    {
        var o = await root.NewOrgAsync(prefix);
        if (qbo) o = await o.ConnectLiveAsync();
        if (gl) await o.K.EnableGlAsync(autoPost);
        else await o.K.CompleteGlSetupAsync();
        return new JeOrg(o, await o.K.AccountsAsync(), await o.K.DefaultCodesAsync());
    }

    public static async Task<JeOrg> RefreshAsync(this JeOrg o) => o with { Accounts = await o.K.AccountsAsync() };

    // ── journal types ────────────────────────────────────────────────────────────

    public static async Task<List<JsonElement>> TypesAsync(this SapKit k, HttpClient? c = null) =>
        Rows(await k.Ok(k.Get($"{Types}?activeOnly=false", c), "journal types"));

    public static async Task<Guid> TypeAsync(this SapKit k, string code) =>
        (await k.TypesAsync()).Single(t => t.S("code") == code).G("uuid");

    // ── journal entries ──────────────────────────────────────────────────────────

    public static object Body(Guid type, DateTime date, IEnumerable<JeLine> lines, string? description = null, string? currency = null,
        decimal? rate = null, bool closing = false, bool opening = false, Guid? clientRequestId = null, bool post = false, string? reference = null) => new
    {
        journalTypeUuid = type, entryDate = Iso(date), description = description ?? "A47 QA", reference, currencyCode = currency, exchangeRate = rate,
        isOpeningEntry = opening, isClosingEntry = closing, clientRequestId, postImmediately = post,
        lines = lines.Select(l => new
        {
            accountUuid = l.Account, debit = l.Debit, credit = l.Credit, description = l.Description, partnerUuid = l.Partner,
            partnerType = l.PartnerType, currencyCode = l.Currency, exchangeRate = l.Rate
        }).ToArray()
    };

    public static Task<Api> CreateEntry(this SapKit k, object body, HttpClient? c = null) => k.Post(Entries, body, c);

    public static async Task<JsonElement> CreateEntryOk(this SapKit k, object body, string what = "create journal entry") =>
        await k.Ok(k.CreateEntry(body), what);

    /// <summary>A two-line entry of <paramref name="amount"/>: Dr <paramref name="debit"/> / Cr <paramref name="credit"/>.</summary>
    public static Task<JsonElement> DraftAsync(this JeOrg o, Guid type, Guid debit, Guid credit, decimal amount, DateTime? date = null, string? description = null) =>
        o.K.CreateEntryOk(Body(type, date ?? Today, [new JeLine(debit, amount, 0m), new JeLine(credit, 0m, amount)], description));

    public static async Task<JsonElement> PostedAsync(this JeOrg o, Guid type, Guid debit, Guid credit, decimal amount, DateTime? date = null, string? description = null)
    {
        var d = await o.DraftAsync(type, debit, credit, amount, date, description);
        return await o.K.Ok(o.K.PostEntry(d.G("uuid")), "post journal entry");
    }

    public static Task<Api> GetEntry(this SapKit k, Guid uuid, HttpClient? c = null) => k.Get($"{Entries}/{uuid}", c);
    public static Task<JsonElement> EntryAsync(this SapKit k, Guid uuid) => k.Ok(k.GetEntry(uuid), "read journal entry");
    public static Task<Api> PostEntry(this SapKit k, Guid uuid, HttpClient? c = null) => k.Post($"{Entries}/{uuid}/post", null, c);
    public static Task<Api> UpdateEntry(this SapKit k, Guid uuid, object body, HttpClient? c = null) => k.Put($"{Entries}/{uuid}", body, c);
    public static Task<Api> DeleteEntry(this SapKit k, Guid uuid, HttpClient? c = null) => k.Delete($"{Entries}/{uuid}", c);

    public static Task<Api> ReverseEntry(this SapKit k, Guid uuid, string reason = "A47 QA", DateTime? date = null, HttpClient? c = null) =>
        k.Post($"{Entries}/{uuid}/reverse", new { reason, reversalDate = date is null ? null : Iso(date.Value) }, c);

    public static Task<Api> ListEntries(this SapKit k, string query = "", HttpClient? c = null) =>
        k.Get($"{Entries}?{query}", c);

    public static async Task<List<JsonElement>> ListEntriesAsync(this SapKit k, string query = "pageSize=200") =>
        Rows(await k.Ok(k.ListEntries(query), $"list journal entries {query}"));

    /// <summary>A line of an entry detail by account uuid.</summary>
    public static List<JsonElement> LinesOn(this JsonElement entry, Guid account) =>
        entry.A("lines").Where(l => l.P("account").G("uuid") == account).ToList();

    // ── fiscal year / periods ────────────────────────────────────────────────────

    public static async Task<JsonElement> YearAsync(this SapKit k) =>
        (await k.Ok(k.Get("/api/finance/fiscal-years"), "fiscal years")).Items()
        .Single(y => DateTime.Parse(y.S("startDate")!, CultureInfo.InvariantCulture).Year == Y);

    public static async Task<List<JsonElement>> PeriodsAsync(this SapKit k, Guid year) =>
        (await k.Ok(k.Get($"/api/finance/fiscal-years/{year}/periods"), "periods")).Items();

    /// <summary>The monthly period (1–12) holding a date.</summary>
    public static async Task<JsonElement> PeriodAsync(this SapKit k, DateTime day)
    {
        var y = await k.YearAsync();
        return (await k.PeriodsAsync(y.G("uuid"))).Single(p => p.I("periodNumber") != 13
            && DateTime.Parse(p.S("startDate")!, CultureInfo.InvariantCulture) <= day.Date
            && DateTime.Parse(p.S("endDate")!, CultureInfo.InvariantCulture) >= day.Date);
    }

    /// <summary>Period-close API (C10). Action: lock | close (final) | reopen | adjustment-only — per the contract.</summary>
    public static Task<Api> LockPeriod(this SapKit k, Guid period, HttpClient? c = null) => k.Post($"{Close}/{period}/close", new { targetStatus = "Locked" }, c);
    public static Task<Api> AdjustmentOnlyPeriod(this SapKit k, Guid period, HttpClient? c = null) => k.Post($"{Close}/{period}/close", new { targetStatus = "AdjustmentOnly" }, c);
    public static Task<Api> ReopenPeriod(this SapKit k, Guid period, HttpClient? c = null) => k.Post($"{Close}/{period}/reopen", new { }, c);
    public static Task<Api> FinalClosePeriod(this SapKit k, Guid period, HttpClient? c = null) => k.Post($"{Close}/{period}/final-close", null, c);
    public static Task<Api> CloseStatus(this SapKit k, Guid year, HttpClient? c = null) => k.Get($"{Close}/{year}/status", c);
    public static Task<Api> YearPreview(this SapKit k, Guid year, HttpClient? c = null) => k.Get($"{YearClose}/{year}/preview", c);
    public static Task<Api> YearExecute(this SapKit k, Guid year, Guid? retainedEarnings = null, HttpClient? c = null) =>
        k.Post($"{YearClose}/{year}/execute", new { retainedEarningsAccountUuid = retainedEarnings }, c);

    /// <summary>Periods 1–12 of the year Locked (through the period-close API).</summary>
    public static async Task LockAllMonthsAsync(this SapKit k, Guid year)
    {
        foreach (var p in (await k.PeriodsAsync(year)).Where(p => p.I("periodNumber") != 13).OrderBy(p => p.I("periodNumber")))
            if (p.S("status") == "Open") await k.Ok(k.LockPeriod(p.G("uuid")), $"lock period {p.I("periodNumber")}");
    }

    // ── trial balance / ledger ───────────────────────────────────────────────────

    public static Task<Api> TrialBalance(this SapKit k, DateTime from, DateTime to, bool opening = false, bool closing = false, HttpClient? c = null) =>
        k.Get($"{Tb}?fromDate={Iso(from)}&toDate={Iso(to)}&includeOpeningBalances={opening.ToString().ToLowerInvariant()}" +
              $"&includeClosingEntries={closing.ToString().ToLowerInvariant()}", c);

    public static Task<JsonElement> TrialBalanceAsync(this SapKit k, DateTime from, DateTime to, bool opening = false, bool closing = false) =>
        k.Ok(k.TrialBalance(from, to, opening, closing), "trial balance");

    /// <summary>The trial balance row of an account (by uuid or code), or default when the account is not listed.</summary>
    public static JsonElement TbRow(this JsonElement tb, string code) =>
        TbRows(tb).FirstOrDefault(r => r.P("account").S("accountCode") == code);

    public static List<JsonElement> TbRows(JsonElement tb) => tb.A("lines");

    public static Task<Api> Ledger(this SapKit k, Guid account, DateTime from, DateTime to, int page = 1, int pageSize = 50, HttpClient? c = null) =>
        k.Get($"{Accounts}/{account}/ledger?fromDate={Iso(from)}&toDate={Iso(to)}&page={page}&pageSize={pageSize}", c);

    public static Task<Api> Balance(this SapKit k, Guid account, DateTime asOf, HttpClient? c = null) =>
        k.Get($"{Accounts}/{account}/balance?asOfDate={Iso(asOf)}", c);

    // ── reconciliation ───────────────────────────────────────────────────────────

    public static Task<Api> OpenItems(this SapKit k, Guid account, Guid? partner = null, HttpClient? c = null) =>
        k.Get($"{Recon}/open-items?accountUuid={account}{(partner is null ? "" : $"&partnerUuid={partner}")}", c);

    public static async Task<List<JsonElement>> OpenItemsAsync(this SapKit k, Guid account) =>
        Rows(await k.Ok(k.OpenItems(account), "open items"));

    public static Task<Api> Suggest(this SapKit k, Guid account, IEnumerable<Guid> lineUuids, int? max = null, HttpClient? c = null) =>
        k.Post($"{Recon}/suggest", new { accountUuid = account, lineUuids = lineUuids.ToArray(), maxResults = max }, c);

    public static Task<Api> ReconGroup(this SapKit k, Guid group, HttpClient? c = null) => k.Get($"{Recon}/{group}", c);

    public static Task<Api> Reconcile(this SapKit k, IEnumerable<Guid> lineUuids, HttpClient? c = null) =>
        k.Post($"{Recon}/reconcile", new { lineUuids = lineUuids.ToArray() }, c);

    public static Task<Api> Unreconcile(this SapKit k, Guid group, HttpClient? c = null) => k.Post($"{Recon}/{group}/unreconcile", new { }, c);

    // ── assertions ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A refusal with the contract's status, <c>result.errorCode</c> (when given) and message (contained, case-insensitive,
    /// in <c>message</c> or any <c>result.errors[].message</c>).
    /// </summary>
    public static void ShouldRefuse(this Api api, HttpStatusCode status, string? errorCode, string message, string because)
    {
        api.Status.Should().Be(status, $"{because} — {api}");
        var texts = new List<string> { api.Message };
        if (api.Result.ValueKind == JsonValueKind.Object && api.Result.Has("errors") && api.Result.P("errors").ValueKind == JsonValueKind.Array)
            texts.AddRange(api.Result.A("errors").Where(e => e.Has("message")).Select(e => e.S("message") ?? ""));
        texts.Should().Contain(t => t.Contains(message, StringComparison.OrdinalIgnoreCase), $"{because}: message '{message}' — {api}");
        if (errorCode is not null)
        {
            var actual = api.Result.ValueKind == JsonValueKind.Object && api.Result.Has("errorCode") ? api.Result.S("errorCode") : null;
            if (actual != errorCode && Environment.GetEnvironmentVariable("A47QA_SOFT_CODES") == "1")
            {
                // QA survey mode only: record and go on, so one run shows every mismatch. Default = strict.
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "a47qa-soft.txt"),
                    $"{DateTime.Now:HH:mm:ss} {because}: errorCode {actual ?? "(none)"} instead of {errorCode} — {api}{Environment.NewLine}"); } catch { }
                return;
            }
            actual.Should().Be(errorCode, $"{because}: result.errorCode — {api}");
        }
    }

    public static void ShouldBeRefusedState(this Api api, string? errorCode, string message, string because) =>
        api.ShouldRefuse(HttpStatusCode.Conflict, errorCode, message, because);

    public static void ShouldBeInvalid(this Api api, string? errorCode, string message, string because) =>
        api.ShouldRefuse(HttpStatusCode.BadRequest, errorCode, message, because);

    public static void ShouldBeAllowed(this Api api, string because) =>
        api.Status.Should().NotBe(HttpStatusCode.Forbidden, $"{because} — {api}").And.NotBe(HttpStatusCode.Unauthorized);

    // ── helpers ──────────────────────────────────────────────────────────────────

    /// <summary>A list answer: an array, or a page { data | items | rows }.</summary>
    public static List<JsonElement> Rows(JsonElement r) =>
        r.ValueKind == JsonValueKind.Array ? r.Items()
        : r.A(r.Has("data") ? "data" : r.Has("items") ? "items" : r.Has("rows") ? "rows" : "lines");

    /// <summary>The account's CurrentBalance (A42 normal direction) from the flat chart.</summary>
    public static async Task<decimal> CurrentBalanceAsync(this SapKit k, string code) => (await k.AccountsAsync())[code].D("currentBalance");

    /// <summary>A new active leaf (level 4) under the parent of an existing leaf.</summary>
    public static async Task<JsonElement> NewLeafAsync(this SapKit k, string siblingCode, string code, string name)
    {
        var all = await k.AccountsAsync();
        var parent = all[siblingCode].G("parentUuid");
        return await k.Ok(k.CreateAccount(parent, code, name, all[siblingCode].S("accountType")), $"create account {code}");
    }

    /// <summary>A bulk SQL statement on the factory's (LocalDB) database with a 10-minute timeout.</summary>
    public static async Task<int> ExecuteLongAsync(this SapWebApplicationFactory f, string sql, params (string Name, object Value)[] parameters)
    {
        var cs = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(f.Services)["Data:mainOrg"]!;
        cs.Should().Contain("(localdb)");
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 600;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The uuid of the single (latest) journal of a source document.</summary>
    public static async Task<Guid> JournalUuidAsync(this SapWebApplicationFactory f, Guid org, Guid source)
    {
        var rows = await f.QueryAsync("SELECT Uuid FROM finance.JournalEntries WHERE OrganizationId = @o AND SourceDocumentUuid = @s ORDER BY Id DESC",
            ("@o", org), ("@s", source));
        rows.Should().NotBeEmpty($"a journal for source {source}");
        return (Guid)rows[0]["Uuid"]!;
    }

    /// <summary>The uuids of every journal of a source document, oldest first.</summary>
    public static async Task<List<Guid>> JournalUuidsAsync(this SapWebApplicationFactory f, Guid org, Guid source) =>
        (await f.QueryAsync("SELECT Uuid FROM finance.JournalEntries WHERE OrganizationId = @o AND SourceDocumentUuid = @s ORDER BY Id",
            ("@o", org), ("@s", source))).Select(r => (Guid)r["Uuid"]!).ToList();

    public static Task<HttpClient> UserWithAsync(this JeOrg o, string tag, params string[] codes) => o.K.LoginWithPermissionsAsync(tag, codes);

    public static readonly System.Text.RegularExpressions.Regex NumberFormat = new(@"^[A-Z]+/\d{4}/\d{4,}$");
}
