using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A35 D-8 — Finance's answer to "is this currency / this domain's base used by locked documents?": issued sales invoices
/// (Sale) and approved supplier invoices (Purchase). Drafts and unapproved invoices are not locked; another organization's
/// documents never count, whoever asks.
/// </summary>
public class FinanceDocumentCurrencyUsageCheckerTests
{
    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Usd = Guid.NewGuid();

    private sealed class Codes : ICurrencyCodeLookup
    {
        public Task<string?> GetCodeAsync(Guid currencyId, CancellationToken ct = default) =>
            Task.FromResult<string?>(currencyId == Pkr ? "PKR" : currencyId == Usd ? "USD" : null);
    }

    private readonly Guid _org   = Guid.NewGuid();
    private readonly Guid _other = Guid.NewGuid();
    private readonly string _db  = Guid.NewGuid().ToString();

    private FinanceDbContext Db(Guid org, bool superAdmin = false) => new(
        new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(_db).Options,
        new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin });

    private FinanceDocumentCurrencyUsageChecker Checker(bool superAdmin = false) =>
        new(Db(_org, superAdmin), new Codes());

    private async Task SalesInvoiceAsync(Guid org, string status, string currency, string? baseCode = "PKR")
    {
        await using var db = Db(org);
        db.SalesInvoices.Add(new SalesInvoice
        {
            UUID = Guid.NewGuid(), OrganizationId = org, InvoiceNumber = $"SINV-{Guid.NewGuid():N}"[..20], SaleOrderNumber = "SO-1",
            PartnerName = "C", Status = status, CurrencyCode = currency, BaseCurrencyCode = status == SalesInvoiceStatuses.Draft ? null : baseCode,
            InvoiceDate = DateTime.UtcNow, DueDate = DateTime.UtcNow, CreatedDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task SupplierInvoiceAsync(Guid org, string matchStatus, string currency, string? baseCode = "PKR")
    {
        await using var db = Db(org);
        db.Invoices.Add(new Invoice
        {
            UUID = Guid.NewGuid(), OrganizationId = org, InvoiceNumber = $"INV-{Guid.NewGuid():N}"[..20], SupplierName = "S",
            MatchStatus = matchStatus, Currency = currency, BaseCurrencyCode = matchStatus == "Approved" ? baseCode : null,
            InvoiceDate = DateTime.UtcNow, ReceivedDate = DateTime.UtcNow, DueDate = DateTime.UtcNow, CreatedDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Nothing_locked_is_no_usage()
    {
        await SalesInvoiceAsync(_org, SalesInvoiceStatuses.Draft, "USD");
        await SupplierInvoiceAsync(_org, "Pending", "USD");

        var checker = Checker();
        (await checker.DescribeCurrencyUsageAsync(_org, Usd)).Should().BeNull();
        (await checker.DescribeDomainBaseUsageAsync(_org, TransactionDomain.Sale)).Should().BeNull();
        (await checker.DescribeDomainBaseUsageAsync(_org, TransactionDomain.Purchase)).Should().BeNull();
    }

    [Fact]
    public async Task Issued_sales_invoices_lock_the_sale_base_and_their_currencies()
    {
        await SalesInvoiceAsync(_org, SalesInvoiceStatuses.Issued, "USD");
        await SalesInvoiceAsync(_org, SalesInvoiceStatuses.Paid, "PKR");

        var checker = Checker();
        (await checker.DescribeDomainBaseUsageAsync(_org, TransactionDomain.Sale)).Should().Be("2 issued sales invoices");
        (await checker.DescribeDomainBaseUsageAsync(_org, TransactionDomain.Purchase)).Should().BeNull();
        (await checker.DescribeCurrencyUsageAsync(_org, Usd)).Should().Contain("1 issued sales invoice");
        (await checker.DescribeCurrencyUsageAsync(_org, Pkr)).Should().Contain("2 issued sales invoices", "PKR is the base of both");
    }

    [Fact]
    public async Task Approved_supplier_invoices_lock_the_purchase_base()
    {
        await SupplierInvoiceAsync(_org, "Approved", "USD", baseCode: "USD");

        var checker = Checker();
        (await checker.DescribeDomainBaseUsageAsync(_org, TransactionDomain.Purchase)).Should().Be("1 approved supplier invoice");
        (await checker.DescribeDomainBaseUsageAsync(_org, TransactionDomain.Sale)).Should().BeNull();
        (await checker.DescribeCurrencyUsageAsync(_org, Usd)).Should().Be("1 approved supplier invoice");
        (await checker.DescribeCurrencyUsageAsync(_org, Pkr)).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_documents_never_count_even_for_a_super_admin()
    {
        await SalesInvoiceAsync(_other, SalesInvoiceStatuses.Issued, "USD");
        await SupplierInvoiceAsync(_other, "Approved", "USD");

        var checker = Checker(superAdmin: true);
        (await checker.DescribeCurrencyUsageAsync(_org, Usd)).Should().BeNull();
        (await checker.DescribeDomainBaseUsageAsync(_org, TransactionDomain.Sale)).Should().BeNull();
        (await checker.DescribeDomainBaseUsageAsync(_org, TransactionDomain.Purchase)).Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_currency_id_is_not_used()
    {
        await SalesInvoiceAsync(_org, SalesInvoiceStatuses.Issued, "USD");

        (await Checker().DescribeCurrencyUsageAsync(_org, Guid.NewGuid())).Should().BeNull();
    }
}
