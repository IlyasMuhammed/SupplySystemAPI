using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Accounting.Domain;
using SMS.Shared.Finance.Posting;
using Xunit;
using static SMS.Modules.Accounting.Tests.PostingHarness;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A47 GW on real SQL Server (LocalDB only, a throw-away database per test): the A47_GatewayReversalReconcile data fix (D-GW-5)
/// on A44 reversal pairs left open by the A47 backfill — marked, reconciled per account, numbered after the existing REC numbers,
/// a user's group and RR's own reversals left alone, re-run idempotent — and the gateway's update / void through the engine
/// (sequence UPDLOCK batch, app locks, self-referencing save) on SQL Server.
/// </summary>
[Trait("Category", "LocalDb")]
public sealed class LocalDbGatewayJournalTests : IDisposable
{
    private const string A47Engine = "20261024031212_A47_JournalEngine";
    private readonly string _dbName = "A47_GwTests_" + Guid.NewGuid().ToString("N")[..12];
    private string Conn(string db) => $"Server=(localdb)\\MSSQLLocalDB;Database={db};Trusted_Connection=True;TrustServerCertificate=True";

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var c = new SqlConnection(Conn(_dbName));
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    private async Task ExecAsync(string sql)
    {
        await using var c = new SqlConnection(Conn(_dbName));
        await c.OpenAsync();
        await new SqlCommand(sql, c).ExecuteNonQueryAsync();
    }

    private async Task<PostingHarness> CreateAsync(string? migrateTo = null)
    {
        await using (var master = new SqlConnection(Conn("master")))
        {
            await master.OpenAsync();
            await new SqlCommand($"CREATE DATABASE [{_dbName}]", master).ExecuteNonQueryAsync();
        }
        var h = new PostingHarness(store: o => o.UseSqlServer(Conn(_dbName)));
        await h.Db.GetService<IMigrator>().MigrateAsync(migrateTo);
        return h;
    }

    [Fact]
    public async Task Data_fix_marks_and_reconciles_the_open_A44_pairs_once()
    {
        using var h = await CreateAsync(A47Engine);
        await h.SetupGlAsync(enable: false);
        var org = h.OrgA;
        var year = DateTime.UtcNow.Year;
        int Id(string code) => h.Db.ChartOfAccounts.IgnoreQueryFilters().Single(a => a.OrganizationId == org && a.AccountCode == code).Id;
        await h.Get<SMS.Modules.Accounting.Services.JournalTypeSeeder>().EnsureAsync(org);
        var type = await ScalarAsync<int>($"SELECT Id FROM finance.JournalTypes WHERE OrganizationId = '{org}' AND Code = 'SALE'");

        // P1 (00001/00002): an A44 void pair written after A47 (not marked). P2 (00003/00004): an update pair the A47 backfill marked,
        // whose AR line a user already reconciled. P3 (00005/00006): an RR reversal (reversal: key) — not touched.
        await ExecAsync($"""
            DECLARE @o uniqueidentifier = '{org}';
            INSERT INTO finance.JournalEntries (Uuid, OrganizationId, EntryNumber, EntryType, EntryDate, Status, TotalDebit, TotalCredit, CurrencyCode, CreatedAt, ModifiedAt, IdempotencyKey, ReversesEntryId, JournalTypeId)
            VALUES (NEWID(), @o, 'JE-2026-00001', 'SALES_INVOICE', '2026-03-10', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:1', NULL, {type}),
                   (NEWID(), @o, 'JE-2026-00003', 'SALES_INVOICE', '2026-04-10', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:2', NULL, {type}),
                   (NEWID(), @o, 'SALE/2026/0001', 'SALES_INVOICE', '2026-05-10', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:3', NULL, {type});
            DECLARE @p1 int = (SELECT Id FROM finance.JournalEntries WHERE OrganizationId = @o AND EntryNumber = 'JE-2026-00001');
            DECLARE @p2 int = (SELECT Id FROM finance.JournalEntries WHERE OrganizationId = @o AND EntryNumber = 'JE-2026-00003');
            DECLARE @p3 int = (SELECT Id FROM finance.JournalEntries WHERE OrganizationId = @o AND EntryNumber = 'SALE/2026/0001');
            INSERT INTO finance.JournalEntries (Uuid, OrganizationId, EntryNumber, EntryType, EntryDate, Status, TotalDebit, TotalCredit, CurrencyCode, CreatedAt, ModifiedAt, IdempotencyKey, ReversesEntryId, JournalTypeId)
            VALUES (NEWID(), @o, 'JE-2026-00002', 'SALES_INVOICE', '2026-03-11', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:1:void', @p1, {type}),
                   (NEWID(), @o, 'JE-2026-00004', 'SALES_INVOICE', '2026-04-11', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:2', @p2, {type}),
                   (NEWID(), @o, 'SALE/2026/0002', 'SALES_INVOICE', '2026-05-11', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'reversal:' + CAST(NEWID() AS nvarchar(36)), @p3, {type});
            UPDATE finance.JournalEntries SET Status = 'Reversed', ReversedByEntryId = (SELECT Id FROM finance.JournalEntries WHERE ReversesEntryId = @p2) WHERE Id = @p2;
            UPDATE finance.JournalEntries SET Status = 'Reversed', ReversedByEntryId = (SELECT Id FROM finance.JournalEntries WHERE ReversesEntryId = @p3) WHERE Id = @p3;
            INSERT INTO finance.JournalEntryLines (Uuid, OrganizationId, JournalEntryId, LineNumber, AccountId, DebitAmount, CreditAmount, AmountResidual, Reconciled, CreatedAt)
            SELECT NEWID(), j.OrganizationId, j.Id, v.n, v.acc, v.d, v.c, v.d - v.c, 0, SYSUTCDATETIME() FROM finance.JournalEntries j
            CROSS APPLY (VALUES
                (1, {Id("1113")}, CASE WHEN j.ReversesEntryId IS NULL THEN 118 ELSE 0 END, CASE WHEN j.ReversesEntryId IS NULL THEN 0 ELSE 118 END),
                (2, {Id("4111")}, CASE WHEN j.ReversesEntryId IS NULL THEN 0 ELSE 118 END, CASE WHEN j.ReversesEntryId IS NULL THEN 118 ELSE 0 END)) v(n, acc, d, c)
            WHERE j.OrganizationId = @o;
            -- a user's partial match on P2's AR line, and an existing REC number of this year
            INSERT INTO finance.ReconcileGroups (Uuid, OrganizationId, GroupNumber, ReconcileType, AccountId, Amount, ReconciledAt)
            VALUES (NEWID(), @o, 'REC/{year}/0007', 'Partial', {Id("1113")}, 18, SYSUTCDATETIME());
            UPDATE finance.JournalEntryLines SET ReconcileGroupId = (SELECT Id FROM finance.ReconcileGroups WHERE GroupNumber = 'REC/{year}/0007'), AmountResidual = 100
            WHERE JournalEntryId = @p2 AND LineNumber = 1;
            """);

        var migrator = h.Db.GetService<IMigrator>();
        await migrator.MigrateAsync();

        // P1 marked (D-4).
        (await ScalarAsync<string>($"SELECT Status FROM finance.JournalEntries WHERE OrganizationId = '{org}' AND EntryNumber = 'JE-2026-00001'")).Should().Be("Reversed");
        (await ScalarAsync<int>($"""
            SELECT COUNT(*) FROM finance.JournalEntries o JOIN finance.JournalEntries r ON r.Id = o.ReversedByEntryId
            WHERE o.EntryNumber = 'JE-2026-00001' AND r.EntryNumber = 'JE-2026-00002' AND o.OrganizationId = '{org}'
            """)).Should().Be(1);
        // P1: both accounts; P2: revenue only (the AR line keeps the user's group); P3: none.
        async Task<int> ReconciledLines(string number) => await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM finance.JournalEntryLines l JOIN finance.JournalEntries j ON j.Id = l.JournalEntryId WHERE j.OrganizationId = '{org}' AND j.EntryNumber = '{number}' AND l.Reconciled = 1 AND l.AmountResidual = 0");
        (await ReconciledLines("JE-2026-00001"), await ReconciledLines("JE-2026-00002")).Should().Be((2, 2));
        (await ReconciledLines("JE-2026-00003"), await ReconciledLines("JE-2026-00004")).Should().Be((1, 1));
        (await ReconciledLines("SALE/2026/0001"), await ReconciledLines("SALE/2026/0002")).Should().Be((0, 0), "RR's own reversal is not touched");
        (await ScalarAsync<int>($"SELECT AmountResidual FROM finance.JournalEntryLines l JOIN finance.JournalEntries j ON j.Id = l.JournalEntryId WHERE j.EntryNumber = 'JE-2026-00003' AND l.LineNumber = 1"))
            .Should().Be(100, "the user's partial stays as it was");
        // Clearing = the reversal on its date; groups Full, numbered after REC/{year}/0007; the counter follows.
        (await ScalarAsync<int>($"""
            SELECT COUNT(*) FROM finance.JournalEntryLines l JOIN finance.JournalEntries r ON r.Id = l.ClearingEntryId
            WHERE l.OrganizationId = '{org}' AND l.Reconciled = 1 AND r.EntryNumber IN ('JE-2026-00002', 'JE-2026-00004') AND l.ClearingDate = r.EntryDate
            """)).Should().Be(6);
        (await ScalarAsync<string>($"SELECT STRING_AGG(GroupNumber, ',') WITHIN GROUP (ORDER BY GroupNumber) FROM finance.ReconcileGroups WHERE OrganizationId = '{org}' AND ReconcileType = 'Full'"))
            .Should().Be($"REC/{year}/0008,REC/{year}/0009,REC/{year}/0010");
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.ReconcileGroups WHERE OrganizationId = '{org}' AND ReconcileType = 'Full' AND Amount = 118 AND CurrencyCode = 'PKR'")).Should().Be(3);
        (await ScalarAsync<int>($"SELECT CurrentSequence FROM finance.JournalEntrySequences WHERE OrganizationId = '{org}' AND JournalTypeId IS NULL AND FiscalYearId IS NULL AND SequenceYear = {year}"))
            .Should().Be(10);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.JournalEntryLines l JOIN finance.ReconcileGroups g ON g.Id = l.ReconcileGroupId WHERE g.ReconcileType = 'Full' AND l.AccountId <> g.AccountId"))
            .Should().Be(0);

        // Re-run (every API start): nothing changes.
        await ExecAsync("DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE '%A47_GatewayReversalReconcile'");
        await migrator.MigrateAsync();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.ReconcileGroups")).Should().Be(4);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.JournalEntryLines WHERE Reconciled = 1")).Should().Be(6);
        (await ScalarAsync<int>($"SELECT CurrentSequence FROM finance.JournalEntrySequences WHERE OrganizationId = '{org}' AND JournalTypeId IS NULL")).Should().Be(10);
        h.Db.Database.HasPendingModelChanges().Should().BeFalse();

        // The next reconciliation the application numbers continues after the fix.
        h.NewScope();
        (await h.Get<SMS.Modules.Accounting.Services.IJournalEntrySequenceService>().NextReconcileGroupNumberAsync(org, DateOnly.FromDateTime(DateTime.UtcNow)))
            .Should().Be($"REC/{year}/0011");
    }

    [Fact]
    public async Task REV10_an_original_whose_reversal_is_a_Draft_is_not_Reversed_after_the_A47_backfill_and_the_data_fix()
    {
        // A44-shaped: an invoice journal with a Draft reversal (AutoPostToGL switched off between post and update), and a
        // normal pair with a Posted reversal. The A47_JournalEngine backfill marks both; the data fix must un-mark the first.
        using var h = await CreateAsync("20261016171319_A44_PostingMirroredFlag");
        await h.SetupGlAsync(enable: false);
        var org = h.OrgA;
        int Id(string code) => h.Db.ChartOfAccounts.IgnoreQueryFilters().Single(a => a.OrganizationId == org && a.AccountCode == code).Id;
        await ExecAsync($"""
            DECLARE @o uniqueidentifier = '{org}';
            INSERT INTO finance.JournalEntries (Uuid, OrganizationId, EntryNumber, EntryType, EntryDate, Status, TotalDebit, TotalCredit, CurrencyCode, CreatedAt, ModifiedAt, IdempotencyKey, ReversesEntryId)
            VALUES (NEWID(), @o, 'JE-2026-00001', 'SALES_INVOICE', '2026-03-10', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:1', NULL),
                   (NEWID(), @o, 'JE-2026-00003', 'SALES_INVOICE', '2026-03-12', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:2', NULL);
            DECLARE @a int = (SELECT Id FROM finance.JournalEntries WHERE EntryNumber = 'JE-2026-00001' AND OrganizationId = @o);
            DECLARE @b int = (SELECT Id FROM finance.JournalEntries WHERE EntryNumber = 'JE-2026-00003' AND OrganizationId = @o);
            INSERT INTO finance.JournalEntries (Uuid, OrganizationId, EntryNumber, EntryType, EntryDate, Status, TotalDebit, TotalCredit, CurrencyCode, CreatedAt, ModifiedAt, IdempotencyKey, ReversesEntryId)
            VALUES (NEWID(), @o, 'JE-2026-00002', 'SALES_INVOICE', '2026-03-11', 'Draft',  118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:1', @a),
                   (NEWID(), @o, 'JE-2026-00004', 'SALES_INVOICE', '2026-03-13', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:2:void', @b);
            INSERT INTO finance.JournalEntryLines (OrganizationId, JournalEntryId, LineNumber, AccountId, DebitAmount, CreditAmount, Memo)
            SELECT j.OrganizationId, j.Id, v.n, v.acc, v.d, v.c, NULL FROM finance.JournalEntries j
            CROSS APPLY (VALUES
                (1, {Id("1113")}, CASE WHEN j.ReversesEntryId IS NULL THEN 118 ELSE 0 END, CASE WHEN j.ReversesEntryId IS NULL THEN 0 ELSE 118 END),
                (2, {Id("4111")}, CASE WHEN j.ReversesEntryId IS NULL THEN 0 ELSE 118 END, CASE WHEN j.ReversesEntryId IS NULL THEN 118 ELSE 0 END)) v(n, acc, d, c)
            WHERE j.OrganizationId = @o;
            """);

        var migrator = h.Db.GetService<IMigrator>();
        await migrator.MigrateAsync();

        async Task<(string Status, int ReversedBy)> StateOf(string number) => (
            await ScalarAsync<string>($"SELECT Status FROM finance.JournalEntries WHERE OrganizationId = '{org}' AND EntryNumber = '{number}'"),
            await ScalarAsync<int>($"SELECT ISNULL(ReversedByEntryId, 0) FROM finance.JournalEntries WHERE OrganizationId = '{org}' AND EntryNumber = '{number}'"));
        (await StateOf("JE-2026-00001")).Should().Be(("Posted", 0), "REV-10 — a Draft reversal does not reverse");
        (await StateOf("JE-2026-00003")).Status.Should().Be("Reversed");
        async Task<int> Reconciled(string number) => await ScalarAsync<int>(
            $"SELECT COUNT(*) FROM finance.JournalEntryLines l JOIN finance.JournalEntries j ON j.Id = l.JournalEntryId WHERE j.OrganizationId = '{org}' AND j.EntryNumber = '{number}' AND l.Reconciled = 1");
        (await Reconciled("JE-2026-00001"), await Reconciled("JE-2026-00002")).Should().Be((0, 0));
        (await Reconciled("JE-2026-00003"), await Reconciled("JE-2026-00004")).Should().Be((2, 2));

        // Re-run: still not marked.
        await ExecAsync("DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE '%A47_GatewayReversalReconcile'");
        await migrator.MigrateAsync();
        (await StateOf("JE-2026-00001")).Should().Be(("Posted", 0));
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.ReconcileGroups")).Should().Be(2);
    }

    [Fact]
    public async Task Clean_database_runs_the_data_fix_as_a_no_op()
    {
        using var h = await CreateAsync();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE '%A47_GatewayReversalReconcile'")).Should().Be(1);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.ReconcileGroups")).Should().Be(0);
    }

    [Fact]
    public async Task Gateway_post_update_void_on_SQL_Server_numbers_per_type_marks_Reversed_and_reconciles()
    {
        using var h = await CreateAsync();
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        (await h.Gateway.PostAsync(Invoice(id))).Status.Should().Be(PostingStatus.Success);
        (await h.Gateway.PostAsync(Invoice(id, net: 200m, tax: 36m))).Status.Should().Be(PostingStatus.Success);
        (await h.Gateway.VoidAsync(VoidOf(id))).Status.Should().Be(PostingStatus.Success);
        h.NewScope();

        var journals = await h.JournalsAsync();
        journals.Select(j => j.EntryNumber).Should().Equal("SALE/2026/0001", "SALE/2026/0002", "SALE/2026/0003", "SALE/2026/0004");
        journals.Select(j => j.Status).Should().Equal(JournalStatuses.Reversed, JournalStatuses.Posted, JournalStatuses.Reversed, JournalStatuses.Posted);
        (journals[0].ReversedByEntryId, journals[2].ReversedByEntryId).Should().Be((journals[1].Id, journals[3].Id));
        journals.SelectMany(j => j.Lines).Should().OnlyContain(l => l.Reconciled && l.AmountResidual == 0);
        (await h.Db.ReconcileGroups.IgnoreQueryFilters().CountAsync()).Should().Be(6);
        (await h.BalanceAsync("1113")).Should().Be(0m);
        (await h.BalanceAsync("4111")).Should().Be(0m);

        // The duplicate void consumes nothing.
        (await h.Gateway.VoidAsync(VoidOf(id))).TargetResults.Single(r => r.TargetCode == PostingTargetCodes.InternalGl).InternalId.Should().Be("SALE/2026/0004");
        (await h.JournalsAsync()).Should().HaveCount(4);
    }

    public void Dispose()
    {
        SqlConnection.ClearAllPools();
        try
        {
            using var master = new SqlConnection(Conn("master"));
            master.Open();
            new SqlCommand($"IF DB_ID('{_dbName}') IS NOT NULL BEGIN ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}]; END", master).ExecuteNonQuery();
        }
        catch (SqlException) { /* best effort */ }
    }
}
