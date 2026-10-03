using FluentAssertions;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using Xunit;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>
/// What Finance tells Lookups before a currency is deleted or its code changed: whether any organization's
/// Finance rows carry the currency's code.
/// </summary>
public class FinanceCurrencyReferenceCheckerTests
{
    private readonly SetupWorld _world = new();
    private readonly SetupDesk  _acme;
    private readonly SetupDesk  _globex;

    public FinanceCurrencyReferenceCheckerTests()
    {
        _acme   = _world.For(Guid.NewGuid());
        _globex = _world.For(Guid.NewGuid());
    }

    private bool IsReferenced(Guid currencyId)
    {
        // Lookups runs as whoever deletes the currency — an ordinary organization's admin here.
        using var db = _acme.Finance();
        return new FinanceCurrencyReferenceChecker(db, _world.Lookups().Object).IsValueReferenced(currencyId);
    }

    [Fact]
    public void Nothing_in_finance_means_not_referenced_and_an_id_that_is_not_a_currency_never_is()
    {
        IsReferenced(SetupWorld.UsdId).Should().BeFalse();
        IsReferenced(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public async Task A_supplier_invoice_in_another_organization_counts_whatever_the_case_of_its_code()
    {
        await _globex.Seed(new Invoice
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = "INV-1", SupplierId = Guid.NewGuid(),
            SupplierName = "Karachi Steel", Currency = "usd", CreatedBy = 1, CreatedDate = DateTime.UtcNow
        });

        IsReferenced(SetupWorld.UsdId).Should().BeTrue();
        IsReferenced(SetupWorld.PkrId).Should().BeFalse();
    }

    [Fact]
    public async Task A_base_currency_snapshot_on_a_supplier_invoice_counts()
    {
        await _acme.Seed(new Invoice
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = "INV-2", SupplierId = Guid.NewGuid(),
            SupplierName = "Karachi Steel", Currency = "USD", BaseCurrencyCode = "PKR", CreatedBy = 1, CreatedDate = DateTime.UtcNow
        });

        IsReferenced(SetupWorld.PkrId).Should().BeTrue();
    }

    [Fact]
    public async Task Sales_invoices_their_base_currency_customer_payments_and_ledger_entries_count()
    {
        var invoice = Receivables.Invoice(_acme.Org, Guid.NewGuid(), "SINV-1", new DateTime(2026, 9, 1), 100m, currency: "EUR");
        invoice.BaseCurrencyCode = "AED";
        await _acme.Seed(invoice);

        IsReferenced(SetupWorld.EurId).Should().BeTrue();
        IsReferenced(SetupWorld.AedId).Should().BeTrue("the catalog stores it as 'aed'; the snapshot as 'AED'");
        IsReferenced(SetupWorld.UsdId).Should().BeFalse();

        await _globex.Seed(new CustomerPayment
        {
            UUID = Guid.NewGuid(), PartnerId = Guid.NewGuid(), PartnerName = "Acme", PaymentNumber = "CPAY-1",
            PaymentDate = new DateTime(2026, 9, 1), Amount = 10m, PaymentMethod = "CASH", CurrencyCode = "USD",
            Status = "RECEIVED", CreatedBy = 1, CreatedDate = DateTime.UtcNow
        });
        IsReferenced(SetupWorld.UsdId).Should().BeTrue();
    }

    [Fact]
    public async Task A_customer_ledger_entry_counts()
    {
        await _acme.Seed(new CustomerLedgerEntry
        {
            UUID = Guid.NewGuid(), PartnerId = Guid.NewGuid(), SequenceNo = 1, EntryDate = DateTime.UtcNow, EntryType = "INVOICE",
            ReferenceType = "SalesInvoice", ReferenceId = Guid.NewGuid(), ReferenceNumber = "SINV-1",
            DebitAmount = 10m, CreditAmount = 0m, RunningBalance = 10m, CurrencyCode = "EUR", CreatedBy = 1, CreatedDate = DateTime.UtcNow
        });

        IsReferenced(SetupWorld.EurId).Should().BeTrue();
    }

    [Fact]
    public async Task A_live_exchange_rate_counts_on_either_side_and_a_deleted_one_does_not()
    {
        var rate = await _globex.CreateRate("USD", "EUR", 0.92m, "2026-10-01");

        IsReferenced(SetupWorld.UsdId).Should().BeTrue();
        IsReferenced(SetupWorld.EurId).Should().BeTrue();

        await _globex.Rates(s => s.DeleteAsync(rate.Uuid, SetupWorld.User));

        IsReferenced(SetupWorld.UsdId).Should().BeFalse();
        IsReferenced(SetupWorld.EurId).Should().BeFalse();
    }
}
