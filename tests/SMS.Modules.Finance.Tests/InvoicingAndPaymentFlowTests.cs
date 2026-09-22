using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Exceptions;
using Xunit;

using static SMS.Modules.Finance.Tests.ReceivablesDesk;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-10 §9, §10 — the sales side end to end, one test per thing the task lists, driven through the
/// real invoice service, payment service, ledger and overdue job over one shared database. The deeper
/// rule-by-rule tests live beside each service (<c>SalesInvoiceServiceTests</c>,
/// <c>CustomerPaymentServiceTests</c>, <c>CustomerLedgerServiceTests</c>, <c>InvoiceOverdueJobTests</c>);
/// these prove the services agree with each other.
/// </summary>
public class InvoicingAndPaymentFlowTests
{
    private const int User = ReceivablesDesk.User;

    private static (ReceivablesWorld World, ReceivablesDesk Desk, Guid Customer) Setup()
    {
        var world = new ReceivablesWorld();
        var desk  = world.For(Guid.NewGuid());
        return (world, desk, desk.NewCustomer());
    }

    private static void At(ReceivablesWorld world, int month, int day) =>
        world.Clock.Value = new DateTime(2026, month, day, 10, 30, 0, DateTimeKind.Utc);

    // ── Invoice only against delivered quantity ──────────────────────────────

    [Fact]
    public async Task An_invoice_bills_what_was_delivered_at_the_orders_price_not_what_was_ordered()
    {
        var (_, desk, customer) = Setup();
        var order    = await desk.PlaceOrderAsync(customer, new OrderLine(Qty: 100m, Price: 40m, Fulfilled: 60m));
        var delivery = desk.Deliver(order, "DELIVERED", (0, 60m));

        await using var scope = desk.Scope();
        var created = await scope.Invoices.CreateFromFulfillmentAsync(delivery, User);

        created.GrandTotal.Should().Be(2400m, "60 delivered at 40, not the 100 ordered");
        var invoice = await scope.Invoices.GetAsync(created.InvoiceUuid);
        invoice!.Lines.Should().ContainSingle().Which.Quantity.Should().Be(60m);
    }

    [Fact]
    public async Task Goods_that_have_not_reached_the_customer_cannot_be_invoiced()
    {
        var (_, desk, customer) = Setup();
        var order    = await desk.PlaceOrderAsync(customer, new OrderLine(10m));
        var delivery = desk.Deliver(order, "IN_TRANSIT", (0, 10m));

        await using var scope = desk.Scope();
        var act = () => scope.Invoices.CreateFromFulfillmentAsync(delivery, User);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("IN_TRANSIT");
        (await desk.BooksAsync(customer)).Invoices.Should().BeEmpty();
    }

    [Fact]
    public async Task A_delivery_claiming_more_than_the_order_has_delivered_is_refused()
    {
        var (_, desk, customer) = Setup();
        var order = await desk.PlaceOrderAsync(customer, new OrderLine(Qty: 100m, Fulfilled: 60m));

        await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 60m)));
        var stale = desk.Deliver(order, "DELIVERED", (0, 50m));

        await using var scope = desk.Scope();
        var act = () => scope.Invoices.CreateFromFulfillmentAsync(stale, User);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should()
            .Contain("above the 60 delivered", "the order's delivered quantity is the ceiling across every invoice");
        (await desk.BooksAsync(customer)).Invoices.Should().ContainSingle();
    }

    [Fact]
    public async Task A_delivery_with_nothing_delivered_on_it_has_nothing_to_invoice()
    {
        var (_, desk, customer) = Setup();
        var order    = await desk.PlaceOrderAsync(customer, new OrderLine(10m));
        var delivery = desk.Deliver(order, "DELIVERED", (0, 0m));

        await using var scope = desk.Scope();
        var act = () => scope.Invoices.CreateFromFulfillmentAsync(delivery, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── Partial invoicing ────────────────────────────────────────────────────

    [Fact]
    public async Task An_order_delivered_in_three_lots_is_invoiced_three_times_and_never_beyond_the_order()
    {
        var (_, desk, customer) = Setup();
        var order = await desk.PlaceOrderAsync(customer, new OrderLine(Qty: 100m, Price: 10m));

        var lots     = new[] { 40m, 35m, 25m };
        var invoices = new List<Invoiced>();
        foreach (var lot in lots)
            invoices.Add(await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, lot))));

        invoices.Select(i => i.Amount).Should().Equal(400m, 350m, 250m);
        invoices.Select(i => i.Number).Should().OnlyHaveUniqueItems();

        var books = await desk.BooksAsync(customer);
        books.Invoices.Should().HaveCount(3);
        books.Invoices.Select(i => i.TraceId).Distinct().Should().ContainSingle("one trace spans SO → delivery → invoice → payment");
        books.Invoices.Select(i => i.SaleOrderUuid).Distinct().Should().ContainSingle().Which.Should().Be(order.Uuid);
        books.Ledger.Select(e => e.DebitAmount).Should().Equal(400m, 350m, 250m);
        (await desk.OwesAsync(customer)).Should().Be(1000m);

        var overrun = desk.Deliver(order, "DELIVERED", (0, 1m));
        await using var scope = desk.Scope();
        var act = () => scope.Invoices.CreateFromFulfillmentAsync(overrun, User);
        await act.Should().ThrowAsync<BadRequestException>("the hundred are billed; a hundred-and-first is not");
        (await desk.BooksAsync(customer)).Invoices.Should().HaveCount(3);
    }

    [Fact]
    public async Task Invoicing_the_same_delivery_twice_returns_the_first_invoice_rather_than_billing_again()
    {
        var (_, desk, customer) = Setup();
        var order    = await desk.PlaceOrderAsync(customer, new OrderLine(10m));
        var delivery = desk.Deliver(order, "DELIVERED", (0, 10m));

        await using var scope = desk.Scope();
        var first  = await scope.Invoices.CreateFromFulfillmentAsync(delivery, User);
        var second = await scope.Invoices.CreateFromFulfillmentAsync(delivery, User);

        second.AlreadyExisted.Should().BeTrue();
        second.InvoiceUuid.Should().Be(first.InvoiceUuid);
        (await desk.BooksAsync(customer)).Invoices.Should().ContainSingle();
    }

    [Fact]
    public async Task A_draft_books_no_receivable_and_issuing_it_debits_the_ledger_for_the_grand_total()
    {
        var (_, desk, customer) = Setup();
        var order = await desk.PlaceOrderAsync(customer, new OrderLine(10m, 25m));

        await using var scope = desk.Scope();
        var created = await scope.Invoices.CreateFromFulfillmentAsync(desk.Deliver(order, "DELIVERED", (0, 10m)), User);

        (await desk.BooksAsync(customer)).Ledger.Should().BeEmpty("a draft has booked no receivable");

        var issued = await scope.Invoices.IssueAsync(created.InvoiceUuid, User);

        issued.PartnerBalance.Should().Be(250m);
        var books = await desk.BooksAsync(customer);
        books.Invoices.Single().Status.Should().Be("ISSUED");
        books.Ledger.Should().ContainSingle().Which.Should().Match<CustomerLedgerEntry>(
            e => e.EntryType == "INVOICE" && e.DebitAmount == 250m && e.ReferenceId == created.InvoiceUuid);
    }

    // ── Payment auto-allocation, FIFO ────────────────────────────────────────

    [Fact]
    public async Task A_payment_settles_the_oldest_invoice_first_by_date_not_by_when_it_was_keyed()
    {
        var (world, desk, customer) = Setup();

        At(world, 9, 25);
        var newer = await desk.InvoiceAsync(customer, 300m);        // dated 25 Sep, keyed first
        At(world, 9, 10);
        var older = await desk.InvoiceAsync(customer, 200m);        // dated 10 Sep, keyed second
        At(world, 9, 26);

        var payment = await desk.PayAsync(customer, 250m);

        payment.Allocations.Select(a => (a.InvoiceNumber, a.Amount, a.BalanceDue, a.InvoiceStatus)).Should().Equal(
            (older.Number, 200m, 0m,  "PAID"),
            (newer.Number, 50m,  250m, "PARTIALLY_PAID"));
        payment.UnallocatedAmount.Should().Be(0m);
    }

    [Fact]
    public async Task Money_beyond_every_open_invoice_stays_on_the_account_and_is_never_spilled_elsewhere()
    {
        var (world, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 400m);

        var payment = await desk.PayAsync(customer, 1000m);

        payment.AllocatedAmount.Should().Be(400m);
        payment.UnallocatedAmount.Should().Be(600m);
        payment.PartnerBalance.Should().Be(-600m, "the customer is 600 in credit");

        var books = await desk.BooksAsync(customer);
        books.OnAccount.Should().Be(600m);
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_issued_invoices_of_the_same_customer_and_currency_are_candidates_for_fifo()
    {
        var (world, desk, customer) = Setup();
        var other = desk.NewCustomer("Other Ltd");

        var mine   = await desk.InvoiceAsync(customer, 100m);
        var theirs = await desk.InvoiceAsync(other, 100m);

        // A draft: raised but never issued.
        var order = await desk.PlaceOrderAsync(customer, new OrderLine(1m, 999m));
        await using (var scope = desk.Scope())
            await scope.Invoices.CreateFromFulfillmentAsync(desk.Deliver(order, "DELIVERED", (0, 1m)), User);

        var payment = await desk.PayAsync(customer, 500m);

        payment.Allocations.Should().ContainSingle().Which.InvoiceUuid.Should().Be(mine.Uuid);
        (await desk.BooksAsync(other)).Invoice(theirs.Uuid).Status.Should().Be("ISSUED");
        (await desk.BooksAsync(customer)).Invoices.Single(i => i.Status == "DRAFT").AmountPaid.Should().Be(0m);
    }

    // ── Payment manual allocation ────────────────────────────────────────────

    [Fact]
    public async Task A_manual_allocation_pays_exactly_the_invoices_named_and_leaves_the_rest_on_account()
    {
        var (world, desk, customer) = Setup();
        var oldest = await desk.InvoiceAsync(customer, 300m);
        var middle = await desk.InvoiceAsync(customer, 300m);
        var newest = await desk.InvoiceAsync(customer, 300m);

        var payment = await desk.PayAsync(customer, 500m, allocations:
        [
            new ManualPaymentAllocation(newest.Uuid, 300m),
            new ManualPaymentAllocation(middle.Uuid, 100m)
        ]);

        payment.AllocatedAmount.Should().Be(400m);
        payment.UnallocatedAmount.Should().Be(100m, "what the override does not name is not spilled onto the oldest invoice");

        var books = await desk.BooksAsync(customer);
        books.Invoice(oldest.Uuid).Status.Should().Be("ISSUED");
        books.Invoice(middle.Uuid).Status.Should().Be("PARTIALLY_PAID");
        books.Invoice(newest.Uuid).Status.Should().Be("PAID");
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    [Fact]
    public async Task What_a_manual_allocation_left_on_account_can_be_applied_later_and_the_ledger_does_not_move()
    {
        var (world, desk, customer) = Setup();
        var a = await desk.InvoiceAsync(customer, 300m);
        var b = await desk.InvoiceAsync(customer, 300m);

        var payment = await desk.PayAsync(customer, 500m, allocations: [new ManualPaymentAllocation(b.Uuid, 100m)]);
        var ledgerBefore = (await desk.BooksAsync(customer)).Ledger.Count;

        await using var scope = desk.Scope();
        var allocated = await scope.Payments.AllocateAsync(payment.PaymentUuid, null, User);

        allocated.Allocations.Select(x => (x.InvoiceNumber, x.Amount)).Should().Equal((a.Number, 300m), (b.Number, 100m));
        allocated.UnallocatedAmount.Should().Be(0m);

        var books = await desk.BooksAsync(customer);
        books.Ledger.Should().HaveCount(ledgerBefore, "moving money from on-account to an invoice changes nothing the customer owes");
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    [Fact]
    public async Task A_manual_allocation_beyond_an_invoices_balance_is_refused_and_nothing_is_recorded()
    {
        var (_, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 300m);
        var before  = (await desk.BooksAsync(customer)).Fingerprint();

        var act = () => desk.PayAsync(customer, 500m, allocations: [new ManualPaymentAllocation(invoice.Uuid, 301m)]);

        await act.Should().ThrowAsync<BadRequestException>();
        (await desk.BooksAsync(customer)).Fingerprint().Should().Be(before);
    }

    // ── Ledger running balance accuracy ──────────────────────────────────────

    [Fact]
    public async Task The_ledger_running_balance_is_right_at_every_step_of_a_realistic_month()
    {
        var (world, desk, customer) = Setup();

        await desk.InvoiceAsync(customer, 1000m);                                               // owes 1000
        await desk.InvoiceAsync(customer, 500.50m);                                             // owes 1500.50
        var cheque = await desk.PayAsync(customer, 300m, "CHEQUE");                             // owes 1200.50
        await desk.PayAsync(customer, 1200.50m);                                                // owes 0
        await desk.PayAsync(customer, 75.25m);                                                  // in credit 75.25
        await desk.BounceAsync(cheque.PaymentUuid, "Insufficient funds");                       // owes 224.75
        await desk.InvoiceAsync(customer, 100m);                                                // owes 324.75

        var books = await desk.BooksAsync(customer);

        books.Ledger.Select(e => (e.SequenceNo, e.EntryType, e.DebitAmount, e.CreditAmount, e.RunningBalance)).Should().Equal(
            (1, "INVOICE", 1000m,   0m,       1000m),
            (2, "INVOICE", 500.50m, 0m,       1500.50m),
            (3, "PAYMENT", 0m,      300m,     1200.50m),
            (4, "PAYMENT", 0m,      1200.50m, 0m),
            (5, "PAYMENT", 0m,      75.25m,   -75.25m),
            (6, "PAYMENT", 300m,    0m,       224.75m),
            (7, "INVOICE", 100m,    0m,       324.75m));

        // Every balance is the one before, plus debit, less credit — checked independently of the literals above.
        books.Ledger.Zip(books.Ledger.Skip(1), (prev, next) => next.RunningBalance - (prev.RunningBalance + next.DebitAmount - next.CreditAmount))
            .Should().OnlyContain(gap => gap == 0m);

        // And it agrees with the documents: 1600.50 billed, less the 1275.75 that is still good.
        (await desk.OwesAsync(customer)).Should().Be(324.75m);
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    [Fact]
    public async Task A_date_range_or_a_page_of_the_ledger_shows_the_balances_as_they_were_not_recomputed()
    {
        var (world, desk, customer) = Setup();

        At(world, 9, 1);  await desk.InvoiceAsync(customer, 1000m);
        At(world, 9, 10); await desk.PayAsync(customer, 400m);
        At(world, 9, 20); await desk.InvoiceAsync(customer, 250m);
        At(world, 9, 30); await desk.PayAsync(customer, 100m);

        await using var scope = desk.Scope();

        var september20To30 = await scope.Ledger.GetLedgerAsync(customer, new CustomerLedgerFilter
        {
            DateFrom = new DateTime(2026, 9, 20), DateTo = new DateTime(2026, 9, 30)
        });
        // Newest first, and the balance carried forward from before the range is still in it.
        september20To30.Data.Select(e => (e.SequenceNo, e.RunningBalance)).Should().Equal((4, 750m), (3, 850m));

        var firstPage = await scope.Ledger.GetLedgerAsync(customer, new CustomerLedgerFilter { Page = 1, PageSize = 3 });
        var lastPage  = await scope.Ledger.GetLedgerAsync(customer, new CustomerLedgerFilter { Page = 2, PageSize = 3 });

        firstPage.TotalRecords.Should().Be(4);
        firstPage.Data.Select(e => e.SequenceNo).Should().Equal(4, 3, 2);
        lastPage.Data.Select(e => (e.SequenceNo, e.RunningBalance)).Should().Equal((1, 1000m));
    }

    // ── Overdue detection ────────────────────────────────────────────────────

    [Fact]
    public async Task The_sweep_flags_what_is_past_due_and_still_owing_and_nothing_else()
    {
        var (world, desk, customer) = Setup();

        var unpaid  = await desk.InvoiceAsync(customer, 1000m);                                  // due 20 Oct
        var partial = await desk.InvoiceAsync(customer, 800m);
        var settled = await desk.InvoiceAsync(customer, 500m);
        await desk.PayAsync(customer, 300m, allocations: [new ManualPaymentAllocation(partial.Uuid, 300m)]);
        await desk.PayAsync(customer, 500m, allocations: [new ManualPaymentAllocation(settled.Uuid, 500m)]);
        var draftOrder = await desk.PlaceOrderAsync(customer, new OrderLine(1m, 50m));
        await using (var scope = desk.Scope())
            await scope.Invoices.CreateFromFulfillmentAsync(desk.Deliver(draftOrder, "DELIVERED", (0, 1m)), User);

        At(world, 10, 20);      // the due date itself
        (await world.SweepOverdueAsync()).Should().Be(0, "the customer has the whole of the due date");

        At(world, 10, 21);
        (await world.SweepOverdueAsync()).Should().Be(2);

        var books = await desk.BooksAsync(customer);
        books.Invoice(unpaid.Uuid).Status.Should().Be("OVERDUE");
        books.Invoice(partial.Uuid).Status.Should().Be("OVERDUE");
        books.Invoice(settled.Uuid).Status.Should().Be("PAID");
        books.Invoices.Single(i => i.Status == "DRAFT").Status.Should().Be("DRAFT");

        (await world.SweepOverdueAsync()).Should().Be(0, "a second run the same day has nothing left to flag");
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date, afterOverdueSweep: true).Should().BeEmpty();
    }

    [Fact]
    public async Task Flagging_an_invoice_overdue_moves_no_money()
    {
        var (world, desk, customer) = Setup();
        await desk.InvoiceAsync(customer, 1000m);
        await desk.PayAsync(customer, 250m);
        var before = await desk.BooksAsync(customer);

        At(world, 11, 1);
        await world.SweepOverdueAsync();

        var after = await desk.BooksAsync(customer);
        var entries = (Books b) => b.Ledger.Select(e => (e.UUID, e.SequenceNo, e.DebitAmount, e.CreditAmount, e.RunningBalance));
        var payments = (Books b) => b.Payments.Select(p => (p.UUID, p.Status, p.ModifiedDate, Applied: p.Allocations.Sum(a => a.AllocatedAmount)));

        entries(after).Should().Equal(entries(before), "the sweep writes no ledger entry");
        payments(after).Should().Equal(payments(before));
        after.Invoices.Single().Status.Should().Be("OVERDUE");
        after.Invoices.Single().AmountPaid.Should().Be(250m);
        after.Invoices.Single().BalanceDue.Should().Be(750m);
    }

    [Fact]
    public async Task Paying_an_overdue_invoice_in_full_settles_it_and_a_later_sweep_leaves_it_alone()
    {
        var (world, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);

        At(world, 11, 1);
        await world.SweepOverdueAsync();
        (await desk.BooksAsync(customer)).Invoice(invoice.Uuid).Status.Should().Be("OVERDUE");

        var payment = await desk.PayAsync(customer, 1000m);

        payment.Allocations.Single().InvoiceStatus.Should().Be("PAID");
        At(world, 11, 2);
        (await world.SweepOverdueAsync()).Should().Be(0);
        (await desk.BooksAsync(customer)).Invoice(invoice.Uuid).Status.Should().Be("PAID");
    }

    [Fact]
    public async Task A_part_payment_on_an_overdue_invoice_is_flagged_again_by_the_next_sweep()
    {
        var (world, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);

        At(world, 11, 1);
        await world.SweepOverdueAsync();
        await desk.PayAsync(customer, 100m);
        (await desk.BooksAsync(customer)).Invoice(invoice.Uuid).Status.Should().Be("PARTIALLY_PAID");

        At(world, 11, 2);
        (await world.SweepOverdueAsync()).Should().Be(1, "900 is still owing and the due date is long past");
        (await desk.BooksAsync(customer)).Invoice(invoice.Uuid).Status.Should().Be("OVERDUE");
    }

    // ── The whole life of a receivable ───────────────────────────────────────

    [Fact]
    public async Task Delivered_invoiced_part_paid_by_a_cheque_that_bounces_overdue_then_finally_settled()
    {
        var (world, desk, customer) = Setup();

        var order    = await desk.PlaceOrderAsync(customer, new OrderLine(Qty: 50m, Price: 20m));
        var invoiced = await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 50m)));
        invoiced.Amount.Should().Be(1000m);

        var cheque = await desk.PayAsync(customer, 600m, "CHEQUE");
        (await desk.BooksAsync(customer)).Invoice(invoiced.Uuid).Status.Should().Be("PARTIALLY_PAID");

        At(world, 10, 5);
        await desk.BounceAsync(cheque.PaymentUuid, "Stopped by drawer");
        (await desk.BooksAsync(customer)).Invoice(invoiced.Uuid).Status.Should().Be("ISSUED");

        At(world, 10, 21);
        await world.SweepOverdueAsync();
        (await desk.BooksAsync(customer)).Invoice(invoiced.Uuid).Status.Should().Be("OVERDUE");

        await desk.PayAsync(customer, 1000m, "BANK_TRANSFER");

        var books = await desk.BooksAsync(customer);
        books.Invoice(invoiced.Uuid).Status.Should().Be("PAID");
        (await desk.OwesAsync(customer)).Should().Be(0m);
        books.Payments.Select(p => p.Status).Should().Equal("BOUNCED", "RECEIVED");
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }
}
