using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Tests.Connections;

/// <summary>
/// What the Integration module tells Lookups before a currency is deleted or its code changed: a
/// connection's home currency, kept as a CODE — so the checker turns the id into its code first, through
/// the SMS.Shared catalog contract. Every organization's connections count. Stored payloads are left out
/// on purpose (see the checker's summary).
/// </summary>
public class IntegrationCurrencyReferenceCheckerTests
{
    private static readonly Guid OrgA        = Guid.NewGuid();
    private static readonly Guid OrgB        = Guid.NewGuid();
    private static readonly Guid Usd         = Guid.NewGuid();
    private static readonly Guid NotCurrency = Guid.NewGuid();
    private static readonly Guid BlankCode   = Guid.NewGuid();

    private readonly string _dbName = Guid.NewGuid().ToString();

    private static ICurrencyCodeLookup Catalog()
    {
        var lookup = new Mock<ICurrencyCodeLookup>(MockBehavior.Strict);
        lookup.Setup(l => l.GetCodeAsync(Usd, It.IsAny<CancellationToken>())).ReturnsAsync("USD");
        lookup.Setup(l => l.GetCodeAsync(NotCurrency, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        lookup.Setup(l => l.GetCodeAsync(BlankCode, It.IsAny<CancellationToken>())).ReturnsAsync("  ");
        return lookup.Object;
    }

    /// <summary>The connection is organization A's; Lookups' request comes from B, not a super admin.</summary>
    private IntegrationCurrencyReferenceChecker Checker() => new(IntegrationTestDb.OpenAs(_dbName, OrgB), Catalog());

    private async Task SeedAsync(string? homeCurrency, ConnectionStatus status = ConnectionStatus.Live)
    {
        await using var db = IntegrationTestDb.OpenAs(_dbName, OrgA);
        db.Connections.Add(new IntegrationConnection
        {
            OrganizationId = OrgA, RealmId = "9130000000000001", CompanyName = "Sandbox Company",
            Status = status, HomeCurrencyCode = homeCurrency, MultiCurrencyEnabled = false, Country = "PK"
        });
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData("USD")]
    [InlineData("usd")]
    [InlineData(" Usd ")]
    public async Task Another_organizations_home_currency_is_referenced_whatever_its_case_or_padding(string stored)
    {
        await SeedAsync(stored);

        await using (var orgB = IntegrationTestDb.OpenAs(_dbName, OrgB))
            (await orgB.Connections.CountAsync()).Should().Be(0, "the tenant filter hides organization A's connection from B");

        Checker().IsValueReferenced(Usd).Should().BeTrue();
    }

    [Theory]
    [InlineData("EUR")]
    [InlineData("USDT")]
    [InlineData(null)]
    public async Task A_different_or_missing_home_currency_is_not_a_reference(string? stored)
    {
        await SeedAsync(stored);

        Checker().IsValueReferenced(Usd).Should().BeFalse();
    }

    [Fact]
    public async Task A_connection_in_any_status_counts()
    {
        await SeedAsync("USD", ConnectionStatus.Revoked);

        Checker().IsValueReferenced(Usd).Should().BeTrue("a revoked connection keeps its home currency and can be reconnected");
    }

    [Fact]
    public async Task An_id_that_is_not_a_currency_is_not_referenced()
    {
        await SeedAsync("USD");

        Checker().IsValueReferenced(NotCurrency).Should().BeFalse();
    }

    [Fact]
    public async Task A_currency_with_a_blank_code_is_not_referenced_even_by_an_empty_home_currency()
    {
        await SeedAsync("");

        Checker().IsValueReferenced(BlankCode).Should().BeFalse();
    }

    [Fact]
    public async Task The_empty_id_is_answered_without_asking_the_catalog()
    {
        await SeedAsync("USD");

        // The strict mock has no setup for Guid.Empty, so asking it would throw.
        Checker().IsValueReferenced(Guid.Empty).Should().BeFalse();
    }

    [Fact]
    public void Sql_server_is_sent_one_trimmed_upper_cased_query_across_organizations()
    {
        var commands = new SqlServerWithoutAServer();
        var options = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseSqlServer("Server=none;Database=none;Trusted_Connection=True;")
            .AddInterceptors(commands, new NeverOpen())
            .Options;
        using var db = new IntegrationDbContext(options, new StaticTenantContext { OrganizationId = OrgB });

        new IntegrationCurrencyReferenceChecker(db, Catalog()).IsValueReferenced(Usd).Should().BeFalse("the command found no row");

        var table = db.Model.FindEntityType(typeof(IntegrationConnection))!.GetTableName();
        commands.Sql.Should().ContainSingle()
            .Which.Should().Contain($"[{table}]").And.Contain("UPPER(").And.Contain("TRIM(")
            .And.NotContain("OrganizationId", "the tenant filter is ignored: the catalog is global")
            .And.NotContain("PayloadJson");
    }

    /// <summary>Records each command and answers it with one row holding false — "no row exists".</summary>
    private sealed class SqlServerWithoutAServer : DbCommandInterceptor
    {
        public List<string> Sql { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Sql.Add(command.CommandText);
            var table = new DataTable();
            table.Columns.Add("Value", typeof(bool));
            table.Rows.Add(false);
            return InterceptionResult<DbDataReader>.SuppressWithResult(table.CreateDataReader());
        }
    }

    /// <summary>There is no server: the connection is never really opened.</summary>
    private sealed class NeverOpen : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result) => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    [Fact]
    public void The_module_registers_the_checker_for_lookups_and_the_container_can_build_it()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Data:mainOrg"] = @"Server=(localdb)\MSSQLLocalDB;Database=SMS_QBI_never_opened;Trusted_Connection=True"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());

        services.AddIntegrationModule(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(ILookupReferenceChecker)
                                    && d.ImplementationType == typeof(IntegrationCurrencyReferenceChecker)
                                    && d.Lifetime == ServiceLifetime.Scoped);

        // Lookups always registers the catalog in the real host.
        services.AddSingleton(Mock.Of<ICurrencyCodeLookup>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetServices<ILookupReferenceChecker>()
            .Should().ContainSingle(c => c is IntegrationCurrencyReferenceChecker);
    }
}
