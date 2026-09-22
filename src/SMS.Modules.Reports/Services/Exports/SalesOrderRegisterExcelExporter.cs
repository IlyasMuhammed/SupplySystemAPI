using ClosedXML.Excel;
using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>
/// A29-P9-01 §15 R1 — the sale order register as a workbook. Amounts and dates are written as numbers and
/// dates, not text, so the sheet can be summed, sorted and filtered. Because the orders can be in more
/// than one currency there is no single grand total row to write; the per-currency totals sit in their own block below the orders.
/// </summary>
public static class SalesOrderRegisterExcelExporter
{
    private static readonly string[] Headers =
    [
        "SO Number", "Order Date", "Expected Delivery", "Customer", "Status", "Delivery Mode",
        "Currency", "Lines", "Subtotal", "Discount", "Tax", "Total"
    ];

    private const int AmountFirstColumn = 9;
    private const int AmountLastColumn  = 12;

    public static byte[] Export(SalesOrderRegisterReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Sales Order Register");

        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var item in report.Items)
        {
            ws.Cell(row, 1).Value = item.SoNumber;
            ws.Cell(row, 2).Value = item.OrderDate.Date;
            if (item.ExpectedDeliveryDate is { } expected)
                ws.Cell(row, 3).Value = expected.Date;
            ws.Cell(row, 4).Value = item.CustomerName ?? string.Empty;
            ws.Cell(row, 5).Value = item.Status;
            ws.Cell(row, 6).Value = item.DeliveryMode;
            ws.Cell(row, 7).Value = item.CurrencyCode;
            ws.Cell(row, 8).Value = item.LineCount;
            ws.Cell(row, 9).Value = item.Subtotal;
            ws.Cell(row, 10).Value = item.DiscountAmount;
            ws.Cell(row, 11).Value = item.TaxAmount;
            ws.Cell(row, 12).Value = item.GrandTotal;
            row++;
        }

        var lastDataRow = row - 1;
        if (lastDataRow >= 2)
        {
            ws.Range(2, 2, lastDataRow, 3).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Range(2, AmountFirstColumn, lastDataRow, AmountLastColumn).Style.NumberFormat.Format = "#,##0.00";
        }

        row++;
        ws.Cell(row, 1).Value = $"Totals for {report.TotalRecords} orders";
        ws.Cell(row, 1).Style.Font.Bold = true;
        row++;

        foreach (var total in report.Totals)
        {
            ws.Cell(row, 1).Value = "Total";
            ws.Cell(row, 7).Value = total.CurrencyCode;
            ws.Cell(row, 8).Value = total.OrderCount;
            ws.Cell(row, 9).Value = total.Subtotal;
            ws.Cell(row, 10).Value = total.DiscountAmount;
            ws.Cell(row, 11).Value = total.TaxAmount;
            ws.Cell(row, 12).Value = total.GrandTotal;
            ws.Range(row, 1, row, AmountLastColumn).Style.Font.Bold = true;
            ws.Range(row, AmountFirstColumn, row, AmountLastColumn).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}
