using FluentAssertions;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Modules.Finance.Tests.QuickBooks;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Finance.Tests.Posting;

/// <summary>A44 (D-20, D-24, D-75) — supplier payments and notes through their postings classes, over an in-memory database.</summary>
public class PayablesPostingTests
{
    private readonly Guid _org = Guid.NewGuid();
    private readonly string _db = Guid.NewGuid().ToString();
    private readonly RecordingPostingGateway _posting = new();
    private readonly RecordingQuickBooksGateway _quickBooks = new();

    private SupplierPayment Payment(string status, string type = "STANDARD", Guid? bill = null) => new()
    {
        UUID = Guid.NewGuid(), OrganizationId = _org, TraceId = Guid.NewGuid(), PaymentNumber = $"SPAY-2026-{Random.Shared.Next(10000, 99999)}",
        SupplierId = Guid.NewGuid(), SupplierName = "Karachi Steel", PaymentDate = new DateTime(2026, 9, 20), PaymentMethod = "CHEQUE",
        ChequeNo = "000123", TotalAmount = 400m, Status = status, PaymentType = type, CurrencyCode = "PKR",
        PostedAt = status is "POSTED" or "BOUNCED" ? DateTime.UtcNow : null, BouncedAt = status == "BOUNCED" ? DateTime.UtcNow : null,
        Lines = bill is { } b ? [new SupplierPaymentLine { UUID = Guid.NewGuid(), OrganizationId = _org, InvoiceUuid = b, InvoiceNumber = "INV-1", AllocatedAmount = 400m }] : []
    };

    private SupplierPaymentPostings Payments(Guid? org = null, IFinancialPostingGateway? posting = null) =>
        new(Receivables.Db(org ?? _org, _db), _quickBooks, new ListLogger<SupplierPaymentPostings>(), posting ?? _posting);

    private SupplierNotePostings Notes(Guid? org = null) =>
        new(Receivables.Db(org ?? _org, _db), _quickBooks, new ListLogger<SupplierNotePostings>(), _posting);

    [Fact]
    public async Task A_posted_payment_is_a_bill_payment_a_bounced_one_is_voided_drafts_and_return_settlements_post_nothing()
    {
        var bill = Guid.NewGuid();
        var posted = Payment("POSTED", bill: bill);
        var bounced = Payment("BOUNCED", bill: bill);
        var draft = Payment("APPROVED");
        var settlement = Payment("POSTED", "PURCHASE_RETURN_SETTLEMENT");
        await Receivables.Seed(Receivables.Db(_org, _db), posted, bounced, draft, settlement);

        foreach (var p in new[] { posted, bounced, draft, settlement }) await Payments().PublishAsync(p.UUID);

        var tx = _posting.Posts.Should().ContainSingle().Subject;
        tx.IdempotencyKey.Should().Be($"finance:BillPayment:{posted.UUID}");
        PostingAssert.Balanced(tx);
        ((BillPaymentPayload)tx.Metadata!.Document!).Applications.Should().ContainSingle().Which.BillExternalId.Should().Be(bill.ToString());
        _posting.Voids.Should().ContainSingle().Which.IdempotencyKey.Should().Be($"finance:BillPayment:{bounced.UUID}:void");
    }

    [Fact]
    public async Task The_pull_back_goes_straight_to_QuickBooks_for_this_organization_only()
    {
        var posted = Payment("POSTED");
        await Receivables.Seed(Receivables.Db(_org, _db), posted);

        await Payments(Guid.NewGuid()).PushAsync(SyncKind.BillPayment, [posted.UUID.ToString()]);
        _quickBooks.BillPayments.Should().BeEmpty("another organization's caller gets nothing (security audit Q1)");

        (await Payments().PushAllAsync(SyncKind.BillPayment, null)).Should().Be(1);
        _quickBooks.BillPayments.Should().ContainSingle();
        _posting.Posts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_throwing_posting_gateway_is_logged_never_thrown()
    {
        var posted = Payment("POSTED");
        await Receivables.Seed(Receivables.Db(_org, _db), posted);
        _posting.Throw = new InvalidOperationException("down");
        var log = new ListLogger<SupplierPaymentPostings>();

        await new SupplierPaymentPostings(Receivables.Db(_org, _db), _quickBooks, log, _posting)
            .Invoking(p => p.PublishAsync(posted.UUID)).Should().NotThrowAsync();

        log.At(Microsoft.Extensions.Logging.LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain(posted.UUID.ToString());
    }

    [Fact]
    public async Task Notes_are_vendor_credits_in_their_invoices_currency_and_a_written_off_debit_note_is_reversed()
    {
        var invoice = new Invoice
        {
            UUID = Guid.NewGuid(), OrganizationId = _org, TraceId = Guid.NewGuid(), InvoiceNumber = "INV-1", SupplierId = Guid.NewGuid(), SupplierName = "S",
            Currency = "USD", ExchangeRate = 280m, BaseCurrencyCode = "PKR", MatchStatus = "Approved", CreatedBy = 1
        };
        var cn = new CreditNote
        {
            UUID = Guid.NewGuid(), OrganizationId = _org, CreditNoteNumber = "CN-2026-00001", SupplierCreditNoteNo = "X-1", SroNumber = "SRO-1",
            SupplierId = invoice.SupplierId, SupplierName = "S", InvoiceUuid = invoice.UUID, CreditDate = new DateTime(2026, 9, 5), CreditAmount = 50m, CreatedBy = 1
        };
        var dn = new DebitNote
        {
            UUID = Guid.NewGuid(), OrganizationId = _org, DebitNoteNumber = "DN-2026-00001", SroNumber = "SRO-2", SupplierId = invoice.SupplierId,
            SupplierName = "S", DebitReason = "DAMAGED", DebitAmount = 30m, Status = "WRITTEN_OFF", IssuedAt = DateTime.UtcNow, CreatedBy = 1
        };
        await Receivables.Seed(Receivables.Db(_org, _db), invoice, cn, dn);

        await Notes().PublishCreditNoteAsync(cn.UUID);
        await Notes().PublishDebitNoteAsync(dn.UUID);
        await Notes().PublishDebitNoteStatusAsync(dn.UUID, "WRITTEN_OFF", 7);
        await Notes().PublishDebitNoteStatusAsync(dn.UUID, "DISPUTED", 7);

        var credit = _posting.Credits.Should().ContainSingle("a written-off debit note is not (re)sent").Subject;
        credit.IdempotencyKey.Should().Be($"finance:VendorCredit:{cn.UUID}");
        credit.CurrencyCode.Should().Be("USD");
        credit.ExchangeRate.Should().Be(280m);
        var reverse = _posting.Reverses.Should().ContainSingle("DISPUTED posts nothing (D-75)").Subject;
        reverse.IdempotencyKey.Should().Be($"finance:VendorCredit:{dn.UUID}:void");
        reverse.Reason.Should().Contain("written off").And.Contain("by hand");
    }
}
