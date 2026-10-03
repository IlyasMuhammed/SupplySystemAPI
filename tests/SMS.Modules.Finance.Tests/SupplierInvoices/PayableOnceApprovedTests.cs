using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// A supplier invoice is a payable only once it is Approved (booked to the supplier ledger). Before that it can be
/// neither paid, nor aged, nor reduced by a credit or debit note — a note deducted from an invoice not yet booked
/// was counted twice: its own ledger credit at once, and again in the smaller debit the approval then posted.
/// Cancelling or bouncing a payment that already exists still works whatever its invoice's status. And none of it
/// reaches another organization's invoice or payment, not even for a super admin.
/// </summary>
public class PayableOnceApprovedTests
{
    private const int User = PurchaseRig.User;

    // ── Payments: only an approved invoice can be paid ───────────────────────

    [Theory]
    [InlineData(InvoiceMatchStatus.Pending)]
    [InlineData(InvoiceMatchStatus.Matched)]
    [InlineData(InvoiceMatchStatus.Variance)]
    public async Task A_payment_cannot_be_drafted_against_an_invoice_that_is_not_approved_yet(string status)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, status);
        var number  = (await rig.LoadAsync(invoice)).InvoiceNumber;

        await Payments(rig).Invoking(p => p.CreateAsync(Pay(rig, invoice, 100m), User))
            .Should().ThrowAsync<BadRequestException>()
            .WithMessage($"Invoice {number} is not approved yet; approve it before paying.");

        rig.Forget();
        (await rig.Finance.SupplierPayments.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(InvoiceMatchStatus.Reversed)]
    [InlineData(InvoiceMatchStatus.Rejected)]
    public async Task A_reversed_or_rejected_invoice_still_owes_nothing(string status)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, status);

        await Payments(rig).Invoking(p => p.CreateAsync(Pay(rig, invoice, 100m), User))
            .Should().ThrowAsync<BadRequestException>()
            .WithMessage($"*has been {status.ToLowerInvariant()}, so nothing is owed on it.*");
    }

    [Fact]
    public async Task An_approved_invoice_is_paid_through_draft_approval_and_posting()
    {
        var rig      = PurchaseRig.New();
        var invoice  = await InvoiceAsync(rig, InvoiceMatchStatus.Approved);
        var payments = Payments(rig);

        var payment = await payments.CreateAsync(Pay(rig, invoice, 400m), User);
        (await payments.ApproveAsync(payment, User)).Should().BeTrue();
        (await payments.PostAsync(payment, User)).Should().BeTrue();

        rig.Forget();
        var stored = await rig.LoadAsync(invoice);
        (stored.PaidAmount, stored.PaymentStatus).Should().Be((400m, InvoicePaymentStatus.PartiallyPaid));
    }

    [Theory]
    [InlineData(InvoiceMatchStatus.Pending)]
    [InlineData(InvoiceMatchStatus.Matched)]
    [InlineData(InvoiceMatchStatus.Variance)]
    [InlineData(InvoiceMatchStatus.Reversed)]
    [InlineData(InvoiceMatchStatus.Rejected)]
    public async Task Approving_a_payment_whose_invoice_is_not_approved_is_a_conflict(string status)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, InvoiceMatchStatus.Approved);
        var payment = await Payments(rig).CreateAsync(Pay(rig, invoice, 100m), User);
        rig.Forget();
        // What older data, or an invoice changed after the draft, leaves behind.
        await rig.TamperAsync(invoice, i => i.MatchStatus = status);

        await Payments(rig).Invoking(p => p.ApproveAsync(payment, User))
            .Should().ThrowAsync<ConflictException>().WithMessage(MessageFor(status));

        rig.Forget();
        (await rig.Finance.SupplierPayments.AsNoTracking().SingleAsync(p => p.UUID == payment)).Status.Should().Be("DRAFT");
    }

    [Theory]
    [InlineData(InvoiceMatchStatus.Pending)]
    [InlineData(InvoiceMatchStatus.Matched)]
    [InlineData(InvoiceMatchStatus.Variance)]
    [InlineData(InvoiceMatchStatus.Reversed)]
    [InlineData(InvoiceMatchStatus.Rejected)]
    public async Task Posting_a_payment_whose_invoice_is_not_approved_is_a_conflict_and_moves_no_money(string status)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, InvoiceMatchStatus.Approved);
        var payment = await rig.SupplierPaymentAsync(invoice, "APPROVED", amount: 100m);
        await rig.TamperAsync(invoice, i => i.MatchStatus = status);
        var ledgerBefore = await rig.Finance.SupplierLedgerEntries.CountAsync();

        await Payments(rig).Invoking(p => p.PostAsync(payment.UUID, User))
            .Should().ThrowAsync<ConflictException>().WithMessage(MessageFor(status));

        rig.Forget();
        (await rig.LoadAsync(invoice)).PaidAmount.Should().Be(0m);
        (await rig.Finance.SupplierLedgerEntries.CountAsync()).Should().Be(ledgerBefore);
        (await rig.Finance.SupplierPayments.AsNoTracking().SingleAsync(p => p.UUID == payment.UUID)).Status.Should().Be("APPROVED");
    }

    private static string MessageFor(string status) =>
        InvoiceMatchStatus.Is(status, InvoiceMatchStatus.Reversed) || InvoiceMatchStatus.Is(status, InvoiceMatchStatus.Rejected)
            ? $"*has been {status.ToLowerInvariant()}, so nothing is owed on it*"
            : "*is not approved yet; approve it before paying*";

    // ── Cancel and bounce work whatever the invoice's status ─────────────────

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("APPROVED")]
    public async Task A_draft_or_approved_payment_against_a_pending_invoice_can_still_be_cancelled(string paymentStatus)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, InvoiceMatchStatus.Pending);
        var payment = await rig.SupplierPaymentAsync(invoice, paymentStatus, amount: 100m);

        (await Payments(rig).CancelAsync(payment.UUID, User)).Should().BeTrue();

        rig.Forget();
        (await rig.Finance.SupplierPayments.AsNoTracking().SingleAsync(p => p.UUID == payment.UUID)).Status.Should().Be("CANCELLED");
    }

    [Fact]
    public async Task A_posted_cheque_against_a_pending_invoice_can_still_bounce()
    {
        var rig      = PurchaseRig.New();
        var invoice  = await InvoiceAsync(rig, InvoiceMatchStatus.Approved);
        var payments = Payments(rig);
        var payment  = await payments.CreateAsync(Pay(rig, invoice, 300m, "CHEQUE"), User);
        await payments.ApproveAsync(payment, User);
        await payments.PostAsync(payment, User);
        rig.Forget();
        // Posted when unapproved invoices could still be paid.
        await rig.TamperAsync(invoice, i => i.MatchStatus = InvoiceMatchStatus.Pending);

        (await Payments(rig).BounceAsync(payment, User)).Should().BeTrue();

        rig.Forget();
        var stored = await rig.LoadAsync(invoice);
        (stored.PaidAmount, stored.PaymentStatus).Should().Be((0m, InvoicePaymentStatus.Unpaid));
        (await rig.Finance.SupplierPayments.AsNoTracking().SingleAsync(p => p.UUID == payment)).Status.Should().Be("BOUNCED");
        (await rig.LedgerAsync(payment)).Select(e => e.TransactionType).Should().Equal("PAYMENT_POSTED", "PAYMENT_BOUNCED");
    }

    /// <summary>
    /// A bounced cheque paid nothing, so its invoice is fully owed again. Its line used to keep counting as allocated
    /// (only CANCELLED payments were left out), so the invoice could not be paid again in full.
    /// </summary>
    [Fact]
    public async Task After_a_cheque_bounces_its_invoice_can_be_paid_again_in_full()
    {
        var rig      = PurchaseRig.New();
        var invoice  = await InvoiceAsync(rig, InvoiceMatchStatus.Approved, 1000m);
        var payments = Payments(rig);
        var cheque   = await payments.CreateAsync(Pay(rig, invoice, 1000m, "CHEQUE"), User);
        await payments.ApproveAsync(cheque, User);
        await payments.PostAsync(cheque, User);
        await payments.BounceAsync(cheque, User);
        rig.Forget();

        (await Payments(rig).GetOutstandingInvoicesAsync(rig.Supplier)).Single().OutstandingAmount.Should().Be(1000m);
        (await Payments(rig).CreateAsync(Pay(rig, invoice, 1000m), User)).Should().NotBeEmpty();
    }

    // ── Another organization ─────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Another_organizations_invoice_cannot_be_paid_even_by_a_super_admin(bool superAdmin)
    {
        var rig      = PurchaseRig.New();
        var invoice  = await InvoiceAsync(rig, InvoiceMatchStatus.Approved);
        var stranger = Stranger(rig, superAdmin);

        await stranger.Payments.Invoking(p => p.CreateAsync(Pay(rig, invoice, 100m), User))
            .Should().ThrowAsync<NotFoundException>();

        (await rig.Finance.SupplierPayments.AsNoTracking().IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Another_organizations_payment_is_not_found_by_approve_post_cancel_or_bounce_even_for_a_super_admin()
    {
        var rig      = PurchaseRig.New();
        var invoice  = await InvoiceAsync(rig, InvoiceMatchStatus.Approved);
        var payments = Payments(rig);
        var draft    = await payments.CreateAsync(Pay(rig, invoice, 100m), User);
        var approved = await payments.CreateAsync(Pay(rig, invoice, 100m), User);
        await payments.ApproveAsync(approved, User);
        var posted   = await payments.CreateAsync(Pay(rig, invoice, 100m, "CHEQUE"), User);
        await payments.ApproveAsync(posted, User);
        await payments.PostAsync(posted, User);
        rig.Forget();

        var stranger = Stranger(rig, superAdmin: true).Payments;

        (await stranger.ApproveAsync(draft, User)).Should().BeFalse();
        (await stranger.CancelAsync(draft, User)).Should().BeFalse();
        (await stranger.PostAsync(approved, User)).Should().BeFalse();
        (await stranger.BounceAsync(posted, User)).Should().BeFalse();

        rig.Forget();
        var statuses = await rig.Finance.SupplierPayments.AsNoTracking().ToDictionaryAsync(p => p.UUID, p => p.Status);
        statuses.Should().BeEquivalentTo(new Dictionary<Guid, string> { [draft] = "DRAFT", [approved] = "APPROVED", [posted] = "POSTED" });
        (await rig.LoadAsync(invoice)).PaidAmount.Should().Be(100m);
    }

    // ── Outstanding, aging and payables: approved invoices only ──────────────

    [Fact]
    public async Task Only_an_approved_invoice_is_outstanding_aged_or_payable_even_with_old_payments_against_the_others()
    {
        var rig      = PurchaseRig.New();
        var approved = await InvoiceAsync(rig, InvoiceMatchStatus.Approved, 1000m);
        var pending  = await InvoiceAsync(rig, InvoiceMatchStatus.Pending, 200m);
        var matched  = await InvoiceAsync(rig, InvoiceMatchStatus.Matched, 300m);
        await InvoiceAsync(rig, InvoiceMatchStatus.Variance, 400m);
        // Drafted when an invoice not yet approved could still be paid.
        await rig.SupplierPaymentAsync(pending, "DRAFT", amount: 50m);
        await rig.SupplierPaymentAsync(matched, "APPROVED", amount: 60m);
        var payments = Payments(rig);

        (await payments.GetOutstandingInvoicesAsync(rig.Supplier)).Select(i => i.InvoiceUuid).Should().Equal(approved);
        (await payments.GetSupplierAgingAsync(rig.Supplier)).GrandTotal.Should().Be(1000m);
        (await payments.GetCrossSupplierAgingAsync()).GrandTotalRow!.GrandTotal.Should().Be(1000m);
        (await payments.GetOutstandingPayablesAsync(new OutstandingPayablesFilter())).Data.SelectMany(g => g.Invoices)
            .Select(i => i.InvoiceUuid).Should().Equal(approved);
    }

    // ── Credit and debit notes ───────────────────────────────────────────────

    /// <summary>
    /// The double count: a note of 100 on a Pending invoice of 1000 credited the ledger 100 at once and took the
    /// invoice to 900; its approval then debited 900. The ledger said 800, while 900 was owed. Now the note is
    /// carried forward until the invoice is booked, and applying it afterwards leaves ledger and invoice agreeing.
    /// </summary>
    [Theory]
    [InlineData("credit")]
    [InlineData("debit")]
    public async Task A_note_on_an_invoice_not_yet_approved_is_carried_forward_so_the_ledger_matches_what_is_owed(string kind)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, InvoiceMatchStatus.Pending, 1000m);

        var note = await NoteAsync(rig, kind, 100m, invoice);
        (await rig.Service.ApproveAsync(invoice, null, User)).Should().BeTrue();

        var (ledger, owed) = await BooksAsync(rig);
        ledger.Should().Be(owed, "the supplier ledger says what is owed: the invoice, less the credit still to be used");
        ledger.Should().Be(900m);
        (await NoteStateAsync(rig, kind, note)).Should().Be(("CARRIED_FORWARD", (Guid?)null, (decimal?)100m));
        (await rig.LoadAsync(invoice)).TotalAmount.Should().Be(1000m);

        // Once booked, the note can be deducted from it — and the books still agree.
        await ApplyAsync(rig, kind, note, invoice);
        (ledger, owed) = await BooksAsync(rig);
        (ledger, owed).Should().Be((900m, 900m));
        (await rig.LoadAsync(invoice)).TotalAmount.Should().Be(900m);
    }

    [Theory]
    [InlineData("credit")]
    [InlineData("debit")]
    public async Task A_note_on_an_approved_invoice_is_deducted_from_it_at_once(string kind)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, InvoiceMatchStatus.Approved, 1000m);

        var note = await NoteAsync(rig, kind, 100m, invoice);

        (await NoteStateAsync(rig, kind, note)).Should().Be(("APPLIED_TO_INVOICE", (Guid?)invoice, (decimal?)null));
        (await rig.LoadAsync(invoice)).TotalAmount.Should().Be(900m);
        (await BooksAsync(rig)).Should().Be((900m, 900m));
    }

    [Theory]
    [InlineData("credit")]
    [InlineData("debit")]
    public async Task A_note_larger_than_what_is_still_owed_is_carried_forward_not_deducted(string kind)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, InvoiceMatchStatus.Approved, 1000m);
        // 950 of it is on a payment not yet posted: 50 is still owed.
        await Payments(rig).CreateAsync(Pay(rig, invoice, 950m), User);
        rig.Forget();

        var note = await NoteAsync(rig, kind, 100m, invoice);

        (await NoteStateAsync(rig, kind, note)).Should().Be(("CARRIED_FORWARD", (Guid?)null, (decimal?)100m));
        (await rig.LoadAsync(invoice)).TotalAmount.Should().Be(1000m, "a total below what is already allocated would leave the payment paying more than is owed");
    }

    [Theory]
    [InlineData("credit")]
    [InlineData("debit")]
    public async Task A_note_naming_another_suppliers_invoice_is_not_deducted_from_it(string kind)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, InvoiceMatchStatus.Approved, 1000m, supplier: Guid.NewGuid());

        var note = await NoteAsync(rig, kind, 100m, invoice);

        (await NoteStateAsync(rig, kind, note)).Status.Should().Be("CARRIED_FORWARD");
        (await rig.LoadAsync(invoice)).TotalAmount.Should().Be(1000m);
    }

    [Theory]
    [InlineData("credit", InvoiceMatchStatus.Pending)]
    [InlineData("credit", InvoiceMatchStatus.Matched)]
    [InlineData("credit", InvoiceMatchStatus.Variance)]
    [InlineData("debit", InvoiceMatchStatus.Pending)]
    [InlineData("debit", InvoiceMatchStatus.Matched)]
    [InlineData("debit", InvoiceMatchStatus.Variance)]
    public async Task Applying_a_carried_forward_note_to_an_invoice_not_yet_approved_is_refused(string kind, string status)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, status, 1000m);
        var number  = (await rig.LoadAsync(invoice)).InvoiceNumber;
        var note    = await CarriedForwardAsync(rig, kind, 100m);

        await rig.Invoking(r => ApplyAsync(r, kind, note, invoice))
            .Should().ThrowAsync<UnprocessableEntityException>()
            .WithMessage($"Invoice {number} is not approved yet; a note can be applied only to an approved invoice.");

        (await rig.LoadAsync(invoice)).TotalAmount.Should().Be(1000m);
        (await NoteStateAsync(rig, kind, note)).Status.Should().Be("CARRIED_FORWARD");
    }

    [Theory]
    [InlineData("credit")]
    [InlineData("debit")]
    public async Task Applying_a_carried_forward_note_to_another_suppliers_invoice_is_a_bad_request(string kind)
    {
        var rig     = PurchaseRig.New();
        var invoice = await InvoiceAsync(rig, InvoiceMatchStatus.Approved, 1000m, supplier: Guid.NewGuid());
        var note    = await CarriedForwardAsync(rig, kind, 100m);

        await rig.Invoking(r => ApplyAsync(r, kind, note, invoice)).Should().ThrowAsync<BadRequestException>();

        (await rig.LoadAsync(invoice)).TotalAmount.Should().Be(1000m);
    }

    /// <summary>
    /// What is still owed is the total less what is paid (posted), what is on payments not yet posted, and legacy
    /// payments not reversed: 1000 − 50 − 600 − 300 = 50. A note of 100 would take the invoice below what is
    /// already paid or promised; a note of 50 is exactly what is left.
    /// </summary>
    [Theory]
    [InlineData("credit")]
    [InlineData("debit")]
    public async Task Applying_a_carried_forward_note_larger_than_what_is_still_owed_is_refused(string kind)
    {
        var rig      = PurchaseRig.New();
        var invoice  = await InvoiceAsync(rig, InvoiceMatchStatus.Approved, 1000m);
        var number   = (await rig.LoadAsync(invoice)).InvoiceNumber;
        var payments = Payments(rig);
        var posted   = await payments.CreateAsync(Pay(rig, invoice, 50m), User);
        await payments.ApproveAsync(posted, User);
        await payments.PostAsync(posted, User);
        await payments.CreateAsync(Pay(rig, invoice, 600m), User);
        var cancelled = await payments.CreateAsync(Pay(rig, invoice, 25m), User);
        await payments.CancelAsync(cancelled, User);
        rig.Forget();
        await rig.LegacyPaymentAsync(invoice, "Completed", 300m);
        await rig.LegacyPaymentAsync(invoice, "Reversed", 999m);

        var tooBig = await CarriedForwardAsync(rig, kind, 100m);
        await rig.Invoking(r => ApplyAsync(r, kind, tooBig, invoice))
            .Should().ThrowAsync<UnprocessableEntityException>()
            .WithMessage($"*the note is more than is still owed on invoice {number}*");
        (await rig.LoadAsync(invoice)).TotalAmount.Should().Be(1000m);

        var justRight = await CarriedForwardAsync(rig, kind, 50m);
        await ApplyAsync(rig, kind, justRight, invoice);
        (await rig.LoadAsync(invoice)).TotalAmount.Should().Be(950m);
        (await NoteStateAsync(rig, kind, justRight)).Should().Be(("APPLIED", (Guid?)invoice, (decimal?)50m));
    }

    /// <summary>900 of 1000 is paid; a note of 100 brings the total down to what is paid, so it is fully paid now.</summary>
    [Theory]
    [InlineData("credit")]
    [InlineData("debit")]
    public async Task A_note_that_brings_the_total_down_to_what_is_paid_makes_the_invoice_fully_paid(string kind)
    {
        var rig      = PurchaseRig.New();
        var invoice  = await InvoiceAsync(rig, InvoiceMatchStatus.Approved, 1000m);
        var payments = Payments(rig);
        var payment  = await payments.CreateAsync(Pay(rig, invoice, 900m), User);
        await payments.ApproveAsync(payment, User);
        await payments.PostAsync(payment, User);
        rig.Forget();
        var note = await CarriedForwardAsync(rig, kind, 100m);

        await ApplyAsync(rig, kind, note, invoice);

        rig.Forget();
        var stored = await rig.LoadAsync(invoice);
        (stored.TotalAmount, stored.PaidAmount, stored.PaymentStatus).Should().Be((900m, 900m, InvoicePaymentStatus.FullyPaid));
    }

    [Theory]
    [InlineData("credit", false)]
    [InlineData("credit", true)]
    [InlineData("debit", false)]
    [InlineData("debit", true)]
    public async Task Another_organizations_invoice_is_not_found_when_applying_a_note_even_for_a_super_admin(string kind, bool superAdmin)
    {
        var rig      = PurchaseRig.New();
        var invoice  = await InvoiceAsync(rig, InvoiceMatchStatus.Approved, 1000m);
        var stranger = Stranger(rig, superAdmin);
        var note     = await CarriedForwardAsync(rig, kind, 100m, org: stranger.Org);

        var apply = kind == "credit"
            ? stranger.Credits.ApplyCarriedForwardAsync(note, new ApplyCreditNoteRequest { InvoiceUuid = invoice }, User)
            : stranger.Debits.ApplyCarriedForwardAsync(note, new ApplyDebitNoteRequest { InvoiceUuid = invoice }, User);
        await apply.Invoking(a => a).Should().ThrowAsync<NotFoundException>();

        (await rig.LoadAsync(invoice)).TotalAmount.Should().Be(1000m);
    }

    // ── The rig ──────────────────────────────────────────────────────────────

    private static SupplierPaymentRepository Payments(PurchaseRig rig) =>
        new(rig.Finance, new SupplierLedgerService(rig.Finance), Mock.Of<INotificationService>());

    private static CreditNoteRepository Credits(PurchaseRig rig) =>
        new(rig.Finance, rig.Warehouse, new SupplierLedgerService(rig.Finance));

    private static DebitNoteRepository Debits(PurchaseRig rig) =>
        new(rig.Finance, rig.Warehouse, Mock.Of<IBackgroundJobClient>(), Mock.Of<INotificationService>(),
            Mock.Of<IAuditService>(), new SupplierLedgerService(rig.Finance));

    /// <summary>A purchase-order-less invoice of <paramref name="total"/>, brought to <paramref name="status"/> the way the API does (or, for Matched/Variance, set directly).</summary>
    private static async Task<Guid> InvoiceAsync(PurchaseRig rig, string status, decimal total = 1000m, Guid? supplier = null)
    {
        var request = rig.Request(subtotal: total);
        if (supplier is { } other) request.SupplierId = other;
        var uuid = await rig.CreateAsync(request);

        switch (status)
        {
            case InvoiceMatchStatus.Pending:
                break;
            case InvoiceMatchStatus.Approved:
                (await rig.Service.ApproveAsync(uuid, null, User)).Should().BeTrue();
                break;
            case InvoiceMatchStatus.Rejected:
                (await rig.Service.RejectAsync(uuid, "Not ours", User)).Should().BeTrue();
                break;
            case InvoiceMatchStatus.Reversed:
                (await rig.Service.ApproveAsync(uuid, null, User)).Should().BeTrue();
                rig.Forget();
                (await rig.Service.ReverseAsync(uuid, "Entered twice", User)).Should().BeTrue();
                break;
            default:
                await rig.TamperAsync(uuid, i => i.MatchStatus = status);
                break;
        }

        rig.Forget();
        return uuid;
    }

    private static CreateSupplierPaymentRequest Pay(PurchaseRig rig, Guid invoice, decimal amount, string method = "CASH") => new()
    {
        SupplierId = rig.Supplier, SupplierName = "Karachi Steel", PaymentDate = new DateTime(2026, 9, 20), PaymentMethod = method,
        ChequeNo = method == "CHEQUE" ? $"CHQ-{Random.Shared.Next(1000, 9999)}" : null,
        ChequeDate = method == "CHEQUE" ? new DateTime(2026, 9, 20) : null,
        TotalAmount = amount,
        Lines = [new CreateSupplierPaymentLineRequest { InvoiceUuid = invoice, AllocatedAmount = amount }]
    };

    private static async Task<Guid> SroAsync(PurchaseRig rig)
    {
        var sro = new SupplierReturnOrder
        {
            UUID = Guid.NewGuid(), ReturnNumber = $"SRO-2026-{Random.Shared.Next(10000, 99999)}", SroType = "POST_RECEIPT_DEFECT",
            SupplierId = rig.Supplier, SupplierName = "Karachi Steel", ReturnReason = "DAMAGED", Status = "SUPPLIER_RECEIVED",
            IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow,
            Lines = [new SupplierReturnOrderLine { UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "Steel bar", QtyToReturn = 10m, UnitCost = 100m }]
        };
        rig.Warehouse.SupplierReturnOrders.Add(sro);
        await rig.Warehouse.SaveChangesAsync();
        rig.Forget();
        return sro.UUID;
    }

    /// <summary>A note raised against a returned shipment, naming <paramref name="invoice"/>.</summary>
    private static async Task<Guid> NoteAsync(PurchaseRig rig, string kind, decimal amount, Guid? invoice)
    {
        var sro = await SroAsync(rig);
        var uuid = kind == "credit"
            ? await Credits(rig).CreateAsync(new CreateCreditNoteRequest
              {
                  SroId = sro, SupplierCreditNoteNo = "S-CN-1", CreditDate = new DateTime(2026, 9, 25), CreditAmount = amount, InvoiceUuid = invoice
              }, User)
            : await Debits(rig).CreateAsync(new CreateDebitNoteRequest
              {
                  SroId = sro, DebitReason = "DAMAGED_GOODS", DebitAmount = amount, InvoiceUuid = invoice
              }, User);
        rig.Forget();
        return uuid;
    }

    /// <summary>A note already carried forward (in <paramref name="org"/>, or the rig's own).</summary>
    private static async Task<Guid> CarriedForwardAsync(PurchaseRig rig, string kind, decimal amount, Guid? org = null)
    {
        var uuid = Guid.NewGuid();
        if (kind == "credit")
            rig.Finance.CreditNotes.Add(new CreditNote
            {
                UUID = uuid, OrganizationId = org ?? Guid.Empty, CreditNoteNumber = $"CN-2026-{Random.Shared.Next(10000, 99999)}",
                SupplierCreditNoteNo = "S-1", SroUuid = Guid.NewGuid(), SroNumber = "SRO-1", SupplierId = rig.Supplier, SupplierName = "Karachi Steel",
                CreditDate = new DateTime(2026, 9, 20), CreditAmount = amount, ApplicationStatus = "CARRIED_FORWARD", CarriedForwardAmount = amount,
                IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
            });
        else
            rig.Finance.DebitNotes.Add(new DebitNote
            {
                UUID = uuid, OrganizationId = org ?? Guid.Empty, DebitNoteNumber = $"DN-2026-{Random.Shared.Next(10000, 99999)}",
                SroUuid = Guid.NewGuid(), SroNumber = "SRO-2", SupplierId = rig.Supplier, SupplierName = "Karachi Steel",
                DebitReason = "SHORT_SUPPLY", DebitAmount = amount, ApplicationStatus = "CARRIED_FORWARD", CarriedForwardAmount = amount,
                Status = "ISSUED", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
            });
        await rig.Finance.SaveChangesAsync();
        rig.Forget();
        return uuid;
    }

    private static Task ApplyAsync(PurchaseRig rig, string kind, Guid note, Guid invoice) =>
        kind == "credit"
            ? Credits(rig).ApplyCarriedForwardAsync(note, new ApplyCreditNoteRequest { InvoiceUuid = invoice }, User)
            : Debits(rig).ApplyCarriedForwardAsync(note, new ApplyDebitNoteRequest { InvoiceUuid = invoice }, User);

    private static async Task<(string Status, Guid? AppliedTo, decimal? CarriedForward)> NoteStateAsync(PurchaseRig rig, string kind, Guid uuid)
    {
        rig.Forget();
        if (kind == "credit")
        {
            var c = await rig.Finance.CreditNotes.AsNoTracking().IgnoreQueryFilters().SingleAsync(x => x.UUID == uuid);
            return (c.ApplicationStatus, c.AppliedToInvoiceUuid, c.CarriedForwardAmount);
        }

        var d = await rig.Finance.DebitNotes.AsNoTracking().IgnoreQueryFilters().SingleAsync(x => x.UUID == uuid);
        return (d.ApplicationStatus, d.AppliedToInvoiceUuid, d.CarriedForwardAmount);
    }

    /// <summary>
    /// The supplier ledger's balance, and what is owed: the approved invoices' totals less what is paid on them,
    /// less every note still carried forward (credit the supplier has given us, not yet used).
    /// </summary>
    private static async Task<(decimal Ledger, decimal Owed)> BooksAsync(PurchaseRig rig)
    {
        rig.Forget();
        var ledger = (await rig.Finance.SupplierLedgerEntries.AsNoTracking().Where(e => e.SupplierId == rig.Supplier).ToListAsync())
            .Sum(e => e.DebitAmount - e.CreditAmount);
        var invoices = (await rig.Finance.Invoices.AsNoTracking()
                .Where(i => i.SupplierId == rig.Supplier && i.MatchStatus == InvoiceMatchStatus.Approved).ToListAsync())
            .Sum(i => i.TotalAmount - i.PaidAmount);
        var credits = (await rig.Finance.CreditNotes.AsNoTracking()
                .Where(c => c.SupplierId == rig.Supplier && c.ApplicationStatus == "CARRIED_FORWARD").ToListAsync())
            .Sum(c => c.CarriedForwardAmount ?? c.CreditAmount);
        var debits = (await rig.Finance.DebitNotes.AsNoTracking()
                .Where(d => d.SupplierId == rig.Supplier && d.ApplicationStatus == "CARRIED_FORWARD").ToListAsync())
            .Sum(d => d.CarriedForwardAmount ?? d.DebitAmount);
        return (ledger, invoices - credits - debits);
    }

    private sealed record StrangerRepos(Guid Org, SupplierPaymentRepository Payments, CreditNoteRepository Credits, DebitNoteRepository Debits);

    /// <summary>Repositories of another organization over the rig's database — a super admin's, or an ordinary user's.</summary>
    private static StrangerRepos Stranger(PurchaseRig rig, bool superAdmin)
    {
        var tenant    = new StaticTenantContext { OrganizationId = Guid.NewGuid(), IsSuperAdmin = superAdmin };
        var finance   = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);
        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(rig.DbName).Options, tenant);
        var ledger    = new SupplierLedgerService(finance);
        return new StrangerRepos(
            tenant.OrganizationId,
            new SupplierPaymentRepository(finance, ledger, Mock.Of<INotificationService>()),
            new CreditNoteRepository(finance, warehouse, ledger),
            new DebitNoteRepository(finance, warehouse, Mock.Of<IBackgroundJobClient>(), Mock.Of<INotificationService>(),
                Mock.Of<IAuditService>(), ledger));
    }
}
