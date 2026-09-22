using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Moq;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using UglyToad.PdfPig;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-08 §9.1 — the invoice PDF. Rendered for real with QuestPDF and read back with PdfPig, so
/// the assertions are about what the customer would actually see on the page, not just that some
/// bytes came out.
/// </summary>
public class SalesInvoiceDocumentServiceTests
{
    private static readonly Guid InvoiceId = Guid.NewGuid();

    private static SalesInvoiceDetailModel Detail(string status = "ISSUED", int lines = 2) => new()
    {
        Uuid = InvoiceId, InvoiceNumber = "SINV-20260920-0001", SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = "SO-2026-00042",
        DeliveryNumber = "DLV-2026-00007", PartnerId = Guid.NewGuid(), PartnerName = "Acme Traders Ltd",
        InvoiceDate = new DateTime(2026, 9, 20), DueDate = new DateTime(2026, 10, 20),
        Subtotal = 4000m, DiscountAmount = 400m, TaxAmount = 180m, GrandTotal = 3780m,
        AmountPaid = 0m, BalanceDue = 3780m, Status = status, CurrencyCode = "PKR", Notes = null,
        Lines = Enumerable.Range(1, lines).Select(i => new SalesInvoiceLineModel
        {
            LineNo = i, Description = i == 1 ? "4mm copper cable, 100m drum" : $"Item number {i}",
            Quantity = i == 1 ? 100m : 3m, UnitPrice = i == 1 ? 40m : 50m,
            DiscountPercent = i == 1 ? 10m : 0m, TaxPercent = i == 1 ? 5m : 0m, LineTotal = i == 1 ? 3780m : 150m
        }).ToList()
    };

    private static PoDocumentTemplateModel Letterhead() => new()
    {
        CompanyName = "Sunrise Electricals", CompanyAddress = "Plot 12, Korangi Industrial Area, Karachi",
        CompanyPhone = "+92 21 111 000", CompanyEmail = "accounts@sunrise.example", CompanyTaxId = "NTN-1234567",
        FooterText = "Thank you for your business"
    };

    private static SalesInvoiceDocumentService Service(
        SalesInvoiceDetailModel? invoice, PoDocumentTemplateModel? template = null, SupplierContactInfo? contact = null, TestClock? clock = null)
    {
        var invoices = new Mock<ISalesInvoiceService>();
        invoices.Setup(i => i.GetAsync(It.IsAny<Guid>())).ReturnsAsync(invoice);

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync(template);

        var contacts = new Mock<ISupplierContactLookupService>();
        contacts.Setup(c => c.GetContactInfoAsync(It.IsAny<Guid>())).ReturnsAsync(contact);

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.WebRootPath).Returns((string?)null);

        return new SalesInvoiceDocumentService(invoices.Object, templates.Object, contacts.Object, env.Object, clock ?? new TestClock());
    }

    private static (int Pages, string Text) Read(SalesInvoicePdf pdf)
    {
        using var document = PdfDocument.Open(pdf.Content);
        var pages = document.GetPages().ToList();
        return (pages.Count, string.Join(" ", pages.SelectMany(p => p.GetWords()).Select(w => w.Text)));
    }

    [Fact]
    public async Task It_is_a_real_pdf_named_after_the_invoice()
    {
        var pdf = await Service(Detail(), Letterhead()).GeneratePdfAsync(InvoiceId);

        pdf.FileName.Should().Be("SINV-20260920-0001.pdf");
        System.Text.Encoding.ASCII.GetString(pdf.Content, 0, 5).Should().Be("%PDF-");
        Read(pdf).Pages.Should().Be(1);
    }

    [Fact]
    public async Task The_page_carries_the_letterhead_the_customer_and_the_invoice_details()
    {
        var pdf = await Service(Detail(), Letterhead(),
            new SupplierContactInfo("+92 300 1112222", "buyer@acme.example", "5 Mall Road, Lahore")).GeneratePdfAsync(InvoiceId);

        var (_, text) = Read(pdf);

        text.Should().Contain("Sunrise Electricals").And.Contain("Korangi").And.Contain("NTN-1234567");
        text.Should().Contain("Billed To").And.Contain("Acme Traders Ltd").And.Contain("5 Mall Road, Lahore")
            .And.Contain("+92 300 1112222").And.Contain("buyer@acme.example");
        text.Should().Contain("SINV-20260920-0001");
        text.Should().Contain("20 Sep 2026", "the invoice date").And.Contain("20 Oct 2026", "the due date");
        text.Should().Contain("SO-2026-00042").And.Contain("DLV-2026-00007");
        text.Should().Contain("Thank you for your business");
    }

    [Fact]
    public async Task Every_line_and_every_total_is_on_the_page_as_the_invoice_states_it()
    {
        var pdf = await Service(Detail(), Letterhead()).GeneratePdfAsync(InvoiceId);

        var (_, text) = Read(pdf);

        text.Should().Contain("4mm copper cable, 100m drum").And.Contain("Item number 2");
        text.Should().Contain("100.00").And.Contain("40.00").And.Contain("3,780.00").And.Contain("150.00");
        text.Should().Contain("Subtotal").And.Contain("4,000.00 PKR");
        text.Should().Contain("Discount").And.Contain("-400.00 PKR");
        text.Should().Contain("Tax").And.Contain("180.00 PKR");
        text.Should().Contain("Total").And.Contain("3,780.00 PKR");
        text.Should().Contain("Balance").And.Contain("Due");
    }

    [Fact]
    public async Task Each_figure_is_printed_against_its_own_label_and_not_another_that_happens_to_match()
    {
        // Every total is different here, so a figure printed against the wrong label cannot hide behind
        // an equal one (an unpaid invoice's total and balance are the same number).
        var invoice = Detail("PARTIALLY_PAID");
        invoice.Subtotal = 5000m; invoice.DiscountAmount = 300m; invoice.TaxAmount = 150m;
        invoice.GrandTotal = 4850m; invoice.AmountPaid = 1000m; invoice.BalanceDue = 3850m;

        var (_, text) = Read(await Service(invoice, Letterhead()).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("Subtotal 5,000.00 PKR");
        text.Should().Contain("Discount -300.00 PKR");
        text.Should().Contain("Tax 150.00 PKR");
        text.Should().Contain("Total 4,850.00 PKR");
        text.Should().Contain("Paid -1,000.00 PKR");
        text.Should().Contain("Balance Due 3,850.00 PKR");
    }

    // ── Payment terms and bank details (A29-P7-09) ───────────────────────────

    [Theory]
    [InlineData(30, "Net 30 days")]
    [InlineData(45, "Net 45 days")]
    [InlineData(1, "Net 1 day")]
    [InlineData(0, "Due on receipt")]
    [InlineData(-3, "Due on receipt")]
    public void Payment_terms_are_read_off_the_invoices_own_dates(int days, string expected)
    {
        var invoice = Detail();
        invoice.InvoiceDate = new DateTime(2026, 9, 20);
        invoice.DueDate = invoice.InvoiceDate.AddDays(days);

        SalesInvoiceDocumentService.PaymentTerms(invoice).Should().Be(expected);
    }

    [Fact]
    public void A_time_of_day_on_either_date_does_not_change_the_terms()
    {
        var invoice = Detail();
        invoice.InvoiceDate = new DateTime(2026, 9, 20, 23, 30, 0);
        invoice.DueDate = new DateTime(2026, 10, 20, 0, 5, 0);

        SalesInvoiceDocumentService.PaymentTerms(invoice).Should().Be("Net 30 days");
    }

    [Fact]
    public async Task The_terms_are_printed_with_the_invoice_and_follow_an_edited_due_date()
    {
        var invoice = Detail();
        var (_, text) = Read(await Service(invoice, Letterhead()).GeneratePdfAsync(InvoiceId));
        text.Should().Contain("Payment Terms").And.Contain("Net 30 days");

        invoice.DueDate = invoice.InvoiceDate.AddDays(45);
        var (_, edited) = Read(await Service(invoice, Letterhead()).GeneratePdfAsync(InvoiceId));
        edited.Should().Contain("Net 45 days").And.NotContain("Net 30 days");
    }

    [Fact]
    public async Task The_companys_bank_details_are_printed_line_by_line_with_where_and_by_when_to_pay()
    {
        var template = Letterhead();
        template.BankDetails = "Habib Bank Ltd, Korangi Branch\nAccount title: Sunrise Electricals\r\nIBAN: PK36HABB0000123456702568";

        var (_, text) = Read(await Service(Detail(), template).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("PAYMENT DETAILS");
        text.Should().Contain("Please pay 3,780.00 PKR by 20 Oct 2026, quoting SINV-20260920-0001.");
        text.Should().Contain("Bank details")
            .And.Contain("Habib Bank Ltd, Korangi Branch")
            .And.Contain("Account title: Sunrise Electricals")
            .And.Contain("IBAN: PK36HABB0000123456702568");
    }

    [Fact]
    public async Task With_no_bank_details_configured_the_box_still_says_how_much_by_when_and_under_what_reference()
    {
        var (_, text) = Read(await Service(Detail(), Letterhead()).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("PAYMENT DETAILS").And.Contain("Please pay 3,780.00 PKR by 20 Oct 2026");
        text.Should().NotContain("Bank details", "no account is named that nobody has entered");
    }

    [Fact]
    public async Task With_no_template_at_all_there_is_no_bank_block_and_the_invoice_still_prints()
    {
        var (_, text) = Read(await Service(Detail(), template: null).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("Please pay").And.NotContain("Bank details");
    }

    [Fact]
    public async Task The_amount_to_pay_is_what_is_still_owing_not_the_invoice_total()
    {
        var invoice = Detail("PARTIALLY_PAID");
        invoice.AmountPaid = 1000m; invoice.BalanceDue = 2780m;
        var template = Letterhead();
        template.BankDetails = "HBL 0123";

        var (_, text) = Read(await Service(invoice, template).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("Please pay 2,780.00 PKR").And.NotContain("Please pay 3,780.00 PKR");
    }

    [Theory]
    [InlineData("PAID", 0.0)]
    [InlineData("CANCELLED", 3780.0)]
    public async Task An_invoice_with_nothing_to_pay_or_that_is_void_does_not_ask_for_money(string status, double balance)
    {
        var invoice = Detail(status);
        invoice.BalanceDue = (decimal)balance;
        var template = Letterhead();
        template.BankDetails = "HBL 0123";

        var (_, text) = Read(await Service(invoice, template).GeneratePdfAsync(InvoiceId));

        text.Should().NotContain("PAYMENT DETAILS").And.NotContain("Please pay").And.NotContain("HBL 0123");
    }

    [Fact]
    public async Task A_draft_preview_shows_where_it_will_ask_to_be_paid()
    {
        var template = Letterhead();
        template.BankDetails = "HBL 0123";

        var (_, text) = Read(await Service(Detail("DRAFT"), template).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("DRAFT INVOICE").And.Contain("PAYMENT DETAILS").And.Contain("HBL 0123");
    }

    [Fact]
    public async Task Blank_lines_and_stray_spaces_in_the_bank_details_are_not_printed_as_gaps()
    {
        var template = Letterhead();
        template.BankDetails = "  HBL  \n\n   \n IBAN PK00  \n";

        var (_, text) = Read(await Service(Detail(), template).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("HBL").And.Contain("IBAN PK00");
    }

    [Fact]
    public async Task A_full_length_bank_block_of_a_thousand_characters_still_prints()
    {
        var template = Letterhead();
        template.BankDetails = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"Line {i:D2} of the bank block"));

        var (_, text) = Read(await Service(Detail(), template).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("Line 01 of the bank block").And.Contain("Line 40 of the bank block");
    }

    [Fact]
    public async Task A_discount_row_appears_only_when_there_is_a_discount()
    {
        var withDiscount = Detail();
        var without = Detail();
        without.DiscountAmount = 0m; without.Subtotal = 3780m;

        Read(await Service(withDiscount, Letterhead()).GeneratePdfAsync(InvoiceId)).Text.Should().Contain("-400.00 PKR");
        Read(await Service(without, Letterhead()).GeneratePdfAsync(InvoiceId)).Text.Should().NotContain("Discount");
    }

    [Fact]
    public async Task A_draft_says_it_is_a_draft_and_not_a_request_for_payment()
    {
        var (_, text) = Read(await Service(Detail("DRAFT"), Letterhead()).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("DRAFT INVOICE").And.Contain("not yet issued").And.Contain("not a request for payment");
    }

    [Theory]
    [InlineData("ISSUED")]
    [InlineData("PARTIALLY_PAID")]
    [InlineData("PAID")]
    [InlineData("OVERDUE")]
    public async Task An_invoice_that_has_been_issued_carries_no_draft_marking_and_shows_its_status(string status)
    {
        var (_, text) = Read(await Service(Detail(status), Letterhead()).GeneratePdfAsync(InvoiceId));

        text.Should().NotContain("DRAFT").And.NotContain("not yet issued");
        text.Should().Contain("Status:").And.Contain(status.Replace('_', ' '));
    }

    [Fact]
    public async Task A_cancelled_invoice_is_marked_void()
    {
        var (_, text) = Read(await Service(Detail("CANCELLED"), Letterhead()).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("CANCELLED").And.Contain("void");
    }

    [Fact]
    public async Task Payments_received_are_listed_with_what_was_applied_and_what_is_still_owed()
    {
        var invoice = Detail("PARTIALLY_PAID");
        invoice.AmountPaid = 1000m;
        invoice.BalanceDue = 2780m;
        invoice.Payments =
        [
            new SalesInvoicePaymentModel { PaymentNumber = "CPAY-20260925-0001", PaymentDate = new DateTime(2026, 9, 25), PaymentMethod = "BANK_TRANSFER", PaymentStatus = "RECEIVED", AllocatedAmount = 1000m }
        ];

        var (_, text) = Read(await Service(invoice, Letterhead()).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("PAYMENTS RECEIVED").And.Contain("CPAY-20260925-0001").And.Contain("25 Sep 2026")
            .And.Contain("BANK TRANSFER").And.Contain("1,000.00 PKR").And.Contain("RECEIVED");
        text.Should().Contain("Paid").And.Contain("-1,000.00 PKR");
        text.Should().Contain("2,780.00 PKR", "the balance due");
    }

    [Fact]
    public async Task An_invoice_nobody_has_paid_has_no_payments_section_or_paid_row()
    {
        var (_, text) = Read(await Service(Detail(), Letterhead()).GeneratePdfAsync(InvoiceId));

        text.Should().NotContain("PAYMENTS RECEIVED").And.NotContain("Paid");
    }

    [Fact]
    public async Task Notes_are_printed_when_there_are_some()
    {
        var invoice = Detail();
        invoice.Notes = "Net 45 as agreed with the buyer";

        var (_, text) = Read(await Service(invoice, Letterhead()).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("NOTES").And.Contain("Net 45 as agreed with the buyer");
        Read(await Service(Detail(), Letterhead()).GeneratePdfAsync(InvoiceId)).Text.Should().NotContain("NOTES");
    }

    [Fact]
    public async Task A_long_invoice_runs_onto_more_pages_without_losing_a_line()
    {
        var invoice = Detail(lines: 120);

        var (pages, text) = Read(await Service(invoice, Letterhead()).GeneratePdfAsync(InvoiceId));

        pages.Should().BeGreaterThan(1);
        foreach (var n in new[] { 2, 60, 120 })
            text.Should().Contain($"Item number {n}", $"line {n}");
        text.Should().Contain("Balance", "the totals come after the last line");
    }

    [Fact]
    public async Task A_very_long_description_wraps_rather_than_breaking_the_layout()
    {
        var invoice = Detail();
        invoice.Lines[0].Description = string.Join(" ", Enumerable.Repeat("armoured", 40)) + " cable";

        var (_, text) = Read(await Service(invoice, Letterhead()).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("cable").And.Contain("3,780.00");
    }

    [Fact]
    public async Task With_no_letterhead_configured_and_no_contact_details_it_still_prints()
    {
        var pdf = await Service(Detail(), template: null, contact: null).GeneratePdfAsync(InvoiceId);

        var (_, text) = Read(pdf);

        text.Should().Contain("Company Name", "the placeholder rather than a failure").And.Contain("Acme Traders Ltd").And.Contain("SINV-20260920-0001");
    }

    [Fact]
    public async Task A_logo_path_that_does_not_exist_is_ignored()
    {
        var template = Letterhead();
        template.CompanyLogoUrl = "/uploads/logos/missing.png";

        var act = async () => await Service(Detail(), template).GeneratePdfAsync(InvoiceId);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Accented_names_print()
    {
        var invoice = Detail();
        invoice.PartnerName = "Müller & Söhne GmbH";

        var (_, text) = Read(await Service(invoice, Letterhead()).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("Müller").And.Contain("Söhne");
    }

    [Fact]
    public async Task A_customer_named_in_urdu_script_does_not_stop_the_invoice_printing()
    {
        // These customers are in Pakistan. A font without the glyphs must not turn into a 500.
        var invoice = Detail();
        invoice.PartnerName = "احمد ٹریڈرز";

        var act = async () => await Service(invoice, Letterhead()).GeneratePdfAsync(InvoiceId);

        await act.Should().NotThrowAsync();
        var (_, text) = Read(await act());
        text.Should().Contain("SINV-20260920-0001");

        // The name is on the page, shaped and drawn with a fallback font — not dropped. It is read
        // back in its visual presentation forms rather than the letters that were typed, so the
        // check is for Arabic-script characters, not for the original string.
        System.Text.RegularExpressions.Regex.Matches(text, @"[؀-ۿﭐ-﷿ﹰ-﻿]")
            .Count.Should().BeGreaterThanOrEqualTo(6, "the customer's name is printed");
    }

    [Fact]
    public async Task The_print_time_is_stamped_from_the_clock_in_utc()
    {
        var clock = new TestClock { Value = new DateTime(2026, 9, 21, 14, 5, 0, DateTimeKind.Utc) };

        var (_, text) = Read(await Service(Detail(), Letterhead(), clock: clock).GeneratePdfAsync(InvoiceId));

        text.Should().Contain("Generated").And.Contain("2026-09-21").And.Contain("14:05").And.Contain("UTC");
    }

    [Fact]
    public async Task An_unknown_invoice_is_not_found()
    {
        var act = async () => await Service(invoice: null).GeneratePdfAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
