using System.Globalization;
using Microsoft.AspNetCore.Hosting;
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
/// A29-P7-08 §9.1 — the sales invoice as a PDF for the customer. Same structure, palette and
/// letterhead source as the supplier-invoice and purchase-order documents, so the three read as one
/// product: the company configures one letterhead, under Purchase Order Templates, and it is reused.
/// <para>
/// The direction is the mirror of <see cref="InvoiceDocumentService"/>: the letterhead company is now
/// who is billing, and the customer is who is billed.
/// </para>
/// <para>
/// A50 (D-25) — the org's default SalesInvoice document template, when it has one, brands the built-in layout (colour, logo,
/// header / footer, bank details, terms); one with a BodyHtml is rendered by Onboarding's templated renderer instead. Without
/// a template the PDF is the pre-A50 one, byte for byte. Rendered through <see cref="PdfRenderGate"/>.
/// </para>
/// </summary>
internal sealed class SalesInvoiceDocumentService : ISalesInvoiceDocumentService
{
    private readonly ISalesInvoiceService            _invoices;
    private readonly IPoDocumentTemplateService      _templates;
    private readonly ISupplierContactLookupService   _contacts;
    private readonly IWebHostEnvironment             _env;
    private readonly TimeProvider                    _clock;
    private readonly IDocumentBrandingProvider       _branding;
    private readonly ITemplatedDocumentRenderer      _templated;

    private const string WarningColor   = "#B45309";

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public SalesInvoiceDocumentService(
        ISalesInvoiceService invoices, IPoDocumentTemplateService templates,
        ISupplierContactLookupService contacts, IWebHostEnvironment env, TimeProvider? clock = null,
        IDocumentBrandingProvider? branding = null, ITemplatedDocumentRenderer? templated = null)
    {
        _invoices  = invoices;
        _templates = templates;
        _contacts  = contacts;
        _env       = env;
        _clock     = clock ?? TimeProvider.System;
        _branding  = branding ?? new NullDocumentBrandingProvider();
        _templated = templated ?? new NullTemplatedDocumentRenderer();
    }

    public async Task<SalesInvoicePdf> GeneratePdfAsync(Guid invoiceUuid)
    {
        var invoice = await _invoices.GetAsync(invoiceUuid)
            ?? throw new NotFoundException("SalesInvoice", invoiceUuid);

        var template = await _templates.GetActiveAsync();
        var customer = await _contacts.GetContactInfoAsync(invoice.PartnerId);
        var brand    = await _branding.GetAsync(DocumentTemplateTypes.SalesInvoice);
        var logo     = brand?.LogoBytes ?? TryLoadLogoBytes(template?.CompanyLogoUrl);
        var printed  = _clock.GetUtcNow().UtcDateTime;
        var fileName = $"{invoice.InvoiceNumber}.pdf";

        if (brand is not null)
            brand = BrandedDocumentKit.WithLetterhead(brand, template?.CompanyName, template?.CompanyAddress, template?.CompanyPhone,
                template?.CompanyEmail, template?.CompanyTaxId);

        if (brand is not null && !string.IsNullOrWhiteSpace(brand.BodyHtml))
        {
            var bytes = _templated.Render(brand, Templated(invoice, customer, template, printed));
            if (bytes is not null) return new SalesInvoicePdf(fileName, bytes);
        }

        var p = DocumentPalette.Resolve(brand, DocumentPalette.House);
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));

                page.Header().Element(c => ComposeHeader(c, template, invoice, customer, logo, p, brand));
                page.Content().Element(c => ComposeContent(c, invoice, template, p, brand));
                page.Footer().Element(c => ComposeFooter(c, template, printed, brand, invoice));
            });
        });

        return new SalesInvoicePdf(fileName, PdfRenderGate.Run(document.GeneratePdf));
    }

    /// <summary>A50 — the invoice for a BodyHtml template (TEMPLATE-PLACEHOLDERS.md).</summary>
    internal static TemplatedDocument Templated(
        SalesInvoiceDetailModel invoice, SupplierContactInfo? customer, PoDocumentTemplateModel? template, DateTime printed)
    {
        var anyCode = invoice.Lines.Any(l => !string.IsNullOrWhiteSpace(l.TaxCode));
        var doc = new TemplatedDocument
        {
            TemplateType = DocumentTemplateTypes.SalesInvoice,
            Title = invoice.Status == SalesInvoiceStatuses.Draft ? "DRAFT INVOICE" : "INVOICE",
            GeneratedAt = printed,
            Values = new(StringComparer.OrdinalIgnoreCase)
            {
                ["DocumentNumber"] = invoice.InvoiceNumber, ["DocumentDate"] = Date(invoice.InvoiceDate),
                ["InvoiceNumber"] = invoice.InvoiceNumber, ["InvoiceDate"] = Date(invoice.InvoiceDate), ["DueDate"] = Date(invoice.DueDate),
                ["PaymentTerms"] = PaymentTerms(invoice), ["SaleOrderNumber"] = invoice.SaleOrderNumber, ["DeliveryNumber"] = invoice.DeliveryNumber ?? "-",
                ["Status"] = StatusLabel(invoice.Status), ["CurrencyCode"] = invoice.CurrencyCode,
                ["CustomerName"] = invoice.PartnerName, ["CustomerAddress"] = customer?.Address ?? "", ["CustomerEmail"] = customer?.Email ?? "",
                ["CustomerPhone"] = customer?.Phone ?? "",
                ["Subtotal"] = BrandedDocumentKit.Money(invoice.Subtotal), ["DiscountAmount"] = BrandedDocumentKit.Money(invoice.DiscountAmount),
                ["TaxAmount"] = BrandedDocumentKit.Money(invoice.TaxAmount), ["TotalAmount"] = BrandedDocumentKit.Money(invoice.GrandTotal),
                ["AmountPaid"] = BrandedDocumentKit.Money(invoice.AmountPaid), ["BalanceDue"] = BrandedDocumentKit.Money(invoice.BalanceDue),
                ["AmountInWords"] = AmountInWords.Format(invoice.GrandTotal, invoice.CurrencyCode), ["Notes"] = invoice.Notes ?? "",
                ["PartyLabel"] = "Billed To:", ["PartyName"] = invoice.PartnerName, ["PartyAddress"] = customer?.Address ?? "",
                ["HeaderFacts"] = $"Due Date: {Date(invoice.DueDate)}\nStatus: {StatusLabel(invoice.Status)}",
            },
            SignatureDisclaimer = template?.SignatureDisclaimer,
        };
        doc.LineColumns.AddRange([new("#", 0.4f), new("Description", 3f), new("Qty", 0.9f, true), new("Unit Price", 1.1f, true),
                                  new("Disc %", 0.7f, true), new(anyCode ? "Tax" : "Tax %", anyCode ? 1.2f : 0.7f, true), new("Amount", 1.3f, true)]);
        foreach (var l in invoice.Lines)
            doc.LineRows.Add([l.LineNo.ToString(Culture), l.Description, l.Quantity.ToString("N2", Culture), l.UnitPrice.ToString("N2", Culture),
                              l.DiscountPercent.ToString("0.##", Culture), TaxLabel(l, anyCode), l.LineTotal.ToString("N2", Culture)]);
        foreach (var g in invoice.Lines.Where(l => l.TaxPercent != 0m).GroupBy(l => TaxLabel(l, true)))
            doc.TaxSummary.Add(new(g.Key, Money(g.Sum(l => l.LineTotal - l.LineTotal / (1 + l.TaxPercent / 100m)), invoice.CurrencyCode)));
        doc.Totals.Add(new("Subtotal", Money(invoice.Subtotal, invoice.CurrencyCode)));
        if (invoice.DiscountAmount != 0m) doc.Totals.Add(new("Discount", "-" + Money(invoice.DiscountAmount, invoice.CurrencyCode)));
        doc.Totals.Add(new("Tax", Money(invoice.TaxAmount, invoice.CurrencyCode)));
        doc.Totals.Add(new("Total", Money(invoice.GrandTotal, invoice.CurrencyCode), true));
        if (invoice.AmountPaid != 0m) doc.Totals.Add(new("Paid", "-" + Money(invoice.AmountPaid, invoice.CurrencyCode)));
        doc.Totals.Add(new("Balance Due", Money(invoice.BalanceDue, invoice.CurrencyCode), true));
        return doc;
    }

    private byte[]? TryLoadLogoBytes(string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl)) return null;

        var relative = logoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var webRoot  = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var fullPath = Path.Combine(webRoot, relative);

        return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    private static string Money(decimal amount, string currency) => $"{amount.ToString("N2", Culture)} {currency}";
    private static string Date(DateTime date) => date.ToString("dd MMM yyyy", Culture);

    private static string StatusLabel(string status) => status.Replace('_', ' ');

    // ── Composition ──────────────────────────────────────────────────────────

    private static void ComposeHeader(
        IContainer container, PoDocumentTemplateModel? template, SalesInvoiceDetailModel invoice,
        SupplierContactInfo? customer, byte[]? logo, DocumentPalette p, DocumentBranding? brand)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                var letterhead = row.RelativeItem();
                var branded = brand is not null && BrandedDocumentKit.TryComposeHeader(letterhead, brand,
                    BrandedDocumentKit.HeaderValues(brand, "INVOICE", invoice.InvoiceNumber, invoice.InvoiceDate), p.Dark);

                if (!branded)
                    letterhead.Column(col =>
                    {
                        col.Item().Text(template?.CompanyName ?? brand?.CompanyName ?? "Company Name").FontSize(16).Bold().FontColor(p.Dark);
                        if (!string.IsNullOrWhiteSpace(template?.CompanyAddress))
                            col.Item().PaddingTop(2).Text(template.CompanyAddress).FontSize(8);
                        var contactLine = string.Join("   |   ", new[]
                        {
                            !string.IsNullOrWhiteSpace(template?.CompanyPhone) ? $"Tel: {template.CompanyPhone}" : null,
                            !string.IsNullOrWhiteSpace(template?.CompanyEmail) ? $"Email: {template.CompanyEmail}" : null,
                            !string.IsNullOrWhiteSpace(template?.CompanyTaxId) ? $"Tax ID: {template.CompanyTaxId}" : null,
                        }.Where(s => s is not null));
                        if (!string.IsNullOrWhiteSpace(contactLine))
                            col.Item().PaddingTop(2).Text(contactLine).FontSize(8);
                    });

                if (logo is not null)
                    row.ConstantItem(80).Height(50).Image(logo).FitArea();
            });

            column.Item().PaddingTop(16).Text(invoice.Status == SalesInvoiceStatuses.Draft ? "DRAFT INVOICE" : "INVOICE")
                  .FontSize(15).Bold().AlignCenter();

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("Billed To:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(1).Text(invoice.PartnerName).FontSize(10.5f).Bold();
                    if (!string.IsNullOrWhiteSpace(customer?.Address))
                        col.Item().PaddingTop(1).Text(customer.Address).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                    if (!string.IsNullOrWhiteSpace(customer?.Phone))
                        col.Item().PaddingTop(1).Text($"Tel: {customer.Phone}").FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                    if (!string.IsNullOrWhiteSpace(customer?.Email))
                        col.Item().PaddingTop(1).Text($"Email: {customer.Email}").FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(170).Column(col =>
                {
                    col.Item().AlignRight().Text($"Invoice No: {invoice.InvoiceNumber}").FontSize(9).Bold();
                    col.Item().PaddingTop(2).AlignRight().Text($"Invoice Date: {Date(invoice.InvoiceDate)}").FontSize(9);
                    col.Item().PaddingTop(2).AlignRight().Text($"Due Date: {Date(invoice.DueDate)}").FontSize(9);
                    col.Item().PaddingTop(2).AlignRight().Text($"Status: {StatusLabel(invoice.Status)}").FontSize(9).Bold()
                       .FontColor(invoice.Status is SalesInvoiceStatuses.Overdue or SalesInvoiceStatuses.Cancelled
                           ? WarningColor : p.Dark);
                });
            });

            column.Item().PaddingTop(14).LineHorizontal(1.5f).LineColor(p.Primary);
        });
    }

    /// <summary>
    /// How long the customer has, read off the invoice itself so it is always true to it: an invoice
    /// whose due date was edited says what it now is, not the 30 days it started with.
    /// </summary>
    internal static string PaymentTerms(SalesInvoiceDetailModel invoice)
    {
        var days = (invoice.DueDate.Date - invoice.InvoiceDate.Date).Days;
        return days <= 0 ? "Due on receipt" : days == 1 ? "Net 1 day" : $"Net {days} days";
    }

    /// <summary>Whether there is a balance for the customer to pay, and so a reason to say where.</summary>
    private static bool AwaitsPayment(SalesInvoiceDetailModel invoice) =>
        invoice.BalanceDue > 0m && invoice.Status != SalesInvoiceStatuses.Cancelled;

    private static void ComposeContent(
        IContainer container, SalesInvoiceDetailModel invoice, PoDocumentTemplateModel? template, DocumentPalette p, DocumentBranding? brand)
    {
        container.PaddingTop(14).Column(column =>
        {
            if (invoice.Status == SalesInvoiceStatuses.Draft)
                column.Item().PaddingBottom(10).Background("#FEF3C7").Padding(6).AlignCenter()
                      .Text("DRAFT — not yet issued. This is not a request for payment.").FontSize(9).Bold().FontColor(WarningColor);
            else if (invoice.Status == SalesInvoiceStatuses.Cancelled)
                column.Item().PaddingBottom(10).Background("#FEE2E2").Padding(6).AlignCenter()
                      .Text("CANCELLED — this invoice is void.").FontSize(9).Bold().FontColor(WarningColor);

            ComposeReferenceStrip(column, invoice);
            ComposeLineItemsTable(column, invoice, p);
            ComposeSummaryBox(column, invoice, p);

            if (invoice.Payments.Count > 0)
                ComposePaymentsTable(column, invoice, p);

            if (AwaitsPayment(invoice))
                ComposePaymentDetails(column, invoice, template, p, brand);

            if (!string.IsNullOrWhiteSpace(invoice.Notes))
            {
                column.Item().PaddingBottom(6).Text("NOTES").FontSize(9).Bold().FontColor(p.Dark);
                column.Item().Background(p.Tint).Padding(8).Text(invoice.Notes).FontSize(8.5f);
            }

            if (brand is not null)
                BrandedDocumentKit.ComposeTerms(column, brand, p.Dark);
        });
    }

    private static void ComposeReferenceStrip(ColumnDescriptor column, SalesInvoiceDetailModel invoice)
    {
        var rows = new List<(string Label, string Value)>
        {
            ("Sale Order",    invoice.SaleOrderNumber),
            ("Delivery",      invoice.DeliveryNumber ?? "-"),
            ("Payment Terms", PaymentTerms(invoice)),
            ("Currency",      invoice.CurrencyCode),
        };

        column.Item().PaddingBottom(14).Row(row =>
        {
            foreach (var (label, value) in rows)
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
                    col.Item().Text(value).FontSize(9.5f).Bold();
                });
        });
    }

    /// <summary>
    /// SAP alignment (S-3) — what the tax column says for a line. A line with a tax code shows the code with
    /// the rate it was snapshotted at ("GST17 · 17%"); a line without one shows its percentage as it always
    /// did — bare under the "Tax %" heading of an invoice with no codes at all, with a "%" beside coded lines.
    /// </summary>
    internal static string TaxLabel(SalesInvoiceLineModel line, bool anyCode)
    {
        var percent = line.TaxPercent.ToString("0.##", Culture);
        if (!string.IsNullOrWhiteSpace(line.TaxCode)) return $"{line.TaxCode.Trim()} · {percent}%";
        return anyCode ? $"{percent}%" : percent;
    }

    private static void ComposeLineItemsTable(ColumnDescriptor column, SalesInvoiceDetailModel invoice, DocumentPalette p)
    {
        var anyCode = invoice.Lines.Any(l => !string.IsNullOrWhiteSpace(l.TaxCode));

        column.Item().PaddingBottom(10).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(24);
                columns.RelativeColumn(3);
                columns.ConstantColumn(48);
                columns.ConstantColumn(70);
                columns.ConstantColumn(40);
                // A code and its rate need room; an invoice with no codes keeps the old layout exactly.
                columns.ConstantColumn(anyCode ? 76 : 40);
                columns.ConstantColumn(78);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("#");
                header.Cell().Element(HeaderCell).Text("Description");
                header.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                header.Cell().Element(HeaderCell).AlignRight().Text("Unit Price");
                header.Cell().Element(HeaderCell).AlignRight().Text("Disc %");
                header.Cell().Element(HeaderCell).AlignRight().Text(anyCode ? "Tax" : "Tax %");
                header.Cell().Element(HeaderCell).AlignRight().Text("Amount");

                IContainer HeaderCell(IContainer c) =>
                    c.Background(p.Primary)
                     .DefaultTextStyle(x => x.Bold().FontSize(8.5f).FontColor(Colors.White))
                     .PaddingVertical(8).PaddingHorizontal(4);
            });

            var lines = invoice.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                var line   = lines[i];
                var isLast = i == lines.Count - 1;
                var isEven = i % 2 == 1;

                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.LineNo.ToString(Culture));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.Description);
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.Quantity.ToString("N2", Culture));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.UnitPrice.ToString("N2", Culture));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.DiscountPercent.ToString("0.##", Culture));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(TaxLabel(line, anyCode));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.LineTotal.ToString("N2", Culture));

                IContainer BodyCell(IContainer c, bool isLast, bool isEven) =>
                    c.Background(isEven ? p.Tint : Colors.White)
                     .PaddingVertical(6).PaddingHorizontal(4)
                     .BorderBottom(isLast ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
            }
        });
    }

    private static void ComposeSummaryBox(ColumnDescriptor column, SalesInvoiceDetailModel invoice, DocumentPalette p)
    {
        column.Item().PaddingBottom(14).AlignRight().Width(240).Column(col =>
        {
            SummaryRow(col, "Subtotal", Money(invoice.Subtotal, invoice.CurrencyCode));
            if (invoice.DiscountAmount != 0m)
                SummaryRow(col, "Discount", "-" + Money(invoice.DiscountAmount, invoice.CurrencyCode));
            SummaryRow(col, "Tax", Money(invoice.TaxAmount, invoice.CurrencyCode));

            col.Item().PaddingTop(4).BorderTop(1.5f).BorderColor(p.Primary).PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Text("Total").FontSize(10.5f).Bold();
                row.ConstantItem(120).AlignRight().Text(Money(invoice.GrandTotal, invoice.CurrencyCode)).FontSize(10.5f).Bold();
            });

            if (invoice.AmountPaid != 0m)
                col.Item().PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().Text("Paid").FontSize(9).FontColor("#374151");
                    row.ConstantItem(120).AlignRight().Text("-" + Money(invoice.AmountPaid, invoice.CurrencyCode)).FontSize(9.5f);
                });

            col.Item().PaddingTop(6).BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1).PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Text("Balance Due").FontSize(11).Bold();
                row.ConstantItem(120).AlignRight().Text(Money(invoice.BalanceDue, invoice.CurrencyCode))
                   .FontSize(12).Bold().FontColor(p.Dark);
            });
        });
    }

    private static void SummaryRow(ColumnDescriptor col, string label, string value) =>
        col.Item().PaddingBottom(4).Row(row =>
        {
            row.RelativeItem().Text(label).FontSize(9).FontColor("#374151");
            row.ConstantItem(120).AlignRight().Text(value).FontSize(9.5f).FontColor("#111827");
        });

    private static void ComposePaymentsTable(ColumnDescriptor column, SalesInvoiceDetailModel invoice, DocumentPalette p)
    {
        column.Item().PaddingBottom(6).Text("PAYMENTS RECEIVED").FontSize(9).Bold().FontColor(p.Dark);

        column.Item().PaddingBottom(14).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(1.4f);
                columns.RelativeColumn(1);
                columns.RelativeColumn(1);
                columns.RelativeColumn(1);
                columns.RelativeColumn(1);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("Payment #");
                header.Cell().Element(HeaderCell).Text("Date");
                header.Cell().Element(HeaderCell).Text("Method");
                header.Cell().Element(HeaderCell).AlignRight().Text("Applied");
                header.Cell().Element(HeaderCell).Text("Status");

                static IContainer HeaderCell(IContainer c) =>
                    c.Background(Colors.Grey.Darken3)
                     .DefaultTextStyle(x => x.Bold().FontSize(8).FontColor(Colors.White))
                     .PaddingVertical(6).PaddingHorizontal(4);
            });

            var payments = invoice.Payments;
            for (var i = 0; i < payments.Count; i++)
            {
                var pay    = payments[i];
                var isLast = i == payments.Count - 1;

                table.Cell().Element(c => Cell(c, isLast)).Text(pay.PaymentNumber);
                table.Cell().Element(c => Cell(c, isLast)).Text(Date(pay.PaymentDate));
                table.Cell().Element(c => Cell(c, isLast)).Text(StatusLabel(pay.PaymentMethod));
                table.Cell().Element(c => Cell(c, isLast)).AlignRight().Text(Money(pay.AllocatedAmount, invoice.CurrencyCode));
                table.Cell().Element(c => Cell(c, isLast)).Text(StatusLabel(pay.PaymentStatus));

                static IContainer Cell(IContainer c, bool isLast) =>
                    c.DefaultTextStyle(x => x.FontSize(8.5f))
                     .PaddingVertical(5).PaddingHorizontal(4)
                     .BorderBottom(isLast ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
            }
        });
    }

    /// <summary>
    /// Where and by when to pay. The bank details are the company's own, from the organization's
    /// document template; where none are configured the box still says how much is owed, by when and
    /// under what reference, rather than naming an account nobody has entered.
    /// A50 — a SalesInvoice template decides: its bank text (else the letterhead's), or none when it hides bank details.
    /// </summary>
    private static void ComposePaymentDetails(
        ColumnDescriptor column, SalesInvoiceDetailModel invoice, PoDocumentTemplateModel? template, DocumentPalette p, DocumentBranding? brand)
    {
        var bankLines = BrandedDocumentKit.BankLines(brand, template?.BankDetails)
            ?? (template?.BankDetails ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        column.Item().PaddingBottom(6).Text("PAYMENT DETAILS").FontSize(9).Bold().FontColor(p.Dark);

        column.Item().PaddingBottom(14).Background(p.Tint).Padding(8).Column(box =>
        {
            box.Item().Text($"Please pay {Money(invoice.BalanceDue, invoice.CurrencyCode)} by {Date(invoice.DueDate)}, quoting {invoice.InvoiceNumber}.")
               .FontSize(8.5f).Bold();

            if (bankLines.Length > 0)
            {
                box.Item().PaddingTop(4).Text("Bank details").FontSize(8).FontColor(Colors.Grey.Darken1);
                foreach (var line in bankLines)
                    box.Item().Text(line).FontSize(8.5f);
            }
        });
    }

    private static void ComposeFooter(
        IContainer container, PoDocumentTemplateModel? template, DateTime printed, DocumentBranding? brand, SalesInvoiceDetailModel invoice)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

            var branded = brand is not null && BrandedDocumentKit.TryComposeFooter(column, brand,
                BrandedDocumentKit.HeaderValues(brand, "INVOICE", invoice.InvoiceNumber, invoice.InvoiceDate));

            if (!branded && !string.IsNullOrWhiteSpace(template?.FooterText))
                column.Item().AlignCenter().Text(template.FooterText).FontSize(7).FontColor(Colors.Grey.Medium);

            column.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text($"Generated {printed.ToString("yyyy-MM-dd HH:mm", Culture)} UTC").FontSize(7).FontColor(Colors.Grey.Medium);
                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.CurrentPageNumber().FontSize(7).FontColor(Colors.Grey.Medium);
                    text.Span(" of ").FontSize(7).FontColor(Colors.Grey.Medium);
                    text.TotalPages().FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });
    }
}
