using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Services;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A35 (FIN) on the receivable side: the sales invoice locks its rate at ISSUE against the sale base (D-12) and a foreign
/// invoice with no rate is refused (D-5); a customer payment locks its rate when recorded, pays only invoices in its own
/// currency (D-14), and books a realized exchange difference per allocation (BR-C7-01..03, T-C8-01..05) — reversed when the
/// cheque bounces. Real services over one in-memory database, ICurrencyService faked.
/// </summary>
public class ReceivablesCurrencyTests
{
    internal static readonly Guid Pkr = Guid.NewGuid();
    internal static readonly Guid Aed = Guid.NewGuid();
    internal static readonly Guid Eur = Guid.NewGuid();
    internal static readonly Guid Usd = Guid.NewGuid();

    private const int User = 42;

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _customer = Guid.NewGuid();
    private readonly TestClock _clock = new();
    private readonly FakeCurrencyService _fx = new FakeCurrencyService(Pkr, "PKR").Currency(Aed, "AED").Currency(Eur, "EUR").Currency(Usd, "USD");
    private readonly Mock<IOrganizationCurrencyService> _settings = new();

    public ReceivablesCurrencyTests()
    {
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((Guid org, CancellationToken _) => new OrgCurrencySettingsSnapshot(org, Pkr, Pkr, Pkr, Pkr, "7110", "7120", "7130", "7140", true));
    }

    internal static Mock<ILookupsService> Lookups()
    {
        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns(
        [
            new CurrencyModel { Id = Pkr, Name = "Rupee", Code = "PKR" },
            new CurrencyModel { Id = Aed, Name = "Dirham", Code = "AED" },
            new CurrencyModel { Id = Eur, Name = "Euro", Code = "EUR" },
            new CurrencyModel { Id = Usd, Name = "Dollar", Code = "USD" }
        ]);
        return lookups;
    }

    private FinanceDbContext Db() => Receivables.Db(_org, _dbName);

    private SalesInvoiceService Invoices(FinanceDbContext db) => new(
        db, new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(_dbName).Options, new StaticTenantContext { OrganizationId = _org }),
        new Mock<IDeliveryFulfillmentReader>().Object, new CustomerLedgerService(db, _clock), new ProductLedgerService(db, new FakeVariants(), _clock),
        Receivables.Names().Object, Lookups().Object, new Mock<IBackgroundJobClient>().Object, NullLogger<SalesInvoiceService>.Instance, _clock,
        currency: _fx);

    private CustomerPaymentService Payments(FinanceDbContext db) => new(
        db, new CustomerLedgerService(db, _clock), Receivables.Names().Object, Lookups().Object, _clock,
        currency: _fx, differences: new ExchangeDifferenceWriter(db, _settings.Object, _clock));

    /// <summary>A DRAFT invoice in <paramref name="code"/>, issued on <paramref name="issuedOn"/>.</summary>
    private async Task<SalesInvoice> IssuedAsync(string code, Guid currencyId, decimal grand, DateTime issuedOn)
    {
        var draft = Receivables.Invoice(_org, _customer, $"SINV-{Guid.NewGuid():N}"[..18], issuedOn, grand, status: "DRAFT", currency: code);
        draft.CurrencyId = currencyId;
        await using (var db = Db()) await Receivables.Seed(db, draft);

        _clock.Value = issuedOn;
        await using (var db = Db()) await Invoices(db).IssueAsync(draft.UUID, User);

        await using var read = Db();
        return await read.SalesInvoices.AsNoTracking().SingleAsync(i => i.UUID == draft.UUID);
    }

    private async Task<CustomerPaymentRecorded> PayAsync(string? code, decimal amount, DateTime on, params (Guid Invoice, decimal Amount)[] allocations)
    {
        _clock.Value = on;
        await using var db = Db();
        return await Payments(db).RecordPaymentAsync(_customer, amount, "CHEQUE",
            new CustomerPaymentDetails(code, on, ChequeNumber: "CHQ-1",
                Allocations: allocations.Length == 0 ? null : [.. allocations.Select(a => new ManualPaymentAllocation(a.Invoice, a.Amount))]),
            User);
    }

    private async Task<List<ExchangeDifference>> RegisterAsync()
    {
        await using var db = Receivables.Auditor(_dbName);
        return await db.ExchangeDifferences.AsNoTracking().OrderBy(d => d.Id).ToListAsync();
    }

    private static readonly DateTime Day1 = new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Day8 = new(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);

    // ── Sales invoice: lock at issue ─────────────────────────────────────────

    [Fact]
    public async Task An_invoice_in_the_sale_base_locks_at_rate_one_without_needing_a_rate()
    {
        var invoice = await IssuedAsync("PKR", Pkr, 1234.56m, Day1);

        (invoice.ExchangeRate, invoice.BaseCurrencyId, invoice.BaseCurrencyCode, invoice.BaseGrandTotal, invoice.CurrencyId)
            .Should().Be((1m, Pkr, "PKR", 1234.56m, Pkr));
        invoice.ExchangeRateLockedAt.Should().Be(Day1);
    }

    [Fact]
    public async Task A_foreign_invoice_locks_the_rate_of_the_issue_date_against_the_sale_base()
    {
        _fx.Rate(Aed, 76.10m, new DateOnly(2026, 9, 1)).Rate(Aed, 76.30m, new DateOnly(2026, 10, 1)).Rate(Aed, 76.45m, new DateOnly(2026, 10, 8));

        var invoice = await IssuedAsync("AED", Aed, 10_550m, Day1);

        (invoice.ExchangeRate, invoice.BaseCurrencyId, invoice.BaseGrandTotal).Should().Be((76.30m, Pkr, 804_965.00m));
        _fx.Locks.Should().ContainSingle().Which.Should().Be((_org, Aed, new DateOnly(2026, 10, 1), TransactionDomain.Sale));
    }

    [Fact]
    public async Task A_foreign_invoice_with_no_rate_is_refused_and_stays_a_draft()
    {
        var draft = Receivables.Invoice(_org, _customer, "SINV-NORATE", Day1, 100m, status: "DRAFT", currency: "EUR");
        draft.CurrencyId = Eur;
        await using (var db = Db()) await Receivables.Seed(db, draft);
        _clock.Value = Day1;

        await using (var db = Db())
        {
            var act = () => Invoices(db).IssueAsync(draft.UUID, User);
            (await act.Should().ThrowAsync<BadRequestException>())
                .Which.Message.Should().Be("No exchange rate for EUR on 2026-10-01. Add one under Settings → Exchange Rates.");
        }

        await using var read = Db();
        var after = await read.SalesInvoices.AsNoTracking().SingleAsync(i => i.UUID == draft.UUID);
        (after.Status, after.ExchangeRate).Should().Be(("DRAFT", (decimal?)null));
        (await read.CustomerLedgerEntries.CountAsync()).Should().Be(0, "nothing was booked");
    }

    // ── Customer payment: lock + realized difference ─────────────────────────

    [Fact] // T-C8-01
    public async Task A_payment_at_a_higher_rate_books_a_realized_gain_on_the_allocation()
    {
        _fx.Rate(Aed, 76.30m, new DateOnly(2026, 10, 1)).Rate(Aed, 76.45m, new DateOnly(2026, 10, 8));
        var invoice = await IssuedAsync("AED", Aed, 10_550m, Day1);

        var paid = await PayAsync("AED", 10_550m, Day8, (invoice.UUID, 10_550m));

        await using var db = Db();
        var payment = await db.CustomerPayments.AsNoTracking().Include(p => p.Allocations).SingleAsync(p => p.UUID == paid.PaymentUuid);
        (payment.CurrencyId, payment.ExchangeRate, payment.BaseCurrencyId, payment.AmountBase, payment.ExchangeDifference)
            .Should().Be((Aed, 76.45m, Pkr, 806_547.50m, 1_582.50m));
        payment.Allocations.Single().ExchangeDifference.Should().Be(1_582.50m);

        var row = (await RegisterAsync()).Should().ContainSingle().Subject;
        (row.Kind, row.Side, row.DocumentType, row.DocumentUuid, row.PaymentType, row.PaymentUuid).Should().Be(
            ("REALIZED", "RECEIVABLE", "SALES_INVOICE", invoice.UUID, "CUSTOMER_PAYMENT", paid.PaymentUuid));
        (row.AmountCurrency, row.BookedRate, row.SettlementRate, row.BookedAmountBase, row.SettledAmountBase, row.DifferenceBase, row.AccountCode)
            .Should().Be((10_550m, 76.30m, 76.45m, 804_965.00m, 806_547.50m, 1_582.50m, "7110"));
        (row.PaymentId, row.AllocationId, row.DocumentId).Should().Be((payment.Id, payment.Allocations.Single().Id, invoice.Id));
        (row.OrganizationId, row.CurrencyCode, row.BaseCurrencyCode, row.PartnerId).Should().Be((_org, "AED", "PKR", _customer));

        (await db.CustomerLedgerEntries.AsNoTracking().Where(e => e.ReferenceId == paid.PaymentUuid).Select(e => e.CreditAmount).SingleAsync())
            .Should().Be(10_550m, "the customer's ledger stays in the transaction currency — no FX in it");

        // E-06 — the invoice's detail shows the allocation's difference and the net booked on it.
        var detail = (await Invoices(db).GetAsync(invoice.UUID))!;
        (detail.RealizedExchangeDifference, detail.Payments.Single().ExchangeDifference, detail.BaseCurrencyId, detail.CurrencyId)
            .Should().Be((1_582.50m, 1_582.50m, Pkr, Aed));
        detail.ExchangeRateLockedAt.Should().Be(Day1);
        var paymentDetail = (await Payments(db).GetAsync(paid.PaymentUuid))!;
        (paymentDetail.ExchangeRate, paymentDetail.BaseCurrencyCode, paymentDetail.AmountBase, paymentDetail.ExchangeDifference,
         paymentDetail.Allocations.Single().ExchangeDifference).Should().Be((76.45m, "PKR", 806_547.50m, 1_582.50m, 1_582.50m));
    }

    [Fact] // T-C8-02
    public async Task A_payment_at_a_lower_rate_books_a_loss_to_the_loss_account()
    {
        _fx.Rate(Eur, 316.48m, new DateOnly(2026, 10, 1)).Rate(Eur, 315.90m, new DateOnly(2026, 10, 8));
        var invoice = await IssuedAsync("EUR", Eur, 5_000m, Day1);

        await PayAsync("EUR", 5_000m, Day8, (invoice.UUID, 5_000m));

        var row = (await RegisterAsync()).Single();
        (row.DifferenceBase, row.AccountCode).Should().Be((-2_900m, "7120"));
    }

    [Fact] // T-C8-03
    public async Task The_same_rate_books_no_difference()
    {
        _fx.Rate(Aed, 76.30m, new DateOnly(2026, 10, 1));
        var invoice = await IssuedAsync("AED", Aed, 10_550m, Day1);

        var paid = await PayAsync("AED", 10_550m, Day8, (invoice.UUID, 10_550m));

        (await RegisterAsync()).Should().BeEmpty();
        await using var db = Db();
        (await db.CustomerPayments.AsNoTracking().SingleAsync(p => p.UUID == paid.PaymentUuid)).ExchangeDifference.Should().Be(0m);
    }

    [Fact] // T-C8-04
    public async Task A_payment_in_the_sale_base_never_looks_up_a_rate_or_books_a_difference()
    {
        var invoice = await IssuedAsync("PKR", Pkr, 50_000m, Day1);

        var paid = await PayAsync("PKR", 50_000m, Day8, (invoice.UUID, 50_000m));

        (await RegisterAsync()).Should().BeEmpty();
        await using var db = Db();
        var payment = await db.CustomerPayments.AsNoTracking().Include(p => p.Allocations).SingleAsync(p => p.UUID == paid.PaymentUuid);
        (payment.ExchangeRate, payment.AmountBase, payment.ExchangeDifference).Should().Be((1m, 50_000m, 0m));
        payment.Allocations.Single().ExchangeDifference.Should().Be(0m);
    }

    [Fact] // T-C8-05
    public async Task A_partial_payment_books_the_difference_on_the_part_paid_and_leaves_the_rest_at_the_invoice_rate()
    {
        _fx.Rate(Aed, 76.30m, new DateOnly(2026, 10, 1)).Rate(Aed, 76.50m, new DateOnly(2026, 10, 8));
        var invoice = await IssuedAsync("AED", Aed, 10_000m, Day1);

        await PayAsync("AED", 4_000m, Day8, (invoice.UUID, 4_000m));

        (await RegisterAsync()).Single().DifferenceBase.Should().Be(800m);
        await using var db = Db();
        var after = await db.SalesInvoices.AsNoTracking().SingleAsync(i => i.UUID == invoice.UUID);
        (after.BalanceDue, after.ExchangeRate, after.BaseGrandTotal).Should().Be((6_000m, 76.30m, 763_000m), "the invoice's own lock never changes");
    }

    [Fact]
    public async Task Money_applied_later_settles_at_the_rate_the_payment_was_locked_at()
    {
        _fx.Rate(Aed, 76.30m, new DateOnly(2026, 10, 1)).Rate(Aed, 76.45m, new DateOnly(2026, 10, 8));
        var invoice = await IssuedAsync("AED", Aed, 1_000m, Day1);

        // Received on the 8th and held on account (an empty allocation list), applied on the 10th after the rate moved to 80.
        _clock.Value = Day8;
        Guid advance;
        await using (var db = Db())
            advance = (await Payments(db).RecordPaymentAsync(_customer, 1_000m, "CASH", new CustomerPaymentDetails("AED", Day8, Allocations: []), User)).PaymentUuid;
        (await RegisterAsync()).Should().BeEmpty("nothing was applied yet");

        _fx.Rate(Aed, 80m, new DateOnly(2026, 10, 9));
        _clock.Value = Day8.AddDays(2);
        await using (var db = Db()) await Payments(db).AllocateAsync(advance, null, User);

        var row = (await RegisterAsync()).Single();
        (row.SettlementRate, row.DifferenceBase).Should().Be((76.45m, 150m), "the payment's own locked rate, not the rate of the day it was applied");
        row.DocumentUuid.Should().Be(invoice.UUID);
    }

    [Fact] // D-14
    public async Task A_payment_cannot_be_applied_to_an_invoice_in_another_currency()
    {
        _fx.Rate(Aed, 76.30m).Rate(Usd, 278m);
        var invoice = await IssuedAsync("AED", Aed, 100m, Day1);

        var act = () => PayAsync("USD", 100m, Day8, (invoice.UUID, 100m));

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message
            .Should().Be($"Payment is in USD; invoice {invoice.InvoiceNumber} is in AED. Allocate it to invoices in the same currency.");
    }

    [Fact] // D-5
    public async Task A_foreign_payment_with_no_rate_on_its_date_is_refused_and_nothing_is_written()
    {
        _fx.Rate(Eur, 316m, new DateOnly(2026, 11, 1));

        var act = () => PayAsync("EUR", 100m, Day8);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("No exchange rate for EUR on 2026-10-08");
        await using var db = Db();
        (await db.CustomerPayments.CountAsync()).Should().Be(0);
    }

    [Fact] // D-14
    public async Task Without_a_currency_the_payment_takes_its_first_invoices()
    {
        _fx.Rate(Aed, 76.30m);
        var invoice = await IssuedAsync("AED", Aed, 100m, Day1);

        var paid = await PayAsync(null, 100m, Day8, (invoice.UUID, 100m));

        paid.CurrencyCode.Should().Be("AED");
    }

    [Fact]
    public async Task A_bounced_cheque_books_its_differences_back_and_a_second_bounce_adds_nothing()
    {
        _fx.Rate(Aed, 76.30m, new DateOnly(2026, 10, 1)).Rate(Aed, 76.45m, new DateOnly(2026, 10, 8));
        var invoice = await IssuedAsync("AED", Aed, 10_550m, Day1);
        var paid = await PayAsync("AED", 10_550m, Day8, (invoice.UUID, 10_550m));

        await using (var db = Db()) await Payments(db).BounceAsync(paid.PaymentUuid, "returned", User);
        await using (var db = Db())
        {
            var again = () => Payments(db).BounceAsync(paid.PaymentUuid, "again", User);
            await again.Should().ThrowAsync<ConflictException>();
        }

        var rows = await RegisterAsync();
        rows.Should().HaveCount(2);
        rows.Sum(r => r.DifferenceBase).Should().Be(0m);
        rows[1].DifferenceBase.Should().Be(-1_582.50m);
        (rows[1].AmountCurrency, rows[1].PaymentUuid, rows[1].DocumentUuid).Should().Be((-10_550m, paid.PaymentUuid, invoice.UUID));
    }
}
