using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Demand.Services.Documents;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A50 TPL (D-25) — the customer payment receipt PDF (GET api/customer-payments/{uuid}/pdf): who paid, how much, how, and
/// which invoices it was applied to. Same letterhead and palette as the invoice; the org's PaymentReceipt template brands it
/// (or its BodyHtml replaces the layout). Rendered through <see cref="PdfRenderGate"/>.
/// </summary>
public sealed class CustomerPaymentReceiptService
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private readonly ICustomerPaymentService _payments;
    private readonly IPoDocumentTemplateService _templates;
    private readonly IDocumentBrandingProvider _branding;
    private readonly ITemplatedDocumentRenderer _templated;
    private readonly TimeProvider _clock;

    public CustomerPaymentReceiptService(
        ICustomerPaymentService payments, IPoDocumentTemplateService templates,
        IDocumentBrandingProvider? branding = null, ITemplatedDocumentRenderer? templated = null, TimeProvider? clock = null)
    {
        _payments = payments;
        _templates = templates;
        _branding = branding ?? new NullDocumentBrandingProvider();
        _templated = templated ?? new NullTemplatedDocumentRenderer();
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<(byte[] Content, string FileName)> GeneratePdfAsync(Guid paymentUuid)
    {
        var payment = await _payments.GetAsync(paymentUuid) ?? throw new NotFoundException("CustomerPayment", paymentUuid);
        var template = await _templates.GetActiveAsync();
        var brand = await _branding.GetAsync(DocumentTemplateTypes.PaymentReceipt);
        if (brand is not null)
            brand = BrandedDocumentKit.WithLetterhead(brand, template?.CompanyName, template?.CompanyAddress, template?.CompanyPhone,
                template?.CompanyEmail, template?.CompanyTaxId);
        var printed = _clock.GetUtcNow().UtcDateTime;
        var fileName = $"Receipt-{payment.PaymentNumber}.pdf";

        if (brand is not null && !string.IsNullOrWhiteSpace(brand.BodyHtml) && _templated.Render(brand, Templated(payment, printed)) is { } body)
            return (body, fileName);

        var p = DocumentPalette.Resolve(brand, DocumentPalette.House);
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));
            page.Header().Element(c => Header(c, payment, template, brand, p));
            page.Content().Element(c => Content(c, payment, brand, p));
            page.Footer().Element(c => Footer(c, payment, template, brand, printed));
        }));
        return (PdfRenderGate.Run(document.GeneratePdf), fileName);
    }

    private static string Money(decimal v, string ccy) => $"{v.ToString("N2", Culture)} {ccy}".Trim();
    private static string Date(DateTime d) => d.ToString("dd MMM yyyy", Culture);
    private static string Label(string code) => code.Replace('_', ' ');

    private static string Reference(CustomerPaymentDetailModel p) =>
        string.Join(" / ", new[] { p.ChequeNumber, p.BankReference }.Where(s => !string.IsNullOrWhiteSpace(s)));

    internal static TemplatedDocument Templated(CustomerPaymentDetailModel pay, DateTime printed)
    {
        var doc = new TemplatedDocument
        {
            TemplateType = DocumentTemplateTypes.PaymentReceipt, Title = "PAYMENT RECEIPT", GeneratedAt = printed,
            Values = new(StringComparer.OrdinalIgnoreCase)
            {
                ["DocumentNumber"] = pay.PaymentNumber, ["DocumentDate"] = Date(pay.PaymentDate),
                ["CustomerName"] = pay.PartnerName, ["PaymentNumber"] = pay.PaymentNumber, ["PaymentDate"] = Date(pay.PaymentDate),
                ["PaymentMethod"] = Label(pay.PaymentMethod), ["Reference"] = Reference(pay), ["CurrencyCode"] = pay.CurrencyCode,
                ["AmountPaid"] = BrandedDocumentKit.Money(pay.Amount), ["TotalAmount"] = BrandedDocumentKit.Money(pay.Amount),
                ["AllocatedAmount"] = BrandedDocumentKit.Money(pay.AllocatedAmount), ["UnallocatedAmount"] = BrandedDocumentKit.Money(pay.UnallocatedAmount),
                ["InvoiceNumber"] = string.Join(", ", pay.Allocations.Select(a => a.InvoiceNumber)),
                ["AmountInWords"] = AmountInWords.Format(pay.Amount, pay.CurrencyCode), ["Notes"] = pay.Notes ?? "",
                ["PartyLabel"] = "Received From:", ["PartyName"] = pay.PartnerName, ["PartyAddress"] = "",
                ["HeaderFacts"] = $"Method: {Label(pay.PaymentMethod)}",
            },
        };
        doc.LineColumns.AddRange([new("Invoice", 2f), new("Applied", 1.2f, true), new("Balance after", 1.2f, true)]);
        foreach (var a in pay.Allocations)
            doc.LineRows.Add([a.InvoiceNumber, a.AllocatedAmount.ToString("N2", Culture), a.InvoiceBalanceDue.ToString("N2", Culture)]);
        doc.Totals.AddRange([new("Amount received", Money(pay.Amount, pay.CurrencyCode), true),
                             new("Applied to invoices", Money(pay.AllocatedAmount, pay.CurrencyCode)),
                             new("On account", Money(pay.UnallocatedAmount, pay.CurrencyCode))]);
        return doc;
    }

    private static void Header(IContainer container, CustomerPaymentDetailModel pay, PoDocumentTemplateModel? t, DocumentBranding? brand, DocumentPalette p)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                var left = row.RelativeItem();
                var branded = brand is not null && BrandedDocumentKit.TryComposeHeader(left, brand,
                    BrandedDocumentKit.HeaderValues(brand, "PAYMENT RECEIPT", pay.PaymentNumber, pay.PaymentDate), p.Dark);
                if (!branded)
                    left.Column(col =>
                    {
                        col.Item().Text(t?.CompanyName ?? brand?.CompanyName ?? "Company Name").FontSize(16).Bold().FontColor(p.Dark);
                        if (!string.IsNullOrWhiteSpace(t?.CompanyAddress)) col.Item().PaddingTop(2).Text(t.CompanyAddress).FontSize(8);
                        var contact = string.Join("   |   ", new[]
                        {
                            string.IsNullOrWhiteSpace(t?.CompanyPhone) ? null : $"Tel: {t.CompanyPhone}",
                            string.IsNullOrWhiteSpace(t?.CompanyEmail) ? null : $"Email: {t.CompanyEmail}",
                            string.IsNullOrWhiteSpace(t?.CompanyTaxId) ? null : $"Tax ID: {t.CompanyTaxId}",
                        }.Where(s => s is not null));
                        if (contact.Length > 0) col.Item().PaddingTop(2).Text(contact).FontSize(8);
                    });
                if (brand?.LogoBytes is { Length: > 0 } logo) row.ConstantItem(80).Height(50).Image(logo).FitArea();
            });

            column.Item().PaddingTop(16).Text("PAYMENT RECEIPT").FontSize(15).Bold().AlignCenter();

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("Received From:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(1).Text(pay.PartnerName).FontSize(10.5f).Bold();
                });
                row.ConstantItem(190).Column(col =>
                {
                    col.Item().AlignRight().Text($"Receipt No: {pay.PaymentNumber}").FontSize(9).Bold();
                    col.Item().PaddingTop(2).AlignRight().Text($"Date: {Date(pay.PaymentDate)}").FontSize(9);
                    col.Item().PaddingTop(2).AlignRight().Text($"Status: {Label(pay.Status)}").FontSize(9);
                });
            });

            column.Item().PaddingTop(14).LineHorizontal(1.5f).LineColor(p.Primary);
        });
    }

    private static void Content(IContainer container, CustomerPaymentDetailModel pay, DocumentBranding? brand, DocumentPalette p)
    {
        container.PaddingTop(14).Column(column =>
        {
            column.Item().PaddingBottom(14).Background(p.Tint).Padding(10).Column(box =>
            {
                box.Item().Row(r =>
                {
                    r.RelativeItem().Text("Amount received").FontSize(10).Bold();
                    r.ConstantItem(180).AlignRight().Text(Money(pay.Amount, pay.CurrencyCode)).FontSize(13).Bold().FontColor(p.Dark);
                });
                box.Item().PaddingTop(2).Text(AmountInWords.Format(pay.Amount, pay.CurrencyCode)).FontSize(8.5f).Italic();
                box.Item().PaddingTop(6).Text($"Method: {Label(pay.PaymentMethod)}" +
                    (Reference(pay) is { Length: > 0 } reference ? $"   ·   Reference: {reference}" : "")).FontSize(8.5f);
            });

            if (pay.Allocations.Count > 0)
            {
                column.Item().PaddingBottom(6).Text("APPLIED TO").FontSize(9).Bold().FontColor(p.Dark);
                column.Item().PaddingBottom(12).Table(table =>
                {
                    table.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(1.2f); c.RelativeColumn(1.2f); c.RelativeColumn(1); });
                    table.Header(h =>
                    {
                        foreach (var (title, right) in new[] { ("Invoice", false), ("Applied", true), ("Balance after", true), ("Status", false) })
                        {
                            var cell = h.Cell().Background(p.Primary).DefaultTextStyle(x => x.Bold().FontSize(8.5f).FontColor(Colors.White))
                                        .PaddingVertical(6).PaddingHorizontal(4);
                            (right ? cell.AlignRight() : cell).Text(title);
                        }
                    });
                    foreach (var a in pay.Allocations)
                    {
                        table.Cell().PaddingVertical(5).PaddingHorizontal(4).Text(a.InvoiceNumber);
                        table.Cell().PaddingVertical(5).PaddingHorizontal(4).AlignRight().Text(Money(a.AllocatedAmount, pay.CurrencyCode));
                        table.Cell().PaddingVertical(5).PaddingHorizontal(4).AlignRight().Text(Money(a.InvoiceBalanceDue, pay.CurrencyCode));
                        table.Cell().PaddingVertical(5).PaddingHorizontal(4).Text(Label(a.InvoiceStatus));
                    }
                });
            }

            column.Item().PaddingBottom(12).AlignRight().Width(250).Column(col =>
            {
                foreach (var (label, value) in new[] { ("Applied to invoices", pay.AllocatedAmount), ("On account", pay.UnallocatedAmount) })
                    col.Item().PaddingBottom(3).Row(r =>
                    {
                        r.RelativeItem().Text(label).FontSize(9);
                        r.ConstantItem(130).AlignRight().Text(Money(value, pay.CurrencyCode)).FontSize(9.5f);
                    });
            });

            if (!string.IsNullOrWhiteSpace(pay.Notes))
            {
                column.Item().PaddingBottom(6).Text("NOTES").FontSize(9).Bold().FontColor(p.Dark);
                column.Item().PaddingBottom(12).Background(p.Tint).Padding(8).Text(pay.Notes).FontSize(8.5f);
            }

            if (brand is not null) BrandedDocumentKit.ComposeTerms(column, brand, p.Dark);

            column.Item().PaddingTop(30).AlignCenter().Text("This is a computer-generated receipt and needs no signature.").FontSize(8).Italic();
        });
    }

    private static void Footer(IContainer container, CustomerPaymentDetailModel pay, PoDocumentTemplateModel? t, DocumentBranding? brand, DateTime printed)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
            var branded = brand is not null && BrandedDocumentKit.TryComposeFooter(column, brand,
                BrandedDocumentKit.HeaderValues(brand, "PAYMENT RECEIPT", pay.PaymentNumber, pay.PaymentDate));
            if (!branded && !string.IsNullOrWhiteSpace(t?.FooterText))
                column.Item().AlignCenter().Text(t.FooterText).FontSize(7).FontColor(Colors.Grey.Medium);
            column.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text($"Generated {printed.ToString("yyyy-MM-dd HH:mm", Culture)} UTC").FontSize(7).FontColor(Colors.Grey.Medium);
                row.RelativeItem().AlignRight().Text(x =>
                {
                    x.CurrentPageNumber().FontSize(7).FontColor(Colors.Grey.Medium);
                    x.Span(" of ").FontSize(7).FontColor(Colors.Grey.Medium);
                    x.TotalPages().FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });
    }
}

/// <summary>
/// A50 TPL (D-26) — POST api/sales-invoices/{uuid}/email: the invoice PDF to the customer with the org's InvoiceSent template
/// (else the built-in text). A draft is not a request for payment and a cancelled invoice is void: both are refused.
/// </summary>
public sealed class SalesInvoiceEmailService
{
    private readonly ISalesInvoiceService _invoices;
    private readonly ISalesInvoiceDocumentService _documents;
    private readonly ISupplierContactLookupService _contacts;
    private readonly IPoDocumentTemplateService _templates;
    private readonly DocumentEmailDispatcher _dispatcher;

    public SalesInvoiceEmailService(
        ISalesInvoiceService invoices, ISalesInvoiceDocumentService documents, ISupplierContactLookupService contacts,
        IPoDocumentTemplateService templates, IEmailSender sender, IEmailTemplateRenderer? emailTemplates = null)
    {
        _invoices = invoices;
        _documents = documents;
        _contacts = contacts;
        _templates = templates;
        _dispatcher = new DocumentEmailDispatcher(sender, emailTemplates);
    }

    public async Task<DocumentEmailResult> SendAsync(Guid invoiceUuid, DocumentEmailRequest? request, CancellationToken ct = default)
    {
        var invoice = await _invoices.GetAsync(invoiceUuid) ?? throw new NotFoundException("SalesInvoice", invoiceUuid);
        if (invoice.Status == SalesInvoiceStatuses.Draft)
            throw new BadRequestException("A draft invoice is not a request for payment; issue it before emailing it.");
        if (invoice.Status == SalesInvoiceStatuses.Cancelled)
            throw new BadRequestException("A cancelled invoice is void and is not emailed.");

        var contact = await _contacts.GetContactInfoAsync(invoice.PartnerId);
        var pdf = await _documents.GeneratePdfAsync(invoiceUuid);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CustomerName"] = invoice.PartnerName, ["CustomerEmail"] = contact?.Email ?? "",
            ["InvoiceNumber"] = invoice.InvoiceNumber, ["InvoiceDate"] = BrandedDocumentKit.Date(invoice.InvoiceDate),
            ["DueDate"] = BrandedDocumentKit.Date(invoice.DueDate), ["TotalAmount"] = BrandedDocumentKit.Money(invoice.GrandTotal),
            ["AmountDue"] = BrandedDocumentKit.Money(invoice.BalanceDue), ["CurrencyCode"] = invoice.CurrencyCode,
            ["AmountInWords"] = AmountInWords.Format(invoice.GrandTotal, invoice.CurrencyCode),
        };
        DocumentEmailDispatcher.AddCompany(values, await _templates.GetActiveAsync());

        return await _dispatcher.SendAsync(request, contact?.Email, EmailTemplateTypes.InvoiceSent, values,
            new EmailAttachment(pdf.FileName, pdf.Content, "application/pdf"), ct);
    }
}
