using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.ArAp.Reporting;

/// <summary>A48 RPT — <c>aging/export?format=pdf|xlsx</c>: the configurable-bucket aging (partners, totals, details).</summary>
internal static class AgingExporters
{
    internal const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    internal static string FileName(AgingReport r, string extension) =>
        $"aging-{(r.ReportType == "Payable" ? "payables" : "receivables")}-{r.AsOfDate:yyyyMMdd}.{extension}";

    public static byte[] Pdf(AgingReport r, string? companyName)
    {
        var title = r.ReportType == "Payable" ? "AGED PAYABLES" : "AGED RECEIVABLES";
        var partnerWord = r.ReportType == "Payable" ? "Vendor" : "Customer";

        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4.Landscape());
            page.Margin(24);
            page.DefaultTextStyle(x => x.FontSize(8).LineHeight(1.2f));

            page.Header().Column(c =>
            {
                if (!string.IsNullOrWhiteSpace(companyName)) c.Item().Text(companyName).FontSize(13).Bold();
                c.Item().Text(title).FontSize(12).Bold();
                c.Item().Text($"As of {r.AsOfDate:dd MMM yyyy} · basis {r.Basis} · amounts in {r.BaseCurrencyCode} · " +
                              $"{r.ReportType switch { "Payable" => "DPO", _ => "DSO" }} {r.DsoOrDpo.ToString("N1", Culture)} days");
            });

            page.Content().PaddingTop(8).Column(col =>
            {
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(3);
                        foreach (var _ in r.BucketLabels) cd.RelativeColumn(1);
                        cd.RelativeColumn(1.2f); cd.RelativeColumn(1.1f); cd.RelativeColumn(1.2f);
                    });
                    table.Header(h =>
                    {
                        h.Cell().Element(Head).Text(partnerWord);
                        foreach (var label in r.BucketLabels) h.Cell().Element(Head).AlignRight().Text(label);
                        h.Cell().Element(Head).AlignRight().Text("Total");
                        h.Cell().Element(Head).AlignRight().Text("Credits");
                        h.Cell().Element(Head).AlignRight().Text("Net");
                    });
                    foreach (var l in r.Lines)
                    {
                        table.Cell().Element(Body).Text(l.PartnerName ?? l.PartnerId.ToString());
                        foreach (var a in l.BucketAmounts) table.Cell().Element(Body).AlignRight().Text(Money(a));
                        table.Cell().Element(Body).AlignRight().Text(Money(l.TotalOutstanding));
                        table.Cell().Element(Body).AlignRight().Text(Money(l.UnappliedCredits));
                        table.Cell().Element(Body).AlignRight().Text(Money(l.NetBalance));
                    }
                    table.Cell().Element(Total).Text($"Total ({r.Totals.TotalPartners})");
                    foreach (var a in r.Totals.BucketTotals) table.Cell().Element(Total).AlignRight().Text(Money(a));
                    table.Cell().Element(Total).AlignRight().Text(Money(r.Totals.GrandTotal));
                    table.Cell().Element(Total).AlignRight().Text(Money(r.Totals.UnappliedCredits));
                    table.Cell().Element(Total).AlignRight().Text(Money(r.Totals.NetBalance));
                });

                var details = r.Lines.SelectMany(l => (l.Details ?? []).Select(d => (l.PartnerName, d))).ToList();
                if (details.Count == 0) return;

                col.Item().PaddingTop(12).Text("OPEN DOCUMENTS").Bold();
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(2.2f); cd.ConstantColumn(90); cd.ConstantColumn(54); cd.ConstantColumn(54);
                        cd.ConstantColumn(34); cd.ConstantColumn(46); cd.ConstantColumn(30); cd.RelativeColumn(1); cd.RelativeColumn(1.1f);
                    });
                    table.Header(h =>
                    {
                        foreach (var t in new[] { partnerWord, "Document", "Date", "Due", "Days", "Bucket", "Cur." })
                            h.Cell().Element(Head).Text(t);
                        h.Cell().Element(Head).AlignRight().Text("Open");
                        h.Cell().Element(Head).AlignRight().Text($"Open ({r.BaseCurrencyCode})");
                    });
                    foreach (var (name, d) in details)
                    {
                        table.Cell().Element(Body).Text(name ?? "-");
                        table.Cell().Element(Body).Text(d.DocumentNumber);
                        table.Cell().Element(Body).Text($"{d.DocumentDate:dd MMM yy}");
                        table.Cell().Element(Body).Text($"{d.DueDate:dd MMM yy}");
                        table.Cell().Element(Body).AlignRight().Text(d.DaysOverdue.ToString(Culture));
                        table.Cell().Element(Body).Text(d.BucketLabel);
                        table.Cell().Element(Body).Text(d.CurrencyCode);
                        table.Cell().Element(Body).AlignRight().Text(Money(d.OpenAmount));
                        table.Cell().Element(Body).AlignRight().Text(Money(d.OpenAmountBase) + (d.RateMissing ? "*" : ""));
                    }
                });
            });

            page.Footer().AlignCenter().Text(t =>
            {
                t.Span($"Generated {r.GeneratedAt:dd MMM yyyy HH:mm} UTC · Page ").FontSize(7);
                t.CurrentPageNumber().FontSize(7);
            });
        }));

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    public static byte[] Excel(AgingReport r)
    {
        using var workbook = new XLWorkbook();

        var ws = workbook.Worksheets.Add("Aging");
        ws.Cell(1, 1).Value = $"{(r.ReportType == "Payable" ? "Aged payables" : "Aged receivables")} as of {r.AsOfDate:yyyy-MM-dd} ({r.Basis}, {r.BaseCurrencyCode})";
        ws.Cell(1, 1).Style.Font.Bold = true;

        var headers = new List<string> { r.ReportType == "Payable" ? "Vendor" : "Customer", "Code" };
        headers.AddRange(r.BucketLabels);
        headers.AddRange(["Total", "Overdue", "Credits", "Net", "Open items", "Oldest date"]);
        for (var i = 0; i < headers.Count; i++) { ws.Cell(3, i + 1).Value = headers[i]; ws.Cell(3, i + 1).Style.Font.Bold = true; }

        var row = 4;
        foreach (var l in r.Lines)
        {
            var c = 1;
            ws.Cell(row, c++).Value = l.PartnerName ?? l.PartnerId.ToString();
            ws.Cell(row, c++).Value = l.PartnerCode ?? string.Empty;
            foreach (var a in l.BucketAmounts) ws.Cell(row, c++).Value = a;
            ws.Cell(row, c++).Value = l.TotalOutstanding;
            ws.Cell(row, c++).Value = l.TotalOverdue;
            ws.Cell(row, c++).Value = l.UnappliedCredits;
            ws.Cell(row, c++).Value = l.NetBalance;
            ws.Cell(row, c++).Value = l.OpenItemCount;
            ws.Cell(row, c).Value   = l.OldestOpenDate is { } d ? d.ToString("yyyy-MM-dd", Culture) : string.Empty;
            row++;
        }
        {
            var c = 1;
            ws.Cell(row, c++).Value = "Total"; c++;
            foreach (var a in r.Totals.BucketTotals) ws.Cell(row, c++).Value = a;
            ws.Cell(row, c++).Value = r.Totals.GrandTotal;
            ws.Cell(row, c++).Value = r.Totals.TotalOverdue;
            ws.Cell(row, c++).Value = r.Totals.UnappliedCredits;
            ws.Cell(row, c).Value   = r.Totals.NetBalance;
            ws.Row(row).Style.Font.Bold = true;
        }
        ws.Range(4, 3, row, 2 + r.BucketLabels.Length + 4).Style.NumberFormat.Format = "#,##0.00";
        ws.Columns().AdjustToContents();

        var dw = workbook.Worksheets.Add("Documents");
        string[] dh = ["Partner", "Type", "Document", "Reference", "Date", "Due", "Days overdue", "Bucket", "Currency", "Original",
                       "Open", "Rate", $"Open ({r.BaseCurrencyCode})", "Rate missing"];
        for (var i = 0; i < dh.Length; i++) { dw.Cell(1, i + 1).Value = dh[i]; dw.Cell(1, i + 1).Style.Font.Bold = true; }
        var dr = 2;
        foreach (var l in r.Lines)
            foreach (var d in l.Details ?? [])
            {
                dw.Cell(dr, 1).Value  = l.PartnerName ?? l.PartnerId.ToString();
                dw.Cell(dr, 2).Value  = d.DocumentType;
                dw.Cell(dr, 3).Value  = d.DocumentNumber;
                dw.Cell(dr, 4).Value  = d.Reference ?? string.Empty;
                dw.Cell(dr, 5).Value  = d.DocumentDate.ToString("yyyy-MM-dd", Culture);
                dw.Cell(dr, 6).Value  = d.DueDate.ToString("yyyy-MM-dd", Culture);
                dw.Cell(dr, 7).Value  = d.DaysOverdue;
                dw.Cell(dr, 8).Value  = d.BucketLabel;
                dw.Cell(dr, 9).Value  = d.CurrencyCode;
                dw.Cell(dr, 10).Value = d.OriginalAmount;
                dw.Cell(dr, 11).Value = d.OpenAmount;
                dw.Cell(dr, 12).Value = d.ExchangeRate;
                dw.Cell(dr, 13).Value = d.OpenAmountBase;
                dw.Cell(dr, 14).Value = d.RateMissing ? "yes" : string.Empty;
                dr++;
            }
        if (dr > 2) dw.Range(2, 10, dr - 1, 13).Style.NumberFormat.Format = "#,##0.00";
        dw.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static IContainer Head(IContainer c) => c.Background("#EEF0FB").Padding(3).DefaultTextStyle(x => x.Bold());
    private static IContainer Body(IContainer c) => c.BorderBottom(0.3f).BorderColor("#E0E0E0").Padding(3);
    private static IContainer Total(IContainer c) => c.BorderTop(0.8f).Padding(3).DefaultTextStyle(x => x.Bold());
    private static string Money(decimal v) => v.ToString("N2", Culture);
}
