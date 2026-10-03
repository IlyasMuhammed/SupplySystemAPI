using System.Data;
using System.Data.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// What Logistics tells Lookups before a currency is deleted or its code changed. Logistics keeps currency
/// CODES, so the checker turns the id into its code first; every organization's rows count, deleted ones
/// included, and stored codes match whatever their case or padding.
/// </summary>
public class LogisticsCurrencyReferenceCheckerTests
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

    /// <summary>The rows are written in organization A; Lookups' request comes from B, not a super admin.</summary>
    private LogisticsCurrencyReferenceChecker Checker() => new(LogisticsTestDb.OpenAs(_dbName, OrgB), Catalog());

    // ── Every column, one at a time ──────────────────────────────────────────

    private const string Other = "ZZZ";

    private static readonly Dictionary<string, Action<LogisticsDbContext, string, bool>> Writers = new()
    {
        ["CarrierInvoice.Currency"] = (db, code, deleted) => db.CarrierInvoices.Add(new CarrierInvoice
        {
            UUID = Guid.NewGuid(), OrganizationId = OrgA, CarrierId = 1, InvoiceNumber = "INV-1", InvoiceDate = DateTime.UtcNow,
            Currency = code, TotalAmount = 100m, IsDelete = deleted
        }),
        ["CodCollection.Currency"] = (db, code, deleted) => db.CodCollections.Add(new CodCollection
        {
            UUID = Guid.NewGuid(), OrganizationId = OrgA, ConsignmentId = 1, ExpectedAmount = 100m, Currency = code, IsDelete = deleted
        }),
        ["Consignment.CodCurrency"] = (db, code, deleted) => db.Consignments.Add(new Consignment
        {
            UUID = Guid.NewGuid(), OrganizationId = OrgA, ConsignmentNumber = "SHP-2026-00001", CodAmount = 100m, CodCurrency = code, IsDelete = deleted
        }),
        ["Consignment.FreightCurrency"] = (db, code, deleted) => db.Consignments.Add(new Consignment
        {
            UUID = Guid.NewGuid(), OrganizationId = OrgA, ConsignmentNumber = "SHP-2026-00002", FreightCost = 100m, FreightCurrency = code, IsDelete = deleted
        }),
        ["Carrier.DefaultCurrency"] = (db, code, deleted) => db.Carriers.Add(new Carrier
        {
            UUID = Guid.NewGuid(), OrganizationId = OrgA, Name = "Carrier", Code = "CAR", DefaultCurrency = code, IsDelete = deleted
        }),
        ["FreightAccrual.Currency"] = (db, code, deleted) => db.FreightAccruals.Add(new FreightAccrual
        {
            UUID = Guid.NewGuid(), OrganizationId = OrgA, ConsignmentId = 1, AccruedAmount = 100m, Currency = code, IsDelete = deleted
        }),
        ["FreightAccrual.BookedCurrency"] = (db, code, deleted) => db.FreightAccruals.Add(new FreightAccrual
        {
            UUID = Guid.NewGuid(), OrganizationId = OrgA, ConsignmentId = 1, AccruedAmount = 100m, Currency = Other,
            BookedAmount = 100m, BookedCurrency = code, IsDelete = deleted
        }),
        ["RateCard.Currency"] = (db, code, deleted) => db.RateCards.Add(new RateCard
        {
            UUID = Guid.NewGuid(), OrganizationId = OrgA, CarrierId = 1, Name = "Tariff", Currency = code, IsDelete = deleted
        }),
        // The command ledger has no IsDelete: it is never deleted.
        ["CarrierCommand.CostCurrency"] = (db, code, _) => db.CarrierCommands.Add(new CarrierCommand
        {
            UUID = Guid.NewGuid(), OrganizationId = OrgA, CommandType = "BOOK", IdempotencyKey = Guid.NewGuid().ToString(),
            RequestFingerprint = "fp", ConsignmentId = 1, ProviderKey = "MANUAL", Status = "SUCCEEDED", Cost = 100m, CostCurrency = code
        }),
    };

    public static TheoryData<string> Columns => new(Writers.Keys);

    public static TheoryData<string> DeletableColumns => new(Writers.Keys.Where(k => !k.StartsWith("CarrierCommand.")));

    private void Seed(string column, string code, bool deleted = false)
    {
        using var db = LogisticsTestDb.OpenAs(_dbName, OrgA);
        Writers[column](db, code, deleted);
        db.SaveChanges();
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public void A_code_on_another_organizations_row_is_referenced(string column)
    {
        Seed(column, "USD");

        Checker().IsValueReferenced(Usd).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public void A_stored_code_matches_whatever_its_case_or_padding(string column)
    {
        Seed(column, " usd ");

        Checker().IsValueReferenced(Usd).Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(Columns))]
    public void A_different_code_is_not_a_reference(string column)
    {
        Seed(column, "EUR");
        Seed(column, "USDT");

        Checker().IsValueReferenced(Usd).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(DeletableColumns))]
    public void Deleted_rows_still_count(string column)
    {
        Seed(column, "USD", deleted: true);

        Checker().IsValueReferenced(Usd).Should().BeTrue("deleted documents stay on record with their currency");
    }

    [Fact]
    public void The_tenant_filter_would_have_hidden_these_rows()
    {
        foreach (var column in Writers.Keys) Seed(column, "USD");

        using var orgB = LogisticsTestDb.OpenAs(_dbName, OrgB);
        orgB.CarrierInvoices.Count().Should().Be(0);
        orgB.Consignments.Count().Should().Be(0);
        orgB.CarrierCommands.Count().Should().Be(0);

        Checker().IsValueReferenced(Usd).Should().BeTrue("the checker looks across organizations");
    }

    // ── Ids that are not currencies ──────────────────────────────────────────

    [Fact]
    public void An_id_that_is_not_a_currency_is_not_referenced()
    {
        foreach (var column in Writers.Keys) Seed(column, "USD");

        Checker().IsValueReferenced(NotCurrency).Should().BeFalse();
    }

    [Fact]
    public void A_currency_with_a_blank_code_is_not_referenced_even_by_rows_with_an_empty_code()
    {
        Seed("CarrierInvoice.Currency", "");
        Seed("CodCollection.Currency", "  ");

        Checker().IsValueReferenced(BlankCode).Should().BeFalse();
    }

    [Fact]
    public void The_empty_id_is_answered_without_asking_the_catalog()
    {
        foreach (var column in Writers.Keys) Seed(column, "USD");

        // The strict mock has no setup for Guid.Empty, so asking it would throw.
        Checker().IsValueReferenced(Guid.Empty).Should().BeFalse();
    }

    // ── What SQL Server is actually sent ─────────────────────────────────────

    [Fact]
    public void Every_table_is_queried_in_sql_server_trimmed_upper_cased_and_across_organizations()
    {
        var commands = new SqlServerWithoutAServer();
        var options = new DbContextOptionsBuilder<LogisticsDbContext>()
            .UseSqlServer("Server=none;Database=none;Trusted_Connection=True;")
            .AddInterceptors(commands, new NeverOpen())
            .Options;
        using var db = new LogisticsDbContext(options, new StaticTenantContext { OrganizationId = OrgB });

        new LogisticsCurrencyReferenceChecker(db, Catalog()).IsValueReferenced(Usd).Should().BeFalse("no command found a row");

        string[] tables =
        [
            Table<CarrierInvoice>(db), Table<CodCollection>(db), Table<Consignment>(db), Table<Carrier>(db),
            Table<FreightAccrual>(db), Table<RateCard>(db), Table<CarrierCommand>(db)
        ];
        commands.Sql.Should().HaveCount(tables.Length, "one query per table, each answered 'no match'");
        foreach (var table in tables)
            commands.Sql.Should().ContainSingle(s => s.Contains($"[{table}]"), $"{table} must be checked");
        commands.Sql.Should().OnlyContain(s => s.Contains("UPPER(") && s.Contains("TRIM("), "codes are compared trimmed and case-insensitively in SQL");
        commands.Sql.Should().NotContain(s => s.Contains("OrganizationId"), "the tenant filter is ignored: the catalog is global");
    }

    private static string Table<T>(DbContext db) => db.Model.FindEntityType(typeof(T))!.GetTableName()!;

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

    // ── Registration ─────────────────────────────────────────────────────────

    [Fact]
    public void The_module_registers_the_checker_for_lookups_and_the_container_can_build_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());

        services.AddLogisticsModule(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(ILookupReferenceChecker)
                                    && d.ImplementationType == typeof(LogisticsCurrencyReferenceChecker)
                                    && d.Lifetime == ServiceLifetime.Scoped);

        // Lookups always registers the catalog in the real host.
        services.AddSingleton(Mock.Of<ICurrencyCodeLookup>());
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetServices<ILookupReferenceChecker>()
            .Should().ContainSingle(c => c is LogisticsCurrencyReferenceChecker);
    }
}
