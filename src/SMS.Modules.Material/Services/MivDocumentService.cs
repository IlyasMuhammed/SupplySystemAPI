using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Material.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Material.Services;

// Mirrors MirDocumentService/InvoiceDocumentService's structure/branding so MIV PDFs read as the
// same product as PO/Invoice/MIR — same brand palette, same letterhead source. Replaces the old
// client-side jsPDF export (miv-pdf.util.ts), which had no configurable letterhead/logo.
internal sealed class MivDocumentService : IMivDocumentService
{
    private readonly IMivService _mivService;
    private readonly IPoDocumentTemplateService _templateService;
    private readonly IWebHostEnvironment _env;

    private const string BrandColor     = "#6C63FF";
    private const string BrandColorDark = "#4A42CC";
    private const string BrandColorTint = "#F4F3FF";

    public MivDocumentService(IMivService mivService, IPoDocumentTemplateService templateService, IWebHostEnvironment env)
    {
        _mivService      = mivService;
        _templateService = templateService;
        _env             = env;
    }

    public async Task<byte[]> GeneratePdfAsync(Guid mivUuid)
    {
        var miv = await _mivService.GetByUuidAsync(mivUuid)
            ?? throw new NotFoundException("Material Issue Voucher not found");
        var template  = await _templateService.GetActiveAsync();
        var logoBytes = TryLoadLogoBytes(template?.CompanyLogoUrl);

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));

                page.Header().Element(c => ComposeHeader(c, template, miv, logoBytes));
                page.Content().Element(c => ComposeContent(c, template, miv));
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

    private static void ComposeHeader(
        IContainer container, PoDocumentTemplateModel? template, MivDetailModel miv, byte[]? logoBytes)
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

            column.Item().PaddingTop(16).Text("MATERIAL ISSUE VOUCHER").FontSize(15).Bold().AlignCenter();

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("Issued To:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(1).Text(miv.IssuedTo ?? "-").FontSize(10.5f).Bold();
                    if (!string.IsNullOrWhiteSpace(miv.MirProjectName))
                        col.Item().PaddingTop(1).Text(miv.MirProjectName).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(160).Column(col =>
                {
                    col.Item().AlignRight().Text($"Issue No: {miv.IssueNo}").FontSize(9).Bold();
                    col.Item().PaddingTop(2).AlignRight().Text($"Issue Date: {miv.IssueDate:dd MMM yyyy}").FontSize(9);
                });
            });

            column.Item().PaddingTop(14).LineHorizontal(1.5f).LineColor(BrandColor);
        });
    }

    private static void ComposeContent(IContainer container, PoDocumentTemplateModel? template, MivDetailModel miv)
    {
        container.PaddingTop(14).Column(column =>
        {
            ComposeReferenceStrip(column, miv);
            ComposeLineItemsTable(column, miv);
            ComposeTotal(column, miv);

            if (!string.IsNullOrWhiteSpace(miv.Notes))
                ComposeNotes(column, miv);

            ComposeSignatureBlock(column, template);
        });
    }

    private static void ComposeReferenceStrip(ColumnDescriptor column, MivDetailModel miv)
    {
        column.Item().PaddingBottom(14).Row(row =>
        {
            var refRows = new (string Label, string Value)[]
            {
                ("MIR Reference", miv.MirRequestNo),
                ("Status",        miv.Status.Replace('_', ' ')),
            };
            row.RelativeItem().Column(col =>
            {
                foreach (var (label, value) in refRows)
                {
                    col.Item().PaddingBottom(2).Row(r =>
                    {
                        r.ConstantItem(85).Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
                        r.RelativeItem().Text(value).FontSize(8.5f).Bold();
                    });
                }
            });
        });
    }

    private static void ComposeLineItemsTable(ColumnDescriptor column, MivDetailModel miv)
    {
        column.Item().PaddingBottom(10).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(26);
                columns.RelativeColumn(3);
                columns.RelativeColumn(1.2f);
                columns.RelativeColumn(1);
                columns.ConstantColumn(55);
                columns.ConstantColumn(75);
                columns.ConstantColumn(80);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("#");
                header.Cell().Element(HeaderCell).Text("Item Description");
                header.Cell().Element(HeaderCell).Text("Warehouse");
                header.Cell().Element(HeaderCell).Text("UOM");
                header.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                header.Cell().Element(HeaderCell).AlignRight().Text("Unit Cost");
                header.Cell().Element(HeaderCell).AlignRight().Text("Line Value");

                static IContainer HeaderCell(IContainer c) =>
                    c.Background(BrandColor)
                     .DefaultTextStyle(x => x.Bold().FontSize(8.5f).FontColor(Colors.White))
                     .PaddingVertical(8).PaddingHorizontal(4);
            });

            var lines = miv.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var isLast = i == lines.Count - 1;
                var isEven = i % 2 == 1;

                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text((i + 1).ToString());
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.ItemDescription);
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.WarehouseName ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.UnitOfMeasure ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.IssuedQty.ToString("N2"));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.UnitCost.ToString("N2"));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.LineValue.ToString("N2"));

                static IContainer BodyCell(IContainer c, bool isLast, bool isEven) =>
                    c.Background(isEven ? BrandColorTint : Colors.White)
                     .PaddingVertical(6).PaddingHorizontal(4)
                     .BorderBottom(isLast ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
            }

            table.Cell().ColumnSpan(6).Element(c => c.PaddingTop(8).PaddingRight(4).AlignRight()
                .Text("Total Value").FontSize(9.5f).Bold());
            table.Cell().Element(c => c.PaddingTop(8).BorderTop(1.5f).BorderColor(BrandColor).PaddingHorizontal(4))
                .AlignRight().Text(miv.TotalValue.ToString("N2")).FontSize(9.5f).Bold();
        });
    }

    private static void ComposeTotal(ColumnDescriptor column, MivDetailModel miv)
    {
        column.Item().PaddingBottom(14).AlignRight().Width(220).Row(row =>
        {
            row.RelativeItem().Text("Total Value").FontSize(11).Bold();
            row.ConstantItem(100).AlignRight().Text(miv.TotalValue.ToString("N2"))
                .FontSize(12).Bold().FontColor(BrandColorDark);
        });
    }

    private static void ComposeNotes(ColumnDescriptor column, MivDetailModel miv)
    {
        column.Item().PaddingBottom(6).Text("NOTES").FontSize(9).Bold().FontColor(BrandColorDark);
        column.Item().PaddingBottom(14).Background(BrandColorTint).Padding(8).Text(miv.Notes).FontSize(8.5f);
    }

    private static void ComposeSignatureBlock(ColumnDescriptor column, PoDocumentTemplateModel? template)
    {
        if (template?.ShowSignatureBlock == false)
        {
            column.Item().PaddingTop(30).AlignCenter().Text(
                template.SignatureDisclaimer ?? "This is a system generated document and does not require a signature."
            ).FontSize(8).Italic();
            return;
        }

        column.Item().PaddingTop(44).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                col.Item().PaddingTop(4).Text(template?.PreparedByLabel ?? "Prepared By").FontSize(8);
            });
            row.ConstantItem(24);
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                col.Item().PaddingTop(4).Text(template?.ApprovedByLabel ?? "Approved By").FontSize(8);
            });
            row.ConstantItem(24);
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                col.Item().PaddingTop(4).Text(template?.AuthorizedSignatoryLabel ?? "Authorized Signatory").FontSize(8);
            });
        });
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
