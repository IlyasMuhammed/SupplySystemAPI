using ClosedXML.Excel;
using SMS.Modules.Inventory.Models;

namespace SMS.Modules.Inventory.Services.Exports;

// RC-006 (FSD Addendum 28) — mirrors RateComparisonExcelExporter's shape exactly. Column order and
// names are also what VariantSupplierService's import parser reads back by header name, so the
// exported file round-trips as an editable rate card without any manual reformatting.
public static class RateCardExcelExporter
{
    private static readonly string[] Headers =
    {
        "Product Code", "Product Name", "Variant SKU", "Variant Name", "Vendor Part No",
        "Current Rate", "Lead Days", "Min Qty", "Effective From", "Effective To", "Notes"
    };

    public static byte[] Export(List<RateCardExportRowModel> rows)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Rate Card");

        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var r in rows)
        {
            ws.Cell(row, 1).Value = r.ProductCode;
            ws.Cell(row, 2).Value = r.ProductName;
            ws.Cell(row, 3).Value = r.VariantSku;
            ws.Cell(row, 4).Value = r.VariantName;
            ws.Cell(row, 5).Value = r.VendorPartNo ?? string.Empty;
            ws.Cell(row, 6).Value = r.CurrentRate;
            ws.Cell(row, 7).Value = r.LeadDays?.ToString() ?? string.Empty;
            ws.Cell(row, 8).Value = r.MinQty?.ToString() ?? string.Empty;
            ws.Cell(row, 9).Value = r.EffectiveFrom;
            if (r.EffectiveTo.HasValue) ws.Cell(row, 10).Value = r.EffectiveTo.Value; else ws.Cell(row, 10).Value = string.Empty;
            ws.Cell(row, 11).Value = r.Notes ?? string.Empty;

            row++;
        }

        var lastDataRow = row - 1;
        if (lastDataRow >= 2)
        {
            ws.Range(2, 6, lastDataRow, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(2, 9, lastDataRow, 10).Style.DateFormat.Format = "yyyy-mm-dd";
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }
}
