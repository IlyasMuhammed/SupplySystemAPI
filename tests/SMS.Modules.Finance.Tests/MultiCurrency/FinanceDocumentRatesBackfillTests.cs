using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using Xunit;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>A35 D-11 — locked foreign documents with no rate get the rate of their own lock date, once rates exist; idempotent; drafts and other organizations untouched.</summary>
public class FinanceDocumentRatesBackfillTests
{
    private static readonly Guid Pkr = ReceivablesCurrencyTests.Pkr;
    private static readonly Guid Usd = ReceivablesCurrencyTests.Usd;
    private static readonly Guid Eur = ReceivablesCurrencyTests.Eur;

    [Fact]
    public async Task Foreign_locked_documents_get_the_rate_of_their_lock_date_and_the_rest_is_left_alone()
    {
        var dbName = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();
        var other = Guid.NewGuid();
        var fx = new FakeCurrencyService(Pkr, "PKR").Currency(Usd, "USD").Currency(Eur, "EUR")
            .Rate(Usd, 270m, new DateOnly(2026, 1, 1)).Rate(Usd, 280m, new DateOnly(2026, 9, 1));

        var issued = Receivables.Invoice(org, Guid.NewGuid(), "SI-1", new DateTime(2026, 8, 15), 100m, currency: "USD");
        var noRate = Receivables.Invoice(org, Guid.NewGuid(), "SI-2", new DateTime(2026, 8, 15), 100m, currency: "EUR");
        var draft  = Receivables.Invoice(org, Guid.NewGuid(), "SI-3", new DateTime(2026, 8, 15), 100m, status: "DRAFT", currency: "USD");
        var foreignOrg = Receivables.Invoice(other, Guid.NewGuid(), "SI-4", new DateTime(2026, 8, 15), 100m, currency: "USD");
        var receipt = new CustomerPayment
        {
            UUID = Guid.NewGuid(), OrganizationId = org, PartnerId = Guid.NewGuid(), PartnerName = "C", PaymentNumber = "CPAY-1",
            PaymentDate = new DateTime(2026, 9, 10), Amount = 50m, PaymentMethod = "CASH", CurrencyCode = "USD", CreatedDate = DateTime.UtcNow
        };
        await using (var db = Receivables.Auditor(dbName)) await Receivables.Seed(db, issued, noRate, draft, foreignOrg, receipt);

        await using (var db = Receivables.Db(org, dbName))
        {
            var backfill = new FinanceDocumentRatesBackfill(db, fx, ReceivablesCurrencyTests.Lookups().Object);
            await backfill.OnCurrencyRatesReadyAsync(org);
            await backfill.OnCurrencyRatesReadyAsync(org); // idempotent
        }

        await using var read = Receivables.Auditor(dbName);
        var si = await read.SalesInvoices.AsNoTracking().ToDictionaryAsync(i => i.InvoiceNumber);
        (si["SI-1"].ExchangeRate, si["SI-1"].BaseCurrencyId, si["SI-1"].BaseGrandTotal, si["SI-1"].CurrencyId).Should().Be((270m, Pkr, 27_000m, Usd));
        si["SI-1"].ExchangeRateLockedAt.Should().Be(new DateTime(2026, 8, 15));
        si["SI-2"].ExchangeRate.Should().BeNull("no EUR rate — left, not guessed");
        si["SI-3"].ExchangeRate.Should().BeNull("a draft is not locked");
        si["SI-4"].ExchangeRate.Should().BeNull("another organization's");
        var p = await read.CustomerPayments.AsNoTracking().SingleAsync();
        (p.ExchangeRate, p.AmountBase, p.ExchangeDifference).Should().Be((280m, 14_000m, (decimal?)null));
        fx.Locks.Count(l => l.Currency == Usd).Should().Be(2, "the second run touches only what still has no rate (the EUR one)");
    }
}
