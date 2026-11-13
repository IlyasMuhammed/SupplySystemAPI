using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Demand.Services.Documents;

/// <summary>The three colours of a built-in layout: accents / headings / stripes.</summary>
internal sealed record DocumentPalette(string Primary, string Dark, string Tint)
{
    /// <summary>The house palette every built-in document used before A50.</summary>
    public static readonly DocumentPalette House = new("#6C63FF", "#4A42CC", "#F4F3FF");

    /// <summary>
    /// The template's colour, or <paramref name="fallback"/> when there is no template, no valid colour, or the house colour
    /// itself (so the house look stays exactly the house look).
    /// </summary>
    public static DocumentPalette Resolve(DocumentBranding? branding, DocumentPalette fallback)
    {
        var color = branding?.PrimaryColor?.Trim();
        if (!BrandPalette.IsValid(color) || string.Equals(color, fallback.Primary, StringComparison.OrdinalIgnoreCase)) return fallback;
        var primary = color!.ToUpperInvariant();
        return new DocumentPalette(primary, BrandPalette.Darken(primary), BrandPalette.Tint(primary));
    }
}

/// <summary>
/// A50 TPL (D-25, D-66) — what the built-in QuestPDF layouts (sales invoice, quotation, PO, packing list, statement, receipt)
/// draw from an organization's document template: the header / footer HTML as styled lines (<see cref="HtmlLite"/>) with the
/// {Placeholders} filled, and the terms block. Every method is only called when the org has a template, so a document
/// without one never runs this code (its output stays the pre-A50 one, byte for byte).
/// </summary>
internal static class BrandedDocumentKit
{
    /// <summary>The header/footer placeholder values (TEMPLATE-PLACEHOLDERS.md §2).</summary>
    public static Dictionary<string, string?> HeaderValues(
        DocumentBranding branding, string documentTitle, string? documentNumber, DateTime? documentDate) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CompanyName"]    = branding.CompanyName ?? string.Empty,
            ["CompanyAddress"] = branding.CompanyAddress ?? string.Empty,
            ["CompanyPhone"]   = branding.CompanyPhone ?? string.Empty,
            ["CompanyEmail"]   = branding.CompanyEmail ?? string.Empty,
            ["CompanyTaxId"]   = branding.CompanyTaxId ?? string.Empty,
            ["DocumentTitle"]  = documentTitle,
            ["DocumentNumber"] = documentNumber ?? string.Empty,
            ["DocumentDate"]   = documentDate?.ToString("dd MMM yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
            ["PageNumber"]     = string.Empty,
        };

    /// <summary>The branding with the company block filled from the letterhead where the provider had none.</summary>
    public static DocumentBranding WithLetterhead(
        DocumentBranding b, string? name, string? address, string? phone, string? email, string? taxId) => new()
    {
        TemplateType = b.TemplateType, LogoBytes = b.LogoBytes, PrimaryColor = b.PrimaryColor, HeaderHtml = b.HeaderHtml, BodyHtml = b.BodyHtml,
        FooterHtml = b.FooterHtml, ShowBankDetails = b.ShowBankDetails, BankDetailsText = b.BankDetailsText, TermsAndConditions = b.TermsAndConditions,
        CompanyName = Pick(b.CompanyName, name), CompanyAddress = Pick(b.CompanyAddress, address), CompanyPhone = Pick(b.CompanyPhone, phone),
        CompanyEmail = Pick(b.CompanyEmail, email), CompanyTaxId = Pick(b.CompanyTaxId, taxId),
    };

    /// <summary>The template's value, else the letterhead's.</summary>
    public static string? Pick(string? branded, string? letterhead) => string.IsNullOrWhiteSpace(branded) ? letterhead : branded;

    public static string Money(decimal amount) => amount.ToString("N2", CultureInfo.InvariantCulture);
    public static string Date(DateTime date) => date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>The header HTML in place of the letterhead's company block. False when the template has no header.</summary>
    public static bool TryComposeHeader(IContainer container, DocumentBranding? branding, IReadOnlyDictionary<string, string?> values, string dark)
    {
        if (branding is null || string.IsNullOrWhiteSpace(branding.HeaderHtml)) return false;
        var columns = HtmlLite.ToColumns(TemplateText.Render(branding.HeaderHtml, values, html: true));
        if (columns.Count == 0) return false;

        container.Row(row =>
        {
            for (var i = 0; i < columns.Count; i++)
            {
                var lines = columns[i];
                var first = i == 0;
                row.RelativeItem().Column(col =>
                {
                    for (var j = 0; j < lines.Count; j++)
                        DrawLine(col.Item().PaddingTop(j == 0 ? 0 : 2), lines[j], first && j == 0 ? 16f : 8f, first && j == 0 ? dark : null);
                });
            }
        });
        return true;
    }

    /// <summary>The footer HTML lines, centred unless styled otherwise. False when the template has no footer.</summary>
    public static bool TryComposeFooter(ColumnDescriptor column, DocumentBranding? branding, IReadOnlyDictionary<string, string?> values)
    {
        if (branding is null || string.IsNullOrWhiteSpace(branding.FooterHtml)) return false;
        var lines = HtmlLite.ToLines(TemplateText.Render(branding.FooterHtml, values, html: true));
        if (lines.Count == 0) return false;
        foreach (var line in lines)
            DrawLine(column.Item(), line with { Align = line.Align == "left" ? "center" : line.Align }, 7f, Colors.Grey.Medium);
        return true;
    }

    /// <summary>"TERMS &amp; CONDITIONS" and the template's text, line by line. Nothing when there is none.</summary>
    public static void ComposeTerms(ColumnDescriptor column, DocumentBranding? branding, string dark)
    {
        var lines = TemplateText.ToPlainLines(branding?.TermsAndConditions);
        if (lines.Count == 0) return;
        column.Item().PaddingTop(6).PaddingBottom(4).Text("TERMS & CONDITIONS").FontSize(9).Bold().FontColor(dark);
        foreach (var line in lines)
            column.Item().PaddingBottom(2).Text(line).FontSize(8).FontColor(Colors.Grey.Darken2);
    }

    /// <summary>The bank details the template prints (null when it hides them or has none).</summary>
    public static string[]? BankLines(DocumentBranding? branding, string? fallbackText)
    {
        if (branding is null) return null;
        if (!branding.ShowBankDetails) return [];
        var text = string.IsNullOrWhiteSpace(branding.BankDetailsText) ? fallbackText : branding.BankDetailsText;
        return (text ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static void DrawLine(IContainer item, HtmlLiteLine line, float defaultSize, string? defaultColor)
    {
        var aligned = line.Align switch
        {
            "center" => item.AlignCenter(),
            "right"  => item.AlignRight(),
            _        => item
        };
        var text = aligned.Text(line.Text).FontSize(line.FontSizePt ?? defaultSize);
        if (line.Bold) text.Bold();
        var color = line.Color ?? defaultColor;
        if (color is not null) text.FontColor(color);
    }
}
