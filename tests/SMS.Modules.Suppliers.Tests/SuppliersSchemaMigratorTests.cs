using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>
/// A35 REV-08 — the host's migrate-only hook must not crash the API when the suppliers tables exist (applied by hand) but
/// the migrations history lacks them: it logs a critical error naming the missing ids and skips. Real SQL Server (LocalDB).
/// </summary>
public sealed class SuppliersSchemaMigratorTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A35_SUP_Migr_{Guid.NewGuid():N};";

    private SuppliersDbContext NewContext() => new(
        new DbContextOptionsBuilder<SuppliersDbContext>().UseSqlServer(_connection).Options, new StaticTenantContext());

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    [Fact]
    public async Task Tables_present_but_history_missing_skips_migrating_and_logs_the_missing_ids()
    {
        await using (var setup = NewContext())
        {
            // A hand-built database: the table is there, no __EFMigrationsHistory rows for this module.
            await setup.Database.EnsureCreatedAsync();
        }

        var log = new CapturingLogger();
        await using var db = NewContext();
        var migrated = SuppliersSchemaMigrator.MigrateIfSafe(db, log);

        migrated.Should().BeFalse();
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical)
            .Which.Message.Should().Contain(SuppliersSchemaMigrator.FirstMigrationId).And.Contain("BusinessPartnerEntityRename")
            .And.NotContain("A35_PartnerDefaultSaleCurrency", "the host applies A35's own migration — it is not 'missing'");
        db.Database.GetAppliedMigrations().Should().BeEmpty("nothing was attempted");
    }

    [Fact]
    public async Task InitialCreate_present_but_a_later_hand_applied_id_missing_also_skips()
    {
        const string rename = "20260919070313_RenameSuppliersToBusinessPartners";
        await using (var setup = NewContext())
        {
            await setup.Database.MigrateAsync();
            await setup.Database.ExecuteSqlRawAsync(
                "DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId] IN ({0}, {1})", rename, SuppliersSchemaMigrator.FirstMigrationGuardedId);
        }

        var log = new CapturingLogger();
        await using var db = NewContext();
        SuppliersSchemaMigrator.MigrateIfSafe(db, log).Should().BeFalse();

        var critical = log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Critical).Which.Message;
        critical.Should().Contain(rename).And.NotContain(SuppliersSchemaMigrator.FirstMigrationId);
        db.Database.GetAppliedMigrations().Should().NotContain(SuppliersSchemaMigrator.FirstMigrationGuardedId, "Migrate() was skipped");
    }

    [Fact]
    public async Task A_normal_database_is_migrated_and_a_second_run_is_a_no_op()
    {
        var log = new CapturingLogger();
        await using var db = NewContext();

        SuppliersSchemaMigrator.MigrateIfSafe(db, log).Should().BeTrue();
        db.Database.GetAppliedMigrations().Should().BeEquivalentTo(db.Database.GetMigrations());

        SuppliersSchemaMigrator.MigrateIfSafe(db, log).Should().BeTrue();
        log.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
