using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Finance.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A35 P1-01 / P1-06 — <c>A35_CurrencyCore</c> on LocalDB (throwaway database): up, down, up again, and a re-run on a database
/// that has the schema but lost the history row (the drifted shared database). The CHECK constraints refuse bad rows.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class A35CurrencyCoreMigrationTests : IAsyncLifetime
{
    private const string Server   = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private const string Previous = "20261001173747_SAP_TaxCodesExchangeRatesReversals";
    private const string Name     = "A35_CurrencyCore";
    private readonly string _connection = $"{Server}Database=A35_FinMig_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();

    private FinanceDbContext NewContext() => new(
        new DbContextOptionsBuilder<FinanceDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = _org });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<int> ObjectCountAsync() => ScalarAsync(
        "SELECT (SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id " +
        "        WHERE s.name = 'finance' AND t.name IN ('org_currencies','currency_rates','exchange_differences')) " +
        "+ (SELECT COUNT(*) FROM sys.indexes WHERE object_id IN (OBJECT_ID('finance.org_currencies'), " +
        "   OBJECT_ID('finance.currency_rates'), OBJECT_ID('finance.exchange_differences')) AND is_primary_key = 0 AND type > 0) " +
        "+ (SELECT COUNT(*) FROM sys.check_constraints WHERE parent_object_id IN (OBJECT_ID('finance.org_currencies'), " +
        "   OBJECT_ID('finance.currency_rates'), OBJECT_ID('finance.exchange_differences')))");

    /// <summary>Up to A35_CurrencyCore only (later Finance migrations are FIN's and tested by FIN).</summary>
    private static Task UpToThisAsync(FinanceDbContext db) =>
        db.GetService<IMigrator>().MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("_" + Name)));

    // 3 tables + 13 indexes + 10 checks.
    private const int Expected = 26;

    [Fact]
    public async Task Up_down_up_and_a_rerun_on_a_drifted_database_all_work()
    {
        // Finance's early migrations do not replay on an empty database (AddDebitNotes predates its table), so build the
        // pre-A35 state from the model: create everything, drop the three A35 tables, record every older migration as applied.
        await using (var db = NewContext())
        {
            await db.Database.EnsureCreatedAsync();
            await ExecAsync("DROP TABLE finance.currency_rates; DROP TABLE finance.exchange_differences; DROP TABLE finance.org_currencies;");
            await ExecAsync("CREATE TABLE [dbo].[__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL PRIMARY KEY, [ProductVersion] nvarchar(32) NOT NULL);");
            foreach (var id in db.Database.GetMigrations().Where(m => string.CompareOrdinal(m, Previous) <= 0))
                await ExecAsync($"INSERT INTO [dbo].[__EFMigrationsHistory] VALUES (N'{id}', N'9.0.0');");
        }
        (await ObjectCountAsync()).Should().Be(0);

        await using (var db = NewContext()) await UpToThisAsync(db);
        (await ObjectCountAsync()).Should().Be(Expected);

        // CHECKs: bad code case, bad range, non-positive rate, unknown kind.
        var cur = Guid.NewGuid();
        var lower = () => ExecAsync(
            "INSERT INTO finance.org_currencies (Uuid, OrganizationId, CurrencyId, Code, Name, Symbol, DisplayOrder, CreatedDate) " +
            $"VALUES (NEWID(), '{_org}', '{cur}', 'usd', 'US Dollar', '$', 1, SYSUTCDATETIME());");
        await lower.Should().ThrowAsync<SqlException>();
        await ExecAsync(
            "INSERT INTO finance.org_currencies (Uuid, OrganizationId, CurrencyId, Code, Name, Symbol, DisplayOrder, CreatedDate) " +
            $"VALUES (NEWID(), '{_org}', '{cur}', 'USD', 'US Dollar', '$', 1, SYSUTCDATETIME());");
        (await ScalarAsync("SELECT DecimalPlaces FROM finance.org_currencies WHERE Code = 'USD'")).Should().Be(2);

        var range = () => ExecAsync(
            "INSERT INTO finance.currency_rates (Uuid, OrganizationId, CurrencyId, CurrencyCode, Rate, InverseRate, EffectiveFrom, EffectiveTo, CreatedDate) " +
            $"VALUES (NEWID(), '{_org}', '{cur}', 'USD', 278, 0.0035, '2026-10-07', '2026-10-06', SYSUTCDATETIME());");
        await range.Should().ThrowAsync<SqlException>();
        var negative = () => ExecAsync(
            "INSERT INTO finance.currency_rates (Uuid, OrganizationId, CurrencyId, CurrencyCode, Rate, InverseRate, EffectiveFrom, EffectiveTo, CreatedDate) " +
            $"VALUES (NEWID(), '{_org}', '{cur}', 'USD', -278, 0.0035, '2026-10-07', '9999-12-31', SYSUTCDATETIME());");
        await negative.Should().ThrowAsync<SqlException>();
        await ExecAsync(
            "INSERT INTO finance.currency_rates (Uuid, OrganizationId, CurrencyId, CurrencyCode, Rate, InverseRate, EffectiveFrom, EffectiveTo, CreatedDate) " +
            $"VALUES (NEWID(), '{_org}', '{cur}', 'USD', 278.05, 0.0035964754, '2026-10-07', '9999-12-31', SYSUTCDATETIME());");
        var sameDay = () => ExecAsync(
            "INSERT INTO finance.currency_rates (Uuid, OrganizationId, CurrencyId, CurrencyCode, Rate, InverseRate, EffectiveFrom, EffectiveTo, CreatedDate) " +
            $"VALUES (NEWID(), '{_org}', '{cur}', 'USD', 279, 0.0035, '2026-10-07', '9999-12-31', SYSUTCDATETIME());");
        await sameDay.Should().ThrowAsync<SqlException>("unique (org, currency, effectiveFrom)");

        // The EF model reads what the SQL created.
        await using (var db = NewContext())
        {
            var rate = await db.CurrencyRates.SingleAsync();
            rate.EffectiveTo.Should().Be(CurrencyConventions.OpenEnd);
            rate.Source.Should().Be("MANUAL");
            (await db.OrgCurrencies.CountAsync()).Should().Be(1);
            (await db.ExchangeDifferences.CountAsync()).Should().Be(0);
        }

        // Down, then up again.
        await using (var db = NewContext()) await db.GetService<IMigrator>().MigrateAsync(Previous);
        (await ObjectCountAsync()).Should().Be(0);
        await using (var db = NewContext()) await UpToThisAsync(db);
        (await ObjectCountAsync()).Should().Be(Expected);

        // Re-run with the schema present but the history row lost.
        await ExecAsync($"DELETE FROM [dbo].[__EFMigrationsHistory] WHERE MigrationId LIKE '%{Name}'");
        await using (var db = NewContext()) await UpToThisAsync(db);
        (await ObjectCountAsync()).Should().Be(Expected);
        (await ScalarAsync($"SELECT COUNT(*) FROM [dbo].[__EFMigrationsHistory] WHERE MigrationId LIKE '%{Name}'")).Should().Be(1);
    }
}
