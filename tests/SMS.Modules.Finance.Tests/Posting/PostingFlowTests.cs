using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.MultiCurrency;
using SMS.Modules.Finance.Tests.QuickBooks;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Modules.Finance.Tests.Posting.PostingAssert;

namespace SMS.Modules.Finance.Tests.Posting;

/// <summary>
/// A44 — the real counter-sale and customer payment services, committing to one in-memory database, with the posting
/// classes behind them and a recording posting gateway: which document each POS case produces, its key, the voids,
/// never-throw, and the pull-back (direct to QuickBooks, tenant-scoped).
/// </summary>
public sealed class PostingFlowTests
{
    private readonly ReceivablesWorld _world = new();
    private readonly ReceivablesDesk  _desk;
    private readonly Guid             _customer;
    private readonly RecordingPostingGateway    _posting = new();
    private readonly RecordingQuickBooksGateway _quickBooks = new();
    private readonly ListLogger<PosDocumentPostings> _posLog = new();
    private int _seq;

    public PostingFlowTests()
    {
        _desk     = _world.For(Guid.NewGuid());
        _customer = _desk.NewCustomer("Counter Customer");
    }

    // ── Which document each POS sale becomes ─────────────────────────────────

    [Fact]
    public async Task A_sale_paid_in_full_in_cash_like_tenders_is_one_sales_receipt_and_nothing_else()
    {
        var sale = await SaleAsync(Line(4m, 25m), Tender("CASH", 60m), Tender("CARD", 40m));

        var tx = _posting.Posts.Should().ContainSingle().Subject;
        tx.DocumentType.Should().Be(FinDocType.SalesReceipt);
        tx.IdempotencyKey.Should().Be($"pos:SalesReceipt:{sale.SalesInvoiceUuid}");
        Balanced(tx);
        Dr(tx, PostingRoles.Cash).Should().Be(60m);
        Dr(tx, PostingRoles.Bank).Should().Be(40m);
        _quickBooks.Calls.Should().BeEmpty("with a posting layer nothing goes straight to QuickBooks");
    }

    [Fact]
    public async Task A_sale_partly_on_account_is_the_invoice_and_its_cash_tender_as_a_payment()
    {
        var sale = await SaleAsync(Line(1m, 100m), Tender("CASH", 30m), Tender("ON_ACCOUNT", 70m));

        _posting.Posts.Select(p => p.DocumentType).Should().Equal(FinDocType.SalesInvoice, FinDocType.Payment);
        _posting.Posts[0].IdempotencyKey.Should().Be($"pos:SalesInvoice:{sale.SalesInvoiceUuid}");
        _posting.Posts[1].IdempotencyKey.Should().Be($"pos:Payment:{sale.PaymentUuids.Single()}");
        var payment = (PaymentPayload)_posting.Posts[1].Metadata!.Document!;
        payment.Applications.Should().ContainSingle().Which.InvoiceExternalId.Should().Be(sale.SalesInvoiceUuid.ToString());
        payment.SourceType.Should().Be("POS");
        foreach (var tx in _posting.Posts) Balanced(tx);
    }

    [Fact]
    public async Task Store_credit_and_exchange_tenders_make_an_invoice_and_are_not_posted_as_payments()
    {
        await SaleAsync(Line(1m, 100m), Tender("STORE_CREDIT", 60m), Tender("EXCHANGE", 25m), Tender("CASH", 15m));

        _posting.Posts.Select(p => p.DocumentType).Should().Equal(FinDocType.SalesInvoice, FinDocType.Payment);
        ((PaymentPayload)_posting.Posts[1].Metadata!.Document!).Amount.Should().Be(15m);
    }

    // ── Voids (REV-9: by the type the sale was posted as) ────────────────────

    [Fact]
    public async Task Voiding_a_receipt_sale_voids_the_sales_receipt_key()
    {
        var sale = await SaleAsync(Line(1m, 50m), Tender("CASH", 50m));

        await PostAsync(s => s.VoidSaleAsync(_desk.Org, sale.SalesInvoiceUuid, Guid.NewGuid(), "Rung up twice", Receivables.User));

        _posting.Voids.Should().ContainSingle().Which.IdempotencyKey.Should().Be($"pos:SalesReceipt:{sale.SalesInvoiceUuid}:void");
        _posting.All.Should().ContainSingle("the void's own credit note is not posted");
    }

    [Fact]
    public async Task Voiding_an_invoiced_sale_voids_the_invoice_and_its_tender_payments()
    {
        var sale = await SaleAsync(Line(1m, 100m), Tender("CARD", 40m), Tender("ON_ACCOUNT", 60m));

        await PostAsync(s => s.VoidSaleAsync(_desk.Org, sale.SalesInvoiceUuid, Guid.NewGuid(), "Customer left", Receivables.User));

        _posting.Voids.Select(v => v.IdempotencyKey).Should().Equal(
            $"pos:SalesInvoice:{sale.SalesInvoiceUuid}:void", $"pos:Payment:{sale.PaymentUuids.Single()}:void");
    }

    // ── Returns ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_return_refunded_in_cash_is_a_refund_receipt()
    {
        var sale = await SaleAsync(Line(2m, 50m), Tender("CASH", 100m));
        _posting.Posts.Clear();

        var ret = await ReturnAsync(sale, 0, 1m, 50m, Tender("CASH", 50m));

        var tx = _posting.Credits.Should().ContainSingle().Subject;
        tx.DocumentType.Should().Be(FinDocType.RefundReceipt);
        tx.IdempotencyKey.Should().Be($"pos:RefundReceipt:{ret.CreditNoteUuid}");
        Balanced(tx);
        Cr(tx, PostingRoles.Cash).Should().Be(50m);
        ((RefundReceiptPayload)tx.Metadata!.Document!).OriginalInvoiceExternalId.Should().BeNull("the sale is a receipt");
    }

    [Fact]
    public async Task A_return_on_account_of_an_invoiced_sale_is_a_credit_memo_applied_to_it()
    {
        var sale = await SaleAsync(Line(2m, 50m), Tender("ON_ACCOUNT", 100m));

        var ret = await ReturnAsync(sale, 0, 1m, 50m, Tender("ON_ACCOUNT", 50m));

        var tx = _posting.Credits.Should().ContainSingle().Subject;
        tx.DocumentType.Should().Be(FinDocType.CreditNote);
        tx.IdempotencyKey.Should().Be($"pos:CreditNote:{ret.CreditNoteUuid}");
        var memo = (CreditMemoPayload)tx.Metadata!.Document!;
        memo.ApplyToOriginal.Should().BeTrue();
        memo.OriginalInvoiceExternalId.Should().Be(sale.SalesInvoiceUuid.ToString());
        tx.Metadata.Properties![PostingProperties.AppliedAmount].Should().Be("50.00");
    }

    [Fact]
    public async Task A_return_refunded_partly_in_cash_is_a_credit_memo_flagged_for_the_accountant()
    {
        var sale = await SaleAsync(Line(2m, 50m), Tender("ON_ACCOUNT", 100m));

        await ReturnAsync(sale, 0, 1m, 50m, Tender("CASH", 20m), Tender("ON_ACCOUNT", 30m));

        var tx = _posting.Credits.Should().ContainSingle().Subject;
        tx.DocumentType.Should().Be(FinDocType.CreditNote);
        tx.Metadata!.Properties![PostingProperties.FlagForAccountant].Should().Contain("20.00");
        Balanced(tx);
    }

    // ── REV-8: the type is stable ────────────────────────────────────────────

    [Fact]
    public async Task A_bounced_cheque_tender_keeps_the_sale_a_receipt_and_a_re_post_keeps_one_document_key()
    {
        var sale = await SaleAsync(Line(1m, 80m), Tender("CHEQUE", 80m));
        await using (var scope = _desk.Scope())
        {
            var service = new CustomerPaymentService(scope.Db, scope.Ledger, Receivables.Names().Object, Receivables.Lookups().Object, _world.Clock,
                postings: new CustomerPaymentPostings(scope.Db, _quickBooks, new ListLogger<CustomerPaymentPostings>(), _posting));
            await service.BounceAsync(sale.PaymentUuids.Single(), "Returned by the bank", Receivables.User);
        }

        await using (var scope = _desk.Scope())
            await PosPostings(scope.Db).PublishSaleAsync(sale.SalesInvoiceUuid);

        _posting.Posts.Where(p => p.DocumentType == FinDocType.SalesReceipt).Select(p => p.IdempotencyKey).Distinct()
            .Should().Equal($"pos:SalesReceipt:{sale.SalesInvoiceUuid}");
        _posting.Voids.Should().BeEmpty("the receipt stands; a till tender of a receipt is not a payment of its own");

        // REV-15: the bounce reopens the receivable in the GL — a document-less Payment under the tender's key.
        var bounce = _posting.Posts.Should().ContainSingle(p => p.DocumentType == FinDocType.Payment).Subject;
        bounce.IdempotencyKey.Should().Be($"pos:Payment:{sale.PaymentUuids.Single()}");
        bounce.Metadata!.Document.Should().BeNull("QuickBooks skips it; the receipt is flagged there instead once synced");
        Balanced(bounce);
        Dr(bounce, PostingRoles.Receivable).Should().Be(80m);
        Cr(bounce, PostingRoles.Bank).Should().Be(80m);
        _quickBooks.Flags.Should().BeEmpty("the receipt is not synced in QuickBooks yet, so it is not flagged (that would drop it)");
    }

    // ── Back-office payments ─────────────────────────────────────────────────

    [Fact]
    public async Task Recording_allocating_and_bouncing_a_cheque_post_the_same_payment_key_then_void_it()
    {
        Guid invoice;
        await using (var scope = _desk.Scope())
        {
            var i = Receivables.Invoice(_desk.Org, _customer, "SINV-1", new DateTime(2026, 9, 1), 500m);
            await Receivables.Seed(scope.Db, i);
            invoice = i.UUID;
        }

        Guid paymentUuid;
        await using (var scope = _desk.Scope())
        {
            var service = PaymentService(scope);
            var recorded = await service.RecordPaymentAsync(_customer, 800m, "CHEQUE",
                new CustomerPaymentDetails("PKR", ChequeNumber: "000777", Allocations: []), Receivables.User);
            paymentUuid = recorded.PaymentUuid;
        }
        await using (var scope = _desk.Scope())
            await PaymentService(scope).AllocateAsync(paymentUuid, [new ManualPaymentAllocation(invoice, 500m)], Receivables.User);
        await using (var scope = _desk.Scope())
            await PaymentService(scope).BounceAsync(paymentUuid, "NSF", Receivables.User);

        _posting.Posts.Select(p => p.IdempotencyKey).Should().Equal($"finance:Payment:{paymentUuid}", $"finance:Payment:{paymentUuid}");
        ((PaymentPayload)_posting.Posts[0].Metadata!.Document!).Applications.Should().BeEmpty();
        ((PaymentPayload)_posting.Posts[1].Metadata!.Document!).Applications.Should().ContainSingle().Which.Amount.Should().Be(500m);
        _posting.Voids.Should().ContainSingle().Which.IdempotencyKey.Should().Be($"finance:Payment:{paymentUuid}:void");
    }

    // ── Never throws ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_throwing_posting_gateway_never_fails_the_sale()
    {
        _posting.Throw = new InvalidOperationException("Accounting is down");

        var sale = await SaleAsync(Line(1m, 50m), Tender("CASH", 50m));

        await using var scope = _desk.Scope();
        (await scope.Db.SalesInvoices.AsNoTracking().SingleAsync(i => i.UUID == sale.SalesInvoiceUuid)).Status.Should().Be("PAID");
        _posLog.At(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain(sale.SalesInvoiceUuid.ToString());
    }

    // ── Without a posting layer: straight to QuickBooks ──────────────────────

    [Fact]
    public async Task Without_a_posting_layer_a_receipt_goes_straight_to_QuickBooks_as_a_sales_receipt()
    {
        var sale = await SaleAsync(Line(1m, 50m), [Tender("CASH", 50m)], new NullFinancialPostingGateway());

        _quickBooks.SalesReceipts.Should().ContainSingle().Which.ExternalId.Should().Be(sale.SalesInvoiceUuid.ToString());
        _quickBooks.SalesInvoices.Should().BeEmpty();
    }

    // ── Pull-back ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_pull_back_is_direct_scoped_to_the_organization_and_keeps_pre_A44_invoices_invoices()
    {
        var receipt = await SaleAsync(Line(1m, 50m), Tender("CASH", 50m));
        var legacy  = await SaleAsync(Line(1m, 70m), Tender("CASH", 70m));
        _quickBooks.Known[SyncKind.SalesInvoice] = [legacy.SalesInvoiceUuid.ToString()];
        _posting.Posts.Clear();

        await using (var scope = _desk.Scope())
        {
            var pos = PosPostings(scope.Db);
            (await pos.PushAllAsync(SyncKind.SalesReceipt, null)).Should().Be(1);
            var invoices = new SalesInvoiceQuickBooksSource(scope.Db, _quickBooks, new ListLogger<SalesInvoiceQuickBooksSource>(), _posting);
            (await invoices.PushAllAsync(SyncKind.SalesInvoice, null)).Should().Be(1);
        }

        _quickBooks.SalesReceipts.Should().ContainSingle().Which.ExternalId.Should().Be(receipt.SalesInvoiceUuid.ToString());
        _quickBooks.SalesInvoices.Should().ContainSingle().Which.ExternalId.Should().Be(legacy.SalesInvoiceUuid.ToString());
        _posting.Posts.Should().BeEmpty("the pull-back never goes through the posting gateway (D-3, REV-3)");

        // Another organization's caller asks for these uuids: nothing.
        var other = _world.For(Guid.NewGuid());
        _quickBooks.Calls.Clear();
        await using (var scope = other.Scope())
            await PosPostings(scope.Db).PushAsync(SyncKind.SalesReceipt, [receipt.SalesInvoiceUuid.ToString()]);
        _quickBooks.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task REV_20_pushing_a_receipt_sale_as_a_sales_invoice_over_an_empty_placeholder_sends_no_invoice_and_the_receipt_still_goes()
    {
        var sale = await SaleAsync(Line(1m, 50m), Tender("CASH", 50m));
        var id = sale.SalesInvoiceUuid.ToString();
        // What "Send to QuickBooks" (POST sync/push) leaves for any id: an empty NotSynced map, no remote id, no hold marker.
        _quickBooks.Statuses[id] = new SyncStatus(SyncKind.SalesInvoice, id, SyncState.NotSynced, null, null, null, null, null, null);

        await using (var scope = _desk.Scope())
        {
            await new SalesInvoiceQuickBooksSource(scope.Db, _quickBooks, new ListLogger<SalesInvoiceQuickBooksSource>(), _posting)
                .PushAsync(SyncKind.SalesInvoice, [id]);
            await PosPostings(scope.Db).PushAsync(SyncKind.SalesReceipt, [id]);
        }

        _quickBooks.SalesInvoices.Should().BeEmpty("a placeholder is not a pre-A44 invoice: no second document, no doubled revenue");
        _quickBooks.SalesReceipts.Should().ContainSingle().Which.ExternalId.Should().Be(id);
    }

    [Theory]
    [InlineData("POS_SALES_HELD", "NotSynced", null)]
    [InlineData("HELD", "NotSynced", null)]
    [InlineData(null, "Synced", "177")]
    [InlineData(null, "Failed", null)]
    public async Task REV_20_a_genuine_pre_A44_POS_invoice_map_held_sent_or_failed_stays_an_invoice(string? code, string state, string? remoteId)
    {
        var sale = await SaleAsync(Line(1m, 50m), Tender("CASH", 50m));
        var id = sale.SalesInvoiceUuid.ToString();
        _quickBooks.Statuses[id] = new SyncStatus(SyncKind.SalesInvoice, id, Enum.Parse<SyncState>(state), remoteId, null, code, null, null, null);

        await using (var scope = _desk.Scope())
        {
            await new SalesInvoiceQuickBooksSource(scope.Db, _quickBooks, new ListLogger<SalesInvoiceQuickBooksSource>(), _posting)
                .PushAsync(SyncKind.SalesInvoice, [id]);
            await PosPostings(scope.Db).PushAsync(SyncKind.SalesReceipt, [id]);
        }

        _quickBooks.SalesInvoices.Should().ContainSingle().Which.ExternalId.Should().Be(id);
        _quickBooks.SalesReceipts.Should().BeEmpty();
    }

    [Fact]
    public void REV_20_only_a_map_that_carries_an_invoice_is_legacy()
    {
        SyncStatus S(SyncState state, string? remote = null, string? code = null) => new(SyncKind.SalesInvoice, "x", state, remote, null, code, null, null, null);

        PosDocuments.IsLegacyInvoice(S(SyncState.NotSynced)).Should().BeFalse();
        PosDocuments.IsLegacyInvoice(S(SyncState.NotSynced, code: "POS_SALES_HELD")).Should().BeTrue();
        PosDocuments.IsLegacyInvoice(S(SyncState.NotSynced, code: "held")).Should().BeTrue();
        PosDocuments.IsLegacyInvoice(S(SyncState.NotSynced, remote: "9")).Should().BeTrue();
        PosDocuments.IsLegacyInvoice(S(SyncState.Pending)).Should().BeTrue();
        PosDocuments.IsLegacyInvoice(S(SyncState.NotSynced, code: "TAX_UNMAPPED")).Should().BeFalse();
    }

    [Fact]
    public async Task The_sources_serve_their_kinds_only()
    {
        await using var scope = _desk.Scope();
        var pos = PosPostings(scope.Db);
        pos.Kinds.Should().BeEquivalentTo([SyncKind.SalesReceipt, SyncKind.CreditMemo, SyncKind.RefundReceipt]);
        await pos.Invoking(p => p.PushAsync(SyncKind.Payment, ["x"])).Should().ThrowAsync<ArgumentOutOfRangeException>();
        new CustomerPaymentPostings(scope.Db, _quickBooks, new ListLogger<CustomerPaymentPostings>()).Kinds.Should().Equal(SyncKind.Payment);
        new SupplierPaymentPostings(scope.Db, _quickBooks, new ListLogger<SupplierPaymentPostings>()).Kinds.Should().Equal(SyncKind.BillPayment);
        new SupplierNotePostings(scope.Db, _quickBooks, new ListLogger<SupplierNotePostings>()).Kinds.Should().Equal(SyncKind.VendorCredit);
    }

    // ══ Harness ══════════════════════════════════════════════════════════════

    private CustomerPaymentService PaymentService(ReceivablesScope scope) =>
        new(scope.Db, scope.Ledger, Receivables.Names().Object, Receivables.Lookups().Object, _world.Clock,
            postings: new CustomerPaymentPostings(scope.Db, _quickBooks, new ListLogger<CustomerPaymentPostings>(), _posting));

    private PosDocumentPostings PosPostings(FinanceDbContext db, IFinancialPostingGateway? posting = null)
    {
        posting ??= _posting;
        return new PosDocumentPostings(db, _quickBooks, _posLog, posting,
            new SalesInvoiceQuickBooksSource(db, _quickBooks, new ListLogger<SalesInvoiceQuickBooksSource>(), posting),
            new CustomerPaymentPostings(db, _quickBooks, new ListLogger<CustomerPaymentPostings>(), posting));
    }

    private static CounterSaleLine Line(decimal qty, decimal price) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Counter item", qty, price, 0m, qty * price, null, null, 0m, 0m, qty * price);

    private static CounterSaleTender Tender(string method, decimal amount) => new(Guid.NewGuid(), method, amount, null);

    private readonly Dictionary<Guid, CounterSaleRequest> _sales = [];

    private Task<CounterSaleResult> SaleAsync(CounterSaleLine line, params CounterSaleTender[] tenders) => SaleAsync(line, tenders, null);

    private async Task<CounterSaleResult> SaleAsync(CounterSaleLine line, CounterSaleTender[] tenders, IFinancialPostingGateway? posting)
    {
        await _desk.StockAsync(line.VariantUuid, 100m, 10m);
        var request = new CounterSaleRequest(_desk.Org, Guid.NewGuid(), $"POS-S1-20260920-{++_seq:0000}", _customer, _world.Clock.Value,
            [line], tenders, Receivables.User);
        var result = await PostAsync(s => s.PostSaleAsync(request), posting);
        _sales[result.SalesInvoiceUuid] = request;
        return result;
    }

    private Task<CounterReturnResult> ReturnAsync(CounterSaleResult sale, int index, decimal qty, decimal net, params CounterSaleTender[] refunds) =>
        PostAsync(s => s.PostReturnAsync(new CounterReturnRequest(_desk.Org, Guid.NewGuid(), $"RET-S1-20260920-{++_seq:0000}", sale.SalesInvoiceUuid,
            _world.Clock.Value, [new CounterReturnLine(_sales[sale.SalesInvoiceUuid].Lines[index].SourceLineUuid, Guid.NewGuid(), qty, net, 0m, net)],
            refunds, "Faulty", Receivables.User)));

    private async Task<T> PostAsync<T>(Func<CounterSaleService, Task<T>> call, IFinancialPostingGateway? posting = null)
    {
        await using var scope = _desk.Scope();

        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => _world.NamesFor(_desk.Org, ids));

        var service = new CounterSaleService(
            scope.Db, scope.Invoices, scope.Payments, scope.Ledger, scope.ProductLedger, names.Object,
            new FakeCurrencyService(ReceivablesWorld.PkrId, "PKR"), NullLogger<CounterSaleService>.Instance, _world.Clock,
            postings: PosPostings(scope.Db, posting));

        return await call(service);
    }
}
