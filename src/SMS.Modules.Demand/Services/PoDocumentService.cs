using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services.Documents;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;
using HtmlElement = AngleSharp.Dom.IElement;

namespace SMS.Modules.Demand.Services;

internal sealed class PoDocumentService : IPoDocumentService
{
    private readonly IPurchaseOrderService _poService;
    private readonly IPoDocumentTemplateService _templateService;
    private readonly ISupplierContactLookupService _supplierContactService;
    private readonly IWebHostEnvironment _env;

    // Structural markers the template author inserts via the "Insert Variable" picker — these
    // aren't simple text substitutions, they mark where the QuestPDF-composed line-items table /
    // signature block should be spliced into the free-text body.
    private const string LineItemsMarker  = "{{LineItemsTable}}";
    private const string SignatureMarker  = "{{SignatureBlock}}";

    // Brand accent used across the app's UI (dialogs, buttons, icon chips) — reused here so the
    // generated PDF reads as the same product rather than a generic black-and-white document.
    // A50 — the house palette is DocumentPalette.House (#6C63FF / #4A42CC / #F4F3FF); a PurchaseOrder template may replace it.

    public PoDocumentService(
        IPurchaseOrderService poService, IPoDocumentTemplateService templateService,
        ISupplierContactLookupService supplierContactService, IWebHostEnvironment env, TimeProvider? clock = null,
        IDocumentBrandingProvider? branding = null, ITemplatedDocumentRenderer? templated = null)
    {
        _poService = poService;
        _templateService = templateService;
        _supplierContactService = supplierContactService;
        _env = env;
        _clock = clock ?? TimeProvider.System;
        _branding = branding ?? new NullDocumentBrandingProvider();
        _templated = templated ?? new NullTemplatedDocumentRenderer();
    }

    private readonly TimeProvider _clock;
    private readonly IDocumentBrandingProvider _branding;
    private readonly ITemplatedDocumentRenderer _templated;

    /// <summary>
    /// A50 (D-25) — the org's PurchaseOrder document template wins over the pre-A50 PoDocumentTemplate body: its BodyHtml
    /// through the templated renderer, else this letter layout with the template's colour, logo, header, footer and terms.
    /// No template = the pre-A50 PDF, byte for byte. Every render goes through PdfRenderGate.
    /// </summary>
    public async Task<byte[]> GeneratePdfAsync(Guid poUuid)
    {
        var po = await _poService.GetByIdAsync(poUuid)
            ?? throw new NotFoundException("Purchase order not found");
        var template = await _templateService.GetActiveAsync();
        var supplierContact = await _supplierContactService.GetContactInfoAsync(po.SupplierId);
        var brand = await _branding.GetAsync(DocumentTemplateTypes.PurchaseOrder);
        if (brand is not null)
            brand = BrandedDocumentKit.WithLetterhead(brand, template?.CompanyName, template?.CompanyAddress, template?.CompanyPhone,
                template?.CompanyEmail, template?.CompanyTaxId);

        var now = _clock.GetLocalNow().DateTime;
        var tokens = BuildTokenValues(po, template, now);

        if (brand is not null && !string.IsNullOrWhiteSpace(brand.BodyHtml)
            && _templated.Render(brand, Templated(po, template, supplierContact, tokens, now)) is { } bodyPdf)
            return bodyPdf;

        var bodyWithTokens = ReplaceSimpleTokens(template?.BodyHtml ?? "", tokens);
        var segments = await ParseBodySegmentsAsync(bodyWithTokens);
        var logoBytes = brand?.LogoBytes ?? TryLoadLogoBytes(template?.CompanyLogoUrl);
        var p = DocumentPalette.Resolve(brand, DocumentPalette.House);

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));

                page.Header().Element(c => ComposeHeader(c, template, po, supplierContact, logoBytes, p, brand));
                page.Content().Element(c => ComposeContent(c, template, po, segments, p, brand));
                page.Footer().Element(c => ComposeFooter(c, template, now, brand, po));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    /// <summary>A50 — the PO for a BodyHtml template: the pre-A50 tokens without braces, plus the common ones.</summary>
    internal static TemplatedDocument Templated(
        PoDetailModel po, PoDocumentTemplateModel? template, SupplierContactInfo? supplier, Dictionary<string, string> tokens, DateTime now)
    {
        var values = tokens.ToDictionary(kv => kv.Key.Trim('{', '}'), kv => (string?)kv.Value, StringComparer.OrdinalIgnoreCase);
        values["DocumentNumber"] = po.PoNumber;
        values["DocumentDate"] = po.CreatedDate.ToString("dd MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        values["SupplierAddress"] = supplier?.Address ?? "";
        values["SupplierPhone"] = supplier?.Phone ?? "";
        values["Status"] = po.Status ?? "";
        values["CurrencyCode"] = po.CurrencyCode ?? "";
        values["AmountInWords"] = AmountInWords.Format(po.TotalAmount, po.CurrencyCode);
        values["PartyLabel"] = "To:";
        values["PartyName"] = po.SupplierName ?? "-";
        values["PartyAddress"] = supplier?.Address ?? "";
        if (po.DeliveryDate is { } due) values["HeaderFacts"] = $"Delivery: {due:dd MMM yyyy}";

        var doc = new TemplatedDocument
        {
            TemplateType = DocumentTemplateTypes.PurchaseOrder, Title = "PURCHASE ORDER", Values = values, GeneratedAt = now,
            SignatureDisclaimer = template?.ShowSignatureBlock == false
                ? template.SignatureDisclaimer ?? "This is a system generated document and does not require a signature." : null,
        };
        if (template?.ShowSignatureBlock != false)
            doc.SignatureLabels.AddRange([template?.PreparedByLabel ?? "Prepared By", template?.ApprovedByLabel ?? "Approved By",
                                          template?.AuthorizedSignatoryLabel ?? "Authorized Signatory"]);
        doc.LineColumns.AddRange([new("#", 0.4f), new("Item", 3f), new("Spec", 1.3f), new("UOM", 0.7f), new("Qty", 0.9f, true),
                                  new("Unit Price", 1.1f, true), new("Line Total", 1.2f, true)]);
        foreach (var l in po.Lines ?? [])
            doc.LineRows.Add([l.LineNo.ToString(), l.ItemDescription ?? "-", l.Specification ?? "-", l.UnitOfMeasure ?? "-",
                              l.Quantity.ToString("N2"), l.UnitPrice.ToString("N2"), l.LineTotal.ToString("N2")]);
        doc.Totals.Add(new("Grand Total", po.TotalAmount.ToString("N2") + (string.IsNullOrWhiteSpace(po.CurrencyCode) ? "" : " " + po.CurrencyCode), true));
        return doc;
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

    private static Dictionary<string, string> BuildTokenValues(PoDetailModel po, PoDocumentTemplateModel? template, DateTime now)
    {
        return new Dictionary<string, string>
        {
            ["{{PoNumber}}"]         = po.PoNumber ?? po.UUID.ToString(),
            ["{{PoTitle}}"]          = po.Title ?? "-",
            ["{{PoStatus}}"]         = po.Status ?? "-",
            ["{{PoDate}}"]           = po.CreatedDate.ToString("dd MMM yyyy"),
            ["{{DeliveryDate}}"]     = po.DeliveryDate?.ToString("dd MMM yyyy") ?? "-",
            ["{{DeliveryWarehouse}}"]= po.DeliveryWarehouseName ?? "-",
            // Budget code is a per-line attribute (each line can carry a different code) — show
            // the distinct set here rather than a single value.
            ["{{BudgetCode}}"]       = BuildBudgetCodeSummary(po),
            ["{{Notes}}"]            = po.Notes ?? "",
            ["{{TotalAmount}}"]      = po.TotalAmount.ToString("N2"),
            ["{{SupplierName}}"]     = po.SupplierName ?? "-",
            ["{{SupplierMobile}}"]   = po.SupplierContactMobile ?? "-",
            ["{{CompanyName}}"]      = template?.CompanyName ?? "Company Name",
            ["{{CompanyAddress}}"]   = template?.CompanyAddress ?? "",
            ["{{CompanyPhone}}"]     = template?.CompanyPhone ?? "",
            ["{{CompanyEmail}}"]     = template?.CompanyEmail ?? "",
            ["{{GeneratedDate}}"]    = now.ToString("dd MMM yyyy HH:mm"),
        };
    }

    private static string BuildBudgetCodeSummary(PoDetailModel po)
    {
        var codes = (po.Lines ?? new List<PoLineModel>())
            .Select(l => l.BudgetCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct()
            .ToList();
        return codes.Count > 0 ? string.Join(", ", codes) : "-";
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

    // Letterhead + a fixed "Dated / To" block — these are structural, bound directly to PO data,
    // so they can never end up "missing" the way free-typed body text could be. The narrative
    // (subject, greeting, body paragraphs) is entirely user-designed in the template body below.
    private static void ComposeHeader(
        IContainer container, PoDocumentTemplateModel? template, PoDetailModel po,
        SupplierContactInfo? supplierContact, byte[]? logoBytes, DocumentPalette p, DocumentBranding? brand)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                var letterhead = row.RelativeItem();
                var branded = brand is not null && BrandedDocumentKit.TryComposeHeader(letterhead, brand,
                    BrandedDocumentKit.HeaderValues(brand, "PURCHASE ORDER", po.PoNumber, po.CreatedDate), p.Dark);

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

                if (logoBytes is not null)
                    row.ConstantItem(80).Height(50).Image(logoBytes).FitArea();
            });

            column.Item().PaddingTop(16).Text("PURCHASE ORDER").FontSize(15).Bold().AlignCenter();

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("To:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(1).Text(po.SupplierName ?? "-").FontSize(10.5f).Bold();
                    if (!string.IsNullOrWhiteSpace(supplierContact?.Address))
                        col.Item().PaddingTop(1).Text(supplierContact.Address).FontSize(8.5f).FontColor(Colors.Grey.Darken1);

                    var supplierContactLine = string.Join("   |   ", new[]
                    {
                        !string.IsNullOrWhiteSpace(supplierContact?.Phone) ? $"Tel: {supplierContact.Phone}" : null,
                        !string.IsNullOrWhiteSpace(po.SupplierContactMobile) && po.SupplierContactMobile != supplierContact?.Phone
                            ? $"Mobile: {po.SupplierContactMobile}" : null,
                    }.Where(s => s is not null));
                    if (!string.IsNullOrWhiteSpace(supplierContactLine))
                        col.Item().PaddingTop(1).Text(supplierContactLine).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(140).Column(col =>
                {
                    col.Item().AlignRight().Text($"PO No: {po.PoNumber}").FontSize(9).Bold();
                    col.Item().PaddingTop(2).AlignRight().Text($"Dated: {po.CreatedDate:dd MMM yyyy}").FontSize(9);
                });
            });

            column.Item().PaddingTop(14).LineHorizontal(1.5f).LineColor(p.Primary);
        });
    }

    private static void ComposeContent(
        IContainer container, PoDocumentTemplateModel? template, PoDetailModel po, List<BodySegment> segments, DocumentPalette p, DocumentBranding? brand)
    {
        container.PaddingTop(14).Column(column =>
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
                        ComposeLineItemsTable(column, po, p);
                        break;
                    case SegmentKind.SignatureBlock:
                        ComposeSignatureBlock(column, template);
                        break;
                }
            }

            if (brand is not null)
                BrandedDocumentKit.ComposeTerms(column, brand, p.Dark);
        });
    }

    private static void ComposeLineItemsTable(ColumnDescriptor column, PoDetailModel po, DocumentPalette p)
    {
        column.Item().PaddingTop(6).PaddingBottom(10).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(26);
                columns.RelativeColumn(3);
                columns.RelativeColumn(1.3f);
                columns.ConstantColumn(46);
                columns.ConstantColumn(60);
                columns.ConstantColumn(75);
                columns.ConstantColumn(80);
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

                IContainer HeaderCell(IContainer c) =>
                    c.Background(p.Primary)
                     .DefaultTextStyle(x => x.Bold().FontSize(8.5f).FontColor(Colors.White))
                     .PaddingVertical(8).PaddingHorizontal(4);
            });

            var lines = po.Lines ?? new List<PoLineModel>();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var isLast = i == lines.Count - 1;
                var isEven = i % 2 == 1;

                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.LineNo.ToString());
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.ItemDescription ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.Specification ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(line.UnitOfMeasure ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.Quantity.ToString("N2"));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.UnitPrice.ToString("N2"));
                table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight().Text(line.LineTotal.ToString("N2"));

                IContainer BodyCell(IContainer c, bool isLast, bool isEven) =>
                    c.Background(isEven ? p.Tint : Colors.White)
                     .PaddingVertical(6).PaddingHorizontal(4)
                     .BorderBottom(isLast ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
            }

            // Grand Total row — right-aligned under the Line Total column, not a free-typed
            // paragraph, so it always lines up with the table regardless of template body content.
            table.Cell().ColumnSpan(6).Element(c => c.PaddingTop(8).PaddingRight(4).AlignRight()
                .Text("Grand Total").FontSize(9.5f).Bold());
            table.Cell().Element(c => c.PaddingTop(8).BorderTop(1.5f).BorderColor(p.Primary).PaddingTop(8).PaddingHorizontal(4))
                .AlignRight().Text(po.TotalAmount.ToString("N2")).FontSize(10.5f).Bold().FontColor(p.Dark);
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

    private static void ComposeFooter(IContainer container, PoDocumentTemplateModel? template, DateTime now, DocumentBranding? brand, PoDetailModel po)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

            var branded = brand is not null && BrandedDocumentKit.TryComposeFooter(column, brand,
                BrandedDocumentKit.HeaderValues(brand, "PURCHASE ORDER", po.PoNumber, po.CreatedDate));

            if (!branded && !string.IsNullOrWhiteSpace(template?.FooterText))
                column.Item().AlignCenter().Text(template.FooterText).FontSize(7).FontColor(Colors.Grey.Medium);

            column.Item().PaddingTop(2).AlignCenter().Text(text =>
            {
                text.Span("Generated ").FontSize(7).FontColor(Colors.Grey.Medium);
                text.Span(now.ToString("yyyy-MM-dd HH:mm")).FontSize(7).FontColor(Colors.Grey.Medium);
            });
        });
    }

    // ── Minimal HTML-fragment renderer ──────────────────────────────────────────
    // Supports exactly the tags the frontend's rich-text editor can produce: p, div, br,
    // b/strong, i/em, u, ul/li, plus text-align (left/center/right) via inline style.

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
                    {
                        var alignment = GetAlignment(el);
                        var item = column.Item().PaddingBottom(8);
                        var textContainer = alignment switch
                        {
                            TextAlignment.Center => item.AlignCenter(),
                            TextAlignment.Right  => item.AlignRight(),
                            _                    => (IContainer)item
                        };
                        textContainer.Text(t => { foreach (var r in runs) ApplyRun(t.Span(r.Text), r); });
                    }
                    return;
                }
                case "UL":
                {
                    foreach (var li in el.Children.Where(c => c.TagName == "LI"))
                    {
                        var runs = new List<TextRun>();
                        CollectRuns(li, false, false, false, runs);
                        column.Item().PaddingBottom(4).PaddingLeft(12).Text(t =>
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
            column.Item().PaddingBottom(8).Text(text.TextContent.Trim());
    }

    private enum TextAlignment { Left, Center, Right }

    private static TextAlignment GetAlignment(HtmlElement el)
    {
        var style = el.GetAttribute("style") ?? "";
        if (style.Contains("text-align: right", StringComparison.OrdinalIgnoreCase) ||
            style.Contains("text-align:right", StringComparison.OrdinalIgnoreCase))
            return TextAlignment.Right;
        if (style.Contains("text-align: center", StringComparison.OrdinalIgnoreCase) ||
            style.Contains("text-align:center", StringComparison.OrdinalIgnoreCase))
            return TextAlignment.Center;
        return TextAlignment.Left;
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
