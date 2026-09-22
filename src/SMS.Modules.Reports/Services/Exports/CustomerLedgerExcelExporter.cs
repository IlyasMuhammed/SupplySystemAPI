using ClosedXML.Excel;
using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>
/// A29-P9-02 §15 R2 — the customer ledger as a workbook: the entries on the first sheet, one row each with
/// typed amounts and dates so it can be summed, sorted and filtered, and what the customer was asked for
/// and what the account did over it on the second.
/// </summary>
public static class CustomerLedgerExcelExporter
{
    private static readonly string[] EntryHeaders =
        ["Date", "Entry Type", "Reference", "Narration", "Currency", "Debit", "Credit", "Balance"];

    private static readonly string[] SummaryHeaders =
        ["Currency", "Opening Balance", "Total Debit", "Total Credit", "Closing Balance", "Entries"];

    public static byte[] Export(CustomerLedgerReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        WriteEntries(workbook.Worksheets.Add("Customer Ledger"), report);
        WriteSummary(workbook.Worksheets.Add("Summary"), report);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteEntries(IXLWorksheet ws, CustomerLedgerReport report)
    {
        for (var i = 0; i < EntryHeaders.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = EntryHeaders[i];
            cell.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var e in report.Entries)
        {
            ws.Cell(row, 1).Value = e.EntryDate.Date;
            ws.Cell(row, 2).Value = e.EntryType;
            ws.Cell(row, 3).Value = e.ReferenceNumber;
            ws.Cell(row, 4).Value = e.Narration ?? string.Empty;
            ws.Cell(row, 5).Value = e.CurrencyCode;
            ws.Cell(row, 6).Value = e.DebitAmount;
            ws.Cell(row, 7).Value = e.CreditAmount;
            ws.Cell(row, 8).Value = e.Balance;
            row++;
        }

        if (row > 2)
        {
            ws.Range(2, 1, row - 1, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Range(2, 6, row - 1, 8).Style.NumberFormat.Format = "#,##0.00";
        }

        ws.Columns().AdjustToContents();
    }

    private static void WriteSummary(IXLWorksheet ws, CustomerLedgerReport report)
    {
        var c = report.Criteria;

        ws.Cell(1, 1).Value = "Customer";
        ws.Cell(1, 2).Value = c.CustomerName ?? c.PartnerId?.ToString() ?? string.Empty;
        ws.Cell(2, 1).Value = "From";
        if (c.DateFrom is { } from) ws.Cell(2, 2).Value = from.Date; else ws.Cell(2, 2).Value = "All dates";
        ws.Cell(3, 1).Value = "To";
        if (c.DateTo is { } to) ws.Cell(3, 2).Value = to.Date; else ws.Cell(3, 2).Value = "All dates";
        ws.Range(1, 1, 3, 1).Style.Font.Bold = true;
        ws.Range(2, 2, 3, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Range(2, 2, 3, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        const int headerRow = 5;
        for (var i = 0; i < SummaryHeaders.Length; i++)
        {
            var cell = ws.Cell(headerRow, i + 1);
            cell.Value = SummaryHeaders[i];
            cell.Style.Font.Bold = true;
        }

        var row = headerRow + 1;
        foreach (var s in report.Summaries)
        {
            ws.Cell(row, 1).Value = s.CurrencyCode;
            ws.Cell(row, 2).Value = s.OpeningBalance;
            ws.Cell(row, 3).Value = s.TotalDebit;
            ws.Cell(row, 4).Value = s.TotalCredit;
            ws.Cell(row, 5).Value = s.ClosingBalance;
            ws.Cell(row, 6).Value = s.EntryCount;
            row++;
        }

        if (row > headerRow + 1)
            ws.Range(headerRow + 1, 2, row - 1, 5).Style.NumberFormat.Format = "#,##0.00";

        ws.Columns().AdjustToContents();
    }
}
