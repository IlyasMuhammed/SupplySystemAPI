using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using static SMS.Modules.Reports.Services.Exports.ReportPdf;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>A29-P9-05 §15 R8 — sales against the cost of the goods sold as a landscape A4 PDF: a row to a period and currency, then a bold total per currency.</summary>
public static class SalesVsPurchasePdfExporter
{
    public static byte[] Export(SalesVsPurchaseReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => Header(c, report.CompanyName, "SALES VS PURCHASE", CriteriaLines(report.Criteria)));
                page.Content().Element(c => ComposeContent(c, report));
                page.Footer().Element(c => Footer(c, report.GeneratedAt));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    /// <summary>What the report is of, in words.</summary>
    internal static IReadOnlyList<string> CriteriaLines(SalesVsPurchaseCriteria c) =>
    [
        $"Issued: {Period(c.DateFrom, c.DateTo)}",
        $"By {c.Period.ToLowerInvariant()}",
        "Revenue before tax; cost as booked when each invoice was issued"
    ];

    internal static string Percent(decimal? percent) => percent is { } p ? p.ToString("0.00", Culture) + "%" : "-";

    /// <summary>What the report cannot vouch for, when there is something; null when every invoice has its cost.</summary>
    internal static string? UncostedNote(SalesVsPurchaseReport report) =>
        report.Totals.Sum(t => t.UncostedRevenue) == 0m
            ? null
            : "Some invoices have no cost of sales booked, such as those issued before the product ledger existed. Their revenue is counted with no cost, so the margin on those rows is overstated by that much.";

    private static void ComposeContent(IContainer container, SalesVsPurchaseReport report)
    {
        container.Column(column =>
        {
            if (report.TotalRecords == 0)
            {
                Empty(column, "No sales were issued in this period.");
                return;
            }

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(2f);   // Period
                    columns.ConstantColumn(34);   // Currency
                    columns.RelativeColumn(0.8f); // Invoices
                    columns.RelativeColumn(1.3f); // Revenue
                    columns.RelativeColumn(1.3f); // Cost of goods sold
                    columns.RelativeColumn(1.3f); // Gross margin
                    columns.RelativeColumn(0.9f); // Margin %
                    columns.RelativeColumn(1.3f); // Revenue with no cost
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeadCell).Text("Period");
                    header.Cell().Element(HeadCell).Text("Cur.");
                    header.Cell().Element(HeadCell).AlignRight().Text("Invoices");
                    header.Cell().Element(HeadCell).AlignRight().Text("Revenue");
                    header.Cell().Element(HeadCell).AlignRight().Text("Cost of goods");
                    header.Cell().Element(HeadCell).AlignRight().Text("Gross margin");
                    header.Cell().Element(HeadCell).AlignRight().Text("Margin %");
                    header.Cell().Element(HeadCell).AlignRight().Text("Revenue, no cost");
                });

                for (var i = 0; i < report.Items.Count; i++)
                {
                    var item      = report.Items[i];
                    var alternate = i % 2 == 1;

                    table.Cell().Element(c => BodyCell(c, alternate)).Text(item.PeriodLabel);
                    table.Cell().Element(c => BodyCell(c, alternate)).Text(item.CurrencyCode);
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(item.InvoiceCount.ToString(Culture));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(item.Revenue));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(item.CostOfGoodsSold));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(item.GrossMargin));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Percent(item.GrossMarginPercent));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(item.UncostedRevenue));
                }

                foreach (var total in report.Totals)
                {
                    table.Cell().Element(TotalCell).Text($"Total, {total.PeriodCount.ToString(Culture)} periods");
                    table.Cell().Element(TotalCell).Text(total.CurrencyCode);
                    table.Cell().Element(TotalCell).AlignRight().Text(total.InvoiceCount.ToString(Culture));
                    table.Cell().Element(TotalCell).AlignRight().Text(Money(total.Revenue));
                    table.Cell().Element(TotalCell).AlignRight().Text(Money(total.CostOfGoodsSold));
                    table.Cell().Element(TotalCell).AlignRight().Text(Money(total.GrossMargin));
                    table.Cell().Element(TotalCell).AlignRight().Text(Percent(total.GrossMarginPercent));
                    table.Cell().Element(TotalCell).AlignRight().Text(Money(total.UncostedRevenue));
                }
            });

            if (UncostedNote(report) is { } note)
                column.Item().PaddingTop(8).Text(note).FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }
}

/// <summary>A29-P9-05 §15 R8 — sales against the cost of the goods sold as a workbook: the periods on the first sheet, typed for summing and filtering, and what was asked for and the totals on the second.</summary>
public static class SalesVsPurchaseExcelExporter
{
    private static readonly string[] Headers =
    [
        "Period", "Period Start", "Currency", "Invoices", "Revenue", "Cost of Goods Sold", "Gross Margin", "Margin %", "Revenue With No Cost"
    ];

    public static byte[] Export(SalesVsPurchaseReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        WritePeriods(workbook.Worksheets.Add("Sales vs Purchase"), report);
        WriteSummary(workbook.Worksheets.Add("Summary"), report);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WritePeriods(IXLWorksheet ws, SalesVsPurchaseReport report)
    {
        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var item in report.Items)
        {
            ws.Cell(row, 1).Value = item.PeriodLabel;
            ws.Cell(row, 2).Value = item.PeriodStart.Date;
            ws.Cell(row, 3).Value = item.CurrencyCode;
            ws.Cell(row, 4).Value = item.InvoiceCount;
            ws.Cell(row, 5).Value = item.Revenue;
            ws.Cell(row, 6).Value = item.CostOfGoodsSold;
            ws.Cell(row, 7).Value = item.GrossMargin;
            if (item.GrossMarginPercent is { } percent) ws.Cell(row, 8).Value = percent;
            ws.Cell(row, 9).Value = item.UncostedRevenue;
            row++;
        }

        if (row > 2)
        {
            ws.Range(2, 2, row - 1, 2).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Range(2, 5, row - 1, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(2, 8, row - 1, 8).Style.NumberFormat.Format = "0.00";
            ws.Range(2, 9, row - 1, 9).Style.NumberFormat.Format = "#,##0.00";
        }

        ws.Columns().AdjustToContents();
    }

    private static void WriteSummary(IXLWorksheet ws, SalesVsPurchaseReport report)
    {
        var c = report.Criteria;

        ws.Cell(1, 1).Value = "Issued from";
        if (c.DateFrom is { } from) ws.Cell(1, 2).Value = from.Date; else ws.Cell(1, 2).Value = "All dates";
        ws.Cell(2, 1).Value = "Issued to";
        if (c.DateTo is { } to) ws.Cell(2, 2).Value = to.Date; else ws.Cell(2, 2).Value = "All dates";
        ws.Cell(3, 1).Value = "Period";
        ws.Cell(3, 2).Value = c.Period;
        ws.Range(1, 1, 3, 1).Style.Font.Bold = true;
        ws.Range(1, 2, 2, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Range(1, 2, 3, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        const int headerRow = 5;
        string[] heads = ["Currency", "Periods", "Invoices", "Revenue", "Cost of Goods Sold", "Gross Margin", "Margin %", "Revenue With No Cost"];
        for (var i = 0; i < heads.Length; i++)
        {
            var cell = ws.Cell(headerRow, i + 1);
            cell.Value = heads[i];
            cell.Style.Font.Bold = true;
        }

        var row = headerRow + 1;
        foreach (var t in report.Totals)
        {
            ws.Cell(row, 1).Value = t.CurrencyCode;
            ws.Cell(row, 2).Value = t.PeriodCount;
            ws.Cell(row, 3).Value = t.InvoiceCount;
            ws.Cell(row, 4).Value = t.Revenue;
            ws.Cell(row, 5).Value = t.CostOfGoodsSold;
            ws.Cell(row, 6).Value = t.GrossMargin;
            if (t.GrossMarginPercent is { } percent) ws.Cell(row, 7).Value = percent;
            ws.Cell(row, 8).Value = t.UncostedRevenue;
            row++;
        }

        if (row > headerRow + 1)
        {
            ws.Range(headerRow + 1, 4, row - 1, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(headerRow + 1, 7, row - 1, 7).Style.NumberFormat.Format = "0.00";
            ws.Range(headerRow + 1, 8, row - 1, 8).Style.NumberFormat.Format = "#,##0.00";
        }

        ws.Columns().AdjustToContents();
    }
}
