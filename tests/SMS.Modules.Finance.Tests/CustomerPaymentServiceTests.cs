using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-05 §9.3–§9.5 — recording a customer payment: FIFO auto-allocation with a manual override,
/// the invoice balances and statuses that follow, and the ledger CREDIT written in the same
/// transaction as all of it.
/// </summary>
public class CustomerPaymentServiceTests
{
    private const int User = 42;
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateTime Today = new(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

    private sealed class FakeClock : TimeProvider
    {
        public DateTime Now { get; set; } = Today;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    /// <summary>Records what each save carried, to prove the payment, the invoices and the ledger went together.</summary>
    private sealed class SavedTogether : SaveChangesInterceptor
    {
        public List<(int Payments, int Allocations, int LedgerEntries, int ModifiedInvoices)> Saves { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToList();
            Saves.Add((
                entries.Count(e => e.Entity is CustomerPayment && e.State == EntityState.Added),
                entries.Count(e => e.Entity is PaymentAllocation && e.State == EntityState.Added),
                entries.Count(e => e.Entity is CustomerLedgerEntry && e.State == EntityState.Added),
                entries.Count(e => e.Entity is SalesInvoice && e.State == EntityState.Modified)));
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    private sealed class Harness
    {
        public required FinanceDbContext Db;
        public required CustomerPaymentService Service;
        public required Mock<ISupplierNameLookupService> Names;
        public required FakeClock Clock;
        public required Guid OrgId;
        public required string DbName;
    }

    private static Harness NewHarness(Guid? orgId = null, string? dbName = null, IInterceptor? interceptor = null, FakeClock? clock = null)
    {
        orgId  ??= Guid.NewGuid();
        dbName ??= Guid.NewGuid().ToString();
        clock  ??= new FakeClock();

        var options = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var db = new FinanceDbContext(options.Options, new StaticTenantContext { OrganizationId = orgId.Value });

        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.ToDictionary(id => id, _ => "Acme Ltd"));

        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns(
        [
            new CurrencyModel { Id = Guid.NewGuid(), Name = "Pakistani Rupee", Code = " PKR " },
            new CurrencyModel { Id = Guid.NewGuid(), Name = "US Dollar", Code = "USD" },
            new CurrencyModel { Id = Guid.NewGuid(), Name = "Unnamed", Code = null }
        ]);

        return new Harness
        {
            Db = db, Names = names, Clock = clock, OrgId = orgId.Value, DbName = dbName,
            Service = new CustomerPaymentService(db, new CustomerLedgerService(db), names.Object, lookups.Object, clock)
        };
    }

    // ── Seeding and reading ──────────────────────────────────────────────────

    private static SalesInvoice Invoice(
        string number, DateTime date, decimal grand, string status = "ISSUED", decimal paid = 0m,
        string currency = "PKR", bool deleted = false, Guid? partner = null) =>
        new()
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = number,
            SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = "SO-2026-00042",
            PartnerId = partner ?? Customer, PartnerName = "Acme Ltd",
            InvoiceDate = date, DueDate = date.AddDays(30),
            Subtotal = grand, GrandTotal = grand, AmountPaid = paid, BalanceDue = grand - paid,
            Status = status, CurrencyCode = currency, IsDelete = deleted,
            CreatedBy = 1, CreatedDate = date
        };

    private static async Task Seed(Harness h, params SalesInvoice[] invoices)
    {
        h.Db.SalesInvoices.AddRange(invoices);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    /// <summary>What the customer already owes: one INVOICE debit on their ledger.</summary>
    private static async Task OweAlready(Harness h, decimal amount, Guid? partner = null)
    {
        await new CustomerLedgerService(h.Db).TrackEntryAsync(new CustomerLedgerPosting(
            partner ?? Customer, "INVOICE", "SalesInvoice", Guid.NewGuid(), "SINV-SEED", amount, 0m, "PKR", null, Today, 1));
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    /// <summary>The usual three: 1,000 (1 Sep), 2,000 (5 Sep), 3,000 (10 Sep) — inserted newest first so id order is not date order.</summary>
    private static async Task<(SalesInvoice A, SalesInvoice B, SalesInvoice C)> SeedThree(Harness h)
    {
        var c = Invoice("SINV-20260910-0001", new DateTime(2026, 9, 10), 3000m);
        var a = Invoice("SINV-20260901-0001", new DateTime(2026, 9, 1), 1000m);
        var b = Invoice("SINV-20260905-0001", new DateTime(2026, 9, 5), 2000m);
        await Seed(h, c, a, b);
        return (a, b, c);
    }

    private static Task<SalesInvoice> Inv(Harness h, string number) =>
        h.Db.SalesInvoices.AsNoTracking().SingleAsync(i => i.InvoiceNumber == number);

    private static Task<List<CustomerLedgerEntry>> Ledger(Harness h) =>
        h.Db.CustomerLedgerEntries.AsNoTracking().OrderBy(e => e.PartnerId).ThenBy(e => e.SequenceNo).ToListAsync();

    private static Task<CustomerPaymentRecorded> Pay(
        Harness h, decimal amount, string method = "BANK_TRANSFER", string currency = "PKR",
        IReadOnlyList<ManualPaymentAllocation>? manual = null, DateTime? date = null,
        string? cheque = null, string? bankRef = null, string? notes = null, Guid? partner = null) =>
        h.Service.RecordPaymentAsync(partner ?? Customer, amount, method,
            new CustomerPaymentDetails(currency, date, cheque, bankRef, notes, manual), User);

    private static async Task NothingWritten(Harness h)
    {
        (await h.Db.CustomerPayments.CountAsync()).Should().Be(0, "a refused payment records no payment");
        (await h.Db.PaymentAllocations.CountAsync()).Should().Be(0);
        (await h.Db.CustomerLedgerEntries.CountAsync()).Should().Be(0, "and touches no ledger");
        (await h.Db.SalesInvoices.AsNoTracking().AnyAsync(i => i.AmountPaid != 0m)).Should().BeFalse("nor any invoice");
    }

    // ── The payment record ───────────────────────────────────────────────────

    [Fact]
    public async Task A_payment_is_recorded_as_received_with_everything_it_was_given()
    {
        var h = NewHarness();
        await SeedThree(h);

        var result = await Pay(h, 500m, "cheque", "pkr", date: new DateTime(2026, 9, 18, 23, 0, 0), cheque: " 004521 ", bankRef: "BR-77", notes: "Received at counter");

        result.PaymentNumber.Should().Be("CPAY-20260920-0001");
        result.CurrencyCode.Should().Be("PKR", "the catalog's spelling, trimmed");

        var payment = await h.Db.CustomerPayments.AsNoTracking().SingleAsync();
        payment.UUID.Should().Be(result.PaymentUuid);
        payment.Status.Should().Be("RECEIVED");
        payment.PartnerId.Should().Be(Customer);
        payment.PartnerName.Should().Be("Acme Ltd");
        payment.Amount.Should().Be(500m);
        payment.PaymentMethod.Should().Be("CHEQUE", "normalized from 'cheque'");
        payment.ChequeNumber.Should().Be("004521");
        payment.BankReference.Should().Be("BR-77");
        payment.Notes.Should().Be("Received at counter");
        payment.CurrencyCode.Should().Be("PKR");
        payment.PaymentDate.Should().Be(new DateTime(2026, 9, 18), "the receipt date, not the day it was keyed in");
        payment.CreatedBy.Should().Be(User);
        payment.CreatedDate.Should().Be(Today);
        payment.OrganizationId.Should().Be(h.OrgId);
    }

    [Fact]
    public async Task The_payment_date_defaults_to_today()
    {
        var h = NewHarness();
        await SeedThree(h);

        await Pay(h, 100m);

        (await h.Db.CustomerPayments.AsNoTracking().SingleAsync()).PaymentDate.Should().Be(new DateTime(2026, 9, 20));
    }

    [Fact]
    public async Task Payment_numbers_run_per_day_per_organization_and_never_reuse_one()
    {
        var clock = new FakeClock();
        var a = NewHarness(clock: clock);
        await SeedThree(a);

        (await Pay(a, 100m)).PaymentNumber.Should().Be("CPAY-20260920-0001");
        (await Pay(a, 100m)).PaymentNumber.Should().Be("CPAY-20260920-0002");

        clock.Now = Today.AddDays(1);
        (await Pay(a, 100m)).PaymentNumber.Should().Be("CPAY-20260921-0001", "each day restarts the count");

        var b = NewHarness(dbName: a.DbName);
        (await Pay(b, 100m, partner: Guid.NewGuid())).PaymentNumber.Should().Be("CPAY-20260920-0001", "another organization counts alone");
    }

    // ── §9.5: the ledger CREDIT ──────────────────────────────────────────────

    [Fact]
    public async Task The_customers_ledger_is_credited_with_the_payment()
    {
        var h = NewHarness();
        await SeedThree(h);
        await OweAlready(h, 6000m);

        var result = await Pay(h, 2500m, "BANK_TRANSFER", bankRef: "TRX-9", date: new DateTime(2026, 9, 19));

        result.PartnerBalance.Should().Be(3500m, "6,000 owed less 2,500 received");

        var entries = await Ledger(h);
        entries.Should().HaveCount(2);
        var entry = entries[1];
        entry.SequenceNo.Should().Be(2);
        entry.EntryType.Should().Be("PAYMENT");
        entry.CreditAmount.Should().Be(2500m, "a receipt reduces what the customer owes");
        entry.DebitAmount.Should().Be(0m);
        entry.RunningBalance.Should().Be(3500m);
        entry.ReferenceType.Should().Be("CustomerPayment");
        entry.ReferenceId.Should().Be(result.PaymentUuid);
        entry.ReferenceNumber.Should().Be(result.PaymentNumber);
        entry.EntryDate.Should().Be(new DateTime(2026, 9, 19));
        entry.CurrencyCode.Should().Be("PKR");
        entry.CreatedBy.Should().Be(User);
        entry.OrganizationId.Should().Be(h.OrgId);
        entry.Narration.Should().Contain(result.PaymentNumber).And.Contain("BANK_TRANSFER").And.Contain("TRX-9")
             .And.Contain("SINV-20260901-0001").And.Contain("SINV-20260905-0001");
    }

    [Fact]
    public async Task The_payment_the_invoices_and_the_ledger_are_one_unit_of_work()
    {
        // §9.5: "in the same transaction". One SaveChanges carries all of it, so it commits together
        // or not at all — there is no window where the invoice says paid and the ledger disagrees.
        var recorder = new SavedTogether();
        var h = NewHarness(interceptor: recorder);
        await SeedThree(h);
        recorder.Saves.Clear();

        await Pay(h, 2500m);

        recorder.Saves.Should().ContainSingle("recording a payment saves exactly once")
                .Which.Should().Be((Payments: 1, Allocations: 2, LedgerEntries: 1, ModifiedInvoices: 2));
    }

    [Fact]
    public async Task If_the_save_fails_nothing_changes_and_the_real_error_surfaces_after_the_retries()
    {
        var seed = NewHarness();
        await SeedThree(seed);
        var refusing = new AlwaysFail();
        var h = NewHarness(seed.OrgId, seed.DbName, refusing);

        var act = async () => await Pay(h, 2500m);

        (await act.Should().ThrowAsync<DbUpdateException>()).WithMessage("*refusing writes*");
        refusing.Attempts.Should().Be(5);
        await NothingWritten(seed);
        (await Inv(seed, "SINV-20260901-0001")).Status.Should().Be("ISSUED", "the failed unit of work left the invoices as they were");
        h.Db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Should().BeEmpty("a failed payment is not left on the context for a later save to commit half of");
    }

    // ── §9.4: FIFO auto-allocation ───────────────────────────────────────────

    [Fact]
    public async Task Money_goes_to_the_oldest_unpaid_invoice_first_and_spills_to_the_next()
    {
        var h = NewHarness();
        await SeedThree(h);

        var result = await Pay(h, 2500m);

        result.Allocations.Select(a => (a.InvoiceNumber, a.Amount, a.BalanceDue, a.InvoiceStatus)).Should().Equal(
            ("SINV-20260901-0001", 1000m, 0m, "PAID"),
            ("SINV-20260905-0001", 1500m, 500m, "PARTIALLY_PAID"));
        result.AllocatedAmount.Should().Be(2500m);
        result.UnallocatedAmount.Should().Be(0m);

        var a = await Inv(h, "SINV-20260901-0001");
        (a.AmountPaid, a.BalanceDue, a.Status).Should().Be((1000m, 0m, "PAID"));
        var b = await Inv(h, "SINV-20260905-0001");
        (b.AmountPaid, b.BalanceDue, b.Status).Should().Be((1500m, 500m, "PARTIALLY_PAID"));
        var c = await Inv(h, "SINV-20260910-0001");
        (c.AmountPaid, c.BalanceDue, c.Status).Should().Be((0m, 3000m, "ISSUED"), "the newest is not touched until the older ones are paid");
    }

    [Fact]
    public async Task Oldest_means_invoice_date_not_the_order_they_were_entered()
    {
        var h = NewHarness();
        var newer = Invoice("SINV-20260910-0001", new DateTime(2026, 9, 10), 500m);
        var older = Invoice("SINV-20260901-0001", new DateTime(2026, 9, 1), 500m);
        await Seed(h, newer, older);

        await Pay(h, 500m);

        (await Inv(h, "SINV-20260901-0001")).Status.Should().Be("PAID");
        (await Inv(h, "SINV-20260910-0001")).Status.Should().Be("ISSUED");
    }

    [Fact]
    public async Task Invoices_on_the_same_day_are_paid_in_the_order_they_were_raised()
    {
        var h = NewHarness();
        await Seed(h,
            Invoice("SINV-20260901-0001", new DateTime(2026, 9, 1), 500m),
            Invoice("SINV-20260901-0002", new DateTime(2026, 9, 1), 500m));

        await Pay(h, 500m);

        (await Inv(h, "SINV-20260901-0001")).Status.Should().Be("PAID");
        (await Inv(h, "SINV-20260901-0002")).Status.Should().Be("ISSUED");
    }

    [Fact]
    public async Task A_payment_that_exactly_clears_everything_pays_every_invoice_and_leaves_nothing_over()
    {
        var h = NewHarness();
        await SeedThree(h);
        await OweAlready(h, 6000m);

        var result = await Pay(h, 6000m);

        result.Allocations.Should().HaveCount(3).And.OnlyContain(a => a.InvoiceStatus == "PAID" && a.BalanceDue == 0m);
        result.UnallocatedAmount.Should().Be(0m);
        result.PartnerBalance.Should().Be(0m);
    }

    [Fact]
    public async Task A_second_payment_continues_where_the_first_stopped()
    {
        var h = NewHarness();
        await SeedThree(h);

        await Pay(h, 1500m);   // A paid, B has 500 paid
        var second = await Pay(h, 1600m);   // B's remaining 1,500, then 100 into C

        second.Allocations.Select(a => (a.InvoiceNumber, a.Amount, a.InvoiceStatus)).Should().Equal(
            ("SINV-20260905-0001", 1500m, "PAID"),
            ("SINV-20260910-0001", 100m, "PARTIALLY_PAID"));

        var b = await Inv(h, "SINV-20260905-0001");
        (b.AmountPaid, b.BalanceDue).Should().Be((2000m, 0m), "amount_paid accumulates across payments");
    }

    [Fact]
    public async Task More_than_is_owed_pays_everything_and_the_rest_is_held_on_account()
    {
        var h = NewHarness();
        await Seed(h, Invoice("SINV-20260901-0001", new DateTime(2026, 9, 1), 1000m));
        await OweAlready(h, 1000m);

        var result = await Pay(h, 1500m);

        result.AllocatedAmount.Should().Be(1000m);
        result.UnallocatedAmount.Should().Be(500m);
        result.PartnerBalance.Should().Be(-500m, "the customer is now in credit");

        var invoice = await Inv(h, "SINV-20260901-0001");
        (invoice.AmountPaid, invoice.BalanceDue).Should().Be((1000m, 0m), "an invoice is never paid past its total");
        (await Ledger(h)).Last().CreditAmount.Should().Be(1500m, "the ledger takes the whole receipt, allocated or not");
    }

    [Fact]
    public async Task A_customer_with_nothing_open_still_has_the_payment_recorded_and_credited()
    {
        var h = NewHarness();
        await OweAlready(h, 0.01m);   // a customer already known to the ledger, with no invoice at all

        var result = await Pay(h, 800m);

        result.Allocations.Should().BeEmpty();
        result.AllocatedAmount.Should().Be(0m);
        result.UnallocatedAmount.Should().Be(800m);
        (await h.Db.CustomerPayments.CountAsync()).Should().Be(1);
        (await Ledger(h)).Last().CreditAmount.Should().Be(800m);
        (await Ledger(h)).Last().Narration.Should().Contain("held on account");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("PAID")]
    [InlineData("CANCELLED")]
    [InlineData("CREDIT_NOTE")]
    public async Task Only_issued_invoices_are_candidates_for_auto_allocation(string status)
    {
        var h = NewHarness();
        await Seed(h, Invoice("SINV-20260901-0001", new DateTime(2026, 9, 1), 1000m, status, paid: status == "PAID" ? 1000m : 0m));

        var result = await Pay(h, 400m);

        result.Allocations.Should().BeEmpty($"a {status} invoice has no receivable to pay down");
        result.UnallocatedAmount.Should().Be(400m);
        (await Inv(h, "SINV-20260901-0001")).Status.Should().Be(status);
    }

    [Theory]
    [InlineData("ISSUED")]
    [InlineData("PARTIALLY_PAID")]
    [InlineData("OVERDUE")]
    public async Task Issued_partly_paid_and_overdue_invoices_can_all_be_paid(string status)
    {
        var h = NewHarness();
        await Seed(h, Invoice("SINV-20260901-0001", new DateTime(2026, 9, 1), 1000m, status, paid: status == "PARTIALLY_PAID" ? 300m : 0m));

        var result = await Pay(h, 200m);

        result.Allocations.Should().ContainSingle().Which.Amount.Should().Be(200m);
    }

    [Fact]
    public async Task An_overdue_invoice_paid_in_part_becomes_partially_paid_and_in_full_becomes_paid()
    {
        var h = NewHarness();
        await Seed(h, Invoice("SINV-20260801-0001", new DateTime(2026, 8, 1), 1000m, "OVERDUE"));

        await Pay(h, 400m);
        (await Inv(h, "SINV-20260801-0001")).Status.Should().Be("PARTIALLY_PAID", "§9.5: PARTIALLY_PAID when amount_paid > 0");

        await Pay(h, 600m);
        (await Inv(h, "SINV-20260801-0001")).Status.Should().Be("PAID");
    }

    [Fact]
    public async Task Other_customers_other_currencies_deleted_invoices_and_other_organizations_are_never_touched()
    {
        var h = NewHarness();
        var other = Guid.NewGuid();
        await Seed(h,
            Invoice("SINV-MINE-0001", new DateTime(2026, 9, 10), 1000m),
            Invoice("SINV-THEIRS-01", new DateTime(2026, 9, 1), 1000m, partner: other),
            Invoice("SINV-USD-0001", new DateTime(2026, 9, 2), 1000m, currency: "USD"),
            Invoice("SINV-GONE-0001", new DateTime(2026, 9, 3), 1000m, deleted: true));

        var stranger = NewHarness(Guid.NewGuid(), h.DbName);
        await Seed(stranger, Invoice("SINV-ORG2-0001", new DateTime(2026, 8, 1), 1000m));

        var result = await Pay(h, 5000m);

        result.Allocations.Should().ContainSingle().Which.InvoiceNumber.Should().Be("SINV-MINE-0001");
        result.UnallocatedAmount.Should().Be(4000m);

        foreach (var number in new[] { "SINV-THEIRS-01", "SINV-USD-0001", "SINV-GONE-0001" })
            (await Inv(h, number)).AmountPaid.Should().Be(0m, number);
        (await Inv(stranger, "SINV-ORG2-0001")).AmountPaid.Should().Be(0m, "another organization's invoice");
    }

    [Fact]
    public async Task A_payment_in_another_currency_pays_that_currencys_invoices()
    {
        var h = NewHarness();
        await Seed(h,
            Invoice("SINV-PKR-0001", new DateTime(2026, 9, 1), 1000m),
            Invoice("SINV-USD-0001", new DateTime(2026, 9, 2), 100m, currency: "USD"));

        var result = await Pay(h, 100m, currency: "usd");

        result.CurrencyCode.Should().Be("USD");
        result.Allocations.Should().ContainSingle().Which.InvoiceNumber.Should().Be("SINV-USD-0001");
        (await Inv(h, "SINV-PKR-0001")).AmountPaid.Should().Be(0m);
        (await Ledger(h)).Single().CurrencyCode.Should().Be("USD");
    }

    [Fact]
    public async Task Each_allocation_records_who_when_and_how_much()
    {
        var h = NewHarness();
        await SeedThree(h);

        var result = await Pay(h, 1500m);

        var allocations = await h.Db.PaymentAllocations.AsNoTracking().Include(a => a.SalesInvoice).OrderBy(a => a.SalesInvoice.InvoiceDate).ToListAsync();
        allocations.Select(a => (a.SalesInvoice.InvoiceNumber, a.AllocatedAmount)).Should().Equal(
            ("SINV-20260901-0001", 1000m), ("SINV-20260905-0001", 500m));
        allocations.Should().OnlyContain(a => a.AllocatedBy == User && a.AllocatedAt == Today && a.OrganizationId == h.OrgId);
        allocations.Select(a => a.CustomerPaymentId).Distinct().Should().ContainSingle();

        var invoice = await Inv(h, "SINV-20260901-0001");
        invoice.ModifiedBy.Should().Be(User);
        invoice.ModifiedDate.Should().Be(Today);
        (await Inv(h, "SINV-20260910-0001")).ModifiedBy.Should().BeNull("an invoice the payment did not reach is not modified");
        result.Allocations.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(0.01)]
    [InlineData(999.99)]
    [InlineData(1000)]
    [InlineData(1000.01)]
    [InlineData(2999.99)]
    [InlineData(3000)]
    [InlineData(6000)]
    [InlineData(6000.01)]
    [InlineData(10000)]
    public async Task Whatever_the_amount_the_books_stay_consistent(double paid)
    {
        var h = NewHarness();
        await SeedThree(h);
        var amount = (decimal)paid;

        var result = await Pay(h, amount);

        result.AllocatedAmount.Should().BeLessThanOrEqualTo(amount);
        (result.AllocatedAmount + result.UnallocatedAmount).Should().Be(amount, "every cent is either applied or on account");

        var invoices = await h.Db.SalesInvoices.AsNoTracking().Include(i => i.Allocations).ToListAsync();
        foreach (var i in invoices)
        {
            i.AmountPaid.Should().Be(i.Allocations.Sum(a => a.AllocatedAmount), $"{i.InvoiceNumber}: amount_paid is the sum of its allocations");
            i.BalanceDue.Should().Be(i.GrandTotal - i.AmountPaid, $"{i.InvoiceNumber}: balance_due is what remains");
            i.BalanceDue.Should().BeGreaterThanOrEqualTo(0m, $"{i.InvoiceNumber}: never overpaid");
            i.Status.Should().Be(i.BalanceDue == 0m ? "PAID" : i.AmountPaid > 0m ? "PARTIALLY_PAID" : "ISSUED", i.InvoiceNumber);
        }

        (await Ledger(h)).Single().CreditAmount.Should().Be(amount);
    }

    // ── §9.4: manual override ────────────────────────────────────────────────

    [Fact]
    public async Task A_manual_allocation_is_applied_exactly_and_ignores_the_fifo_order()
    {
        var h = NewHarness();
        var (a, b, c) = await SeedThree(h);

        // The customer says: this cheque is for the September 10 invoice, not the oldest.
        var result = await Pay(h, 3000m, "CHEQUE", cheque: "77", manual: [new(c.UUID, 3000m)]);

        result.Allocations.Should().ContainSingle().Which.Should().Be(
            new AppliedPaymentAllocation(c.UUID, "SINV-20260910-0001", 3000m, 0m, "PAID"));
        (await Inv(h, "SINV-20260901-0001")).AmountPaid.Should().Be(0m, "the older invoices were not paid");
        (await Inv(h, "SINV-20260905-0001")).AmountPaid.Should().Be(0m);
    }

    [Fact]
    public async Task A_manual_allocation_can_split_a_payment_across_chosen_invoices_and_part_pay_one()
    {
        var h = NewHarness();
        var (a, b, c) = await SeedThree(h);

        var result = await Pay(h, 2000m, manual: [new(c.UUID, 1200m), new(a.UUID, 800m)]);

        result.Allocations.Select(x => (x.InvoiceNumber, x.Amount, x.InvoiceStatus)).Should().Equal(
            ("SINV-20260910-0001", 1200m, "PARTIALLY_PAID"),
            ("SINV-20260901-0001", 800m, "PARTIALLY_PAID"));
        result.UnallocatedAmount.Should().Be(0m);
    }

    [Fact]
    public async Task What_a_manual_allocation_does_not_name_stays_on_account_rather_than_spilling_onto_other_invoices()
    {
        var h = NewHarness();
        var (a, _, _) = await SeedThree(h);

        var result = await Pay(h, 3000m, manual: [new(a.UUID, 1000m)]);

        result.AllocatedAmount.Should().Be(1000m);
        result.UnallocatedAmount.Should().Be(2000m);
        (await Inv(h, "SINV-20260905-0001")).AmountPaid.Should().Be(0m, "an override is not topped up by FIFO");
        (await Ledger(h)).Single().CreditAmount.Should().Be(3000m);
    }

    [Fact]
    public async Task An_empty_manual_allocation_deliberately_puts_the_whole_payment_on_account()
    {
        var h = NewHarness();
        await SeedThree(h);

        var result = await Pay(h, 500m, manual: []);

        result.Allocations.Should().BeEmpty();
        result.UnallocatedAmount.Should().Be(500m);
        (await h.Db.SalesInvoices.AsNoTracking().AnyAsync(i => i.AmountPaid != 0m)).Should().BeFalse();
        (await Ledger(h)).Should().ContainSingle();
    }

    [Fact]
    public async Task A_manual_allocation_naming_an_unknown_invoice_is_not_found_and_writes_nothing()
    {
        var h = NewHarness();
        await SeedThree(h);

        var act = async () => await Pay(h, 500m, manual: [new(Guid.NewGuid(), 500m)]);

        await act.Should().ThrowAsync<NotFoundException>();
        await NothingWritten(h);
    }

    [Fact]
    public async Task A_manual_allocation_cannot_reach_another_customers_or_another_organizations_invoice()
    {
        var h = NewHarness();
        var theirs = Invoice("SINV-THEIRS-01", new DateTime(2026, 9, 1), 1000m, partner: Guid.NewGuid());
        await Seed(h, theirs);

        var stranger = NewHarness(Guid.NewGuid(), h.DbName);
        var foreign = Invoice("SINV-ORG2-0001", new DateTime(2026, 9, 1), 1000m);
        await Seed(stranger, foreign);

        var otherCustomer = async () => await Pay(h, 500m, manual: [new(theirs.UUID, 500m)]);
        (await otherCustomer.Should().ThrowAsync<BadRequestException>()).WithMessage("*another customer*");

        var otherOrg = async () => await Pay(h, 500m, manual: [new(foreign.UUID, 500m)]);
        await otherOrg.Should().ThrowAsync<NotFoundException>("the tenant filter makes it simply not exist");

        await NothingWritten(h);
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("PAID")]
    [InlineData("CANCELLED")]
    [InlineData("CREDIT_NOTE")]
    public async Task A_manual_allocation_to_an_invoice_that_is_not_payable_is_refused(string status)
    {
        var h = NewHarness();
        var invoice = Invoice("SINV-20260901-0001", new DateTime(2026, 9, 1), 1000m, status, paid: status == "PAID" ? 1000m : 0m);
        await Seed(h, invoice);

        var act = async () => await Pay(h, 500m, manual: [new(invoice.UUID, 500m)]);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage($"*{status}*").WithMessage("*Only an ISSUED*");
        (await h.Db.CustomerPayments.CountAsync()).Should().Be(0);
        (await h.Db.CustomerLedgerEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_manual_allocation_in_the_wrong_currency_is_refused()
    {
        var h = NewHarness();
        var usd = Invoice("SINV-USD-0001", new DateTime(2026, 9, 1), 100m, currency: "USD");
        await Seed(h, usd);

        var act = async () => await Pay(h, 50m, currency: "PKR", manual: [new(usd.UUID, 50m)]);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*USD*").WithMessage("*PKR*");
        await NothingWritten(h);
    }

    [Fact]
    public async Task A_manual_allocation_cannot_pay_more_than_the_invoice_still_owes()
    {
        var h = NewHarness();
        var invoice = Invoice("SINV-20260901-0001", new DateTime(2026, 9, 1), 1000m, "PARTIALLY_PAID", paid: 700m);
        await Seed(h, invoice);

        var act = async () => await Pay(h, 500m, manual: [new(invoice.UUID, 400m)]);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*400.00*").WithMessage("*300.00 still owing*");
        (await Inv(h, "SINV-20260901-0001")).AmountPaid.Should().Be(700m);
    }

    [Fact]
    public async Task Manual_allocations_cannot_add_up_to_more_than_the_payment()
    {
        var h = NewHarness();
        var (a, b, _) = await SeedThree(h);

        var act = async () => await Pay(h, 1500m, manual: [new(a.UUID, 1000m), new(b.UUID, 600m)]);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*1600.00*").WithMessage("*payment of 1500.00*");
        await NothingWritten(h);
    }

    [Fact]
    public async Task An_invoice_named_twice_is_refused_rather_than_silently_summed()
    {
        var h = NewHarness();
        var (a, _, _) = await SeedThree(h);

        var act = async () => await Pay(h, 1000m, manual: [new(a.UUID, 300m), new(a.UUID, 300m)]);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*named twice*");
        await NothingWritten(h);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(10.005)]
    public async Task A_manual_allocation_amount_must_be_positive_money(double amount)
    {
        var h = NewHarness();
        var (a, _, _) = await SeedThree(h);

        var act = async () => await Pay(h, 1000m, manual: [new(a.UUID, (decimal)amount)]);

        await act.Should().ThrowAsync<BadRequestException>();
        await NothingWritten(h);
    }

    // ── Validation of the payment itself ─────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(100.005)]
    public async Task A_payment_must_be_positive_money_of_at_most_two_decimal_places(double amount)
    {
        var h = NewHarness();
        await SeedThree(h);

        var act = async () => await Pay(h, (decimal)amount);

        await act.Should().ThrowAsync<BadRequestException>();
        await NothingWritten(h);
    }

    [Theory]
    [InlineData("")]
    [InlineData("WIRE")]
    [InlineData("credit")]
    public async Task An_unknown_payment_method_is_refused(string method)
    {
        var h = NewHarness();
        await SeedThree(h);

        var act = async () => await Pay(h, 100m, method);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*CASH, CHEQUE, BANK_TRANSFER, CARD, ONLINE*");
        await NothingWritten(h);
    }

    [Theory]
    [InlineData("CASH")]
    [InlineData("CHEQUE")]
    [InlineData("BANK_TRANSFER")]
    [InlineData("CARD")]
    [InlineData("ONLINE")]
    public async Task Every_section_9_3_method_is_accepted(string method)
    {
        var h = NewHarness();
        await SeedThree(h);

        var act = async () => await Pay(h, 100m, method, cheque: "1234");

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_cheque_needs_its_number(string? cheque)
    {
        var h = NewHarness();
        await SeedThree(h);

        var act = async () => await Pay(h, 100m, "CHEQUE", cheque: cheque);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*cheque number*");
        await NothingWritten(h);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("EUR")]
    [InlineData("Mystery")]
    public async Task The_currency_must_be_named_and_be_in_the_catalog(string currency)
    {
        var h = NewHarness();
        await SeedThree(h);

        var act = async () => await Pay(h, 100m, currency: currency);

        await act.Should().ThrowAsync<BadRequestException>();
        await NothingWritten(h);
    }

    [Fact]
    public async Task Over_long_references_are_refused_before_they_reach_the_database()
    {
        var h = NewHarness();
        await SeedThree(h);

        (await FluentActions.Awaiting(() => Pay(h, 100m, "CHEQUE", cheque: new string('9', 31))).Should().ThrowAsync<BadRequestException>()).WithMessage("*cheque number*");
        (await FluentActions.Awaiting(() => Pay(h, 100m, bankRef: new string('x', 101))).Should().ThrowAsync<BadRequestException>()).WithMessage("*bank reference*");
        (await FluentActions.Awaiting(() => Pay(h, 100m, notes: new string('n', 501))).Should().ThrowAsync<BadRequestException>()).WithMessage("*notes*");
        await NothingWritten(h);
    }

    // ── Who the customer is ──────────────────────────────────────────────────

    [Fact]
    public async Task A_customer_nobody_knows_is_not_found()
    {
        var h = NewHarness();
        h.Names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new Dictionary<Guid, string>());

        var act = async () => await Pay(h, 100m, partner: Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
        await NothingWritten(h);
    }

    [Fact]
    public async Task A_customer_we_have_already_billed_is_known_even_if_the_master_record_cannot_be_reached()
    {
        var h = NewHarness();
        await SeedThree(h);
        h.Names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new Dictionary<Guid, string>());

        await Pay(h, 100m);

        (await h.Db.CustomerPayments.AsNoTracking().SingleAsync()).PartnerName.Should().Be("Acme Ltd", "taken from their invoices");
    }

    // ── Lost races ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Losing_the_race_re_plans_against_what_the_winner_committed_so_nothing_is_paid_twice()
    {
        // Both payments are about to clear the oldest invoice (1,000). The rival's commits first.
        // Ours must not pay it again — it re-reads, finds it PAID, and puts the money on the next one.
        var dbName = Guid.NewGuid().ToString();
        var orgId  = Guid.NewGuid();
        var rival  = NewHarness(orgId, dbName);
        await SeedThree(rival);
        await OweAlready(rival, 6000m);

        var race = new LoseTheRaceOnce(db => db.ChangeTracker.Entries<CustomerPayment>().Any(e => e.State == EntityState.Added), async () =>
            await Pay(rival, 1000m));

        var h = NewHarness(orgId, dbName, race);
        var result = await Pay(h, 1500m);

        race.Fired.Should().BeTrue();

        var a = await Inv(h, "SINV-20260901-0001");
        (a.AmountPaid, a.BalanceDue, a.Status).Should().Be((1000m, 0m, "PAID"), "paid once, by the winner — not twice");

        result.Allocations.Should().ContainSingle().Which.InvoiceNumber.Should().Be("SINV-20260905-0001");
        var b = await Inv(h, "SINV-20260905-0001");
        (b.AmountPaid, b.BalanceDue, b.Status).Should().Be((1500m, 500m, "PARTIALLY_PAID"));

        result.PaymentNumber.Should().Be("CPAY-20260920-0002", "the winner took 0001");
        (await h.Db.CustomerPayments.CountAsync()).Should().Be(2);

        var ledger = await Ledger(h);
        ledger.Select(e => (e.SequenceNo, e.RunningBalance)).Should().Equal((1, 6000m), (2, 5000m), (3, 3500m));
    }

    [Fact]
    public async Task Losing_the_race_for_a_payment_number_takes_the_next_one()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgId  = Guid.NewGuid();
        var rival  = NewHarness(orgId, dbName);
        var elsewhere = Guid.NewGuid();

        var race = new LoseTheRaceOnce(db => db.ChangeTracker.Entries<CustomerPayment>().Any(e => e.State == EntityState.Added), async () =>
            await Pay(rival, 10m, partner: elsewhere));

        var h = NewHarness(orgId, dbName, race);
        var result = await Pay(h, 20m);

        race.Fired.Should().BeTrue();
        result.PaymentNumber.Should().Be("CPAY-20260920-0002");
    }

    [Fact]
    public async Task A_retry_that_finds_the_invoice_no_longer_payable_refuses_a_manual_allocation_rather_than_overpaying()
    {
        // The customer's manual instruction was "pay this invoice 1,000". By the time we retry, the
        // rival has already paid it in full — applying our instruction again would overpay it.
        var dbName = Guid.NewGuid().ToString();
        var orgId  = Guid.NewGuid();
        var rival  = NewHarness(orgId, dbName);
        var a = Invoice("SINV-20260901-0001", new DateTime(2026, 9, 1), 1000m);
        await Seed(rival, a);

        var race = new LoseTheRaceOnce(db => db.ChangeTracker.Entries<CustomerPayment>().Any(e => e.State == EntityState.Added), async () =>
            await Pay(rival, 1000m));

        var h = NewHarness(orgId, dbName, race);
        var act = async () => await Pay(h, 1000m, manual: [new(a.UUID, 1000m)]);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*PAID*");
        (await Inv(h, "SINV-20260901-0001")).AmountPaid.Should().Be(1000m);
        (await h.Db.CustomerPayments.CountAsync()).Should().Be(1, "only the winner's");
    }
}
