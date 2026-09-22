using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using static SMS.Modules.Reports.Services.Exports.ReportPdf;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>
/// A29-P9-02 §15 R2 — one customer's account as a landscape A4 statement: what they owed when the period
/// began, every entry in it with the balance after each, and what they owed when it ended.
/// </summary>
public static class CustomerLedgerPdfExporter
{
    public static byte[] Export(CustomerLedgerReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => Header(c, report.CompanyName, "CUSTOMER LEDGER", CriteriaLines(report.Criteria)));
                page.Content().Element(c => ComposeContent(c, report));
                page.Footer().Element(c => Footer(c, report.GeneratedAt));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    /// <summary>What the statement is of, in words.</summary>
    internal static IReadOnlyList<string> CriteriaLines(CustomerLedgerReportCriteria c)
    {
        var period = (c.DateFrom, c.DateTo) switch
        {
            (null, null)   => "All dates",
            ({ } f, null)  => $"From {Date(f)}",
            (null, { } t)  => $"Up to {Date(t)}",
            ({ } f, { } t) => $"{Date(f)} to {Date(t)}"
        };

        return
        [
            $"Customer: {c.CustomerName ?? c.PartnerId?.ToString() ?? "-"}",
            $"Period: {period}",
            "Balances by business date"
        ];
    }

    private static void ComposeContent(IContainer container, CustomerLedgerReport report)
    {
        container.Column(column =>
        {
            if (report.Summaries.Count > 0)
                ComposeSummary(column, report);

            if (report.Entries.Count == 0)
            {
                Empty(column, "No ledger entries in this period.");
                return;
            }

            SectionTitle(column, "ENTRIES");

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(56);   // Date
                    columns.ConstantColumn(74);   // Type
                    columns.ConstantColumn(104);  // Reference
                    columns.RelativeColumn(3);    // Narration
                    columns.ConstantColumn(30);   // Currency
                    columns.RelativeColumn(1);    // Debit
                    columns.RelativeColumn(1);    // Credit
                    columns.RelativeColumn(1.1f); // Balance
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeadCell).Text("Date");
                    header.Cell().Element(HeadCell).Text("Type");
                    header.Cell().Element(HeadCell).Text("Reference");
                    header.Cell().Element(HeadCell).Text("Narration");
                    header.Cell().Element(HeadCell).Text("Cur.");
                    header.Cell().Element(HeadCell).AlignRight().Text("Debit");
                    header.Cell().Element(HeadCell).AlignRight().Text("Credit");
                    header.Cell().Element(HeadCell).AlignRight().Text("Balance");
                });

                for (var i = 0; i < report.Entries.Count; i++)
                {
                    var entry     = report.Entries[i];
                    var alternate = i % 2 == 1;

                    table.Cell().Element(c => BodyCell(c, alternate)).Text(Date(entry.EntryDate));
                    table.Cell().Element(c => BodyCell(c, alternate)).Text(Label(entry.EntryType));
                    table.Cell().Element(c => BodyCell(c, alternate)).Text(entry.ReferenceNumber);
                    table.Cell().Element(c => BodyCell(c, alternate)).Text(entry.Narration ?? "-");
                    table.Cell().Element(c => BodyCell(c, alternate)).Text(entry.CurrencyCode);
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(entry.DebitAmount == 0m ? "" : Money(entry.DebitAmount));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(entry.CreditAmount == 0m ? "" : Money(entry.CreditAmount));
                    table.Cell().Element(c => BodyCell(c, alternate)).AlignRight().Text(Money(entry.Balance));
                }
            });
        });
    }

    /// <summary>What the account did over the period, a row to a currency: where it began, what moved, where it ended.</summary>
    private static void ComposeSummary(ColumnDescriptor column, CustomerLedgerReport report)
    {
        SectionTitle(column, "SUMMARY");

        column.Item().PaddingBottom(10).EnsureSpace(60).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(60);
                columns.RelativeColumn(1);
                columns.RelativeColumn(1);
                columns.RelativeColumn(1);
                columns.RelativeColumn(1);
                columns.ConstantColumn(60);
            });

            table.Header(header =>
            {
                header.Cell().Element(SummaryHeadCell).Text("Currency");
                header.Cell().Element(SummaryHeadCell).AlignRight().Text("Opening balance");
                header.Cell().Element(SummaryHeadCell).AlignRight().Text("Debits");
                header.Cell().Element(SummaryHeadCell).AlignRight().Text("Credits");
                header.Cell().Element(SummaryHeadCell).AlignRight().Text("Closing balance");
                header.Cell().Element(SummaryHeadCell).AlignRight().Text("Entries");
            });

            foreach (var s in report.Summaries)
            {
                table.Cell().Element(SummaryCell).Text(s.CurrencyCode).Bold();
                table.Cell().Element(SummaryCell).AlignRight().Text(Money(s.OpeningBalance));
                table.Cell().Element(SummaryCell).AlignRight().Text(Money(s.TotalDebit));
                table.Cell().Element(SummaryCell).AlignRight().Text(Money(s.TotalCredit));
                table.Cell().Element(SummaryCell).AlignRight().Text(Money(s.ClosingBalance)).Bold();
                table.Cell().Element(SummaryCell).AlignRight().Text(s.EntryCount.ToString(Culture));
            }
        });
    }
}
