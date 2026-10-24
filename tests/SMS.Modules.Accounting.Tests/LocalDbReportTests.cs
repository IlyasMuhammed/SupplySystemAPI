using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using Xunit;
using Xunit.Abstractions;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A47 RPT on real SQL Server (LocalDB only — never the shared Azure database), a throw-away database: the trial balance,
/// balance and ledger queries translate to SQL and match a hand-written T-SQL aggregate over 12,000+ journal entries
/// (task 427 &lt; 2 s; task 428 a 6,000-line account ledger, deep page); then the period close and the year-end close run on
/// SQL Server (conditional UPDATEs, sequence lock, transaction), including two concurrent year-end closes (yearclose: key).
/// </summary>
[Trait("Category", "LocalDb")]
public sealed class LocalDbReportTests : IDisposable
{
    private const int Entries = 12_000;
    private readonly ITestOutputHelper _out;
    private readonly string _dbName = "A47_ReportTests_" + Guid.NewGuid().ToString("N")[..12];
    private string Conn(string db) => $"Server=(localdb)\\MSSQLLocalDB;Database={db};Trusted_Connection=True;TrustServerCertificate=True";

    public LocalDbReportTests(ITestOutputHelper output) => _out = output;

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var c = new SqlConnection(Conn(_dbName));
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c) { CommandTimeout = 300 };
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    private async Task ExecAsync(string sql)
    {
        await using var c = new SqlConnection(Conn(_dbName));
        await c.OpenAsync();
        await new SqlCommand(sql, c) { CommandTimeout = 300 }.ExecuteNonQueryAsync();
    }

    private static async Task<(T Result, long Ms)> TimeAsync<T>(Func<Task<T>> work)
    {
        var sw = Stopwatch.StartNew();
        var r = await work();
        return (r, sw.ElapsedMilliseconds);
    }

    [Fact]
    public async Task Trial_balance_ledger_and_year_end_close_on_sql_server_with_12k_entries()
    {
        await using (var master = new SqlConnection(Conn("master")))
        {
            await master.OpenAsync();
            await new SqlCommand($"CREATE DATABASE [{_dbName}]", master).ExecuteNonQueryAsync();
        }
        using var h = new PostingHarness(store: o => o.UseSqlServer(Conn(_dbName)));
        await h.Db.Database.MigrateAsync();
        await h.SetupGlAsync(enable: false);
        var org = h.OrgA;
        var year = await h.Db.FiscalYears.IgnoreQueryFilters().SingleAsync(y => y.OrganizationId == org);
        h.Db.FiscalPeriods.Add(new FiscalPeriod { OrganizationId = org, FiscalYearId = year.Id, PeriodNumber = 13, PeriodName = "Year-End Adjustment",
            StartDate = year.EndDate, EndDate = year.EndDate });
        await h.Db.SaveChangesAsync();
        await h.Get<JournalTypeSeeder>().EnsureAsync(org);
        var admin = new AccountingCaller(org, 7, AccountingCaller.AllGlCodes, "Ada Admin");

        // 12,000 posted MISC entries over 2026 (even n: 1113 Dr / 4111 Cr; odd n: 5111 or 5112 Dr / 1111 or 2111 Cr),
        // + 100 drafts and 50 posted closing-flagged adjustments (CLOSE type) on the year end. Set-based T-SQL.
        await ExecAsync($"""
            SET NOCOUNT ON;
            DECLARE @o uniqueidentifier = '{org}';
            DECLARE @misc int = (SELECT Id FROM finance.JournalTypes WHERE OrganizationId = @o AND Code = 'MISC');
            DECLARE @close int = (SELECT Id FROM finance.JournalTypes WHERE OrganizationId = @o AND Code = 'CLOSE');
            DECLARE @a1111 int = (SELECT Id FROM finance.ChartOfAccounts WHERE OrganizationId = @o AND AccountCode = '1111');
            DECLARE @a1113 int = (SELECT Id FROM finance.ChartOfAccounts WHERE OrganizationId = @o AND AccountCode = '1113');
            DECLARE @a2111 int = (SELECT Id FROM finance.ChartOfAccounts WHERE OrganizationId = @o AND AccountCode = '2111');
            DECLARE @a4111 int = (SELECT Id FROM finance.ChartOfAccounts WHERE OrganizationId = @o AND AccountCode = '4111');
            DECLARE @a5111 int = (SELECT Id FROM finance.ChartOfAccounts WHERE OrganizationId = @o AND AccountCode = '5111');
            DECLARE @a5112 int = (SELECT Id FROM finance.ChartOfAccounts WHERE OrganizationId = @o AND AccountCode = '5112');
            DECLARE @a3111 int = (SELECT Id FROM finance.ChartOfAccounts WHERE OrganizationId = @o AND AccountCode = '3111');
            ;WITH n AS (SELECT TOP ({Entries + 150}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO finance.JournalEntries (Uuid, OrganizationId, EntryNumber, EntryType, EntryDate, Status, TotalDebit, TotalCredit, CurrencyCode,
                CreatedAt, ModifiedAt, IdempotencyKey, JournalTypeId, IsOpeningEntry, IsClosingEntry, LineCount, Description)
            SELECT NEWID(), @o, CONCAT('PERF/2026/', i), 'MANUAL_JOURNAL',
                   CASE WHEN i > {Entries} THEN '2026-12-31' ELSE DATEADD(day, i % 365, '2026-01-01') END,
                   CASE WHEN i BETWEEN {Entries + 1} AND {Entries + 100} THEN 'Draft' ELSE 'Posted' END,
                   10 + i % 97, 10 + i % 97, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), CONCAT('perf:', i),
                   CASE WHEN i > {Entries + 100} THEN @close ELSE @misc END, 0, CASE WHEN i > {Entries + 100} THEN 1 ELSE 0 END, 2, CONCAT('Perf ', i)
            FROM n;
            INSERT INTO finance.JournalEntryLines (Uuid, OrganizationId, JournalEntryId, LineNumber, AccountId, DebitAmount, CreditAmount, AmountResidual, CreatedAt)
            SELECT NEWID(), @o, j.Id, 1,
                   CASE WHEN j.IsClosingEntry = 1 THEN @a5112 WHEN k.i % 2 = 0 THEN @a1113 WHEN k.i % 3 = 0 THEN @a5111 ELSE @a5112 END,
                   j.TotalDebit, 0, j.TotalDebit, SYSUTCDATETIME()
            FROM finance.JournalEntries j CROSS APPLY (SELECT CAST(SUBSTRING(j.EntryNumber, 11, 10) AS int) AS i) k WHERE j.OrganizationId = @o
            UNION ALL
            SELECT NEWID(), @o, j.Id, 2,
                   CASE WHEN j.IsClosingEntry = 1 THEN @a2111 WHEN k.i % 2 = 0 THEN @a4111 WHEN k.i % 5 = 0 THEN @a2111 ELSE @a1111 END,
                   0, j.TotalCredit, -j.TotalCredit, SYSUTCDATETIME()
            FROM finance.JournalEntries j CROSS APPLY (SELECT CAST(SUBSTRING(j.EntryNumber, 11, 10) AS int) AS i) k WHERE j.OrganizationId = @o;
            """);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.JournalEntries WHERE OrganizationId = '{org}'")).Should().Be(Entries + 150);

        // Expected (hand-written T-SQL): Posted lines through 2026-12-31 without closing entries, per account.
        async Task<decimal> ExpectedAsync(string code, string side, string from, string to, bool closing = false) => await ScalarAsync<decimal>($"""
            SELECT ISNULL(SUM(l.{side}), 0) FROM finance.JournalEntryLines l JOIN finance.JournalEntries j ON j.Id = l.JournalEntryId
            JOIN finance.ChartOfAccounts a ON a.Id = l.AccountId
            WHERE j.OrganizationId = '{org}' AND j.Status IN ('Posted','Reversed') AND a.AccountCode = '{code}'
              AND j.EntryDate BETWEEN '{from}' AND '{to}' {(closing ? "" : "AND j.IsClosingEntry = 0")}
            """);

        h.NewScope();
        var reports = h.Get<ITrialBalanceService>();
        await reports.GenerateAsync(admin, new TrialBalanceQuery { FromDate = new(2026, 1, 1), ToDate = new(2026, 1, 31) }); // warm-up (model, plan cache)

        // Task 427 — the full year.
        var (tb, tbMs) = await TimeAsync(() => reports.GenerateAsync(admin, new TrialBalanceQuery { FiscalYearUuid = year.Uuid }));
        tb.IsBalanced.Should().BeTrue();
        tb.Lines.Single(l => l.Account.AccountCode == "1113").PeriodDebit.Should().Be(await ExpectedAsync("1113", "DebitAmount", "2026-01-01", "2026-12-31"));
        tb.Lines.Single(l => l.Account.AccountCode == "5112").PeriodDebit.Should().Be(await ExpectedAsync("5112", "DebitAmount", "2026-01-01", "2026-12-31"));
        tb.Lines.Single(l => l.Account.AccountCode == "2111").ClosingCredit.Should().Be(await ExpectedAsync("2111", "CreditAmount", "2026-01-01", "2026-12-31"));

        // October with opening balances (opening = Jan–Sep), closing entries included.
        var (oct, octMs) = await TimeAsync(() => reports.GenerateAsync(admin, new TrialBalanceQuery
            { FromDate = new(2026, 10, 1), ToDate = new(2026, 10, 31), IncludeClosingEntries = true }));
        oct.IsBalanced.Should().BeTrue();
        oct.Lines.Single(l => l.Account.AccountCode == "4111").OpeningCredit.Should().Be(await ExpectedAsync("4111", "CreditAmount", "2026-01-01", "2026-09-30"));
        oct.Lines.Single(l => l.Account.AccountCode == "4111").PeriodCredit.Should().Be(await ExpectedAsync("4111", "CreditAmount", "2026-10-01", "2026-10-31"));
        var withClose = await reports.GenerateAsync(admin, new TrialBalanceQuery { FiscalYearUuid = year.Uuid, IncludeClosingEntries = true });
        withClose.Lines.Single(l => l.Account.AccountCode == "5112").PeriodDebit
            .Should().Be(await ExpectedAsync("5112", "DebitAmount", "2026-01-01", "2026-12-31", closing: true));

        var (csv, csvMs) = await TimeAsync(() => reports.ExportCsvAsync(admin, new TrialBalanceQuery { FiscalYearUuid = year.Uuid }));
        csv.Content.Length.Should().BeGreaterThan(100);

        // Task 428 — the 6,000-line receivable ledger: page 1 and the last page (running balance from a SUM over TOP n).
        var ar = await h.Db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.OrganizationId == org && a.AccountCode == "1113");
        var q = new AccountLedgerQuery { FromDate = new(2026, 1, 1), ToDate = new(2026, 12, 31), PageSize = 50 };
        var (first, p1Ms) = await TimeAsync(() => reports.GetAccountLedgerAsync(admin, ar.Uuid, q));
        first.Page.TotalRecords.Should().Be(Entries / 2);
        q.Page = first.Page.TotalPages;
        var (last, lastMs) = await TimeAsync(() => reports.GetAccountLedgerAsync(admin, ar.Uuid, q));
        last.Page.Data[^1].RunningBalance.Should().Be(last.ClosingBalance);
        last.ClosingBalance.Should().Be(await ExpectedAsync("1113", "DebitAmount", "2026-01-01", "2026-12-31", closing: true));
        var (bal, balMs) = await TimeAsync(() => reports.GetAccountBalanceAsync(admin, ar.Uuid, new DateOnly(2026, 6, 30)));
        bal.Debit.Should().Be(await ExpectedAsync("1113", "DebitAmount", "2026-01-01", "2026-06-30", closing: true));

        _out.WriteLine($"TB year {tbMs} ms, TB Oct {octMs} ms, CSV {csvMs} ms, ledger p1 {p1Ms} ms, ledger last page {lastMs} ms, balance {balMs} ms " +
                       $"({Entries + 150} entries, {(Entries + 150) * 2} lines)");
        tbMs.Should().BeLessThan(2000, "task 427 target");
        octMs.Should().BeLessThan(2000);
        lastMs.Should().BeLessThan(2000, "task 428 target");

        // Period close on SQL Server: drafts block the lock (BR-PC-001), then lock 1–12 through the service.
        var close = h.Get<IPeriodCloseService>();
        var periods = await h.Db.FiscalPeriods.IgnoreQueryFilters().AsNoTracking().Where(p => p.FiscalYearId == year.Id).OrderBy(p => p.PeriodNumber).ToListAsync();
        await FluentActions.Awaiting(() => close.ClosePeriodAsync(admin, periods[11].Uuid, "Locked"))
            .Should().ThrowAsync<JournalConflictException>().Where(e => e.ErrorCode == PeriodCloseErrorCodes.PeriodDrafts);
        var overview = await close.GetPeriodStatusesAsync(admin, year.Uuid);
        overview.Periods.Sum(p => p.PostedEntryCount + p.DraftEntryCount).Should().Be(Entries + 150);
        await ExecAsync($"DELETE FROM finance.JournalEntries WHERE OrganizationId = '{org}' AND Status = 'Draft'");
        foreach (var p in periods.Where(p => p.PeriodNumber <= 12)) await close.ClosePeriodAsync(admin, p.Uuid, "Locked");

        // Year-end close twice at once: one closing entry, both answer with it.
        h.NewScope();
        var preview = await h.Get<IPeriodCloseService>().PreviewYearEndCloseAsync(admin, year.Uuid, null);
        preview.CanExecute.Should().BeTrue(string.Join("; ", preview.Blockers));
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var scope = h.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPeriodCloseService>().CloseYearAsync(admin, year.Uuid, null);
        }));
        results.Select(r => r.ClosingEntry!.Uuid).Distinct().Should().ContainSingle();
        results.Count(r => !r.AlreadyClosed).Should().BeLessThanOrEqualTo(1);
        results.Should().OnlyContain(r => r.NetIncomeTransferred == preview.NetIncome);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.JournalEntries WHERE OrganizationId = '{org}' AND IdempotencyKey LIKE 'yearclose:%'")).Should().Be(1);
        (await ScalarAsync<string>($"SELECT EntryNumber FROM finance.JournalEntries WHERE OrganizationId = '{org}' AND IdempotencyKey LIKE 'yearclose:%'")).Should().Be("CLOSE/2026/0001");
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.FiscalPeriods WHERE FiscalYearId = {year.Id} AND Status <> 'Closed'")).Should().Be(0);
        (await ScalarAsync<bool>($"SELECT IsClosed FROM finance.FiscalYears WHERE Id = {year.Id}")).Should().BeTrue();

        h.NewScope();
        var after = await h.Get<ITrialBalanceService>().GenerateAsync(admin, new TrialBalanceQuery { FiscalYearUuid = year.Uuid, IncludeClosingEntries = true });
        after.Lines.Where(l => l.RootType is RootTypes.Revenue or RootTypes.Expense).Should().OnlyContain(l => l.Balance == 0m);
        after.IsBalanced.Should().BeTrue();
    }

    public void Dispose()
    {
        SqlConnection.ClearAllPools();
        using var master = new SqlConnection(Conn("master"));
        master.Open();
        new SqlCommand($"IF DB_ID(N'{_dbName}') IS NOT NULL BEGIN ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}]; END",
            master).ExecuteNonQuery();
    }
}
