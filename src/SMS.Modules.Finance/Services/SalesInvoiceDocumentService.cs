using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A29-P7-08 §9.1 — the sales invoice as a PDF for the customer. Same structure, palette and
/// letterhead source as the supplier-invoice and purchase-order documents, so the three read as one
/// product: the company configures one letterhead, under Purchase Order Templates, and it is reused.
/// <para>
/// The direction is the mirror of <see cref="InvoiceDocumentService"/>: the letterhead company is now
/// who is billing, and the customer is who is billed.
/// </para>
/// </summary>
internal sealed class SalesInvoiceDocumentService : ISalesInvoiceDocumentService
{
    private readonly ISalesInvoiceService            _invoices;
    private readonly IPoDocumentTemplateService      _templates;
    private readonly ISupplierContactLookupService   _contacts;
    private readonly IWebHostEnvironment             _env;
    private readonly TimeProvider                    _clock;

    private const string BrandColor     = "#6C63FF";
    private const string BrandColorDark = "#4A42CC";
    private const string BrandColorTint = "#F4F3FF";
    private const string WarningColor   = "#B45309";

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public SalesInvoiceDocumentService(
        ISalesInvoiceService invoices, IPoDocumentTemplateService templates,
        ISupplierContactLookupService contacts, IWebHostEnvironment env, TimeProvider? clock = null)
    {
        _invoices  = invoices;
        _templates = templates;
        _contacts  = contacts;
        _env       = env;
        _clock     = clock ?? TimeProvider.System;
    }

    public async Task<SalesInvoicePdf> GeneratePdfAsync(Guid invoiceUuid)
    {
        var invoice = await _invoices.GetAsync(invoiceUuid)
            ?? throw new NotFoundException("SalesInvoice", invoiceUuid);

        var template = await _templates.GetActiveAsync();
        var customer = await _contacts.GetContactInfoAsync(invoice.PartnerId);
        var logo     = TryLoadLogoBytes(template?.CompanyLogoUrl);
        var printed  = _clock.GetUtcNow().UtcDateTime;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));

                page.Header().Element(c => ComposeHeader(c, template, invoice, customer, logo));
                page.Content().Element(c => ComposeContent(c, invoice, template));
                page.Footer().Element(c => ComposeFooter(c, template, printed));
            });
        });

        return new SalesInvoicePdf($"{invoice.InvoiceNumber}.pdf", document.GeneratePdf());
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
        SupplierContactInfo? customer, byte[]? logo)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text(template?.CompanyName ?? "Company Name").FontSize(16).Bold().FontColor(BrandColorDark);
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
                           ? WarningColor : BrandColorDark);
                });
            });

            column.Item().PaddingTop(14).LineHorizontal(1.5f).LineColor(BrandColor);
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

    private static void ComposeContent(IContainer container, SalesInvoiceDetailModel invoice, PoDocumentTemplateModel? template)
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
            ComposeLineItemsTable(column, invoice);
            ComposeSummaryBox(column, invoice);

            if (invoice.Payments.Count > 0)
                ComposePaymentsTable(column, invoice);

            if (AwaitsPayment(invoice))
                ComposePaymentDetails(column, invoice, template);

            if (!string.IsNullOrWhiteSpace(invoice.Notes))
            {
                column.Item().PaddingBottom(6).Text("NOTES").FontSize(9).Bold().FontColor(BrandColorDark);
                column.Item().Background(BrandColorTint).Padding(8).Text(invoice.Notes).FontSize(8.5f);
            }
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

    private static void ComposeLineItemsTable(ColumnDescriptor column, SalesInvoiceDetailModel invoice)
    {
        column.Item().PaddingBottom(10).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(24);
                columns.RelativeColumn(3);
                columns.ConstantColumn(48);
                columns.ConstantColumn(70);
                columns.ConstantColumn(40);
                columns.ConstantColumn(40);
                columns.ConstantColumn(78);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("#");
                header.Cell().Element(HeaderCell).Text("Description");
                header.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                header.Cell().Element(HeaderCell).AlignRight().Text("Unit Price");
                header.Cell().Element(HeaderCell).AlignRight().Text("Disc %");
                header.Cell().Element(HeaderCell).AlignRight().Text("Tax %");
                header.Cell().Element(HeaderCell).AlignRight().Text("Amount");

                static IContainer HeaderCell(IContainer c) =>
                    c.Background(BrandColor)
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
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.TaxPercent.ToString("0.##", Culture));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.LineTotal.ToString("N2", Culture));

                static IContainer BodyCell(IContainer c, bool isLast, bool isEven) =>
                    c.Background(isEven ? BrandColorTint : Colors.White)
                     .PaddingVertical(6).PaddingHorizontal(4)
                     .BorderBottom(isLast ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
            }
        });
    }

    private static void ComposeSummaryBox(ColumnDescriptor column, SalesInvoiceDetailModel invoice)
    {
        column.Item().PaddingBottom(14).AlignRight().Width(240).Column(col =>
        {
            SummaryRow(col, "Subtotal", Money(invoice.Subtotal, invoice.CurrencyCode));
            if (invoice.DiscountAmount != 0m)
                SummaryRow(col, "Discount", "-" + Money(invoice.DiscountAmount, invoice.CurrencyCode));
            SummaryRow(col, "Tax", Money(invoice.TaxAmount, invoice.CurrencyCode));

            col.Item().PaddingTop(4).BorderTop(1.5f).BorderColor(BrandColor).PaddingTop(6).Row(row =>
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
                   .FontSize(12).Bold().FontColor(BrandColorDark);
            });
        });
    }

    private static void SummaryRow(ColumnDescriptor col, string label, string value) =>
        col.Item().PaddingBottom(4).Row(row =>
        {
            row.RelativeItem().Text(label).FontSize(9).FontColor("#374151");
            row.ConstantItem(120).AlignRight().Text(value).FontSize(9.5f).FontColor("#111827");
        });

    private static void ComposePaymentsTable(ColumnDescriptor column, SalesInvoiceDetailModel invoice)
    {
        column.Item().PaddingBottom(6).Text("PAYMENTS RECEIVED").FontSize(9).Bold().FontColor(BrandColorDark);

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
                var p      = payments[i];
                var isLast = i == payments.Count - 1;

                table.Cell().Element(c => Cell(c, isLast)).Text(p.PaymentNumber);
                table.Cell().Element(c => Cell(c, isLast)).Text(Date(p.PaymentDate));
                table.Cell().Element(c => Cell(c, isLast)).Text(StatusLabel(p.PaymentMethod));
                table.Cell().Element(c => Cell(c, isLast)).AlignRight().Text(Money(p.AllocatedAmount, invoice.CurrencyCode));
                table.Cell().Element(c => Cell(c, isLast)).Text(StatusLabel(p.PaymentStatus));

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
    /// </summary>
    private static void ComposePaymentDetails(ColumnDescriptor column, SalesInvoiceDetailModel invoice, PoDocumentTemplateModel? template)
    {
        var bankLines = (template?.BankDetails ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        column.Item().PaddingBottom(6).Text("PAYMENT DETAILS").FontSize(9).Bold().FontColor(BrandColorDark);

        column.Item().PaddingBottom(14).Background(BrandColorTint).Padding(8).Column(box =>
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

    private static void ComposeFooter(IContainer container, PoDocumentTemplateModel? template, DateTime printed)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

            if (!string.IsNullOrWhiteSpace(template?.FooterText))
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
