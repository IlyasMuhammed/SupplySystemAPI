using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A throwaway SQL Server database with the tables of the modules a test reads, created from their models
/// and dropped after. LocalDB by default; override with <c>SMS_TEST_SQLSERVER</c>.
/// </summary>
internal sealed class SqlServerHarness : IAsyncDisposable
{
    private readonly string _database;

    private SqlServerHarness(string database, string connectionString)
    {
        _database        = database;
        ConnectionString = connectionString;
    }

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

    /// <param name="demand">Create Demand's tables: the sale orders.</param>
    /// <param name="finance">Create Finance's tables: the receivables books.</param>
    /// <param name="logistics">Create Logistics' tables: the deliveries.</param>
    /// <param name="inventory">Create Inventory's tables: the warehouses.</param>
    internal static async Task<SqlServerHarness> CreateAsync(bool demand = false, bool finance = false, bool logistics = false, bool inventory = false)
    {
        var database = $"SMS_RptTest_{Guid.NewGuid():N}";
        var builder  = new SqlConnectionStringBuilder(MasterConnectionString) { InitialCatalog = "master" };

        await using (var conn = new SqlConnection(builder.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{database}]";
            await cmd.ExecuteNonQueryAsync();
        }

        builder.InitialCatalog = database;
        var harness = new SqlServerHarness(database, builder.ConnectionString);

        // Each module's model is self-contained (it points at the others by bare uuid), so each context
        // creates just its own tables in the shared database.
        if (demand)
        {
            await using var db = harness.NewDemandContext(Guid.NewGuid());
            await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        }

        if (finance)
        {
            await using var db = harness.NewFinanceContext(Guid.NewGuid());
            await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        }

        if (logistics)
        {
            await using var db = harness.NewLogisticsContext(Guid.NewGuid());
            await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        }

        if (inventory)
        {
            await using var db = harness.NewInventoryContext(Guid.NewGuid());
            await db.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        }

        return harness;
    }

    /// <summary>The retrying execution strategy every module's context is registered with in production.</summary>
    private void UseSql(DbContextOptionsBuilder options) =>
        options.UseSqlServer(ConnectionString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null));

    internal DemandDbContext NewDemandContext(Guid organizationId)
    {
        var options = new DbContextOptionsBuilder<DemandDbContext>();
        UseSql(options);
        return new DemandDbContext(options.Options, new StaticTenantContext { OrganizationId = organizationId });
    }

    internal FinanceDbContext NewFinanceContext(Guid organizationId)
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>();
        UseSql(options);
        return new FinanceDbContext(options.Options, new StaticTenantContext { OrganizationId = organizationId });
    }

    internal LogisticsDbContext NewLogisticsContext(Guid organizationId)
    {
        var options = new DbContextOptionsBuilder<LogisticsDbContext>();
        UseSql(options);
        return new LogisticsDbContext(options.Options, new StaticTenantContext { OrganizationId = organizationId });
    }

    internal InventoryDbContext NewInventoryContext(Guid organizationId)
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>();
        UseSql(options);
        return new InventoryDbContext(options.Options, new StaticTenantContext { OrganizationId = organizationId });
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
internal sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (!SqlServerHarness.IsAvailable)
            Skip = "No SQL Server reachable. Set SMS_TEST_SQLSERVER to run the report translation tests.";
    }
}

/// <summary>
/// A29-P9-01 §15 R1 — what the in-memory provider cannot show: that the register's queries translate to
/// SQL Server at all (the grouped sums, the per-order line count in the projection, the day bounds on a real
/// datetime2, the tenant filter) and that SQL Server and the in-memory provider agree on every answer.
/// </summary>
public class SalesOrderRegisterSqlServerTests
{
    private static readonly Guid Org      = RegisterWorld.Org;
    private static readonly Guid OtherOrg = RegisterWorld.OtherOrg;

    /// <summary>
    /// The same sixty orders on both providers: seven statuses, both delivery modes, three customers (one
    /// the name lookup does not know), three currencies (one the catalog does not know), some with no lines,
    /// some soft-deleted, some another organization's. Numbers are fixed so the two can be compared row by row.
    /// </summary>
    private static List<SaleOrder> Dataset(RegisterWorld w)
    {
        string[] statuses = ["DRAFT", "CONFIRMED", "PARTIALLY_FULFILLED", "FULFILLED", "INVOICED", "CLOSED", "CANCELLED"];
        Guid[] partners   = [w.Acme, w.Globex, Guid.Parse("33333333-3333-3333-3333-333333333333")];
        Guid[] currencies = [w.Pkr, w.Usd, Guid.Parse("44444444-4444-4444-4444-444444444444")];

        var orders = new List<SaleOrder>();
        for (var i = 0; i < 60; i++)
        {
            var order = new SaleOrder
            {
                UUID           = Guid.NewGuid(),
                TraceId        = Guid.NewGuid(),
                OrganizationId = i % 11 == 10 ? OtherOrg : Org,
                SoNumber       = $"SO-2026-{i:00000}",
                PartnerId      = partners[i % 3],
                OrderDate      = new DateTime(2026, 9, 1).AddDays(i % 20).AddMinutes(i * 37),
                CurrencyId     = currencies[(i / 3) % 3],
                Subtotal       = 100m + i * 7.35m,
                DiscountAmount = i % 5 * 1.25m,
                TaxAmount      = i % 3 * 2.5m,
                Status         = statuses[i % 7],
                DeliveryMode   = i % 2 == 0 ? "SHIP" : "SELF_PICKUP",
                IsDeleted      = i % 13 == 12,
                CreatedBy      = 1
            };
            order.GrandTotal = order.Subtotal - order.DiscountAmount + order.TaxAmount;

            for (var l = 0; l < i % 4; l++)
                order.Lines.Add(new SaleOrderLine
                {
                    UUID = Guid.NewGuid(), OrganizationId = order.OrganizationId, VariantUuid = Guid.NewGuid(),
                    Quantity = 1m, UnitPrice = 1m, LineTotal = 1m
                });

            orders.Add(order);
        }

        return orders;
    }

    private static SalesReportService ServiceOn(DemandDbContext db, RegisterWorld w)
    {
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.Where(w.Names.ContainsKey).ToDictionary(id => id, id => w.Names[id]));
        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns(() => [.. w.Currencies]);
        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync(new PoDocumentTemplateModel { CompanyName = "Northwind Trading" });

        return new SalesReportService(db, names.Object, lookups.Object, templates.Object, new FixedClock());
    }

    /// <summary>
    /// Everything a report says, one line each and in order, so a difference in which rows or in what order
    /// shows as a difference. Uuids are left out (each store is seeded with its own); amounts to the cent.
    /// </summary>
    private static string Fingerprint(SalesOrderRegisterReport r) => string.Join('\n',
        new[]
        {
            $"records {r.TotalRecords} page {r.Page} size {r.PageSize} pages {r.TotalPages}",
            $"criteria {r.Criteria.DateFrom:O} {r.Criteria.DateTo:O} {r.Criteria.Status} {r.Criteria.PartnerId} {r.Criteria.CustomerName} {r.Criteria.DeliveryMode}"
        }
        .Concat(r.Items.Select(i =>
            $"{i.SoNumber} {i.OrderDate:O} {i.ExpectedDeliveryDate:O} {i.PartnerId} {i.CustomerName} {i.Status} {i.DeliveryMode} " +
            $"{i.CurrencyCode} lines {i.LineCount} {i.Subtotal:F2} {i.DiscountAmount:F2} {i.TaxAmount:F2} {i.GrandTotal:F2}"))
        .Concat(r.Totals.Select(t =>
            $"total {t.CurrencyCode} {t.OrderCount} {t.Subtotal:F2} {t.DiscountAmount:F2} {t.TaxAmount:F2} {t.GrandTotal:F2}")));

    private static readonly SalesOrderRegisterFilter[] Filters =
    [
        new() { PageSize = 100 },
        new() { Page = 1, PageSize = 20 },
        new() { Page = 2, PageSize = 20 },
        new() { Page = 3, PageSize = 20 },
        new() { Page = 2, PageSize = 7 },
        new() { Status = "confirmed", PageSize = 100 },
        new() { DeliveryMode = "SELF_PICKUP", PageSize = 100 },
        new() { DateFrom = new DateTime(2026, 9, 5), DateTo = new DateTime(2026, 9, 12), PageSize = 100 },
        new() { DateFrom = new DateTime(2026, 9, 8, 13, 0, 0), PageSize = 100 },
        new() { DateTo = new DateTime(2026, 9, 3), PageSize = 100 },
        new() { PartnerId = RegisterWorld.GlobexId, PageSize = 100 },
        new() { Status = "INVOICED", DeliveryMode = "SELF_PICKUP", PartnerId = RegisterWorld.GlobexId, DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30), PageSize = 100 },
        new() { Status = "CANCELLED", DateFrom = new DateTime(2027, 1, 1), PageSize = 100 },
    ];

    private static RegisterWorld World() => new();

    [SqlServerFact]
    public async Task SQL_Server_and_the_in_memory_provider_give_the_same_register_for_every_filter()
    {
        await using var sql = await SqlServerHarness.CreateAsync(demand: true);
        var w = World();

        await using (var seed = sql.NewDemandContext(Org))
        {
            seed.SaleOrders.AddRange(Dataset(w));
            await seed.SaveChangesAsync();
        }
        using (var memory = w.Db(Org))
        {
            memory.SaleOrders.AddRange(Dataset(w));
            await memory.SaveChangesAsync();
        }

        foreach (var filter in Filters)
        {
            await using var db = sql.NewDemandContext(Org);
            var onSql    = await ServiceOn(db, w).GetOrderRegisterAsync(filter);
            var inMemory = await w.Service(Org).GetOrderRegisterAsync(filter);

            Fingerprint(onSql).Should().Be(Fingerprint(inMemory), $"filter {System.Text.Json.JsonSerializer.Serialize(filter)}");
        }

        await using var all = sql.NewDemandContext(Org);
        var everything = await ServiceOn(all, w).GetOrderRegisterAsync(new SalesOrderRegisterFilter { PageSize = 100 });
        everything.TotalRecords.Should().BeGreaterThan(30, "guards the comparison: a dataset that matched nothing would agree with anything");
    }

    [SqlServerFact]
    public async Task The_export_reads_every_row_in_the_same_order_on_SQL_Server()
    {
        await using var sql = await SqlServerHarness.CreateAsync(demand: true);
        var w = World();
        await using (var seed = sql.NewDemandContext(Org))
        {
            seed.SaleOrders.AddRange(Dataset(w));
            await seed.SaveChangesAsync();
        }
        using (var memory = w.Db(Org))
        {
            memory.SaleOrders.AddRange(Dataset(w));
            await memory.SaveChangesAsync();
        }

        await using var db = sql.NewDemandContext(Org);
        var onSql    = await ServiceOn(db, w).GetOrderRegisterForExportAsync(new SalesOrderRegisterFilter { Page = 4, PageSize = 2 });
        var inMemory = await w.Service(Org).GetOrderRegisterForExportAsync(new SalesOrderRegisterFilter { Page = 4, PageSize = 2 });

        onSql.Items.Count.Should().BeGreaterThan(30);
        Fingerprint(onSql).Should().Be(Fingerprint(inMemory));
    }

    [SqlServerFact]
    public async Task The_day_bounds_hold_to_the_last_tick_of_the_day_on_a_real_datetime2()
    {
        await using var sql = await SqlServerHarness.CreateAsync(demand: true);
        var w = new RegisterWorld();
        var lastTick = new DateTime(2026, 9, 10).AddDays(1).AddTicks(-1);

        await using (var seed = sql.NewDemandContext(Org))
        {
            foreach (var (number, date) in new[]
            {
                ("SO-BEFORE",     new DateTime(2026, 9, 4).AddDays(1).AddTicks(-1)),
                ("SO-FIRST",      new DateTime(2026, 9, 5)),
                ("SO-LAST-TICK",  lastTick),
                ("SO-NEXT-DAY",   new DateTime(2026, 9, 11)),
            })
                seed.SaleOrders.Add(new SaleOrder
                {
                    UUID = Guid.NewGuid(), OrganizationId = Org, SoNumber = number, PartnerId = w.Acme, CurrencyId = w.Pkr,
                    OrderDate = date, Status = "CONFIRMED", DeliveryMode = "SHIP", CreatedBy = 1
                });
            await seed.SaveChangesAsync();
        }

        await using var db = sql.NewDemandContext(Org);
        var report = await ServiceOn(db, w).GetOrderRegisterAsync(
            new SalesOrderRegisterFilter { DateFrom = new DateTime(2026, 9, 5), DateTo = new DateTime(2026, 9, 10) });

        report.Items.Select(i => i.SoNumber).Should().BeEquivalentTo("SO-FIRST", "SO-LAST-TICK");
    }

    [SqlServerFact]
    public async Task Another_organizations_orders_never_reach_the_register_or_its_totals_on_SQL_Server()
    {
        await using var sql = await SqlServerHarness.CreateAsync(demand: true);
        var w = new RegisterWorld();

        foreach (var (org, number, amount) in new[] { (Org, "SO-MINE", 100m), (OtherOrg, "SO-THEIRS", 7000m) })
        {
            await using var seed = sql.NewDemandContext(org);
            seed.SaleOrders.Add(new SaleOrder
            {
                UUID = Guid.NewGuid(), OrganizationId = org, SoNumber = number, PartnerId = w.Acme, CurrencyId = w.Pkr,
                OrderDate = new DateTime(2026, 9, 10), Subtotal = amount, GrandTotal = amount, Status = "CONFIRMED", DeliveryMode = "SHIP", CreatedBy = 1
            });
            await seed.SaveChangesAsync();
        }

        await using var mine = sql.NewDemandContext(Org);
        var report = await ServiceOn(mine, w).GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        report.Items.Select(i => i.SoNumber).Should().Equal("SO-MINE");
        (report.TotalRecords, report.Totals.Single().GrandTotal).Should().Be((1, 100m));
    }
}
