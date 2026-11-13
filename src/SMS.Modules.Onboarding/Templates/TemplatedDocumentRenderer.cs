using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;
using IElement = AngleSharp.Dom.IElement;

namespace SMS.Modules.Onboarding.Templates;

/// <summary>
/// A50 TPL (D-25, D-67) — renders a document template's BodyHtml with its {Placeholders} into a PDF. Generalised from the PO
/// letter path (PoDocumentService): the same AngleSharp parse of the rich-text editor's HTML (p, div, br, b/strong, i/em, u,
/// ul/ol/li, h1–h4, text-align), with structural tokens spliced in as QuestPDF blocks: {LineItems}, {TaxSummary}, {Totals},
/// {BankDetails}, {TermsAndConditions}, {SignatureBlock}. The letterhead (template header or company block + logo), title,
/// party / number block and footer are structural too, so they can never go missing from a typed body.
/// Values are HTML-encoded before the parse: a customer named "&lt;b&gt;x" prints as typed. Rendered through PdfRenderGate.
/// </summary>
internal sealed class TemplatedDocumentRenderer : ITemplatedDocumentRenderer
{
    internal static readonly string[] StructuralTokens = ["LineItems", "TaxSummary", "Totals", "BankDetails", "TermsAndConditions", "SignatureBlock"];
    private static readonly Regex Structural = new(@"\{(" + string.Join("|", StructuralTokens) + @")\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private const string HousePrimary = "#6C63FF";

    /// <summary>Used for the preview of a template without a body: the blocks the built-in layouts print, in their order.</summary>
    internal const string DefaultBody = "{LineItems}{TaxSummary}{Totals}<p>{Notes}</p>{BankDetails}{TermsAndConditions}";

    static TemplatedDocumentRenderer() => QuestPDF.Settings.License ??= LicenseType.Community;

    public byte[]? Render(DocumentBranding branding, TemplatedDocument document) => RenderBody(branding, document, branding.BodyHtml);

    internal byte[] RenderBody(DocumentBranding branding, TemplatedDocument doc, string? bodyHtml)
    {
        ArgumentNullException.ThrowIfNull(branding);
        ArgumentNullException.ThrowIfNull(doc);

        var primary = BrandPalette.IsValid(branding.PrimaryColor) ? branding.PrimaryColor!.ToUpperInvariant() : HousePrimary;
        var p = new Palette(primary, BrandPalette.Darken(primary), BrandPalette.Tint(primary));
        var values = Values(branding, doc);
        var segments = Segments(string.IsNullOrWhiteSpace(bodyHtml) ? DefaultBody : bodyHtml!, values);

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));
                page.Header().Element(c => Header(c, branding, doc, values, p));
                page.Content().Element(c => Content(c, branding, doc, segments, p));
                page.Footer().Element(c => Footer(c, branding, doc, values));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    private sealed record Palette(string Primary, string Dark, string Tint);

    /// <summary>The document's values plus the company ones from the branding and the date of printing.</summary>
    internal static Dictionary<string, string?> Values(DocumentBranding branding, TemplatedDocument doc)
    {
        var v = new Dictionary<string, string?>(doc.Values, StringComparer.OrdinalIgnoreCase);
        void Default(string key, string? value) { if (!v.TryGetValue(key, out var have) || string.IsNullOrEmpty(have)) v[key] = value ?? string.Empty; }
        Default("CompanyName", branding.CompanyName);
        Default("CompanyAddress", branding.CompanyAddress);
        Default("CompanyPhone", branding.CompanyPhone);
        Default("CompanyEmail", branding.CompanyEmail);
        Default("CompanyTaxId", branding.CompanyTaxId);
        Default("DocumentTitle", doc.Title);
        Default("GeneratedDate", doc.GeneratedAt.ToString("dd MMM yyyy HH:mm", Culture));
        Default("BankDetails", branding.ShowBankDetails ? branding.BankDetailsText : string.Empty);
        Default("TermsAndConditions", branding.TermsAndConditions);
        Default("PageNumber", string.Empty);
        return v;
    }

    // ── Body parsing ─────────────────────────────────────────────────────────────

    private enum Kind { Html, Block }
    private sealed record Segment(Kind Kind, IElement? Html, string? Block);

    private static List<Segment> Segments(string bodyHtml, IReadOnlyDictionary<string, string?> values)
    {
        // Structural tokens first (they are blocks, not text), then the simple ones (HTML-encoded) in what is left.
        var parts = Structural.Split(bodyHtml);
        var context = BrowsingContext.New(Configuration.Default);
        var segments = new List<Segment>();
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            // Regex.Split with one capture group: odd indexes are the captured token names.
            if (i % 2 == 1) { segments.Add(new Segment(Kind.Block, null, part)); continue; }
            if (string.IsNullOrWhiteSpace(part)) continue;
            var html = TemplateText.Render(part, values, html: true);
            var parsed = context.OpenAsync(req => req.Content($"<body>{html}</body>")).GetAwaiter().GetResult();
            if (parsed.Body is not null) segments.Add(new Segment(Kind.Html, parsed.Body, null));
        }
        return segments;
    }

    // ── Composition ──────────────────────────────────────────────────────────────

    private static void Header(IContainer container, DocumentBranding b, TemplatedDocument doc, IReadOnlyDictionary<string, string?> values, Palette p)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    var header = HtmlLite.ToColumns(TemplateText.Render(b.HeaderHtml, values, html: true));
                    if (header.Count > 0)
                    {
                        col.Item().Row(r =>
                        {
                            for (var i = 0; i < header.Count; i++)
                            {
                                var lines = header[i];
                                var first = i == 0;
                                r.RelativeItem().Column(c =>
                                {
                                    for (var j = 0; j < lines.Count; j++)
                                        Line(c.Item().PaddingTop(j == 0 ? 0 : 2), lines[j], first && j == 0 ? 16f : 8f, first && j == 0 ? p.Dark : null);
                                });
                            }
                        });
                        return;
                    }

                    col.Item().Text(Get(values, "CompanyName", "Company Name")).FontSize(16).Bold().FontColor(p.Dark);
                    if (!string.IsNullOrWhiteSpace(b.CompanyAddress)) col.Item().PaddingTop(2).Text(b.CompanyAddress).FontSize(8);
                    var contact = string.Join("   |   ", new[]
                    {
                        string.IsNullOrWhiteSpace(b.CompanyPhone) ? null : $"Tel: {b.CompanyPhone}",
                        string.IsNullOrWhiteSpace(b.CompanyEmail) ? null : $"Email: {b.CompanyEmail}",
                        string.IsNullOrWhiteSpace(b.CompanyTaxId) ? null : $"Tax ID: {b.CompanyTaxId}",
                    }.Where(s => s is not null));
                    if (contact.Length > 0) col.Item().PaddingTop(2).Text(contact).FontSize(8);
                });

                if (b.LogoBytes is { Length: > 0 } logo)
                    row.ConstantItem(80).Height(50).Image(logo).FitArea();
            });

            column.Item().PaddingTop(16).Text(doc.Title).FontSize(15).Bold().AlignCenter();

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text(Get(values, "PartyLabel", "To:")).FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(1).Text(Get(values, "PartyName", "-")).FontSize(10.5f).Bold();
                    foreach (var line in TemplateText.ToPlainLines(Get(values, "PartyAddress", "")))
                        col.Item().PaddingTop(1).Text(line).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                });
                row.ConstantItem(170).Column(col =>
                {
                    col.Item().AlignRight().Text($"No: {Get(values, "DocumentNumber", "-")}").FontSize(9).Bold();
                    col.Item().PaddingTop(2).AlignRight().Text($"Date: {Get(values, "DocumentDate", "-")}").FontSize(9);
                    foreach (var fact in TemplateText.ToPlainLines(Get(values, "HeaderFacts", "")))
                        col.Item().PaddingTop(2).AlignRight().Text(fact).FontSize(9);
                });
            });

            column.Item().PaddingTop(14).LineHorizontal(1.5f).LineColor(p.Primary);
        });
    }

    private static void Content(IContainer container, DocumentBranding b, TemplatedDocument doc, List<Segment> segments, Palette p)
    {
        container.PaddingTop(14).Column(column =>
        {
            foreach (var s in segments)
            {
                if (s.Kind == Kind.Html) { foreach (var node in s.Html!.ChildNodes) Block(column, node); continue; }
                switch (s.Block!.ToLowerInvariant())
                {
                    case "lineitems":          LineItems(column, doc, p); break;
                    case "taxsummary":         Amounts(column, doc.TaxSummary, p, "TAX SUMMARY"); break;
                    case "totals":             Amounts(column, doc.Totals, p, null); break;
                    case "bankdetails":        BankDetails(column, b, p); break;
                    case "termsandconditions": Terms(column, b, p); break;
                    case "signatureblock":     Signature(column, doc); break;
                }
            }
        });
    }

    private static void LineItems(ColumnDescriptor column, TemplatedDocument doc, Palette p)
    {
        if (doc.LineColumns.Count == 0) return;
        column.Item().PaddingTop(6).PaddingBottom(10).Table(table =>
        {
            table.ColumnsDefinition(cols => { foreach (var c in doc.LineColumns) cols.RelativeColumn(c.RelativeWidth); });
            table.Header(h =>
            {
                foreach (var c in doc.LineColumns)
                {
                    var cell = h.Cell().Background(p.Primary).DefaultTextStyle(x => x.Bold().FontSize(8.5f).FontColor(Colors.White))
                                .PaddingVertical(7).PaddingHorizontal(4);
                    (c.AlignRight ? cell.AlignRight() : cell).Text(c.Header);
                }
            });
            for (var i = 0; i < doc.LineRows.Count; i++)
            {
                var row = doc.LineRows[i];
                var last = i == doc.LineRows.Count - 1;
                var even = i % 2 == 1;
                for (var c = 0; c < doc.LineColumns.Count; c++)
                {
                    var cell = table.Cell().Background(even ? p.Tint : Colors.White).PaddingVertical(5).PaddingHorizontal(4)
                                    .BorderBottom(last ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
                    (doc.LineColumns[c].AlignRight ? cell.AlignRight() : cell).Text(c < row.Count ? row[c] : string.Empty);
                }
            }
        });
    }

    private static void Amounts(ColumnDescriptor column, List<TemplatedAmount> amounts, Palette p, string? title)
    {
        if (amounts.Count == 0) return;
        column.Item().PaddingBottom(12).AlignRight().Width(250).Column(col =>
        {
            if (title is not null) col.Item().PaddingBottom(4).Text(title).FontSize(8.5f).Bold().FontColor(p.Dark);
            foreach (var a in amounts)
            {
                var item = a.Emphasis ? col.Item().PaddingTop(4).BorderTop(1.5f).BorderColor(p.Primary).PaddingTop(5) : col.Item().PaddingBottom(3);
                item.Row(r =>
                {
                    var label = r.RelativeItem().Text(a.Label).FontSize(a.Emphasis ? 10.5f : 9);
                    var value = r.ConstantItem(130).AlignRight().Text(a.Value).FontSize(a.Emphasis ? 10.5f : 9.5f);
                    if (a.Emphasis) { label.Bold(); value.Bold().FontColor(p.Dark); }
                });
            }
        });
    }

    private static void BankDetails(ColumnDescriptor column, DocumentBranding b, Palette p)
    {
        var lines = b.ShowBankDetails ? TemplateText.ToPlainLines(b.BankDetailsText) : [];
        if (lines.Count == 0) return;
        column.Item().PaddingBottom(6).Text("BANK DETAILS").FontSize(9).Bold().FontColor(p.Dark);
        column.Item().PaddingBottom(12).Background(p.Tint).Padding(8).Column(box => { foreach (var l in lines) box.Item().Text(l).FontSize(8.5f); });
    }

    private static void Terms(ColumnDescriptor column, DocumentBranding b, Palette p)
    {
        var lines = TemplateText.ToPlainLines(b.TermsAndConditions);
        if (lines.Count == 0) return;
        column.Item().PaddingTop(4).PaddingBottom(4).Text("TERMS & CONDITIONS").FontSize(9).Bold().FontColor(p.Dark);
        foreach (var l in lines) column.Item().PaddingBottom(2).Text(l).FontSize(8).FontColor(Colors.Grey.Darken2);
    }

    private static void Signature(ColumnDescriptor column, TemplatedDocument doc)
    {
        if (!string.IsNullOrWhiteSpace(doc.SignatureDisclaimer))
        {
            column.Item().PaddingTop(30).AlignCenter().Text(doc.SignatureDisclaimer).FontSize(8).Italic();
            return;
        }
        var labels = doc.SignatureLabels.Count > 0 ? doc.SignatureLabels : ["Prepared By", "Approved By", "Authorized Signatory"];
        column.Item().PaddingTop(44).Row(row =>
        {
            for (var i = 0; i < labels.Count; i++)
            {
                if (i > 0) row.ConstantItem(24);
                var label = labels[i];
                row.RelativeItem().Column(col =>
                {
                    col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    col.Item().PaddingTop(4).Text(label).FontSize(8);
                });
            }
        });
    }

    private static void Footer(IContainer container, DocumentBranding b, TemplatedDocument doc, IReadOnlyDictionary<string, string?> values)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
            foreach (var line in HtmlLite.ToLines(TemplateText.Render(b.FooterHtml, values, html: true)))
                Line(column.Item(), line with { Align = line.Align == "left" ? "center" : line.Align }, 7f, Colors.Grey.Medium);
            column.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text($"Generated {doc.GeneratedAt.ToString("yyyy-MM-dd HH:mm", Culture)}").FontSize(7).FontColor(Colors.Grey.Medium);
                row.RelativeItem().AlignRight().Text(t =>
                {
                    t.CurrentPageNumber().FontSize(7).FontColor(Colors.Grey.Medium);
                    t.Span(" of ").FontSize(7).FontColor(Colors.Grey.Medium);
                    t.TotalPages().FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });
    }

    private static void Line(IContainer item, HtmlLiteLine line, float defaultSize, string? defaultColor)
    {
        var aligned = line.Align switch { "center" => item.AlignCenter(), "right" => item.AlignRight(), _ => item };
        var text = aligned.Text(line.Text).FontSize(line.FontSizePt ?? defaultSize);
        if (line.Bold) text.Bold();
        if ((line.Color ?? defaultColor) is { } color) text.FontColor(color);
    }

    private static string Get(IReadOnlyDictionary<string, string?> values, string key, string fallback) =>
        values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v! : fallback;

    // ── The rich-text subset (from PoDocumentService) ───────────────────────────

    private static void Block(ColumnDescriptor column, INode node)
    {
        if (node is IElement el)
        {
            switch (el.TagName)
            {
                case "P": case "DIV": case "H1": case "H2": case "H3": case "H4":
                {
                    var runs = new List<Run>();
                    var heading = el.TagName.StartsWith('H');
                    Collect(el, heading, false, false, runs);
                    if (runs.Count == 0) return;
                    var size = el.TagName switch { "H1" => 15f, "H2" => 13f, "H3" => 11.5f, "H4" => 10.5f, _ => (float?)null };
                    var item = column.Item().PaddingBottom(8);
                    var aligned = Alignment(el) switch { "center" => item.AlignCenter(), "right" => item.AlignRight(), _ => item };
                    aligned.Text(t => { foreach (var r in runs) { var s = Apply(t.Span(r.Text), r); if (size is { } f) s.FontSize(f); } });
                    return;
                }
                case "UL": case "OL":
                {
                    var n = 0;
                    foreach (var li in el.Children.Where(c => c.TagName == "LI"))
                    {
                        n++;
                        var runs = new List<Run>();
                        Collect(li, false, false, false, runs);
                        var bullet = el.TagName == "OL" ? $"{n}.  " : "•  ";
                        column.Item().PaddingBottom(4).PaddingLeft(12).Text(t => { t.Span(bullet); foreach (var r in runs) Apply(t.Span(r.Text), r); });
                    }
                    return;
                }
                case "BR":
                    column.Item().Height(4);
                    return;
                case "TABLE": case "TBODY": case "TR":
                    foreach (var child in el.ChildNodes) Block(column, child);
                    return;
                case "TD": case "TH":
                {
                    var runs = new List<Run>();
                    Collect(el, el.TagName == "TH", false, false, runs);
                    if (runs.Count > 0) column.Item().PaddingBottom(2).Text(t => { foreach (var r in runs) Apply(t.Span(r.Text), r); });
                    return;
                }
                default:
                {
                    // Inline content at the top level (b, span, a …): one paragraph.
                    var runs = new List<Run>();
                    Collect(el, el.TagName is "B" or "STRONG", el.TagName is "I" or "EM", el.TagName == "U", runs);
                    if (runs.Count > 0) column.Item().PaddingBottom(8).Text(t => { foreach (var r in runs) Apply(t.Span(r.Text), r); });
                    return;
                }
            }
        }

        if (node is IText text && !string.IsNullOrWhiteSpace(text.TextContent))
            column.Item().PaddingBottom(8).Text(text.TextContent.Trim());
    }

    private static string Alignment(IElement el)
    {
        var style = (el.GetAttribute("style") ?? "").Replace(" ", "");
        if (style.Contains("text-align:right", StringComparison.OrdinalIgnoreCase)) return "right";
        if (style.Contains("text-align:center", StringComparison.OrdinalIgnoreCase)) return "center";
        return "left";
    }

    private sealed record Run(string Text, bool Bold, bool Italic, bool Underline);

    private static void Collect(INode node, bool bold, bool italic, bool underline, List<Run> runs)
    {
        foreach (var child in node.ChildNodes)
        {
            if (child is IText t)
            {
                if (!string.IsNullOrEmpty(t.TextContent)) runs.Add(new Run(t.TextContent, bold, italic, underline));
            }
            else if (child is IElement el)
            {
                if (el.TagName == "BR") { runs.Add(new Run("\n", bold, italic, underline)); continue; }
                var style = el.GetAttribute("style") ?? "";
                var b = bold || el.TagName is "B" or "STRONG" || style.Contains("bold", StringComparison.OrdinalIgnoreCase)
                        || style.Contains("font-weight: 700", StringComparison.OrdinalIgnoreCase);
                var i = italic || el.TagName is "I" or "EM" || style.Contains("italic", StringComparison.OrdinalIgnoreCase);
                var u = underline || el.TagName == "U" || style.Contains("underline", StringComparison.OrdinalIgnoreCase);
                Collect(el, b, i, u, runs);
            }
        }
    }

    private static TextSpanDescriptor Apply(TextSpanDescriptor span, Run run)
    {
        if (run.Bold) span.Bold();
        if (run.Italic) span.Italic();
        if (run.Underline) span.Underline();
        return span;
    }

    /// <summary>For tests: the text a body would print, placeholders substituted (encoded) and tags stripped.</summary>
    internal static string PreviewText(string bodyHtml, IReadOnlyDictionary<string, string?> values) =>
        WebUtility.HtmlDecode(Regex.Replace(TemplateText.Render(bodyHtml, values, html: true), "<[^>]+>", " "));
}
