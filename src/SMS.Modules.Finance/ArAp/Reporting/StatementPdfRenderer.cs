using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Demand.Services.Documents;
using SMS.Modules.Lookups.Models;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Finance.ArAp.Reporting;

/// <summary>
/// A48 RPT (tasks 266–268, D-11) — the statement of account as an A4 document: company letterhead, the partner and its
/// address, the period, one section per currency (opening, the lines with running balance, closing), the aging grid and
/// totals. Rendered through <see cref="PdfRenderGate"/> (parallel QuestPDF renders garble bold text).
/// <para>
/// A50 (D-25) — a customer statement takes the org's CustomerStatement template when it has one: its colour, logo, header,
/// footer and terms on this layout, or its BodyHtml through the templated renderer. No template = the pre-A50 PDF.
/// </para>
/// </summary>
internal static class StatementPdfRenderer
{
    private const string Brand     = "#4A42CC";
    private const string HeadFill  = "#EEF0FB";
    private const string AltFill   = "#F8F9FC";
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private sealed record Look(string Brand, string HeadFill, string AltFill);
    private static readonly Look House = new(Brand, HeadFill, AltFill);

    public static byte[] Render(StatementResult s, PoDocumentTemplateModel? template,
        DocumentBranding? branding = null, ITemplatedDocumentRenderer? templated = null)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (branding is not null)
            branding = BrandedDocumentKit.WithLetterhead(branding, template?.CompanyName ?? s.CompanyName, template?.CompanyAddress,
                template?.CompanyPhone, template?.CompanyEmail, template?.CompanyTaxId);

        if (branding is not null && templated is not null && !string.IsNullOrWhiteSpace(branding.BodyHtml)
            && templated.Render(branding, Templated(s)) is { } bodyPdf)
            return bodyPdf;

        var look = House;
        if (branding is not null && BrandPalette.IsValid(branding.PrimaryColor)
            && !string.Equals(branding.PrimaryColor, Brand, StringComparison.OrdinalIgnoreCase))
        {
            var primary = branding.PrimaryColor!.ToUpperInvariant();
            look = new Look(primary, BrandPalette.Tint(primary, 0.1), BrandPalette.Tint(primary, 0.04));
        }

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(8.5f).LineHeight(1.25f));

                page.Header().Element(c => Header(c, s, template, look, branding));
                page.Content().Element(c => Content(c, s, look, branding));
                page.Footer().Element(c => Footer(c, s, branding));
            });
        });

        return PdfRenderGate.Run(document.GeneratePdf);
    }

    private static void Footer(IContainer container, StatementResult s, DocumentBranding? branding)
    {
        if (branding is null)
        {
            FooterLine(container.AlignCenter(), s);
            return;
        }

        container.Column(column =>
        {
            BrandedDocumentKit.TryComposeFooter(column, branding,
                BrandedDocumentKit.HeaderValues(branding, Title(s), s.PartnerCode, s.ToDate.ToDateTime(TimeOnly.MinValue)));
            FooterLine(column.Item().AlignCenter(), s);
        });
    }

    private static void FooterLine(IContainer container, StatementResult s) =>
        container.Text(t =>
        {
            t.Span($"Generated {s.GeneratedAt:dd MMM yyyy HH:mm} UTC · Page ").FontSize(7).FontColor(Colors.Grey.Darken1);
            t.CurrentPageNumber().FontSize(7).FontColor(Colors.Grey.Darken1);
            t.Span(" of ").FontSize(7).FontColor(Colors.Grey.Darken1);
            t.TotalPages().FontSize(7).FontColor(Colors.Grey.Darken1);
        });

    private static string Title(StatementResult s) => s.Type == StatementTypes.OpenItems ? "OPEN ITEMS STATEMENT" : "STATEMENT OF ACCOUNT";

    /// <summary>A50 — the statement for a BodyHtml template: one row per line (all currencies), totals per currency.</summary>
    internal static TemplatedDocument Templated(StatementResult s)
    {
        var doc = new TemplatedDocument
        {
            TemplateType = DocumentTemplateTypes.CustomerStatement, Title = Title(s), GeneratedAt = s.GeneratedAt,
            Values = new(StringComparer.OrdinalIgnoreCase)
            {
                ["DocumentNumber"] = s.PartnerCode ?? "", ["DocumentDate"] = s.ToDate.ToString("dd MMM yyyy", Culture),
                ["CustomerName"] = s.PartnerName ?? "", ["CustomerCode"] = s.PartnerCode ?? "",
                ["PeriodFrom"] = s.FromDate.ToString("dd MMM yyyy", Culture), ["PeriodTo"] = s.ToDate.ToString("dd MMM yyyy", Culture),
                ["OpeningBalance"] = Money(s.OpeningBalanceBase), ["ClosingBalance"] = Money(s.ClosingBalanceBase),
                ["CurrencyCode"] = s.BaseCurrencyCode, ["TotalAmount"] = Money(s.ClosingBalanceBase),
                ["AmountInWords"] = AmountInWords.Format(s.ClosingBalanceBase, s.BaseCurrencyCode),
                ["PartyLabel"] = s.PartnerType == "Vendor" ? "Vendor:" : "Customer:", ["PartyName"] = s.PartnerName ?? "",
                ["PartyAddress"] = s.PartnerAddress ?? "",
                ["HeaderFacts"] = $"{s.FromDate:dd MMM yyyy} – {s.ToDate:dd MMM yyyy}",
            },
        };
        doc.LineColumns.AddRange([new("Date", 0.9f), new("Document", 1.5f), new("Description", 2f), new("Due", 0.9f), new("Debit", 1f, true),
                                  new("Credit", 1f, true), new("Balance", 1.1f, true), new("Ccy", 0.5f)]);
        foreach (var section in s.Sections)
        foreach (var l in section.Lines)
            doc.LineRows.Add([$"{l.Date:dd MMM yy}", l.EntryNumber, l.Description, l.DueDate is { } d ? $"{d:dd MMM yy}" : "",
                              l.Debit is { } dr ? Money(dr) : "", l.Credit is { } cr ? Money(cr) : "", Money(l.RunningBalance), section.CurrencyCode]);
        foreach (var section in s.Sections)
            doc.Totals.Add(new($"Closing balance ({section.CurrencyCode})", Money(section.ClosingBalance), true));
        return doc;
    }

    private static void Header(IContainer container, StatementResult s, PoDocumentTemplateModel? template, Look look, DocumentBranding? branding)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                var left = row.RelativeItem();
                var branded = branding is not null && BrandedDocumentKit.TryComposeHeader(left, branding,
                    BrandedDocumentKit.HeaderValues(branding, Title(s), s.PartnerCode, s.ToDate.ToDateTime(TimeOnly.MinValue)), look.Brand);
                if (!branded)
                    left.Column(col =>
                    {
                        col.Item().Text(template?.CompanyName ?? s.CompanyName ?? "Statement of account").FontSize(14).Bold().FontColor(look.Brand);
                        if (!string.IsNullOrWhiteSpace(template?.CompanyAddress)) col.Item().Text(template.CompanyAddress).FontSize(7.5f);
                        var contact = string.Join("   |   ", new[]
                        {
                            string.IsNullOrWhiteSpace(template?.CompanyPhone) ? null : $"Tel: {template.CompanyPhone}",
                            string.IsNullOrWhiteSpace(template?.CompanyEmail) ? null : $"Email: {template.CompanyEmail}",
                            string.IsNullOrWhiteSpace(template?.CompanyTaxId) ? null : $"Tax ID: {template.CompanyTaxId}"
                        }.Where(x => x is not null));
                        if (contact.Length > 0) col.Item().Text(contact).FontSize(7.5f);
                    });
                if (branding?.LogoBytes is { Length: > 0 } logo)
                    row.ConstantItem(70).Height(44).Image(logo).FitArea();
                row.ConstantItem(200).AlignRight().Column(col =>
                {
                    col.Item().AlignRight().Text(Title(s)).FontSize(12).Bold();
                    col.Item().AlignRight().Text($"{s.FromDate:dd MMM yyyy} – {s.ToDate:dd MMM yyyy}");
                    col.Item().AlignRight().Text(s.PartnerType == "Vendor" ? "Vendor statement" : "Customer statement")
                        .FontColor(Colors.Grey.Darken1);
                });
            });

            column.Item().PaddingTop(10).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text(s.PartnerName ?? s.PartnerId.ToString()).Bold();
                    if (!string.IsNullOrWhiteSpace(s.PartnerCode))    col.Item().Text($"Account: {s.PartnerCode}");
                    if (!string.IsNullOrWhiteSpace(s.PartnerAddress)) col.Item().Text(s.PartnerAddress);
                    if (!string.IsNullOrWhiteSpace(s.PartnerEmail))   col.Item().Text(s.PartnerEmail);
                });
                row.ConstantItem(200).AlignRight().Column(col =>
                {
                    col.Item().AlignRight().Text($"Balance due ({s.BaseCurrencyCode}): {Money(s.ClosingBalanceBase)}").Bold();
                    if (s.Type == StatementTypes.Activity)
                        col.Item().AlignRight().Text($"Opening ({s.BaseCurrencyCode}): {Money(s.OpeningBalanceBase)}");
                });
            });

            column.Item().PaddingTop(8).LineHorizontal(1.2f).LineColor(look.Brand);
        });
    }

    private static void Content(IContainer container, StatementResult s, Look look, DocumentBranding? branding)
    {
        container.PaddingTop(8).Column(column =>
        {
            if (s.Sections.Count == 0)
            {
                column.Item().PaddingVertical(20).AlignCenter().Text("Nothing to show for this period.").Italic();
                if (branding is not null) BrandedDocumentKit.ComposeTerms(column, branding, look.Brand);
                return;
            }

            foreach (var section in s.Sections)
            {
                column.Item().PaddingTop(6).Text($"{section.CurrencyCode}").FontSize(10).Bold().FontColor(look.Brand);
                if (s.Type == StatementTypes.Activity)
                    column.Item().Text($"Opening balance: {Money(section.OpeningBalance)}");

                column.Item().PaddingTop(3).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(54);   // date
                        c.ConstantColumn(92);   // number
                        c.RelativeColumn(2.6f); // description
                        c.ConstantColumn(54);   // due
                        c.RelativeColumn(1);    // debit
                        c.RelativeColumn(1);    // credit
                        c.RelativeColumn(1.1f); // balance
                        c.ConstantColumn(62);   // status
                    });

                    table.Header(h =>
                    {
                        foreach (var title in new[] { "Date", "Document", "Description", "Due", "Debit", "Credit", "Balance", "Status" })
                        {
                            var cell = h.Cell().Background(look.HeadFill).Padding(3);
                            if (title is "Debit" or "Credit" or "Balance") cell.AlignRight().Text(title).Bold();
                            else cell.Text(title).Bold();
                        }
                    });

                    for (var i = 0; i < section.Lines.Count; i++)
                    {
                        var l   = section.Lines[i];
                        var bg  = i % 2 == 1 ? look.AltFill : "#FFFFFF";
                        table.Cell().Background(bg).Padding(3).Text($"{l.Date:dd MMM yy}");
                        table.Cell().Background(bg).Padding(3).Text(l.EntryNumber);
                        table.Cell().Background(bg).Padding(3).Text(l.Description);
                        table.Cell().Background(bg).Padding(3).Text(l.DueDate is { } d ? $"{d:dd MMM yy}" : "");
                        table.Cell().Background(bg).Padding(3).AlignRight().Text(l.Debit is { } dr ? Money(dr) : "");
                        table.Cell().Background(bg).Padding(3).AlignRight().Text(l.Credit is { } cr ? Money(cr) : "");
                        table.Cell().Background(bg).Padding(3).AlignRight().Text(Money(l.RunningBalance));
                        table.Cell().Background(bg).Padding(3).Text(l.Status ?? "");
                    }

                    table.Cell().ColumnSpan(4).BorderTop(0.8f).Padding(3).Text("Totals").Bold();
                    table.Cell().BorderTop(0.8f).Padding(3).AlignRight().Text(Money(section.TotalDebits)).Bold();
                    table.Cell().BorderTop(0.8f).Padding(3).AlignRight().Text(Money(section.TotalCredits)).Bold();
                    table.Cell().BorderTop(0.8f).Padding(3).AlignRight().Text(Money(section.ClosingBalance)).Bold();
                    table.Cell().BorderTop(0.8f).Padding(3).Text("");
                });

                column.Item().PaddingTop(2).AlignRight().Text($"Closing balance ({section.CurrencyCode}): {Money(section.ClosingBalance)}").Bold();

                if (section.Aging is { Length: > 0 } aging && s.AgingLabels.Length == aging.Length)
                {
                    column.Item().PaddingTop(4).Table(table =>
                    {
                        table.ColumnsDefinition(c => { foreach (var _ in aging) c.RelativeColumn(); c.RelativeColumn(); });
                        foreach (var label in s.AgingLabels)
                            table.Cell().Background(look.HeadFill).Padding(3).AlignRight().Text(label).Bold();
                        table.Cell().Background(look.HeadFill).Padding(3).AlignRight().Text("Total due").Bold();
                        foreach (var amount in aging) table.Cell().Padding(3).AlignRight().Text(Money(amount));
                        table.Cell().Padding(3).AlignRight().Text(Money(aging.Sum())).Bold();
                    });
                }
            }

            if (branding is not null) BrandedDocumentKit.ComposeTerms(column, branding, look.Brand);
        });
    }

    private static string Money(decimal value) => value.ToString("N2", Culture);
}
