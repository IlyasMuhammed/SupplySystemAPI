using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Finance.Models;

namespace SMS.Modules.Finance.Services.Exports;

public static class MasterLedgerPdfExporter
{
    public static byte[] Export(List<MasterLedgerEntryModel> entries, MasterLedgerSummaryModel summary)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(30);
                page.DefaultTextStyle(x => x.FontSize(8));

                page.Header().Column(column =>
                {
                    column.Item().Text("Master Payables Ledger").FontSize(14).Bold();
                    column.Item().PaddingTop(2).Text($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(8).FontColor(Colors.Grey.Medium);

                    column.Item().PaddingTop(8).Row(row =>
                    {
                        row.RelativeItem().Text($"Total Payables: {summary.TotalPayables:N2}").Bold();
                        row.RelativeItem().Text($"Total Debits: {summary.TotalDebits:N2}");
                        row.RelativeItem().Text($"Total Credits: {summary.TotalCredits:N2}");
                        row.RelativeItem().Text($"Net Movement: {summary.NetMovement:N2}");
                    });

                    column.Item().PaddingTop(6).LineHorizontal(1);
                });

                page.Content().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(70);
                        columns.RelativeColumn(1.5f);
                        columns.RelativeColumn(1.3f);
                        columns.RelativeColumn(1.2f);
                        columns.ConstantColumn(65);
                        columns.ConstantColumn(65);
                        columns.ConstantColumn(70);
                        columns.RelativeColumn(2);
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Date");
                        header.Cell().Element(HeaderCell).Text("Supplier");
                        header.Cell().Element(HeaderCell).Text("Transaction Type");
                        header.Cell().Element(HeaderCell).Text("Reference No");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Debit");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Credit");
                        header.Cell().Element(HeaderCell).AlignRight().Text("Balance After");
                        header.Cell().Element(HeaderCell).Text("Narration");

                        static IContainer HeaderCell(IContainer c) =>
                            c.DefaultTextStyle(x => x.Bold()).PaddingVertical(4).BorderBottom(1);
                    });

                    foreach (var e in entries)
                    {
                        table.Cell().Element(BodyCell).Text(e.EntryDate.ToString("yyyy-MM-dd"));
                        table.Cell().Element(BodyCell).Text(e.SupplierName);
                        table.Cell().Element(BodyCell).Text(e.TransactionType);
                        table.Cell().Element(BodyCell).Text(e.ReferenceNo);
                        table.Cell().Element(BodyCell).AlignRight().Text(e.DebitAmount == 0 ? "-" : e.DebitAmount.ToString("N2"));
                        table.Cell().Element(BodyCell).AlignRight().Text(e.CreditAmount == 0 ? "-" : e.CreditAmount.ToString("N2"));
                        table.Cell().Element(BodyCell).AlignRight().Text(e.BalanceAfter.ToString("N2"));
                        table.Cell().Element(BodyCell).Text(e.Narration ?? string.Empty);

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