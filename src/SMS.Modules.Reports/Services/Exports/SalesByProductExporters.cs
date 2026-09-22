using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using static SMS.Modules.Reports.Services.Exports.ReportPdf;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>A29-P9-04 §15 R4 — sales by product as a landscape A4 PDF: a row to a product and currency, then a bold total per currency.</summary>
public static class SalesByProductPdfExporter
{
    public static byte[] Export(SalesByProductReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => Header(c, report.CompanyName, "SALES BY PRODUCT", CriteriaLines(report.Criteria)));
                page.Content().Element(c => ComposeContent(c, report));
                page.Footer().Element(c => Footer(c, report.GeneratedAt));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    /// <summary>What the report is of, in words.</summary>
    internal static IReadOnlyList<string> CriteriaLines(SalesByProductCriteria c) =>
    [
        $"Issued: {Period(c.DateFrom, c.DateTo)}",
        $"Customer: {(c.PartnerId is null ? "All customers" : c.CustomerName ?? c.PartnerId.Value.ToString())}",
        "Revenue before tax"
    ];

    private static void ComposeContent(IContainer container, SalesByProductReport report)
    {
        container.Column(column =>
        {
            if (report.TotalRecords == 0)
            {
                Empty(column, "No products were sold in this period.");
                return;
            }

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(4);   // Product
                    columns.ConstantColumn(34);  // Currency
                    columns.RelativeColumn(1);   // Quantity
                    columns.RelativeColumn(1);   // Average price
                    columns.RelativeColumn(1.2f); // Revenue
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeadCell).Text("Product");
                    header.Cell().Element(HeadCell).Text("Cur.");
                    header.Cell().Element(HeadCell).AlignRight().Text("Quantity sold");
                    header.Cell().Element(HeadCell).AlignRight().Text("Avg unit price");
                    header.Cell().Element(HeadCell).AlignRight().Text("Revenue");
                });

                for (var i = 0; i < report.Items.Count; i++)
                {
                    var item      = report.Items[i];
                    var alternate = i % 2 == 1;

                    table.Cell().Element(c => BodyCell(c, alternate)).Text(item.ProductName ?? "-");
                    table.Cell().Element(c => BodyCell(c, alternate)).Text(item.CurrencyCode);
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Qty(item.QuantitySold));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(item.AverageUnitPrice is { } p ? Money(p) : "-");
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(item.Revenue));
                }

                foreach (var total in report.Totals)
                {
                    table.Cell().Element(TotalCell).Text($"Total, {total.ProductCount.ToString(Culture)} products");
                    table.Cell().Element(TotalCell).Text(total.CurrencyCode);
                    table.Cell().Element(TotalCell).Text(string.Empty);
                    table.Cell().Element(TotalCell).Text(string.Empty);
                    table.Cell().Element(TotalCell).AlignRight().Text(Money(total.Revenue));
                }
            });
        });
    }
}

/// <summary>A29-P9-04 §15 R4 — sales by product as a workbook: the products on the first sheet, typed for summing and filtering, and what was asked for and the totals on the second.</summary>
public static class SalesByProductExcelExporter
{
    private static readonly string[] Headers = ["Product", "Currency", "Quantity Sold", "Average Unit Price", "Revenue"];

    public static byte[] Export(SalesByProductReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        WriteProducts(workbook.Worksheets.Add("Sales by Product"), report);
        WriteSummary(workbook.Worksheets.Add("Summary"), report);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteProducts(IXLWorksheet ws, SalesByProductReport report)
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
            ws.Cell(row, 1).Value = item.ProductName ?? string.Empty;
            ws.Cell(row, 2).Value = item.CurrencyCode;
            ws.Cell(row, 3).Value = item.QuantitySold;
            if (item.AverageUnitPrice is { } price) ws.Cell(row, 4).Value = price;
            ws.Cell(row, 5).Value = item.Revenue;
            row++;
        }

        if (row > 2)
        {
            ws.Range(2, 3, row - 1, 3).Style.NumberFormat.Format = "#,##0.####";
            ws.Range(2, 4, row - 1, 5).Style.NumberFormat.Format = "#,##0.00";
        }

        ws.Columns().AdjustToContents();
    }

    private static void WriteSummary(IXLWorksheet ws, SalesByProductReport report)
    {
        var c = report.Criteria;

        ws.Cell(1, 1).Value = "Issued from";
        if (c.DateFrom is { } from) ws.Cell(1, 2).Value = from.Date; else ws.Cell(1, 2).Value = "All dates";
        ws.Cell(2, 1).Value = "Issued to";
        if (c.DateTo is { } to) ws.Cell(2, 2).Value = to.Date; else ws.Cell(2, 2).Value = "All dates";
        ws.Cell(3, 1).Value = "Customer";
        ws.Cell(3, 2).Value = c.PartnerId is null ? "All customers" : c.CustomerName ?? c.PartnerId.Value.ToString();
        ws.Range(1, 1, 3, 1).Style.Font.Bold = true;
        ws.Range(1, 2, 2, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Range(1, 2, 3, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        const int headerRow = 5;
        string[] heads = ["Currency", "Products", "Revenue"];
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
            row++;
        }

        if (row > headerRow + 1) ws.Range(headerRow + 1, 3, row - 1, 3).Style.NumberFormat.Format = "#,##0.00";

        ws.Columns().AdjustToContents();
    }
}
