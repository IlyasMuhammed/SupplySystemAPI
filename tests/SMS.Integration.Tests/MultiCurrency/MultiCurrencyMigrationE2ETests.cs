using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.MultiCurrency;

/// <summary>
/// A35 (QA) — every owner's A35 migration on real SQL Server (LocalDB):
/// <list type="bullet">
/// <item><b>Fresh:</b> Tenancy, Suppliers, Demand and Finance migrate from an empty database in the host's order, and run
/// again cleanly when the history forgets the A35 rows (the drifted shared database).</item>
/// <item><b>Upgrade of a pre-A35 database with real data, through the real startup:</b> a host is booted, real PKR documents
/// are made through the API (approved PO + GRN, confirmed SOs, an issued invoice), the host is stopped, every A35 migration
/// is rolled back (Down) so the database is exactly pre-A35 with data, legacy shapes are added by SQL (a confirmed AED
/// order, legacy <c>finance.exchange_rates</c> rows incl. a soft-deleted one and a pair without the rate currency), then the
/// host boots again: Migrate + CurrencyBootstrapper (legacy rate conversion, then DemandCurrencyBackfill per org — REV-01).
/// Asserts the settings row, the SYSTEM row, the converted rates, the D-11 document backfill (PKR → 1; AED → the converted
/// 76.30; drafts null; PO currency = purchase base), the Suppliers column and partner reads. Then the history forgets every
/// A35 row and the host boots a third time: nothing duplicates, nothing changes.</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~MultiCurrencyMigrationE2ETests</c>.</para>
/// </summary>
public sealed class MultiCurrencyMigrationE2ETests
{
    private static DbContextOptions<T> Opts<T>(string cs) where T : DbContext => new DbContextOptionsBuilder<T>().UseSqlServer(cs).Options;

    /// <summary>The four contexts with A35 migrations, in the host's startup order (Program.cs).</summary>
    private static IEnumerable<(string Name, Func<string, DbContext> Make)> Contexts() =>
    [
        ("Tenancy",   Internal("SMS.Modules.Tenancy", "SMS.Modules.Tenancy.Data.TenancyDbContext")),
        ("Suppliers", cs => new SuppliersDbContext(Opts<SuppliersDbContext>(cs), new StaticTenantContext())),
        ("Demand",    cs => new DemandDbContext(Opts<DemandDbContext>(cs), new StaticTenantContext())),
        ("Finance",   cs => new FinanceDbContext(Opts<FinanceDbContext>(cs), new StaticTenantContext())),
    ];

    /// <summary>An internal DbContext (Tenancy's) built by reflection with SQL Server options.</summary>
    private static Func<string, DbContext> Internal(string assembly, string type) => cs =>
    {
        var t = System.Reflection.Assembly.Load(assembly).GetType(type, throwOnError: true)!;
        var builder = (DbContextOptionsBuilder)Activator.CreateInstance(typeof(DbContextOptionsBuilder<>).MakeGenericType(t))!;
        builder.UseSqlServer(cs);
        return (DbContext)Activator.CreateInstance(t, builder.Options)!;
    };

    private static string LocalDb(string db) =>
        $"Server=(localdb)\\mssqllocaldb;Database={db};Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

    private static async Task ForgetA35HistoryAsync(string cs) =>
        await A35HostFactory.ExecuteAsync(cs, "DELETE FROM [__EFMigrationsHistory] WHERE [MigrationId] LIKE N'%[_]A35[_]%'");

    [Fact]
    public async Task Fresh_database_A35_contexts_migrate_from_empty_in_host_order_and_rerun_cleanly()
    {
        var name = $"SMS_A35_MIG_FRESH_{Guid.NewGuid():N}";
        var cs = LocalDb(name);
        try
        {
            await using (var master = new Microsoft.Data.SqlClient.SqlConnection(LocalDb("master")))
            {
                await master.OpenAsync();
                await using var cmd = master.CreateCommand();
                cmd.CommandText = $"CREATE DATABASE [{name}]";
                await cmd.ExecuteNonQueryAsync();
            }

            var failures = new List<string>();
            foreach (var (ctxName, make) in Contexts())
            {
                await using var db = make(cs);
                db.Database.GetMigrations().Should().Contain(m => m.Contains("_A35_"), $"{ctxName} has an A35 migration");
                if (ctxName == "Finance")
                {
                    // Pre-existing (not A35): Finance's chain does not replay from empty ("finance.debit_notes" is altered
                    // before it exists), which is why every host factory builds Finance from its model. So: the current model's
                    // tables + stamped history, every A35 migration rolled back = a pre-A35 Finance; then A35 goes up from there.
                    await db.GetInfrastructure().GetRequiredService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>().CreateTablesAsync();
                    var version = typeof(DbContext).Assembly.GetName().Version!.ToString(3);
                    await db.Database.ExecuteSqlRawAsync(
                        "IF OBJECT_ID(N'[__EFMigrationsHistory]', N'U') IS NULL CREATE TABLE [__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL, " +
                        "[ProductVersion] nvarchar(32) NOT NULL, CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId]));");
                    var all = db.Database.GetMigrations().ToList();
                    foreach (var id in all)
                        await db.Database.ExecuteSqlRawAsync("INSERT INTO [__EFMigrationsHistory] VALUES ({0}, {1})", id, version);
                    try { await db.GetService<IMigrator>().MigrateAsync(all[all.FindIndex(m => m.Contains("_A35_")) - 1]); }
                    catch (Exception e) { failures.Add($"Finance A35 Down: {e.GetType().Name}: {e.Message}"); }
                }
                try { await db.Database.MigrateAsync(); }
                catch (Exception e) { failures.Add($"{ctxName} from empty: {e.GetType().Name}: {e.Message}"); }
            }
            failures.Should().BeEmpty("every A35 context migrates from an empty database");

            await ForgetA35HistoryAsync(cs);
            foreach (var (ctxName, make) in Contexts())
            {
                await using var db = make(cs);
                try { await db.Database.MigrateAsync(); }
                catch (Exception e) { failures.Add($"{ctxName} re-run: {e.GetType().Name}: {e.Message}"); }
                (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty($"{ctxName} is up to date");
            }
            failures.Should().BeEmpty("A35 migrations re-run cleanly on a database that already has them");
        }
        finally
        {
            await A35HostFactory.DropDatabaseAsync(name);
        }
    }

    [Fact]
    public async Task Pre_A35_database_with_real_data_upgrades_through_the_real_startup_and_reboots_without_change()
    {
        // ── 1. Deploy and make real (single-currency) data through the API ──────────────
        var f1 = new SapWebApplicationFactory();
        await f1.InitializeAsync();
        var dbName = f1.DatabaseName;
        var cs = LocalDb(dbName);
        var org = f1.OrganizationId;
        Guid pkr, aed, po, so1, so2, so3, invoice;
        try
        {
            var k = new SapKit(f1, "MIG");
            pkr = await k.PkrBaseAsync();
            aed = await k.EnsureCurrencyAsync("AED", "UAE Dirham", "AED");
            await k.EnsureCurrencyAsync("USD", "US Dollar", "$");
            await k.EnsureCurrencyAsync("EUR", "Euro", "€");
            await k.CreateApproverPlaceholdersAsync();
            var wh = await k.CreateWarehouseAsync();
            var vendor = await k.CreateVendorAsync("Legacy Vendor");
            var customer = await k.CreateCustomerAsync("Legacy Customer");
            var item = await k.CreateProductAsync("Legacy Widget", 50m, 100m);
            (po, _) = await k.StockUpAsync(vendor, wh, (item, 100m, 50m));
            (so1, _, invoice) = await k.SellAsync(customer, pkr, SapKit.Line(item, 2m));
            so2 = await k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(item, 3m));
            await k.ConfirmSaleOrderAsync(so2);
            so3 = await k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(item, 1m));
        }
        finally
        {
            f1.Dispose(); // stop the host, keep the database
        }

        try
        {
            // ── 2. Roll every A35 migration back: the database is now pre-A35, with its data ──
            foreach (var (ctxName, make) in Contexts().Reverse())
            {
                await using var db = make(cs);
                var all = db.Database.GetMigrations().ToList();
                var first = all.FindIndex(m => m.Contains("_A35_"));
                first.Should().BePositive($"{ctxName} has a migration before A35");
                await db.GetService<IMigrator>().MigrateAsync(all[first - 1]);
            }
            (await A35HostFactory.QueryAsync(cs, "SELECT OBJECT_ID(N'finance.currency_rates') AS T, COL_LENGTH(N'demand.sale_orders', N'ExchangeRate') AS C, " +
                "OBJECT_ID(N'tenant.organization_currency_settings') AS S, COL_LENGTH(N'suppliers.BusinessPartners', N'DefaultSaleCurrency') AS P"))
                .Single().Values.Should().OnlyContain(v => v == null, "every A35 object is gone after Down");

            // Legacy shapes: a confirmed AED order (S-era SO currency), legacy rates.
            await A35HostFactory.ExecuteAsync(cs, "UPDATE demand.sale_orders SET CurrencyId = @aed WHERE UUID = @so", ("@aed", aed), ("@so", so2));
            var from = DateTime.UtcNow.Date.AddDays(-30);
            const string ins = "INSERT finance.exchange_rates (Uuid, OrganizationId, FromCurrencyCode, ToCurrencyCode, Rate, EffectiveDate, Source, IsDelete, CreatedBy, CreatedDate) " +
                               "VALUES (NEWID(), @o, @f, @t, @r, @d, N'MANUAL', @del, 1, SYSUTCDATETIME())";
            await A35HostFactory.ExecuteAsync(cs, ins, ("@o", org), ("@f", "AED"), ("@t", "PKR"), ("@r", 76.30m), ("@d", from), ("@del", false));
            await A35HostFactory.ExecuteAsync(cs, ins, ("@o", org), ("@f", "PKR"), ("@t", "USD"), ("@r", 0.0036m), ("@d", from), ("@del", false));
            await A35HostFactory.ExecuteAsync(cs, ins, ("@o", org), ("@f", "USD"), ("@t", "EUR"), ("@r", 0.92m), ("@d", from), ("@del", false));
            await A35HostFactory.ExecuteAsync(cs, ins, ("@o", org), ("@f", "AED"), ("@t", "PKR"), ("@r", 99m), ("@d", from.AddDays(1)), ("@del", true));

            // ── 3. Boot the upgraded code on it ───────────────────────────────────────────
            using (var f2 = new A35HostFactory(dbName))
            {
                await f2.StartAsync();
                await AssertUpgradedAsync(f2, cs, org, pkr, aed, po, so1, so2, so3, invoice);
            }

            var counts = await CountsAsync(cs);

            // ── 4. The drifted shared DB: history forgets A35, the host boots again ────────
            await ForgetA35HistoryAsync(cs);
            using (var f3 = new A35HostFactory(dbName))
            {
                await f3.StartAsync();
                (await CountsAsync(cs)).Should().BeEquivalentTo(counts, "a re-run duplicates nothing");
                await AssertUpgradedAsync(f3, cs, org, pkr, aed, po, so1, so2, so3, invoice);
            }
        }
        finally
        {
            await A35HostFactory.DropDatabaseAsync(dbName);
            Environment.SetEnvironmentVariable("Data__mainOrg", null);
        }
    }

    private static async Task<Dictionary<string, int>> CountsAsync(string cs)
    {
        var r = (await A35HostFactory.QueryAsync(cs,
            "SELECT (SELECT COUNT(*) FROM finance.currency_rates) AS Rates, (SELECT COUNT(*) FROM finance.org_currencies) AS Currencies, " +
            "(SELECT COUNT(*) FROM tenant.organization_currency_settings) AS Settings, (SELECT COUNT(*) FROM finance.exchange_differences) AS Diffs")).Single();
        return r.ToDictionary(x => x.Key, x => Convert.ToInt32(x.Value));
    }

    private static async Task AssertUpgradedAsync(A35HostFactory f, string cs, Guid org, Guid pkr, Guid aed,
        Guid po, Guid so1, Guid so2, Guid so3, Guid invoice)
    {
        // Settings (TEN backfill): all PKR, rate currency PKR.
        var s = (await A35HostFactory.QueryAsync(cs, "SELECT * FROM tenant.organization_currency_settings WHERE OrganizationId = @o", ("@o", org))).Single();
        foreach (var col in new[] { "SaleBaseCurrencyId", "PurchaseBaseCurrencyId", "ServiceBaseCurrencyId", "RateCurrencyId" })
            s[col].Should().Be(pkr, col);

        // Org currencies + the SYSTEM row (CUR bootstrapper).
        (await A35HostFactory.QueryAsync(cs, "SELECT Code, IsActive FROM finance.org_currencies WHERE OrganizationId = @o AND CurrencyId = @c", ("@o", org), ("@c", pkr)))
            .Should().ContainSingle().Which["IsActive"].Should().Be(true);
        var rates = await A35HostFactory.QueryAsync(cs,
            "SELECT CurrencyCode, Rate, EffectiveFrom, EffectiveTo, Source FROM finance.currency_rates WHERE OrganizationId = @o ORDER BY CurrencyCode, EffectiveFrom", ("@o", org));
        rates.Should().ContainSingle(r => (string)r["CurrencyCode"]! == "PKR");
        var sys = rates.Single(r => (string)r["CurrencyCode"]! == "PKR");
        (sys["Rate"], sys["Source"], ((DateTime)sys["EffectiveFrom"]!).Year, ((DateTime)sys["EffectiveTo"]!).Year)
            .Should().Be(((object?)1m, (object?)"SYSTEM", 2000, 9999));

        // Legacy rates (D-3): AED→PKR as AED's rate; PKR→USD as USD = 1/0.0036; USD→EUR not converted; the deleted row ignored.
        var aedRows = rates.Where(r => (string)r["CurrencyCode"]! == "AED").ToList();
        aedRows.Should().ContainSingle("the soft-deleted 99 row is not converted");
        aedRows[0]["Rate"].Should().Be(76.30m);
        ((DateTime)aedRows[0]["EffectiveTo"]!).Year.Should().Be(9999);
        rates.Where(r => (string)r["CurrencyCode"]! == "USD").Should().ContainSingle()
            .Which["Rate"].Should().Be(Math.Round(1m / 0.0036m, 10, MidpointRounding.AwayFromZero), "reciprocal of PKR→USD");
        rates.Should().NotContain(r => (string)r["CurrencyCode"]! == "EUR", "USD→EUR has no rate-currency side — reported, never guessed");

        // Demand documents (D-11 via the migration + DemandCurrencyBackfill after the rate conversion).
        var orders = (await A35HostFactory.QueryAsync(cs,
            "SELECT UUID, Status, ExchangeRate, BaseCurrencyId, GrandTotal, GrandTotalBase FROM demand.sale_orders WHERE UUID IN (@a, @b, @c)",
            ("@a", so1), ("@b", so2), ("@c", so3))).ToDictionary(r => (Guid)r["UUID"]!);
        orders[so1]["ExchangeRate"].Should().Be(1m, "a PKR order in a PKR org");
        orders[so1]["GrandTotalBase"].Should().Be(orders[so1]["GrandTotal"]);
        orders[so2]["ExchangeRate"].Should().Be(76.30m, "the locked AED order gets the converted legacy rate at its lock date (REV-01 ordering)");
        orders[so2]["BaseCurrencyId"].Should().Be(pkr);
        orders[so2]["GrandTotalBase"].Should().Be(Math.Round((decimal)orders[so2]["GrandTotal"]! * 76.30m, 2, MidpointRounding.AwayFromZero));
        orders[so3]["ExchangeRate"].Should().BeNull("a draft is not locked");

        var poRow = (await A35HostFactory.QueryAsync(cs, "SELECT CurrencyId, ExchangeRate, TotalAmount, TotalAmountBase FROM demand.purchase_orders WHERE UUID = @p", ("@p", po))).Single();
        poRow["CurrencyId"].Should().Be(pkr, "a pre-A35 PO gets the purchase base");
        poRow["ExchangeRate"].Should().Be(1m);
        poRow["TotalAmountBase"].Should().Be(poRow["TotalAmount"]);

        var inv = (await A35HostFactory.QueryAsync(cs, "SELECT CurrencyId, BaseCurrencyId, ExchangeRate FROM finance.sales_invoices WHERE Uuid = @i", ("@i", invoice))).Single();
        inv["CurrencyId"].Should().Be(pkr);
        inv["BaseCurrencyId"].Should().Be(pkr);
        inv["ExchangeRate"].Should().Be(1m);

        // Suppliers column (TEN, migrate-only startup step) + reads through the API.
        (await A35HostFactory.QueryAsync(cs, "SELECT COL_LENGTH(N'suppliers.BusinessPartners', N'DefaultSaleCurrency') AS L")).Single()["L"]
            .Should().NotBeNull("the Suppliers A35 migration ran at startup");
        using var admin = f.Admin();
        foreach (var url in new[] { "/api/partners", "/api/currencies", "/api/currency-rates/active", "/api/organization/currency-settings",
                                     $"/api/sale-orders/{so2}", $"/api/purchase-orders/{po}" })
        {
            var resp = await admin.GetAsync(url);
            ((int)resp.StatusCode).Should().Be(200, $"{url} after the upgrade — {await resp.Content.ReadAsStringAsync()}");
        }
    }
}
