using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using UglyToad.PdfPig;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>S-3 — a supplier invoice's purchase tax code: checked, snapshotted, and the tax worked out from it.</summary>
public class PurchaseTaxCodeTests
{
    // ── Create ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_purchase_code_is_snapshotted_and_the_tax_worked_out_from_the_subtotal_ignoring_the_amount_sent()
    {
        var rig = PurchaseRig.New();
        var gst = rig.TaxCodes.Add("GST17", 17m);
        var (po, lines) = await rig.PoAsync(true, (10m, 100m), (3m, 33.33m));

        var uuid = await rig.CreateAsync(rig.Request(po, lines, tax: 5m, taxCode: gst.Uuid));

        var invoice = await rig.LoadAsync(uuid);
        invoice.Subtotal.Should().Be(1099.99m);
        invoice.TaxAmount.Should().Be(187.00m, "1099.99 × 17% = 186.9983, rounded to 2dp — the 5.00 sent is ignored");
        invoice.TotalAmount.Should().Be(1286.99m);
        invoice.TaxCodeUuid.Should().Be(gst.Uuid);
        invoice.TaxCode.Should().Be("GST17");
        invoice.TaxPercent.Should().Be(17m);

        var detail = await rig.Service.GetByUuidAsync(uuid);
        (detail!.TaxCodeUuid, detail.TaxCode, detail.TaxPercent, detail.TaxAmount).Should().Be((gst.Uuid, "GST17", 17m, 187.00m));

        var row = (await rig.Service.GetListAsync(new InvoiceFilter())).Data.Should().ContainSingle().Subject;
        (row.TaxCode, row.TaxPercent).Should().Be(("GST17", 17m));
    }

    [Fact]
    public async Task The_tax_rounds_half_away_from_zero_like_every_Finance_amount()
    {
        var rig = PurchaseRig.New();
        var code = rig.TaxCodes.Add("GST5", 5m);

        // 150.50 × 5% = 7.525 exactly: away from zero is 7.53 (banker's rounding would give 7.52).
        var uuid = await rig.CreateAsync(rig.Request(subtotal: 150.50m, taxCode: code.Uuid));

        (await rig.LoadAsync(uuid)).TaxAmount.Should().Be(7.53m);
    }

    [Fact]
    public async Task A_code_usable_on_both_sides_is_accepted_on_a_supplier_invoice()
    {
        var rig = PurchaseRig.New();
        var both = rig.TaxCodes.Add("STD18", 18m, TaxCodeUsage.Both);

        var uuid = await rig.CreateAsync(rig.Request(subtotal: 1000m, taxCode: both.Uuid));

        var invoice = await rig.LoadAsync(uuid);
        (invoice.TaxCode, invoice.TaxAmount, invoice.TotalAmount).Should().Be(("STD18", 180m, 1180m));
    }

    [Theory]
    [InlineData("unknown",  "*does not exist*")]
    [InlineData("inactive", "*GST17 is inactive*")]
    [InlineData("sales",    "*OUT17 is for sales only*")]
    public async Task An_unknown_inactive_or_sales_only_code_is_a_bad_request_and_nothing_is_saved(string which, string message)
    {
        var rig = PurchaseRig.New();
        var codeUuid = which switch
        {
            "inactive" => rig.TaxCodes.Add("GST17", 17m, active: false).Uuid,
            "sales"    => rig.TaxCodes.Add("OUT17", 17m, TaxCodeUsage.Sales).Uuid,
            _          => Guid.NewGuid()
        };

        await rig.Service.Invoking(s => s.CreateAsync(rig.Request(subtotal: 1000m, taxCode: codeUuid), PurchaseRig.User))
            .Should().ThrowAsync<BadRequestException>().WithMessage(message);

        (await rig.Finance.Invoices.AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Without_a_code_the_tax_is_the_amount_entered_as_before()
    {
        var rig = PurchaseRig.New();
        rig.TaxCodes.Add("GST17", 17m, isDefault: true);
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));

        var uuid = await rig.CreateAsync(rig.Request(po, lines, tax: 123.45m));

        var invoice = await rig.LoadAsync(uuid);
        (invoice.TaxAmount, invoice.TotalAmount).Should().Be((123.45m, 1123.45m));
        invoice.TaxCodeUuid.Should().BeNull("a manual invoice is not given the default code behind the user's back");
        invoice.TaxCode.Should().BeNull();
        invoice.TaxPercent.Should().BeNull();
    }

    [Fact]
    public async Task A_harness_without_the_tax_code_lookup_still_creates_manual_invoices_but_refuses_a_code()
    {
        var rig = PurchaseRig.New(withLookups: false);

        var uuid = await rig.CreateAsync(rig.Request(subtotal: 1000m, tax: 170m));
        (await rig.LoadAsync(uuid)).TotalAmount.Should().Be(1170m);

        await rig.Service.Invoking(s => s.CreateAsync(rig.Request(subtotal: 1000m, taxCode: Guid.NewGuid()), PurchaseRig.User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not available*");
    }

    [Fact]
    public async Task A_later_change_to_the_codes_rate_does_not_change_the_invoice_nor_does_picking_the_same_code_again()
    {
        var rig = PurchaseRig.New();
        var gst = rig.TaxCodes.Add("GST17", 17m);
        var uuid = await rig.CreateAsync(rig.Request(subtotal: 1000m, taxCode: gst.Uuid));

        rig.TaxCodes.Codes[0] = gst with { RatePercent = 18m };
        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { TaxCodeUuid = gst.Uuid }, PurchaseRig.User);

        var invoice = await rig.LoadAsync(uuid);
        (invoice.TaxPercent, invoice.TaxAmount, invoice.TotalAmount).Should().Be((17m, 170m, 1170m));
    }

    // ── Patch (before approval) ──────────────────────────────────────────────

    [Fact]
    public async Task Patching_a_code_onto_an_unapproved_invoice_works_the_tax_out_and_leaves_the_match_alone()
    {
        var rig = PurchaseRig.New();
        var gst = rig.TaxCodes.Add("GST17", 17m);
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var uuid = await rig.CreateAsync(rig.Request(po, lines));

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { TaxCodeUuid = gst.Uuid, TaxAmount = 1m }, PurchaseRig.User);

        var invoice = await rig.LoadAsync(uuid);
        (invoice.TaxCode, invoice.TaxPercent, invoice.TaxAmount, invoice.TotalAmount).Should().Be(("GST17", 17m, 170m, 1170m));
        invoice.MatchStatus.Should().Be("Matched", "the match is on the Subtotal (S-8), which tax does not change");
        invoice.VarianceAmount.Should().Be(0m);
    }

    [Fact]
    public async Task A_tax_change_moves_the_total_by_the_difference_so_a_deducted_credit_note_stays_deducted()
    {
        var rig = PurchaseRig.New();
        var gst = rig.TaxCodes.Add("GST17", 17m);
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var uuid = await rig.CreateAsync(rig.Request(po, lines));
        await rig.CreditNoteAppliedAsync(uuid, 100m);

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { TaxCodeUuid = gst.Uuid }, PurchaseRig.User);

        (await rig.LoadAsync(uuid)).TotalAmount.Should().Be(1070m, "1000 − 100 deducted + 170 tax; Subtotal + Tax would have erased the credit note");
    }

    [Fact]
    public async Task Removing_the_code_lets_the_tax_be_entered_by_hand_again()
    {
        var rig = PurchaseRig.New();
        var gst = rig.TaxCodes.Add("GST17", 17m);
        var keep  = await rig.CreateAsync(rig.Request(subtotal: 1000m, taxCode: gst.Uuid));
        var enter = await rig.CreateAsync(rig.Request(subtotal: 1000m, taxCode: gst.Uuid));

        await rig.Service.PatchAsync(keep,  new PatchInvoiceRequest { TaxCodeUuid = Guid.Empty }, PurchaseRig.User);
        await rig.Service.PatchAsync(enter, new PatchInvoiceRequest { TaxCodeUuid = Guid.Empty, TaxAmount = 50m }, PurchaseRig.User);

        var kept = await rig.LoadAsync(keep);
        (kept.TaxCodeUuid, kept.TaxCode, kept.TaxPercent, kept.TaxAmount, kept.TotalAmount).Should().Be(((Guid?)null, (string?)null, (decimal?)null, 170m, 1170m));
        var entered = await rig.LoadAsync(enter);
        (entered.TaxCode, entered.TaxAmount, entered.TotalAmount).Should().Be(((string?)null, 50m, 1050m));
    }

    [Fact]
    public async Task A_bare_tax_amount_on_an_invoice_taxed_by_a_code_is_refused()
    {
        var rig = PurchaseRig.New();
        var gst = rig.TaxCodes.Add("GST17", 17m);
        var uuid = await rig.CreateAsync(rig.Request(subtotal: 1000m, taxCode: gst.Uuid));

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { TaxAmount = 99m }, PurchaseRig.User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*comes from tax code GST17 (17%)*");

        (await rig.LoadAsync(uuid)).TaxAmount.Should().Be(170m);
    }

    [Fact]
    public async Task Patching_an_unusable_code_is_refused_and_changes_nothing()
    {
        var rig = PurchaseRig.New();
        var sales = rig.TaxCodes.Add("OUT17", 17m, TaxCodeUsage.Sales);
        var uuid = await rig.CreateAsync(rig.Request(subtotal: 1000m, tax: 10m));

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { TaxCodeUuid = sales.Uuid, Notes = "x" }, PurchaseRig.User))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*sales only*");

        var invoice = await rig.LoadAsync(uuid);
        (invoice.TaxCode, invoice.TaxAmount, invoice.Notes).Should().Be(((string?)null, 10m, (string?)null));
    }

    // ── GRN auto-invoices ────────────────────────────────────────────────────

    private static InvoiceAutoCreationService AutoCreation(PurchaseRig rig, bool withLookups = true) =>
        withLookups
            ? new InvoiceAutoCreationService(rig.Warehouse, rig.Finance, rig.Repo, new Mock<IBackgroundJobClient>().Object,
                rig.TaxCodes, rig.BaseCurrency, rig.BaseCurrency)
            : new InvoiceAutoCreationService(rig.Warehouse, rig.Finance, rig.Repo, new Mock<IBackgroundJobClient>().Object);

    [Fact]
    public async Task A_GRN_auto_invoice_takes_the_default_purchase_code_and_still_matches()
    {
        var rig = PurchaseRig.New();
        rig.TaxCodes.Add("OUT17", 17m, TaxCodeUsage.Sales, isDefault: true);
        var gst = rig.TaxCodes.Add("GST17", 17m, isDefault: true);
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var grn = await rig.GrnAsync(po, lines, 10m);

        await AutoCreation(rig).CreateFromGrnAsync(grn.UUID);

        var invoice = await rig.Finance.Invoices.AsNoTracking().SingleAsync(i => i.GrnUuid == grn.UUID);
        (invoice.TaxCodeUuid, invoice.TaxCode, invoice.TaxPercent).Should().Be((gst.Uuid, "GST17", 17m));
        (invoice.Subtotal, invoice.TaxAmount, invoice.TotalAmount).Should().Be((1000m, 170m, 1170m));
        invoice.MatchStatus.Should().Be("Matched", "17% tax on a matching GRN is not a variance (S-8)");
        invoice.Currency.Should().Be("PKR");
    }

    [Fact]
    public async Task Without_a_default_purchase_code_the_auto_invoice_has_no_tax_as_before()
    {
        var rig = PurchaseRig.New();
        rig.TaxCodes.Add("GST17", 17m);
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var grn = await rig.GrnAsync(po, lines, 10m);

        await AutoCreation(rig).CreateFromGrnAsync(grn.UUID);

        var invoice = await rig.Finance.Invoices.AsNoTracking().SingleAsync(i => i.GrnUuid == grn.UUID);
        (invoice.TaxCode, invoice.TaxAmount, invoice.TotalAmount).Should().Be(((string?)null, 0m, 1000m));
    }

    [Fact]
    public async Task A_misconfigured_default_is_not_applied_rather_than_failing_the_job()
    {
        var rig = PurchaseRig.New();
        rig.TaxCodes.DefaultOverride = rig.TaxCodes.Add("OUT17", 17m, TaxCodeUsage.Sales, isDefault: true);
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var grn = await rig.GrnAsync(po, lines, 10m);

        await AutoCreation(rig).CreateFromGrnAsync(grn.UUID);

        (await rig.Finance.Invoices.AsNoTracking().SingleAsync(i => i.GrnUuid == grn.UUID)).TaxCode.Should().BeNull();
    }

    [Theory]
    [InlineData("USD", "USD")]
    [InlineData("eur", "EUR")]
    [InlineData(null,  "PKR")]
    public async Task The_auto_invoice_is_in_the_organizations_base_currency_and_PKR_only_when_there_is_none(string? baseCurrency, string expected)
    {
        var rig = PurchaseRig.New(baseCurrency);
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var grn = await rig.GrnAsync(po, lines, 10m);

        await AutoCreation(rig).CreateFromGrnAsync(grn.UUID);

        (await rig.Finance.Invoices.AsNoTracking().SingleAsync(i => i.GrnUuid == grn.UUID)).Currency.Should().Be(expected);
    }

    [Fact]
    public async Task A_harness_without_the_lookups_auto_invoices_as_before()
    {
        var rig = PurchaseRig.New("USD");
        rig.TaxCodes.Add("GST17", 17m, isDefault: true);
        var (po, lines) = await rig.PoAsync(true, (10m, 100m));
        var grn = await rig.GrnAsync(po, lines, 10m);

        await AutoCreation(rig, withLookups: false).CreateFromGrnAsync(grn.UUID);

        var invoice = await rig.Finance.Invoices.AsNoTracking().SingleAsync(i => i.GrnUuid == grn.UUID);
        (invoice.Currency, invoice.TaxCode, invoice.TaxAmount).Should().Be(("PKR", (string?)null, 0m));
    }

    // ── The PDF ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null,    null,  "Tax Amount")]
    [InlineData("GST17", 17.0,  "Tax (GST17 · 17%)")]
    [InlineData("GST12", 12.5,  "Tax (GST12 · {0}%)")]
    public void The_PDF_labels_the_tax_with_its_code_and_rate(string? code, double? rate, string label)
    {
        var detail = new InvoiceDetailModel { TaxCode = code, TaxPercent = rate is null ? null : (decimal)rate };

        // {0}: 12.5 as this machine's culture writes it, as the PDF does.
        InvoiceDocumentService.TaxLabel(detail).Should().Be(string.Format(label, $"{12.5m:0.##}"));
    }

    [Fact]
    public async Task The_rendered_PDF_shows_the_code_the_rate_and_the_base_currency_total()
    {
        var uuid = Guid.NewGuid();
        var detail = new InvoiceDetailModel
        {
            UUID = uuid, InvoiceNumber = "INV-2026-00042", SupplierName = "Karachi Steel", SupplierId = Guid.NewGuid(),
            PoNumber = null, InvoiceDate = new DateTime(2026, 9, 15), DueDate = new DateTime(2026, 10, 15),
            ReceivedDate = new DateTime(2026, 9, 16), Currency = "USD", Subtotal = 1000m, TaxAmount = 170m, TotalAmount = 1170m,
            TaxCode = "GST17", TaxPercent = 17m, ExchangeRate = 278.5m, BaseCurrencyCode = "PKR", BaseTotalAmount = 325845m,
            MatchStatus = "Approved", PaymentStatus = "Unpaid"
        };
        var invoices  = new Mock<IInvoiceService>();
        invoices.Setup(i => i.GetByUuidAsync(uuid)).ReturnsAsync(detail);
        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);
        var contacts  = new Mock<ISupplierContactLookupService>();
        contacts.Setup(c => c.GetContactInfoAsync(It.IsAny<Guid>())).ReturnsAsync((SupplierContactInfo?)null);
        var env = new Mock<IWebHostEnvironment>();

        var bytes = await new InvoiceDocumentService(invoices.Object, templates.Object, contacts.Object, env.Object).GeneratePdfAsync(uuid);

        // Rendered at all: a PO-less invoice (PoNumber null) prints a dash for its PO reference.
        using var pdf = PdfDocument.Open(bytes);
        var text = string.Join(" ", pdf.GetPages().SelectMany(p => p.GetWords()).Select(w => w.Text));
        text.Should().Contain("(GST17").And.Contain("17%)").And.Contain($"{170m:N2} USD");
        text.Should().Contain($"In PKR @ {278.5m:0.######}").And.Contain($"{325845m:N2} PKR");
    }
}
