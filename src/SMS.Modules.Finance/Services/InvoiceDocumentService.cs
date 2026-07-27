using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Finance.Models;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

// Mirrors SMS.Modules.Demand.Services.PoDocumentService's structure/branding so PO and Invoice
// PDFs read as the same product — same brand palette, same letterhead source (the org only
// configures one letterhead, under Purchase Order Templates, reused here rather than duplicated).
internal sealed class InvoiceDocumentService : IInvoiceDocumentService
{
    private readonly IInvoiceService _invoiceService;
    private readonly IPoDocumentTemplateService _templateService;
    private readonly ISupplierContactLookupService _supplierContactService;
    private readonly IWebHostEnvironment _env;

    private const string BrandColor     = "#6C63FF";
    private const string BrandColorDark = "#4A42CC";
    private const string BrandColorTint = "#F4F3FF";

    public InvoiceDocumentService(
        IInvoiceService invoiceService, IPoDocumentTemplateService templateService,
        ISupplierContactLookupService supplierContactService, IWebHostEnvironment env)
    {
        _invoiceService          = invoiceService;
        _templateService         = templateService;
        _supplierContactService  = supplierContactService;
        _env                     = env;
    }

    public async Task<byte[]> GeneratePdfAsync(Guid invoiceUuid)
    {
        var invoice = await _invoiceService.GetByUuidAsync(invoiceUuid)
            ?? throw new NotFoundException("Invoice not found");
        var template = await _templateService.GetActiveAsync();
        var supplierContact = await _supplierContactService.GetContactInfoAsync(invoice.SupplierId);
        var logoBytes = TryLoadLogoBytes(template?.CompanyLogoUrl);

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));

                page.Header().Element(c => ComposeHeader(c, template, invoice, supplierContact, logoBytes));
                page.Content().Element(c => ComposeContent(c, template, invoice));
                page.Footer().Element(c => ComposeFooter(c, template));
            });
        });

        return document.GeneratePdf();
    }

    private byte[]? TryLoadLogoBytes(string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl)) return null;

        var relative = logoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var webRoot  = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var fullPath = Path.Combine(webRoot, relative);

        return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }

    // ── Composition ────────────────────────────────────────────────────────────

    // Letterhead + a fixed "Billed From / Invoice No / Dates" block — structural, bound directly
    // to invoice data (this document has no user-editable narrative body, unlike the PO letter).
    private static void ComposeHeader(
        IContainer container, PoDocumentTemplateModel? template, InvoiceDetailModel invoice,
        SupplierContactInfo? supplierContact, byte[]? logoBytes)
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

                if (logoBytes is not null)
                    row.ConstantItem(80).Height(50).Image(logoBytes).FitArea();
            });

            column.Item().PaddingTop(16).Text("INVOICE").FontSize(15).Bold().AlignCenter();

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("Billed From:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(1).Text(invoice.SupplierName ?? "-").FontSize(10.5f).Bold();
                    if (!string.IsNullOrWhiteSpace(supplierContact?.Address))
                        col.Item().PaddingTop(1).Text(supplierContact.Address).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                    if (!string.IsNullOrWhiteSpace(supplierContact?.Phone))
                        col.Item().PaddingTop(1).Text($"Tel: {supplierContact.Phone}").FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(160).Column(col =>
                {
                    col.Item().AlignRight().Text($"Invoice No: {invoice.InvoiceNumber}").FontSize(9).Bold();
                    col.Item().PaddingTop(2).AlignRight().Text($"Invoice Date: {invoice.InvoiceDate:dd MMM yyyy}").FontSize(9);
                    col.Item().PaddingTop(2).AlignRight().Text($"Due Date: {invoice.DueDate:dd MMM yyyy}").FontSize(9);
                });
            });

            column.Item().PaddingTop(14).LineHorizontal(1.5f).LineColor(BrandColor);
        });
    }

    private static void ComposeContent(IContainer container, PoDocumentTemplateModel? template, InvoiceDetailModel invoice)
    {
        container.PaddingTop(14).Column(column =>
        {
            ComposeReferenceStrip(column, template, invoice);
            ComposeLineItemsTable(column, invoice);
            ComposeSummaryBox(column, invoice);

            if (invoice.Payments.Count > 0)
                ComposePaymentsTable(column, invoice);

            if (!string.IsNullOrWhiteSpace(invoice.Notes))
                ComposeNotes(column, invoice);
        });
    }

    // "Billed To" + PO/GRN reference + payment terms — a compact key/value strip, no free text.
    private static void ComposeReferenceStrip(ColumnDescriptor column, PoDocumentTemplateModel? template, InvoiceDetailModel invoice)
    {
        column.Item().PaddingBottom(14).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("Billed To:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                col.Item().PaddingTop(1).Text(template?.CompanyName ?? "Company Name").FontSize(10).Bold();
                if (!string.IsNullOrWhiteSpace(template?.CompanyAddress))
                    col.Item().PaddingTop(1).Text(template.CompanyAddress).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                if (!string.IsNullOrWhiteSpace(template?.CompanyPhone))
                    col.Item().PaddingTop(1).Text($"Tel: {template.CompanyPhone}").FontSize(8.5f).FontColor(Colors.Grey.Darken1);
            });

            row.RelativeItem(1.4f).Column(col =>
            {
                var refRows = new (string Label, string Value)[]
                {
                    ("PO Reference",  invoice.PoNumber),
                    ("GRN Reference", invoice.GrnNumber ?? "-"),
                    ("Received Date", invoice.ReceivedDate.ToString("dd MMM yyyy")),
                    ("Payment Terms", invoice.PaymentMethod ?? "-"),
                };
                foreach (var (label, value) in refRows)
                {
                    col.Item().PaddingBottom(2).Row(r =>
                    {
                        r.ConstantItem(85).Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
                        r.RelativeItem().AlignRight().Text(value).FontSize(8.5f).Bold();
                    });
                }
            });
        });
    }

    private static void ComposeLineItemsTable(ColumnDescriptor column, InvoiceDetailModel invoice)
    {
        column.Item().PaddingBottom(10).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(26);
                columns.RelativeColumn(3);
                columns.RelativeColumn(1);
                columns.ConstantColumn(50);
                columns.ConstantColumn(75);
                columns.ConstantColumn(80);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("#");
                header.Cell().Element(HeaderCell).Text("Item Description");
                header.Cell().Element(HeaderCell).Text("UOM");
                header.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                header.Cell().Element(HeaderCell).AlignRight().Text("Unit Price");
                header.Cell().Element(HeaderCell).AlignRight().Text("Amount");

                static IContainer HeaderCell(IContainer c) =>
                    c.Background(BrandColor)
                     .DefaultTextStyle(x => x.Bold().FontSize(8.5f).FontColor(Colors.White))
                     .PaddingVertical(8).PaddingHorizontal(4);
            });

            var lines = invoice.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var isLast = i == lines.Count - 1;
                var isEven = i % 2 == 1;

                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.LineNo.ToString());
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.ItemDescription ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.UnitOfMeasure ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.QtyInvoiced.ToString("N2"));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.UnitPrice.ToString("N2"));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.LineTotal.ToString("N2"));

                static IContainer BodyCell(IContainer c, bool isLast, bool isEven) =>
                    c.Background(isEven ? BrandColorTint : Colors.White)
                     .PaddingVertical(6).PaddingHorizontal(4)
                     .BorderBottom(isLast ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
            }

            table.Cell().ColumnSpan(5).Element(c => c.PaddingTop(8).PaddingRight(4).AlignRight()
                .Text("Subtotal").FontSize(9.5f).Bold());
            table.Cell().Element(c => c.PaddingTop(8).BorderTop(1.5f).BorderColor(BrandColor).PaddingHorizontal(4))
                .AlignRight().Text($"{invoice.Subtotal:N2} {invoice.Currency}").FontSize(9.5f).Bold();
        });
    }

    private static void ComposeSummaryBox(ColumnDescriptor column, InvoiceDetailModel invoice)
    {
        column.Item().PaddingBottom(14).AlignRight().Width(220).Column(col =>
        {
            SummaryRow(col, "Subtotal", $"{invoice.Subtotal:N2} {invoice.Currency}");
            SummaryRow(col, "Tax Amount", $"{invoice.TaxAmount:N2} {invoice.Currency}");

            col.Item().PaddingTop(6).BorderTop(1.5f).BorderColor(BrandColor).PaddingTop(6).Row(row =>
            {
                row.RelativeItem().Text("Amount Due").FontSize(11).Bold();
                row.ConstantItem(100).AlignRight().Text($"{invoice.TotalAmount:N2} {invoice.Currency}")
                    .FontSize(12).Bold().FontColor(BrandColorDark);
            });
        });
    }

    private static void SummaryRow(ColumnDescriptor col, string label, string value, bool muted = false, string? valueColor = null)
    {
        var color = valueColor ?? (muted ? "#9E9E9E" : "#111827");
        col.Item().PaddingBottom(4).Row(row =>
        {
            row.RelativeItem().Text(label).FontSize(9).FontColor(muted ? "#9E9E9E" : "#374151");
            row.ConstantItem(100).AlignRight().Text(value).FontSize(9.5f).FontColor(color);
        });
    }

    private static void ComposePaymentsTable(ColumnDescriptor column, InvoiceDetailModel invoice)
    {
        column.Item().PaddingBottom(6).Text("PAYMENT HISTORY").FontSize(9).Bold().FontColor(BrandColorDark);

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
                header.Cell().Element(HeaderCell).AlignRight().Text("Amount");
                header.Cell().Element(HeaderCell).Text("Status");

                static IContainer HeaderCell(IContainer c) =>
                    c.Background(Colors.Grey.Darken3)
                     .DefaultTextStyle(x => x.Bold().FontSize(8).FontColor(Colors.White))
                     .PaddingVertical(6).PaddingHorizontal(4);
            });

            var payments = invoice.Payments;
            for (var i = 0; i < payments.Count; i++)
            {
                var p = payments[i];
                var isLast = i == payments.Count - 1;

                table.Cell().Element(c => Cell(c, isLast)).Text(p.PaymentNumber);
                table.Cell().Element(c => Cell(c, isLast)).Text(p.PaymentDate.ToString("dd MMM yyyy"));
                table.Cell().Element(c => Cell(c, isLast)).Text(p.PaymentMethod);
                table.Cell().Element(c => Cell(c, isLast)).AlignRight().Text($"{p.AmountPaid:N2} {invoice.Currency}");
                table.Cell().Element(c => Cell(c, isLast)).Text(p.Status);

                static IContainer Cell(IContainer c, bool isLast) =>
                    c.DefaultTextStyle(x => x.FontSize(8.5f))
                     .PaddingVertical(5).PaddingHorizontal(4)
                     .BorderBottom(isLast ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
            }
        });
    }

    private static void ComposeNotes(ColumnDescriptor column, InvoiceDetailModel invoice)
    {
        column.Item().PaddingBottom(6).Text("NOTES").FontSize(9).Bold().FontColor(BrandColorDark);
        column.Item().Background(BrandColorTint).Padding(8).Text(invoice.Notes).FontSize(8.5f);
    }

    private static void ComposeFooter(IContainer container, PoDocumentTemplateModel? template)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

            if (!string.IsNullOrWhiteSpace(template?.FooterText))
                column.Item().AlignCenter().Text(template.FooterText).FontSize(7).FontColor(Colors.Grey.Medium);

            column.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(7).FontColor(Colors.Grey.Medium);
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
