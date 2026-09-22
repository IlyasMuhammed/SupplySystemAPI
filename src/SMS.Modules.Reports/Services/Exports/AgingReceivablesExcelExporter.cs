using ClosedXML.Excel;
using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>
/// A29-P9-03 §15 R3 — aging receivables as a workbook. The first sheet is the summary — who owes what in
/// each bucket, with a bold row per currency because there is no rate to add one to another with — and the
/// second is the outstanding invoices behind it, with typed amounts and dates so it can be summed and filtered.
/// </summary>
public static class AgingReceivablesExcelExporter
{
    private static readonly string[] SummaryHeaders =
        ["Customer", "Currency", "Invoices", $"{AgingBuckets.Days0To30} days", $"{AgingBuckets.Days31To60} days",
         $"{AgingBuckets.Days61To90} days", $"{AgingBuckets.Over90} days", "Total"];

    private static readonly string[] InvoiceHeaders =
        ["Invoice Number", "Sale Order", "Customer", "Invoice Date", "Due Date", "Days Past Due", "Bucket", "Currency",
         "Total", "Paid", "Outstanding"];

    public static byte[] Export(AgingReceivablesReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        WriteSummary(workbook.Worksheets.Add("Summary"), report);
        WriteInvoices(workbook.Worksheets.Add("Invoices"), report);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteSummary(IXLWorksheet ws, AgingReceivablesReport report)
    {
        var c = report.Criteria;

        ws.Cell(1, 1).Value = "As of";
        ws.Cell(1, 2).Value = c.AsOf.Date;
        ws.Cell(2, 1).Value = "Customer";
        ws.Cell(2, 2).Value = c.PartnerId is null ? "All customers" : c.CustomerName ?? c.PartnerId.Value.ToString();
        ws.Cell(3, 1).Value = "Aged by";
        ws.Cell(3, 2).Value = "Days past the due date";
        ws.Range(1, 1, 3, 1).Style.Font.Bold = true;
        ws.Cell(1, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Range(1, 2, 3, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        const int headerRow = 5;
        for (var i = 0; i < SummaryHeaders.Length; i++)
        {
            var cell = ws.Cell(headerRow, i + 1);
            cell.Value = SummaryHeaders[i];
            cell.Style.Font.Bold = true;
        }

        var row = headerRow + 1;
        foreach (var s in report.Customers)
        {
            ws.Cell(row, 1).Value = s.CustomerName ?? string.Empty;
            ws.Cell(row, 2).Value = s.CurrencyCode;
            ws.Cell(row, 3).Value = s.InvoiceCount;
            ws.Cell(row, 4).Value = s.Days0To30;
            ws.Cell(row, 5).Value = s.Days31To60;
            ws.Cell(row, 6).Value = s.Days61To90;
            ws.Cell(row, 7).Value = s.Over90;
            ws.Cell(row, 8).Value = s.Total;
            row++;
        }

        if (row > headerRow + 1)
            ws.Range(headerRow + 1, 4, row - 1, 8).Style.NumberFormat.Format = "#,##0.00";

        row++;
        foreach (var t in report.Totals)
        {
            ws.Cell(row, 1).Value = "Total";
            ws.Cell(row, 2).Value = t.CurrencyCode;
            ws.Cell(row, 3).Value = t.InvoiceCount;
            ws.Cell(row, 4).Value = t.Days0To30;
            ws.Cell(row, 5).Value = t.Days31To60;
            ws.Cell(row, 6).Value = t.Days61To90;
            ws.Cell(row, 7).Value = t.Over90;
            ws.Cell(row, 8).Value = t.Total;
            ws.Range(row, 1, row, 8).Style.Font.Bold = true;
            ws.Range(row, 4, row, 8).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }

        ws.Columns().AdjustToContents();
    }

    private static void WriteInvoices(IXLWorksheet ws, AgingReceivablesReport report)
    {
        for (var i = 0; i < InvoiceHeaders.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = InvoiceHeaders[i];
            cell.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var inv in report.Invoices)
        {
            ws.Cell(row, 1).Value  = inv.InvoiceNumber;
            ws.Cell(row, 2).Value  = inv.SaleOrderNumber;
            ws.Cell(row, 3).Value  = inv.CustomerName ?? string.Empty;
            ws.Cell(row, 4).Value  = inv.InvoiceDate.Date;
            ws.Cell(row, 5).Value  = inv.DueDate.Date;
            ws.Cell(row, 6).Value  = inv.DaysPastDue;
            ws.Cell(row, 7).Value  = inv.Bucket;
            ws.Cell(row, 8).Value  = inv.CurrencyCode;
            ws.Cell(row, 9).Value  = inv.GrandTotal;
            ws.Cell(row, 10).Value = inv.AmountPaid;
            ws.Cell(row, 11).Value = inv.Outstanding;
            row++;
        }

        if (row > 2)
        {
            ws.Range(2, 4, row - 1, 5).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Range(2, 9, row - 1, 11).Style.NumberFormat.Format = "#,##0.00";
        }

        ws.Columns().AdjustToContents();
    }
}
