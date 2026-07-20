using ClosedXML.Excel;
using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services.Exports;

public static class SupplierLedgerSummaryExcelExporter
{
    private static readonly string[] Headers =
        { "Supplier", "Code", "Total Invoiced", "Total Paid", "Outstanding Balance", "Advance Balance", "Last Transaction Date" };

    public static byte[] Export(SupplierLedgerSummaryReport report)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Supplier Ledger Summary");

        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var item in report.Items)
        {
            ws.Cell(row, 1).Value = item.SupplierName;
            ws.Cell(row, 2).Value = item.SupplierCode ?? string.Empty;
            ws.Cell(row, 3).Value = item.TotalInvoiced;
            ws.Cell(row, 4).Value = item.TotalPaid;
            ws.Cell(row, 5).Value = item.OutstandingBalance;
            ws.Cell(row, 6).Value = item.AdvanceBalance;
            if (item.LastTransactionDate.HasValue)
                ws.Cell(row, 7).Value = item.LastTransactionDate.Value;
            row++;
        }

        var lastDataRow = row - 1;
        if (lastDataRow >= 2)
        {
            ws.Range(2, 3, lastDataRow, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(2, 7, lastDataRow, 7).Style.DateFormat.Format = "yyyy-mm-dd";
        }

        ws.Cell(row, 1).Value = "Grand Total";
        ws.Cell(row, 3).Value = report.GrandTotalInvoiced;
        ws.Cell(row, 4).Value = report.GrandTotalPaid;
        ws.Cell(row, 5).Value = report.GrandTotalOutstanding;
        ws.Range(row, 1, row, 7).Style.Font.Bold = true;
        ws.Range(row, 3, row, 5).Style.NumberFormat.Format = "#,##0.00";

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}
