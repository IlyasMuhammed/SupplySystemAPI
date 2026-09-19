using Microsoft.AspNetCore.Hosting;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Services;

// Public because the controller is; the implementation stays internal.
public interface IDeliveryDocumentService
{
    /// <summary>The paper that travels with the goods, carton by carton.</summary>
    Task<(byte[] Content, string FileName)> GeneratePackingListAsync(Guid deliveryUuid);

    /// <summary>The paper the gate keeps: what left, how many pieces, and who authorised it.</summary>
    Task<(byte[] Content, string FileName)> GenerateGatePassAsync(Guid deliveryUuid);
}

/// <summary>
/// The two documents a delivery has to print.
/// <para>
/// Follows <c>MivDocumentService</c> / <c>PoDocumentService</c> exactly — same QuestPDF
/// composition, same brand palette, same letterhead source — so a packing list reads as the same
/// product as the purchase order that started the chain. Deliberately not a new house style.
/// </para>
/// <para>
/// <b>They are different documents for different readers.</b> A packing list is for whoever opens
/// the boxes: it itemises contents, batch by batch. A gate pass is for security at the barrier:
/// it counts pieces and identifies them, and does <em>not</em> itemise what is inside, because the
/// person checking a lorry out of a yard should not be handed a manifest of what is worth taking.
/// </para>
/// </summary>
internal sealed class DeliveryDocumentService : IDeliveryDocumentService
{
    // The same palette as the PO, MIR, MIV and invoice documents.
    private const string BrandColor     = "#6C63FF";
    private const string BrandColorDark = "#4A42CC";
    private const string BrandColorTint = "#F4F3FF";

    private readonly IDeliveryService           _deliveries;
    private readonly IPackageRepository         _packages;
    private readonly IPoDocumentTemplateService _templates;
    private readonly IWebHostEnvironment        _env;

    public DeliveryDocumentService(
        IDeliveryService deliveries,
        IPackageRepository packages,
        IPoDocumentTemplateService templates,
        IWebHostEnvironment env)
    {
        _deliveries = deliveries;
        _packages   = packages;
        _templates  = templates;
        _env        = env;
    }

    // ── Packing list ──────────────────────────────────────────────────────────

    public async Task<(byte[] Content, string FileName)> GeneratePackingListAsync(Guid deliveryUuid)
    {
        var (delivery, packing, template, logo) = await LoadAsync(deliveryUuid);

        var live = packing.Packages.Where(p => !p.IsVoided).ToList();

        // A packing list with no cartons describes nothing. Better to refuse than to hand someone
        // a sheet of paper that implies the goods are ready when they are still on the floor.
        if (live.Count == 0)
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} has no packages, so there is nothing to list. " +
                "Pack it first.");

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));

                page.Header().Element(c => ComposeHeader(c, template, delivery, logo, "PACKING LIST"));
                page.Content().Element(c => ComposePackingContent(c, template, delivery, packing, live));
                page.Footer().Element(c => ComposeFooter(c, template, delivery));
            });
        });

        return (document.GeneratePdf(), $"PackingList-{delivery.DeliveryNumber}.pdf");
    }

    private static void ComposePackingContent(
        IContainer container,
        PoDocumentTemplateModel? template,
        DeliveryDetailModel delivery,
        DeliveryPackingModel packing,
        IReadOnlyList<PackageModel> packages)
    {
        container.PaddingTop(14).Column(column =>
        {
            ComposeAddresses(column, delivery);
            ComposeSummaryStrip(column, packages);

            foreach (var package in packages)
                ComposePackage(column, package);

            ComposeTotals(column, packing, packages);

            if (!string.IsNullOrWhiteSpace(delivery.Notes))
                ComposeNotes(column, delivery.Notes!);

            ComposeReceiptBlock(column, template);
        });
    }

    /// <summary>One block per carton: what it is, then what is inside it.</summary>
    private static void ComposePackage(ColumnDescriptor column, PackageModel package)
    {
        column.Item().PaddingBottom(4).Background(BrandColorTint).Padding(6).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text(package.PackageBarcode).FontSize(10).Bold().FontColor(BrandColorDark);
                col.Item().PaddingTop(1).Text(Describe(package)).FontSize(8)
                          .FontColor(Colors.Grey.Darken1);
            });

            row.ConstantItem(150).AlignRight().Column(col =>
            {
                if (package.GrossWeightKg is { } gross)
                    col.Item().Text($"Gross {gross:N3} kg").FontSize(8.5f);

                if (package.ParentPackageBarcode is { } pallet)
                    col.Item().PaddingTop(1).Text($"On pallet {pallet}").FontSize(8)
                              .FontColor(Colors.Grey.Darken1);
            });
        });

        column.Item().PaddingBottom(10).Table(table =>
        {
            table.ColumnsDefinition(columns =>
            {
                columns.ConstantColumn(26);
                columns.RelativeColumn(3);
                columns.RelativeColumn(1.4f);
                columns.RelativeColumn(1.4f);
                columns.ConstantColumn(50);
                columns.ConstantColumn(60);
            });

            table.Header(header =>
            {
                header.Cell().Element(HeaderCell).Text("Ln");
                header.Cell().Element(HeaderCell).Text("Item Description");
                header.Cell().Element(HeaderCell).Text("Batch");
                header.Cell().Element(HeaderCell).Text("Serial");
                header.Cell().Element(HeaderCell).Text("UOM");
                header.Cell().Element(HeaderCell).AlignRight().Text("Qty");

                static IContainer HeaderCell(IContainer c) =>
                    c.Background(BrandColor)
                     .DefaultTextStyle(x => x.Bold().FontSize(8.5f).FontColor(Colors.White))
                     .PaddingVertical(6).PaddingHorizontal(4);
            });

            for (var i = 0; i < package.Contents.Count; i++)
            {
                var content = package.Contents[i];
                var isLast  = i == package.Contents.Count - 1;

                table.Cell().Element(c => BodyCell(c, isLast)).Text(content.DeliveryLineNo.ToString());
                table.Cell().Element(c => BodyCell(c, isLast)).Text(content.ItemDescription);
                table.Cell().Element(c => BodyCell(c, isLast)).Text(content.BatchNumber  ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast)).Text(content.SerialNumber ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast)).Text(content.UnitOfMeasure ?? "-");
                table.Cell().Element(c => BodyCell(c, isLast)).AlignRight().Text(content.Qty.ToString("N3"));

                static IContainer BodyCell(IContainer c, bool isLast) =>
                    c.PaddingVertical(5).PaddingHorizontal(4)
                     .BorderBottom(isLast ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
            }
        });
    }

    private static void ComposeTotals(
        ColumnDescriptor column, DeliveryPackingModel packing, IReadOnlyList<PackageModel> packages)
    {
        var totals = new (string Label, string Value)[]
        {
            ("Packages",     packages.Count.ToString()),
            ("Total Pieces", packing.QtyPacked.ToString("N3")),
            ("Gross Weight", $"{packing.TotalGrossWeightKg:N3} kg")
        };

        column.Item().PaddingTop(4).PaddingBottom(14).AlignRight().Width(240).Column(col =>
        {
            col.Item().PaddingBottom(4).LineHorizontal(1.5f).LineColor(BrandColor);

            foreach (var (label, value) in totals)
            {
                col.Item().PaddingBottom(2).Row(row =>
                {
                    row.RelativeItem().Text(label).FontSize(9);
                    row.ConstantItem(110).AlignRight().Text(value).FontSize(9.5f).Bold();
                });
            }
        });
    }

    // ── Gate pass ─────────────────────────────────────────────────────────────

    public async Task<(byte[] Content, string FileName)> GenerateGatePassAsync(Guid deliveryUuid)
    {
        var (delivery, packing, template, logo) = await LoadAsync(deliveryUuid);

        var live = packing.Packages.Where(p => !p.IsVoided).ToList();

        // A gate pass authorises goods to leave. Before staging, nothing is at the barrier, and a
        // pass printed early is a pass someone can walk out with while the goods are still being
        // packed.
        var allowed = delivery.Status is "STAGED" or "PENDING_APPROVAL" or "GOODS_ISSUED"
                                      or "IN_TRANSIT" or "DELIVERED" or "PARTIALLY_DELIVERED";

        if (!allowed)
            throw new ConflictException(
                $"Delivery {delivery.DeliveryNumber} is {delivery.Status}. A gate pass is issued " +
                "once the goods are staged at the dock — printing one earlier would let them leave " +
                "before they are ready.");

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(32);
                page.DefaultTextStyle(x => x.FontSize(9.5f).LineHeight(1.35f));

                page.Header().Element(c => ComposeHeader(c, template, delivery, logo, "GATE PASS"));
                page.Content().Element(c => ComposeGatePassContent(c, template, delivery, packing, live));
                page.Footer().Element(c => ComposeFooter(c, template, delivery));
            });
        });

        return (document.GeneratePdf(), $"GatePass-{delivery.DeliveryNumber}.pdf");
    }

    private static void ComposeGatePassContent(
        IContainer container,
        PoDocumentTemplateModel? template,
        DeliveryDetailModel delivery,
        DeliveryPackingModel packing,
        IReadOnlyList<PackageModel> packages)
    {
        container.PaddingTop(14).Column(column =>
        {
            ComposeAddresses(column, delivery);
            ComposeVehicleBlock(column);

            // Counted and identified, never itemised. Whoever checks a lorry out of the yard does
            // not need — and should not be handed — a manifest of what is worth taking.
            column.Item().PaddingBottom(6).Text("PACKAGES PRESENTED")
                  .FontSize(9).Bold().FontColor(BrandColorDark);

            column.Item().PaddingBottom(12).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(26);
                    columns.RelativeColumn(2.4f);
                    columns.RelativeColumn(1);
                    columns.ConstantColumn(80);
                    columns.ConstantColumn(70);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("#");
                    header.Cell().Element(HeaderCell).Text("Handling Unit");
                    header.Cell().Element(HeaderCell).Text("Type");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Gross (kg)");
                    header.Cell().Element(HeaderCell).AlignRight().Text("Seal");

                    static IContainer HeaderCell(IContainer c) =>
                        c.Background(BrandColor)
                         .DefaultTextStyle(x => x.Bold().FontSize(8.5f).FontColor(Colors.White))
                         .PaddingVertical(7).PaddingHorizontal(4);
                });

                for (var i = 0; i < packages.Count; i++)
                {
                    var package = packages[i];
                    var isLast  = i == packages.Count - 1;
                    var isEven  = i % 2 == 1;

                    table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text((i + 1).ToString());
                    table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(package.PackageBarcode);
                    table.Cell().Element(c => BodyCell(c, isLast, isEven)).Text(Title(package.PackageType));
                    table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight()
                         .Text(package.GrossWeightKg?.ToString("N3") ?? "-");
                    table.Cell().Element(c => BodyCell(c, isLast, isEven)).AlignRight()
                         .Text(package.SealNumber ?? "-");

                    static IContainer BodyCell(IContainer c, bool isLast, bool isEven) =>
                        c.Background(isEven ? BrandColorTint : Colors.White)
                         .PaddingVertical(6).PaddingHorizontal(4)
                         .BorderBottom(isLast ? 0 : 0.5f).BorderColor(Colors.Grey.Lighten2);
                }
            });

            column.Item().PaddingBottom(16).AlignRight().Width(260).Column(col =>
            {
                col.Item().PaddingBottom(4).LineHorizontal(1.5f).LineColor(BrandColor);
                col.Item().Row(row =>
                {
                    row.RelativeItem().Text("Total Packages").FontSize(10).Bold();
                    row.ConstantItem(90).AlignRight().Text(packages.Count.ToString())
                       .FontSize(12).Bold().FontColor(BrandColorDark);
                });
                col.Item().PaddingTop(2).Row(row =>
                {
                    row.RelativeItem().Text("Total Gross Weight").FontSize(10).Bold();
                    row.ConstantItem(90).AlignRight()
                       .Text($"{packing.TotalGrossWeightKg:N3} kg")
                       .FontSize(12).Bold().FontColor(BrandColorDark);
                });
            });

            ComposeGateSignatureBlock(column, template);
        });
    }

    /// <summary>
    /// Vehicle and driver, left blank to be written in at the barrier.
    /// <para>
    /// The system does not know them: a carrier booking would supply them, and that is Phase 2.
    /// Printing empty ruled fields is honest — inventing a placeholder that looks like data is not.
    /// </para>
    /// </summary>
    private static void ComposeVehicleBlock(ColumnDescriptor column)
    {
        var fields = new[] { "Vehicle No.", "Driver Name", "Driver CNIC / ID", "Time Out" };

        column.Item().PaddingBottom(14).Row(row =>
        {
            foreach (var field in fields)
            {
                row.RelativeItem().PaddingRight(10).Column(col =>
                {
                    col.Item().Text(field).FontSize(8).FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(12).LineHorizontal(0.5f).LineColor(Colors.Grey.Medium);
                });
            }
        });
    }

    private static void ComposeGateSignatureBlock(ColumnDescriptor column, PoDocumentTemplateModel? template)
    {
        var labels = new[] { "Issued By (Store)", "Checked By (Security)", "Received By (Driver)" };

        column.Item().PaddingTop(40).Row(row =>
        {
            for (var i = 0; i < labels.Length; i++)
            {
                if (i > 0) row.ConstantItem(24);

                var label = labels[i];
                row.RelativeItem().Column(col =>
                {
                    col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                    col.Item().PaddingTop(4).Text(label).FontSize(8);
                });
            }
        });

        if (!string.IsNullOrWhiteSpace(template?.SignatureDisclaimer))
            column.Item().PaddingTop(16).AlignCenter()
                  .Text(template.SignatureDisclaimer).FontSize(7.5f).Italic();
    }

    // ── Shared composition ────────────────────────────────────────────────────

    private async Task<(DeliveryDetailModel Delivery,
                        DeliveryPackingModel Packing,
                        PoDocumentTemplateModel? Template,
                        byte[]? Logo)> LoadAsync(Guid deliveryUuid)
    {
        var delivery = await _deliveries.GetByUuidAsync(deliveryUuid)
            ?? throw new NotFoundException("Delivery not found.");

        var packing = await _packages.GetForDeliveryAsync(deliveryUuid)
            ?? throw new NotFoundException("Delivery not found.");

        var template = await _templates.GetActiveAsync();

        return (delivery, packing, template, TryLoadLogoBytes(template?.CompanyLogoUrl));
    }

    private byte[]? TryLoadLogoBytes(string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl)) return null;

        var relative = logoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var webRoot  = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var fullPath = Path.Combine(webRoot, relative);

        return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
    }

    private static void ComposeHeader(
        IContainer container,
        PoDocumentTemplateModel? template,
        DeliveryDetailModel delivery,
        byte[]? logoBytes,
        string title)
    {
        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text(template?.CompanyName ?? "Company Name")
                              .FontSize(16).Bold().FontColor(BrandColorDark);

                    if (!string.IsNullOrWhiteSpace(template?.CompanyAddress))
                        col.Item().PaddingTop(2).Text(template.CompanyAddress).FontSize(8);

                    var contactLine = string.Join("   |   ", new[]
                    {
                        !string.IsNullOrWhiteSpace(template?.CompanyPhone) ? $"Tel: {template.CompanyPhone}" : null,
                        !string.IsNullOrWhiteSpace(template?.CompanyEmail) ? $"Email: {template.CompanyEmail}" : null,
                        !string.IsNullOrWhiteSpace(template?.CompanyTaxId) ? $"Tax ID: {template.CompanyTaxId}" : null,
                    }.Where(s => s is not null));

                    if (!string.IsNullOrWhiteSpace(contactLine))
                        col.Item().PaddingTop(2).Text(contactLine).FontSize(8);
                });

                if (logoBytes is not null)
                    row.ConstantItem(80).Height(50).Image(logoBytes).FitArea();
            });

            column.Item().PaddingTop(16).Text(title).FontSize(15).Bold().AlignCenter();

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem().Column(col =>
                {
                    col.Item().Text("Delivery No:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                    col.Item().PaddingTop(1).Text(delivery.DeliveryNumber).FontSize(11).Bold();

                    if (!string.IsNullOrWhiteSpace(delivery.SourceNumber))
                        col.Item().PaddingTop(1)
                           .Text($"Against {Title(delivery.SourceType)} {delivery.SourceNumber}")
                           .FontSize(8.5f).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(180).Column(col =>
                {
                    col.Item().AlignRight().Text($"Date: {DateTime.Now:dd MMM yyyy}").FontSize(9);
                    col.Item().PaddingTop(2).AlignRight()
                       .Text($"Status: {Title(delivery.Status)}").FontSize(9);

                    if (delivery.PromisedDate is { } promised)
                        col.Item().PaddingTop(2).AlignRight()
                           .Text($"Promised: {promised:dd MMM yyyy}").FontSize(9);
                });
            });

            column.Item().PaddingTop(14).LineHorizontal(1.5f).LineColor(BrandColor);
        });
    }

    private static void ComposeAddresses(ColumnDescriptor column, DeliveryDetailModel delivery)
    {
        column.Item().PaddingBottom(14).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().Text("Ship From:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                foreach (var line in AddressLines(delivery.ShipFromAddress))
                    col.Item().PaddingTop(1).Text(line).FontSize(8.5f);
            });

            row.ConstantItem(20);

            row.RelativeItem().Column(col =>
            {
                col.Item().Text("Ship To:").FontSize(8.5f).Bold().FontColor(Colors.Grey.Darken1);
                foreach (var line in AddressLines(delivery.ShipToAddress))
                    col.Item().PaddingTop(1).Text(line).FontSize(8.5f);
            });
        });
    }

    /// <summary>
    /// An address as printable lines, empty parts dropped. Returns a dash rather than nothing when
    /// no address is recorded — a blank space on a printed document reads as a bug.
    /// </summary>
    private static IReadOnlyList<string> AddressLines(AddressModel? address)
    {
        if (address is null) return ["-"];

        var lines = new[]
        {
            address.ContactName,
            address.Line1,
            address.Line2,
            string.Join(", ", new[] { address.CityName, address.State, address.PostalCode }
                              .Where(s => !string.IsNullOrWhiteSpace(s))),
            address.CountryName,
            address.ContactPhoneE164 ?? address.ContactPhone
        }
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => s!)
        .ToList();

        return lines.Count > 0 ? lines : ["-"];
    }

    private static void ComposeSummaryStrip(ColumnDescriptor column, IReadOnlyList<PackageModel> packages)
    {
        column.Item().PaddingBottom(12).Text($"CONTENTS BY PACKAGE ({packages.Count})")
              .FontSize(9).Bold().FontColor(BrandColorDark);
    }

    private static void ComposeNotes(ColumnDescriptor column, string notes)
    {
        column.Item().PaddingBottom(6).Text("NOTES").FontSize(9).Bold().FontColor(BrandColorDark);
        column.Item().PaddingBottom(14).Background(BrandColorTint).Padding(8).Text(notes).FontSize(8.5f);
    }

    private static void ComposeReceiptBlock(ColumnDescriptor column, PoDocumentTemplateModel? template)
    {
        column.Item().PaddingTop(40).Row(row =>
        {
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                col.Item().PaddingTop(4).Text(template?.PreparedByLabel ?? "Packed By").FontSize(8);
            });
            row.ConstantItem(24);
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                col.Item().PaddingTop(4).Text("Received By").FontSize(8);
            });
            row.ConstantItem(24);
            row.RelativeItem().Column(col =>
            {
                col.Item().LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                col.Item().PaddingTop(4).Text("Date & Time").FontSize(8);
            });
        });
    }

    private static void ComposeFooter(
        IContainer container, PoDocumentTemplateModel? template, DeliveryDetailModel delivery)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

            if (!string.IsNullOrWhiteSpace(template?.FooterText))
                column.Item().AlignCenter().Text(template.FooterText)
                      .FontSize(7).FontColor(Colors.Grey.Medium);

            column.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text($"{delivery.DeliveryNumber}  ·  Generated {DateTime.Now:yyyy-MM-dd HH:mm}")
                   .FontSize(7).FontColor(Colors.Grey.Medium);

                row.RelativeItem().AlignRight().Text(text =>
                {
                    text.CurrentPageNumber().FontSize(7).FontColor(Colors.Grey.Medium);
                    text.Span(" of ").FontSize(7).FontColor(Colors.Grey.Medium);
                    text.TotalPages().FontSize(7).FontColor(Colors.Grey.Medium);
                });
            });
        });
    }

    /// <summary>A carton's dimensions and type, as one readable line.</summary>
    private static string Describe(PackageModel package)
    {
        var parts = new List<string> { Title(package.PackageType) };

        if (package.LengthCm is { } l && package.WidthCm is { } w && package.HeightCm is { } h)
            parts.Add($"{l:N0} × {w:N0} × {h:N0} cm");

        if (package.Contents.Count > 0)
            parts.Add($"{package.Contents.Count} item{(package.Contents.Count == 1 ? "" : "s")}");

        return string.Join("   ·   ", parts);
    }

    /// <summary>Turns a persisted code into something a human reads: SHORT_CLOSED → Short Closed.</summary>
    private static string Title(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "-";

        var words = code.Replace('_', ' ').ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
