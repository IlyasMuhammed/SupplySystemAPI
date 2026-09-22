using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-10 §9.3/§9.5/§10 — a cheque the bank sends back. The receivable stands again: every invoice
/// the cheque paid is reopened, the payment is BOUNCED, and the customer's ledger is debited with the
/// full amount, offsetting the credit booked when the cheque was received — all in one transaction.
/// </summary>
public class CustomerPaymentBounceTests
{
    private const int User = ReceivablesDesk.User;

    private static (ReceivablesWorld World, ReceivablesDesk Desk, Guid Customer) Setup()
    {
        var world = new ReceivablesWorld();
        var desk  = world.For(Guid.NewGuid());
        return (world, desk, desk.NewCustomer());
    }

    /// <summary>Records every save's contents, to prove the bounce is one unit of work.</summary>
    private sealed class SaveRecorder : SaveChangesInterceptor
    {
        public List<(int Payments, int Invoices, int LedgerEntries)> Saves { get; } = [];

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToList();
            Saves.Add((
                entries.Count(e => e.Entity is CustomerPayment && e.State == EntityState.Modified),
                entries.Count(e => e.Entity is SalesInvoice && e.State == EntityState.Modified),
                entries.Count(e => e.Entity is CustomerLedgerEntry && e.State == EntityState.Added)));
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    // ── What a bounce does ───────────────────────────────────────────────────

    [Fact]
    public async Task A_bounced_cheque_reopens_the_invoice_it_paid_and_puts_the_debt_back_on_the_ledger()
    {
        var (world, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);
        var payment = await desk.PayAsync(customer, 1000m, "CHEQUE", chequeNumber: "CHQ-1001");

        (await desk.BooksAsync(customer)).Invoice(invoice.Uuid).Status.Should().Be("PAID");
        (await desk.OwesAsync(customer)).Should().Be(0m);

        var bounced = await desk.BounceAsync(payment.PaymentUuid, "Insufficient funds");

        bounced.Amount.Should().Be(1000m);
        bounced.PartnerBalance.Should().Be(1000m);
        bounced.Reversed.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new ReversedPaymentAllocation(invoice.Uuid, invoice.Number, 1000m, 1000m, "ISSUED"));

        var books = await desk.BooksAsync(customer);

        var reopened = books.Invoice(invoice.Uuid);
        reopened.Status.Should().Be("ISSUED");
        reopened.AmountPaid.Should().Be(0m);
        reopened.BalanceDue.Should().Be(1000m);
        reopened.ModifiedBy.Should().Be(User);

        var stored = books.Payment(payment.PaymentUuid);
        stored.Status.Should().Be("BOUNCED");
        stored.Notes.Should().Contain("Insufficient funds");
        stored.ModifiedBy.Should().Be(User);
        stored.Allocations.Should().ContainSingle("the allocation is kept, so the history says what the cheque once paid");

        books.Ledger.Select(e => (e.SequenceNo, e.EntryType, e.DebitAmount, e.CreditAmount, e.RunningBalance)).Should().Equal(
            (1, "INVOICE", 1000m, 0m,    1000m),
            (2, "PAYMENT", 0m,    1000m, 0m),
            (3, "PAYMENT", 1000m, 0m,    1000m));

        var reversal = books.Ledger[2];
        reversal.ReferenceType.Should().Be("CustomerPayment");
        reversal.ReferenceId.Should().Be(payment.PaymentUuid);
        reversal.ReferenceNumber.Should().Be(payment.PaymentNumber);
        reversal.CurrencyCode.Should().Be("PKR");
        reversal.Narration.Should().Contain("CHQ-1001").And.Contain("bounced").And.Contain("Insufficient funds");
        reversal.CreatedBy.Should().Be(User);

        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    [Fact]
    public async Task A_cheque_that_paid_two_invoices_reopens_both_and_leaves_other_payments_alone()
    {
        var (world, desk, customer) = Setup();
        var a = await desk.InvoiceAsync(customer, 600m);
        var b = await desk.InvoiceAsync(customer, 500m);

        await desk.PayAsync(customer, 200m, "BANK_TRANSFER");                 // A: 200 paid
        var cheque = await desk.PayAsync(customer, 700m, "CHEQUE");            // A: +400 (PAID), B: +300

        var before = await desk.BooksAsync(customer);
        before.Invoice(a.Uuid).Status.Should().Be("PAID");
        before.Invoice(b.Uuid).Status.Should().Be("PARTIALLY_PAID");

        var bounced = await desk.BounceAsync(cheque.PaymentUuid);

        bounced.Reversed.Select(r => (r.InvoiceNumber, r.Amount, r.BalanceDue, r.InvoiceStatus)).Should().Equal(
            (a.Number, 400m, 400m, "PARTIALLY_PAID"),
            (b.Number, 300m, 500m, "ISSUED"));
        bounced.PartnerBalance.Should().Be(900m, "1100 billed, of which only the 200 bank transfer is still good");

        var books = await desk.BooksAsync(customer);
        books.Invoice(a.Uuid).AmountPaid.Should().Be(200m, "the bank transfer still covers part of A");
        books.Invoice(b.Uuid).AmountPaid.Should().Be(0m);
        books.Payments.Single(p => p.PaymentMethod == "BANK_TRANSFER").Status.Should().Be("RECEIVED");

        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    [Fact]
    public async Task A_cheque_held_on_account_bounces_without_touching_any_invoice()
    {
        var (world, desk, customer) = Setup();
        var cheque = await desk.PayAsync(customer, 300m, "CHEQUE");

        cheque.AllocatedAmount.Should().Be(0m);
        (await desk.OwesAsync(customer)).Should().Be(-300m, "the customer is in credit while the cheque stands");

        var bounced = await desk.BounceAsync(cheque.PaymentUuid);

        bounced.Reversed.Should().BeEmpty();
        bounced.PartnerBalance.Should().Be(0m);

        var books = await desk.BooksAsync(customer);
        books.Ledger.Select(e => (e.DebitAmount, e.CreditAmount, e.RunningBalance)).Should().Equal(
            (0m, 300m, -300m),
            (300m, 0m, 0m));
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    [Fact]
    public async Task A_partly_applied_cheque_debits_the_ledger_for_all_of_it_not_only_what_was_applied()
    {
        var (world, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);
        var cheque  = await desk.PayAsync(customer, 1000m, "CHEQUE", [new ManualPaymentAllocation(invoice.Uuid, 400m)]);

        cheque.UnallocatedAmount.Should().Be(600m);
        (await desk.OwesAsync(customer)).Should().Be(0m, "600 on account and 400 applied: nothing is owed");

        var bounced = await desk.BounceAsync(cheque.PaymentUuid);

        bounced.Reversed.Should().ContainSingle().Which.Amount.Should().Be(400m);
        bounced.PartnerBalance.Should().Be(1000m);

        var books = await desk.BooksAsync(customer);
        books.Ledger[^1].DebitAmount.Should().Be(1000m);
        books.Invoice(invoice.Uuid).BalanceDue.Should().Be(1000m);
        books.OnAccount.Should().Be(0m, "a bounced cheque leaves nothing on account");
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    [Fact]
    public async Task An_invoice_already_past_its_due_date_is_reopened_as_overdue_not_issued()
    {
        var (world, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);       // dated 20 Sep, due 20 Oct
        var cheque  = await desk.PayAsync(customer, 1000m, "CHEQUE");

        world.Clock.Value = new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

        var bounced = await desk.BounceAsync(cheque.PaymentUuid);

        bounced.Reversed.Single().InvoiceStatus.Should().Be("OVERDUE");
        var books = await desk.BooksAsync(customer);
        books.Invoice(invoice.Uuid).Status.Should().Be("OVERDUE");
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    [Fact]
    public async Task An_invoice_due_today_is_still_not_overdue_when_its_cheque_bounces()
    {
        var (world, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);
        var cheque  = await desk.PayAsync(customer, 1000m, "CHEQUE");

        world.Clock.Value = new DateTime(2026, 10, 20, 23, 0, 0, DateTimeKind.Utc);    // the due date itself

        await desk.BounceAsync(cheque.PaymentUuid);

        (await desk.BooksAsync(customer)).Invoice(invoice.Uuid).Status.Should().Be("ISSUED",
            "the customer has the whole of the due date, exactly as the overdue job allows");
    }

    [Fact]
    public async Task The_receivable_stands_again_so_a_new_payment_settles_the_reopened_invoice()
    {
        var (world, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);
        var cheque  = await desk.PayAsync(customer, 1000m, "CHEQUE");
        await desk.BounceAsync(cheque.PaymentUuid);

        var replacement = await desk.PayAsync(customer, 1000m, "BANK_TRANSFER");

        replacement.Allocations.Should().ContainSingle().Which.InvoiceUuid.Should().Be(invoice.Uuid);
        replacement.PartnerBalance.Should().Be(0m);

        var books = await desk.BooksAsync(customer);
        books.Invoice(invoice.Uuid).Status.Should().Be("PAID");
        books.Ledger.Should().HaveCount(4);
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    // ── What is refused ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("BANK_TRANSFER")]
    [InlineData("CASH")]
    [InlineData("CARD")]
    [InlineData("ONLINE")]
    public async Task Only_a_cheque_can_bounce(string method)
    {
        var (_, desk, customer) = Setup();
        await desk.InvoiceAsync(customer, 500m);
        var payment = await desk.PayAsync(customer, 500m, method);
        var before  = (await desk.BooksAsync(customer)).Fingerprint();

        var act = () => desk.BounceAsync(payment.PaymentUuid);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("Only a cheque can bounce");
        (await desk.BooksAsync(customer)).Fingerprint().Should().Be(before, "a refused bounce changes nothing");
    }

    [Fact]
    public async Task A_cheque_cannot_bounce_twice_and_the_customer_is_not_debited_twice()
    {
        var (_, desk, customer) = Setup();
        await desk.InvoiceAsync(customer, 500m);
        var cheque = await desk.PayAsync(customer, 500m, "CHEQUE");
        await desk.BounceAsync(cheque.PaymentUuid);
        var afterFirst = (await desk.BooksAsync(customer)).Fingerprint();

        var act = () => desk.BounceAsync(cheque.PaymentUuid, "again");

        (await act.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("BOUNCED");
        var books = await desk.BooksAsync(customer);
        books.Fingerprint().Should().Be(afterFirst);
        books.Ledger.Count(e => e.DebitAmount == 500m && e.ReferenceId == cheque.PaymentUuid).Should().Be(1);
    }

    [Fact]
    public async Task A_payment_that_does_not_exist_cannot_bounce()
    {
        var (_, desk, _) = Setup();

        var act = () => desk.BounceAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_reason_over_two_hundred_characters_is_refused_and_two_hundred_is_kept_whole()
    {
        var (_, desk, customer) = Setup();
        await desk.InvoiceAsync(customer, 500m);
        var refused = await desk.PayAsync(customer, 100m, "CHEQUE");
        var kept    = await desk.PayAsync(customer, 100m, "CHEQUE");

        var tooLong = () => desk.BounceAsync(refused.PaymentUuid, new string('r', 201));
        await tooLong.Should().ThrowAsync<BadRequestException>();
        (await desk.BooksAsync(customer)).Payment(refused.PaymentUuid).Status.Should().Be("RECEIVED");

        await desk.BounceAsync(kept.PaymentUuid, new string('r', 200));
        var books = await desk.BooksAsync(customer);
        books.Payment(kept.PaymentUuid).Notes.Should().EndWith(new string('r', 200));
        books.Ledger.Last().Narration.Should().EndWith(new string('r', 200));
    }

    // ── What is written on the payment ───────────────────────────────────────

    [Fact]
    public async Task The_receipts_own_notes_are_kept_and_the_bounce_is_recorded_after_them()
    {
        var (_, desk, customer) = Setup();
        var cheque = await desk.PayAsync(customer, 100m, "CHEQUE", notes: "Deposited at the Main Branch");

        await desk.BounceAsync(cheque.PaymentUuid, "  Account closed  ");

        (await desk.BooksAsync(customer)).Payment(cheque.PaymentUuid).Notes
            .Should().Be("Deposited at the Main Branch\nBounced 2026-09-20: Account closed");
    }

    [Fact]
    public async Task A_bounce_without_a_reason_is_still_recorded_with_its_date()
    {
        var (_, desk, customer) = Setup();
        var cheque = await desk.PayAsync(customer, 100m, "CHEQUE");

        await desk.BounceAsync(cheque.PaymentUuid, "   ");

        var books = await desk.BooksAsync(customer);
        books.Payment(cheque.PaymentUuid).Notes.Should().Be("Bounced 2026-09-20");
        books.Ledger.Last().Narration.Should().EndWith("bounced");
    }

    [Fact]
    public async Task Notes_that_leave_no_room_are_shortened_so_the_bounce_still_fits_in_the_column()
    {
        var (_, desk, customer) = Setup();
        var cheque = await desk.PayAsync(customer, 100m, "CHEQUE", notes: new string('n', 500));

        await desk.BounceAsync(cheque.PaymentUuid, new string('r', 200));

        var notes = (await desk.BooksAsync(customer)).Payment(cheque.PaymentUuid).Notes!;
        notes.Length.Should().BeLessThanOrEqualTo(500);
        notes.Should().StartWith("nnn").And.EndWith(new string('r', 200), "the bounce is what matters now, so it is what stays whole");
    }

    // ── One transaction ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_payment_the_invoices_and_the_ledger_debit_are_committed_in_a_single_save()
    {
        var (_, desk, customer) = Setup();
        await desk.InvoiceAsync(customer, 600m);
        await desk.InvoiceAsync(customer, 500m);
        var cheque = await desk.PayAsync(customer, 1000m, "CHEQUE");

        var recorder = new SaveRecorder();
        await using var scope = desk.Scope(recorder);
        await scope.Payments.BounceAsync(cheque.PaymentUuid, null, User);

        recorder.Saves.Should().ContainSingle().Which.Should().Be((Payments: 1, Invoices: 2, LedgerEntries: 1));
    }

    [Fact]
    public async Task A_bounce_that_cannot_be_saved_changes_nothing_and_leaves_nothing_waiting_on_the_context()
    {
        var (_, desk, customer) = Setup();
        await desk.InvoiceAsync(customer, 500m);
        var cheque = await desk.PayAsync(customer, 500m, "CHEQUE");
        var before = (await desk.BooksAsync(customer)).Fingerprint();

        var failing = new AlwaysFail();
        await using var scope = desk.Scope(failing);

        var act = () => scope.Payments.BounceAsync(cheque.PaymentUuid, null, User);

        await act.Should().ThrowAsync<DbUpdateException>();
        failing.Attempts.Should().Be(5, "it tries a bounded number of times and then gives up");
        scope.Db.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Should().BeEmpty("nothing half-done may be left for a later save on this context to commit");
        (await desk.BooksAsync(customer)).Fingerprint().Should().Be(before);
    }

    [Fact]
    public async Task Losing_the_race_for_the_ledger_sequence_retries_against_the_balance_that_moved_on()
    {
        var (world, desk, customer) = Setup();
        await desk.InvoiceAsync(customer, 500m);
        var cheque = await desk.PayAsync(customer, 500m, "CHEQUE");

        // Another request bills the customer again after this bounce has read the ledger's last entry.
        var winner = new LoseTheRaceOnce(
            db => db.ChangeTracker.Entries<CustomerLedgerEntry>().Any(e => e.State == EntityState.Added),
            () => desk.InvoiceAsync(customer, 250m));

        await using var scope = desk.Scope(winner);
        var bounced = await scope.Payments.BounceAsync(cheque.PaymentUuid, null, User);

        winner.Fired.Should().BeTrue();
        bounced.PartnerBalance.Should().Be(750m, "500 reopened on top of the 250 the winner billed");

        var books = await desk.BooksAsync(customer);
        books.Ledger.Select(e => e.SequenceNo).Should().Equal(1, 2, 3, 4);
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    [Fact]
    public async Task Two_bounces_of_the_same_cheque_at_once_debit_the_customer_once()
    {
        var (world, desk, customer) = Setup();
        await desk.InvoiceAsync(customer, 500m);
        var cheque = await desk.PayAsync(customer, 500m, "CHEQUE");

        var winner = new LoseTheRaceOnce(
            db => db.ChangeTracker.Entries<CustomerPayment>().Any(e => e.State == EntityState.Modified),
            () => desk.BounceAsync(cheque.PaymentUuid, "the first request"),
            asConcurrencyConflict: true);

        await using var scope = desk.Scope(winner);
        var act = () => scope.Payments.BounceAsync(cheque.PaymentUuid, "the second request", User);

        await act.Should().ThrowAsync<ConflictException>("the loser re-reads the winner's result and is refused");

        var books = await desk.BooksAsync(customer);
        books.Ledger.Count(e => e.DebitAmount == 500m && e.ReferenceId == cheque.PaymentUuid).Should().Be(1);
        books.Payment(cheque.PaymentUuid).Notes.Should().Contain("the first request").And.NotContain("the second request");
        ReceivablesInvariants.Violations(books, world.Clock.Value.Date).Should().BeEmpty();
    }

    // ── How a bounced payment reads afterwards ───────────────────────────────

    [Fact]
    public async Task A_bounced_payment_reports_nothing_applied_and_nothing_on_account_yet_keeps_its_history()
    {
        var (_, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);
        var cheque  = await desk.PayAsync(customer, 1000m, "CHEQUE", [new ManualPaymentAllocation(invoice.Uuid, 400m)]);
        await desk.BounceAsync(cheque.PaymentUuid, "Refer to drawer");

        await using var scope = desk.Scope();
        var detail = await scope.Payments.GetAsync(cheque.PaymentUuid);

        detail!.Status.Should().Be("BOUNCED");
        detail.Amount.Should().Be(1000m);
        detail.AllocatedAmount.Should().Be(0m);
        detail.UnallocatedAmount.Should().Be(0m);
        detail.Allocations.Should().ContainSingle().Which.AllocatedAmount.Should().Be(400m);
        detail.Allocations[0].InvoiceBalanceDue.Should().Be(1000m, "the invoice reads as it stands now");
    }

    [Fact]
    public async Task A_bounced_payment_is_neither_unallocated_nor_fully_allocated_in_the_list()
    {
        var (_, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);
        var bounced = await desk.PayAsync(customer, 1000m, "CHEQUE", [new ManualPaymentAllocation(invoice.Uuid, 400m)]);
        var good    = await desk.PayAsync(customer, 300m, "CHEQUE", []);            // held on account
        await desk.BounceAsync(bounced.PaymentUuid);

        await using var scope = desk.Scope();

        var byStatus = await scope.Payments.ListAsync(new CustomerPaymentFilter { Status = "BOUNCED" });
        byStatus.Data.Should().ContainSingle().Which.Should().Match<CustomerPaymentListItemModel>(
            p => p.Uuid == bounced.PaymentUuid && p.AllocatedAmount == 0m && p.UnallocatedAmount == 0m);

        var onAccount = await scope.Payments.ListAsync(new CustomerPaymentFilter { Unallocated = true });
        onAccount.Data.Select(p => p.Uuid).Should().Equal(good.PaymentUuid);

        var applied = await scope.Payments.ListAsync(new CustomerPaymentFilter { Unallocated = false });
        applied.Data.Should().BeEmpty("neither payment is fully applied to invoices");
    }

    [Fact]
    public async Task The_invoice_still_lists_the_bounced_payment_with_its_status()
    {
        var (_, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);
        var cheque  = await desk.PayAsync(customer, 1000m, "CHEQUE");
        await desk.BounceAsync(cheque.PaymentUuid);

        await using var scope = desk.Scope();
        var detail = await scope.Invoices.GetAsync(invoice.Uuid);

        detail!.Status.Should().Be("ISSUED");
        detail.BalanceDue.Should().Be(1000m);
        detail.Payments.Should().ContainSingle().Which.Should().Match<SalesInvoicePaymentModel>(
            p => p.PaymentUuid == cheque.PaymentUuid && p.PaymentStatus == "BOUNCED" && p.AllocatedAmount == 1000m);
    }

    [Fact]
    public async Task What_a_bounced_cheque_had_not_yet_applied_can_no_longer_be_allocated()
    {
        var (_, desk, customer) = Setup();
        var invoice = await desk.InvoiceAsync(customer, 1000m);
        var cheque  = await desk.PayAsync(customer, 1000m, "CHEQUE", [new ManualPaymentAllocation(invoice.Uuid, 400m)]);
        await desk.BounceAsync(cheque.PaymentUuid);

        await using var scope = desk.Scope();
        var act = () => scope.Payments.AllocateAsync(cheque.PaymentUuid, null, User);

        (await act.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("BOUNCED");
    }

    // ── An invoice that is no longer a receivable ────────────────────────────

    [Fact]
    public async Task A_cancelled_invoice_gets_its_amounts_back_but_is_not_reopened()
    {
        var (world, desk, customer) = Setup();
        var org = desk.Org;

        var invoice = Receivables.Invoice(org, customer, "SINV-20260901-0001", new DateTime(2026, 9, 1), 500m,
            status: "CANCELLED", paid: 500m);
        var payment = new CustomerPayment
        {
            UUID = Guid.NewGuid(), OrganizationId = org, PartnerId = customer, PartnerName = "Acme Ltd",
            PaymentNumber = "CPAY-20260901-0001", PaymentDate = new DateTime(2026, 9, 1), Amount = 500m,
            PaymentMethod = "CHEQUE", ChequeNumber = "CHQ-9", CurrencyCode = "PKR", Status = "RECEIVED",
            CreatedBy = 1, CreatedDate = new DateTime(2026, 9, 1)
        };
        payment.Allocations.Add(new PaymentAllocation
        {
            UUID = Guid.NewGuid(), OrganizationId = org, SalesInvoice = invoice, AllocatedAmount = 500m,
            AllocatedAt = new DateTime(2026, 9, 1), AllocatedBy = 1
        });
        await using (var seed = Receivables.Auditor(world.DbName))
            await Receivables.Seed(seed, payment);

        await desk.BounceAsync(payment.UUID);

        var stored = (await desk.BooksAsync(customer)).Invoice(invoice.UUID);
        stored.Status.Should().Be("CANCELLED");
        stored.AmountPaid.Should().Be(0m);
        stored.BalanceDue.Should().Be(500m);
    }
}
