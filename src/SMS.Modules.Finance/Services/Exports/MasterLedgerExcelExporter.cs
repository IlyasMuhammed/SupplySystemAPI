using ClosedXML.Excel;
using SMS.Modules.Finance.Models;

namespace SMS.Modules.Finance.Services.Exports;

public static class MasterLedgerExcelExporter
{
    private static readonly string[] Headers =
        { "Date", "Supplier", "Transaction Type", "Reference No", "Debit", "Credit", "Balance After", "Narration" };

    public static byte[] Export(List<MasterLedgerEntryModel> entries)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Master Payables Ledger");

        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var e in entries)
        {
            ws.Cell(row, 1).Value = e.EntryDate;
            ws.Cell(row, 2).Value = e.SupplierName;
            ws.Cell(row, 3).Value = e.TransactionType;
            ws.Cell(row, 4).Value = e.ReferenceNo;
            ws.Cell(row, 5).Value = e.DebitAmount;
            ws.Cell(row, 6).Value = e.CreditAmount;
            ws.Cell(row, 7).Value = e.BalanceAfter;
            ws.Cell(row, 8).Value = e.Narration ?? string.Empty;
            row++;
        }

        var lastDataRow = row - 1;
        if (lastDataRow >= 2)
        {
            ws.Range(2, 1, lastDataRow, 1).Style.DateFormat.Format = "yyyy-mm-dd HH:mm";
            ws.Range(2, 5, lastDataRow, 7).Style.NumberFormat.Format = "#,##0.00";
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}