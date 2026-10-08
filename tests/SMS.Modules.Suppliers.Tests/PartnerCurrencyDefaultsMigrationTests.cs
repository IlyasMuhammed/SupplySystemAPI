using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>
/// A35 P2-06 (M4, D-9) — BusinessPartners gains DefaultSaleCurrency (Guid?, NULL = the organization's sale base).
/// PreferredCurrency is reused as the default purchase currency, so nothing else changes. Guarded SQL only (every API
/// start migrates the shared, drifted database).
/// </summary>
public class PartnerCurrencyDefaultsMigrationTests
{
    private const string MigrationName = "A35_PartnerDefaultSaleCurrency";

    private static Migration Target()
    {
        using var db = new SuppliersDbContext(
            new DbContextOptionsBuilder<SuppliersDbContext>().UseSqlServer("Server=none;Database=none;Trusted_Connection=True;").Options,
            new StaticTenantContext());
        var assembly = db.GetService<IMigrationsAssembly>();
        var all = assembly.Migrations.OrderBy(m => m.Key, StringComparer.Ordinal)
            .Select(m => assembly.CreateMigration(m.Value, "Microsoft.EntityFrameworkCore.SqlServer")).ToList();
        all.Last().GetType().Name.Should().Be(MigrationName);
        return all.Last();
    }

    [Fact]
    public void Up_adds_the_nullable_column_only_when_missing()
    {
        var up = Target().UpOperations;

        up.Should().OnlyContain(op => op is SqlOperation);
        var sql = up.OfType<SqlOperation>().Single().Sql;
        sql.Should().Contain("COL_LENGTH(N'suppliers.BusinessPartners', N'DefaultSaleCurrency') IS NULL");
        sql.Should().Contain("ADD [DefaultSaleCurrency] uniqueidentifier NULL");
    }

    [Fact]
    public void Down_drops_it_only_when_present()
    {
        var sql = Target().DownOperations.OfType<SqlOperation>().Single().Sql;

        sql.Should().Contain("COL_LENGTH(N'suppliers.BusinessPartners', N'DefaultSaleCurrency') IS NOT NULL");
        sql.Should().Contain("DROP COLUMN [DefaultSaleCurrency]");
    }
}
