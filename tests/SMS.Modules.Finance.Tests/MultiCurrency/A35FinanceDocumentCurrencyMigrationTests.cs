using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A35 P3-03 — <c>A35_FinanceDocumentCurrency</c> on a throwaway LocalDB database: down (columns gone, data kept), up
/// (columns back, same-currency documents backfilled to rate 1 / base = amount, currency ids from codes, supplier payments
/// given their invoices' currency, foreign documents without a rate left for the startup backfill), down + up again, and a
/// re-run with the history row lost (the drifted shared database).
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class A35FinanceDocumentCurrencyMigrationTests : IAsyncLifetime
{
    private const string Server   = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private const string Name     = "A35_FinanceDocumentCurrency";
    private const string Previous = "20261007182414_A35_CurrencyCore";
    private readonly string _connection = $"{Server}Database=A35_FinDocMig_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _pkr = Guid.NewGuid();
    private readonly Guid _usd = Guid.NewGuid();

    private FinanceDbContext NewContext() => new(
        new DbContextOptionsBuilder<FinanceDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = _org });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull ? null : v;
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> NewColumnCountAsync() => (int)(await ScalarAsync(
        "SELECT COUNT(*) FROM sys.columns WHERE (object_id = OBJECT_ID('finance.sales_invoices') AND name IN ('CurrencyId','BaseCurrencyId','ExchangeRateLockedAt')) " +
        "OR (object_id = OBJECT_ID('finance.invoices') AND name IN ('CurrencyId','BaseCurrencyId','ExchangeRateLockedAt')) " +
        "OR (object_id = OBJECT_ID('finance.customer_payments') AND name IN ('CurrencyId','ExchangeRate','BaseCurrencyId','AmountBase','ExchangeDifference')) " +
        "OR (object_id = OBJECT_ID('finance.payment_allocations') AND name = 'ExchangeDifference') " +
        "OR (object_id = OBJECT_ID('finance.supplier_payments') AND name IN ('CurrencyCode','CurrencyId','ExchangeRate','BaseCurrencyId','AmountBase','ExchangeDifference')) " +
        "OR (object_id = OBJECT_ID('finance.supplier_payment_lines') AND name = 'ExchangeDifference')"))!;

    private const int Expected = 3 + 3 + 5 + 1 + 6 + 1;

    private static Task UpAsync(FinanceDbContext db) =>
        db.GetService<IMigrator>().MigrateAsync(db.Database.GetMigrations().Single(m => m.EndsWith("_" + Name)));

    [Fact]
    public async Task Down_up_backfill_and_a_rerun_on_a_drifted_database_all_work()
    {
        // The current model, every migration up to this one recorded as applied (Finance's early migrations do not replay on an
        // empty database), and the two tables the data step reads from other modules.
        await using (var db = NewContext())
        {
            await db.Database.EnsureCreatedAsync();
            await ExecAsync("CREATE TABLE [dbo].[__EFMigrationsHistory] ([MigrationId] nvarchar(150) NOT NULL PRIMARY KEY, [ProductVersion] nvarchar(32) NOT NULL);");
            foreach (var id in db.Database.GetMigrations().Where(m => string.CompareOrdinal(m, Previous) <= 0 || m.EndsWith("_" + Name)))
                await ExecAsync($"INSERT INTO [dbo].[__EFMigrationsHistory] VALUES (N'{id}', N'9.0.0');");
        }
        await ExecAsync("CREATE SCHEMA lookups;");
        await ExecAsync("CREATE TABLE lookups.Currencies (Id uniqueidentifier NOT NULL PRIMARY KEY, Name nvarchar(100) NOT NULL, Code nvarchar(10) NULL, Symbol nvarchar(10) NULL);");
        await ExecAsync($"INSERT INTO lookups.Currencies VALUES ('{_pkr}', 'Rupee', 'PKR', 'Rs'), ('{_usd}', 'Dollar', ' usd ', '$');");
        await ExecAsync("CREATE SCHEMA tenant;");
        await ExecAsync("CREATE TABLE tenant.Organizations (Id uniqueidentifier NOT NULL PRIMARY KEY, BaseCurrency uniqueidentifier NULL);");
        await ExecAsync($"INSERT INTO tenant.Organizations VALUES ('{_org}', '{_pkr}');");

        // Documents as they stood before A35.
        var partner = Guid.NewGuid();
        var day = new DateTime(2026, 9, 1);
        var siPkr     = Receivables.Invoice(_org, partner, "SI-PKR", day, 1000m);
        var siUsdRate = Receivables.Invoice(_org, partner, "SI-USD-R", day, 10m, currency: "USD");
        siUsdRate.ExchangeRate = 280m; siUsdRate.BaseCurrencyCode = "PKR"; siUsdRate.BaseGrandTotal = 2800m;
        var siUsdNone = Receivables.Invoice(_org, partner, "SI-USD-N", day, 10m, currency: "USD");
        var siDraft   = Receivables.Invoice(_org, partner, "SI-DRAFT", day, 50m, status: "DRAFT");
        var bill = new Invoice
        {
            UUID = Guid.NewGuid(), OrganizationId = _org, InvoiceNumber = "INV-1", SupplierName = "S", Currency = "PKR",
            TotalAmount = 500m, MatchStatus = "Approved", ApprovedAt = day, InvoiceDate = day, ReceivedDate = day, DueDate = day, CreatedDate = day
        };
        var usdBill = new Invoice
        {
            UUID = Guid.NewGuid(), OrganizationId = _org, InvoiceNumber = "INV-2", SupplierName = "S", Currency = "USD",
            TotalAmount = 100m, MatchStatus = "Approved", InvoiceDate = day, ReceivedDate = day, DueDate = day, CreatedDate = day
        };
        var receipt = new CustomerPayment
        {
            UUID = Guid.NewGuid(), OrganizationId = _org, PartnerId = partner, PartnerName = "C", PaymentNumber = "CPAY-1",
            PaymentDate = day, Amount = 400m, PaymentMethod = "CASH", CurrencyCode = "PKR", CreatedDate = day
        };
        receipt.Allocations.Add(new PaymentAllocation { UUID = Guid.NewGuid(), OrganizationId = _org, SalesInvoice = siPkr, AllocatedAmount = 400m, AllocatedAt = day });
        var usdPayment = new SupplierPayment
        {
            UUID = Guid.NewGuid(), OrganizationId = _org, PaymentNumber = "SPAY-1", SupplierName = "S", PaymentDate = day,
            PaymentMethod = "CASH", TotalAmount = 40m, Status = "POSTED", CreatedDate = day
        };
        usdPayment.Lines.Add(new SupplierPaymentLine { UUID = Guid.NewGuid(), OrganizationId = _org, InvoiceUuid = usdBill.UUID, InvoiceNumber = "INV-2", AllocatedAmount = 40m });
        var advance = new SupplierPayment
        {
            UUID = Guid.NewGuid(), OrganizationId = _org, PaymentNumber = "SPAY-2", SupplierName = "S", PaymentDate = day,
            PaymentMethod = "CASH", TotalAmount = 70m, Status = "POSTED", PaymentType = "ADVANCE_PAYMENT", CreatedDate = day
        };
        await using (var db = NewContext())
        {
            db.AddRange(siPkr, siUsdRate, siUsdNone, siDraft, bill, usdBill, receipt, usdPayment, advance);
            await db.SaveChangesAsync();
        }

        // Down: the columns go, the documents stay.
        await using (var db = NewContext()) await db.GetService<IMigrator>().MigrateAsync(Previous);
        (await NewColumnCountAsync()).Should().Be(0);
        (await ScalarAsync("SELECT CAST(ExchangeRate AS decimal(18,2)) FROM finance.sales_invoices WHERE InvoiceNumber = 'SI-USD-R'")).Should().Be(280m);

        // Up: columns back, data filled.
        await using (var db = NewContext()) await UpAsync(db);
        (await NewColumnCountAsync()).Should().Be(Expected);
        await AssertBackfilledAsync();

        // Down + up again, then the history row lost and the migration re-run on the schema it already made.
        await using (var db = NewContext()) await db.GetService<IMigrator>().MigrateAsync(Previous);
        await using (var db = NewContext()) await UpAsync(db);
        await ExecAsync($"DELETE FROM [dbo].[__EFMigrationsHistory] WHERE MigrationId LIKE '%{Name}'");
        await using (var db = NewContext()) await UpAsync(db);
        (await NewColumnCountAsync()).Should().Be(Expected);
        await AssertBackfilledAsync();

        // A rate of ten places survives the round trip.
        await ExecAsync("UPDATE finance.sales_invoices SET ExchangeRate = 0.0035964754 WHERE InvoiceNumber = 'SI-USD-N'");
        await using (var db = NewContext())
            (await db.SalesInvoices.SingleAsync(i => i.InvoiceNumber == "SI-USD-N")).ExchangeRate.Should().Be(0.0035964754m);
    }

    private async Task AssertBackfilledAsync()
    {
        await using var db = NewContext();

        var si = await db.SalesInvoices.AsNoTracking().ToDictionaryAsync(i => i.InvoiceNumber);
        (si["SI-PKR"].CurrencyId, si["SI-PKR"].ExchangeRate, si["SI-PKR"].BaseCurrencyId, si["SI-PKR"].BaseCurrencyCode, si["SI-PKR"].BaseGrandTotal)
            .Should().Be((_pkr, 1m, _pkr, "PKR", 1000m));
        si["SI-PKR"].ExchangeRateLockedAt.Should().Be(new DateTime(2026, 9, 1));
        (si["SI-USD-R"].CurrencyId, si["SI-USD-R"].ExchangeRate, si["SI-USD-R"].BaseCurrencyId).Should().Be((_usd, 280m, _pkr));
        (si["SI-USD-N"].CurrencyId, si["SI-USD-N"].ExchangeRate, si["SI-USD-N"].BaseCurrencyId).Should().Be((_usd, (decimal?)null, (Guid?)null),
            "a foreign document with no rate is left for the startup backfill");
        (si["SI-DRAFT"].ExchangeRate, si["SI-DRAFT"].ExchangeRateLockedAt).Should().Be(((decimal?)null, (DateTime?)null), "a draft is not locked");

        var bills = await db.Invoices.AsNoTracking().ToDictionaryAsync(i => i.InvoiceNumber);
        (bills["INV-1"].CurrencyId, bills["INV-1"].ExchangeRate, bills["INV-1"].BaseCurrencyId, bills["INV-1"].BaseTotalAmount).Should().Be((_pkr, 1m, _pkr, 500m));
        (bills["INV-2"].CurrencyId, bills["INV-2"].ExchangeRate).Should().Be((_usd, (decimal?)null));

        var receipt = await db.CustomerPayments.AsNoTracking().Include(p => p.Allocations).SingleAsync();
        (receipt.CurrencyId, receipt.ExchangeRate, receipt.BaseCurrencyId, receipt.AmountBase, receipt.ExchangeDifference).Should().Be((_pkr, 1m, _pkr, 400m, 0m));
        receipt.Allocations.Single().ExchangeDifference.Should().Be(0m);

        var payments = await db.SupplierPayments.AsNoTracking().Include(p => p.Lines).ToDictionaryAsync(p => p.PaymentNumber);
        (payments["SPAY-1"].CurrencyCode, payments["SPAY-1"].CurrencyId, payments["SPAY-1"].ExchangeRate).Should().Be(("USD", _usd, (decimal?)null),
            "its invoice is in USD");
        payments["SPAY-1"].Lines.Single().ExchangeDifference.Should().BeNull();
        (payments["SPAY-2"].CurrencyCode, payments["SPAY-2"].CurrencyId, payments["SPAY-2"].ExchangeRate, payments["SPAY-2"].AmountBase)
            .Should().Be(("PKR", _pkr, 1m, 70m), "an advance with no invoice is in the organization's base");
    }
}
