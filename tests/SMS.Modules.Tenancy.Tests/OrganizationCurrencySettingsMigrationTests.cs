using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Tenancy.Data;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>
/// A35 P2-01 / P1-13 (settings part) — the tenant.organization_currency_settings migration is additive and idempotent
/// (every API start migrates the shared, drifted database): only guarded SQL, nothing dropped, one backfill row per org.
/// </summary>
public class OrganizationCurrencySettingsMigrationTests
{
    private const string MigrationName = "A35_OrganizationCurrencySettings";

    private static TenancyDbContext SqlServerContext() =>
        new(new DbContextOptionsBuilder<TenancyDbContext>().UseSqlServer("Server=none;Database=none;Trusted_Connection=True;").Options);

    private static IReadOnlyList<Migration> Migrations()
    {
        using var db = SqlServerContext();
        var assembly = db.GetService<IMigrationsAssembly>();
        return assembly.Migrations
            .OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => assembly.CreateMigration(m.Value, "Microsoft.EntityFrameworkCore.SqlServer"))
            .ToList();
    }

    private static Migration Target() => Migrations().Single(m => m.GetType().Name == MigrationName);

    private static List<string> Sql(IEnumerable<MigrationOperation> ops) =>
        ops.OfType<SqlOperation>().Select(o => o.Sql).ToList();

    [Fact]
    public void The_migration_is_the_last_one_and_uses_only_guarded_sql()
    {
        Migrations().Last().GetType().Name.Should().Be(MigrationName);

        var up = Target().UpOperations;
        up.Should().NotBeEmpty();
        up.Should().OnlyContain(op => op is SqlOperation, "raw guarded SQL only — a plain CreateTable fails on a re-run");
        up.Should().NotContain(op => op is DropTableOperation || op is DropColumnOperation || op is AlterColumnOperation);
    }

    [Fact]
    public void The_table_is_created_only_when_missing_with_every_column()
    {
        var create = Sql(Target().UpOperations).Single(s => s.Contains("CREATE TABLE"));

        create.Should().Contain("OBJECT_ID(N'tenant.organization_currency_settings', N'U') IS NULL");
        foreach (var column in new[]
                 {
                     "[OrganizationId] uniqueidentifier NOT NULL", "[SaleBaseCurrencyId] uniqueidentifier NOT NULL",
                     "[PurchaseBaseCurrencyId] uniqueidentifier NOT NULL", "[ServiceBaseCurrencyId] uniqueidentifier NOT NULL",
                     "[RateCurrencyId] uniqueidentifier NOT NULL", "[ExchangeGainAccountCode] nvarchar(20) NULL",
                     "[ExchangeLossAccountCode] nvarchar(20) NULL", "[UnrealizedGainAccountCode] nvarchar(20) NULL",
                     "[UnrealizedLossAccountCode] nvarchar(20) NULL", "[CreatedAt] datetime2 NOT NULL",
                     "[UpdatedAt] datetime2 NOT NULL", "[ModifiedBy] int NULL",
                     "CONSTRAINT [PK_organization_currency_settings] PRIMARY KEY ([OrganizationId])",
                     "CONSTRAINT [FK_organization_currency_settings_Organizations]"
                 })
            create.Should().Contain(column);
    }

    [Fact]
    public void The_backfill_inserts_one_row_per_organization_with_a_base_currency_and_no_row()
    {
        var backfill = Sql(Target().UpOperations).Single(s => s.Contains("INSERT INTO [tenant].[organization_currency_settings]"));

        backfill.Should().Contain("NOT EXISTS", "a re-run must not insert a second row");
        backfill.Should().Contain("o.[BaseCurrency] IS NOT NULL",
            "D-7: a row's sale base must equal Organization.BaseCurrency — an org without one keeps no row (reads as PKR)");
        backfill.Should().NotContain("PKR");
    }

    [Fact]
    public void Rolling_back_drops_the_table_only_if_it_is_there()
    {
        var down = Sql(Target().DownOperations);

        down.Should().ContainSingle().Which.Should().Contain("IF OBJECT_ID(N'tenant.organization_currency_settings', N'U') IS NOT NULL")
            .And.Contain("DROP TABLE [tenant].[organization_currency_settings]");
    }

    [Fact]
    public void There_are_no_model_changes_still_waiting_for_a_migration()
    {
        using var db = SqlServerContext();

        var snapshotModel = db.GetService<IMigrationsAssembly>().ModelSnapshot!.Model;
        if (snapshotModel is IMutableModel mutable) snapshotModel = mutable.FinalizeModel();
        snapshotModel = db.GetService<IModelRuntimeInitializer>().Initialize(snapshotModel, designTime: true, validationLogger: null);

        db.GetService<IMigrationsModelDiffer>()
            .GetDifferences(snapshotModel.GetRelationalModel(), db.GetService<IDesignTimeModel>().Model.GetRelationalModel())
            .Should().BeEmpty("the entities and the migration snapshot have drifted — run 'dotnet ef migrations add'");
    }
}
