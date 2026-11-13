using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Demand.Models;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Demand.Services.Documents;

/// <summary>
/// A50 TPL (D-25) — the sale quotation as a PDF (GET api/sale-quotations/{uuid}/pdf). The invoice's layout and letterhead
/// (company block, "Quoted To", validity, lines with discount and tax, totals, terms); the org's Quotation template brands it
/// or, with a BodyHtml, replaces it. Rejected lines are not offered, so they are left out. Rendered through PdfRenderGate.
/// </summary>
public sealed class SaleQuotationDocumentService
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    private readonly ISaleQuotationService _quotations;
    private readonly IPoDocumentTemplateService _templates;
    private readonly ISupplierContactLookupService _contacts;
    private readonly IWebHostEnvironment? _env;
    private readonly IDocumentBrandingProvider _branding;
    private readonly ITemplatedDocumentRenderer _templated;
    private readonly TimeProvider _clock;

    public SaleQuotationDocumentService(
        ISaleQuotationService quotations, IPoDocumentTemplateService templates, ISupplierContactLookupService contacts,
        IWebHostEnvironment? env = null, IDocumentBrandingProvider? branding = null, ITemplatedDocumentRenderer? templated = null,
        TimeProvider? clock = null)
    {
        _quotations = quotations;
        _templates = templates;
        _contacts = contacts;
        _env = env;
        _branding = branding ?? new NullDocumentBrandingProvider();
        _templated = templated ?? new NullTemplatedDocumentRenderer();
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<(byte[] Content, string FileName)> GeneratePdfAsync(Guid quotationUuid)
    {
        var q = await _quotations.GetByIdAsync(quotationUuid) ?? throw new NotFoundException("SaleQuotation", quotationUuid);
        var template = await _templates.GetActiveAsync();
        var customer = await _contacts.GetContactInfoAsync(q.PartnerId);
        var brand = await _branding.GetAsync(DocumentTemplateTypes.Quotation);
        if (brand is not null)
            brand = BrandedDocumentKit.WithLetterhead(brand, template?.CompanyName, template?.CompanyAddress, template?.CompanyPhone,
                template?.CompanyEmail, template?.CompanyTaxId);
        var printed = _clock.GetUtcNow().UtcDateTime;
        var fileName = $"{q.QuotationNumber}.pdf";
        var lines = Offered(q);

        if (brand is not null && !string.IsNullOrWhiteSpace(brand.BodyHtml)
            && _templated.Render(brand, Templated(q, lines, customer, printed)) is { } body)
            return (body, fileName);

        var p = DocumentPalette.Resolve(brand, DocumentPalette.House);
        var logo = brand?.LogoBytes ?? LoadLogo(template?.CompanyLogoUrl);
        var document = Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));
            page.Header().Element(c => Header(c, q, template, customer, brand, logo, p));
            page.Content().Element(c => Content(c, q, lines, brand, p));
            page.Footer().Element(c => Footer(c, q, template, brand, printed));
        }));
        return (PdfRenderGate.Run(document.GeneratePdf), fileName);
    }

    internal static List<SaleQuotationLineModel> Offered(SaleQuotationModel q) =>
        q.Lines.Where(l => l.LineType != "REJECTED").OrderBy(l => l.LineNumber).ToList();

    private static string Ccy(SaleQuotationModel q) => q.CurrencyCode ?? string.Empty;
    private static string Money(decimal v, string ccy) => $"{v.ToString("N2", Culture)} {ccy}".Trim();
    private static string Date(DateTime d) => d.ToString("dd MMM yyyy", Culture);

    private static string TaxLabel(SaleQuotationLineModel l, bool anyCode)
    {
        var percent = l.TaxPercent.ToString("0.##", Culture);
        if (!string.IsNullOrWhiteSpace(l.TaxCode)) return $"{l.TaxCode.Trim()} · {percent}%";
        return anyCode ? $"{percent}%" : percent;
    }

    private static string Describe(SaleQuotationLineModel l) =>
        l.LineType == "ALTERNATIVE" && l.AlternativeForLineNumber is { } n ? $"{l.ProductDescription} (alternative to line {n})" : l.ProductDescription;

    internal static TemplatedDocument Templated(SaleQuotationModel q, List<SaleQuotationLineModel> lines, SupplierContactInfo? customer, DateTime printed)
    {
        var anyCode = lines.Any(l => !string.IsNullOrWhiteSpace(l.TaxCode));
        var doc = new TemplatedDocument
        {
            TemplateType = DocumentTemplateTypes.Quotation, Title = "QUOTATION", GeneratedAt = printed,
            Values = new(StringComparer.OrdinalIgnoreCase)
            {
                ["DocumentNumber"] = q.QuotationNumber, ["DocumentDate"] = Date(q.ValidFrom),
                ["QuotationNumber"] = q.QuotationNumber, ["QuotationDate"] = Date(q.ValidFrom), ["ValidUntil"] = Date(q.ValidTo),
                ["CustomerName"] = q.PartnerName ?? "", ["CustomerAddress"] = customer?.Address ?? "", ["CustomerEmail"] = customer?.Email ?? "",
                ["CustomerReference"] = q.CustomerReference ?? "", ["PaymentTerms"] = q.PaymentTerms ?? "", ["DeliveryTerms"] = q.DeliveryTerms ?? "",
                ["Status"] = q.Status, ["CurrencyCode"] = Ccy(q),
                ["Subtotal"] = BrandedDocumentKit.Money(q.Subtotal), ["DiscountAmount"] = BrandedDocumentKit.Money(q.DiscountAmount),
                ["TaxAmount"] = BrandedDocumentKit.Money(q.TaxAmount), ["TotalAmount"] = BrandedDocumentKit.Money(q.GrandTotal),
                ["AmountInWords"] = AmountInWords.Format(q.GrandTotal, q.CurrencyCode), ["Notes"] = q.Notes ?? "",
                ["PartyLabel"] = "Quoted To:", ["PartyName"] = q.PartnerName ?? "-", ["PartyAddress"] = customer?.Address ?? "",
                ["HeaderFacts"] = $"Valid until: {Date(q.ValidTo)}",
            },
        };
        doc.LineColumns.AddRange([new("#", 0.4f), new("Description", 3f), new("Qty", 0.9f, true), new("UOM", 0.6f), new("Unit Price", 1.1f, true),
                                  new("Disc %", 0.7f, true), new(anyCode ? "Tax" : "Tax %", anyCode ? 1.2f : 0.7f, true), new("Amount", 1.3f, true)]);
        foreach (var l in lines)
            doc.LineRows.Add([l.LineNumber.ToString(Culture), Describe(l), l.Quantity.ToString("N2", Culture), l.UomCode ?? "",
                              l.UnitPrice.ToString("N2", Culture), l.DiscountPercent.ToString("0.##", Culture), TaxLabel(l, anyCode),
                              l.LineTotal.ToString("N2", Culture)]);
        foreach (var g in lines.Where(l => l.TaxAmount != 0m).GroupBy(l => TaxLabel(l, true)))
            doc.TaxSummary.Add(new(g.Key, Money(g.Sum(l => l.TaxAmount), Ccy(q))));
        doc.Totals.Add(new("Subtotal", Money(q.Subtotal, Ccy(q))));
        if (q.DiscountAmount != 0m) doc.Totals.Add(new("Discount", "-" + Money(q.DiscountAmount, Ccy(q))));
        doc.Totals.Add(new("Tax", Money(q.TaxAmount, Ccy(q))));
        doc.Totals.Add(new("Total", Money(q.GrandTotal, Ccy(q)), true));
        return doc;
    }

    private byte[]? LoadLogo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || _env is null) return null;
        var webRoot = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var full = Path.Combine(webRoot, url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    private static void Header(IContainer container, SaleQuotationModel q, PoDocumentTemplateModel? t, SupplierContactInfo? customer,
        DocumentBranding? brand, byte[]? logo, DocumentPalette p)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                var left = row.RelativeItem();
                var branded = brand is not null && BrandedDocumentKit.TryComposeHeader(left, brand,
                    BrandedDocumentKit.HeaderValues(brand, "QUOTATION", q.QuotationNumber, q.ValidFrom), p.Dark);
                if (!branded)
                    left.Column(col =>
                    {
                        col.Item().Text(t?.CompanyName ?? brand?.CompanyName ?? "Company Name").FontSize(16).Bold().FontColor(p.Dark);
                        if (!string.IsNullOrWhiteSpace(t?.CompanyAddress)) col.Item().PaddingTop(2).Text(t.CompanyAddress).FontSize(8);
                        var contact = string.Join("   |   ", new[]
                        {
                            string.IsNullOrWhiteSpace(t?.CompanyPhone) ? null : $"Tel: {t.CompanyPhone}",
                            string.IsNullOrWhiteSpace(t?.CompanyEmail) ? null : $"Email: {t.CompanyEmail}",
                            string.IsNullOrWhiteSpace(t?.CompanyTaxId) ? null : $"Tax ID: {t.CompanyTaxId}",
                        }.Where(s => s is not null));
                        if (contact.Length > 0) col.Item().PaddingTop(2).Text(contact).FontSize(8);
                    });
                if (logo is { Length: > 0 }) row.ConstantItem(80).Height(50).Image(logo).FitArea();
            });

            column.Item().PaddingTop(16).Text(q.Status == "DRAFT" ? "DRAFT QUOTATION" : "QUOTATION").FontSize(15).Bold().AlignCenter();

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("Quoted To:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(1).Text(q.PartnerName ?? "-").FontSize(10.5f).Bold();
                    if (!string.IsNullOrWhiteSpace(customer?.Address)) col.Item().PaddingTop(1).Text(customer.Address).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                    if (!string.IsNullOrWhiteSpace(customer?.Email)) col.Item().PaddingTop(1).Text($"Email: {customer.Email}").FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                    if (!string.IsNullOrWhiteSpace(q.CustomerReference)) col.Item().PaddingTop(1).Text($"Your reference: {q.CustomerReference}").FontSize(8.5f);
                });
                row.ConstantItem(170).Column(col =>
                {
                    col.Item().AlignRight().Text($"Quotation No: {q.QuotationNumber}").FontSize(9).Bold();
                    col.Item().PaddingTop(2).AlignRight().Text($"Date: {Date(q.ValidFrom)}").FontSize(9);
                    col.Item().PaddingTop(2).AlignRight().Text($"Valid until: {Date(q.ValidTo)}").FontSize(9).Bold().FontColor(p.Dark);
                });
            });

            column.Item().PaddingTop(14).LineHorizontal(1.5f).LineColor(p.Primary);
        });
    }

    private static void Content(IContainer container, SaleQuotationModel q, List<SaleQuotationLineModel> lines, DocumentBranding? brand, DocumentPalette p)
    {
        var anyCode = lines.Any(l => !string.IsNullOrWhiteSpace(l.TaxCode));
        container.PaddingTop(14).Column(column =>
        {
            var strip = new List<(string, string)> { ("Currency", Ccy(q)) };
            if (!string.IsNullOrWhiteSpace(q.PaymentTerms)) strip.Add(("Payment Terms", q.PaymentTerms));
            if (!string.IsNullOrWhiteSpace(q.DeliveryTerms)) strip.Add(("Delivery Terms", q.DeliveryTerms));
            column.Item().PaddingBottom(14).Row(row =>
            {
                foreach (var (label, value) in strip)
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
                        col.Item().Text(value).FontSize(9.5f).Bold();
                    });
            });

            column.Item().PaddingBottom(10).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(24); c.RelativeColumn(3); c.ConstantColumn(48); c.ConstantColumn(40); c.ConstantColumn(66);
                    c.ConstantColumn(40); c.ConstantColumn(anyCode ? 76 : 40); c.ConstantColumn(76);
                });
                table.Header(h =>
                {
                    foreach (var (title, right) in new[] { ("#", false), ("Description", false), ("Qty", true), ("UOM", false), ("Unit Price", true),
                                                           ("Disc %", true), (anyCode ? "Tax" : "Tax %", true), ("Amount", true) })
                    {
                        var cell = h.Cell().Background(p.Primary).DefaultTextStyle(x => x.Bold().FontSize(8.5f).FontColor(Colors.White))
                                    .PaddingVertical(8).PaddingHorizontal(4);
                        (right ? cell.AlignRight() : cell).Text(title);
                    }
                });
                for (var i = 0; i < lines.Count; i++)
                {
                    var l = lines[i];
                    string bg = i % 2 == 1 ? p.Tint : (string)Colors.White;
                    IContainer Cell() => table.Cell().Background(bg).PaddingVertical(6).PaddingHorizontal(4)
                                              .BorderBottom(i == lines.Count - 1 ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
                    Cell().Text(l.LineNumber.ToString(Culture));
                    Cell().Text(Describe(l));
                    Cell().AlignRight().Text(l.Quantity.ToString("N2", Culture));
                    Cell().Text(l.UomCode ?? "");
                    Cell().AlignRight().Text(l.UnitPrice.ToString("N2", Culture));
                    Cell().AlignRight().Text(l.DiscountPercent.ToString("0.##", Culture));
                    Cell().AlignRight().Text(TaxLabel(l, anyCode));
                    Cell().AlignRight().Text(l.LineTotal.ToString("N2", Culture));
                }
            });

            column.Item().PaddingBottom(14).AlignRight().Width(240).Column(col =>
            {
                void Row(string label, string value) => col.Item().PaddingBottom(4).Row(r =>
                {
                    r.RelativeItem().Text(label).FontSize(9).FontColor("#374151");
                    r.ConstantItem(120).AlignRight().Text(value).FontSize(9.5f).FontColor("#111827");
                });
                Row("Subtotal", Money(q.Subtotal, Ccy(q)));
                if (q.DiscountAmount != 0m) Row("Discount", "-" + Money(q.DiscountAmount, Ccy(q)));
                Row("Tax", Money(q.TaxAmount, Ccy(q)));
                col.Item().PaddingTop(4).BorderTop(1.5f).BorderColor(p.Primary).PaddingTop(6).Row(r =>
                {
                    r.RelativeItem().Text("Total").FontSize(11).Bold();
                    r.ConstantItem(120).AlignRight().Text(Money(q.GrandTotal, Ccy(q))).FontSize(12).Bold().FontColor(p.Dark);
                });
                col.Item().PaddingTop(2).AlignRight().Text(AmountInWords.Format(q.GrandTotal, q.CurrencyCode)).FontSize(7.5f).Italic();
            });

            if (!string.IsNullOrWhiteSpace(q.Notes))
            {
                column.Item().PaddingBottom(6).Text("NOTES").FontSize(9).Bold().FontColor(p.Dark);
                column.Item().PaddingBottom(12).Background(p.Tint).Padding(8).Text(q.Notes).FontSize(8.5f);
            }

            if (brand is not null)
            {
                var bank = BrandedDocumentKit.BankLines(brand, null);
                if (bank is { Length: > 0 })
                {
                    column.Item().PaddingBottom(6).Text("BANK DETAILS").FontSize(9).Bold().FontColor(p.Dark);
                    column.Item().PaddingBottom(12).Background(p.Tint).Padding(8).Column(b => { foreach (var line in bank) b.Item().Text(line).FontSize(8.5f); });
                }
                BrandedDocumentKit.ComposeTerms(column, brand, p.Dark);
            }
        });
    }

    private static void Footer(IContainer container, SaleQuotationModel q, PoDocumentTemplateModel? t, DocumentBranding? brand, DateTime printed)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
            var branded = brand is not null && BrandedDocumentKit.TryComposeFooter(column, brand,
                BrandedDocumentKit.HeaderValues(brand, "QUOTATION", q.QuotationNumber, q.ValidFrom));
            if (!branded && !string.IsNullOrWhiteSpace(t?.FooterText))
                column.Item().AlignCenter().Text(t.FooterText).FontSize(7).FontColor(Colors.Grey.Medium);
            column.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text($"Generated {printed.ToString("yyyy-MM-dd HH:mm", Culture)} UTC").FontSize(7).FontColor(Colors.Grey.Medium);
                row.RelativeItem().AlignRight().Text(x =>
                {
                    x.CurrentPageNumber().FontSize(7).FontColor(Colors.Grey.Medium);
                    x.Span(" of ").FontSize(7).FontColor(Colors.Grey.Medium);
                    x.TotalPages().FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });
    }
}

/// <summary>A50 TPL (D-26) — POST api/sale-quotations/{uuid}/email and api/purchase-orders/{uuid}/email.</summary>
public sealed class SalesDocumentEmailService
{
    private readonly ISaleQuotationService _quotations;
    private readonly SaleQuotationDocumentService _quotationPdf;
    private readonly IPurchaseOrderService _pos;
    private readonly IPoDocumentService _poPdf;
    private readonly ISupplierContactLookupService _contacts;
    private readonly IPoDocumentTemplateService _templates;
    private readonly DocumentEmailDispatcher _dispatcher;

    public SalesDocumentEmailService(
        ISaleQuotationService quotations, SaleQuotationDocumentService quotationPdf, IPurchaseOrderService pos, IPoDocumentService poPdf,
        ISupplierContactLookupService contacts, IPoDocumentTemplateService templates, IEmailSender sender, IEmailTemplateRenderer? emailTemplates = null)
    {
        _quotations = quotations;
        _quotationPdf = quotationPdf;
        _pos = pos;
        _poPdf = poPdf;
        _contacts = contacts;
        _templates = templates;
        _dispatcher = new DocumentEmailDispatcher(sender, emailTemplates);
    }

    public async Task<DocumentEmailResult> SendQuotationAsync(Guid uuid, DocumentEmailRequest? request, CancellationToken ct = default)
    {
        var q = await _quotations.GetByIdAsync(uuid) ?? throw new NotFoundException("SaleQuotation", uuid);
        var contact = await _contacts.GetContactInfoAsync(q.PartnerId);
        var (pdf, fileName) = await _quotationPdf.GeneratePdfAsync(uuid);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["CustomerName"] = q.PartnerName ?? "", ["QuotationNumber"] = q.QuotationNumber,
            ["QuotationDate"] = BrandedDocumentKit.Date(q.ValidFrom), ["ValidUntil"] = BrandedDocumentKit.Date(q.ValidTo),
            ["TotalAmount"] = BrandedDocumentKit.Money(q.GrandTotal), ["CurrencyCode"] = q.CurrencyCode ?? "",
            ["AmountInWords"] = AmountInWords.Format(q.GrandTotal, q.CurrencyCode),
        };
        DocumentEmailDispatcher.AddCompany(values, await _templates.GetActiveAsync());
        return await _dispatcher.SendAsync(request, contact?.Email, EmailTemplateTypes.QuotationSent, values,
            new EmailAttachment(fileName, pdf, "application/pdf"), ct);
    }

    public async Task<DocumentEmailResult> SendPurchaseOrderAsync(Guid uuid, DocumentEmailRequest? request, CancellationToken ct = default)
    {
        var po = await _pos.GetByIdAsync(uuid) ?? throw new NotFoundException("Purchase order not found");
        var contact = await _contacts.GetContactInfoAsync(po.SupplierId);
        var pdf = await _poPdf.GeneratePdfAsync(uuid);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SupplierName"] = po.SupplierName, ["PoNumber"] = po.PoNumber, ["PoDate"] = BrandedDocumentKit.Date(po.CreatedDate),
            ["DeliveryDate"] = po.DeliveryDate is { } d ? BrandedDocumentKit.Date(d) : "-",
            ["TotalAmount"] = BrandedDocumentKit.Money(po.TotalAmount), ["CurrencyCode"] = po.CurrencyCode ?? "",
            ["AmountInWords"] = AmountInWords.Format(po.TotalAmount, po.CurrencyCode),
        };
        DocumentEmailDispatcher.AddCompany(values, await _templates.GetActiveAsync());
        return await _dispatcher.SendAsync(request, contact?.Email, EmailTemplateTypes.PoToSupplier, values,
            new EmailAttachment($"PO-{po.PoNumber}.pdf", pdf, "application/pdf"), ct);
    }
}
