using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Demand.Models;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Exceptions;
using HtmlElement = AngleSharp.Dom.IElement;

namespace SMS.Modules.Demand.Services;

internal sealed class PoDocumentService : IPoDocumentService
{
    private readonly IPurchaseOrderService _poService;
    private readonly IPoDocumentTemplateService _templateService;
    private readonly IWebHostEnvironment _env;

    // Structural markers the template author inserts via the "Insert Variable" picker — these
    // aren't simple text substitutions, they mark where the QuestPDF-composed line-items table /
    // signature block should be spliced into the free-text body.
    private const string LineItemsMarker  = "{{LineItemsTable}}";
    private const string SignatureMarker  = "{{SignatureBlock}}";

    public PoDocumentService(IPurchaseOrderService poService, IPoDocumentTemplateService templateService, IWebHostEnvironment env)
    {
        _poService = poService;
        _templateService = templateService;
        _env = env;
    }

    public async Task<byte[]> GeneratePdfAsync(Guid poUuid)
    {
        var po = await _poService.GetByIdAsync(poUuid)
            ?? throw new NotFoundException("Purchase order not found");
        var template = await _templateService.GetActiveAsync();

        var tokens = BuildTokenValues(po, template);
        var bodyWithTokens = ReplaceSimpleTokens(template?.BodyHtml ?? "", tokens);
        var segments = await ParseBodySegmentsAsync(bodyWithTokens);
        var logoBytes = TryLoadLogoBytes(template?.CompanyLogoUrl);

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Element(c => ComposeHeader(c, template, logoBytes));
                page.Content().Element(c => ComposeContent(c, template, po, segments));
                page.Footer().Element(c => ComposeFooter(c, template));
            });
        });

        return document.GeneratePdf();
    }

    // The logo is stored as a web-relative URL (e.g. "/uploads/attachments/po-template-logo/x.png")
    // by the generic attachment upload endpoint — resolve it straight off disk rather than making
    // an HTTP round-trip back into the same API.
    private byte[]? TryLoadLogoBytes(string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl)) return null;

        var relative = logoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var webRoot  = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var fullPath = Path.Combine(webRoot, relative);

        return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }

    // ── Token resolution ───────────────────────────────────────────────────────

    private static Dictionary<string, string> BuildTokenValues(PoDetailModel po, PoDocumentTemplateModel? template)
    {
        return new Dictionary<string, string>
        {
            ["{{PoNumber}}"]         = po.PoNumber ?? po.UUID.ToString(),
            ["{{PoTitle}}"]          = po.Title ?? "-",
            ["{{PoStatus}}"]         = po.Status ?? "-",
            ["{{PoDate}}"]           = po.CreatedDate.ToString("dd MMM yyyy"),
            ["{{DeliveryDate}}"]     = po.DeliveryDate?.ToString("dd MMM yyyy") ?? "-",
            ["{{DeliveryWarehouse}}"]= po.DeliveryWarehouseName ?? "-",
            ["{{BudgetCode}}"]       = po.BudgetCode ?? "-",
            ["{{Notes}}"]            = po.Notes ?? "",
            ["{{TotalAmount}}"]      = po.TotalAmount.ToString("N2"),
            ["{{SupplierName}}"]     = po.SupplierName ?? "-",
            ["{{SupplierMobile}}"]   = po.SupplierContactMobile ?? "-",
            ["{{CompanyName}}"]      = template?.CompanyName ?? "Company Name",
            ["{{CompanyAddress}}"]   = template?.CompanyAddress ?? "",
            ["{{CompanyPhone}}"]     = template?.CompanyPhone ?? "",
            ["{{CompanyEmail}}"]     = template?.CompanyEmail ?? "",
            ["{{GeneratedDate}}"]    = DateTime.Now.ToString("dd MMM yyyy HH:mm"),
        };
    }

    private static string ReplaceSimpleTokens(string bodyHtml, Dictionary<string, string> tokens)
    {
        var result = bodyHtml;
        foreach (var (token, value) in tokens)
            result = result.Replace(token, System.Net.WebUtility.HtmlEncode(value));
        return result;
    }

    // ── Body parsing: split into ordered segments around the two structural markers ──────────

    private enum SegmentKind { Html, LineItemsTable, SignatureBlock }
    private sealed record BodySegment(SegmentKind Kind, HtmlElement? ParsedHtml);

    private static async Task<List<BodySegment>> ParseBodySegmentsAsync(string bodyWithTokens)
    {
        var parts = Regex.Split(bodyWithTokens, $"({Regex.Escape(LineItemsMarker)}|{Regex.Escape(SignatureMarker)})");
        var context = BrowsingContext.New(Configuration.Default);
        var segments = new List<BodySegment>();

        foreach (var part in parts)
        {
            if (part == LineItemsMarker) { segments.Add(new BodySegment(SegmentKind.LineItemsTable, null)); continue; }
            if (part == SignatureMarker) { segments.Add(new BodySegment(SegmentKind.SignatureBlock, null)); continue; }
            if (string.IsNullOrWhiteSpace(part)) continue;

            var doc = await context.OpenAsync(req => req.Content($"<body>{part}</body>"));
            segments.Add(new BodySegment(SegmentKind.Html, doc.Body));
        }

        return segments;
    }

    // ── Composition ────────────────────────────────────────────────────────────

    // Letterhead only — the "To" section, subject, greeting, and everything else is entirely
    // user-designed in the template body (via {{SupplierName}}, {{SupplierMobile}}, etc.), not
    // auto-generated here.
    private static void ComposeHeader(IContainer container, PoDocumentTemplateModel? template, byte[]? logoBytes)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text(template?.CompanyName ?? "Company Name").FontSize(16).Bold();
                    if (!string.IsNullOrWhiteSpace(template?.CompanyAddress))
                        col.Item().Text(template.CompanyAddress).FontSize(8);
                    var contactLine = string.Join("  |  ", new[]
                    {
                        !string.IsNullOrWhiteSpace(template?.CompanyPhone) ? $"Tel: {template.CompanyPhone}" : null,
                        !string.IsNullOrWhiteSpace(template?.CompanyEmail) ? $"Email: {template.CompanyEmail}" : null,
                        !string.IsNullOrWhiteSpace(template?.CompanyTaxId) ? $"Tax ID: {template.CompanyTaxId}" : null,
                    }.Where(s => s is not null));
                    if (!string.IsNullOrWhiteSpace(contactLine))
                        col.Item().Text(contactLine).FontSize(8);
                });

                if (logoBytes is not null)
                    row.ConstantItem(80).Height(50).Image(logoBytes).FitArea();
            });

            column.Item().PaddingTop(10).Text("PURCHASE ORDER").FontSize(14).Bold().AlignCenter();
            column.Item().PaddingBottom(5).LineHorizontal(1);
        });
    }

    private static void ComposeContent(
        IContainer container, PoDocumentTemplateModel? template, PoDetailModel po, List<BodySegment> segments)
    {
        container.Column(column =>
        {
            foreach (var segment in segments)
            {
                switch (segment.Kind)
                {
                    case SegmentKind.Html:
                        if (segment.ParsedHtml is not null)
                            ComposeHtmlBody(column, segment.ParsedHtml);
                        break;
                    case SegmentKind.LineItemsTable:
                        ComposeLineItemsTable(column, po);
                        break;
                    case SegmentKind.SignatureBlock:
                        ComposeSignatureBlock(column, template);
                        break;
                }
            }
        });
    }

    private static void ComposeLineItemsTable(ColumnDescriptor column, PoDetailModel po)
    {
        column.Item().PaddingTop(10).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(20);
                columns.RelativeColumn(3);
                columns.RelativeColumn(1.2f);
                columns.ConstantColumn(50);
                columns.ConstantColumn(60);
                columns.ConstantColumn(70);
                columns.ConstantColumn(70);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("#");
                header.Cell().Element(HeaderCell).Text("Item");
                header.Cell().Element(HeaderCell).Text("Spec");
                header.Cell().Element(HeaderCell).Text("UOM");
                header.Cell().Element(HeaderCell).AlignRight().Text("Qty");
                header.Cell().Element(HeaderCell).AlignRight().Text("Unit Price");
                header.Cell().Element(HeaderCell).AlignRight().Text("Line Total");

                static IContainer HeaderCell(IContainer c) =>
                    c.DefaultTextStyle(x => x.Bold()).PaddingVertical(4).BorderBottom(1);
            });

            foreach (var line in po.Lines ?? new List<PoLineModel>())
            {
                table.Cell().Element(BodyCell).Text(line.LineNo.ToString());
                table.Cell().Element(BodyCell).Text(line.ItemDescription ?? "-");
                table.Cell().Element(BodyCell).Text(line.Specification ?? "-");
                table.Cell().Element(BodyCell).Text(line.UnitOfMeasure ?? "-");
                table.Cell().Element(BodyCell).AlignRight().Text(line.Quantity.ToString("N2"));
                table.Cell().Element(BodyCell).AlignRight().Text(line.UnitPrice.ToString("N2"));
                table.Cell().Element(BodyCell).AlignRight().Text(line.LineTotal.ToString("N2"));

                static IContainer BodyCell(IContainer c) =>
                    c.PaddingVertical(3).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);
            }
        });
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

        column.Item().PaddingTop(40).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.5f);
                col.Item().PaddingTop(3).Text(template?.PreparedByLabel ?? "Prepared By").FontSize(8);
            });
            row.ConstantItem(20);
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.5f);
                col.Item().PaddingTop(3).Text(template?.ApprovedByLabel ?? "Approved By").FontSize(8);
            });
            row.ConstantItem(20);
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.5f);
                col.Item().PaddingTop(3).Text(template?.AuthorizedSignatoryLabel ?? "Authorized Signatory").FontSize(8);
            });
        });
    }

    private static void ComposeFooter(IContainer container, PoDocumentTemplateModel? template)
    {
        container.Column(column =>
        {
            if (!string.IsNullOrWhiteSpace(template?.FooterText))
                column.Item().AlignCenter().Text(template.FooterText).FontSize(7).FontColor(Colors.Grey.Medium);

            column.Item().AlignCenter().Text(text =>
            {
                text.Span("Generated ").FontSize(7).FontColor(Colors.Grey.Medium);
                text.Span(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).FontSize(7).FontColor(Colors.Grey.Medium);
            });
        });
    }

    // ── Minimal HTML-fragment renderer ──────────────────────────────────────────
    // Supports exactly the tags the frontend's rich-text editor can produce: p, div, br,
    // b/strong, i/em, u, ul/li. Anything else is treated as plain inline text.

    private static void ComposeHtmlBody(ColumnDescriptor column, HtmlElement body)
    {
        foreach (var node in body.ChildNodes)
            ComposeBlockNode(column, node);
    }

    private static void ComposeBlockNode(ColumnDescriptor column, INode node)
    {
        if (node is HtmlElement el)
        {
            switch (el.TagName)
            {
                case "P":
                case "DIV":
                {
                    var runs = new List<TextRun>();
                    CollectRuns(el, false, false, false, runs);
                    if (runs.Count > 0)
                        column.Item().PaddingBottom(6).Text(t => { foreach (var r in runs) ApplyRun(t.Span(r.Text), r); });
                    return;
                }
                case "UL":
                {
                    foreach (var li in el.Children.Where(c => c.TagName == "LI"))
                    {
                        var runs = new List<TextRun>();
                        CollectRuns(li, false, false, false, runs);
                        column.Item().PaddingBottom(3).PaddingLeft(10).Text(t =>
                        {
                            t.Span("•  ");
                            foreach (var r in runs) ApplyRun(t.Span(r.Text), r);
                        });
                    }
                    return;
                }
                case "BR":
                    column.Item().Height(4);
                    return;
            }
        }

        if (node is IText text && !string.IsNullOrWhiteSpace(text.TextContent))
            column.Item().PaddingBottom(6).Text(text.TextContent.Trim());
    }

    private sealed record TextRun(string Text, bool Bold, bool Italic, bool Underline);

    private static void CollectRuns(INode node, bool bold, bool italic, bool underline, List<TextRun> runs)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText t)
            {
                if (!string.IsNullOrEmpty(t.TextContent))
                    runs.Add(new TextRun(t.TextContent, bold, italic, underline));
            }
            else if (child is HtmlElement el)
            {
                if (el.TagName == "BR") { runs.Add(new TextRun("\n", bold, italic, underline)); continue; }

                // Chrome's execCommand doesn't reliably emit <b>/<i>/<u> — depending on
                // styleWithCSS state it can wrap the selection in <span style="font-weight: bold">
                // etc. instead. Check both so formatting applied in the editor always survives.
                var style = el.GetAttribute("style") ?? "";
                var b = bold || el.TagName is "B" or "STRONG"
                             || style.Contains("bold", StringComparison.OrdinalIgnoreCase)
                             || style.Contains("font-weight: 700", StringComparison.OrdinalIgnoreCase);
                var i = italic || el.TagName is "I" or "EM"
                               || style.Contains("italic", StringComparison.OrdinalIgnoreCase);
                var u = underline || el.TagName == "U"
                                   || style.Contains("underline", StringComparison.OrdinalIgnoreCase);
                CollectRuns(el, b, i, u, runs);
            }
        }
    }

    private static void ApplyRun(TextSpanDescriptor span, TextRun run)
    {
        if (run.Bold) span.Bold();
        if (run.Italic) span.Italic();
        if (run.Underline) span.Underline();
    }
}
