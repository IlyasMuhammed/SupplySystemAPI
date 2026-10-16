using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A42 on real SQL Server (LocalDB only — never the shared Azure database): the guarded migration, the template seeder,
/// applying US_GENERAL (paths, parent FKs, CHECKs), the recursive-CTE ancestry, replacing the chart and the setup reset
/// (set-based deletes of the self-referencing chart). A throw-away database per run, dropped afterwards.
/// </summary>
[Trait("Category", "LocalDb")]
public sealed class LocalDbChartTests : IDisposable
{
    private readonly string _dbName = "A42_ChartTests_" + Guid.NewGuid().ToString("N")[..12];
    private string Conn(string db) => $"Server=(localdb)\\MSSQLLocalDB;Database={db};Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>Counts the SQL commands sent (N+1 check).</summary>
    private sealed class CommandCounter : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private readonly CommandCounter _commands = new();

    private ServiceProvider Build(Guid org)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext { OrganizationId = org });
        services.AddDbContext<AccountingDbContext>(o => o.UseSqlServer(Conn(_dbName)).AddInterceptors(_commands));
        AccountingModuleExtensions.AddAccountingServices(services);
        return services.BuildServiceProvider();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var c = new SqlConnection(Conn(_dbName));
        await c.OpenAsync();
        await using var cmd = new SqlCommand(sql, c);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    [Fact]
    public async Task Apply_us_general_on_sql_server()
    {
        var org = Guid.NewGuid();
        using var sp = Build(org);
        var admin = new AccountingCaller(org, 1, [P.GL_VIEW, P.GL_ACCOUNT_MANAGE, P.GL_SETUP_MANAGE], "Ada");
        await using (var master = new SqlConnection(Conn("master")))
        {
            await master.OpenAsync();
            // The finance schema already exists on the real database (Finance module): start from that shape.
            await new SqlCommand($"CREATE DATABASE [{_dbName}]", master).ExecuteNonQueryAsync();
        }
        await using (var c = new SqlConnection(Conn(_dbName)))
        {
            await c.OpenAsync();
            await new SqlCommand("EXEC(N'CREATE SCHEMA [finance]')", c).ExecuteNonQueryAsync();
        }

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync(); // nothing pending — and the Up itself is guarded
            await scope.ServiceProvider.GetRequiredService<ITemplateSeeder>().SeedAsync();
            await scope.ServiceProvider.GetRequiredService<ITemplateSeeder>().SeedAsync();
        }
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.CoaTemplates")).Should().Be(16);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM finance.CoaTemplateItems")).Should().Be(324);

        Guid account1112;
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var us = await db.CoaTemplates.SingleAsync(t => t.TemplateCode == "US_GENERAL");
            var result = await scope.ServiceProvider.GetRequiredService<IChartOfAccountsService>().ApplyTemplateAsync(admin, us.Uuid);
            result.AccountsCreated.Should().Be(110);
            result.DefaultsAssigned.Should().Be(16);
            account1112 = (await db.ChartOfAccounts.SingleAsync(a => a.AccountCode == "1112")).Uuid;
        }

        // Every non-root's path is its parent's path + "/" + its code, one level down, same root; roots have no parent.
        (await ScalarAsync<int>($"""
            SELECT COUNT(*) FROM finance.ChartOfAccounts c
            LEFT JOIN finance.ChartOfAccounts p ON p.Id = c.ParentAccountId
            WHERE c.OrganizationId = '{org}' AND (
                (c.[Level] = 1 AND (c.ParentAccountId IS NOT NULL OR c.MaterializedPath <> '/' + c.AccountCode)) OR
                (c.[Level] > 1 AND (p.Id IS NULL OR p.[Level] <> c.[Level] - 1 OR p.RootType <> c.RootType OR p.OrganizationId <> c.OrganizationId
                                    OR c.MaterializedPath <> p.MaterializedPath + '/' + c.AccountCode)))
            """)).Should().Be(0);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.ChartOfAccounts WHERE OrganizationId = '{org}' AND MaterializedPath LIKE '/1/11/%'"))
            .Should().BeGreaterThan(5);
        (await ScalarAsync<int>("SELECT COUNT(*) FROM sys.foreign_keys WHERE schema_id = SCHEMA_ID('finance') AND is_not_trusted = 0")).Should().Be(14); // A42: 12; A44: posting targets → postings, journal → reversed journal
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.AccountingDefaults WHERE OrganizationId = '{org}' AND AccountId IS NOT NULL")).Should().Be(16);

        using (var scope = sp.CreateScope())
        {
            var chart = scope.ServiceProvider.GetRequiredService<IChartOfAccountsService>();
            (await chart.GetAncestorsAsync(admin, account1112)).Select(a => a.AccountCode).Should().Equal("1", "11", "111");
            var current = await scope.ServiceProvider.GetRequiredService<AccountingDbContext>().ChartOfAccounts.SingleAsync(a => a.AccountCode == "11");
            (await chart.GetSubtreeAsync(admin, current.Uuid)).Should().OnlyContain(a => a.MaterializedPath.StartsWith("/1/11/"));

            // Delete refusals for all 110 accounts in a fixed number of queries (no N+1): 1 chart + 4 batch lookups.
            var before = _commands.Count;
            var tree = await chart.GetTreeAsync(admin);
            (_commands.Count - before).Should().BeLessThanOrEqualTo(5);
            tree.Roots.Should().OnlyContain(r => !r.CanDelete && r.DeleteBlockedReason == "Cannot delete a system account.");
            before = _commands.Count;
            var flat = await chart.GetFlatAsync(admin, new SMS.Modules.Accounting.Models.AccountFlatQuery());
            flat.Should().HaveCount(110);
            (_commands.Count - before).Should().BeLessThanOrEqualTo(6);

            // CHECK constraints hold on the server: a level-4 group is refused.
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var parent = await db.ChartOfAccounts.SingleAsync(a => a.AccountCode == "111");
            db.ChartOfAccounts.Add(new ChartOfAccount { OrganizationId = org, AccountCode = "1199", AccountName = "Bad", ParentAccountId = parent.Id,
                MaterializedPath = "/1/11/111/1199", Level = 4, IsGroup = true, RootType = RootTypes.Asset });
            await ((Func<Task>)(() => db.SaveChangesAsync())).Should().ThrowAsync<DbUpdateException>();
        }

        // Replacing the chart (one set-based delete of the self-referencing table) and resetting the setup.
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountingDbContext>();
            var pk = await db.CoaTemplates.SingleAsync(t => t.TemplateCode == "PK_GENERAL");
            (await scope.ServiceProvider.GetRequiredService<IChartOfAccountsService>().ApplyTemplateAsync(admin, pk.Uuid)).AccountsCreated.Should().Be(110);
        }
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.ChartOfAccounts WHERE OrganizationId = '{org}'")).Should().Be(110);
        using (var scope = sp.CreateScope())
        {
            var model = await scope.ServiceProvider.GetRequiredService<ISetupStatusService>().ResetAsync(admin);
            model.Status.Should().Be(SetupStatuses.NotStarted);
        }
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.ChartOfAccounts WHERE OrganizationId = '{org}'")).Should().Be(0);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.AccountingDefaults WHERE OrganizationId = '{org}' AND AccountId IS NULL")).Should().Be(16);
        (await ScalarAsync<int>($"SELECT COUNT(*) FROM finance.GlSetupHistory WHERE OrganizationId = '{org}'")).Should().BeGreaterThan(3);
    }

    public void Dispose()
    {
        SqlConnection.ClearAllPools();
        using var c = new SqlConnection(Conn("master"));
        c.Open();
        using var cmd = new SqlCommand($"IF DB_ID('{_dbName}') IS NOT NULL BEGIN ALTER DATABASE [{_dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_dbName}]; END", c);
        cmd.ExecuteNonQuery();
    }
}
