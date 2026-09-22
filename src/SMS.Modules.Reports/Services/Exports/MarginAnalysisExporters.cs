using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using static SMS.Modules.Reports.Services.Exports.ReportPdf;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>
/// The columns of the margin analysis for one grouping, defined once so the PDF and the workbook cannot drift
/// apart: what each is called, how it is read off a row and off a currency's total, and how it lines up.
/// </summary>
internal sealed record MarginColumn(
    string Head,
    bool Numeric,
    float Weight,
    Func<MarginAnalysisItem, string> Text,
    Func<MarginAnalysisItem, object?> Value,
    string? Format,
    Func<MarginAnalysisTotal, string>? TotalText)
{
    /// <summary>The currency column is a fixed, narrow width; every other column shares what is left by its weight.</summary>
    internal bool IsCurrency => Weight == 0f;

    private static string Pct(decimal? percent) => percent is { } p ? p.ToString("0.00", Culture) + "%" : "-";

    internal static IReadOnlyList<MarginColumn> For(string grouping)
    {
        var (name, noun) = grouping switch
        {
            MarginGroupings.Customer => ("Customer", "customers"),
            MarginGroupings.Order    => ("Sale order", "orders"),
            _                        => ("Product", "products")
        };

        var columns = new List<MarginColumn>
        {
            new(name, false, 4f, i => i.Name ?? "-", i => i.Name ?? string.Empty, null, t => $"Total, {t.GroupCount.ToString(Culture)} {noun}")
        };

        if (grouping == MarginGroupings.Order)
            columns.Add(new("Customer", false, 3f, i => i.Detail ?? "-", i => i.Detail ?? string.Empty, null, null));

        columns.Add(new("Cur.", false, 0f, i => i.CurrencyCode, i => i.CurrencyCode, null, t => t.CurrencyCode));
        columns.Add(new("Lines", true, 0.7f, i => i.LineCount.ToString(Culture), i => i.LineCount, null, t => t.LineCount.ToString(Culture)));

        if (grouping == MarginGroupings.Product)
        {
            columns.Add(new("Units", true, 1f, i => Qty(i.Quantity ?? 0m), i => i.Quantity, "#,##0.####", null));
            columns.Add(new("Avg selling", true, 1f, i => i.AverageSellingPrice is { } p ? Money(p) : "-", i => i.AverageSellingPrice, "#,##0.00", null));
            columns.Add(new("Avg cost", true, 1f, i => i.AverageCost is { } c ? Money(c) : "-", i => i.AverageCost, "#,##0.00", null));
        }

        columns.Add(new("Selling value", true, 1.2f, i => Money(i.SellingValue), i => i.SellingValue, "#,##0.00", t => Money(t.SellingValue)));
        columns.Add(new("Cost", true, 1.2f, i => Money(i.Cost), i => i.Cost, "#,##0.00", t => Money(t.Cost)));
        columns.Add(new("Margin", true, 1.2f, i => Money(i.Margin), i => i.Margin, "#,##0.00", t => Money(t.Margin)));
        columns.Add(new("Margin %", true, 0.9f, i => Pct(i.MarginPercent), i => i.MarginPercent, "0.00", t => Pct(t.MarginPercent)));

        return columns;
    }
}

/// <summary>A29-P9-05 §15 R7 — the margin sale orders are expected to make, as a landscape A4 PDF: a row to a product, customer or order and currency, then a bold total per currency.</summary>
public static class MarginAnalysisPdfExporter
{
    public static byte[] Export(MarginAnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => Header(c, report.CompanyName, "MARGIN ANALYSIS", CriteriaLines(report.Criteria)));
                page.Content().Element(c => ComposeContent(c, report));
                page.Footer().Element(c => Footer(c, report.GeneratedAt));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    /// <summary>What the report is of, in words.</summary>
    internal static IReadOnlyList<string> CriteriaLines(MarginAnalysisCriteria c) =>
    [
        $"Orders dated: {Period(c.DateFrom, c.DateTo)}",
        $"Grouped by: {Label(c.GroupBy)}",
        "Selling price after discount against purchase order price, before tax"
    ];

    /// <summary>What the report leaves out, when it leaves something out; null when it leaves nothing.</summary>
    internal static string? UncostedNote(MarginAnalysisReport report)
    {
        var uncosted = report.Totals.Sum(t => t.UncostedLineCount);
        return uncosted == 0
            ? null
            : $"{uncosted.ToString(Culture)} order lines have no purchase order behind them, so there is no cost to set against them. They are not in these figures.";
    }

    private static void ComposeContent(IContainer container, MarginAnalysisReport report)
    {
        container.Column(column =>
        {
            var note = UncostedNote(report);

            if (report.TotalRecords == 0)
            {
                Empty(column, "No order lines with a purchase order behind them match these filters.");
                if (note is not null) column.Item().PaddingTop(6).AlignCenter().Text(note).FontSize(8).FontColor(Colors.Grey.Darken1);
                return;
            }

            var columns = MarginColumn.For(report.Criteria.GroupBy);

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(definition =>
                {
                    foreach (var c in columns)
                    {
                        if (c.IsCurrency) definition.ConstantColumn(34);
                        else definition.RelativeColumn(c.Weight);
                    }
                });

                table.Header(header =>
                {
                    foreach (var c in columns)
                    {
                        var cell = header.Cell().Element(HeadCell);
                        (c.Numeric ? cell.AlignRight() : cell).Text(c.Head);
                    }
                });

                for (var i = 0; i < report.Items.Count; i++)
                {
                    var item      = report.Items[i];
                    var alternate = i % 2 == 1;

                    foreach (var c in columns)
                    {
                        var cell = table.Cell().Element(x => BodyCell(x, alternate));
                        (c.Numeric ? cell.AlignRight() : cell).Text(c.Text(item));
                    }
                }

                foreach (var total in report.Totals.Where(t => t.GroupCount > 0))
                    foreach (var c in columns)
                    {
                        var cell = table.Cell().Element(TotalCell);
                        (c.Numeric ? cell.AlignRight() : cell).Text(c.TotalText?.Invoke(total) ?? string.Empty);
                    }
            });

            if (note is not null) column.Item().PaddingTop(8).Text(note).FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }
}

/// <summary>A29-P9-05 §15 R7 — the margin sale orders are expected to make, as a workbook: the rows on the first sheet, typed for summing and filtering, and what was asked for and the totals on the second.</summary>
public static class MarginAnalysisExcelExporter
{
    public static byte[] Export(MarginAnalysisReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var workbook = new XLWorkbook();
        WriteRows(workbook.Worksheets.Add("Margin Analysis"), report);
        WriteSummary(workbook.Worksheets.Add("Summary"), report);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteRows(IXLWorksheet ws, MarginAnalysisReport report)
    {
        var columns = MarginColumn.For(report.Criteria.GroupBy);

        for (var c = 0; c < columns.Count; c++)
        {
            var head = ws.Cell(1, c + 1);
            head.Value = columns[c].Head;
            head.Style.Font.Bold = true;
        }

        var row = 2;
        foreach (var item in report.Items)
        {
            for (var c = 0; c < columns.Count; c++)
                Set(ws.Cell(row, c + 1), columns[c].Value(item));
            row++;
        }

        if (row > 2)
            for (var c = 0; c < columns.Count; c++)
                if (columns[c].Format is { } format) ws.Range(2, c + 1, row - 1, c + 1).Style.NumberFormat.Format = format;

        ws.Columns().AdjustToContents();
    }

    private static void WriteSummary(IXLWorksheet ws, MarginAnalysisReport report)
    {
        var c = report.Criteria;

        ws.Cell(1, 1).Value = "Orders from";
        if (c.DateFrom is { } from) ws.Cell(1, 2).Value = from.Date; else ws.Cell(1, 2).Value = "All dates";
        ws.Cell(2, 1).Value = "Orders to";
        if (c.DateTo is { } to) ws.Cell(2, 2).Value = to.Date; else ws.Cell(2, 2).Value = "All dates";
        ws.Cell(3, 1).Value = "Grouped by";
        ws.Cell(3, 2).Value = Label(c.GroupBy);
        ws.Range(1, 1, 3, 1).Style.Font.Bold = true;
        ws.Range(1, 2, 2, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        ws.Range(1, 2, 3, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        const int headerRow = 5;
        string[] heads = ["Currency", "Groups", "Lines", "Lines Without Cost", "Selling Value", "Cost", "Margin", "Margin %"];
        for (var i = 0; i < heads.Length; i++)
        {
            var cell = ws.Cell(headerRow, i + 1);
            cell.Value = heads[i];
            cell.Style.Font.Bold = true;
        }

        var row = headerRow + 1;
        foreach (var t in report.Totals)
        {
            ws.Cell(row, 1).Value = t.CurrencyCode;
            ws.Cell(row, 2).Value = t.GroupCount;
            ws.Cell(row, 3).Value = t.LineCount;
            ws.Cell(row, 4).Value = t.UncostedLineCount;
            ws.Cell(row, 5).Value = t.SellingValue;
            ws.Cell(row, 6).Value = t.Cost;
            ws.Cell(row, 7).Value = t.Margin;
            if (t.MarginPercent is { } percent) ws.Cell(row, 8).Value = percent;
            row++;
        }

        if (row > headerRow + 1)
        {
            ws.Range(headerRow + 1, 5, row - 1, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Range(headerRow + 1, 8, row - 1, 8).Style.NumberFormat.Format = "0.00";
        }

        ws.Columns().AdjustToContents();
    }

    private static void Set(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:       break;
            case string s:   cell.Value = s; break;
            case int i:      cell.Value = i; break;
            case decimal d:  cell.Value = d; break;
            default: throw new InvalidOperationException($"A margin analysis cell of type {value.GetType().Name} has no workbook form.");
        }
    }
}
