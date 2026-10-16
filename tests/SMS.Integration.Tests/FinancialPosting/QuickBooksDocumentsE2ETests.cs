using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.Pos;
using SMS.Integration.Tests.SapAlignment;
using SMS.Modules.Integration.Core.Providers;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Integration.Tests.FinancialPosting.Fp;
using static SMS.Integration.Tests.Pos.PosKit;

namespace SMS.Integration.Tests.FinancialPosting;

/// <summary>
/// A44 (QA) — the new QuickBooks document kinds through the real host with only Intuit faked (D-19..D-24, D-26):
/// <list type="bullet">
/// <item>D-19, D-15, BR-12 — <see cref="A_customer_payment_waits_for_its_invoice_links_to_it_and_takes_the_deposit_account"/>.</item>
/// <item>D-20, D-26 — <see cref="A_posted_supplier_payment_is_a_bill_payment_and_a_bounce_undoes_it"/>.</item>
/// <item>D-24 — <see cref="Supplier_credit_and_debit_notes_are_vendor_credits"/>.</item>
/// <item>D-21..D-23 — <see cref="A_cash_POS_sale_is_a_sales_receipt_a_cash_return_a_refund_receipt_and_a_void_undoes_it"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~QuickBooksDocumentsE2ETests</c>.</para>
/// </summary>
public sealed class QuickBooksDocumentsE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public QuickBooksDocumentsE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A44");
    }

    private T Entity<T>(SyncKind kind, string? remoteId) where T : class =>
        (_f.Remote(kind, remoteId) ?? throw new InvalidOperationException($"QuickBooks has no {kind} {remoteId}")).Entity
        .Should().BeOfType<T>().Subject;

    [Fact]
    public async Task A_customer_payment_waits_for_its_invoice_links_to_it_and_takes_the_deposit_account()
    {
        var o = await (await _root.NewOrgAsync("QPAY")).ConnectLiveAsync();
        var k = o.K;
        var (bank1, bank2) = await k.BankAccountsAsync();
        await k.Ok(k.SetSyncControl("Payment", enabled: true), "Payment on");
        await k.Ok(k.PutDefaults(("DepositToAccountRef.CASH", bank1), ("DepositToAccountRef.BANK_TRANSFER", bank1)), "deposit accounts");
        var (_, _, customer, item) = await k.StockedWorldAsync("QPAY", price: 100m);

        // invoice issued and paid before anything ran: the payment waits for the invoice (and the customer)
        var inv = await k.IssuedInvoiceAsync(customer, item, 2m, o.O.Pkr);
        var total = inv.D("grandTotal");
        var pay = await k.RecordPaymentAsync(customer, total + 66m, "CASH", (inv.G("uuid"), total));
        var payId = pay.G("paymentUuid");
        var waiting = await _f.MapAsync(o.OrgId, payId, "Payment");
        waiting.State.Should().BeOneOf(new[] { "WaitingOnDependency", "Pending" }, waiting.ToString());

        _f.Company.ResetCalls();
        await _f.RunJobsAsync(5);
        var invMap = await _f.MapAsync(o.OrgId, inv.G("uuid"), "SalesInvoice");
        var payMap = await _f.MapAsync(o.OrgId, payId, "Payment");
        (invMap.State, payMap.State).Should().Be(("Synced", "Synced"), $"{invMap} / {payMap}");

        var creates = _f.Company.Writes.Where(w => w.Operation == "Create").Select(w => w.Kind).ToList();
        creates.IndexOf(SyncKind.SalesInvoice).Should().BeLessThan(creates.IndexOf(SyncKind.Payment), "the payment is sent after its invoice");

        var qboPay = Entity<RemotePayment>(SyncKind.Payment, payMap.RemoteId);
        qboPay.TotalAmount.Should().Be(total + 66m, "the unapplied 66 stays as customer credit in QuickBooks");
        qboPay.Lines.Should().ContainSingle().Which.Should().Match<RemotePaymentLine>(l => l.InvoiceId == invMap.RemoteId && l.Amount == total);
        qboPay.CustomerId.Should().Be((await _f.MapAsync(o.OrgId, customer.Uuid, "Customer")).RemoteId);
        qboPay.DepositToAccountId.Should().Be(bank1, "D-15 DepositToAccountRef.CASH");
        qboPay.ReferenceNumber.Should().Be(pay.S("paymentNumber"));

        // the status lookup (what the SCM badge reads) shows it synced with its QuickBooks id
        var lookup = await k.Ok(k.Post($"{Qb}/status/lookup", new { Kind = "Payment", ExternalIds = new[] { payId.ToString() } }), "status lookup");
        lookup.A("items").Should().ContainSingle().Which.S("state").Should().Be("Synced");

        // BR-12 — a Payment override wins over the provider default
        await k.Ok(k.PutOverrides(("Payment", "DepositToAccountRef.CASH", bank2)), "Payment override");
        var pay2 = await k.RecordPaymentAsync(customer, 10m, "CASH");
        await _f.RunJobsAsync(3);
        var pay2Map = await _f.MapAsync(o.OrgId, pay2.G("paymentUuid"), "Payment");
        pay2Map.State.Should().Be("Synced", pay2Map.ToString());
        var qboPay2 = Entity<RemotePayment>(SyncKind.Payment, pay2Map.RemoteId);
        qboPay2.DepositToAccountId.Should().Be(bank2, "the document override wins (BR-12)");
        qboPay2.Lines.Should().BeEmpty("nothing allocated: all unapplied credit");

        // unchanged → nothing re-sent (fingerprint)
        _f.Company.ResetCalls();
        await _f.RunJobsAsync(2);
        _f.WritesOf(SyncKind.Payment).Should().BeEmpty();
    }

    [Fact]
    public async Task A_posted_supplier_payment_is_a_bill_payment_and_a_bounce_undoes_it()
    {
        var o = await (await _root.NewOrgAsync("QBP")).ConnectLiveAsync();
        var k = o.K;
        var (bank1, _) = await k.BankAccountsAsync();

        // D-14: BillPayment is off by default (spec §22.3) — turned on here
        (await k.SyncControlRowAsync("BillPayment")).B("isEnabled").Should().BeFalse("FSD default for BillPayment");
        await k.Ok(k.SetSyncControl("BillPayment", enabled: true), "BillPayment on");
        await k.Ok(k.SetSyncControl("PurchaseBill", enabled: true), "PurchaseBill on");
        await k.Ok(k.PutDefaults(("BankAccountRef.BANK_TRANSFER", bank1), ("BankAccountRef.CHEQUE", bank1)), "bank accounts");

        var vendor = await k.CreateVendorAsync("QBP Vendor");
        var bill = await k.ApprovedBillAsync(vendor, 1000m);
        var sp = await k.PostedSupplierPaymentAsync(vendor, bill, 600m, "CHEQUE"); // only a cheque can bounce
        await _f.RunJobsAsync(5);

        var billMap = await _f.MapAsync(o.OrgId, bill, "Bill");
        var spMap = await _f.MapAsync(o.OrgId, sp, "BillPayment");
        (billMap.State, spMap.State).Should().Be(("Synced", "Synced"), $"{billMap} / {spMap}");
        var qbo = Entity<RemoteBillPayment>(SyncKind.BillPayment, spMap.RemoteId);
        qbo.TotalAmount.Should().Be(600m);
        qbo.Lines.Should().ContainSingle().Which.Should().Match<RemoteBillPaymentLine>(l => l.BillId == billMap.RemoteId && l.Amount == 600m);
        (qbo.PayType, qbo.AccountId).Should().Be((RemoteBillPayType.Check, bank1));
        qbo.VendorId.Should().Be((await _f.MapAsync(o.OrgId, vendor.Uuid, "Vendor")).RemoteId);

        // bounced after posting → undone in QuickBooks (D-20/D-26), once
        _f.Company.ResetCalls();
        await k.Ok(k.Post($"/api/supplier-payments/{sp}/bounce"), "bounce");
        await _f.RunJobsAsync(3);
        _f.Company.Writes.Where(w => w.Operation is "Void" or "Delete" && w.Kind == SyncKind.BillPayment)
            .Should().ContainSingle("a bill payment is undone once (QuickBooks deletes it, D-51)");
        (await _f.MapAsync(o.OrgId, sp, "BillPayment")).State.Should().Be("Voided");
    }

    [Fact]
    public async Task Supplier_credit_and_debit_notes_are_vendor_credits()
    {
        var o = await (await _root.NewOrgAsync("QVC")).ConnectLiveAsync();
        var k = o.K;
        await k.Ok(k.SetSyncControl("VendorCredit", enabled: true), "VendorCredit on");
        var vendor = await k.CreateVendorAsync("QVC Vendor");

        var cn = await k.SupplierCreditNoteAsync(await k.ReadySroAsync(vendor, 2m, 25m), 50m);
        var dn = await k.DebitNoteAsync(await k.ReadySroAsync(vendor, 1m, 30m), 30m);
        await _f.RunJobsAsync(4);

        foreach (var (uuid, amount, what) in new[] { (cn, 50m, "credit note"), (dn, 30m, "debit note") })
        {
            var map = await _f.MapAsync(o.OrgId, uuid, "VendorCredit");
            map.State.Should().Be("Synced", $"{what}: {map}");
            var vc = Entity<RemoteVendorCredit>(SyncKind.VendorCredit, map.RemoteId);
            vc.Lines.Sum(l => l.Amount).Should().Be(amount, what);
            vc.Lines.Should().OnlyContain(l => l.AccountId == "3" && l.ItemId == null, $"{what}: account lines on the default expense account (D-24)");
        }

        // D-75 (SRC deviation from D-24): DISPUTED moves nothing; WRITTEN_OFF after sending → flagged for the accountant
        _f.Company.ResetCalls();
        await k.Ok(k.DebitNoteStatus(dn, "DISPUTED"), "dispute the debit note");
        await _f.RunJobsAsync(2);
        _f.WritesOf(SyncKind.VendorCredit).Should().BeEmpty();
        (await _f.MapAsync(o.OrgId, dn, "VendorCredit")).State.Should().Be("Synced", "a dispute posts nothing (D-75)");
        await k.Ok(k.DebitNoteStatus(dn, "WRITTEN_OFF"), "write the debit note off");
        await _f.RunJobsAsync(2);
        _f.WritesOf(SyncKind.VendorCredit).Should().BeEmpty("a vendor credit is never voided through the integration");
        var flagged = await _f.MapAsync(o.OrgId, dn, "VendorCredit");
        (flagged.State, flagged.LastErrorCode).Should().Be(("NeedsResolution", "ACCOUNTANT_ACTION_NEEDED"), flagged.ToString());
    }

    [Fact]
    public async Task A_cash_POS_sale_is_a_sales_receipt_a_cash_return_a_refund_receipt_and_a_void_undoes_it()
    {
        var w = await _root.PosWorldAsync("QPOS");
        var o = await new FpOrg(w.O, null).ConnectLiveAsync();
        var k = o.K;
        var (bank1, bank2) = await k.BankAccountsAsync();
        await k.Ok(k.SetSyncControl("POSSale", enabled: true, confirmRelease: true), "POS sales on");
        await k.Ok(k.SetSyncControl("POSReturn", enabled: true, confirmRelease: true), "POS returns on");
        await k.Ok(k.PutDefaults(("DepositToAccountRef.CASH", bank1), ("RefundFromAccountRef", bank2)), "deposit / refund accounts");

        var shift = await w.OpenShiftAsync(100m);
        var sup = w.Supervisor.Client;

        // D-21 — paid in full in cash: one SalesReceipt, no invoice, no payment
        var sale = await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m)], [Pay("CASH", 23.40m)]), sup);
        var order = await w.OrderAsync(sale.G("orderUuid"));
        var invoiceUuid = order.G("salesInvoiceUuid");
        await _f.RunJobsAsync(4);
        var receiptMap = await _f.MapAsync(o.OrgId, invoiceUuid, "SalesReceipt");
        receiptMap.State.Should().Be("Synced", receiptMap.ToString());
        (await _f.MapOrNullAsync(o.OrgId, invoiceUuid.ToString(), "SalesInvoice")).Should().BeNull("a fully paid cash sale is not an invoice");
        var receipt = Entity<RemoteSalesReceipt>(SyncKind.SalesReceipt, receiptMap.RemoteId);
        (receipt.Lines.Sum(l => l.Amount) - receipt.DiscountAmount).Should().Be(20m);
        receipt.DepositToAccountId.Should().Be(bank1);
        foreach (var p in order.A("payments"))
            (await _f.MapOrNullAsync(o.OrgId, p.G("uuid").ToString(), "Payment")).Should().BeNull("the tenders are inside the receipt");

        // D-23 — a cash return: RefundReceipt from the refund account
        var line = order.A("lines").Single();
        var ret = await k.Ok(k.Post($"/api/pos/orders/{order.G("uuid")}/return", new
        {
            localId = Guid.NewGuid().ToString("N"), reason = "damaged",
            lines = new[] { new { originalLineUuid = line.G("uuid"), quantity = 1m } },
            refunds = new[] { new { method = "CASH", amount = 11.70m } }
        }, sup), "cash return");
        var creditNote = ret.Has("creditNoteUuid") ? ret.G("creditNoteUuid") : ret.G("salesInvoiceUuid");
        await _f.RunJobsAsync(4);
        var refundMap = await _f.MapAsync(o.OrgId, creditNote, "RefundReceipt");
        refundMap.State.Should().Be("Synced", refundMap.ToString());
        var refund = Entity<RemoteRefundReceipt>(SyncKind.RefundReceipt, refundMap.RemoteId);
        refund.RefundFromAccountId.Should().Be(bank2);
        (refund.Lines.Sum(l => l.Amount) - refund.DiscountAmount).Should().Be(10m);

        // D-22 — a void of another cash sale voids its SalesReceipt
        var v = await w.SellAsync(w.Order(shift, [Line(w.B, 1m, 5.99m)], [Pay("CASH", 7.01m)]), sup);
        var vo = await w.OrderAsync(v.G("orderUuid"));
        await _f.RunJobsAsync(4);
        var vMap = await _f.MapAsync(o.OrgId, vo.G("salesInvoiceUuid"), "SalesReceipt");
        vMap.State.Should().Be("Synced");
        _f.Company.ResetCalls();
        await k.Ok(k.Patch($"/api/pos/orders/{vo.G("uuid")}/void", new { reason = "wrong item" }, sup), "void");
        await _f.RunJobsAsync(3);
        _f.Company.Writes.Should().ContainSingle(c => c.Operation == "Void" && c.Kind == SyncKind.SalesReceipt);
        (await _f.MapAsync(o.OrgId, vo.G("salesInvoiceUuid"), "SalesReceipt")).State.Should().Be("Voided");
        _f.Company.Writes.Should().NotContain(c => c.Kind == SyncKind.RefundReceipt || c.Kind == SyncKind.CreditMemo,
            "the void's own credit note is not sent as a second credit (the receipt is voided)");
    }
}
