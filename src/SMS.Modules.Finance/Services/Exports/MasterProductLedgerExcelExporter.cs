using ClosedXML.Excel;
using SMS.Modules.Finance.Models;

namespace SMS.Modules.Finance.Services.Exports;

public static class MasterProductLedgerExcelExporter
{
    private static readonly string[] Headers =
    {
        "Date", "Product", "Category", "Transaction Type", "Qty In", "Qty Out", "Unit Cost",
        "Total Value", "Source Type", "Source", "Destination Type", "Destination",
        "Reference No", "Warehouse"
    };

    public static byte[] Export(List<MasterProductLedgerEntryModel> entries)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Master Product Ledger");

        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var e in entries)
        {
            ws.Cell(row, 1).Value  = e.TransactionDate;
            ws.Cell(row, 2).Value  = e.ProductName;
            ws.Cell(row, 3).Value  = e.CategoryName ?? string.Empty;
            ws.Cell(row, 4).Value  = e.TransactionType;
            ws.Cell(row, 5).Value  = e.QuantityIn ?? 0m;
            ws.Cell(row, 6).Value  = e.QuantityOut ?? 0m;
            ws.Cell(row, 7).Value  = e.UnitCost;
            ws.Cell(row, 8).Value  = e.TotalValue;
            ws.Cell(row, 9).Value  = e.SourceType;
            ws.Cell(row, 10).Value = e.SourceName ?? string.Empty;
            ws.Cell(row, 11).Value = e.DestinationType;
            ws.Cell(row, 12).Value = e.DestinationName ?? string.Empty;
            ws.Cell(row, 13).Value = e.ReferenceNumber;
            ws.Cell(row, 14).Value = e.WarehouseName;
            row++;
        }

        var lastDataRow = row - 1;
        if (lastDataRow >= 2)
        {
            ws.Range(2, 1, lastDataRow, 1).Style.DateFormat.Format = "yyyy-mm-dd HH:mm";
            ws.Range(2, 5, lastDataRow, 8).Style.NumberFormat.Format = "#,##0.00";
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}
