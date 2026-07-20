using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SMS.Modules.Reports.Models;

namespace SMS.Modules.Reports.Services.Exports;

public static class SupplierLedgerSummaryPdfExporter
{
    private static readonly string[] Headers =
        { "Supplier", "Code", "Total Invoiced", "Total Paid", "Outstanding", "Advance", "Last Txn" };

    private static readonly double[] ColumnWidths = { 190, 80, 90, 90, 90, 80, 80 };

    private const double Margin     = 30;
    private const double RowHeight  = 16;
    private const double PageWidth  = 762;  // A4 landscape, points
    private const double PageHeight = 542;

    public static byte[] Export(SupplierLedgerSummaryReport report)
    {
        var document   = new PdfDocument();
        var titleFont   = new XFont("Verdana", 14, XFontStyle.Bold);
        var headerFont  = new XFont("Verdana", 9, XFontStyle.Bold);
        var cellFont    = new XFont("Verdana", 8, XFontStyle.Regular);

        var page = NewPage(document);
        var gfx  = XGraphics.FromPdfPage(page);
        double y = Margin;

        gfx.DrawString("Supplier-Wise Ledger Summary", titleFont, XBrushes.Black,
            new XRect(Margin, y, PageWidth - 2 * Margin, 20), XStringFormats.TopLeft);
        y += 22;

        gfx.DrawString($"Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC", cellFont, XBrushes.Gray,
            new XRect(Margin, y, PageWidth - 2 * Margin, 14), XStringFormats.TopLeft);
        y += 20;

        y = DrawRow(gfx, headerFont, y, Headers);

        foreach (var item in report.Items)
        {
            if (y > PageHeight - Margin - RowHeight)
            {
                page = NewPage(document);
                gfx  = XGraphics.FromPdfPage(page);
                y    = Margin;
                y    = DrawRow(gfx, headerFont, y, Headers);
            }

            y = DrawRow(gfx, cellFont, y, new[]
            {
                item.SupplierName,
                item.SupplierCode ?? string.Empty,
                item.TotalInvoiced.ToString("N2"),
                item.TotalPaid.ToString("N2"),
                item.OutstandingBalance.ToString("N2"),
                item.AdvanceBalance.ToString("N2"),
                item.LastTransactionDate?.ToString("yyyy-MM-dd") ?? string.Empty
            });
        }

        y += 6;
        gfx.DrawLine(XPens.Black, Margin, y, Margin + ColumnWidths.Sum(), y);
        y += 4;

        DrawRow(gfx, headerFont, y, new[]
        {
            "Grand Total", string.Empty,
            report.GrandTotalInvoiced.ToString("N2"),
            report.GrandTotalPaid.ToString("N2"),
            report.GrandTotalOutstanding.ToString("N2"),
            string.Empty, string.Empty
        });

        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    private static PdfPage NewPage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Width  = XUnit.FromPoint(PageWidth);
        page.Height = XUnit.FromPoint(PageHeight);
        return page;
    }

    private static double DrawRow(XGraphics gfx, XFont font, double y, string[] cells)
    {
        var x = Margin;
        for (var i = 0; i < cells.Length; i++)
        {
            gfx.DrawString(cells[i], font, XBrushes.Black,
                new XRect(x, y, ColumnWidths[i], RowHeight), XStringFormats.TopLeft);
            x += ColumnWidths[i];
        }
        return y + RowHeight;
    }
}
