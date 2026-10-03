using FluentAssertions;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Finance.Tests.QuickBooks;

/// <summary>QuickBooks plan §4 — supplier Invoice + InvoiceLine → BillPayload, field by field.</summary>
public class BillPayloadFactoryTests
{
    private static readonly Guid PoLineWithVariant    = Guid.NewGuid();
    private static readonly Guid PoLineWithoutVariant = Guid.NewGuid();
    private static readonly Guid Variant              = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, Guid?> Resolved = new Dictionary<Guid, Guid?>
    {
        [PoLineWithVariant]    = Variant,
        [PoLineWithoutVariant] = null
    };

    private static Invoice Bill(Action<Invoice>? change = null)
    {
        var invoice = new Invoice
        {
            UUID = Guid.NewGuid(), InvoiceNumber = "INV-2026-00017", SupplierInvoiceNo = "  KSW/2026/881  ",
            SupplierId = Guid.NewGuid(), PoNumber = "PO-2026-00009", GrnNumber = "GRN-2026-00004",
            InvoiceDate = new DateTime(2026, 9, 15), DueDate = new DateTime(2026, 10, 15), Currency = " pkr ",
            Subtotal = 1100m, TaxAmount = 187m, TotalAmount = 1287m, MatchStatus = "Approved"
        };
        invoice.Lines.Add(new InvoiceLine { LineNo = 2, PoLineUuid = null, ItemDescription = "Handling", QtyInvoiced = 1m, UnitPrice = 100m, LineTotal = 100m });
        invoice.Lines.Add(new InvoiceLine { LineNo = 1, PoLineUuid = PoLineWithVariant, ItemDescription = " Steel rod 12mm ", QtyInvoiced = 10m, UnitPrice = 100m, LineTotal = 1000m });
        change?.Invoke(invoice);
        return invoice;
    }

    [Fact]
    public void A_bill_carries_every_mapped_field()
    {
        var invoice = Bill();

        var p = BillPayloadFactory.Build(invoice, Resolved);

        p.ExternalId.Should().Be(invoice.UUID.ToString());
        p.DocNumber.Should().Be("KSW/2026/881", "the supplier's own number, trimmed");
        p.VendorExternalId.Should().Be(invoice.SupplierId.ToString(), "the partner's UUID — the vendor's own ExternalId");
        p.TxnDate.Should().Be(new DateTime(2026, 9, 15));
        p.DueDate.Should().Be(new DateTime(2026, 10, 15));
        p.CurrencyCode.Should().Be("PKR");
        p.ExpectedTaxAmount.Should().Be(187m);
        p.ExpectedTotal.Should().Be(1287m);
        p.PrivateNote.Should().Be("INV-2026-00017 · PO PO-2026-00009 · GRN GRN-2026-00004");

        p.Lines.Select(l => l.LineNo).Should().Equal(1, 2);
        var goods = p.Lines[0];
        goods.Category.Should().Be(BillLineCategory.Goods);
        goods.ItemExternalId.Should().Be(Variant.ToString(), "the PO line's variant — the item's own ExternalId");
        goods.Description.Should().Be("Steel rod 12mm");
        goods.Quantity.Should().Be(10m);
        goods.UnitPrice.Should().Be(100m);
        goods.Amount.Should().Be(1000m);
        goods.TaxPercent.Should().BeNull("an invoice with no purchase tax code gives its lines no rate (plan D-10)");
        goods.TaxCode.Should().BeNull();

        var other = p.Lines[1];
        other.Category.Should().Be(BillLineCategory.Other);
        other.ItemExternalId.Should().BeNull();
        other.Amount.Should().Be(100m);

        (p.Lines.Sum(l => l.Amount) + p.ExpectedTaxAmount).Should().Be(p.ExpectedTotal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_the_suppliers_own_number_the_doc_number_is_ours(string? supplierNo)
    {
        BillPayloadFactory.Build(Bill(i => i.SupplierInvoiceNo = supplierNo), Resolved).DocNumber.Should().Be("INV-2026-00017");
    }

    [Theory]
    [InlineData("PO-1", "GRN-1", "INV-2026-00017 · PO PO-1 · GRN GRN-1")]
    [InlineData("PO-1", null,    "INV-2026-00017 · PO PO-1")]
    [InlineData(null,   "GRN-1", "INV-2026-00017 · GRN GRN-1")]
    [InlineData(null,   null,    "INV-2026-00017")]
    [InlineData(" ",    "",      "INV-2026-00017")]
    public void The_private_note_names_what_is_there(string? po, string? grn, string expected)
    {
        BillPayloadFactory.Build(Bill(i => { i.PoNumber = po; i.GrnNumber = grn; }), Resolved).PrivateNote.Should().Be(expected);
    }

    [Fact]
    public void A_PO_line_that_named_no_variant_or_cannot_be_found_is_not_goods_against_an_item()
    {
        var unknownPoLine = Guid.NewGuid();
        var invoice = Bill(i =>
        {
            i.Lines.Clear();
            i.Lines.Add(new InvoiceLine { LineNo = 1, PoLineUuid = PoLineWithoutVariant, ItemDescription = "Unnamed", QtyInvoiced = 1m, UnitPrice = 5m, LineTotal = 5m });
            i.Lines.Add(new InvoiceLine { LineNo = 2, PoLineUuid = unknownPoLine, ItemDescription = "Vanished", QtyInvoiced = 1m, UnitPrice = 5m, LineTotal = 5m });
        });

        var p = BillPayloadFactory.Build(invoice, Resolved);

        p.Lines.Should().OnlyContain(l => l.Category == BillLineCategory.Other && l.ItemExternalId == null);
    }

    [Fact]
    public void Every_line_of_a_carriers_bill_is_freight_even_one_that_names_a_PO_line()
    {
        var invoice = Bill(i => { i.SourceType = "CARRIER_INVOICE"; i.PoNumber = null; i.GrnNumber = null; });

        var p = BillPayloadFactory.Build(invoice, Resolved);

        p.Lines.Should().HaveCount(2).And.OnlyContain(l => l.Category == BillLineCategory.Freight && l.ItemExternalId == null);
        BillPayloadFactory.PoLinesOf([invoice]).Should().BeEmpty("a carrier bill's lines are never looked up in Demand");
    }

    [Fact]
    public void A_header_only_invoice_goes_as_one_line_for_its_subtotal()
    {
        var withPo = BillPayloadFactory.Build(Bill(i => i.Lines.Clear()), Resolved);
        var withoutPo = BillPayloadFactory.Build(Bill(i => { i.Lines.Clear(); i.PoNumber = null; }), Resolved);
        var carrier = BillPayloadFactory.Build(Bill(i => { i.Lines.Clear(); i.SourceType = "carrier_invoice"; }), Resolved);

        var line = withPo.Lines.Should().ContainSingle().Subject;
        line.Amount.Should().Be(1100m);
        line.Category.Should().Be(BillLineCategory.Other);
        line.Description.Should().Be("Purchase order PO-2026-00009");
        line.Quantity.Should().BeNull();
        line.UnitPrice.Should().BeNull();
        (withPo.Lines.Sum(l => l.Amount) + withPo.ExpectedTaxAmount).Should().Be(withPo.ExpectedTotal);

        withoutPo.Lines.Single().Description.Should().Be("Supplier invoice KSW/2026/881");
        carrier.Lines.Single().Category.Should().Be(BillLineCategory.Freight);
    }

    [Fact]
    public void A_header_only_invoice_of_nothing_has_no_lines()
    {
        BillPayloadFactory.Build(Bill(i => { i.Lines.Clear(); i.Subtotal = 0m; }), Resolved).Lines.Should().BeEmpty();
    }

    [Fact]
    public void The_PO_lines_asked_about_are_every_distinct_one_across_the_invoices()
    {
        var shared = Guid.NewGuid();
        var a = Bill(i => i.Lines.Add(new InvoiceLine { LineNo = 3, PoLineUuid = shared }));
        var b = Bill(i => i.Lines.Add(new InvoiceLine { LineNo = 3, PoLineUuid = shared }));

        BillPayloadFactory.PoLinesOf([a, b]).Should().BeEquivalentTo([PoLineWithVariant, shared]);
    }

    // ── SAP alignment S-11: the header purchase tax code applies to every line ──────────────

    [Fact]
    public void The_invoices_purchase_tax_code_and_rate_go_on_every_line_and_the_totals_still_agree()
    {
        var invoice = Bill(i => { i.TaxCode = " PGST17 "; i.TaxPercent = 17m; });

        var p = BillPayloadFactory.Build(invoice, Resolved);

        p.Lines.Should().HaveCount(2).And.OnlyContain(l => l.TaxCode == "PGST17" && l.TaxPercent == 17m);
        p.Lines.Select(l => l.Category).Should().Equal(BillLineCategory.Goods, BillLineCategory.Other);
        p.ExpectedTaxAmount.Should().Be(187m, "the header tax is still the invoice's own figure");
        (p.Lines.Sum(l => l.Amount) + p.ExpectedTaxAmount).Should().Be(p.ExpectedTotal);
    }

    [Fact]
    public void A_header_only_invoice_with_a_code_puts_it_on_its_one_line()
    {
        var p = BillPayloadFactory.Build(Bill(i => { i.Lines.Clear(); i.TaxCode = "PGST17"; i.TaxPercent = 17m; }), Resolved);

        p.Lines.Should().ContainSingle().Which.Should().Match<BillLinePayload>(l => l.TaxCode == "PGST17" && l.TaxPercent == 17m && l.Amount == 1100m);
        (p.Lines.Sum(l => l.Amount) + p.ExpectedTaxAmount).Should().Be(p.ExpectedTotal);
    }

    [Fact]
    public void A_carriers_bill_with_a_code_carries_it_on_its_freight_lines()
    {
        var p = BillPayloadFactory.Build(Bill(i => { i.SourceType = "CARRIER_INVOICE"; i.TaxCode = "EXEMPT"; i.TaxPercent = 0m; }), Resolved);

        p.Lines.Should().OnlyContain(l => l.Category == BillLineCategory.Freight && l.TaxCode == "EXEMPT" && l.TaxPercent == 0m);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void Without_a_code_or_a_rate_the_lines_carry_neither(string? code, string? expected)
    {
        var p = BillPayloadFactory.Build(Bill(i => { i.TaxCode = code; i.TaxPercent = null; }), Resolved);

        p.Lines.Should().OnlyContain(l => l.TaxCode == expected && l.TaxPercent == null,
            "the gateway then uses the default purchase tax code (D-10), and the payload is what it was before tax codes");
    }

    [Fact]
    public void A_rate_without_a_code_travels_alone()
    {
        var p = BillPayloadFactory.Build(Bill(i => { i.TaxCode = null; i.TaxPercent = 5m; }), Resolved);

        p.Lines.Should().OnlyContain(l => l.TaxCode == null && l.TaxPercent == 5m);
    }

    [Fact]
    public void A_bill_from_before_tax_codes_serializes_without_any_tax_code_field()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(BillPayloadFactory.Build(Bill(), Resolved));

        json.Should().NotContain("TaxCode", "a null code is omitted so stored payloads keep their fingerprints")
            .And.Contain("\"TaxPercent\":null")
            .And.NotContain("ExchangeRate", "a bill without a rate snapshot carries none either");
    }

    // ── SAP alignment S-5 / S-10: the rate fixed at approval travels with the bill ──────────

    [Fact]
    public void A_foreign_currency_bill_carries_the_rate_it_was_approved_at()
    {
        var p = BillPayloadFactory.Build(Bill(i => { i.Currency = "USD"; i.ExchangeRate = 279.25m; i.BaseCurrencyCode = "PKR"; }), Resolved);

        p.ExchangeRate.Should().Be(279.25m);
        p.ExchangeRateCurrencyCode.Should().Be("PKR");
    }

    [Fact]
    public void A_home_currency_bill_carries_no_rate_even_with_its_snapshot_of_one()
    {
        var p = BillPayloadFactory.Build(Bill(i => { i.ExchangeRate = 1m; i.BaseCurrencyCode = "PKR"; }), Resolved);   // currency " pkr "

        p.ExchangeRate.Should().BeNull();
        p.ExchangeRateCurrencyCode.Should().BeNull();
    }

    [Theory]
    [InlineData("Approved", true)]
    [InlineData("approved", true)]
    [InlineData("Matched",  false)]
    [InlineData("Variance", false)]
    [InlineData("Pending",  false)]
    [InlineData("Rejected", false)]
    public void Only_an_approved_invoice_is_a_bill(string matchStatus, bool approved)
    {
        BillPayloadFactory.IsApproved(Bill(i => i.MatchStatus = matchStatus)).Should().Be(approved);
    }
}
