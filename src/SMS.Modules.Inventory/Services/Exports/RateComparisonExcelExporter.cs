using ClosedXML.Excel;
using SMS.Modules.Inventory.Models;

namespace SMS.Modules.Inventory.Services.Exports;

// RC-003 (FSD Addendum 28) — mirrors SMS.Modules.Finance.Services.Exports.MasterProductLedgerExcelExporter's
// shape exactly (same library, same style conventions).
public static class RateComparisonExcelExporter
{
    private static readonly string[] Headers =
    {
        "Supplier Name", "Vendor Part No", "Rate", "Rate vs Lowest", "Lead Days", "Min Qty",
        "Last PO Date", "Scorecard Grade", "Preferred"
    };

    public static byte[] Export(List<RateComparisonRowModel> rows)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Rate Comparison");

        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
        }

        var lowest = rows.Count > 0 ? rows.Min(r => r.VendorUnitCost) : 0m;

        var row = 2;
        foreach (var r in rows)
        {
            var premium = lowest > 0 ? (r.VendorUnitCost - lowest) / lowest : 0m;

            ws.Cell(row, 1).Value = r.SupplierName;
            ws.Cell(row, 2).Value = r.VendorPartNo ?? string.Empty;
            ws.Cell(row, 3).Value = r.VendorUnitCost;
            ws.Cell(row, 4).Value = r.VendorUnitCost == lowest ? "Lowest" : $"+{premium:P1}";
            ws.Cell(row, 5).Value = r.LeadTimeDays?.ToString() ?? string.Empty;
            ws.Cell(row, 6).Value = r.MinOrderValue?.ToString("N2") ?? string.Empty;
            if (r.LastPoDate.HasValue) ws.Cell(row, 7).Value = r.LastPoDate.Value; else ws.Cell(row, 7).Value = string.Empty;
            ws.Cell(row, 8).Value = r.ScorecardGrade ?? "-";
            ws.Cell(row, 9).Value = r.IsPreferred ? "Yes" : string.Empty;

            if (r.VendorUnitCost == lowest)
                ws.Range(row, 1, row, Headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#F0FDF4");

            row++;
        }

        var lastDataRow = row - 1;
        if (lastDataRow >= 2)
        {
            ws.Range(2, 3, lastDataRow, 3).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(2, 7, lastDataRow, 7).Style.DateFormat.Format = "yyyy-mm-dd";
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}
