using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>
/// A29-P9-01 §15 R1 — the sale order register as a landscape A4 PDF. The palette and the letterhead line
/// match the sales invoice and purchase order documents, so a printed register reads as part of the same set.
/// </summary>
public static class SalesOrderRegisterPdfExporter
{
    private const string BrandColor     = "#6C63FF";
    private const string BrandColorDark = "#4A42CC";
    private const string BrandColorTint = "#F4F3FF";

    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static byte[] Export(SalesOrderRegisterReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => ComposeHeader(c, report));
                page.Content().Element(c => ComposeContent(c, report));
                page.Footer().Element(c => ComposeFooter(c, report));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    private static string Money(decimal amount) => amount.ToString("N2", Culture);
    private static string Date(DateTime date) => date.ToString("dd MMM yyyy", Culture);
    private static string Label(string code) => code.Replace('_', ' ');

    /// <summary>What the register is of, in words: the filter as it was applied.</summary>
    internal static IReadOnlyList<string> CriteriaLines(SalesOrderRegisterCriteria c)
    {
        string period = (c.DateFrom, c.DateTo) switch
        {
            (null, null)     => "All dates",
            ({ } f, null)    => $"From {Date(f)}",
            (null, { } t)    => $"Up to {Date(t)}",
            ({ } f, { } t)   => $"{Date(f)} to {Date(t)}"
        };

        var customer = c.PartnerId is null
            ? "All customers"
            : c.CustomerName ?? c.PartnerId.Value.ToString();

        return
        [
            $"Order date: {period}",
            $"Status: {(c.Status is null ? "All" : Label(c.Status))}",
            $"Customer: {customer}",
            $"Delivery mode: {(c.DeliveryMode is null ? "All" : Label(c.DeliveryMode))}"
        ];
    }

    // ── Composition ──────────────────────────────────────────────────────────

    private static void ComposeHeader(IContainer container, SalesOrderRegisterReport report)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Text(report.CompanyName ?? "Company Name")
                   .FontSize(14).Bold().FontColor(BrandColorDark);
                row.RelativeItem().AlignRight().Text("SALES ORDER REGISTER")
                   .FontSize(14).Bold().FontColor(BrandColorDark);
            });

            column.Item().PaddingTop(6).Row(row =>
            {
                foreach (var line in CriteriaLines(report.Criteria))
                    row.RelativeItem().Text(line).FontSize(8).FontColor(Colors.Grey.Darken1);
            });

            column.Item().PaddingTop(6).LineHorizontal(1.5f).LineColor(BrandColor);
            column.Item().PaddingBottom(6);
        });
    }

    private static void ComposeContent(IContainer container, SalesOrderRegisterReport report)
    {
        container.Column(column =>
        {
            if (report.Items.Count == 0)
            {
                column.Item().PaddingTop(20).AlignCenter()
                      .Text("No sale orders match these filters.").FontSize(10).FontColor(Colors.Grey.Darken1);
                return;
            }

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(74);   // SO number
                    columns.ConstantColumn(56);   // Order date
                    columns.RelativeColumn(2f);   // Customer
                    columns.ConstantColumn(104);  // Status, wide enough for PARTIALLY FULFILLED on one line
                    columns.ConstantColumn(62);   // Delivery
                    columns.ConstantColumn(30);   // Currency
                    columns.ConstantColumn(26);   // Lines
                    columns.RelativeColumn(1);    // Subtotal
                    columns.RelativeColumn(1);    // Discount
                    columns.RelativeColumn(1);    // Tax
                    columns.RelativeColumn(1.1f); // Total
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("SO No.");
                    header.Cell().Element(HeaderCell).Text("Date");
                    header.Cell().Element(HeaderCell).Text("Customer");
                    header.Cell().Element(HeaderCell).Text("Status");
                    header.Cell().Element(HeaderCell).Text("Delivery");
                    header.Cell().Element(HeaderCell).Text("Cur.");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Lines");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Subtotal");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Discount");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Tax");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Total");

                    static IContainer HeaderCell(IContainer c) =>
                        c.Background(BrandColor)
                         .DefaultTextStyle(x => x.Bold().FontSize(8).FontColor(Colors.White))
                         .PaddingVertical(5).PaddingHorizontal(3);
                });

                for (var i = 0; i < report.Items.Count; i++)
                {
                    var item   = report.Items[i];
                    var isEven = i % 2 == 1;

                    table.Cell().Element(c => BodyCell(c, isEven)).Text(item.SoNumber);
                    table.Cell().Element(c => BodyCell(c, isEven)).Text(Date(item.OrderDate));
                    table.Cell().Element(c => BodyCell(c, isEven)).Text(item.CustomerName ?? "-");
                    table.Cell().Element(c => BodyCell(c, isEven)).Text(Label(item.Status));
                    table.Cell().Element(c => BodyCell(c, isEven)).Text(Label(item.DeliveryMode));
                    table.Cell().Element(c => BodyCell(c, isEven)).Text(item.CurrencyCode);
                    table.Cell().Element(c => BodyCell(c, isEven)).AlignRight().Text(item.LineCount.ToString(Culture));
                    table.Cell().Element(c => BodyCell(c, isEven)).AlignRight().Text(Money(item.Subtotal));
                    table.Cell().Element(c => BodyCell(c, isEven)).AlignRight().Text(Money(item.DiscountAmount));
                    table.Cell().Element(c => BodyCell(c, isEven)).AlignRight().Text(Money(item.TaxAmount));
                    table.Cell().Element(c => BodyCell(c, isEven)).AlignRight().Text(Money(item.GrandTotal));
                }

                static IContainer BodyCell(IContainer c, bool isEven) =>
                    c.Background(isEven ? BrandColorTint : Colors.White)
                     .PaddingVertical(3).PaddingHorizontal(3)
                     .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);
            });

            ComposeTotals(column, report);
        });
    }

    /// <summary>One row to a currency: there is no rate to add one to another with.</summary>
    private static void ComposeTotals(ColumnDescriptor column, SalesOrderRegisterReport report)
    {
        column.Item().PaddingTop(14).EnsureSpace(80).Column(box =>
        {
            box.Item().PaddingBottom(4).Text($"TOTALS ({report.TotalRecords.ToString(Culture)} orders)")
               .FontSize(9).Bold().FontColor(BrandColorDark);

            box.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(60);
                    columns.ConstantColumn(60);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(1);
                    columns.RelativeColumn(1);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("Currency");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Orders");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Subtotal");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Discount");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Tax");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Total");

                    static IContainer HeaderCell(IContainer c) =>
                        c.Background(Colors.Grey.Darken3)
                         .DefaultTextStyle(x => x.Bold().FontSize(8).FontColor(Colors.White))
                         .PaddingVertical(4).PaddingHorizontal(3);
                });

                foreach (var total in report.Totals)
                {
                    table.Cell().Element(Cell).Text(total.CurrencyCode).Bold();
                    table.Cell().Element(Cell).AlignRight().Text(total.OrderCount.ToString(Culture));
                    table.Cell().Element(Cell).AlignRight().Text(Money(total.Subtotal));
                    table.Cell().Element(Cell).AlignRight().Text(Money(total.DiscountAmount));
                    table.Cell().Element(Cell).AlignRight().Text(Money(total.TaxAmount));
                    table.Cell().Element(Cell).AlignRight().Text(Money(total.GrandTotal)).Bold();
                }

                static IContainer Cell(IContainer c) =>
                    c.PaddingVertical(4).PaddingHorizontal(3)
                     .BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);
            });
        });
    }

    private static void ComposeFooter(IContainer container, SalesOrderRegisterReport report)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
            column.Item().Row(row =>
            {
                row.RelativeItem().Text($"Generated {report.GeneratedAt.ToString("yyyy-MM-dd HH:mm", Culture)} UTC")
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
}
