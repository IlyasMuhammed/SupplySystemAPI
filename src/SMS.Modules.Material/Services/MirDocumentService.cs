using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Material.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Material.Services;

// Mirrors SMS.Modules.Finance.Services.InvoiceDocumentService's structure/branding so MIR PDFs
// read as the same product as PO/Invoice — same brand palette, same letterhead source (the org
// only configures one letterhead, under Purchase Order Templates, reused here rather than
// duplicated). Fully structural like Invoice (no free-typed narrative body to template).
internal sealed class MirDocumentService : IMirDocumentService
{
    private readonly IMirService _mirService;
    private readonly IPoDocumentTemplateService _templateService;
    private readonly IWebHostEnvironment _env;

    private const string BrandColor     = "#6C63FF";
    private const string BrandColorDark = "#4A42CC";
    private const string BrandColorTint = "#F4F3FF";

    public MirDocumentService(IMirService mirService, IPoDocumentTemplateService templateService, IWebHostEnvironment env)
    {
        _mirService      = mirService;
        _templateService = templateService;
        _env             = env;
    }

    public async Task<byte[]> GeneratePdfAsync(Guid mirUuid)
    {
        var mir = await _mirService.GetByUuidAsync(mirUuid)
            ?? throw new NotFoundException("Material Issue Request not found");
        var template  = await _templateService.GetActiveAsync();
        var logoBytes = TryLoadLogoBytes(template?.CompanyLogoUrl);

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));

                page.Header().Element(c => ComposeHeader(c, template, mir, logoBytes));
                page.Content().Element(c => ComposeContent(c, template, mir));
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
        IContainer container, PoDocumentTemplateModel? template, MirDetailModel mir, byte[]? logoBytes)
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

            column.Item().PaddingTop(16).Text("MATERIAL ISSUE REQUEST").FontSize(15).Bold().AlignCenter();

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("Requested For:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(1).Text(DescribeRequestFor(mir)).FontSize(10.5f).Bold();
                    if (!string.IsNullOrWhiteSpace(mir.Purpose))
                        col.Item().PaddingTop(1).Text(mir.Purpose).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(160).Column(col =>
                {
                    col.Item().AlignRight().Text($"Request No: {mir.RequestNo}").FontSize(9).Bold();
                    col.Item().PaddingTop(2).AlignRight().Text($"Dated: {mir.CreatedDate:dd MMM yyyy}").FontSize(9);
                    if (mir.RequiredDate.HasValue)
                        col.Item().PaddingTop(2).AlignRight().Text($"Required By: {mir.RequiredDate:dd MMM yyyy}").FontSize(9);
                });
            });

            column.Item().PaddingTop(14).LineHorizontal(1.5f).LineColor(BrandColor);
        });
    }

    private static string DescribeRequestFor(MirDetailModel mir) => mir.RequestType switch
    {
        "PROJECT"     => mir.ProjectName ?? "-",
        "DEPARTMENT"  => mir.Department ?? "-",
        "MAINTENANCE" => mir.MaintenanceRef ?? "-",
        _             => "-"
    };

    private static void ComposeContent(IContainer container, PoDocumentTemplateModel? template, MirDetailModel mir)
    {
        container.PaddingTop(14).Column(column =>
        {
            ComposeReferenceStrip(column, mir);
            ComposeLineItemsTable(column, mir);
            ComposeTotal(column, mir);

            if (!string.IsNullOrWhiteSpace(mir.Notes))
                ComposeNotes(column, mir);

            ComposeSignatureBlock(column, template);
        });
    }

    private static void ComposeReferenceStrip(ColumnDescriptor column, MirDetailModel mir)
    {
        column.Item().PaddingBottom(14).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                var leftRows = new (string Label, string Value)[]
                {
                    ("Request Type", DescribeRequestType(mir.RequestType)),
                    ("Priority",     Capitalize(mir.Priority)),
                };
                foreach (var (label, value) in leftRows)
                {
                    col.Item().PaddingBottom(2).Row(r =>
                    {
                        r.ConstantItem(85).Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
                        r.RelativeItem().Text(value).FontSize(8.5f).Bold();
                    });
                }
            });

            row.RelativeItem(1.4f).Column(col =>
            {
                var rightRows = new (string Label, string Value)[]
                {
                    ("Status", DescribeStatus(mir.Status)),
                    ("Estimated Value", mir.EstimatedValue.ToString("N2")),
                };
                foreach (var (label, value) in rightRows)
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

    private static string DescribeRequestType(string t) => t switch
    {
        "PROJECT"     => "Project",
        "DEPARTMENT"  => "Department",
        "MAINTENANCE" => "Maintenance",
        _             => t
    };

    private static string DescribeStatus(string s) => s.Replace('_', ' ');

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private static void ComposeLineItemsTable(ColumnDescriptor column, MirDetailModel mir)
    {
        column.Item().PaddingBottom(10).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(26);
                columns.RelativeColumn(3);
                columns.RelativeColumn(1);
                columns.ConstantColumn(55);
                columns.ConstantColumn(75);
                columns.ConstantColumn(80);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("#");
                header.Cell().Element(HeaderCell).Text("Item Description");
                header.Cell().Element(HeaderCell).Text("UOM");
                header.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                header.Cell().Element(HeaderCell).AlignRight().Text("Unit Cost");
                header.Cell().Element(HeaderCell).AlignRight().Text("Line Value");

                static IContainer HeaderCell(IContainer c) =>
                    c.Background(BrandColor)
                     .DefaultTextStyle(x => x.Bold().FontSize(8.5f).FontColor(Colors.White))
                     .PaddingVertical(8).PaddingHorizontal(4);
            });

            var lines = mir.Lines;
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var isLast = i == lines.Count - 1;
                var isEven = i % 2 == 1;

                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.LineNo.ToString());
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.ItemDescription);
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.UnitOfMeasure ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.RequestedQty.ToString("N2"));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.UnitCost.ToString("N2"));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.EstimatedLineValue.ToString("N2"));

                static IContainer BodyCell(IContainer c, bool isLast, bool isEven) =>
                    c.Background(isEven ? BrandColorTint : Colors.White)
                     .PaddingVertical(6).PaddingHorizontal(4)
                     .BorderBottom(isLast ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
            }

            table.Cell().ColumnSpan(5).Element(c => c.PaddingTop(8).PaddingRight(4).AlignRight()
                .Text("Estimated Total").FontSize(9.5f).Bold());
            table.Cell().Element(c => c.PaddingTop(8).BorderTop(1.5f).BorderColor(BrandColor).PaddingHorizontal(4))
                .AlignRight().Text(mir.EstimatedValue.ToString("N2")).FontSize(9.5f).Bold();
        });
    }

    private static void ComposeTotal(ColumnDescriptor column, MirDetailModel mir)
    {
        column.Item().PaddingBottom(14).AlignRight().Width(220).Row(row =>
        {
            row.RelativeItem().Text("Estimated Total").FontSize(11).Bold();
            row.ConstantItem(100).AlignRight().Text(mir.EstimatedValue.ToString("N2"))
                .FontSize(12).Bold().FontColor(BrandColorDark);
        });
    }

    private static void ComposeNotes(ColumnDescriptor column, MirDetailModel mir)
    {
        column.Item().PaddingBottom(6).Text("NOTES").FontSize(9).Bold().FontColor(BrandColorDark);
        column.Item().PaddingBottom(14).Background(BrandColorTint).Padding(8).Text(mir.Notes).FontSize(8.5f);
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
