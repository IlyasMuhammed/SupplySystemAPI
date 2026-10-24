using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using Xunit.Abstractions;
using static SMS.Integration.Tests.JournalEngine.Je;

namespace SMS.Integration.Tests.JournalEngine;

/// <summary>
/// A47 (QA) — performance on LocalDB, tasks 427–431. Bulk journals are cloned in SQL from one posted API entry (every column
/// copied, new uuid/number, dates spread over the year), then the API is timed. Timings go to the test output and to
/// %TEMP%\a47qa-perf.txt; a test fails only when wrong or grossly slow (&gt; <see cref="Gross"/>). Contract target: 10k+ entries &lt; 2 s.
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~JournalEngine.JePerformanceE2ETests</c>.</para>
/// </summary>
public sealed class JePerformanceE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private static readonly TimeSpan Gross = TimeSpan.FromSeconds(30);
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;
    private readonly ITestOutputHelper _out;

    public JePerformanceE2ETests(SapWebApplicationFactory factory, ITestOutputHelper output)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A47");
        _out = output;
    }

    private static DateTime D(int month, int day) => new(Y, month, day);

    private void Record(string task, string what, TimeSpan t)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} A47 perf {task} {what}: {t.TotalMilliseconds:0} ms";
        _out.WriteLine(line);
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "a47qa-perf.txt"), line + Environment.NewLine); } catch { /* best effort */ }
    }

    private async Task<(Api Api, TimeSpan Time)> TimeAsync(Func<Task<Api>> call)
    {
        await call(); // warm-up (plan cache, JIT)
        var sw = Stopwatch.StartNew();
        var api = await call();
        sw.Stop();
        return (api, sw.Elapsed);
    }

    /// <summary>
    /// <paramref name="count"/> copies of the posted entry <paramref name="template"/> (and its lines), numbered P{tag}-n, dated
    /// template date − (n mod spreadDays). Column lists come from sys.columns, so the copy follows whatever CORE's schema is.
    /// </summary>
    private async Task CloneAsync(Guid org, Guid template, int count, string tag, int spreadDays)
    {
        async Task<List<string>> ColumnsAsync(string table) =>
            (await _f.QueryAsync(
                "SELECT c.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
                "WHERE c.object_id = OBJECT_ID(@t) AND c.is_identity = 0 AND c.is_computed = 0 AND t.name NOT IN ('timestamp','rowversion') ORDER BY c.column_id",
                ("@t", table))).Select(r => (string)r["name"]!).ToList();

        var tplId = Convert.ToInt32((await _f.QueryAsync("SELECT Id FROM finance.JournalEntries WHERE Uuid = @u", ("@u", template)))[0]["Id"]);
        var head = await ColumnsAsync("finance.JournalEntries");
        var over = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Uuid"] = "NEWID()",
            ["EntryNumber"] = $"CONCAT('P{tag}-', n.n)",
            ["IdempotencyKey"] = "NULL",
            ["EntryDate"] = $"DATEADD(day, -(n.n % {spreadDays}), t.EntryDate)",
            ["ReversedByEntryId"] = "NULL",
            ["ReversesEntryId"] = "NULL",
        };
        var sql = new StringBuilder();
        sql.Append($"INSERT INTO finance.JournalEntries ({string.Join(",", head.Select(c => $"[{c}]"))}) ");
        sql.Append($"SELECT {string.Join(",", head.Select(c => over.TryGetValue(c, out var v) ? v : $"t.[{c}]"))} ");
        sql.Append("FROM finance.JournalEntries t CROSS JOIN (SELECT TOP (@n) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n ");
        sql.Append("FROM sys.all_objects a CROSS JOIN sys.all_objects b) n WHERE t.Id = @tpl;");
        await _f.ExecuteLongAsync(sql.ToString(), ("@n", count), ("@tpl", tplId));

        var lines = await ColumnsAsync("finance.JournalEntryLines");
        var lover = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Uuid"] = "NEWID()", ["JournalEntryId"] = "e.Id", ["ReconcileGroupId"] = "NULL", ["ClearingEntryId"] = "NULL",
            ["Reconciled"] = "0", ["ClearingDate"] = "NULL",
        };
        var lsql = $"INSERT INTO finance.JournalEntryLines ({string.Join(",", lines.Select(c => $"[{c}]"))}) " +
                   $"SELECT {string.Join(",", lines.Select(c => lover.TryGetValue(c, out var v) ? v : $"l.[{c}]"))} " +
                   "FROM finance.JournalEntries e JOIN finance.JournalEntryLines l ON l.JournalEntryId = @tpl " +
                   $"WHERE e.OrganizationId = @o AND e.EntryNumber LIKE 'P{tag}-%';";
        await _f.ExecuteLongAsync(lsql, ("@tpl", tplId), ("@o", org));
        (await _f.RowCountAsync($"SELECT COUNT(*) FROM finance.JournalEntries WHERE OrganizationId = @o AND EntryNumber LIKE 'P{tag}-%'", ("@o", org)))
            .Should().Be(count);
    }

    [Fact]
    public async Task Perf_427_trial_balance_10k_entries()
    {
        var o = await _root.NewJeOrgAsync("PF7");
        var misc = await o.K.TypeAsync("MISC");
        var tpl = await o.PostedAsync(misc, o.Acc("DEFAULT_CASH"), o.Acc("DEFAULT_REVENUE"), 10m, D(10, 1));
        await CloneAsync(o.OrgId, tpl.G("uuid"), 10_000, "A", 270);

        var (api, t) = await TimeAsync(() => o.K.TrialBalance(D(1, 1), D(12, 31), opening: true));
        Record("427", "trial balance, 10 001 entries / 20 002 lines", t);
        api.Status.Should().Be(HttpStatusCode.OK, api.ToString());
        api.Result.TbRow(o.Code("DEFAULT_CASH")).D("periodDebit").Should().Be(10m * 10_001);
        api.Result.B("isBalanced").Should().BeTrue();
        t.Should().BeLessThan(Gross);

        var (q, tq) = await TimeAsync(() => o.K.TrialBalance(D(4, 1), D(6, 30), opening: true));
        Record("427", "trial balance Apr–Jun with opening, 10k entries", tq);
        q.Status.Should().Be(HttpStatusCode.OK);
        var csv = Stopwatch.StartNew();
        using var file = await o.K.Admin.GetAsync($"{Tb}/export?fromDate={Iso(D(1, 1))}&toDate={Iso(D(12, 31))}");
        csv.Stop();
        Record("427", "trial balance CSV export, 10k entries", csv.Elapsed);
        file.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Perf_428_account_ledger_5k_lines()
    {
        var o = await _root.NewJeOrgAsync("PF8");
        var misc = await o.K.TypeAsync("MISC");
        var cash = o.Acc("DEFAULT_CASH");
        var tpl = await o.PostedAsync(misc, cash, o.Acc("DEFAULT_REVENUE"), 3m, D(10, 1));
        await CloneAsync(o.OrgId, tpl.G("uuid"), 5_000, "B", 270);

        var (first, t1) = await TimeAsync(() => o.K.Ledger(cash, D(1, 1), D(12, 31), 1, 50));
        Record("428", "account ledger page 1/50, 5 001 lines", t1);
        first.Status.Should().Be(HttpStatusCode.OK, first.ToString());
        first.Result.P("page").I("totalRecords").Should().Be(5_001);
        first.Result.D("closingBalance").Should().Be(3m * 5_001);
        var (last, t2) = await TimeAsync(() => o.K.Ledger(cash, D(1, 1), D(12, 31), 101, 50));
        Record("428", "account ledger last page, 5 001 lines", t2);
        last.Result.P("page").A("data").Last().D("runningBalance").Should().Be(3m * 5_001, "the running balance reaches the closing balance");
        (t1 + t2).Should().BeLessThan(Gross);
        var (bal, tb) = await TimeAsync(() => o.K.Balance(cash, D(12, 31)));
        Record("428", "account balance as of year end, 5 001 lines", tb);
        bal.Result.D("balance").Should().Be(3m * 5_001);
    }

    [Fact]
    public async Task Perf_429_ten_parallel_creates_get_unique_numbers()
    {
        var o = await _root.NewJeOrgAsync("PF9");
        var misc = await o.K.TypeAsync("MISC");
        JeLine[] lines = [new JeLine(o.Acc("DEFAULT_CASH"), 1m, 0m), new JeLine(o.Acc("DEFAULT_REVENUE"), 0m, 1m)];
        var sw = Stopwatch.StartNew();
        var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => o.K.CreateEntry(Body(misc, Today, lines, post: true))));
        sw.Stop();
        Record("429", "10 parallel create+post", sw.Elapsed);
        answers.Should().OnlyContain(a => a.Status == HttpStatusCode.OK, string.Join("\n", answers.Select(a => a.ToString())));
        var numbers = answers.Select(a => a.Result.S("entryNumber")!).ToList();
        numbers.Should().OnlyHaveUniqueItems();
        numbers.Select(n => int.Parse(n.Split('/')[2])).OrderBy(n => n).Should().Equal(Enumerable.Range(1, 10), "gap-free");
        sw.Elapsed.Should().BeLessThan(Gross);
    }

    [Fact]
    public async Task Perf_430_list_with_filters_50k_entries()
    {
        var o = await _root.NewJeOrgAsync("PF0");
        var misc = await o.K.TypeAsync("MISC");
        var cash = o.Acc("DEFAULT_CASH");
        var tpl = await o.PostedAsync(misc, cash, o.Acc("DEFAULT_REVENUE"), 1m, D(10, 1), "A47 QA perf list");
        await CloneAsync(o.OrgId, tpl.G("uuid"), 50_000, "C", 270);

        var query = $"status=Posted&journalTypeUuid={misc}&fromDate={Iso(D(3, 1))}&toDate={Iso(D(9, 30))}&accountUuid={cash}&search=perf&sortBy=totalDebit&page=3&pageSize=50";
        var (api, t) = await TimeAsync(() => o.K.ListEntries(query));
        Record("430", "list, 5 filters + search + sort, page 3, 50 001 entries", t);
        api.Status.Should().Be(HttpStatusCode.OK, api.ToString());
        api.Result.I("totalRecords").Should().BeGreaterThan(0);
        api.Result.A("data").Should().HaveCount(50);
        var (plain, tp) = await TimeAsync(() => o.K.ListEntries("page=1&pageSize=25"));
        Record("430", "list, no filter, page 1, 50 001 entries", tp);
        plain.Result.I("totalRecords").Should().Be(50_001);
        (t + tp).Should().BeLessThan(Gross);
    }

    [Fact]
    public async Task Perf_431_year_end_close_100_pl_accounts()
    {
        var o = await _root.NewJeOrgAsync("PF1");
        var k = o.K;
        string revCode = o.Code("DEFAULT_REVENUE"), expCode = o.Code("DEFAULT_PURCHASE");
        var lines = new List<JeLine>();
        decimal net = 0m;
        for (var i = 0; i < 60; i++)
        {
            var r = await k.NewLeafAsync(revCode, $"{revCode}{i:D2}", $"QA perf revenue {i}");
            var e = await k.NewLeafAsync(expCode, $"{expCode}{i:D2}", $"QA perf expense {i}");
            lines.Add(new JeLine(r.G("uuid"), 0m, 10m + i));
            lines.Add(new JeLine(e.G("uuid"), 5m + i, 0m));
            net += 5m;
        }
        lines.Add(new JeLine(o.Acc("DEFAULT_CASH"), net, 0m));
        var misc = await k.TypeAsync("MISC");
        await k.CreateEntryOk(Body(misc, D(5, 5), lines, post: true), "one entry with 121 lines");
        var year = (await k.YearAsync()).G("uuid");
        await k.LockAllMonthsAsync(year);

        var (preview, tp) = await TimeAsync(() => k.YearPreview(year));
        Record("431", "year-end preview, 120 P&L accounts", tp);
        preview.Status.Should().Be(HttpStatusCode.OK, preview.ToString());
        preview.Result.D("netIncome").Should().Be(net);
        preview.Result.A("lines").Count.Should().BeGreaterThanOrEqualTo(120);

        var sw = Stopwatch.StartNew();
        var done = await k.Ok(k.YearExecute(year), "execute");
        sw.Stop();
        Record("431", "year-end execute, 120 P&L accounts", sw.Elapsed);
        var closing = await k.EntryAsync(done.P("closingEntry").G("uuid"));
        closing.I("lineCount").Should().Be(121, "120 P&L lines + retained earnings");
        (tp + sw.Elapsed).Should().BeLessThan(Gross);
    }
}
