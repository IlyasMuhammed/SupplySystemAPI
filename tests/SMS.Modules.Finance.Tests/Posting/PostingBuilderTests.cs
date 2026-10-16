using System.Text.Json;
using FluentAssertions;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Modules.Finance.Tests.Posting.PostingAssert;
using R = SMS.Modules.Finance.Integration.Posting.PostingRoles;

namespace SMS.Modules.Finance.Tests.Posting;

/// <summary>A44 — the GL lines and documents each Finance document posts (API-CONTRACT §3), built from hand-made rows.</summary>
public class PostingBuilderTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static SalesInvoice Invoice(string source = SalesInvoiceSources.SaleOrder, decimal discount = 0m, string currency = "PKR",
        decimal? rate = null, string? baseCode = null, params (decimal Qty, decimal Price, decimal Disc, decimal Tax, string? Code, decimal? LineTax)[] lines)
    {
        var invoice = Receivables.Invoice(Org, Guid.NewGuid(), "SINV-20260920-0001", new DateTime(2026, 9, 20), 0m, currency: currency);
        invoice.SourceType = source;
        invoice.SourceNumber = source == SalesInvoiceSources.Pos ? "POS-1" : null;
        invoice.ExchangeRate = rate;
        invoice.BaseCurrencyCode = baseCode;
        var no = 1;
        foreach (var l in lines)
            invoice.Lines.Add(new SalesInvoiceLine
            {
                UUID = Guid.NewGuid(), LineNo = no++, VariantUuid = Guid.NewGuid(), Description = "Item",
                Quantity = l.Qty, UnitPrice = l.Price, DiscountPercent = l.Disc, TaxPercent = l.Tax, TaxCode = l.Code, TaxAmount = l.LineTax,
                DiscountAmount = source == SalesInvoiceSources.Pos ? 0m : null
            });

        var gross = Math.Round(invoice.Lines.Sum(l => l.Quantity * l.UnitPrice), 2, MidpointRounding.AwayFromZero);
        var lineDisc = SalesInvoicePayloadFactory.LineDiscounts(invoice.Lines);
        var tax = invoice.Lines.Sum(l => l.TaxAmount ?? Math.Round(l.Quantity * l.UnitPrice * (1 - l.DiscountPercent / 100m) * l.TaxPercent / 100m, 2));
        invoice.Subtotal = gross;
        invoice.DiscountAmount = lineDisc + discount;
        invoice.TaxAmount = tax;
        invoice.GrandTotal = gross - invoice.DiscountAmount + tax;
        invoice.BalanceDue = invoice.GrandTotal;
        return invoice;
    }

    // ── Sales invoice ────────────────────────────────────────────────────────

    [Fact]
    public void An_issued_invoice_debits_the_receivable_and_credits_revenue_and_tax_split_by_code()
    {
        var invoice = Invoice(lines: [(10m, 100m, 10m, 16m, "GST16", null), (5m, 40m, 0m, 5m, "GST5", null), (1m, 99.99m, 0m, 0m, null, null)]);

        var tx = SalesPostings.Invoice(invoice);

        Balanced(tx);
        tx.DocumentType.Should().Be(FinDocType.SalesInvoice);
        tx.IdempotencyKey.Should().Be($"finance:SalesInvoice:{invoice.UUID}");
        tx.PartnerType.Should().Be(SMS.Shared.Finance.Posting.PartnerType.Customer);
        Dr(tx, R.Receivable).Should().Be(invoice.GrandTotal);
        Cr(tx, R.TaxPayable).Should().Be(invoice.TaxAmount);
        tx.Lines.Where(l => l.AccountRole == R.TaxPayable).Select(l => (l.TaxCode, l.Credit)).Should().BeEquivalentTo(
            [((string?)"GST16", 144m), ("GST5", 10m)]);
        Cr(tx, R.Revenue).Should().Be(invoice.GrandTotal - invoice.TaxAmount);
        tx.Lines.Should().NotContain(l => l.AccountRole == R.DiscountGiven, "SCM has no header discount: line discounts are net in revenue");
        tx.ExchangeRate.Should().BeNull("a home-currency invoice");
    }

    [Fact]
    public void The_document_is_exactly_the_existing_payload_so_its_fingerprint_is_unchanged()
    {
        var invoice = Invoice(lines: [(3m, 12.345m, 2.5m, 16m, "GST", null)]);

        var document = SalesPostings.Invoice(invoice).Metadata!.Document;

        document.Should().BeOfType<SalesInvoicePayload>();
        JsonSerializer.Serialize((SalesInvoicePayload)document!).Should().Be(JsonSerializer.Serialize(SalesInvoicePayloadFactory.Build(invoice)));
    }

    [Fact]
    public void A_POS_invoice_posts_its_discount_to_discount_given_under_the_pos_module()
    {
        var invoice = Invoice(SalesInvoiceSources.Pos, discount: 7.5m, lines: [(2m, 50m, 0m, 15m, "GST", 14m)]);

        var tx = SalesPostings.Invoice(invoice);

        Balanced(tx);
        tx.SourceModule.Should().Be("pos");
        tx.IdempotencyKey.Should().StartWith("pos:SalesInvoice:");
        Dr(tx, R.DiscountGiven).Should().Be(7.5m);
        Cr(tx, R.Revenue).Should().Be(100m, "gross: the discount is its own line");
        Cr(tx, R.TaxPayable).Should().Be(14m);
    }

    [Fact]
    public void A_foreign_invoice_carries_its_locked_rate_and_lines_stay_in_document_currency()
    {
        var invoice = Invoice(currency: "USD", rate: 278.5m, baseCode: "PKR", lines: [(1m, 100m, 0m, 0m, null, null)]);

        var tx = SalesPostings.Invoice(invoice);

        tx.CurrencyCode.Should().Be("USD");
        tx.ExchangeRate.Should().Be(278.5m);
        Dr(tx, R.Receivable).Should().Be(100m);
    }

    [Fact]
    public void A_cancel_is_a_void_of_the_same_document_key()
    {
        var invoice = Invoice(lines: [(1m, 10m, 0m, 0m, null, null)]);

        var request = SalesPostings.Void(invoice, FinDocType.SalesInvoice, "Wrong customer", 7, DateTime.UtcNow);

        request.IdempotencyKey.Should().Be($"finance:SalesInvoice:{invoice.UUID}:void");
    }

    // ── POS sale receipt and returns ─────────────────────────────────────────

    [Fact]
    public void A_receipt_debits_cash_and_bank_per_tender_and_books_no_receivable()
    {
        var invoice = Invoice(SalesInvoiceSources.Pos, lines: [(4m, 25m, 0m, 0m, null, null)]);
        PosTender[] tenders = [new(Guid.NewGuid(), "CPAY-1", "CASH", 60m), new(Guid.NewGuid(), "CPAY-2", "CARD", 40m)];

        var tx = SalesPostings.SalesReceipt(invoice, tenders);

        Balanced(tx);
        tx.DocumentType.Should().Be(FinDocType.SalesReceipt);
        tx.IdempotencyKey.Should().Be($"pos:SalesReceipt:{invoice.UUID}");
        Dr(tx, R.Cash).Should().Be(60m);
        Dr(tx, R.Bank).Should().Be(40m);
        tx.Lines.Should().NotContain(l => l.AccountRole == R.Receivable);
        var receipt = tx.Metadata!.Document.Should().BeOfType<SalesReceiptPayload>().Subject;
        receipt.Method.Should().Be("CASH", "the largest tender decides the deposit account");
        receipt.SourceType.Should().Be("POS");
        receipt.ExpectedTotal.Should().Be(100m);
        receipt.PrivateNote.Should().Contain("CARD 40.00");
    }

    [Theory]
    [InlineData(new[] { "CASH" }, new[] { 100.0 }, true)]
    [InlineData(new[] { "CASH", "CARD", "MOBILE", "CHEQUE", "BANK_TRANSFER" }, new[] { 20.0, 20, 20, 20, 20 }, true)]
    [InlineData(new[] { "CASH" }, new[] { 60.0 }, false)]                         // the rest ON_ACCOUNT (no payment)
    [InlineData(new[] { "CASH", "STORE_CREDIT" }, new[] { 50.0, 50 }, false)]
    [InlineData(new[] { "EXCHANGE" }, new[] { 100.0 }, false)]
    public void Which_sale_is_a_receipt(string[] methods, double[] amounts, bool receipt)
    {
        var tenders = methods.Select((m, i) => new PosTender(Guid.NewGuid(), $"P{i}", m, (decimal)amounts[i])).ToList();

        PosDocuments.SaleIsReceipt(100m, tenders).Should().Be(receipt);
    }

    [Theory]
    [InlineData(new[] { "CASH", "CARD" }, new[] { 70.0, 30 }, "RefundReceipt")]
    [InlineData(new string[0], new double[0], "CreditMemo")]          // all ON_ACCOUNT
    [InlineData(new[] { "STORE_CREDIT" }, new[] { 100.0 }, "CreditMemo")]
    [InlineData(new[] { "CASH", "STORE_CREDIT" }, new[] { 40.0, 60 }, "Mixed")]
    [InlineData(new[] { "CASH" }, new[] { 40.0 }, "Mixed")]          // the rest ON_ACCOUNT
    public void Which_return_is_a_refund_receipt_or_a_credit_memo(string[] methods, double[] amounts, string kind)
    {
        var refunds = methods.Select((m, i) => new PosTender(Guid.NewGuid(), $"R{i}", m, (decimal)amounts[i])).ToList();

        PosDocuments.ClassifyCredit(100m, refunds).ToString().Should().Be(kind);
    }

    private static (SalesInvoice CreditNote, SalesInvoice Original) Return(decimal credited = 0m)
    {
        var original = Invoice(SalesInvoiceSources.Pos, discount: 10m, lines: [(2m, 50m, 0m, 15m, "GST", 13.5m)]);
        var cn = Receivables.Invoice(Org, original.PartnerId, "SCN-20260920-0001", new DateTime(2026, 9, 21), 0m, status: SalesInvoiceStatuses.CreditNote);
        cn.SourceType = SalesInvoiceSources.Pos;
        cn.SourceNumber = "RET-1";
        cn.OriginalInvoiceUuid = original.UUID;
        cn.Lines.Add(new SalesInvoiceLine
        {
            UUID = Guid.NewGuid(), LineNo = 1, VariantUuid = original.Lines.First().VariantUuid, Description = "Item",
            Quantity = 1m, UnitPrice = 50m, TaxPercent = 15m, TaxCode = "GST", TaxAmount = 6.75m, LineTotal = 51.75m
        });
        cn.TaxAmount = 6.75m;
        cn.GrandTotal = 51.75m;            // net 45 (the sale's discount on one unit is 5)
        cn.Subtotal = 45m;
        cn.CreditedAmount = credited;
        return (cn, original);
    }

    [Fact]
    public void A_cash_refunded_return_is_a_refund_receipt_crediting_cash_with_the_sales_discount_in_its_header()
    {
        var (cn, original) = Return();

        var tx = SalesPostings.CreditNote(cn, original, [new(Guid.NewGuid(), "CRF-1", "CASH", 51.75m)], originalIsInvoice: false);

        Balanced(tx);
        tx.DocumentType.Should().Be(FinDocType.RefundReceipt);
        tx.IdempotencyKey.Should().Be($"pos:RefundReceipt:{cn.UUID}");
        Cr(tx, R.Cash).Should().Be(51.75m);
        Dr(tx, R.TaxPayable).Should().Be(6.75m);
        Dr(tx, R.Revenue).Should().Be(50m);
        Cr(tx, R.DiscountGiven).Should().Be(5m);
        var refund = tx.Metadata!.Document.Should().BeOfType<RefundReceiptPayload>().Subject;
        refund.HeaderDiscountAmount.Should().Be(5m);
        (Math.Round(refund.Lines.Sum(l => l.Quantity * l.UnitPrice), 2) - refund.HeaderDiscountAmount + refund.ExpectedTaxAmount)
            .Should().Be(refund.ExpectedTotal, "the gateway's arithmetic");
        refund.OriginalInvoiceExternalId.Should().BeNull("the sale went as a receipt: nothing to link in QuickBooks");
        tx.Metadata.LinkedDocumentId.Should().Be(original.UUID);
    }

    [Fact]
    public void An_on_account_return_is_a_credit_memo_applied_to_the_invoiced_sale()
    {
        var (cn, original) = Return(credited: 51.75m);

        var tx = (FinancialTransaction)SalesPostings.CreditNote(cn, original, [], originalIsInvoice: true);

        Balanced(tx);
        tx.DocumentType.Should().Be(FinDocType.CreditNote);
        tx.IdempotencyKey.Should().Be($"pos:CreditNote:{cn.UUID}");
        Cr(tx, R.Receivable).Should().Be(51.75m);
        var memo = tx.Metadata!.Document.Should().BeOfType<CreditMemoPayload>().Subject;
        memo.ApplyToOriginal.Should().BeTrue();
        memo.OriginalInvoiceExternalId.Should().Be(original.UUID.ToString());
        tx.Metadata.Properties![PostingProperties.AppliedAmount].Should().Be("51.75");
        tx.Metadata.Properties.Should().NotContainKey(PostingProperties.FlagForAccountant);
    }

    [Fact]
    public void A_mixed_refund_is_one_credit_memo_flagged_for_the_accountant_and_the_GL_books_the_cash_refund_too()
    {
        var (cn, original) = Return();
        PosTender[] refunds = [new(Guid.NewGuid(), "CRF-7", "CASH", 20m), new(Guid.NewGuid(), "CRF-8", "STORE_CREDIT", 31.75m)];

        var tx = SalesPostings.CreditNote(cn, original, refunds, originalIsInvoice: true);

        Balanced(tx);
        tx.DocumentType.Should().Be(FinDocType.CreditNote);
        Cr(tx, R.Cash).Should().Be(20m);
        Dr(tx, R.Receivable).Should().Be(20m, "the cash paid back puts that much on the receivable again");
        Cr(tx, R.Receivable).Should().Be(51.75m);
        tx.Metadata!.Properties![PostingProperties.FlagForAccountant].Should().Contain("CRF-7").And.Contain("20.00").And.Contain("by hand");
        ((CreditMemoPayload)tx.Metadata.Document!).ApplyToOriginal.Should().BeFalse("nothing was applied to the sale's balance");
    }

    // ── Payments ─────────────────────────────────────────────────────────────

    private static CustomerPayment Payment(string method = "BANK_TRANSFER", decimal amount = 1000m, Guid? source = null,
        string currency = "PKR", decimal? rate = null, decimal? difference = null) => new()
    {
        UUID = Guid.NewGuid(), OrganizationId = Org, PartnerId = Guid.NewGuid(), PartnerName = "Acme", PaymentNumber = "CPAY-20260920-0001",
        PaymentDate = new DateTime(2026, 9, 20), Amount = amount, PaymentMethod = method, CurrencyCode = currency, ExchangeRate = rate,
        ExchangeDifference = difference, Status = CustomerPaymentStatuses.Received, SourceUuid = source, BankReference = "TT-9"
    };

    [Fact]
    public void A_receipt_debits_the_bank_and_credits_the_receivable_with_applications_but_never_opening_balances()
    {
        var payment = Payment();
        PostingApplication[] apps = [new(Guid.NewGuid(), "SINV-1", 600m, false), new(Guid.NewGuid(), "OB-1", 300m, true)];

        var tx = PaymentPostings.CustomerPayment(payment, apps, "PKR");

        Balanced(tx);
        tx.IdempotencyKey.Should().Be($"finance:Payment:{payment.UUID}");
        Dr(tx, R.Bank).Should().Be(1000m);
        Cr(tx, R.Receivable).Should().Be(1000m, "the unapplied 100 is still off what the customer owes");
        var doc = tx.Metadata!.Document.Should().BeOfType<PaymentPayload>().Subject;
        doc.Applications.Should().ContainSingle().Which.Amount.Should().Be(600m);
        doc.PrivateNote.Should().Contain("OB-1");
        doc.Reference.Should().Be("TT-9");
        doc.SourceType.Should().BeNull();
    }

    [Fact]
    public void A_cash_POS_tender_debits_cash_under_the_pos_module()
    {
        var tx = PaymentPostings.CustomerPayment(Payment("CASH", 50m, Guid.NewGuid()), [], "PKR");

        Dr(tx, R.Cash).Should().Be(50m);
        tx.SourceModule.Should().Be("pos");
        ((PaymentPayload)tx.Metadata!.Document!).SourceType.Should().Be("POS");
    }

    [Fact]
    public void A_realized_gain_on_a_foreign_receipt_is_credited_to_exchange_gain_loss_in_document_currency()
    {
        // 100 USD at 280, against invoices booked at 278: +200 PKR gain.
        var tx = PaymentPostings.CustomerPayment(Payment(amount: 100m, currency: "USD", rate: 280m, difference: 200m), [], "PKR");

        Balanced(tx);
        tx.ExchangeRate.Should().Be(280m);
        Cr(tx, R.ExchangeGainLoss).Should().Be(0.71m);
        Cr(tx, R.Receivable).Should().Be(99.29m);
        ((PaymentPayload)tx.Metadata!.Document!).ExchangeRateCurrencyCode.Should().Be("PKR");
    }

    [Fact]
    public void A_realized_loss_is_a_debit()
    {
        var tx = PaymentPostings.CustomerPayment(Payment(amount: 100m, currency: "USD", rate: 250m, difference: -500m), [], "PKR");

        Balanced(tx);
        Dr(tx, R.ExchangeGainLoss).Should().Be(2m);
        Cr(tx, R.Receivable).Should().Be(102m);
    }

    [Theory]
    [InlineData("STORE_CREDIT", false)]
    [InlineData("EXCHANGE", false)]
    [InlineData("CASH", true)]
    [InlineData("CHEQUE", true)]
    public void Store_credit_and_exchange_move_no_money_and_are_not_posted(string method, bool posted) =>
        PaymentPostings.IsPosted(Payment(method)).Should().Be(posted);

    private static SupplierPayment SupplierPay(string method = "BANK_TRANSFER", string type = "STANDARD", decimal amount = 500m) => new()
    {
        UUID = Guid.NewGuid(), OrganizationId = Org, PaymentNumber = "SPAY-2026-00001", SupplierId = Guid.NewGuid(), SupplierName = "Karachi Steel",
        PaymentDate = new DateTime(2026, 9, 20), PaymentMethod = method, TotalAmount = amount, Status = "POSTED", PaymentType = type,
        CurrencyCode = "PKR", ChequeNo = method == "CHEQUE" ? "000123" : null, PostedAt = DateTime.UtcNow
    };

    [Theory]
    [InlineData("BANK_TRANSFER", "BANK_TRANSFER", R.Bank)]
    [InlineData("ONLINE_WIRE", "ONLINE", R.Bank)]
    [InlineData("CHEQUE", "CHEQUE", R.Bank)]
    [InlineData("CASH", "CASH", R.Cash)]
    public void A_posted_supplier_payment_debits_the_payable_and_credits_the_money_account(string method, string code, string money)
    {
        var bill = Guid.NewGuid();
        var tx = PaymentPostings.SupplierPayment(SupplierPay(method), [new(bill, "INV-1", 500m, false)], "PKR");

        Balanced(tx);
        tx.DocumentType.Should().Be(FinDocType.BillPayment);
        tx.PartnerType.Should().Be(SMS.Shared.Finance.Posting.PartnerType.Vendor);
        Dr(tx, R.Payable).Should().Be(500m);
        Cr(tx, money).Should().Be(500m);
        var doc = (BillPaymentPayload)tx.Metadata!.Document!;
        doc.Method.Should().Be(code);
        doc.Applications.Should().ContainSingle().Which.BillExternalId.Should().Be(bill.ToString());
    }

    [Fact]
    public void An_advance_links_no_bill_and_a_return_settlement_is_not_posted()
    {
        var advance = PaymentPostings.SupplierPayment(SupplierPay(type: "ADVANCE_PAYMENT"), [new(Guid.NewGuid(), "INV-1", 500m, false)], null);

        ((BillPaymentPayload)advance.Metadata!.Document!).Applications.Should().BeEmpty();
        Balanced(advance);
        PaymentPostings.IsPosted(SupplierPay(type: "PURCHASE_RETURN_SETTLEMENT")).Should().BeFalse();
    }

    [Fact]
    public void A_supplier_payment_gain_lowers_the_payable_debit_side_balance()
    {
        var pay = SupplierPay(amount: 100m);
        pay.CurrencyCode = "USD"; pay.ExchangeRate = 280m; pay.ExchangeDifference = 280m; // paid 1 USD less in base than booked

        var tx = PaymentPostings.SupplierPayment(pay, [], "PKR");

        Balanced(tx);
        Dr(tx, R.Payable).Should().Be(101m);
        Cr(tx, R.ExchangeGainLoss).Should().Be(1m);
    }

    // ── Bills and vendor credits ─────────────────────────────────────────────

    [Fact]
    public void A_bill_debits_inventory_for_goods_purchases_for_the_rest_and_input_tax()
    {
        var invoice = new Invoice
        {
            UUID = Guid.NewGuid(), OrganizationId = Org, InvoiceNumber = "INV-2026-00001", SupplierId = Guid.NewGuid(), SupplierName = "S",
            SupplierInvoiceNo = "KSW/1", InvoiceDate = new DateTime(2026, 9, 1), DueDate = new DateTime(2026, 10, 1), Currency = "PKR",
            Subtotal = 3000m, TaxAmount = 450m, TotalAmount = 3450m, TaxCode = "GST15", TaxPercent = 15m, MatchStatus = "Approved"
        };
        var goodsLine = Guid.NewGuid();
        invoice.Lines.Add(new InvoiceLine { UUID = Guid.NewGuid(), LineNo = 1, PoLineUuid = goodsLine, ItemDescription = "Rod", QtyInvoiced = 20m, UnitPrice = 100m, LineTotal = 2000m });
        invoice.Lines.Add(new InvoiceLine { UUID = Guid.NewGuid(), LineNo = 2, ItemDescription = "Handling", QtyInvoiced = 1m, UnitPrice = 1000m, LineTotal = 1000m });
        var payload = BillPayloadFactory.Build(invoice, new Dictionary<Guid, Guid?> { [goodsLine] = Guid.NewGuid() });

        var tx = PurchasePostings.Bill(invoice, payload);

        Balanced(tx);
        tx.IdempotencyKey.Should().Be($"finance:PurchaseBill:{invoice.UUID}");
        Dr(tx, R.Inventory).Should().Be(2000m);
        Dr(tx, R.Purchase).Should().Be(1000m);
        Dr(tx, R.TaxReceivable).Should().Be(450m);
        tx.Lines.Single(l => l.AccountRole == R.TaxReceivable).TaxCode.Should().Be("GST15");
        Cr(tx, R.Payable).Should().Be(3450m);
        tx.Metadata!.Document.Should().BeSameAs(payload);
        PurchasePostings.ReverseBill(invoice.UUID, invoice.InvoiceNumber, "x", 1).IdempotencyKey.Should().Be($"finance:PurchaseBill:{invoice.UUID}:void");
    }

    [Fact]
    public void Credit_and_debit_notes_are_vendor_credits_debiting_the_payable()
    {
        var cn = new CreditNote
        {
            UUID = Guid.NewGuid(), OrganizationId = Org, CreditNoteNumber = "CN-2026-00001", SupplierCreditNoteNo = "SUP-CN-9", SroNumber = "SRO-1",
            SupplierId = Guid.NewGuid(), SupplierName = "S", CreditDate = new DateTime(2026, 9, 5), CreditAmount = 250m
        };
        var dn = new DebitNote
        {
            UUID = Guid.NewGuid(), OrganizationId = Org, DebitNoteNumber = "DN-2026-00001", SroNumber = "SRO-2", SupplierId = Guid.NewGuid(),
            SupplierName = "S", DebitReason = "DAMAGED", DebitAmount = 80m, IssuedAt = new DateTime(2026, 9, 6, 9, 0, 0), Status = "ISSUED"
        };

        var credit = PurchasePostings.VendorCredit(cn, new NoteCurrency("USD", 280m, "PKR"));
        var debit  = PurchasePostings.VendorCredit(dn, new NoteCurrency("PKR", null, null));

        foreach (var tx in new[] { credit, debit }) Balanced(tx);
        Dr(credit, R.Payable).Should().Be(250m);
        Cr(credit, R.Purchase).Should().Be(250m);
        credit.ExchangeRate.Should().Be(280m);
        var cnDoc = (VendorCreditPayload)credit.Metadata!.Document!;
        cnDoc.SourceKind.Should().Be("CREDIT_NOTE");
        cnDoc.DocNumber.Should().Be("SUP-CN-9");
        cnDoc.ExpectedTotal.Should().Be(cnDoc.Lines.Sum(l => l.Amount));
        var dnDoc = (VendorCreditPayload)debit.Metadata!.Document!;
        dnDoc.SourceKind.Should().Be("DEBIT_NOTE");
        dnDoc.TxnDate.Should().Be(new DateTime(2026, 9, 6));
        debit.IdempotencyKey.Should().Be($"finance:VendorCredit:{dn.UUID}");
    }

    // ── Lines ────────────────────────────────────────────────────────────────

    [Fact]
    public void Lines_round_to_the_cent_drop_zeros_flip_negatives_and_put_a_rounding_cent_on_DEFAULT_ROUNDING()
    {
        var lines = new GlLines()
            .Debit(R.Receivable, 10.005m)
            .Credit(R.Revenue, 10m)
            .Credit(R.TaxPayable, 0m)
            .Debit(R.DiscountGiven, -0m)
            .Build();

        lines.Should().HaveCount(3);
        lines[0].Debit.Should().Be(10.01m);
        lines[2].AccountRole.Should().Be(R.Rounding);
        lines[2].Credit.Should().Be(0.01m);

        var flipped = new GlLines().Debit(R.Cash, 5m).Debit(R.ExchangeGainLoss, -5m).Build();
        flipped[1].Credit.Should().Be(5m);

        FluentActions.Invoking(() => new GlLines().Debit(R.Cash, 1m).Build()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Apportion_adds_up_to_the_total_exactly()
    {
        var parts = GlLines.Apportion(100m, [("A", 1m), ("B", 1m), ("C", 1m)]);

        parts.Sum(p => p.Amount).Should().Be(100m);
        parts.Select(p => p.Amount).Should().BeEquivalentTo([33.34m, 33.33m, 33.33m]);
        GlLines.Apportion(7m, [("A", 0m), ("B", 0m)]).Should().Equal(("A", 7m), ("B", 0m));
    }

    [Fact]
    public void Every_posted_type_maps_to_its_QuickBooks_kind()
    {
        PostingSupport.KindOf(FinDocType.SalesInvoice).Should().Be(SyncKind.SalesInvoice);
        PostingSupport.KindOf(FinDocType.SalesReceipt).Should().Be(SyncKind.SalesReceipt);
        PostingSupport.KindOf(FinDocType.CreditNote).Should().Be(SyncKind.CreditMemo);
        PostingSupport.KindOf(FinDocType.RefundReceipt).Should().Be(SyncKind.RefundReceipt);
        PostingSupport.KindOf(FinDocType.Payment).Should().Be(SyncKind.Payment);
        PostingSupport.KindOf(FinDocType.PurchaseBill).Should().Be(SyncKind.Bill);
        PostingSupport.KindOf(FinDocType.BillPayment).Should().Be(SyncKind.BillPayment);
        PostingSupport.KindOf(FinDocType.VendorCredit).Should().Be(SyncKind.VendorCredit);
        PostingSupport.IsActive(new NullFinancialPostingGateway()).Should().BeFalse();
        PostingSupport.IsActive(null).Should().BeFalse();
        PostingSupport.IsActive(new RecordingPostingGateway()).Should().BeTrue();
    }
}
