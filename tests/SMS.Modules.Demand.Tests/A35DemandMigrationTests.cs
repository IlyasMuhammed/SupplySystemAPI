using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Migrations;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A35 P3-01/P3-02 on real SQL Server (LocalDB): the Demand migration A35_MultiCurrencyOnDemandDocuments goes up, down and
/// up again, re-runs cleanly on a database that already has it (SMSGlobal drift), and its D-11 data step backfills what it
/// can — rate 1 for locked documents in their domain's base, the rate on file at the lock date for foreign ones, null when
/// there is none — and is a no-op when run again.
/// </summary>
public sealed class A35DemandMigrationTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private const string A34 = "20261004031854_A34_LeadTimeAndProductionOnSales";
    private readonly string _connection = $"{Server}Database=A35_DEM_Migr_{Guid.NewGuid():N};";

    private DemandDbContext NewContext(Guid? org = null) => new(
        new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = org ?? Guid.NewGuid() });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private static async Task<HashSet<(string Table, string Column)>> ColumnsAsync(DemandDbContext db)
    {
        var result = new HashSet<(string, string)>();
        var conn = db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'demand'";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    [Fact]
    public async Task Goes_up_down_and_up_and_reruns_cleanly_on_a_database_that_already_has_it()
    {
        await using var db = NewContext();
        var a35 = db.Database.GetMigrations().Single(m => m.EndsWith("_A35_MultiCurrencyOnDemandDocuments", StringComparison.Ordinal));
        db.Database.GetMigrations().Last().Should().Be(a35, "it is Demand's latest migration");

        await db.Database.MigrateAsync();
        var up = await ColumnsAsync(db);
        foreach (var (table, column, _) in A35_MultiCurrencyOnDemandDocuments.Columns)
            up.Should().Contain((table, column));

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(A34);
        var down = await ColumnsAsync(db);
        foreach (var (table, column, _) in A35_MultiCurrencyOnDemandDocuments.Columns)
            down.Should().NotContain((table, column), $"{table}.{column} is dropped by Down");
        down.Should().Contain(("sale_orders", "ProductionCreationPendingSince"), "A34's columns stay");

        await migrator.MigrateAsync(a35);
        (await ColumnsAsync(db)).Should().BeEquivalentTo(up);

        // Drift: the history forgets A35 but every column is there (SMSGlobal). It must run again cleanly.
        var history = db.GetService<IHistoryRepository>();
        await db.Database.ExecuteSqlRawAsync(history.GetDeleteScript(a35));
        await db.Database.MigrateAsync();
        (await ColumnsAsync(db)).Should().BeEquivalentTo(up);
        (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Backfill_locks_base_currency_documents_at_1_converts_foreign_ones_with_a_rate_and_leaves_the_rest_null()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var pkr = Guid.NewGuid(); var aed = Guid.NewGuid(); var eur = Guid.NewGuid(); var usd = Guid.NewGuid();
        var orderDate = new DateTime(2026, 10, 7);

        await using (var setup = NewContext(orgA))
        {
            await setup.Database.MigrateAsync();
            // The other modules' tables the data step reads, as they look on the shared database.
            await setup.Database.ExecuteSqlRawAsync(@"
EXEC(N'CREATE SCHEMA tenant'); EXEC(N'CREATE SCHEMA lookups'); EXEC(N'CREATE SCHEMA finance');");
            await setup.Database.ExecuteSqlRawAsync(@"
CREATE TABLE lookups.Currencies (Id uniqueidentifier PRIMARY KEY, Name nvarchar(50), Code nvarchar(3));
CREATE TABLE tenant.Organizations (Id uniqueidentifier PRIMARY KEY, BaseCurrency uniqueidentifier NULL);
CREATE TABLE tenant.organization_currency_settings (OrganizationId uniqueidentifier PRIMARY KEY,
    SaleBaseCurrencyId uniqueidentifier NOT NULL, PurchaseBaseCurrencyId uniqueidentifier NOT NULL);");
            await setup.Database.ExecuteSqlRawAsync(@"
INSERT lookups.Currencies VALUES ({0}, N'Pakistani Rupee', N'PKR');
INSERT tenant.Organizations VALUES ({1}, {0});
INSERT tenant.Organizations VALUES ({2}, {0});
INSERT tenant.organization_currency_settings VALUES ({2}, {0}, {3});",
                pkr, orgA, orgB, usd);
        }

        int soPkr, soAed, soEur, soDraft, sqSent, sqDraft, inqPlain, inqQuoted, poApproved, poDraft, poOrgB;
        await using (var db = NewContext(orgA))
        {
            SaleOrder Order(Guid currency, string status, decimal price, decimal qty) => new()
            {
                OrganizationId = orgA, SoNumber = $"SO-{Guid.NewGuid():N}"[..20], PartnerId = Guid.NewGuid(), OrderDate = orderDate,
                CurrencyId = currency, Status = status, Subtotal = qty * price, GrandTotal = qty * price,
                Lines = { new SaleOrderLine { OrganizationId = orgA, VariantUuid = Guid.NewGuid(), Quantity = qty, UnitPrice = price, LineTotal = qty * price } }
            };
            var o1 = Order(pkr, "CONFIRMED", 9200m, 25m);
            var o2 = Order(aed, "CONFIRMED", 120m, 50m);
            var o3 = Order(eur, "CONFIRMED", 10m, 1m);
            var o4 = Order(pkr, "DRAFT", 10m, 1m);
            db.SaleOrders.AddRange(o1, o2, o3, o4);

            var i1 = new SaleInquiry { OrganizationId = orgA, InquiryNumber = "INQ-1", PartnerId = Guid.NewGuid(), ReceivedDate = orderDate };
            var i2 = new SaleInquiry { OrganizationId = orgA, InquiryNumber = "INQ-2", PartnerId = Guid.NewGuid(), ReceivedDate = orderDate };
            db.SaleInquiries.AddRange(i1, i2);
            await db.SaveChangesAsync();

            SaleQuotation Quote(string number, Guid currency, DateTime? sentAt, int? inquiry) => new()
            {
                OrganizationId = orgA, QuotationNumber = number, PartnerId = Guid.NewGuid(), CurrencyId = currency,
                ValidFrom = orderDate, ValidTo = orderDate.AddDays(30), Status = sentAt is null ? "DRAFT" : "SENT", SentAt = sentAt,
                SourceInquiryId = inquiry,
                Lines = { new SaleQuotationLine { OrganizationId = orgA, LineNumber = 1, ProductDescription = "x", Quantity = 2, UnitPrice = 50,
                                                  DiscountPercent = 10, TaxAmount = 9, LineTotal = 99 } }
            };
            var q1 = Quote("SQ-1", pkr, orderDate, null);
            var q2 = Quote("SQ-2", aed, null, i2.Id);
            db.SaleQuotations.AddRange(q1, q2);

            PurchaseOrder Po(Guid org, string number, string status) => new()
            {
                UUID = Guid.NewGuid(), OrganizationId = org, PoNumber = number, SupplierName = "S", Status = status, TotalAmount = 500,
                CreatedDate = orderDate,
                Lines = { new PurchaseOrderLine { UUID = Guid.NewGuid(), OrganizationId = org, LineNo = 1, ItemDescription = "x", Quantity = 5, UnitPrice = 100, LineTotal = 500 } }
            };
            var p1 = Po(orgA, "PO-1", "APPROVED");
            var p2 = Po(orgA, "PO-2", "DRAFT");
            var p3 = Po(orgB, "PO-3", "DRAFT");
            db.PurchaseOrders.AddRange(p1, p2, p3);
            await db.SaveChangesAsync();

            (soPkr, soAed, soEur, soDraft) = (o1.Id, o2.Id, o3.Id, o4.Id);
            (sqSent, sqDraft, inqPlain, inqQuoted) = (q1.Id, q2.Id, i1.Id, i2.Id);
            (poApproved, poDraft, poOrgB) = (p1.Id, p2.Id, p3.Id);
        }

        // Finance's rates, as its CurrencyBootstrapper has them once it has run (after every module migrated).
        var currency = new FakeCurrencyService();
        currency.Bases[TransactionDomain.Sale] = pkr;
        currency.Bases[TransactionDomain.Purchase] = pkr;
        currency.Rate(aed, 76.3m, new DateOnly(2026, 10, 1));

        for (var run = 0; run < 2; run++) // the second run must change nothing
        {
            await using (var db = NewContext(orgA))
                await db.Database.ExecuteSqlRawAsync(A35_MultiCurrencyOnDemandDocuments.BackfillSql);

            if (run == 0)
            {
                // REV-01: the migration never reads Finance (Demand migrates first) — a foreign order is still unlocked.
                await using var mid = NewContext(orgA);
                (await mid.SaleOrders.IgnoreQueryFilters().AsNoTracking().SingleAsync(o => o.Id == soAed)).ExchangeRate.Should().BeNull();
            }

            // The rates-ready participant, per organization (orgB has no locked foreign document; nothing to do).
            await using (var db = NewContext(orgA))
            {
                var (locked, missing) = await DemandCurrencyBackfill.RunAsync(db, currency, orgA, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                locked.Should().Be(run == 0 ? 1 : 0, "only the AED order needs Finance; the second run finds nothing new");
                missing.Should().Be(1, "the EUR order has no rate on file");
            }

            await using var check = NewContext(orgA);
            var orders = await check.SaleOrders.IgnoreQueryFilters().Include(o => o.Lines).AsNoTracking().ToDictionaryAsync(o => o.Id);

            var pkrOrder = orders[soPkr];
            pkrOrder.ExchangeRate.Should().Be(1m);
            pkrOrder.BaseCurrencyId.Should().Be(pkr);
            pkrOrder.GrandTotalBase.Should().Be(230_000m);
            pkrOrder.Lines.Single().UnitPriceBase.Should().Be(9200m);

            var aedOrder = orders[soAed]; // T-C5-01 shape: 50 × AED 120 at 76.30 = PKR 457,800
            aedOrder.ExchangeRate.Should().Be(76.3m);
            aedOrder.BaseCurrencyId.Should().Be(pkr);
            aedOrder.RateLockedAt.Should().Be(orderDate);
            aedOrder.Lines.Single().LineTotalBase.Should().Be(457_800m);
            aedOrder.Lines.Single().UnitPriceBase.Should().Be(9_156m);
            aedOrder.GrandTotalBase.Should().Be(457_800m);
            aedOrder.SubtotalBase.Should().Be(457_800m);

            orders[soEur].ExchangeRate.Should().BeNull("no EUR rate is on file — never guessed");
            orders[soEur].Lines.Single().LineTotalBase.Should().BeNull();
            orders[soDraft].ExchangeRate.Should().BeNull("a draft is not locked");

            var quotes = await check.SaleQuotations.IgnoreQueryFilters().Include(q => q.Lines).AsNoTracking().ToDictionaryAsync(q => q.Id);
            quotes[sqSent].ExchangeRate.Should().Be(1m);
            quotes[sqSent].Lines.Single().DiscountAmountBase.Should().Be(10m);
            quotes[sqSent].Lines.Single().LineTotalBase.Should().Be(99m);
            quotes[sqDraft].ExchangeRate.Should().BeNull();

            var inquiries = await check.SaleInquiries.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(i => i.Id);
            inquiries[inqPlain].CurrencyId.Should().Be(pkr, "the sale base");
            inquiries[inqQuoted].CurrencyId.Should().Be(aed, "its quotation's currency");

            var pos = await check.PurchaseOrders.IgnoreQueryFilters().Include(p => p.Lines).AsNoTracking().ToDictionaryAsync(p => p.Id);
            pos[poApproved].CurrencyId.Should().Be(pkr);
            pos[poApproved].ExchangeRate.Should().Be(1m);
            pos[poApproved].TotalAmountBase.Should().Be(500m);
            pos[poApproved].Lines.Single().LineTotalBase.Should().Be(500m);
            pos[poDraft].CurrencyId.Should().Be(pkr);
            pos[poDraft].ExchangeRate.Should().BeNull();
            pos[poOrgB].CurrencyId.Should().Be(usd, "org B's purchase base from the currency settings");
        }
    }
}
