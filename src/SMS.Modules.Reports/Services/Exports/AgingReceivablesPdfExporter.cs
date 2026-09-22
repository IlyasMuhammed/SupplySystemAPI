using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using static SMS.Modules.Reports.Services.Exports.ReportPdf;

namespace SMS.Modules.Reports.Services.Exports;

/// <summary>
/// A29-P9-03 §15 R3 — the receivables aged as of a day, as a landscape A4 document: what each customer
/// owed by bucket, what everyone owed in total per currency, then the invoices behind those figures.
/// </summary>
public static class AgingReceivablesPdfExporter
{
    public static byte[] Export(AgingReceivablesReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => Header(c, report.CompanyName, "AGING RECEIVABLES", CriteriaLines(report.Criteria)));
                page.Content().Element(c => ComposeContent(c, report));
                page.Footer().Element(c => Footer(c, report.GeneratedAt));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    /// <summary>What the report is of, in words.</summary>
    internal static IReadOnlyList<string> CriteriaLines(AgingReceivablesCriteria c) =>
    [
        $"As of: {Date(c.AsOf)}",
        $"Customer: {(c.PartnerId is null ? "All customers" : c.CustomerName ?? c.PartnerId.Value.ToString())}",
        "Aged by days past the due date"
    ];

    private static void ComposeContent(IContainer container, AgingReceivablesReport report)
    {
        container.Column(column =>
        {
            if (report.TotalRecords == 0)
            {
                Empty(column, $"No invoices were outstanding on {Date(report.Criteria.AsOf)}.");
                return;
            }

            ComposeSummary(column, report);
            ComposeInvoices(column, report);
        });
    }

    /// <summary>A row to a customer and currency across the four buckets, then a bold row to a currency: there is no rate to add one to another with.</summary>
    private static void ComposeSummary(ColumnDescriptor column, AgingReceivablesReport report)
    {
        SectionTitle(column, "OUTSTANDING BY CUSTOMER");

        column.Item().PaddingBottom(12).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.RelativeColumn(3);   // Customer
                columns.ConstantColumn(34);  // Currency
                columns.ConstantColumn(46);  // Invoices
                columns.RelativeColumn(1);   // 0-30
                columns.RelativeColumn(1);   // 31-60
                columns.RelativeColumn(1);   // 61-90
                columns.RelativeColumn(1);   // 90+
                columns.RelativeColumn(1.2f);// Total
            });

            table.Header(header =>
            {
                header.Cell().Element(HeadCell).Text("Customer");
                header.Cell().Element(HeadCell).Text("Cur.");
                header.Cell().Element(HeadCell).AlignRight().Text("Invoices");
                header.Cell().Element(HeadCell).AlignRight().Text($"{AgingBuckets.Days0To30} days");
                header.Cell().Element(HeadCell).AlignRight().Text($"{AgingBuckets.Days31To60} days");
                header.Cell().Element(HeadCell).AlignRight().Text($"{AgingBuckets.Days61To90} days");
                header.Cell().Element(HeadCell).AlignRight().Text($"{AgingBuckets.Over90} days");
                header.Cell().Element(HeadCell).AlignRight().Text("Total");
            });

            for (var i = 0; i < report.Customers.Count; i++)
            {
                var c         = report.Customers[i];
                var alternate = i % 2 == 1;

                table.Cell().Element(x => BodyCell(x, alternate)).Text(c.CustomerName ?? "-");
                table.Cell().Element(x => BodyCell(x, alternate)).Text(c.CurrencyCode);
                table.Cell().Element(x => BodyCell(x, alternate)).AlignRight().Text(c.InvoiceCount.ToString(Culture));
                table.Cell().Element(x => BodyCell(x, alternate)).AlignRight().Text(Money(c.Days0To30));
                table.Cell().Element(x => BodyCell(x, alternate)).AlignRight().Text(Money(c.Days31To60));
                table.Cell().Element(x => BodyCell(x, alternate)).AlignRight().Text(Money(c.Days61To90));
                table.Cell().Element(x => BodyCell(x, alternate)).AlignRight().Text(Money(c.Over90));
                table.Cell().Element(x => BodyCell(x, alternate)).AlignRight().Text(Money(c.Total));
            }

            foreach (var t in report.Totals)
            {
                table.Cell().Element(TotalCell).Text("Total");
                table.Cell().Element(TotalCell).Text(t.CurrencyCode);
                table.Cell().Element(TotalCell).AlignRight().Text(t.InvoiceCount.ToString(Culture));
                table.Cell().Element(TotalCell).AlignRight().Text(Money(t.Days0To30));
                table.Cell().Element(TotalCell).AlignRight().Text(Money(t.Days31To60));
                table.Cell().Element(TotalCell).AlignRight().Text(Money(t.Days61To90));
                table.Cell().Element(TotalCell).AlignRight().Text(Money(t.Over90));
                table.Cell().Element(TotalCell).AlignRight().Text(Money(t.Total));
            }
        });
    }

    private static void ComposeInvoices(ColumnDescriptor column, AgingReceivablesReport report)
    {
        SectionTitle(column, "OUTSTANDING INVOICES");

        column.Item().Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(96);   // Invoice
                columns.RelativeColumn(2.4f); // Customer
                columns.ConstantColumn(56);   // Invoice date
                columns.ConstantColumn(56);   // Due date
                columns.ConstantColumn(34);   // Days past due
                columns.ConstantColumn(38);   // Bucket
                columns.ConstantColumn(30);   // Currency
                columns.RelativeColumn(1);    // Total
                columns.RelativeColumn(1);    // Paid
                columns.RelativeColumn(1.1f); // Outstanding
            });

            table.Header(header =>
            {
                header.Cell().Element(HeadCell).Text("Invoice");
                header.Cell().Element(HeadCell).Text("Customer");
                header.Cell().Element(HeadCell).Text("Invoiced");
                header.Cell().Element(HeadCell).Text("Due");
                header.Cell().Element(HeadCell).AlignRight().Text("Days");
                header.Cell().Element(HeadCell).Text("Bucket");
                header.Cell().Element(HeadCell).Text("Cur.");
                header.Cell().Element(HeadCell).AlignRight().Text("Total");
                header.Cell().Element(HeadCell).AlignRight().Text("Paid");
                header.Cell().Element(HeadCell).AlignRight().Text("Outstanding");
            });

            for (var i = 0; i < report.Invoices.Count; i++)
            {
                var inv       = report.Invoices[i];
                var alternate = i % 2 == 1;

                table.Cell().Element(x => BodyCell(x, alternate)).Text(inv.InvoiceNumber);
                table.Cell().Element(x => BodyCell(x, alternate)).Text(inv.CustomerName ?? "-");
                table.Cell().Element(x => BodyCell(x, alternate)).Text(Date(inv.InvoiceDate));
                table.Cell().Element(x => BodyCell(x, alternate)).Text(Date(inv.DueDate));
                table.Cell().Element(x => BodyCell(x, alternate)).AlignRight().Text(inv.DaysPastDue.ToString(Culture));
                table.Cell().Element(x => BodyCell(x, alternate)).Text(inv.Bucket);
                table.Cell().Element(x => BodyCell(x, alternate)).Text(inv.CurrencyCode);
                table.Cell().Element(x => BodyCell(x, alternate)).AlignRight().Text(Money(inv.GrandTotal));
                table.Cell().Element(x => BodyCell(x, alternate)).AlignRight().Text(Money(inv.AmountPaid));
                table.Cell().Element(x => BodyCell(x, alternate)).AlignRight().Text(Money(inv.Outstanding));
            }
        });
    }
}
