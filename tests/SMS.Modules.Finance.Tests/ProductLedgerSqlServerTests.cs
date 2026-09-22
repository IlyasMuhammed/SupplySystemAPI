using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;
using Xunit;

using static SMS.Modules.Finance.Tests.ProductLedgerRig;

namespace SMS.Modules.Finance.Tests;

/// <summary>A throwaway SQL Server database per test, created from the model and dropped after. LocalDB by default; override with <c>SMS_TEST_SQLSERVER</c>.</summary>
internal sealed class FinanceSqlServerHarness : IAsyncDisposable
{
    private readonly string _database;
    private readonly bool   _retryOnFailure;

    private FinanceSqlServerHarness(string database, string connectionString, bool retryOnFailure)
    {
        _database        = database;
        ConnectionString = connectionString;
        _retryOnFailure  = retryOnFailure;
    }

    /// <summary>Production registers every module's context with a retrying execution strategy; this is how a test gets the same.</summary>
    private void UseSql(DbContextOptionsBuilder options) =>
        options.UseSqlServer(ConnectionString, sql =>
        {
            if (_retryOnFailure) sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null);
        });

    public string ConnectionString { get; }

    internal static string MasterConnectionString =>
        Environment.GetEnvironmentVariable("SMS_TEST_SQLSERVER")
        ?? "Server=(localdb)\\mssqllocaldb;Database=master;Trusted_Connection=True;";

    internal static bool IsAvailable
    {
        get
        {
            try { using var conn = new SqlConnection(MasterConnectionString); conn.Open(); return true; }
            catch { return false; }
        }
    }

    /// <param name="withStockAndPurchasing">
    /// Also creates Inventory's and Demand's tables in the same database, for a test that posts a real GRN
    /// through the stock tables and reads the purchase order lines' prices.
    /// </param>
    /// <param name="retryOnFailure">
    /// Configures every context with the retrying execution strategy production uses, under which a
    /// transaction is only allowed inside an execution strategy's unit of work.
    /// </param>
    internal static async Task<FinanceSqlServerHarness> CreateAsync(bool withStockAndPurchasing = false, bool retryOnFailure = false)
    {
        var database = $"SMS_FinTest_{Guid.NewGuid():N}";
        var builder  = new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = "master" };

        await using (var conn = new SqlConnection(builder.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{database}]";
            await cmd.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = database;
        var harness = new FinanceSqlServerHarness(database, builder.ConnectionString, retryOnFailure);

        await using var db = harness.NewContext(Guid.NewGuid());
        await db.Database.EnsureCreatedAsync();

        if (withStockAndPurchasing)
        {
            // EnsureCreated does nothing once a database has tables, so the other modules' tables are
            // created directly. Each module's model is self-contained: it points at the others by bare uuid.
            await using var inventory = harness.NewInventoryContext(Guid.NewGuid());
            await inventory.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
            await using var demand = harness.NewDemandContext(Guid.NewGuid());
            await demand.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        }

        return harness;
    }

    internal FinanceDbContext NewContext(Guid organizationId, IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>();
        UseSql(options);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new FinanceDbContext(options.Options, new StaticTenantContext { OrganizationId = organizationId });
    }

    internal InventoryDbContext NewInventoryContext(Guid organizationId)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>();
        UseSql(options);
        return new InventoryDbContext(options.Options, new StaticTenantContext { OrganizationId = organizationId });
    }

    internal DemandDbContext NewDemandContext(Guid organizationId)
    {
        var options = new DbContextOptionsBuilder<DemandDbContext>();
        UseSql(options);
        return new DemandDbContext(options.Options, new StaticTenantContext { OrganizationId = organizationId });
    }

    public async ValueTask DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        var builder = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]";
        await cmd.ExecuteNonQueryAsync();
    }
}

/// <summary>A fact that skips itself when no SQL Server is reachable, so a machine without one still gets a green suite.</summary>
internal sealed class FinanceSqlServerFactAttribute : FactAttribute
{
    public FinanceSqlServerFactAttribute()
    {
        if (!FinanceSqlServerHarness.IsAvailable)
            Skip = "No SQL Server reachable. Set SMS_TEST_SQLSERVER to run the product ledger transaction tests.";
    }
}

/// <summary>
/// A29-P8-02 §11.3/§17.1 — what the in-memory provider cannot show: that an entry written inside another
/// module's transaction really commits and rolls back with it, that a lost race for the next sequence
/// really is refused by the unique index and retried, and that concurrent writers really end up chained.
/// </summary>
public class ProductLedgerSqlServerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private static CustomerLedgerEntry BusinessRow(Guid partner) => new()
    {
        UUID = Guid.NewGuid(), PartnerId = partner, SequenceNo = 1, EntryDate = DateTime.UtcNow, EntryType = "INVOICE",
        ReferenceType = "SalesInvoice", ReferenceId = Guid.NewGuid(), ReferenceNumber = "SINV-1",
        DebitAmount = 10m, CreditAmount = 0m, RunningBalance = 10m, CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    /// <summary>
    /// What another connection can see of a variant's ledger right now. READPAST skips a row an open
    /// transaction has locked instead of waiting for it, so this answers at once rather than blocking.
    /// </summary>
    private static async Task<int> VisibleToOthersAsync(FinanceSqlServerHarness harness, Guid variant)
    {
        await using var conn = new SqlConnection(harness.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM finance.product_ledger WITH (READPAST) WHERE VariantUuid = @variant";
        cmd.Parameters.AddWithValue("@variant", variant);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>Everything a variant's ledger says, in order, checked against the rule that produced each line.</summary>
    private static void ShouldChain(IReadOnlyList<ProductLedgerEntry> entries)
    {
        decimal qty = 0m, value = 0m;
        var expectedSequence = 1;

        foreach (var e in entries)
        {
            e.SequenceNo.Should().Be(expectedSequence++);

            var step = e.Direction == "IN"
                ? WeightedAverageCosting.In(qty, value, e.Quantity, e.UnitCost)
                : WeightedAverageCosting.Out(qty, value, e.Quantity);

            e.UnitCost.Should().Be(step.UnitCost, $"entry {e.SequenceNo} must be costed from the entry before it");
            e.TotalCost.Should().Be(step.TotalCost);
            e.RunningQty.Should().Be(step.RunningQty);
            e.RunningValue.Should().Be(step.RunningValue);
            (qty, value) = (step.RunningQty, step.RunningValue);
        }
    }

    // ── Same transaction as the business action ──────────────────────────────

    [FinanceSqlServerFact]
    public async Task An_entry_written_in_the_callers_transaction_commits_with_it_or_not_at_all()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var variants = new FakeVariants();
        var (variant, _) = variants.New();
        var partner = Guid.NewGuid();

        foreach (var commit in new[] { false, true })
        {
            await using var conn = new SqlConnection(harness.ConnectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

            // Another module's business write, on its own context, in the caller's transaction …
            await using (var business = harness.NewContext(org))
            {
                business.Database.SetDbConnection(conn, contextOwnsConnection: false);
                await business.Database.UseTransactionAsync(tx);
                business.CustomerLedgerEntries.Add(BusinessRow(partner));
                await business.SaveChangesAsync();
            }

            // … and the product ledger's entry for it, on Finance's own.
            await using var ledgerDb = harness.NewContext(org);
            var service = new ProductLedgerService(ledgerDb, variants);
            var posted = await service.AppendEntryAsync(Buy(variant, 10m, 4m), tx);
            posted.RunningValue.Should().Be(40m, "the entry is visible to its own transaction");

            (await VisibleToOthersAsync(harness, variant)).Should().Be(0, "nothing is visible to anyone else before the caller commits");

            if (commit) await tx.CommitAsync(); else await tx.RollbackAsync();

            await using var after = harness.NewContext(org);
            (await after.ProductLedgerEntries.CountAsync(e => e.VariantUuid == variant)).Should().Be(commit ? 1 : 0);
            (await after.CustomerLedgerEntries.CountAsync(e => e.PartnerId == partner)).Should().Be(commit ? 1 : 0,
                "the business row and the ledger entry stand or fall together");
        }
    }

    [FinanceSqlServerFact]
    public async Task Several_entries_in_one_transaction_chain_and_a_rollback_takes_every_one_back()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var variants = new FakeVariants();
        var (variant, _) = variants.New();

        // A committed opening purchase, so the rolled-back sale has something to be costed against.
        await using (var seed = harness.NewContext(org))
            await new ProductLedgerService(seed, variants).AppendEntryAsync(Buy(variant, 100m, 10m));

        await using var conn = new SqlConnection(harness.ConnectionString);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        await using var ledgerDb = harness.NewContext(org);
        var service = new ProductLedgerService(ledgerDb, variants);

        var first  = await service.AppendEntryAsync(Buy(variant, 50m, 16m), tx);
        var joined = ledgerDb.Database.CurrentTransaction;
        var second = await service.AppendEntryAsync(Sell(variant, 30m), tx);       // the same context, joined once

        joined.Should().NotBeNull();
        ledgerDb.Database.CurrentTransaction.Should().BeSameAs(joined, "posting again in the same transaction does not enlist again");

        first.SequenceNo.Should().Be(2);
        (second.SequenceNo, second.UnitCost, second.TotalCost, second.RunningQty).Should().Be((3, 12m, 360m, 120m));

        await tx.RollbackAsync();

        await using var after = harness.NewContext(org);
        (await after.ProductLedgerEntries.Where(e => e.VariantUuid == variant).CountAsync()).Should().Be(1, "only the committed opening purchase");
    }

    [FinanceSqlServerFact]
    public async Task One_service_can_join_one_transaction_after_another_in_the_same_request()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var variants = new FakeVariants();
        var (variant, _) = variants.New();

        await using var ledgerDb = harness.NewContext(org);
        var service = new ProductLedgerService(ledgerDb, variants);

        foreach (var (qty, cost) in new[] { (10m, 5m), (10m, 7m) })
        {
            await using var conn = new SqlConnection(harness.ConnectionString);
            await conn.OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            await service.AppendEntryAsync(Buy(variant, qty, cost), tx);
            await tx.CommitAsync();
        }

        await using var after = harness.NewContext(org);
        var entries = await after.ProductLedgerEntries.OrderBy(e => e.SequenceNo).ToListAsync();
        entries.Select(e => (e.SequenceNo, e.RunningQty, e.RunningValue)).Should().Equal((1, 10m, 50m), (2, 20m, 120m));
    }

    [FinanceSqlServerFact]
    public async Task A_change_and_a_tracked_entry_saved_together_are_rolled_back_together_when_the_entry_is_refused()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var variants = new FakeVariants();
        var (variant, _) = variants.New();
        var invoice = Receivables.Invoice(org, Guid.NewGuid(), "SINV-1", new DateTime(2026, 9, 20), 450m, status: "DRAFT");

        await using (var seed = harness.NewContext(org))
        {
            seed.SalesInvoices.Add(invoice);
            await seed.SaveChangesAsync();
            await new ProductLedgerService(seed, variants).AppendEntryAsync(Buy(variant, 100m, 10m));
        }

        // The invoice is being issued: its status changes and a SALE is tracked, in one unit of work.
        // Between reading the ledger and saving, another writer takes the next sequence, so the save is
        // refused by the unique index — and the invoice must not be left ISSUED.
        var raced = new RunOnceBeforeSave(
            db => db.ChangeTracker.Entries<ProductLedgerEntry>().Any(e => e.State == EntityState.Added),
            async () =>
            {
                await using var other = harness.NewContext(org);
                await new ProductLedgerService(other, variants).AppendEntryAsync(Buy(variant, 1m, 1m));
            });

        await using var db = harness.NewContext(org, raced);
        var tracked = await db.SalesInvoices.SingleAsync();
        tracked.Status = "ISSUED";
        await new ProductLedgerService(db, variants).TrackEntryAsync(Sell(variant, 30m));

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
        raced.Fired.Should().BeTrue();

        await using var after = harness.NewContext(org);
        (await after.SalesInvoices.SingleAsync()).Status.Should().Be("DRAFT", "the refused ledger entry took the status change with it");
        (await after.ProductLedgerEntries.Where(e => e.VariantUuid == variant && e.EntryType == "SALE").CountAsync()).Should().Be(0);
    }

    // ── Two writers for one variant ──────────────────────────────────────────

    [FinanceSqlServerFact]
    public async Task A_lost_race_inside_the_callers_transaction_is_retried_and_the_transaction_survives()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var variants = new FakeVariants();
        var (variant, _) = variants.New();

        await using (var seed = harness.NewContext(org))
            await new ProductLedgerService(seed, variants).AppendEntryAsync(Buy(variant, 100m, 10m));

        // After this write has read the ledger and before it saves, another writer commits the next entry.
        var raced = new RunOnceBeforeSave(
            db => db.ChangeTracker.Entries<ProductLedgerEntry>().Any(e => e.State == EntityState.Added),
            async () =>
            {
                await using var other = harness.NewContext(org);
                await new ProductLedgerService(other, variants).AppendEntryAsync(Buy(variant, 100m, 20m));
            });

        await using var conn = new SqlConnection(harness.ConnectionString);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        await using var ledgerDb = harness.NewContext(org, raced);
        var sale = await new ProductLedgerService(ledgerDb, variants).AppendEntryAsync(Sell(variant, 50m), tx);

        raced.Fired.Should().BeTrue("the unique index refused the first attempt");
        // 100 @ 10 and 100 @ 20 make 200 worth 3000: 50 leave at 15, not at the 10 the sale first saw.
        (sale.SequenceNo, sale.UnitCost, sale.TotalCost).Should().Be((3, 15m, 750m));

        await tx.CommitAsync();

        await using var after = harness.NewContext(org);
        ShouldChain(await after.ProductLedgerEntries.OrderBy(e => e.SequenceNo).ToListAsync());
    }

    [FinanceSqlServerFact]
    public async Task Writers_racing_for_one_variant_all_land_and_each_is_costed_from_the_one_before_it()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var variants = new FakeVariants();
        var (variant, _) = variants.New();

        await using (var seed = harness.NewContext(org))
            await new ProductLedgerService(seed, variants).AppendEntryAsync(Buy(variant, 1000m, 10m));

        // Five at once, the most one entry's five attempts are guaranteed to outlast: a mix of purchases and sales.
        for (var round = 0; round < 6; round++)
        {
            var postings = new[]
            {
                Buy(variant, 10m + round, 11m + round),
                Sell(variant, 7m),
                Buy(variant, 3.5m, 9.25m),
                Sell(variant, 12.25m),
                Sell(variant, 0.5m)
            };

            var results = await Task.WhenAll(postings.Select(async p =>
            {
                await using var db = harness.NewContext(org);
                return await new ProductLedgerService(db, variants).AppendEntryAsync(p);
            })).WaitAsync(Patience);

            results.Select(r => r.SequenceNo).Distinct().Should().HaveCount(5, "no two writers were given the same place");
        }

        await using var after = harness.NewContext(org);
        var entries = await after.ProductLedgerEntries.OrderBy(e => e.SequenceNo).ToListAsync();

        entries.Should().HaveCount(31);
        ShouldChain(entries);
    }

    [FinanceSqlServerFact]
    public async Task A_second_transaction_waits_for_the_first_and_is_then_costed_from_what_it_committed()
    {
        await using var harness = await FinanceSqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var variants = new FakeVariants();
        var (variant, _) = variants.New();

        await using (var seed = harness.NewContext(org))
            await new ProductLedgerService(seed, variants).AppendEntryAsync(Buy(variant, 100m, 10m));

        await using var connA = new SqlConnection(harness.ConnectionString);
        await connA.OpenAsync();
        await using var txA = (SqlTransaction)await connA.BeginTransactionAsync();
        await using var dbA = harness.NewContext(org);
        await new ProductLedgerService(dbA, variants).AppendEntryAsync(Buy(variant, 100m, 20m), txA);   // uncommitted

        await using var connB = new SqlConnection(harness.ConnectionString);
        await connB.OpenAsync();
        await using var txB = (SqlTransaction)await connB.BeginTransactionAsync();
        await using var dbB = harness.NewContext(org);
        var second = Task.Run(() => new ProductLedgerService(dbB, variants).AppendEntryAsync(Sell(variant, 50m), txB));

        await Task.Delay(500);
        await txA.CommitAsync();

        var sale = await second.WaitAsync(Patience);
        await txB.CommitAsync();

        (sale.SequenceNo, sale.UnitCost, sale.TotalCost).Should().Be((3, 15m, 750m), "costed from A's committed purchase, not from the ledger as it was");

        await using var after = harness.NewContext(org);
        ShouldChain(await after.ProductLedgerEntries.OrderBy(e => e.SequenceNo).ToListAsync());
    }
}
