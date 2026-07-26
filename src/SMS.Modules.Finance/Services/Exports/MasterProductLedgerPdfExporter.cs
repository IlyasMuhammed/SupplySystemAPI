using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Finance.Models;

namespace SMS.Modules.Finance.Services.Exports;

public static class MasterProductLedgerPdfExporter
{
    public static byte[] Export(List<MasterProductLedgerEntryModel> entries, MasterProductLedgerSummaryModel summary)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(7));

                page.Header().Column(column =>
                {
                    column.Item().Text("Master Product Movement Ledger").FontSize(14).Bold();
                    column.Item().PaddingTop(2).Text($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(8).FontColor(Colors.Grey.Medium);

                    column.Item().PaddingTop(8).Row(row =>
                    {
                        row.RelativeItem().Text($"Total Receipts: {summary.TotalReceiptsQty:N2}").Bold();
                        row.RelativeItem().Text($"Total Issues: {summary.TotalIssuesQty:N2}");
                        row.RelativeItem().Text($"Net Movement: {summary.NetMovement:N2}");
                        row.RelativeItem().Text($"Total Value Moved: {summary.TotalValueMoved:N2}");
                    });

                    column.Item().PaddingTop(6).LineHorizontal(1);
                });

                page.Content().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(60);
                        columns.RelativeColumn(1.4f);
                        columns.RelativeColumn(1.1f);
                        columns.RelativeColumn(1.1f);
                        columns.ConstantColumn(45);
                        columns.ConstantColumn(45);
                        columns.ConstantColumn(50);
                        columns.RelativeColumn(1.2f);
                        columns.RelativeColumn(1.2f);
                        columns.RelativeColumn(1.1f);
                        columns.RelativeColumn(1.1f);
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Date");
                        header.Cell().Element(HeaderCell).Text("Product");
                        header.Cell().Element(HeaderCell).Text("Type");
                        header.Cell().Element(HeaderCell).Text("Warehouse");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Qty In");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Qty Out");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Value");
                        header.Cell().Element(HeaderCell).Text("Source");
                        header.Cell().Element(HeaderCell).Text("Destination");
                        header.Cell().Element(HeaderCell).Text("Reference");
                        header.Cell().Element(HeaderCell).Text("Notes");

                        static IContainer HeaderCell(IContainer c) =>
                            c.DefaultTextStyle(x => x.Bold()).PaddingVertical(4).BorderBottom(1);
                    });

                    foreach (var e in entries)
                    {
                        table.Cell().Element(BodyCell).Text(e.TransactionDate.ToString("yyyy-MM-dd"));
                        table.Cell().Element(BodyCell).Text(e.ProductName);
                        table.Cell().Element(BodyCell).Text(e.TransactionType);
                        table.Cell().Element(BodyCell).Text(e.WarehouseName);
                        table.Cell().Element(BodyCell).AlignRight().Text(e.QuantityIn is > 0 ? e.QuantityIn.Value.ToString("N2") : "-");
                        table.Cell().Element(BodyCell).AlignRight().Text(e.QuantityOut is > 0 ? e.QuantityOut.Value.ToString("N2") : "-");
                        table.Cell().Element(BodyCell).AlignRight().Text(e.TotalValue.ToString("N2"));
                        table.Cell().Element(BodyCell).Text($"{e.SourceType}: {e.SourceName}");
                        table.Cell().Element(BodyCell).Text($"{e.DestinationType}: {e.DestinationName}");
                        table.Cell().Element(BodyCell).Text(e.ReferenceNumber);
                        table.Cell().Element(BodyCell).Text(e.Notes ?? string.Empty);

                        static IContainer BodyCell(IContainer c) =>
                            c.PaddingVertical(3).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);
                    }
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.CurrentPageNumber().FontSize(7);
                    text.Span(" / ").FontSize(7);
                    text.TotalPages().FontSize(7);
                });
            });
        });

        return document.GeneratePdf();
    }
}
