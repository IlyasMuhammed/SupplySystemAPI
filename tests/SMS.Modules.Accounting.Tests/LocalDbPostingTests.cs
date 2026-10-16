using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Posting;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using Xunit;
using static SMS.Modules.Accounting.Tests.PostingHarness;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A44 CORE on real SQL Server (LocalDB only — never the shared Azure database): the guarded A44 migration re-run, the
/// atomic balance UPDATE under concurrent postings (REV-1), the per-document application lock and the unique posting key
/// under a concurrent double post (REV-5), the update/void path with ExecuteUpdate, and the retry job's row claim.
/// A throw-away database per run, dropped afterwards.
/// </summary>
[Trait("Category", "LocalDb")]
public sealed class LocalDbPostingTests : IDisposable
{
    private readonly string _dbName = "A44_PostingTests_" + Guid.NewGuid().ToString("N")[..12];
    private string Conn(string db) => $"Server=(localdb)\\MSSQLLocalDB;Database={db};Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>Thread-safe JE numbers (the real app uses the Logistics counter).</summary>
    private sealed class AtomicNumbers : IDocumentNumberGenerator
    {
        private int _next;
        public Task<string> NextAsync(string prefix, DateTime? utcNow = null, Guid? organizationId = null, CancellationToken ct = default) =>
            Task.FromResult($"{prefix}-{(utcNow ?? DateTime.UtcNow).Year}-{Interlocked.Increment(ref _next):D5}");
    }

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
        var h = new PostingHarness(store: o => o.UseSqlServer(Conn(_dbName)), numbers: new AtomicNumbers());
        await h.Db.Database.MigrateAsync();
        // The A44 Up is guarded: forget it ran and run it again over the existing objects.
        await h.Db.Database.ExecuteSqlRawAsync("DELETE FROM dbo.__EFMigrationsHistory WHERE MigrationId LIKE '%A44_FinancialPosting'");
        await h.Db.Database.MigrateAsync();
        return h;
    }

    [Fact]
    public async Task Concurrent_posts_update_void_and_job_claim_on_sql_server()
    {
        using var h = await CreateAsync();
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.tables WHERE schema_id = SCHEMA_ID('finance') AND name IN ('FinancialPostings','FinancialPostingTargets','TenantFinanceConfigs')"))
            .Should().Be(3);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('finance.JournalEntries') AND name IN ('SourceModule','SourceDocumentUuid','IdempotencyKey','ReversesEntryId')"))
            .Should().Be(4);
        await h.SetupGlAsync();

        // REV-1 — 20 invoices at once to the same receivable account: no amount is lost.
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var results = await Task.WhenAll(ids.Select(async (id, i) =>
        {
            using var scope = h.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IFinancialPostingGateway>().PostAsync(Invoice(id, number: $"SI-{i}"));
        }));
        results.Should().OnlyContain(r => r.Status == PostingStatus.Success);
        h.NewScope();
        (await h.BalanceAsync("1113")).Should().Be(20 * 118m);
        (await h.BalanceAsync("4111")).Should().Be(20 * 100m);

        // REV-5 — the same document posted twice at once: one posting row, one journal.
        var twice = Guid.NewGuid();
        await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            using var scope = h.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IFinancialPostingGateway>().PostAsync(Invoice(twice, number: "SI-TWICE"));
        }));
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.FinancialPostings WHERE IdempotencyKey = 'finance:SalesInvoice:{twice}'")).Should().Be(1);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.JournalEntries WHERE IdempotencyKey = 'finance:SalesInvoice:{twice}'")).Should().Be(1);

        // Update + void on SQL Server (ExecuteUpdate balances, reversal FK).
        h.NewScope();
        await h.Gateway.PostAsync(Invoice(twice, net: 50m, tax: 0m, number: "SI-TWICE"));
        await h.Gateway.VoidAsync(VoidOf(twice));
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.JournalEntries WHERE SourceDocumentUuid = '{twice}'")).Should().Be(4);
        h.NewScope();
        (await h.BalanceAsync("1113")).Should().Be(20 * 118m);

        // Job claim: a due Failed row is claimed and retried once.
        h.External.Throw = new InvalidOperationException("down");
        var failing = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(failing, number: "SI-F"));
        h.External.Throw = null;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.NewScope();
        (await h.Get<PostingRetryJob>().RunAsync()).Should().Be(1);
        h.NewScope();
        (await h.PostingAsync($"finance:SalesInvoice:{failing}")).Targets
            .Single(t => t.TargetCode == PostingTargetCodes.QuickBooks).Status.Should().Be(PostingTargetStatuses.Queued);
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
