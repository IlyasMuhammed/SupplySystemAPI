using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Finance.Models;
using SMS.Modules.Lookups.Models;

namespace SMS.Modules.Finance.Services.Exports;

// Mirrors SMS.Modules.Demand.Services.PoDocumentService / SMS.Modules.Finance.Services
// .InvoiceDocumentService's letterhead so every generated document (PO, Invoice, Master Ledger)
// reads as the same product — same company letterhead source, same brand palette, same footer.
public static class MasterLedgerPdfExporter
{
    private const string BrandColor     = "#6C63FF";
    private const string BrandColorDark = "#4A42CC";

    public static byte[] Export(
        List<MasterLedgerEntryModel> entries, MasterLedgerSummaryModel summary,
        PoDocumentTemplateModel? template, byte[]? logoBytes, DateTime? dateFrom, DateTime? dateTo)
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
                    column.Item().Row(row =>
                    {
                        row.RelativeItem().Column(col =>
                        {
                            col.Item().Text(template?.CompanyName ?? "Company Name").FontSize(15).Bold().FontColor(BrandColorDark);
                            if (!string.IsNullOrWhiteSpace(template?.CompanyAddress))
                                col.Item().PaddingTop(2).Text(template.CompanyAddress).FontSize(7.5f);
                            var contactLine = string.Join("   |   ", new[]
                            {
                                !string.IsNullOrWhiteSpace(template?.CompanyPhone) ? $"Tel: {template.CompanyPhone}" : null,
                                !string.IsNullOrWhiteSpace(template?.CompanyEmail) ? $"Email: {template.CompanyEmail}" : null,
                                !string.IsNullOrWhiteSpace(template?.CompanyTaxId) ? $"Tax ID: {template.CompanyTaxId}" : null,
                            }.Where(s => s is not null));
                            if (!string.IsNullOrWhiteSpace(contactLine))
                                col.Item().PaddingTop(2).Text(contactLine).FontSize(7.5f);
                        });

                        if (logoBytes is not null)
                            row.ConstantItem(70).Height(42).Image(logoBytes).FitArea();
                    });

                    column.Item().PaddingTop(12).Text("MASTER PAYABLES LEDGER").FontSize(13).Bold().AlignCenter();

                    column.Item().PaddingTop(4).AlignCenter().Text(
                        dateFrom.HasValue || dateTo.HasValue
                            ? $"Period: {dateFrom?.ToString("dd MMM yyyy") ?? "Inception"} — {dateTo?.ToString("dd MMM yyyy") ?? "Date"}"
                            : "All Transactions"
                    ).FontSize(8).FontColor(Colors.Grey.Darken1);

                    column.Item().PaddingTop(10).Row(row =>
                    {
                        row.RelativeItem().Text($"Total Payables: {summary.TotalPayables:N2}").Bold();
                        row.RelativeItem().Text($"Total Debits: {summary.TotalDebits:N2}");
                        row.RelativeItem().Text($"Total Credits: {summary.TotalCredits:N2}");
                        row.RelativeItem().Text($"Net Movement: {summary.NetMovement:N2}");
                    });

                    column.Item().PaddingTop(8).LineHorizontal(1.5f).LineColor(BrandColor);
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
                            c.Background(BrandColor)
                             .DefaultTextStyle(x => x.Bold().FontColor(Colors.White))
                             .PaddingVertical(5).PaddingHorizontal(3);
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

                page.Footer().Column(column =>
                {
                    column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

                    if (!string.IsNullOrWhiteSpace(template?.FooterText))
                        column.Item().AlignCenter().Text(template.FooterText).FontSize(7).FontColor(Colors.Grey.Medium);

                    column.Item().PaddingTop(2).Row(row =>
                    {
                        row.RelativeItem().Text($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}").FontSize(7).FontColor(Colors.Grey.Medium);
                        row.RelativeItem().AlignRight().Text(text =>
                        {
                            text.CurrentPageNumber().FontSize(7).FontColor(Colors.Grey.Medium);
                            text.Span(" of ").FontSize(7).FontColor(Colors.Grey.Medium);
                            text.TotalPages().FontSize(7).FontColor(Colors.Grey.Medium);
                        });
                    });
                });
            });
        });

        return document.GeneratePdf();
    }
}