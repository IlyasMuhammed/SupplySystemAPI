using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A47 CORE on real SQL Server (LocalDB only — never the shared Azure database), a throw-away database per test:
/// tasks 124/125/127/128/129/132 — the guarded A47_JournalEngine migration on a clean database and on an A44-shaped database
/// with journals (backfill: journal types, periods, Reversed pairs, residuals, line uuids; the 7 types for set-up
/// organizations and for any organization with journals), re-run idempotently, Down and Up again, no pending model changes;
/// D-5 the filtered unique keys; TS-SQ-004 concurrent numbering (UPDLOCK, HOLDLOCK) and a concurrent duplicate request.
/// </summary>
[Trait("Category", "LocalDb")]
public sealed class LocalDbJournalEngineTests : IDisposable
{
    private const string A44Last = "20261016171319_A44_PostingMirroredFlag";
    private readonly string _dbName = "A47_JournalTests_" + Guid.NewGuid().ToString("N")[..12];
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
    public async Task Clean_database_gets_the_whole_schema_with_tenant_columns_and_no_cross_schema_fks()
    {
        using var h = await CreateAsync();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('finance') AND name IN ('JournalTypes','JournalEntrySequences','ReconcileGroups')"))
            .Should().Be(3, "task 127 — all new tables in finance");
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE name = 'OrganizationId' AND object_id IN (OBJECT_ID('finance.JournalTypes'), OBJECT_ID('finance.JournalEntrySequences'), OBJECT_ID('finance.ReconcileGroups'))"))
            .Should().Be(3, "task 129 — tenant column on every new table");
        (await ScalarAsync<int>("""
            SELECT COUNT(*) FROM sys.foreign_keys fk
            WHERE OBJECT_SCHEMA_NAME(fk.parent_object_id) = 'finance' AND OBJECT_SCHEMA_NAME(fk.referenced_object_id) <> 'finance'
            """)).Should().Be(0, "task 126 — no cross-schema foreign keys");
        (await ScalarAsync<string>("SELECT definition FROM sys.check_constraints WHERE name = 'CK_FiscalPeriods_Status'")).Should().Contain("AdjustmentOnly");
        (await ScalarAsync<string>("SELECT definition FROM sys.check_constraints WHERE name = 'CK_JournalEntries_Status'")).Should().Contain("Reversed");
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.triggers WHERE parent_id = OBJECT_ID('finance.JournalEntryLines')")).Should().Be(0, "M7 dropped (D-10)");
        h.Db.Database.HasPendingModelChanges().Should().BeFalse("task 132 — the hand-written SQL produces the model the code maps");
    }

    [Fact]
    public async Task A44_shaped_database_is_backfilled_rerun_idempotent_and_reversible()
    {
        using var h = await CreateAsync(A44Last);
        await h.SetupGlAsync(enable: false); // chart, FY 2026, defaults (DEFAULT_CASH = 1111), setup Complete
        var org = h.OrgA;
        var bare = Guid.NewGuid();            // an organization with an opening journal but no Complete setup (D-CORE-1)
        int Id(string code) => h.Db.ChartOfAccounts.IgnoreQueryFilters().Single(a => a.OrganizationId == org && a.AccountCode == code).Id;
        await ExecAsync($"""
            DECLARE @o uniqueidentifier = '{org}', @b uniqueidentifier = '{bare}';
            INSERT INTO finance.JournalEntries (Uuid, OrganizationId, EntryNumber, EntryType, EntryDate, Status, TotalDebit, TotalCredit, CurrencyCode, CreatedAt, ModifiedAt, IdempotencyKey, ReversesEntryId)
            VALUES (NEWID(), @o, 'JE-2026-00001', 'OPENING_BALANCE', '2026-01-01', 'Posted', 500, 500, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), NULL, NULL),
                   (NEWID(), @o, 'JE-2026-00002', 'SALES_INVOICE',  '2026-03-10', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:1', NULL),
                   (NEWID(), @o, 'JE-2026-00004', 'PAYMENT',        '2026-03-12', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:Payment:1', NULL),
                   (NEWID(), @o, 'JE-2026-00005', 'BILL_PAYMENT',   '2026-12-31', 'Draft',   10,  10, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:BillPayment:1', NULL),
                   (NEWID(), @b, 'JE-2026-00001', 'OPENING_BALANCE', '2026-01-01', 'Posted',  1,   1, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), NULL, NULL);
            DECLARE @inv int = (SELECT Id FROM finance.JournalEntries WHERE OrganizationId = @o AND EntryNumber = 'JE-2026-00002');
            INSERT INTO finance.JournalEntries (Uuid, OrganizationId, EntryNumber, EntryType, EntryDate, Status, TotalDebit, TotalCredit, CurrencyCode, CreatedAt, ModifiedAt, IdempotencyKey, ReversesEntryId)
            VALUES (NEWID(), @o, 'JE-2026-00003', 'SALES_INVOICE', '2026-03-11', 'Posted', 118, 118, 'PKR', SYSUTCDATETIME(), SYSUTCDATETIME(), 'finance:SalesInvoice:1:void', @inv);
            INSERT INTO finance.JournalEntryLines (OrganizationId, JournalEntryId, LineNumber, AccountId, DebitAmount, CreditAmount, Memo)
            SELECT j.OrganizationId, j.Id, v.n, v.acc, v.d, v.c, NULL FROM finance.JournalEntries j
            CROSS APPLY (VALUES
                (1, CASE j.EntryType WHEN 'PAYMENT' THEN {Id("1111")} WHEN 'BILL_PAYMENT' THEN {Id("2111")} WHEN 'OPENING_BALANCE' THEN {Id("1112")} ELSE {Id("1113")} END,
                    CASE WHEN j.ReversesEntryId IS NULL THEN j.TotalDebit ELSE 0 END, CASE WHEN j.ReversesEntryId IS NULL THEN 0 ELSE j.TotalDebit END),
                (2, CASE j.EntryType WHEN 'PAYMENT' THEN {Id("1113")} WHEN 'BILL_PAYMENT' THEN {Id("1112")} WHEN 'OPENING_BALANCE' THEN {Id("3900")} ELSE {Id("4111")} END,
                    CASE WHEN j.ReversesEntryId IS NULL THEN 0 ELSE j.TotalDebit END, CASE WHEN j.ReversesEntryId IS NULL THEN j.TotalDebit ELSE 0 END)) v(n, acc, d, c)
            WHERE j.OrganizationId = @o;
            """);

        var migrator = h.Db.GetService<IMigrator>();
        // A47 GW — up to A47_JournalEngine itself: the later data fix (A47_GatewayReversalReconcile) reconciles the pair below,
        // which the residual check of this backfill test must not see; it has its own test (LocalDbGatewayJournalTests).
        await migrator.MigrateAsync("20261024031212_A47_JournalEngine");

        // M8 — 7 types for the set-up organization and for the one that only has journals.
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.JournalTypes WHERE OrganizationId = '{org}' AND IsSystem = 1")).Should().Be(7);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.JournalTypes WHERE OrganizationId = '{bare}'")).Should().Be(7);
        // D-12 backfill.
        async Task<string> TypeOf(string number) => await ScalarAsync<string>(
            $"SELECT t.Code FROM finance.JournalEntries j JOIN finance.JournalTypes t ON t.Id = j.JournalTypeId WHERE j.OrganizationId = '{org}' AND j.EntryNumber = '{number}'");
        (await TypeOf("JE-2026-00001")).Should().Be("OPEN");
        (await TypeOf("JE-2026-00002")).Should().Be("SALE");
        (await TypeOf("JE-2026-00004")).Should().Be("CASH", "a line is on DEFAULT_CASH");
        (await TypeOf("JE-2026-00005")).Should().Be("BANK");
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.JournalEntries WHERE JournalTypeId IS NULL")).Should().Be(0);
        (await ScalarAsync<string>("SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID('finance.JournalEntries') AND name = 'JournalTypeId'")).Should().Be("False");
        // D-4 — the A44 reversal pair.
        (await ScalarAsync<string>($"SELECT Status FROM finance.JournalEntries WHERE OrganizationId = '{org}' AND EntryNumber = 'JE-2026-00002'")).Should().Be("Reversed");
        (await ScalarAsync<int>($"""
            SELECT COUNT(*) FROM finance.JournalEntries o JOIN finance.JournalEntries r ON r.Id = o.ReversedByEntryId
            WHERE o.OrganizationId = '{org}' AND o.EntryNumber = 'JE-2026-00002' AND r.EntryNumber = 'JE-2026-00003'
            """)).Should().Be(1);
        // Periods, flags, counts, line columns.
        (await ScalarAsync<int>($"SELECT p.PeriodNumber FROM finance.JournalEntries j JOIN finance.FiscalPeriods p ON p.Id = j.FiscalPeriodId WHERE j.OrganizationId = '{org}' AND j.EntryNumber = 'JE-2026-00005'"))
            .Should().Be(12, "the year's last day prefers period 12");
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.JournalEntries WHERE OrganizationId = '{org}' AND IsOpeningEntry = 1")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT MIN(LineCount) FROM finance.JournalEntries WHERE OrganizationId = '{org}'")).Should().Be(2);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.JournalEntryLines WHERE AmountResidual <> DebitAmount - CreditAmount OR CurrencyCode IS NULL OR AmountCurrency IS NULL")).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(DISTINCT Uuid) FROM finance.JournalEntryLines")).Should().Be(await ScalarAsync<int>("SELECT COUNT(*) FROM finance.JournalEntryLines"));

        // Re-run (every API start): forget it ran, run again over the existing objects — nothing changes.
        await ExecAsync("DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE '%A47_JournalEngine'");
        await migrator.MigrateAsync();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.JournalTypes")).Should().Be(14);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.JournalEntries WHERE Status = 'Reversed'")).Should().Be(1);

        // Task 128 — Down to A44, then Up again.
        await migrator.MigrateAsync(A44Last);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE name IN ('JournalTypes','JournalEntrySequences','ReconcileGroups')")).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('finance.JournalEntries') AND name = 'JournalTypeId'")).Should().Be(0);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.JournalEntries WHERE Status = 'Reversed'")).Should().Be(0);
        await migrator.MigrateAsync();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.JournalEntries WHERE Status = 'Reversed'")).Should().Be(1);
        h.Db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task TS_SQ_004_concurrent_entries_get_unique_numbers_and_unique_keys_hold()
    {
        using var h = await CreateAsync();
        await h.SetupGlAsync(enable: false);
        var org = h.OrgA;
        await h.Get<JournalTypeSeeder>().EnsureAsync(org);
        var misc = await h.Db.JournalTypes.IgnoreQueryFilters().Where(t => t.OrganizationId == org && t.Code == "MISC").Select(t => t.Uuid).SingleAsync();
        var accounts = await h.Db.ChartOfAccounts.IgnoreQueryFilters().Where(a => a.OrganizationId == org && (a.AccountCode == "5111" || a.AccountCode == "1111"))
            .ToDictionaryAsync(a => a.AccountCode, a => a.Uuid);
        var caller = new AccountingCaller(org, 1, AccountingCaller.AllGlCodes, "Ada");
        CreateJournalEntryRequest Request(Guid? client = null) => new()
        {
            JournalTypeUuid = misc, EntryDate = new DateOnly(2026, 6, 1), ClientRequestId = client, PostImmediately = true,
            Lines = [new() { AccountUuid = accounts["5111"], Debit = 10 }, new() { AccountUuid = accounts["1111"], Credit = 10 }],
        };

        var numbers = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            using var scope = h.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IJournalEntryService>().CreateAsync(caller, Request())).EntryNumber;
        }));
        numbers.Should().OnlyHaveUniqueItems();
        numbers.Order().Should().Equal(Enumerable.Range(1, 20).Select(i => $"JV/2026/{i:D4}"));
        h.NewScope();
        (await h.BalanceAsync("5111")).Should().Be(200, "REV-1 — the atomic balance move under concurrency");

        // BR-JE-007 — the same request twice at once: one entry.
        var client = Guid.NewGuid();
        var twice = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var scope = h.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IJournalEntryService>().CreateAsync(caller, Request(client))).Uuid;
        }));
        twice.Distinct().Should().HaveCount(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.JournalEntries WHERE IdempotencyKey = 'manual:{client}'")).Should().Be(1);

        // D-5 — manual keys are unique in the database; A44 keys are not.
        var dup = async () => await ExecAsync($"UPDATE finance.JournalEntries SET IdempotencyKey = 'manual:{client}' WHERE EntryNumber = 'JV/2026/0001'");
        await dup.Should().ThrowAsync<SqlException>();
        await ExecAsync("UPDATE finance.JournalEntries SET IdempotencyKey = 'finance:SalesInvoice:shared' WHERE EntryNumber IN ('JV/2026/0001', 'JV/2026/0002')");
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.JournalEntries WHERE IdempotencyKey = 'finance:SalesInvoice:shared'")).Should().Be(2);

        // A posted entry under concurrent second post: the conditional switch moves the balance once.
        var draft = Request();
        draft.PostImmediately = false;
        var d = await h.Get<IJournalEntryService>().CreateAsync(caller, draft);
        var posts = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            using var scope = h.CreateScope();
            try { await scope.ServiceProvider.GetRequiredService<IJournalEntryService>().PostAsync(caller, d.Uuid); return true; }
            catch (JournalConflictException) { return false; }
        }));
        posts.Count(ok => ok).Should().Be(1);
        h.NewScope();
        (await h.BalanceAsync("5111")).Should().Be(220, "20 × 10, the one duplicate-request entry, the draft posted once");

        // REV-11 — a journal waits while a period / year-end close holds the period lock exclusive, then goes through; the
        // same transaction that holds it exclusive (the year-end close) may still write journals.
        using (var closer = h.CreateScope())
        {
            var db = closer.ServiceProvider.GetRequiredService<Data.AccountingDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync();
            await GlPeriodLock.AcquireAsync(db, org, exclusive: true);
            var waiting = Task.Run(async () =>
            {
                using var scope = h.CreateScope();
                return (await scope.ServiceProvider.GetRequiredService<IJournalEntryService>().CreateAsync(caller, Request())).EntryNumber;
            });
            (await Task.WhenAny(waiting, Task.Delay(1500))).Should().NotBe(waiting, "the close holds the period lock");
            var own = await closer.ServiceProvider.GetRequiredService<JournalEntryWriter>().CreateAsync(new JournalDraft
            {
                OrganizationId = org, JournalTypeId = await db.JournalTypes.IgnoreQueryFilters().Where(t => t.OrganizationId == org && t.Code == "MISC").Select(t => t.Id).SingleAsync(),
                EntryDate = new DateOnly(2026, 6, 2), IdempotencyKey = "manual:" + Guid.NewGuid(), AmountsInBase = true,
                Lines = [new JournalDraftLine { AccountId = await db.ChartOfAccounts.IgnoreQueryFilters().Where(a => a.OrganizationId == org && a.AccountCode == "5111").Select(a => a.Id).SingleAsync(), Debit = 1 },
                         new JournalDraftLine { AccountId = await db.ChartOfAccounts.IgnoreQueryFilters().Where(a => a.OrganizationId == org && a.AccountCode == "1111").Select(a => a.Id).SingleAsync(), Credit = 1 }],
            }, post: true);
            own.Created.Should().BeTrue("the lock's owner is not blocked by itself");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            (await waiting).Should().StartWith("JV/2026/");
        }
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
