using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A47 RR on real SQL Server (LocalDB only — never the shared Azure database), a throw-away database per run: the reversal's
/// save order (self-referencing ReversesEntryId / ReversedByEntryId, CK_JournalEntries_ReversedStatus), the balance UPDATEs, the
/// auto-reconciliation rows (CK_JournalEntryLines_Reconciled), two concurrent reversals of one entry (application lock + unique
/// reversal key: one wins, the other 409), reconcile / unreconcile, and two concurrent reconciles sharing a line (account lock:
/// one wins, the other RC-004).
/// </summary>
[Trait("Category", "LocalDb")]
public sealed class LocalDbReversalReconciliationTests : IDisposable
{
    private static readonly Guid Acme = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private readonly string _dbName = "A47_RrTests_" + Guid.NewGuid().ToString("N")[..12];
    private string Conn(string db) => $"Server=(localdb)\\MSSQLLocalDB;Database={db};Trusted_Connection=True;TrustServerCertificate=True";

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var c = new SqlConnection(Conn(_dbName));
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    private async Task<PostingHarness> CreateAsync()
    {
        await using (var master = new SqlConnection(Conn("master")))
        {
            await master.OpenAsync();
            await new SqlCommand($"CREATE DATABASE [{_dbName}]", master).ExecuteNonQueryAsync();
        }
        var h = new PostingHarness(store: o => o.UseSqlServer(Conn(_dbName)));
        await h.Db.Database.MigrateAsync();
        await h.SetupGlAsync(enable: false);
        await h.Get<JournalTypeSeeder>().EnsureAsync(h.OrgA);
        return h;
    }

    private static AccountingCaller Clerk(PostingHarness h) => new(h.OrgA, 7, AccountingCaller.AllGlCodes, "Cleo Clerk");

    private static async Task<JournalEntryDetailModel> PostAsync(PostingHarness h, string type, DateOnly date,
        params (string Code, decimal Debit, decimal Credit, bool Partner)[] lines)
    {
        using var scope = h.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<Data.AccountingDbContext>();
        var accounts = await db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().Where(a => a.OrganizationId == h.OrgA)
            .ToDictionaryAsync(a => a.AccountCode, a => a.Uuid);
        var typeUuid = await db.JournalTypes.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == h.OrgA && t.Code == type)
            .Select(t => t.Uuid).SingleAsync();
        return await sp.GetRequiredService<IJournalEntryService>().CreateAsync(Clerk(h), new CreateJournalEntryRequest
        {
            JournalTypeUuid = typeUuid, EntryDate = date, PostImmediately = true,
            Lines = lines.Select(l => new JournalEntryLineRequest
            {
                AccountUuid = accounts[l.Code], Debit = l.Debit, Credit = l.Credit,
                PartnerUuid = l.Partner ? Acme : null, PartnerType = l.Partner ? "Customer" : null,
            }).ToList(),
        });
    }

    private static Guid Ar(JournalEntryDetailModel e) => e.Lines.Single(l => l.Account.AccountCode == "1113").LineUuid;

    [Fact]
    public async Task Reverse_and_reconcile_on_sql_server_including_concurrent_writers()
    {
        using var h = await CreateAsync();

        // ── reverse: save order, constraints, balances, auto-reconciliation ────────────────────────────────────────
        var expense = await PostAsync(h, SystemJournalTypes.Misc, new DateOnly(2026, 10, 15), ("5112", 250, 0, false), ("1111", 0, 250, false));
        h.NewScope();
        var result = await h.Get<IJournalReversalService>().ReverseAsync(Clerk(h), expense.Uuid, new ReverseJournalEntryRequest { Reason = "Incorrect amount" });
        result.Reversal.Status.Should().Be(JournalStatuses.Posted);
        result.ReconcileGroupNumbers.Should().HaveCount(2);
        (await ScalarAsync<string>($"SELECT Status FROM finance.JournalEntries WHERE Uuid = '{expense.Uuid}'")).Should().Be(JournalStatuses.Reversed);
        (await ScalarAsync<int>($"""
            SELECT COUNT(*) FROM finance.JournalEntries o JOIN finance.JournalEntries r ON r.Id = o.ReversedByEntryId AND r.ReversesEntryId = o.Id
            WHERE o.Uuid = '{expense.Uuid}' AND r.Uuid = '{result.Reversal.Uuid}' AND r.IdempotencyKey = 'reversal:{expense.Uuid}'
            """)).Should().Be(1);
        (await ScalarAsync<int>($"""
            SELECT COUNT(*) FROM finance.JournalEntryLines l JOIN finance.JournalEntries e ON e.Id = l.JournalEntryId
            WHERE e.Uuid IN ('{expense.Uuid}', '{result.Reversal.Uuid}') AND l.Reconciled = 1 AND l.AmountResidual = 0 AND l.ReconcileGroupId IS NOT NULL
              AND l.ClearingEntryId = (SELECT Id FROM finance.JournalEntries WHERE Uuid = '{result.Reversal.Uuid}')
            """)).Should().Be(4);
        h.NewScope();
        (await h.BalanceAsync("5112")).Should().Be(0);
        (await h.BalanceAsync("1111")).Should().Be(0);

        // ── two reversals of one entry at once: one wins, the other is refused, balances move once ──────────────────
        var twice = await PostAsync(h, SystemJournalTypes.Misc, new DateOnly(2026, 10, 16), ("5112", 80, 0, false), ("1111", 0, 80, false));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var scope = h.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IJournalReversalService>()
                    .ReverseAsync(Clerk(h), twice.Uuid, new ReverseJournalEntryRequest { Reason = "Twice" });
                return "ok";
            }
            catch (JournalConflictException ex) { return ex.Message; }
        }));
        outcomes.Should().BeEquivalentTo(["ok", "Entry has already been reversed"]);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.JournalEntries WHERE IdempotencyKey = 'reversal:{twice.Uuid}'")).Should().Be(1);
        h.NewScope();
        (await h.BalanceAsync("5112")).Should().Be(0);

        // ── reconcile, unreconcile, partial ───────────────────────────────────────────────────────────────────────
        var invoice = await PostAsync(h, SystemJournalTypes.Sale, new DateOnly(2026, 10, 1), ("1113", 1000, 0, true), ("4111", 0, 1000, true));
        var pay600 = await PostAsync(h, SystemJournalTypes.Cash, new DateOnly(2026, 10, 5), ("1111", 600, 0, false), ("1113", 0, 600, true));
        var pay400 = await PostAsync(h, SystemJournalTypes.Cash, new DateOnly(2026, 10, 6), ("1111", 400, 0, false), ("1113", 0, 400, true));
        h.NewScope();
        var recon = h.Get<IReconciliationService>();
        var partial = await recon.ReconcileAsync(Clerk(h), new ReconcileRequest { LineUuids = [Ar(invoice), Ar(pay600)] });
        partial.ReconcileType.Should().Be(ReconcileTypes.Partial);
        (await ScalarAsync<decimal>($"SELECT AmountResidual FROM finance.JournalEntryLines WHERE Uuid = '{Ar(invoice)}'")).Should().Be(400);
        h.NewScope();
        (await h.Get<IReconciliationService>().UnreconcileAsync(Clerk(h), partial.Uuid)).LinesRestored.Should().Be(2);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.ReconcileGroups WHERE Uuid = '{partial.Uuid}'")).Should().Be(0);

        // ── two reconciles sharing the invoice line at once: the account lock lets one through ──────────────────────
        var attempts = await Task.WhenAll(new[] { Ar(pay600), Ar(pay400) }.Select(async pay =>
        {
            using var scope = h.CreateScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IReconciliationService>()
                    .ReconcileAsync(Clerk(h), new ReconcileRequest { LineUuids = [Ar(invoice), pay] });
                return "ok";
            }
            catch (JournalConflictException ex) { return ex.ErrorCode; }
        }));
        attempts.Should().BeEquivalentTo(["ok", ReconcileErrorCodes.AlreadyInGroup]);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.ReconcileGroups")).Should().Be(5, "2 + 2 reversal groups + the winner");

        h.NewScope();
        var open = await h.Get<IReconciliationService>().GetOpenItemsAsync(Clerk(h),
            new OpenItemsQuery { AccountUuid = invoice.Lines.Single(l => l.Account.AccountCode == "1113").Account.Uuid });
        open.Should().HaveCount(2, "the partially paid invoice and the payment that lost");
        var suggestions = await h.Get<IReconciliationService>().SuggestMatchesAsync(Clerk(h), new ReconcileSuggestRequest
        {
            AccountUuid = open[0].Account.Uuid, LineUuids = [open.Single(o => o.AmountResidual < 0).LineUuid],
        });
        suggestions.Should().BeEmpty("the only debit is held by a partial reconciliation");
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
