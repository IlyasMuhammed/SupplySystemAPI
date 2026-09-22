using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using static SMS.Modules.Reports.Services.Exports.ReportPdf;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>
/// A29-P9-04 §15 R6 — the open sale-order deliveries as a landscape A4 PDF: how many there are by status,
/// by warehouse and by delivery mode, then each of them, oldest first.
/// </summary>
public static class FulfilmentStatusPdfExporter
{
    public static byte[] Export(FulfilmentStatusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => Header(c, report.CompanyName, "FULFILMENT STATUS", CriteriaLines(report.Criteria)));
                page.Content().Element(c => ComposeContent(c, report));
                page.Footer().Element(c => Footer(c, report.GeneratedAt));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    /// <summary>What the report is of, in words.</summary>
    internal static IReadOnlyList<string> CriteriaLines(FulfilmentStatusCriteria c) =>
    [
        $"Status: {(c.Status is null ? "All open" : Label(c.Status))}",
        $"Warehouse: {(c.WarehouseUuid is null ? "All warehouses" : c.WarehouseName ?? c.WarehouseUuid.Value.ToString())}",
        $"Delivery mode: {(c.DeliveryMode is null ? "All" : Label(c.DeliveryMode))}"
    ];

    private static void ComposeContent(IContainer container, FulfilmentStatusReport report)
    {
        container.Column(column =>
        {
            if (report.TotalRecords == 0)
            {
                Empty(column, "No open deliveries match these filters.");
                return;
            }

            ComposeCounts(column, report);
            ComposeDeliveries(column, report);
        });
    }

    /// <summary>The same open deliveries counted three ways, side by side; each column adds up to the same number.</summary>
    private static void ComposeCounts(ColumnDescriptor column, FulfilmentStatusReport report)
    {
        SectionTitle(column, $"OPEN DELIVERIES ({report.TotalRecords.ToString(Culture)})");

        column.Item().PaddingBottom(12).EnsureSpace(90).Row(row =>
        {
            row.RelativeItem().Element(c => CountTable(c, "Status", report.ByStatus.Select(s => (Label(s.Status), s.Count))));
            row.ConstantItem(16);
            row.RelativeItem().Element(c => CountTable(c, "Warehouse", report.ByWarehouse.Select(w => (w.WarehouseName ?? "-", w.Count))));
            row.ConstantItem(16);
            row.RelativeItem().Element(c => CountTable(c, "Delivery mode", report.ByDeliveryMode.Select(m => (m.DeliveryMode.Length == 0 ? "-" : Label(m.DeliveryMode), m.Count))));
        });
    }

    private static void CountTable(IContainer container, string title, IEnumerable<(string Name, int Count)> rows) =>
        container.Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(3);
                columns.ConstantColumn(46);
            });

            table.Header(header =>
            {
                header.Cell().Element(SummaryHeadCell).Text(title);
                header.Cell().Element(SummaryHeadCell).AlignRight().Text("Open");
            });

            foreach (var (name, count) in rows)
            {
                table.Cell().Element(SummaryCell).Text(name);
                table.Cell().Element(SummaryCell).AlignRight().Text(count.ToString(Culture));
            }
        });

    private static void ComposeDeliveries(ColumnDescriptor column, FulfilmentStatusReport report)
    {
        SectionTitle(column, "DELIVERIES, OLDEST FIRST");

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(84);   // Delivery
                columns.ConstantColumn(80);   // Sale order
                columns.RelativeColumn(2f);   // Customer
                columns.ConstantColumn(104);  // Status, wide enough for PARTIALLY DELIVERED on one line
                columns.ConstantColumn(62);   // Delivery mode
                columns.RelativeColumn(1.5f); // Warehouse
                columns.ConstantColumn(58);   // Promised
                columns.ConstantColumn(32);   // Days open
                columns.ConstantColumn(44);   // Ordered
                columns.ConstantColumn(44);   // Delivered
            });

            table.Header(header =>
            {
                header.Cell().Element(HeadCell).Text("Delivery");
                header.Cell().Element(HeadCell).Text("Sale order");
                header.Cell().Element(HeadCell).Text("Customer");
                header.Cell().Element(HeadCell).Text("Status");
                header.Cell().Element(HeadCell).Text("Mode");
                header.Cell().Element(HeadCell).Text("Warehouse");
                header.Cell().Element(HeadCell).Text("Promised");
                header.Cell().Element(HeadCell).AlignRight().Text("Days");
                header.Cell().Element(HeadCell).AlignRight().Text("Ordered");
                header.Cell().Element(HeadCell).AlignRight().Text("Delivered");
            });

            for (var i = 0; i < report.Items.Count; i++)
            {
                var item      = report.Items[i];
                var alternate = i % 2 == 1;

                table.Cell().Element(c => BodyCell(c, alternate)).Text(item.DeliveryNumber);
                table.Cell().Element(c => BodyCell(c, alternate)).Text(item.SaleOrderNumber ?? "-");
                table.Cell().Element(c => BodyCell(c, alternate)).Text(item.CustomerName ?? "-");
                table.Cell().Element(c => BodyCell(c, alternate)).Text(Label(item.Status));
                table.Cell().Element(c => BodyCell(c, alternate)).Text(item.DeliveryMode is null ? "-" : Label(item.DeliveryMode));
                table.Cell().Element(c => BodyCell(c, alternate)).Text(item.WarehouseName ?? "-");
                table.Cell().Element(c => BodyCell(c, alternate)).Text(item.PromisedDate is { } promised ? Date(promised) : "-");
                table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(item.DaysOpen.ToString(Culture));
                table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Qty(item.QuantityOrdered));
                table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Qty(item.QuantityDelivered));
            }
        });
    }
}

/// <summary>A29-P9-04 §15 R6 — the open sale-order deliveries as a workbook: the deliveries on the first sheet, typed for filtering, and what was asked for and the three counts on the second.</summary>
public static class FulfilmentStatusExcelExporter
{
    private static readonly string[] Headers =
    [
        "Delivery", "Sale Order", "Customer", "Status", "Delivery Mode", "Warehouse", "Requested", "Promised", "Created",
        "Days Open", "Lines", "Qty Ordered", "Qty Delivered"
    ];

    public static byte[] Export(FulfilmentStatusReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        WriteDeliveries(workbook.Worksheets.Add("Open Fulfilments"), report);
        WriteSummary(workbook.Worksheets.Add("Summary"), report);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteDeliveries(IXLWorksheet ws, FulfilmentStatusReport report)
    {
        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var d in report.Items)
        {
            ws.Cell(row, 1).Value = d.DeliveryNumber;
            ws.Cell(row, 2).Value = d.SaleOrderNumber ?? string.Empty;
            ws.Cell(row, 3).Value = d.CustomerName ?? string.Empty;
            ws.Cell(row, 4).Value = d.Status;
            ws.Cell(row, 5).Value = d.DeliveryMode ?? string.Empty;
            ws.Cell(row, 6).Value = d.WarehouseName ?? string.Empty;
            if (d.RequestedDate is { } requested) ws.Cell(row, 7).Value = requested.Date;
            if (d.PromisedDate is { } promised) ws.Cell(row, 8).Value = promised.Date;
            ws.Cell(row, 9).Value  = d.CreatedDate.Date;
            ws.Cell(row, 10).Value = d.DaysOpen;
            ws.Cell(row, 11).Value = d.LineCount;
            ws.Cell(row, 12).Value = d.QuantityOrdered;
            ws.Cell(row, 13).Value = d.QuantityDelivered;
            row++;
        }

        if (row > 2)
        {
            ws.Range(2, 7, row - 1, 9).Style.DateFormat.Format = "yyyy-mm-dd";
            ws.Range(2, 12, row - 1, 13).Style.NumberFormat.Format = "#,##0.####";
        }

        ws.Columns().AdjustToContents();
    }

    private static void WriteSummary(IXLWorksheet ws, FulfilmentStatusReport report)
    {
        var c = report.Criteria;

        ws.Cell(1, 1).Value = "Status";
        ws.Cell(1, 2).Value = c.Status ?? "All open";
        ws.Cell(2, 1).Value = "Warehouse";
        ws.Cell(2, 2).Value = c.WarehouseUuid is null ? "All warehouses" : c.WarehouseName ?? c.WarehouseUuid.Value.ToString();
        ws.Cell(3, 1).Value = "Delivery mode";
        ws.Cell(3, 2).Value = c.DeliveryMode ?? "All";
        ws.Cell(4, 1).Value = "Open deliveries";
        ws.Cell(4, 2).Value = report.TotalRecords;
        ws.Range(1, 1, 4, 1).Style.Font.Bold = true;
        ws.Range(1, 2, 4, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        var row = 6;
        row = Block(ws, row, "By status", "Status", report.ByStatus.Select(s => (s.Status, s.Count)));
        row = Block(ws, row + 1, "By warehouse", "Warehouse", report.ByWarehouse.Select(w => (w.WarehouseName ?? string.Empty, w.Count)));
        Block(ws, row + 1, "By delivery mode", "Delivery mode", report.ByDeliveryMode.Select(m => (m.DeliveryMode, m.Count)));

        ws.Columns().AdjustToContents();
    }

    /// <summary>A titled two-column count table; returns the row after its last.</summary>
    private static int Block(IXLWorksheet ws, int row, string title, string heading, IEnumerable<(string Name, int Count)> rows)
    {
        ws.Cell(row, 1).Value = title;
        ws.Cell(row, 1).Style.Font.Bold = true;
        row++;

        ws.Cell(row, 1).Value = heading;
        ws.Cell(row, 2).Value = "Open";
        ws.Range(row, 1, row, 2).Style.Font.Bold = true;
        row++;

        foreach (var (name, count) in rows)
        {
            ws.Cell(row, 1).Value = name;
            ws.Cell(row, 2).Value = count;
            row++;
        }

        return row;
    }
}
