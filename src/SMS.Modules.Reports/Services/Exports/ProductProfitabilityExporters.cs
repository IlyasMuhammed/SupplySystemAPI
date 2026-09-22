using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using static SMS.Modules.Reports.Services.Exports.ReportPdf;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>A29-P9-06 §15 R10 — product profitability as a landscape A4 PDF: the products ranked by margin, a row to a product and currency, then a bold total per currency.</summary>
public static class ProductProfitabilityPdfExporter
{
    public static byte[] Export(ProfitabilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => Header(c, report.CompanyName, "PRODUCT PROFITABILITY", CriteriaLines(report.Criteria)));
                page.Content().Element(c => ComposeContent(c, report));
                page.Footer().Element(c => Footer(c, report.GeneratedAt));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    /// <summary>What the report is of, in words.</summary>
    internal static IReadOnlyList<string> CriteriaLines(ProfitabilityReportCriteria c) =>
    [
        $"Issued: {Period(c.DateFrom, c.DateTo)}",
        "Ranked by gross profit, each currency on its own",
        "Only sales with a cost of sales booked"
    ];

    internal static string Percent(decimal? percent) => percent is { } p ? p.ToString("0.00", Culture) + "%" : "-";

    private static void ComposeContent(IContainer container, ProfitabilityReport report)
    {
        container.Column(column =>
        {
            if (report.TotalRecords == 0)
            {
                Empty(column, "No sales with a cost of sales were issued in this period.");
                return;
            }

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(30);   // Rank
                    columns.RelativeColumn(4);    // Product
                    columns.ConstantColumn(34);   // Currency
                    columns.RelativeColumn(1);    // Quantity sold
                    columns.RelativeColumn(1.3f); // Revenue
                    columns.RelativeColumn(1.3f); // Cost of goods sold
                    columns.RelativeColumn(1.3f); // Gross profit
                    columns.RelativeColumn(0.9f); // Margin %
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeadCell).AlignRight().Text("#");
                    header.Cell().Element(HeadCell).Text("Product");
                    header.Cell().Element(HeadCell).Text("Cur.");
                    header.Cell().Element(HeadCell).AlignRight().Text("Qty sold");
                    header.Cell().Element(HeadCell).AlignRight().Text("Revenue");
                    header.Cell().Element(HeadCell).AlignRight().Text("Cost of goods");
                    header.Cell().Element(HeadCell).AlignRight().Text("Gross profit");
                    header.Cell().Element(HeadCell).AlignRight().Text("Margin %");
                });

                for (var i = 0; i < report.Items.Count; i++)
                {
                    var item      = report.Items[i];
                    var alternate = i % 2 == 1;

                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(item.Rank.ToString(Culture));
                    table.Cell().Element(c => BodyCell(c, alternate)).Text(item.ProductName ?? "-");
                    table.Cell().Element(c => BodyCell(c, alternate)).Text(item.CurrencyCode);
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Qty(item.QuantitySold));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(item.Revenue));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(item.CostOfGoodsSold));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(item.GrossProfit));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Percent(item.MarginPercent));
                }

                foreach (var total in report.Totals)
                {
                    table.Cell().Element(TotalCell).Text(string.Empty);
                    table.Cell().Element(TotalCell).Text($"Total, {total.ProductCount.ToString(Culture)} products");
                    table.Cell().Element(TotalCell).Text(total.CurrencyCode);
                    table.Cell().Element(TotalCell).Text(string.Empty);
                    table.Cell().Element(TotalCell).AlignRight().Text(Money(total.Revenue));
                    table.Cell().Element(TotalCell).AlignRight().Text(Money(total.CostOfGoodsSold));
                    table.Cell().Element(TotalCell).AlignRight().Text(Money(total.GrossProfit));
                    table.Cell().Element(TotalCell).AlignRight().Text(Percent(total.MarginPercent));
                }
            });
        });
    }
}

/// <summary>A29-P9-06 §15 R10 — product profitability as a workbook: the ranked products on the first sheet, typed for summing and filtering, and what was asked for and the totals on the second.</summary>
public static class ProductProfitabilityExcelExporter
{
    private static readonly string[] Headers =
        ["Rank", "Product", "Currency", "Quantity Sold", "Revenue", "Cost of Goods Sold", "Gross Profit", "Margin %"];

    public static byte[] Export(ProfitabilityReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        WriteProducts(workbook.Worksheets.Add("Product Profitability"), report);
        WriteSummary(workbook.Worksheets.Add("Summary"), report);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteProducts(IXLWorksheet ws, ProfitabilityReport report)
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
            ws.Cell(row, 1).Value = item.Rank;
            ws.Cell(row, 2).Value = item.ProductName ?? string.Empty;
            ws.Cell(row, 3).Value = item.CurrencyCode;
            ws.Cell(row, 4).Value = item.QuantitySold;
            ws.Cell(row, 5).Value = item.Revenue;
            ws.Cell(row, 6).Value = item.CostOfGoodsSold;
            ws.Cell(row, 7).Value = item.GrossProfit;
            if (item.MarginPercent is { } percent) ws.Cell(row, 8).Value = percent;
            row++;
        }

        if (row > 2)
        {
            ws.Range(2, 4, row - 1, 4).Style.NumberFormat.Format = "#,##0.####";
            ws.Range(2, 5, row - 1, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(2, 8, row - 1, 8).Style.NumberFormat.Format = "0.00";
        }

        ws.Columns().AdjustToContents();
    }

    private static void WriteSummary(IXLWorksheet ws, ProfitabilityReport report)
    {
        var c = report.Criteria;

        ws.Cell(1, 1).Value = "Issued from";
        if (c.DateFrom is { } from) ws.Cell(1, 2).Value = from.Date; else ws.Cell(1, 2).Value = "All dates";
        ws.Cell(2, 1).Value = "Issued to";
        if (c.DateTo is { } to) ws.Cell(2, 2).Value = to.Date; else ws.Cell(2, 2).Value = "All dates";
        ws.Range(1, 1, 2, 1).Style.Font.Bold = true;
        ws.Range(1, 2, 2, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Range(1, 2, 2, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        const int headerRow = 4;
        string[] heads = ["Currency", "Products", "Revenue", "Cost of Goods Sold", "Gross Profit", "Margin %"];
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
            ws.Cell(row, 2).Value = t.ProductCount;
            ws.Cell(row, 3).Value = t.Revenue;
            ws.Cell(row, 4).Value = t.CostOfGoodsSold;
            ws.Cell(row, 5).Value = t.GrossProfit;
            if (t.MarginPercent is { } percent) ws.Cell(row, 6).Value = percent;
            row++;
        }

        if (row > headerRow + 1)
        {
            ws.Range(headerRow + 1, 3, row - 1, 5).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(headerRow + 1, 6, row - 1, 6).Style.NumberFormat.Format = "0.00";
        }

        ws.Columns().AdjustToContents();
    }
}
