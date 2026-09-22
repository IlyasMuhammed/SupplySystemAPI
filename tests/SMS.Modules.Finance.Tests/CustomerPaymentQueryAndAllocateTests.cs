using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-08 §9.3–§9.4 — reading customer payments, and applying what is left of one to invoices
/// after the fact (<c>POST /{id}/allocate</c>).
/// </summary>
public class CustomerPaymentQueryAndAllocateTests
{
    private const int User = Receivables.User;
    private static readonly Guid Customer = Guid.NewGuid();

    private sealed record H(FinanceDbContext Db, CustomerPaymentService Service, TestClock Clock, Guid Org, string DbName);

    private static H New(Guid? org = null, string? dbName = null, IInterceptor? interceptor = null, TestClock? clock = null)
    {
        org    ??= Guid.NewGuid();
        dbName ??= Guid.NewGuid().ToString();
        clock  ??= new TestClock();
        var db = Receivables.Db(org.Value, dbName, interceptor);
        var service = new CustomerPaymentService(
            db, new CustomerLedgerService(db, clock), Receivables.Names().Object, Receivables.Lookups().Object, clock);
        return new H(db, service, clock, org.Value, dbName);
    }

    private static Task<CustomerPaymentRecorded> Pay(
        H h, decimal amount, string method = "BANK_TRANSFER", IReadOnlyList<ManualPaymentAllocation>? manual = null,
        DateTime? date = null, string? cheque = null, string? bankRef = null, Guid? partner = null, string currency = "PKR") =>
        h.Service.RecordPaymentAsync(partner ?? Customer, amount, method,
            new CustomerPaymentDetails(currency, date, cheque, bankRef, null, manual), User);

    /// <summary>1,000 (1 Sep), 2,000 (5 Sep) and 3,000 (10 Sep), all issued.</summary>
    private static async Task<(SalesInvoice A, SalesInvoice B, SalesInvoice C)> ThreeInvoices(H h)
    {
        var a = Receivables.Invoice(h.Org, Customer, "SINV-A", new DateTime(2026, 9, 1), 1000m);
        var b = Receivables.Invoice(h.Org, Customer, "SINV-B", new DateTime(2026, 9, 5), 2000m);
        var c = Receivables.Invoice(h.Org, Customer, "SINV-C", new DateTime(2026, 9, 10), 3000m);
        await Receivables.Seed(h.Db, c, a, b);
        return (a, b, c);
    }

    private static Task<SalesInvoice> Inv(H h, string number) =>
        Receivables.Auditor(h.DbName).SalesInvoices.AsNoTracking().SingleAsync(i => i.InvoiceNumber == number);

    private static Task<List<CustomerLedgerEntry>> Ledger(H h) =>
        Receivables.Auditor(h.DbName).CustomerLedgerEntries.AsNoTracking().OrderBy(e => e.SequenceNo).ToListAsync();

    // ── Allocate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Money_held_on_account_is_applied_oldest_invoice_first()
    {
        var h = New();
        await ThreeInvoices(h);
        var payment = await Pay(h, 1500m, manual: []);
        payment.Allocations.Should().BeEmpty();

        var result = await h.Service.AllocateAsync(payment.PaymentUuid, null, User);

        result.Allocations.Select(a => (a.InvoiceNumber, a.Amount, a.BalanceDue, a.InvoiceStatus)).Should().Equal(
            ("SINV-A", 1000m, 0m, "PAID"),
            ("SINV-B", 500m, 1500m, "PARTIALLY_PAID"));
        (result.Amount, result.AllocatedAmount, result.UnallocatedAmount).Should().Be((1500m, 1500m, 0m));
        result.PaymentNumber.Should().Be(payment.PaymentNumber);

        var a = await Inv(h, "SINV-A");
        (a.AmountPaid, a.BalanceDue, a.Status, a.ModifiedBy).Should().Be((1000m, 0m, "PAID", User));
        var c = await Inv(h, "SINV-C");
        (c.AmountPaid, c.Status).Should().Be((0m, "ISSUED"));
    }

    [Fact]
    public async Task Allocating_does_not_touch_the_ledger_because_the_credit_was_booked_in_full_when_the_payment_was_recorded()
    {
        var h = New();
        await ThreeInvoices(h);
        var payment = await Pay(h, 1500m, manual: []);
        var before = await Ledger(h);

        await h.Service.AllocateAsync(payment.PaymentUuid, null, User);

        var after = await Ledger(h);
        after.Should().HaveCount(before.Count).And.HaveCount(1);
        after.Single().CreditAmount.Should().Be(1500m);
        after.Single().Should().BeEquivalentTo(before.Single(), "the entry is exactly as it was");
    }

    [Fact]
    public async Task What_is_left_of_a_part_applied_payment_is_what_gets_applied()
    {
        var h = New();
        var (a, _, _) = await ThreeInvoices(h);
        var payment = await Pay(h, 2500m, manual: [new(a.UUID, 1000m)]);   // 1,500 left on account

        var result = await h.Service.AllocateAsync(payment.PaymentUuid, null, User);

        result.Allocations.Should().ContainSingle().Which.Should().Match<AppliedPaymentAllocation>(
            x => x.InvoiceNumber == "SINV-B" && x.Amount == 1500m, "A is already paid; the 1,500 goes to the next");
        (result.AllocatedAmount, result.UnallocatedAmount).Should().Be((2500m, 0m), "the total includes what was applied before");
        result.Allocations.Should().HaveCount(1, "the result lists only what this call applied");
    }

    [Fact]
    public async Task A_manual_allocation_is_applied_as_written_and_the_rest_stays_on_account()
    {
        var h = New();
        var (_, _, c) = await ThreeInvoices(h);
        var payment = await Pay(h, 3000m, manual: []);

        var result = await h.Service.AllocateAsync(payment.PaymentUuid, [new(c.UUID, 1200m)], User);

        result.Allocations.Should().ContainSingle().Which.Should().Be(
            new AppliedPaymentAllocation(c.UUID, "SINV-C", 1200m, 1800m, "PARTIALLY_PAID"));
        (result.AllocatedAmount, result.UnallocatedAmount).Should().Be((1200m, 1800m));
        (await Inv(h, "SINV-A")).AmountPaid.Should().Be(0m, "an override is not topped up by FIFO");
    }

    [Fact]
    public async Task Applying_in_two_steps_leaves_the_payment_and_the_invoices_consistent()
    {
        var h = New();
        var (a, b, _) = await ThreeInvoices(h);
        var payment = await Pay(h, 2000m, manual: []);

        await h.Service.AllocateAsync(payment.PaymentUuid, [new(b.UUID, 700m)], User);
        var second = await h.Service.AllocateAsync(payment.PaymentUuid, null, User);

        second.Allocations.Select(x => (x.InvoiceNumber, x.Amount)).Should().Equal(("SINV-A", 1000m), ("SINV-B", 300m));
        (second.AllocatedAmount, second.UnallocatedAmount).Should().Be((2000m, 0m));

        var detail = await h.Service.GetAsync(payment.PaymentUuid);
        detail!.Allocations.Sum(x => x.AllocatedAmount).Should().Be(2000m);
        var b2 = await Inv(h, "SINV-B");
        (b2.AmountPaid, b2.BalanceDue).Should().Be((1000m, 1000m), "700 then 300");
    }

    [Fact]
    public async Task A_payment_already_applied_in_full_has_nothing_left_to_allocate()
    {
        var h = New();
        await ThreeInvoices(h);
        var payment = await Pay(h, 1000m);   // FIFO at recording: all of it applied

        var act = async () => await h.Service.AllocateAsync(payment.PaymentUuid, null, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*applied in full*").WithMessage("*nothing left*");
    }

    [Fact]
    public async Task Asking_twice_applies_the_money_once()
    {
        var h = New();
        await ThreeInvoices(h);
        var payment = await Pay(h, 1000m, manual: []);

        await h.Service.AllocateAsync(payment.PaymentUuid, null, User);
        var again = async () => await h.Service.AllocateAsync(payment.PaymentUuid, null, User);

        await again.Should().ThrowAsync<BadRequestException>();
        (await Inv(h, "SINV-A")).AmountPaid.Should().Be(1000m, "not 2,000");
        (await Inv(h, "SINV-B")).AmountPaid.Should().Be(0m);
    }

    [Theory]
    [InlineData("BOUNCED")]
    [InlineData("REVERSED")]
    public async Task Only_a_received_payment_can_be_applied(string status)
    {
        var h = New();
        await ThreeInvoices(h);
        var payment = await Pay(h, 1000m, manual: []);
        var stored = await h.Db.CustomerPayments.SingleAsync(p => p.UUID == payment.PaymentUuid);
        stored.Status = status;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.Service.AllocateAsync(payment.PaymentUuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage($"*{status}*").WithMessage("*RECEIVED*");
        (await Inv(h, "SINV-A")).AmountPaid.Should().Be(0m);
    }

    [Fact]
    public async Task An_unknown_or_another_organizations_payment_is_not_found()
    {
        var h = New();
        await ThreeInvoices(h);
        var payment = await Pay(h, 1000m, manual: []);
        var stranger = New(Guid.NewGuid(), h.DbName);

        await ((Func<Task>)(() => h.Service.AllocateAsync(Guid.NewGuid(), null, User))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => stranger.Service.AllocateAsync(payment.PaymentUuid, null, User))).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task With_no_unpaid_invoice_in_the_payments_currency_there_is_nothing_to_apply_it_to()
    {
        var h = New();
        await Receivables.Seed(h.Db, Receivables.Invoice(h.Org, Customer, "SINV-USD", new DateTime(2026, 9, 1), 100m, currency: "USD"));
        var payment = await Pay(h, 500m, manual: []);   // PKR

        var act = async () => await h.Service.AllocateAsync(payment.PaymentUuid, null, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*no unpaid PKR invoice*");
        (await Inv(h, "SINV-USD")).AmountPaid.Should().Be(0m);
    }

    [Fact]
    public async Task An_empty_manual_list_is_refused_here_because_applying_nothing_is_not_an_allocation()
    {
        var h = New();
        await ThreeInvoices(h);
        var payment = await Pay(h, 500m, manual: []);

        var act = async () => await h.Service.AllocateAsync(payment.PaymentUuid, [], User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*No allocations were given*");
    }

    [Fact]
    public async Task A_manual_allocation_cannot_exceed_what_is_left_or_what_an_invoice_owes_and_changes_nothing_when_refused()
    {
        var h = New();
        var (a, b, _) = await ThreeInvoices(h);
        var payment = await Pay(h, 1500m, manual: [new(b.UUID, 1000m)]);   // 500 left

        var tooMuchInTotal = async () => await h.Service.AllocateAsync(payment.PaymentUuid, [new(a.UUID, 400m), new(b.UUID, 200m)], User);
        (await tooMuchInTotal.Should().ThrowAsync<BadRequestException>()).WithMessage("*600.00*").WithMessage("*what is left to apply from the payment, 500.00*");

        var tooMuchForInvoice = async () => await h.Service.AllocateAsync(payment.PaymentUuid, [new(a.UUID, 1000.01m)], User);
        await tooMuchForInvoice.Should().ThrowAsync<BadRequestException>();

        var notPayable = async () => await h.Service.AllocateAsync(payment.PaymentUuid, [new(Guid.NewGuid(), 10m)], User);
        await notPayable.Should().ThrowAsync<NotFoundException>();

        (await Inv(h, "SINV-A")).AmountPaid.Should().Be(0m);
        (await Inv(h, "SINV-B")).AmountPaid.Should().Be(1000m, "as it was after recording");
        (await h.Service.GetAsync(payment.PaymentUuid))!.UnallocatedAmount.Should().Be(500m);
    }

    [Fact]
    public async Task Allocation_rows_record_who_when_and_how_much_and_the_payment_notes_the_change()
    {
        var h = New();
        await ThreeInvoices(h);
        var payment = await Pay(h, 1500m, manual: []);
        h.Clock.Value = TestClock.Start.AddDays(2);

        await h.Service.AllocateAsync(payment.PaymentUuid, null, 9);

        var detail = await h.Service.GetAsync(payment.PaymentUuid);
        detail!.Allocations.Should().OnlyContain(a => a.AllocatedBy == 9 && a.AllocatedAt == TestClock.Start.AddDays(2));
        (detail.ModifiedBy, detail.ModifiedDate).Should().Be((9, TestClock.Start.AddDays(2)));
    }

    // ── Two requests at once ─────────────────────────────────────────────────

    [Fact]
    public async Task Two_requests_to_apply_the_same_payment_at_once_apply_it_once()
    {
        // A double click. The second request has read the payment with all its money still on account;
        // by the time it saves, the first has applied it. Without a guard, both would apply it.
        var dbName = Guid.NewGuid().ToString();
        var org    = Guid.NewGuid();
        var first  = New(org, dbName);
        await ThreeInvoices(first);
        var payment = await Pay(first, 1000m, manual: []);

        var race = new LoseTheRaceOnce(db => db.ChangeTracker.Entries<PaymentAllocation>().Any(e => e.State == EntityState.Added),
            async () => await first.Service.AllocateAsync(payment.PaymentUuid, null, User), asConcurrencyConflict: true);
        var second = New(org, dbName, race);

        var act = async () => await second.Service.AllocateAsync(payment.PaymentUuid, null, User);

        await act.Should().ThrowAsync<BadRequestException>("its retry finds the payment applied in full");
        race.Fired.Should().BeTrue();

        (await Inv(first, "SINV-A")).AmountPaid.Should().Be(1000m, "paid once, not twice");
        var detail = await first.Service.GetAsync(payment.PaymentUuid);
        detail!.Allocations.Should().ContainSingle();
        detail.UnallocatedAmount.Should().Be(0m);
        second.Db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified).Should().BeEmpty();
    }

    [Fact]
    public async Task Two_payments_applied_to_the_same_invoice_at_once_do_not_both_pay_it()
    {
        // Payments P1 and P2 (1,000 each, on account) are both about to clear invoice A (1,000). P2's
        // request commits first. P1's, having read A as unpaid, must notice and move to the next.
        var dbName = Guid.NewGuid().ToString();
        var org    = Guid.NewGuid();
        var rival  = New(org, dbName);
        await Receivables.Seed(rival.Db,
            Receivables.Invoice(org, Customer, "SINV-A", new DateTime(2026, 9, 1), 1000m),
            Receivables.Invoice(org, Customer, "SINV-B", new DateTime(2026, 9, 5), 1000m));
        var p1 = await Pay(rival, 1000m, manual: []);
        var p2 = await Pay(rival, 1000m, manual: []);

        var race = new LoseTheRaceOnce(db => db.ChangeTracker.Entries<PaymentAllocation>().Any(e => e.State == EntityState.Added),
            async () => await rival.Service.AllocateAsync(p2.PaymentUuid, null, User), asConcurrencyConflict: true);
        var h = New(org, dbName, race);

        var result = await h.Service.AllocateAsync(p1.PaymentUuid, null, User);

        race.Fired.Should().BeTrue();
        result.Allocations.Should().ContainSingle().Which.InvoiceNumber.Should().Be("SINV-B", "A was paid by the other payment in the meantime");

        var a = await Inv(h, "SINV-A");
        var b = await Inv(h, "SINV-B");
        (a.AmountPaid, a.BalanceDue, a.Status).Should().Be((1000m, 0m, "PAID"));
        (b.AmountPaid, b.BalanceDue, b.Status).Should().Be((1000m, 0m, "PAID"));
    }

    [Fact]
    public async Task A_persistent_failure_is_surfaced_and_leaves_nothing_tracked()
    {
        var dbName = Guid.NewGuid().ToString();
        var org    = Guid.NewGuid();
        var seed   = New(org, dbName);
        await ThreeInvoices(seed);
        var payment = await Pay(seed, 1000m, manual: []);
        var refusing = new AlwaysFail();
        var h = New(org, dbName, refusing);

        var act = async () => await h.Service.AllocateAsync(payment.PaymentUuid, null, User);

        (await act.Should().ThrowAsync<DbUpdateException>()).WithMessage("*refusing writes*");
        refusing.Attempts.Should().Be(5);
        h.Db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).Should().BeEmpty();
        (await Inv(seed, "SINV-A")).AmountPaid.Should().Be(0m);
    }

    // ── Get ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_payment_is_returned_with_what_it_was_applied_to_and_where_those_invoices_stand_now()
    {
        var h = New();
        await ThreeInvoices(h);
        var recorded = await Pay(h, 1500m, "CHEQUE", date: new DateTime(2026, 9, 18), cheque: "004521", bankRef: "BR-1");

        var detail = await h.Service.GetAsync(recorded.PaymentUuid);

        detail.Should().NotBeNull();
        (detail!.PaymentNumber, detail.PartnerId, detail.PartnerName, detail.Amount, detail.PaymentMethod, detail.Status, detail.CurrencyCode)
            .Should().Be((recorded.PaymentNumber, Customer, "Acme Ltd", 1500m, "CHEQUE", "RECEIVED", "PKR"));
        (detail.ChequeNumber, detail.BankReference, detail.PaymentDate).Should().Be(("004521", "BR-1", new DateTime(2026, 9, 18)));
        (detail.AllocatedAmount, detail.UnallocatedAmount).Should().Be((1500m, 0m));
        detail.CreatedBy.Should().Be(User);

        detail.Allocations.Select(a => (a.InvoiceNumber, a.AllocatedAmount, a.InvoiceBalanceDue, a.InvoiceStatus)).Should().Equal(
            ("SINV-A", 1000m, 0m, "PAID"), ("SINV-B", 500m, 1500m, "PARTIALLY_PAID"));
    }

    [Fact]
    public async Task An_unknown_or_another_organizations_payment_is_not_returned()
    {
        var h = New();
        await ThreeInvoices(h);
        var recorded = await Pay(h, 100m);
        var stranger = New(Guid.NewGuid(), h.DbName);

        (await h.Service.GetAsync(Guid.NewGuid())).Should().BeNull();
        (await stranger.Service.GetAsync(recorded.PaymentUuid)).Should().BeNull();
    }

    // ── List ─────────────────────────────────────────────────────────────────

    private static async Task<H> ListFixture()
    {
        var h = New();
        await ThreeInvoices(h);
        var other = Guid.NewGuid();
        await Pay(h, 500m,  "CASH",          date: new DateTime(2026, 9, 3));                                   // 0001 applied
        await Pay(h, 800m,  "CHEQUE",        date: new DateTime(2026, 9, 8), cheque: "CHQ-77", manual: []);      // 0002 on account
        await Pay(h, 300m,  "BANK_TRANSFER", date: new DateTime(2026, 9, 12), bankRef: "TRX-9001");             // 0003 applied
        await Pay(h, 90m,   "CARD",          date: new DateTime(2026, 9, 15), partner: other, currency: "USD"); // 0004 nothing to apply to
        return h;
    }

    [Fact]
    public async Task Payments_are_listed_newest_first_with_what_is_applied_and_what_is_on_account()
    {
        var h = await ListFixture();

        var page = await h.Service.ListAsync(new CustomerPaymentFilter());

        page.Data.Select(p => (p.PaymentMethod, p.Amount, p.AllocatedAmount, p.UnallocatedAmount)).Should().Equal(
            ("CARD", 90m, 0m, 90m),
            ("BANK_TRANSFER", 300m, 300m, 0m),
            ("CHEQUE", 800m, 0m, 800m),
            ("CASH", 500m, 500m, 0m));
        page.TotalRecords.Should().Be(4);
        page.Data.Single(p => p.PaymentMethod == "CHEQUE").ChequeNumber.Should().Be("CHQ-77");
    }

    [Fact]
    public async Task The_list_filters_by_customer_status_and_method()
    {
        var h = await ListFixture();

        (await h.Service.ListAsync(new CustomerPaymentFilter { PartnerId = Customer })).TotalRecords.Should().Be(3);
        (await h.Service.ListAsync(new CustomerPaymentFilter { Status = "received" })).TotalRecords.Should().Be(4);
        (await h.Service.ListAsync(new CustomerPaymentFilter { Status = "BOUNCED" })).TotalRecords.Should().Be(0);
        (await h.Service.ListAsync(new CustomerPaymentFilter { Method = "cheque" })).Data.Should().ContainSingle().Which.PaymentMethod.Should().Be("CHEQUE");
    }

    [Fact]
    public async Task Unallocated_keeps_only_payments_with_money_still_on_account()
    {
        var h = await ListFixture();

        var onAccount = await h.Service.ListAsync(new CustomerPaymentFilter { Unallocated = true });
        onAccount.Data.Select(p => p.PaymentMethod).Should().Equal("CARD", "CHEQUE");

        var applied = await h.Service.ListAsync(new CustomerPaymentFilter { Unallocated = false });
        applied.Data.Select(p => p.PaymentMethod).Should().Equal("BANK_TRANSFER", "CASH");
    }

    [Fact]
    public async Task A_bounced_payment_is_not_money_on_account_however_little_of_it_was_applied()
    {
        var h = New();
        await ThreeInvoices(h);
        var recorded = await Pay(h, 800m, manual: []);
        var stored = await h.Db.CustomerPayments.SingleAsync(p => p.UUID == recorded.PaymentUuid);
        stored.Status = "BOUNCED";
        await h.Db.SaveChangesAsync();

        (await h.Service.ListAsync(new CustomerPaymentFilter { Unallocated = true })).TotalRecords.Should().Be(0);
    }

    [Fact]
    public async Task The_payment_date_range_is_whole_days_inclusive_at_both_ends()
    {
        var h = await ListFixture();

        var page = await h.Service.ListAsync(new CustomerPaymentFilter
        {
            DateFrom = new DateTime(2026, 9, 8, 22, 0, 0), DateTo = new DateTime(2026, 9, 12, 1, 0, 0)
        });

        page.Data.Select(p => p.PaymentMethod).Should().Equal("BANK_TRANSFER", "CHEQUE");
    }

    [Theory]
    [InlineData("chq-77", 1)]          // cheque number
    [InlineData("trx-9001", 1)]        // bank reference
    [InlineData("cpay-", 4)]           // payment number
    [InlineData("acme", 4)]            // customer name
    [InlineData("nothing", 0)]
    public async Task Search_matches_the_number_the_cheque_the_bank_reference_or_the_customer(string search, int expected)
    {
        var h = await ListFixture();

        (await h.Service.ListAsync(new CustomerPaymentFilter { Search = search })).TotalRecords.Should().Be(expected);
    }

    [Fact]
    public async Task An_unknown_status_or_method_or_a_reversed_range_is_refused()
    {
        var h = New();

        await ((Func<Task>)(() => h.Service.ListAsync(new CustomerPaymentFilter { Status = "SETTLED" }))).Should().ThrowAsync<BadRequestException>();
        await ((Func<Task>)(() => h.Service.ListAsync(new CustomerPaymentFilter { Method = "WIRE" }))).Should().ThrowAsync<BadRequestException>();
        (await ((Func<Task>)(() => h.Service.ListAsync(new CustomerPaymentFilter { DateFrom = new DateTime(2026, 9, 20), DateTo = new DateTime(2026, 9, 1) })))
            .Should().ThrowAsync<BadRequestException>()).WithMessage("*payment list*");
    }

    [Fact]
    public async Task The_list_pages_and_never_shows_another_organizations_payments()
    {
        var h = New();
        await Receivables.Seed(h.Db, Receivables.Invoice(h.Org, Customer, "SINV-A", new DateTime(2026, 9, 1), 100000m));
        for (var i = 0; i < 25; i++) await Pay(h, 10m);

        var p3 = await h.Service.ListAsync(new CustomerPaymentFilter { Page = 3, PageSize = 10 });
        (p3.Data.Count, p3.TotalRecords, p3.TotalPages, p3.HasNext).Should().Be((5, 25, 3, false));

        var stranger = New(Guid.NewGuid(), h.DbName);
        (await stranger.Service.ListAsync(new CustomerPaymentFilter())).TotalRecords.Should().Be(0);
    }

    [Fact]
    public async Task Reading_payments_tracks_nothing()
    {
        var h = await ListFixture();
        h.Db.ChangeTracker.Clear();

        await h.Service.ListAsync(new CustomerPaymentFilter());
        var any = (await h.Service.ListAsync(new CustomerPaymentFilter())).Data.First();
        await h.Service.GetAsync(any.Uuid);

        h.Db.ChangeTracker.Entries().Should().BeEmpty();
    }
}
