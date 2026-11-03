using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Finance.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApCore;

/// <summary>
/// A48 CORE QA-1 (reported by AR) — <see cref="ArApDefaultsSeeder.EnsureAsync"/> on SQL Server with the API's configuration
/// (<c>EnableRetryOnFailure</c>: a user transaction must run inside the execution strategy), for an organization created after
/// the migration: seeds once, idempotent, and safe when several first requests arrive at once.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class A48DefaultsSeederSqlServerTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A48Seed_{Guid.NewGuid():N};";

    /// <summary>Configured like IFinanceModule: SQL Server with the retrying execution strategy.</summary>
    private FinanceDbContext NewContext(Guid org) => new(
        new DbContextOptionsBuilder<FinanceDbContext>()
            .UseSqlServer(_connection, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)).Options,
        new StaticTenantContext { OrganizationId = org });

    public async Task InitializeAsync()
    {
        await using var db = NewContext(Guid.NewGuid());
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        SqlConnection.ClearAllPools();
        await using var db = NewContext(Guid.NewGuid());
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<(int Procedures, int Levels, int Rules)> CountsAsync(Guid org)
    {
        await using var db = NewContext(org);
        return (await db.DunningProcedures.IgnoreQueryFilters().CountAsync(p => p.OrganizationId == org),
                await db.DunningLevels.IgnoreQueryFilters().CountAsync(l => l.OrganizationId == org),
                await db.MatchToleranceRules.IgnoreQueryFilters().CountAsync(r => r.OrganizationId == org));
    }

    [Fact]
    public async Task A_new_organization_gets_the_defaults_once_with_the_retrying_strategy()
    {
        var org = Guid.NewGuid();
        await using (var db = NewContext(org)) await new ArApDefaultsSeeder(db).EnsureAsync(org);
        await using (var db = NewContext(org)) await new ArApDefaultsSeeder(db).EnsureAsync(org);
        await using (var db = NewContext(org)) await new ArApDefaultsSeeder(db).OnOrganizationProvisionedAsync(org);

        (await CountsAsync(org)).Should().Be((1, 4, 1));
        await using var check = NewContext(org);
        (await check.DunningProcedures.IgnoreQueryFilters().SingleAsync(p => p.OrganizationId == org))
            .Should().Match<SMS.Modules.Finance.Domain.DunningProcedure>(p => p.IsDefault && p.IsActive && p.Code == "STANDARD");
    }

    [Fact]
    public async Task Inside_a_callers_own_transaction_it_seeds_in_that_transaction()
    {
        var org = Guid.NewGuid();
        await using (var db = NewContext(org))
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await new ArApDefaultsSeeder(db).EnsureAsync(org);
                await tx.CommitAsync();
            });
        }
        (await CountsAsync(org)).Should().Be((1, 4, 1));
    }

    [Fact]
    public async Task Eight_first_requests_at_once_seed_exactly_one_set_and_none_fails()
    {
        var org = Guid.NewGuid();
        var runs = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await using var db = NewContext(org);
            await new ArApDefaultsSeeder(db).EnsureAsync(org);
        }));
        await Task.WhenAll(runs);

        (await CountsAsync(org)).Should().Be((1, 4, 1));
    }

    [Fact]
    public void Organization_provisioning_runs_the_seeder()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext(Guid.NewGuid()));
        ArApRegistration.AddArApSubledgers(services);
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetServices<IOrganizationProvisionedHandler>().Should().ContainSingle(h => h is ArApDefaultsSeeder);
    }
}
