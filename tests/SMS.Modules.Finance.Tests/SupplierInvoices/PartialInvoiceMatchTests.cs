using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// The three-way match of a partial invoice (hardening item C). An invoice used to be compared with the WHOLE
/// purchase order's total, so any invoice for part of an order — every invoice raised from a partial goods receipt —
/// came out a Variance. It is now matched the way SAP matches it: against what it bills, at the purchase order's
/// prices, and no line may bill more than was received and is not yet invoiced (nor, when it names a GRN, more than
/// that GRN accepted). MatchedPoValue is that expected value; the variance is the net Subtotal less it.
/// </summary>
public class PartialInvoiceMatchTests
{
    private const int User = PurchaseRig.User;

    /// <summary>A PO of 10 at 100, of which <paramref name="received"/> have been received.</summary>
    private static async Task<(PurchaseOrder Po, List<PurchaseOrderLine> Lines)> PoAsync(PurchaseRig rig, decimal received)
    {
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var line = await rig.Demand.PurchaseOrderLines.SingleAsync(l => l.UUID == lines[0].UUID);
        line.QtyReceived = received;
        await rig.Demand.SaveChangesAsync();
        rig.Forget();
        lines[0].QtyReceived = received;
        return (po, lines);
    }

    /// <summary>An invoice of <paramref name="qty"/> of the PO's line at <paramref name="price"/>.</summary>
    private static CreateInvoiceRequest Billing(PurchaseRig rig, PurchaseOrder po, PurchaseOrderLine line, decimal qty, decimal price = 100m, Guid? grn = null)
    {
        var request = rig.Request(po, [line], grn: grn);
        request.Lines![0].QtyInvoiced = qty;
        request.Lines[0].UnitPrice    = price;
        return request;
    }

    [Fact]
    public async Task Half_of_a_received_order_billed_at_the_PO_price_is_Matched_against_its_own_value()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await PoAsync(rig, received: 10m);

        var invoice = await rig.LoadAsync(await rig.CreateAsync(Billing(rig, po, lines[0], 5m)));

        (invoice.MatchStatus, invoice.MatchedPoValue, invoice.VarianceAmount).Should().Be(("Matched", 500m, 0m),
            "5 × the PO's 100 is what this invoice should bill — not the PO's 1000");
    }

    [Fact]
    public async Task The_second_half_matches_too_once_the_first_is_approved()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await PoAsync(rig, received: 10m);
        var first = await rig.CreateAsync(Billing(rig, po, lines[0], 5m));
        await rig.Service.ApproveAsync(first, null, User);
        rig.Forget();

        var second = await rig.LoadAsync(await rig.CreateAsync(Billing(rig, po, lines[0], 5m)));

        second.MatchStatus.Should().Be("Matched");
    }

    [Fact]
    public async Task Billing_more_than_was_received_is_a_variance_even_when_the_amount_equals_the_PO_total()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await PoAsync(rig, received: 5m);

        var invoice = await rig.LoadAsync(await rig.CreateAsync(Billing(rig, po, lines[0], 10m)));

        (invoice.MatchStatus, invoice.MatchedPoValue).Should().Be(("Variance", 1000m),
            "10 billed, 5 received — the 1000 happens to be the PO total, which is why it used to pass");
    }

    [Fact]
    public async Task Billing_what_another_approved_invoice_already_billed_is_a_variance()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await PoAsync(rig, received: 10m);
        var first = await rig.CreateAsync(Billing(rig, po, lines[0], 8m));
        await rig.Service.ApproveAsync(first, null, User);
        rig.Forget();

        var second = await rig.LoadAsync(await rig.CreateAsync(Billing(rig, po, lines[0], 5m)));

        second.MatchStatus.Should().Be("Variance", "only 2 of the 10 received are not yet invoiced");
    }

    [Fact]
    public async Task A_price_above_the_POs_beyond_five_percent_is_a_variance_within_it_a_match()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await PoAsync(rig, received: 10m);

        (await rig.LoadAsync(await rig.CreateAsync(Billing(rig, po, lines[0], 5m, price: 105m)))).MatchStatus.Should().Be("Matched");
        (await rig.LoadAsync(await rig.CreateAsync(Billing(rig, po, lines[0], 5m, price: 106m)))).MatchStatus.Should().Be("Variance");
    }

    [Fact]
    public async Task A_line_no_purchase_order_line_stands_behind_is_billed_on_top_of_what_was_ordered()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await PoAsync(rig, received: 10m);
        var request = Billing(rig, po, lines[0], 5m);
        request.Lines!.Add(new InvoiceLineRequest { ItemDescription = "Handling", QtyInvoiced = 1m, UnitPrice = 100m });

        var invoice = await rig.LoadAsync(await rig.CreateAsync(request));

        (invoice.MatchStatus, invoice.MatchedPoValue, invoice.VarianceAmount).Should().Be(("Variance", 500m, 100m));
    }

    [Fact]
    public async Task A_header_only_invoice_naming_a_partial_GRN_is_matched_against_that_GRNs_value()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await PoAsync(rig, received: 5m);
        var grn = await rig.GrnAsync(po, lines, 5m);

        var invoice = await rig.LoadAsync(await rig.CreateAsync(rig.Request(po, subtotal: 500m, grn: grn.UUID)));

        (invoice.MatchStatus, invoice.MatchedPoValue, invoice.MatchedGrnValue, invoice.VarianceAmount)
            .Should().Be(("Matched", 500m, 500m, 0m));
    }

    [Fact]
    public async Task Lines_billing_more_than_the_GRN_they_name_accepted_are_a_variance()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await PoAsync(rig, received: 10m);
        var grn = await rig.GrnAsync(po, lines, 4m);

        var invoice = await rig.LoadAsync(await rig.CreateAsync(Billing(rig, po, lines[0], 5m, grn: grn.UUID)));

        (invoice.MatchStatus, invoice.MatchedGrnValue).Should().Be(("Variance", 400m));
    }

    [Fact]
    public async Task A_header_only_invoice_against_a_PO_with_no_GRN_is_still_matched_against_the_PO_total()
    {
        var rig = PurchaseRig.New();
        var (po, _) = await PoAsync(rig, received: 10m);

        var invoice = await rig.LoadAsync(await rig.CreateAsync(rig.Request(po, subtotal: 1000m)));

        (invoice.MatchStatus, invoice.MatchedPoValue).Should().Be(("Matched", 1000m));
    }

    [Fact]
    public async Task The_auto_invoice_of_a_partial_goods_receipt_comes_out_Matched()
    {
        var rig = PurchaseRig.New();
        var (po, lines) = await PoAsync(rig, received: 5m);
        var grn = await rig.GrnAsync(po, lines, 5m);

        await new InvoiceAutoCreationService(rig.Warehouse, rig.Finance, rig.Repo, new Mock<IBackgroundJobClient>().Object,
            rig.TaxCodes, rig.BaseCurrency, rig.BaseCurrency).CreateFromGrnAsync(grn.UUID);

        var invoice = await rig.Finance.Invoices.AsNoTracking().SingleAsync(i => i.GrnUuid == grn.UUID);
        (invoice.Subtotal, invoice.MatchStatus, invoice.MatchedPoValue, invoice.MatchedGrnValue, invoice.VarianceAmount)
            .Should().Be((500m, "Matched", 500m, 500m, 0m), "5 of 10 received and billed at the PO's price");
    }

    [Fact]
    public async Task An_invoice_with_no_purchase_order_is_still_Pending_with_nothing_to_match()
    {
        var rig = PurchaseRig.New();

        var invoice = await rig.LoadAsync(await rig.CreateAsync(rig.Request(subtotal: 750m)));

        (invoice.MatchStatus, invoice.MatchedPoValue, invoice.VarianceAmount).Should().Be(("Pending", 0m, 0m));
    }
}
