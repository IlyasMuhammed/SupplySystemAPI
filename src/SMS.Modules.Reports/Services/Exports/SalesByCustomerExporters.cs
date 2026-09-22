using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using static SMS.Modules.Reports.Services.Exports.ReportPdf;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>A29-P9-04 §15 R5 — sales by customer as a landscape A4 PDF: a row to a customer and currency, then a bold total per currency.</summary>
public static class SalesByCustomerPdfExporter
{
    public static byte[] Export(SalesByCustomerReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => Header(c, report.CompanyName, "SALES BY CUSTOMER", CriteriaLines(report.Criteria)));
                page.Content().Element(c => ComposeContent(c, report));
                page.Footer().Element(c => Footer(c, report.GeneratedAt));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    /// <summary>What the report is of, in words.</summary>
    internal static IReadOnlyList<string> CriteriaLines(SalesByCustomerCriteria c) =>
    [
        $"Issued: {Period(c.DateFrom, c.DateTo)}",
        "Revenue before tax",
        "Average order value is revenue per sale order"
    ];

    private static void ComposeContent(IContainer container, SalesByCustomerReport report)
    {
        container.Column(column =>
        {
            if (report.TotalRecords == 0)
            {
                Empty(column, "No customers were billed in this period.");
                return;
            }

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(4);    // Customer
                    columns.ConstantColumn(34);   // Currency
                    columns.RelativeColumn(1);    // Orders
                    columns.RelativeColumn(1);    // Invoices
                    columns.RelativeColumn(1.2f); // Revenue
                    columns.RelativeColumn(1.2f); // Average order value
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeadCell).Text("Customer");
                    header.Cell().Element(HeadCell).Text("Cur.");
                    header.Cell().Element(HeadCell).AlignRight().Text("Orders");
                    header.Cell().Element(HeadCell).AlignRight().Text("Invoices");
                    header.Cell().Element(HeadCell).AlignRight().Text("Revenue");
                    header.Cell().Element(HeadCell).AlignRight().Text("Avg order value");
                });

                for (var i = 0; i < report.Items.Count; i++)
                {
                    var item      = report.Items[i];
                    var alternate = i % 2 == 1;

                    table.Cell().Element(c => BodyCell(c, alternate)).Text(item.CustomerName ?? "-");
                    table.Cell().Element(c => BodyCell(c, alternate)).Text(item.CurrencyCode);
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(item.OrderCount.ToString(Culture));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(item.InvoiceCount.ToString(Culture));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(item.Revenue));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(item.AverageOrderValue));
                }

                foreach (var total in report.Totals)
                {
                    table.Cell().Element(TotalCell).Text($"Total, {total.CustomerCount.ToString(Culture)} customers");
                    table.Cell().Element(TotalCell).Text(total.CurrencyCode);
                    table.Cell().Element(TotalCell).AlignRight().Text(total.OrderCount.ToString(Culture));
                    table.Cell().Element(TotalCell).AlignRight().Text(total.InvoiceCount.ToString(Culture));
                    table.Cell().Element(TotalCell).AlignRight().Text(Money(total.Revenue));
                    table.Cell().Element(TotalCell).AlignRight().Text(Money(total.AverageOrderValue));
                }
            });
        });
    }
}

/// <summary>A29-P9-04 §15 R5 — sales by customer as a workbook: the customers on the first sheet, typed for summing and filtering, and what was asked for and the totals on the second.</summary>
public static class SalesByCustomerExcelExporter
{
    private static readonly string[] Headers = ["Customer", "Currency", "Orders", "Invoices", "Revenue", "Average Order Value"];

    public static byte[] Export(SalesByCustomerReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        WriteCustomers(workbook.Worksheets.Add("Sales by Customer"), report);
        WriteSummary(workbook.Worksheets.Add("Summary"), report);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteCustomers(IXLWorksheet ws, SalesByCustomerReport report)
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
            ws.Cell(row, 1).Value = item.CustomerName ?? string.Empty;
            ws.Cell(row, 2).Value = item.CurrencyCode;
            ws.Cell(row, 3).Value = item.OrderCount;
            ws.Cell(row, 4).Value = item.InvoiceCount;
            ws.Cell(row, 5).Value = item.Revenue;
            ws.Cell(row, 6).Value = item.AverageOrderValue;
            row++;
        }

        if (row > 2) ws.Range(2, 5, row - 1, 6).Style.NumberFormat.Format = "#,##0.00";

        ws.Columns().AdjustToContents();
    }

    private static void WriteSummary(IXLWorksheet ws, SalesByCustomerReport report)
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
        string[] heads = ["Currency", "Customers", "Orders", "Invoices", "Revenue", "Average Order Value"];
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
            ws.Cell(row, 2).Value = t.CustomerCount;
            ws.Cell(row, 3).Value = t.OrderCount;
            ws.Cell(row, 4).Value = t.InvoiceCount;
            ws.Cell(row, 5).Value = t.Revenue;
            ws.Cell(row, 6).Value = t.AverageOrderValue;
            row++;
        }

        if (row > headerRow + 1) ws.Range(headerRow + 1, 5, row - 1, 6).Style.NumberFormat.Format = "#,##0.00";

        ws.Columns().AdjustToContents();
    }
}
