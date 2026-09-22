using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>
/// What the receivables reports' PDFs share: the palette and letterhead line of the sales invoice and
/// the sales order register, so every printed report reads as part of one set, and the few cell styles
/// their tables are built from.
/// </summary>
internal static class ReportPdf
{
    internal const string Brand     = "#6C63FF";
    internal const string BrandDark = "#4A42CC";
    internal const string BrandTint = "#F4F3FF";

    internal static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    internal static string Money(decimal amount) => amount.ToString("N2", Culture);
    internal static string Date(DateTime date) => date.ToString("dd MMM yyyy", Culture);
    internal static string Label(string code) => code.Replace('_', ' ');

    /// <summary>A quantity to at most four decimal places, as the books hold it, without trailing zeros.</summary>
    internal static string Qty(decimal quantity) => quantity.ToString("#,##0.####", Culture);

    /// <summary>A range of days in words: "All dates", "From …", "Up to …" or "… to …".</summary>
    internal static string Period(DateTime? from, DateTime? to) => (from, to) switch
    {
        (null, null)   => "All dates",
        ({ } f, null)  => $"From {Date(f)}",
        (null, { } t)  => $"Up to {Date(t)}",
        ({ } f, { } t) => $"{Date(f)} to {Date(t)}"
    };

    /// <summary>The company and the report's name, then what the report is of, in words.</summary>
    internal static void Header(IContainer container, string? company, string title, IEnumerable<string> criteria)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Text(company ?? "Company Name").FontSize(14).Bold().FontColor(BrandDark);
                row.RelativeItem().AlignRight().Text(title).FontSize(14).Bold().FontColor(BrandDark);
            });

            column.Item().PaddingTop(6).Row(row =>
            {
                foreach (var line in criteria)
                    row.RelativeItem().Text(line).FontSize(8).FontColor(Colors.Grey.Darken1);
            });

            column.Item().PaddingTop(6).LineHorizontal(1.5f).LineColor(Brand);
            column.Item().PaddingBottom(6);
        });
    }

    internal static void Footer(IContainer container, DateTime generatedAt)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
            column.Item().Row(row =>
            {
                row.RelativeItem().Text($"Generated {generatedAt.ToString("yyyy-MM-dd HH:mm", Culture)} UTC")
                   .FontSize(7).FontColor(Colors.Grey.Medium);
                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.CurrentPageNumber().FontSize(7).FontColor(Colors.Grey.Medium);
                    text.Span(" of ").FontSize(7).FontColor(Colors.Grey.Medium);
                    text.TotalPages().FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });
    }

    internal static void SectionTitle(ColumnDescriptor column, string text) =>
        column.Item().PaddingTop(4).PaddingBottom(4).Text(text).FontSize(9).Bold().FontColor(BrandDark);

    internal static void Empty(ColumnDescriptor column, string message) =>
        column.Item().PaddingTop(20).AlignCenter().Text(message).FontSize(10).FontColor(Colors.Grey.Darken1);

    /// <summary>A table's column heads, on the brand colour.</summary>
    internal static IContainer HeadCell(IContainer c) =>
        c.Background(Brand)
         .DefaultTextStyle(x => x.Bold().FontSize(8).FontColor(Colors.White))
         .PaddingVertical(5).PaddingHorizontal(3);

    /// <summary>A summary table's column heads: darker, so it is not mistaken for the detail beneath it.</summary>
    internal static IContainer SummaryHeadCell(IContainer c) =>
        c.Background(Colors.Grey.Darken3)
         .DefaultTextStyle(x => x.Bold().FontSize(8).FontColor(Colors.White))
         .PaddingVertical(4).PaddingHorizontal(3);

    internal static IContainer BodyCell(IContainer c, bool alternate) =>
        c.Background(alternate ? BrandTint : Colors.White)
         .PaddingVertical(3).PaddingHorizontal(3)
         .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);

    internal static IContainer SummaryCell(IContainer c) =>
        c.PaddingVertical(4).PaddingHorizontal(3)
         .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);

    /// <summary>A totals row: bold on the tint, so it stands out from the customers above it.</summary>
    internal static IContainer TotalCell(IContainer c) =>
        c.Background(BrandTint).PaddingVertical(4).PaddingHorizontal(3)
         .BorderTop(1f).BorderColor(Brand).DefaultTextStyle(x => x.Bold());
}
