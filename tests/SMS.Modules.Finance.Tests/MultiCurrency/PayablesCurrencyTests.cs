using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.SupplierInvoices;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A35 (FIN) on the payable side: a supplier invoice inherits its PO's currency (D-14) and locks its rate at APPROVAL against
/// the <b>purchase</b> base (E-03, D-12; refused when foreign and no rate, D-5); a supplier payment has one currency, locks at
/// POSTED and books a realized difference per line (payable sign: paying more base money than booked is a loss), once —
/// and books it back when the cheque bounces.
/// </summary>
public class PayablesCurrencyTests
{
    private static readonly Guid Pkr = ReceivablesCurrencyTests.Pkr;
    private static readonly Guid Usd = ReceivablesCurrencyTests.Usd;
    private static readonly Guid Eur = ReceivablesCurrencyTests.Eur;
    private static readonly Guid Aed = ReceivablesCurrencyTests.Aed;

    private readonly PurchaseRig _rig = PurchaseRig.New("PKR");
    private readonly FakeCurrencyService _fx = new FakeCurrencyService(Pkr, "PKR").Currency(Usd, "USD").Currency(Eur, "EUR").Currency(Aed, "AED");
    private readonly Mock<IOrganizationCurrencyService> _settings = new();
    private readonly InvoiceService _invoices;
    private readonly SupplierPaymentRepository _payments;

    public PayablesCurrencyTests()
    {
        _settings.Setup(s => s.GetSettingsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                 .ReturnsAsync((Guid org, CancellationToken _) => new OrgCurrencySettingsSnapshot(org, Pkr, Pkr, Pkr, Pkr, "7110", "7120", null, null, true));

        var lookups = ReceivablesCurrencyTests.Lookups().Object;
        var repo = new InvoiceRepository(_rig.Finance, _rig.Demand, _rig.Warehouse, new SupplierLedgerService(_rig.Finance), new FakeSupplierNameLookup(),
            _rig.TaxCodes, _rig.Rates, _rig.BaseCurrency, _rig.BaseCurrency, currency: _fx, lookups: lookups);
        _invoices = new InvoiceService(repo, new Mock<IBackgroundJobClient>().Object);
        _payments = new SupplierPaymentRepository(_rig.Finance, new SupplierLedgerService(_rig.Finance), new Mock<INotificationService>().Object,
            currency: _fx, differences: new ExchangeDifferenceWriter(_rig.Finance, _settings.Object), lookups: lookups);
    }

    private async Task<Guid> ApprovedInvoiceAsync(string currency, decimal qty, decimal price, Guid? poCurrency = null)
    {
        var (po, lines) = await _rig.PoAsync(true, (qty, price));
        if (poCurrency is { } c)
        {
            var stored = await _rig.Demand.PurchaseOrders.SingleAsync(p => p.UUID == po.UUID);
            stored.CurrencyId = c;
            await _rig.Demand.SaveChangesAsync();
            _rig.Forget();
        }
        var uuid = await _invoices.CreateAsync(_rig.Request(po, lines, currency: currency), PurchaseRig.User);
        _rig.Forget();
        (await _invoices.ApproveAsync(uuid, null, PurchaseRig.User)).Should().BeTrue();
        _rig.Forget();
        return uuid;
    }

    private async Task<Guid> PostedPaymentAsync(Guid invoice, decimal amount, DateTime on, string method = "CASH", string? currency = null)
    {
        var inv = await _rig.LoadAsync(invoice);
        var uuid = await _payments.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = inv.SupplierId, SupplierName = inv.SupplierName, PaymentDate = on, PaymentMethod = method, TotalAmount = amount,
            ChequeNo = method == "CHEQUE" ? "CHQ-9" : null, ChequeDate = method == "CHEQUE" ? on : null, CurrencyCode = currency,
            Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = invoice, AllocatedAmount = amount }]
        }, PurchaseRig.User);
        _rig.Forget();
        await _payments.ApproveAsync(uuid, PurchaseRig.User);
        _rig.Forget();
        (await _payments.PostAsync(uuid, PurchaseRig.User)).Should().BeTrue();
        _rig.Forget();
        return uuid;
    }

    private Task<List<ExchangeDifference>> RegisterAsync() =>
        _rig.Finance.ExchangeDifferences.AsNoTracking().OrderBy(d => d.Id).ToListAsync();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // ── Supplier invoice (E-03) ──────────────────────────────────────────────

    [Fact]
    public async Task Approval_locks_against_the_purchase_base_not_the_sale_base()
    {
        _fx.Bases[TransactionDomain.Purchase] = Usd;

        var uuid = await ApprovedInvoiceAsync("USD", 10m, 100m);

        var invoice = await _rig.LoadAsync(uuid);
        (invoice.CurrencyId, invoice.ExchangeRate, invoice.BaseCurrencyId, invoice.BaseCurrencyCode, invoice.BaseTotalAmount)
            .Should().Be((Usd, 1m, Usd, "USD", 1000m));
        invoice.ExchangeRateLockedAt.Should().NotBeNull();
        _fx.Locks.Should().ContainSingle().Which.Domain.Should().Be(TransactionDomain.Purchase);
    }

    [Fact]
    public async Task A_foreign_invoice_locks_the_rate_of_the_approval_date()
    {
        _fx.Rate(Usd, 278.05m);

        var uuid = await ApprovedInvoiceAsync("USD", 100m, 100m);

        var invoice = await _rig.LoadAsync(uuid);
        (invoice.ExchangeRate, invoice.BaseCurrencyId, invoice.BaseTotalAmount).Should().Be((278.05m, Pkr, 2_780_500m));
        _fx.Locks.Single().Date.Should().Be(Today);
    }

    [Fact]
    public async Task A_foreign_invoice_with_no_rate_is_not_approved()
    {
        var (po, lines) = await _rig.PoAsync(true, (10m, 100m));
        var uuid = await _invoices.CreateAsync(_rig.Request(po, lines, currency: "EUR"), PurchaseRig.User);
        _rig.Forget();

        var act = () => _invoices.ApproveAsync(uuid, null, PurchaseRig.User);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().StartWith("No exchange rate for EUR on ");
        _rig.Forget();
        var invoice = await _rig.LoadAsync(uuid);
        (invoice.MatchStatus, invoice.ExchangeRate).Should().NotBe(("Approved", (decimal?)null));
        invoice.MatchStatus.Should().NotBe("Approved");
        (await _rig.LedgerAsync(uuid)).Should().BeEmpty();
    }

    [Fact] // D-14
    public async Task An_invoice_inherits_its_purchase_orders_currency()
    {
        _fx.Rate(Aed, 76.30m);

        var uuid = await ApprovedInvoiceAsync("PKR", 1m, 100m, poCurrency: Aed);

        var invoice = await _rig.LoadAsync(uuid);
        (invoice.Currency, invoice.CurrencyId, invoice.ExchangeRate).Should().Be(("AED", Aed, 76.30m));
    }

    // ── Supplier payment ─────────────────────────────────────────────────────

    [Fact] // T-C8-07 realized counterpart: the currency fell between approval and payment — a gain on a payable
    public async Task Paying_a_payable_after_its_currency_fell_books_a_realized_gain_per_line()
    {
        _fx.Rate(Usd, 278.05m);
        var invoice = await ApprovedInvoiceAsync("USD", 100m, 100m);
        _fx.Rate(Usd, 277.50m, Today.AddDays(1));

        var payment = await PostedPaymentAsync(invoice, 10_000m, DateTime.UtcNow.Date.AddDays(1));

        var stored = await _rig.Finance.SupplierPayments.AsNoTracking().Include(p => p.Lines).SingleAsync(p => p.UUID == payment);
        (stored.CurrencyCode, stored.CurrencyId, stored.ExchangeRate, stored.BaseCurrencyId, stored.AmountBase, stored.ExchangeDifference)
            .Should().Be(("USD", Usd, 277.50m, Pkr, 2_775_000m, 5_500m));
        stored.Lines.Single().ExchangeDifference.Should().Be(5_500m);

        var row = (await RegisterAsync()).Should().ContainSingle().Subject;
        (row.Kind, row.Side, row.DocumentType, row.PaymentType, row.PaymentId, row.AllocationId, row.AccountCode)
            .Should().Be(("REALIZED", "PAYABLE", "SUPPLIER_INVOICE", "SUPPLIER_PAYMENT", (int?)stored.Id, (int?)stored.Lines.Single().Id, "7110"));
        (row.BookedAmountBase, row.SettledAmountBase, row.DifferenceBase).Should().Be((2_780_500m, 2_775_000m, 5_500m));

        var credit = (await _rig.Finance.SupplierLedgerEntries.AsNoTracking().Where(e => e.ReferenceId == payment).SingleAsync()).CreditAmount;
        credit.Should().Be(10_000m, "the supplier ledger stays in the transaction currency");

        // E-06 — the invoice's detail and the payment's detail.
        var detail = (await _invoices.GetByUuidAsync(invoice))!;
        (detail.RealizedExchangeDifference, detail.SupplierPayments.Single().ExchangeDifference, detail.CurrencyId, detail.BaseCurrencyId)
            .Should().Be((5_500m, 5_500m, Usd, Pkr));
        detail.ExchangeRateLockedAt.Should().NotBeNull();
        var paymentDetail = (await _payments.GetByUuidAsync(payment))!;
        (paymentDetail.CurrencyCode, paymentDetail.ExchangeRate, paymentDetail.BaseCurrencyCode, paymentDetail.AmountBase,
         paymentDetail.ExchangeDifference, paymentDetail.Lines.Single().ExchangeDifference)
            .Should().Be(("USD", 277.50m, "PKR", 2_775_000m, 5_500m, 5_500m));
    }

    [Fact]
    public async Task Paying_after_the_currency_rose_is_a_loss()
    {
        _fx.Rate(Usd, 278.05m);
        var invoice = await ApprovedInvoiceAsync("USD", 10m, 100m);
        _fx.Rate(Usd, 280m, Today.AddDays(1));

        await PostedPaymentAsync(invoice, 400m, DateTime.UtcNow.Date.AddDays(1));

        var row = (await RegisterAsync()).Single();
        (row.AmountCurrency, row.DifferenceBase, row.AccountCode).Should().Be((400m, -780m, "7120"), "partial: only the 400 paid settles");
    }

    [Fact] // REV: a payment posted twice (or a retried posting) books its differences once
    public async Task A_payment_posted_twice_books_its_differences_once()
    {
        _fx.Rate(Usd, 278.05m);
        var invoice = await ApprovedInvoiceAsync("USD", 10m, 100m);
        _fx.Rate(Usd, 280m, Today.AddDays(1));
        var payment = await PostedPaymentAsync(invoice, 1_000m, DateTime.UtcNow.Date.AddDays(1));

        var again = () => _payments.PostAsync(payment, PurchaseRig.User);
        await again.Should().ThrowAsync<UnprocessableEntityException>();

        // A retry that finds the payment APPROVED again (a crash between the register and the status, say) still adds nothing.
        var stored = await _rig.Finance.SupplierPayments.SingleAsync(p => p.UUID == payment);
        stored.Status = "APPROVED";
        await _rig.Finance.SaveChangesAsync();
        _rig.Forget();
        await _payments.PostAsync(payment, PurchaseRig.User);

        (await RegisterAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task A_bounced_cheque_books_its_differences_back()
    {
        _fx.Rate(Usd, 278.05m);
        var invoice = await ApprovedInvoiceAsync("USD", 10m, 100m);
        _fx.Rate(Usd, 280m, Today.AddDays(1));
        var payment = await PostedPaymentAsync(invoice, 1_000m, DateTime.UtcNow.Date.AddDays(1), method: "CHEQUE");

        (await _payments.BounceAsync(payment, PurchaseRig.User)).Should().BeTrue();

        var rows = await RegisterAsync();
        rows.Should().HaveCount(2);
        rows.Sum(r => r.DifferenceBase).Should().Be(0m);
        rows[1].AmountCurrency.Should().Be(-1_000m);
    }

    [Fact] // D-14
    public async Task A_payment_in_one_currency_cannot_pay_an_invoice_in_another()
    {
        _fx.Rate(Usd, 278.05m);
        var invoice = await ApprovedInvoiceAsync("USD", 10m, 100m);
        var inv = await _rig.LoadAsync(invoice);

        var act = () => _payments.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = inv.SupplierId, SupplierName = inv.SupplierName, PaymentDate = DateTime.UtcNow.Date, PaymentMethod = "CASH",
            TotalAmount = 100m, CurrencyCode = "PKR", Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = invoice, AllocatedAmount = 100m }]
        }, PurchaseRig.User);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message
            .Should().Be($"Payment is in PKR; invoice {inv.InvoiceNumber} is in USD. Allocate it to invoices in the same currency.");
    }

    [Fact] // REV 1 / D-14 — an advance (no invoice) is in the supplier's default, else the PURCHASE base — never a fixed PKR
    public async Task An_advance_with_no_currency_is_in_the_suppliers_default_else_the_purchase_base()
    {
        _fx.Bases[TransactionDomain.Purchase] = Usd;
        var defaults = new Mock<IPartnerCurrencyDefaults>();
        defaults.Setup(d => d.ResolveDefaultCurrencyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), TransactionDomain.Purchase, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Guid.Empty); // "could not be resolved" (REV 3) — not a currency
        var payments = new SupplierPaymentRepository(_rig.Finance, new SupplierLedgerService(_rig.Finance), new Mock<INotificationService>().Object,
            currency: _fx, partnerDefaults: defaults.Object, lookups: ReceivablesCurrencyTests.Lookups().Object);

        var uuid = await payments.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = _rig.Supplier, SupplierName = "Karachi Steel", PaymentDate = DateTime.UtcNow.Date, PaymentMethod = "CASH",
            TotalAmount = 500m, PaymentType = "ADVANCE_PAYMENT"
        }, PurchaseRig.User);

        (await _rig.Finance.SupplierPayments.AsNoTracking().SingleAsync(p => p.UUID == uuid)).CurrencyCode.Should().Be("USD");

        defaults.Setup(d => d.ResolveDefaultCurrencyAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), TransactionDomain.Purchase, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Eur);
        var second = await payments.CreateAsync(new CreateSupplierPaymentRequest
        {
            SupplierId = _rig.Supplier, SupplierName = "Karachi Steel", PaymentDate = DateTime.UtcNow.Date, PaymentMethod = "CASH",
            TotalAmount = 500m, PaymentType = "ADVANCE_PAYMENT"
        }, PurchaseRig.User);
        (await _rig.Finance.SupplierPayments.AsNoTracking().SingleAsync(p => p.UUID == second)).CurrencyCode.Should().Be("EUR");
    }

    [Fact] // REV 2 — the difference compares against the invoice's OWN booked base and rate, never the organization's current base
    public async Task An_invoice_booked_in_another_base_than_the_payments_gets_no_difference()
    {
        _fx.Rate(Usd, 278.05m);
        var invoice = await ApprovedInvoiceAsync("USD", 10m, 100m);   // booked against PKR at 278.05
        _fx.Bases[TransactionDomain.Purchase] = Usd;                   // the purchase base is now USD

        var payment = await PostedPaymentAsync(invoice, 1_000m, DateTime.UtcNow.Date);

        (await RegisterAsync()).Should().BeEmpty("USD paid in a USD base vs an invoice booked in PKR: no like-for-like difference");
        (await _rig.LoadAsync(invoice)).Should().Match<Invoice>(i => i.ExchangeRate == 278.05m && i.BaseCurrencyId == Pkr, "the invoice's lock is untouched");
        (await _rig.Finance.SupplierPayments.AsNoTracking().SingleAsync(p => p.UUID == payment)).BaseCurrencyId.Should().Be(Usd);
    }

    [Fact]
    public async Task Without_a_currency_the_payment_takes_its_first_invoices_and_the_base_pays_without_a_rate()
    {
        var invoice = await ApprovedInvoiceAsync("PKR", 10m, 100m);

        var payment = await PostedPaymentAsync(invoice, 1_000m, DateTime.UtcNow.Date);

        var stored = await _rig.Finance.SupplierPayments.AsNoTracking().Include(p => p.Lines).SingleAsync(p => p.UUID == payment);
        (stored.CurrencyCode, stored.ExchangeRate, stored.AmountBase, stored.ExchangeDifference).Should().Be(("PKR", 1m, 1_000m, 0m));
        stored.Lines.Single().ExchangeDifference.Should().Be(0m);
        (await RegisterAsync()).Should().BeEmpty();
    }
}
