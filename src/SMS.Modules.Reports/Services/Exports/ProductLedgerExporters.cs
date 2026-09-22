using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using static SMS.Modules.Reports.Services.Exports.ReportPdf;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>A29-P9-06 §15 R9 — a product's stock account as a landscape A4 PDF: where it stood, then every movement with the quantity and value held after it.</summary>
public static class ProductLedgerPdfExporter
{
    public static byte[] Export(ProductLedgerReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => Header(c, report.CompanyName, "PRODUCT LEDGER", CriteriaLines(report.Criteria)));
                page.Content().Element(c => ComposeContent(c, report));
                page.Footer().Element(c => Footer(c, report.GeneratedAt));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    /// <summary>What the report is of, in words.</summary>
    internal static IReadOnlyList<string> CriteriaLines(ProductLedgerReportCriteria c) =>
    [
        $"Product: {c.ProductName ?? c.ProductUuid.ToString()}",
        $"Variant: {(c.VariantUuid is null ? "All variants" : c.VariantName ?? c.VariantUuid.Value.ToString())}",
        $"Period: {Period(c.DateFrom, c.DateTo)}"
    ];

    internal static string Cost(decimal unitCost) => unitCost.ToString("N4", Culture);

    private static void ComposeContent(IContainer container, ProductLedgerReport report)
    {
        container.Column(column =>
        {
            ComposePosition(column, report.Summary);

            if (report.TotalRecords == 0)
            {
                Empty(column, "No movements in this period.");
                return;
            }

            ComposeMovements(column, report);
        });
    }

    /// <summary>Where the product stood, what came in and went out, and where it stands; each line is quantity, value and what a unit is worth.</summary>
    private static void ComposePosition(ColumnDescriptor column, ProductLedgerReportSummary s)
    {
        SectionTitle(column, "POSITION");

        column.Item().PaddingBottom(12).EnsureSpace(80).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(3);
                columns.RelativeColumn(1.2f);
                columns.RelativeColumn(1.4f);
                columns.RelativeColumn(1.2f);
            });

            table.Header(header =>
            {
                header.Cell().Element(SummaryHeadCell).Text(string.Empty);
                header.Cell().Element(SummaryHeadCell).AlignRight().Text("Quantity");
                header.Cell().Element(SummaryHeadCell).AlignRight().Text("Value");
                header.Cell().Element(SummaryHeadCell).AlignRight().Text("Avg cost");
            });

            void Line(string name, decimal qty, decimal value, bool bold)
            {
                var avg = qty <= 0m ? 0m : Math.Round(value / qty, 4, MidpointRounding.AwayFromZero);
                IContainer Style(IContainer c) => bold ? TotalCell(c) : SummaryCell(c);

                table.Cell().Element(Style).Text(name);
                table.Cell().Element(Style).AlignRight().Text(Qty(qty));
                table.Cell().Element(Style).AlignRight().Text(Money(value));
                table.Cell().Element(Style).AlignRight().Text(Cost(avg));
            }

            Line("Opening balance", s.OpeningQuantity, s.OpeningValue, bold: false);
            Line("Received", s.QuantityIn, s.ValueIn, bold: false);
            Line("Issued", s.QuantityOut, s.ValueOut, bold: false);
            Line("Closing balance", s.ClosingQuantity, s.ClosingValue, bold: true);
        });
    }

    private static void ComposeMovements(ColumnDescriptor column, ProductLedgerReport report)
    {
        SectionTitle(column, $"MOVEMENTS ({report.TotalRecords.ToString(Culture)})");

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(62);   // Date
                columns.RelativeColumn(1.6f); // Variant
                columns.ConstantColumn(68);   // Type
                columns.RelativeColumn(1.6f); // Reference
                columns.RelativeColumn(2f);   // Partner
                columns.RelativeColumn(0.8f); // In
                columns.RelativeColumn(0.8f); // Out
                columns.RelativeColumn(1f);   // Unit cost
                columns.RelativeColumn(1.1f); // Value
                columns.RelativeColumn(1f);   // Balance qty
                columns.RelativeColumn(1.3f); // Balance value
            });

            table.Header(header =>
            {
                header.Cell().Element(HeadCell).Text("Date");
                header.Cell().Element(HeadCell).Text("Variant");
                header.Cell().Element(HeadCell).Text("Type");
                header.Cell().Element(HeadCell).Text("Reference");
                header.Cell().Element(HeadCell).Text("Partner");
                header.Cell().Element(HeadCell).AlignRight().Text("In");
                header.Cell().Element(HeadCell).AlignRight().Text("Out");
                header.Cell().Element(HeadCell).AlignRight().Text("Unit cost");
                header.Cell().Element(HeadCell).AlignRight().Text("Value");
                header.Cell().Element(HeadCell).AlignRight().Text("Balance qty");
                header.Cell().Element(HeadCell).AlignRight().Text("Balance value");
            });

            for (var i = 0; i < report.Items.Count; i++)
            {
                var e         = report.Items[i];
                var alternate = i % 2 == 1;
                var isIn      = e.Direction == ProductLedgerDirections.In;

                table.Cell().Element(c => BodyCell(c, alternate)).Text(Date(e.EntryDate));
                table.Cell().Element(c => BodyCell(c, alternate)).Text(e.Sku ?? "-");
                table.Cell().Element(c => BodyCell(c, alternate)).Text(Label(e.EntryType));
                table.Cell().Element(c => BodyCell(c, alternate)).Text(e.ReferenceNumber);
                table.Cell().Element(c => BodyCell(c, alternate)).Text(e.PartnerName ?? "-");
                table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(isIn ? Qty(e.Quantity) : string.Empty);
                table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(isIn ? string.Empty : Qty(e.Quantity));
                table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Cost(e.UnitCost));
                table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(e.TotalCost));
                table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Qty(e.RunningQty));
                table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(e.RunningValue));
            }
        });
    }
}

/// <summary>A29-P9-06 §15 R9 — a product's stock account as a workbook: the movements on the first sheet, typed for summing and filtering, and what was asked for and where the product stood on the second.</summary>
public static class ProductLedgerExcelExporter
{
    private static readonly string[] Headers =
    [
        "Date", "SKU", "Variant", "Type", "Reference", "Partner", "Direction", "Qty In", "Qty Out", "Unit Cost", "Total Cost",
        "Running Qty", "Running Value", "Average Cost", "Variant Running Qty", "Variant Running Value", "Narration"
    ];

    public static byte[] Export(ProductLedgerReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        WriteMovements(workbook.Worksheets.Add("Product Ledger"), report);
        WriteSummary(workbook.Worksheets.Add("Summary"), report);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteMovements(IXLWorksheet ws, ProductLedgerReport report)
    {
        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var e in report.Items)
        {
            var isIn = e.Direction == ProductLedgerDirections.In;

            ws.Cell(row, 1).Value  = e.EntryDate.Date;
            ws.Cell(row, 2).Value  = e.Sku ?? string.Empty;
            ws.Cell(row, 3).Value  = e.VariantName ?? string.Empty;
            ws.Cell(row, 4).Value  = e.EntryType;
            ws.Cell(row, 5).Value  = e.ReferenceNumber;
            ws.Cell(row, 6).Value  = e.PartnerName ?? string.Empty;
            ws.Cell(row, 7).Value  = e.Direction;
            if (isIn) ws.Cell(row, 8).Value = e.Quantity; else ws.Cell(row, 9).Value = e.Quantity;
            ws.Cell(row, 10).Value = e.UnitCost;
            ws.Cell(row, 11).Value = e.TotalCost;
            ws.Cell(row, 12).Value = e.RunningQty;
            ws.Cell(row, 13).Value = e.RunningValue;
            ws.Cell(row, 14).Value = e.WeightedAverageCost;
            ws.Cell(row, 15).Value = e.VariantRunningQty;
            ws.Cell(row, 16).Value = e.VariantRunningValue;
            ws.Cell(row, 17).Value = e.Narration ?? string.Empty;
            row++;
        }

        if (row > 2)
        {
            ws.Range(2, 1, row - 1, 1).Style.DateFormat.Format = "yyyy-mm-dd";
            foreach (var c in new[] { 8, 9, 12, 15 }) ws.Range(2, c, row - 1, c).Style.NumberFormat.Format = "#,##0.####";
            ws.Range(2, 10, row - 1, 10).Style.NumberFormat.Format = "#,##0.0000";
            ws.Range(2, 14, row - 1, 14).Style.NumberFormat.Format = "#,##0.0000";
            foreach (var c in new[] { 11, 13, 16 }) ws.Range(2, c, row - 1, c).Style.NumberFormat.Format = "#,##0.00";
        }

        ws.Columns().AdjustToContents();
    }

    private static void WriteSummary(IXLWorksheet ws, ProductLedgerReport report)
    {
        var c = report.Criteria;

        ws.Cell(1, 1).Value = "Product";
        ws.Cell(1, 2).Value = c.ProductName ?? c.ProductUuid.ToString();
        ws.Cell(2, 1).Value = "Variant";
        ws.Cell(2, 2).Value = c.VariantUuid is null ? "All variants" : c.VariantName ?? c.VariantUuid.Value.ToString();
        ws.Cell(3, 1).Value = "From";
        if (c.DateFrom is { } from) ws.Cell(3, 2).Value = from.Date; else ws.Cell(3, 2).Value = "All dates";
        ws.Cell(4, 1).Value = "To";
        if (c.DateTo is { } to) ws.Cell(4, 2).Value = to.Date; else ws.Cell(4, 2).Value = "All dates";
        ws.Range(1, 1, 4, 1).Style.Font.Bold = true;
        ws.Range(3, 2, 4, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Range(1, 2, 4, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        const int headerRow = 6;
        string[] heads = ["Position", "Quantity", "Value", "Average Cost"];
        for (var i = 0; i < heads.Length; i++)
        {
            var cell = ws.Cell(headerRow, i + 1);
            cell.Value = heads[i];
            cell.Style.Font.Bold = true;
        }

        var s = report.Summary;
        var lines = new (string Name, decimal Qty, decimal Value)[]
        {
            ("Opening balance", s.OpeningQuantity, s.OpeningValue),
            ("Received", s.QuantityIn, s.ValueIn),
            ("Issued", s.QuantityOut, s.ValueOut),
            ("Closing balance", s.ClosingQuantity, s.ClosingValue)
        };

        var row = headerRow + 1;
        foreach (var (name, qty, value) in lines)
        {
            ws.Cell(row, 1).Value = name;
            ws.Cell(row, 2).Value = qty;
            ws.Cell(row, 3).Value = value;
            ws.Cell(row, 4).Value = qty <= 0m ? 0m : Math.Round(value / qty, 4, MidpointRounding.AwayFromZero);
            row++;
        }

        ws.Range(headerRow + 1, 2, row - 1, 2).Style.NumberFormat.Format = "#,##0.####";
        ws.Range(headerRow + 1, 3, row - 1, 3).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(headerRow + 1, 4, row - 1, 4).Style.NumberFormat.Format = "#,##0.0000";
        ws.Range(row - 1, 1, row - 1, 4).Style.Font.Bold = true;

        ws.Columns().AdjustToContents();
    }
}
