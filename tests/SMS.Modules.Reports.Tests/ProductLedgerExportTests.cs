using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using FluentAssertions;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services.Exports;
using UglyToad.PdfPig;
using Xunit;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-06 §15 R9 and R10 — the four documents the two reports are exported as. The PDFs are read back page by
/// page with PdfPig and the workbooks cell by cell with ClosedXML, so what is asserted is what a person who opens
/// the file sees, not what the exporter thinks it wrote.
/// </summary>
public class ProductLedgerExportTests
{
    private static readonly DateTime Generated = new(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);
    private static readonly Guid Laptop = Guid.Parse("a0000000-0000-0000-0000-000000000001");

    private static string TextOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join('\n', doc.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))));
    }

    private static XLWorkbook Workbook(byte[] xlsx) => new(new MemoryStream(xlsx));

    private static void ShouldBeLandscapeA4(byte[] pdf)
    {
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        using var doc = PdfDocument.Open(pdf);
        (Math.Round(doc.GetPage(1).Width), Math.Round(doc.GetPage(1).Height)).Should().Be((842d, 595d));
    }

    // ═════════════════════════════ R9 Product ledger ═════════════════════════════

    private static ProductLedgerReportEntry Entry(
        DateTime date, string? sku, string type, string direction, decimal qty, decimal unitCost, decimal total, decimal runningQty, decimal runningValue,
        string reference = "REF-001", string? partner = null, string? variantName = null, decimal? variantQty = null, decimal? variantValue = null) =>
        new()
        {
            EntryUuid = Guid.NewGuid(), VariantUuid = Guid.NewGuid(), Sku = sku, VariantName = variantName, EntryDate = date, EntryType = type, Direction = direction,
            ReferenceType = "Ref", ReferenceId = Guid.NewGuid(), ReferenceNumber = reference, PartnerId = partner is null ? null : Guid.NewGuid(), PartnerName = partner,
            Quantity = qty, UnitCost = unitCost, TotalCost = total, RunningQty = runningQty, RunningValue = runningValue,
            WeightedAverageCost = runningQty <= 0m ? 0m : Math.Round(runningValue / runningQty, 4), VariantRunningQty = variantQty ?? runningQty, VariantRunningValue = variantValue ?? runningValue
        };

    private static readonly ProductLedgerReportEntry[] Movements =
    [
        Entry(new DateTime(2026, 9, 5), "LAP-BLK", "PURCHASE", "IN", 100m, 10m, 1000m, 120m, 1200m, "GRN-2026-00001", "Globex Corp", "Black", 100m, 1000m),
        Entry(new DateTime(2026, 9, 7), "LAP-BLK", "SALE", "OUT", 40m, 10m, 400m, 80m, 800m, "SINV-2026-00001", "Acme Ltd", "Black", 60m, 600m),
        Entry(new DateTime(2026, 9, 8), "LAP-SLV", "ADJUSTMENT", "IN", 2.5m, 12.5m, 31.25m, 82.5m, 831.25m, "ADJ-2026-00001", null, "Silver", 2.5m, 31.25m),
        Entry(new DateTime(2026, 9, 9), null, "RETURN_OUT", "OUT", 2.5m, 10.0761m, 25.19m, 80m, 806.06m, "SRET-2026-00001", "Globex Corp"),
    ];

    private static ProductLedgerReportSummary Summary(int count = 4) => new()
    {
        OpeningQuantity = 20m, OpeningValue = 200m, QuantityIn = 102.5m, ValueIn = 1031.25m, QuantityOut = 42.5m, ValueOut = 425.19m,
        ClosingQuantity = 80m, ClosingValue = 806.06m, ClosingWeightedAverageCost = 10.0758m, MovementCount = count
    };

    private static ProductLedgerReport Ledger(
        IReadOnlyList<ProductLedgerReportEntry>? items = null, ProductLedgerReportSummary? summary = null, ProductLedgerReportCriteria? criteria = null) =>
        new()
        {
            CompanyName = "Northwind Trading", GeneratedAt = Generated,
            Criteria = criteria ?? new ProductLedgerReportCriteria { ProductUuid = Laptop, ProductName = "Laptop", DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30) },
            Summary = summary ?? Summary(),
            Items = [.. items ?? Movements],
            TotalRecords = (items ?? Movements).Count, Page = 1, PageSize = Math.Max(1, (items ?? Movements).Count), TotalPages = (items ?? Movements).Count == 0 ? 0 : 1
        };

    [Fact]
    public void The_ledger_pdf_is_a_landscape_pdf_that_names_the_company_report_product_variant_and_period()
    {
        var pdf = ProductLedgerPdfExporter.Export(Ledger());

        ShouldBeLandscapeA4(pdf);
        TextOf(pdf).Should().Contain("Northwind Trading").And.Contain("PRODUCT LEDGER").And.Contain("Product: Laptop")
            .And.Contain("Variant: All variants").And.Contain("Period: 01 Sep 2026 to 30 Sep 2026").And.Contain("Generated 2026-09-20 10:30 UTC");
    }

    [Fact]
    public void A_named_variant_an_unnamed_product_and_an_open_period_read_in_words()
    {
        var id = Guid.NewGuid();
        ProductLedgerPdfExporter.CriteriaLines(new ProductLedgerReportCriteria { ProductUuid = id })
            .Should().Equal($"Product: {id}", "Variant: All variants", "Period: All dates");
        ProductLedgerPdfExporter.CriteriaLines(new ProductLedgerReportCriteria { ProductName = "Laptop", VariantUuid = id, VariantName = "Silver", DateFrom = new DateTime(2026, 9, 1) })
            .Should().Equal("Product: Laptop", "Variant: Silver", "Period: From 01 Sep 2026");
        ProductLedgerPdfExporter.CriteriaLines(new ProductLedgerReportCriteria { ProductName = "Laptop", VariantUuid = id })[1].Should().Be($"Variant: {id}");
        ProductLedgerPdfExporter.CriteriaLines(new ProductLedgerReportCriteria { DateTo = new DateTime(2026, 9, 30) })[2].Should().Be("Period: Up to 30 Sep 2026");
    }

    [Fact]
    public void The_position_says_where_the_product_started_what_came_in_and_went_out_and_where_it_ended_with_what_a_unit_was_worth()
    {
        var text = TextOf(ProductLedgerPdfExporter.Export(Ledger()));

        text.Should().Contain("POSITION").And.Contain("Quantity Value Avg cost");
        text.Should().Contain("Opening balance 20 200.00 10.0000");
        text.Should().Contain("Received 102.5 1,031.25 10.0610");
        text.Should().Contain("Issued 42.5 425.19 10.0045");
        text.Should().Contain("Closing balance 80 806.06 10.0758");
    }

    [Fact]
    public void Each_movement_is_a_row_with_its_date_variant_type_reference_partner_cost_and_the_balance_after_it()
    {
        var text = TextOf(ProductLedgerPdfExporter.Export(Ledger()));

        text.Should().Contain("MOVEMENTS (4)");
        text.Should().Contain("Date Variant Type Reference Partner In Out Unit cost Value Balance qty Balance value");
        text.Should().Contain("05 Sep 2026 LAP-BLK PURCHASE GRN-2026-00001 Globex Corp 100 10.0000 1,000.00 120 1,200.00");
        text.Should().Contain("07 Sep 2026 LAP-BLK SALE SINV-2026-00001 Acme Ltd 40 10.0000 400.00 80 800.00");
        text.Should().Contain("08 Sep 2026 LAP-SLV ADJUSTMENT ADJ-2026-00001 - 2.5 12.5000 31.25 82.5 831.25");
        text.Should().Contain("09 Sep 2026 - RETURN OUT SRET-2026-00001 Globex Corp 2.5 10.0761 25.19 80 806.06");
    }

    [Fact]
    public void A_movement_in_puts_its_quantity_under_in_and_one_out_puts_it_under_out()
    {
        var pdf = ProductLedgerPdfExporter.Export(Ledger([Movements[0], Movements[1]]));

        using var doc = PdfDocument.Open(pdf);
        var words = doc.GetPage(1).GetWords().ToList();
        var inX  = words.First(w => w.Text == "In").BoundingBox.Right;
        var outX = words.First(w => w.Text == "Out").BoundingBox.Right;
        var hundred = words.First(w => w.Text == "100").BoundingBox.Right;
        var forty   = words.First(w => w.Text == "40" && w.BoundingBox.Bottom < words.First(x => x.Text == "SALE").BoundingBox.Top + 40).BoundingBox.Right;

        Math.Abs(hundred - inX).Should().BeLessThan(6, "the 100 that came in is under In");
        Math.Abs(forty - outX).Should().BeLessThan(6, "the 40 that went out is under Out");
    }

    [Fact]
    public void No_movements_is_a_page_that_still_says_where_the_product_stands_and_has_no_movement_table()
    {
        var summary = new ProductLedgerReportSummary { OpeningQuantity = 40m, OpeningValue = 400m, ClosingQuantity = 40m, ClosingValue = 400m, ClosingWeightedAverageCost = 10m };

        var text = TextOf(ProductLedgerPdfExporter.Export(Ledger([], summary)));

        text.Should().Contain("No movements in this period.").And.Contain("Closing balance 40 400.00 10.0000").And.NotContain("MOVEMENTS");
    }

    [Fact]
    public void A_long_ledger_runs_over_pages_and_prints_every_movement_once()
    {
        var items = Enumerable.Range(1, 200).Select(i => Entry(new DateTime(2026, 9, 1).AddDays(i % 28), "LAP-BLK", "PURCHASE", "IN", 1m, 1m, 1m, i, i, $"GRN-{i:0000}")).ToList();

        var pdf  = ProductLedgerPdfExporter.Export(Ledger(items, Summary(200)));
        var text = TextOf(pdf);

        using var doc = PdfDocument.Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(3);
        foreach (var item in items) Regex.Matches(text, Regex.Escape(item.ReferenceNumber) + " ").Count.Should().Be(1, item.ReferenceNumber);
    }

    [Fact]
    public void The_ledger_workbook_has_the_movements_then_the_summary_with_the_columns_named()
    {
        using var book = Workbook(ProductLedgerExcelExporter.Export(Ledger()));

        book.Worksheets.Select(w => w.Name).Should().Equal("Product Ledger", "Summary");
        var ws = book.Worksheet("Product Ledger");
        Enumerable.Range(1, 17).Select(c => ws.Cell(1, c).GetString()).Should().Equal(
            "Date", "SKU", "Variant", "Type", "Reference", "Partner", "Direction", "Qty In", "Qty Out", "Unit Cost", "Total Cost",
            "Running Qty", "Running Value", "Average Cost", "Variant Running Qty", "Variant Running Value", "Narration");
        ws.Cell(1, 1).Style.Font.Bold.Should().BeTrue();
        ws.LastRowUsed()!.RowNumber().Should().Be(5, "a row to a movement and nothing else: the position is on the summary");
    }

    [Fact]
    public void The_ledger_workbook_cells_are_typed_and_a_movement_in_has_a_quantity_in_and_none_out_and_the_other_way_round()
    {
        using var book = Workbook(ProductLedgerExcelExporter.Export(Ledger()));
        var ws = book.Worksheet("Product Ledger");

        ws.Cell(2, 1).GetDateTime().Should().Be(new DateTime(2026, 9, 5));
        ws.Cell(2, 1).Style.DateFormat.Format.Should().Be("yyyy-mm-dd");
        (ws.Cell(2, 2).GetString(), ws.Cell(2, 3).GetString(), ws.Cell(2, 4).GetString(), ws.Cell(2, 5).GetString(), ws.Cell(2, 6).GetString(), ws.Cell(2, 7).GetString())
            .Should().Be(("LAP-BLK", "Black", "PURCHASE", "GRN-2026-00001", "Globex Corp", "IN"));
        (ws.Cell(2, 8).GetDouble(), ws.Cell(2, 9).IsEmpty()).Should().Be((100d, true));
        (ws.Cell(3, 8).IsEmpty(), ws.Cell(3, 9).GetDouble()).Should().Be((true, 40d));
        (ws.Cell(2, 10).GetDouble(), ws.Cell(2, 11).GetDouble(), ws.Cell(2, 12).GetDouble(), ws.Cell(2, 13).GetDouble(), ws.Cell(2, 14).GetDouble(), ws.Cell(2, 15).GetDouble(), ws.Cell(2, 16).GetDouble())
            .Should().Be((10d, 1000d, 120d, 1200d, 10d, 100d, 1000d));
        ws.Cell(2, 8).Style.NumberFormat.Format.Should().Be("#,##0.####");
        ws.Cell(2, 10).Style.NumberFormat.Format.Should().Be("#,##0.0000");
        ws.Cell(2, 13).Style.NumberFormat.Format.Should().Be("#,##0.00");
        ws.Cell(4, 6).GetString().Should().BeEmpty("a movement with no partner is an empty cell, not a dash");
        ws.Cell(5, 2).GetString().Should().BeEmpty();
    }

    [Fact]
    public void The_ledger_summary_says_what_was_asked_for_and_where_the_product_stood()
    {
        using var book = Workbook(ProductLedgerExcelExporter.Export(Ledger()));
        var ws = book.Worksheet("Summary");

        (ws.Cell(1, 1).GetString(), ws.Cell(1, 2).GetString()).Should().Be(("Product", "Laptop"));
        (ws.Cell(2, 1).GetString(), ws.Cell(2, 2).GetString()).Should().Be(("Variant", "All variants"));
        (ws.Cell(3, 1).GetString(), ws.Cell(3, 2).GetDateTime()).Should().Be(("From", new DateTime(2026, 9, 1)));
        (ws.Cell(4, 1).GetString(), ws.Cell(4, 2).GetDateTime()).Should().Be(("To", new DateTime(2026, 9, 30)));
        Enumerable.Range(1, 4).Select(c => ws.Cell(6, c).GetString()).Should().Equal("Position", "Quantity", "Value", "Average Cost");
        (ws.Cell(7, 1).GetString(), ws.Cell(7, 2).GetDouble(), ws.Cell(7, 3).GetDouble(), ws.Cell(7, 4).GetDouble()).Should().Be(("Opening balance", 20d, 200d, 10d));
        (ws.Cell(8, 1).GetString(), ws.Cell(8, 2).GetDouble(), ws.Cell(8, 3).GetDouble()).Should().Be(("Received", 102.5d, 1031.25d));
        (ws.Cell(9, 1).GetString(), ws.Cell(9, 2).GetDouble(), ws.Cell(9, 3).GetDouble()).Should().Be(("Issued", 42.5d, 425.19d));
        (ws.Cell(10, 1).GetString(), ws.Cell(10, 2).GetDouble(), ws.Cell(10, 3).GetDouble(), ws.Cell(10, 4).GetDouble()).Should().Be(("Closing balance", 80d, 806.06d, 10.0758d));
        ws.Cell(10, 1).Style.Font.Bold.Should().BeTrue();
    }

    [Fact]
    public void An_unfiltered_ledger_summary_says_all_dates_and_a_named_variant_says_so()
    {
        using var open = Workbook(ProductLedgerExcelExporter.Export(Ledger([], new ProductLedgerReportSummary(), new ProductLedgerReportCriteria { ProductUuid = Laptop, ProductName = "Laptop" })));
        (open.Worksheet("Summary").Cell(3, 2).GetString(), open.Worksheet("Summary").Cell(4, 2).GetString()).Should().Be(("All dates", "All dates"));
        open.Worksheet("Product Ledger").LastRowUsed()!.RowNumber().Should().Be(1, "only the heads");

        var variant = Guid.NewGuid();
        using var named = Workbook(ProductLedgerExcelExporter.Export(Ledger(criteria: new ProductLedgerReportCriteria { ProductUuid = Laptop, VariantUuid = variant, VariantName = "Silver" })));
        (named.Worksheet("Summary").Cell(1, 2).GetString(), named.Worksheet("Summary").Cell(2, 2).GetString()).Should().Be((Laptop.ToString(), "Silver"));
    }

    // ═════════════════════════════ R10 Product profitability ═════════════════════════════

    private static ProfitabilityReportItem Product(
        int rank, string? name, string currency, decimal qty, decimal revenue, decimal cost) =>
        new()
        {
            Rank = rank, ProductUuid = Guid.NewGuid(), ProductName = name, CurrencyCode = currency, QuantitySold = qty, Revenue = revenue, CostOfGoodsSold = cost,
            GrossProfit = revenue - cost,
            MarginPercent = revenue == 0m ? null : Math.Round((revenue - cost) / revenue * 100m, 2, MidpointRounding.AwayFromZero)
        };

    private static readonly ProfitabilityReportItem[] Ranked =
    [
        Product(1, "Laptop", "PKR", 5m, 5300m, 3200m),
        Product(2, "Mouse", "PKR", 10.5m, 210m, 150m),
        Product(3, null, "PKR", 2m, 60m, 90m),
        Product(4, "Cable", "PKR", 4m, 0m, 30m),
        Product(1, "Laptop", "USD", 1m, 900m, 600m),
    ];

    private static ProfitabilityReport Profit(IReadOnlyList<ProfitabilityReportItem>? items = null, ProfitabilityReportCriteria? criteria = null) =>
        new()
        {
            CompanyName = "Northwind Trading", GeneratedAt = Generated,
            Criteria = criteria ?? new ProfitabilityReportCriteria { DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30) },
            Items = [.. items ?? Ranked],
            Totals = [.. (items ?? Ranked).GroupBy(i => i.CurrencyCode).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new ProfitabilityReportTotal
            {
                CurrencyCode = g.Key, ProductCount = g.Count(), Revenue = g.Sum(i => i.Revenue), CostOfGoodsSold = g.Sum(i => i.CostOfGoodsSold), GrossProfit = g.Sum(i => i.GrossProfit),
                MarginPercent = g.Sum(i => i.Revenue) == 0m ? null : Math.Round(g.Sum(i => i.GrossProfit) / g.Sum(i => i.Revenue) * 100m, 2, MidpointRounding.AwayFromZero)
            })],
            TotalRecords = (items ?? Ranked).Count, Page = 1, PageSize = Math.Max(1, (items ?? Ranked).Count), TotalPages = (items ?? Ranked).Count == 0 ? 0 : 1
        };

    [Fact]
    public void The_profitability_pdf_is_a_landscape_pdf_that_names_the_company_report_period_and_how_it_is_ranked()
    {
        var pdf = ProductProfitabilityPdfExporter.Export(Profit());

        ShouldBeLandscapeA4(pdf);
        TextOf(pdf).Should().Contain("Northwind Trading").And.Contain("PRODUCT PROFITABILITY").And.Contain("Issued: 01 Sep 2026 to 30 Sep 2026")
            .And.Contain("Ranked by gross profit, each currency on its own").And.Contain("Only sales with a cost of sales booked")
            .And.Contain("Generated 2026-09-20 10:30 UTC");
    }

    [Fact]
    public void An_open_period_reads_in_words()
    {
        ProductProfitabilityPdfExporter.CriteriaLines(new ProfitabilityReportCriteria())[0].Should().Be("Issued: All dates");
        ProductProfitabilityPdfExporter.CriteriaLines(new ProfitabilityReportCriteria { DateFrom = new DateTime(2026, 9, 1) })[0].Should().Be("Issued: From 01 Sep 2026");
        ProductProfitabilityPdfExporter.CriteriaLines(new ProfitabilityReportCriteria { DateTo = new DateTime(2026, 9, 30) })[0].Should().Be("Issued: Up to 30 Sep 2026");
    }

    [Fact]
    public void The_profitability_pdf_has_a_ranked_row_per_product_a_dash_for_a_missing_name_or_percent_and_a_bold_total_per_currency()
    {
        var text = TextOf(ProductProfitabilityPdfExporter.Export(Profit()));

        text.Should().Contain("# Product Cur. Qty sold Revenue Cost of goods Gross profit Margin %");
        text.Should().Contain("1 Laptop PKR 5 5,300.00 3,200.00 2,100.00 39.62%");
        text.Should().Contain("2 Mouse PKR 10.5 210.00 150.00 60.00 28.57%");
        text.Should().Contain("3 - PKR 2 60.00 90.00 -30.00 -50.00%");
        text.Should().Contain("4 Cable PKR 4 0.00 30.00 -30.00 -");
        text.Should().Contain("1 Laptop USD 1 900.00 600.00 300.00 33.33%");
        text.Should().Contain("Total, 4 products PKR 5,570.00 3,470.00 2,100.00 37.70%");
        text.Should().Contain("Total, 1 products USD 900.00 600.00 300.00 33.33%");
    }

    [Fact]
    public void Nothing_sold_is_a_page_that_says_so_and_has_no_table()
    {
        var text = TextOf(ProductProfitabilityPdfExporter.Export(Profit([])));

        text.Should().Contain("No sales with a cost of sales were issued in this period.").And.NotContain("Cost of goods");
        ProductProfitabilityPdfExporter.Percent(null).Should().Be("-");
        ProductProfitabilityPdfExporter.Percent(-3.5m).Should().Be("-3.50%");
    }

    [Fact]
    public void A_long_profitability_pdf_runs_over_pages_and_prints_every_product_once()
    {
        var items = Enumerable.Range(1, 200).Select(i => Product(i, $"Product {i:000}", "PKR", 1m, i * 10m, i * 6m)).ToList();

        var pdf  = ProductProfitabilityPdfExporter.Export(Profit(items));
        var text = TextOf(pdf);

        using var doc = PdfDocument.Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(3);
        foreach (var item in items) Regex.Matches(text, Regex.Escape(item.ProductName!) + " PKR").Count.Should().Be(1, item.ProductName);
    }

    [Fact]
    public void The_profitability_workbook_has_the_ranked_products_then_the_summary_with_typed_cells()
    {
        using var book = Workbook(ProductProfitabilityExcelExporter.Export(Profit()));

        book.Worksheets.Select(w => w.Name).Should().Equal("Product Profitability", "Summary");
        var ws = book.Worksheet("Product Profitability");
        Enumerable.Range(1, 8).Select(c => ws.Cell(1, c).GetString()).Should().Equal(
            "Rank", "Product", "Currency", "Quantity Sold", "Revenue", "Cost of Goods Sold", "Gross Profit", "Margin %");
        ws.Cell(1, 1).Style.Font.Bold.Should().BeTrue();

        (ws.Cell(2, 1).GetDouble(), ws.Cell(2, 2).GetString(), ws.Cell(2, 3).GetString(), ws.Cell(2, 4).GetDouble(), ws.Cell(2, 5).GetDouble(), ws.Cell(2, 6).GetDouble(), ws.Cell(2, 7).GetDouble(), ws.Cell(2, 8).GetDouble())
            .Should().Be((1d, "Laptop", "PKR", 5d, 5300d, 3200d, 2100d, 39.62d));
        ws.Cell(2, 4).Style.NumberFormat.Format.Should().Be("#,##0.####");
        ws.Cell(2, 5).Style.NumberFormat.Format.Should().Be("#,##0.00");
        ws.Cell(2, 8).Style.NumberFormat.Format.Should().Be("0.00");
        ws.Cell(4, 2).GetString().Should().BeEmpty("a product with no name is an empty cell, not a dash");
        ws.Cell(5, 8).IsEmpty().Should().BeTrue("no revenue, no percent");
        (ws.Cell(6, 1).GetDouble(), ws.Cell(6, 3).GetString()).Should().Be((1d, "USD"));
        ws.LastRowUsed()!.RowNumber().Should().Be(6, "a row to a product and nothing else: totals are on the summary");
    }

    [Fact]
    public void The_profitability_summary_says_what_was_asked_for_and_totals_each_currency()
    {
        using var book = Workbook(ProductProfitabilityExcelExporter.Export(Profit()));
        var ws = book.Worksheet("Summary");

        (ws.Cell(1, 1).GetString(), ws.Cell(1, 2).GetDateTime()).Should().Be(("Issued from", new DateTime(2026, 9, 1)));
        (ws.Cell(2, 1).GetString(), ws.Cell(2, 2).GetDateTime()).Should().Be(("Issued to", new DateTime(2026, 9, 30)));
        Enumerable.Range(1, 6).Select(c => ws.Cell(4, c).GetString()).Should().Equal("Currency", "Products", "Revenue", "Cost of Goods Sold", "Gross Profit", "Margin %");
        (ws.Cell(5, 1).GetString(), ws.Cell(5, 2).GetDouble(), ws.Cell(5, 3).GetDouble(), ws.Cell(5, 4).GetDouble(), ws.Cell(5, 5).GetDouble(), ws.Cell(5, 6).GetDouble())
            .Should().Be(("PKR", 4d, 5570d, 3470d, 2100d, 37.7d));
        (ws.Cell(6, 1).GetString(), ws.Cell(6, 2).GetDouble()).Should().Be(("USD", 1d));
    }

    [Fact]
    public void A_currency_with_no_revenue_has_no_percent_in_either_document()
    {
        var report = Profit([Product(1, "Cable", "PKR", 4m, 0m, 30m)]);

        TextOf(ProductProfitabilityPdfExporter.Export(report)).Should().Contain("Total, 1 products PKR 0.00 30.00 -30.00 -");
        using var book = Workbook(ProductProfitabilityExcelExporter.Export(report));
        (book.Worksheet("Summary").Cell(5, 5).GetDouble(), book.Worksheet("Summary").Cell(5, 6).IsEmpty()).Should().Be((-30d, true));
    }

    [Fact]
    public void An_unfiltered_profitability_summary_says_all_dates()
    {
        using var book = Workbook(ProductProfitabilityExcelExporter.Export(Profit([], new ProfitabilityReportCriteria())));

        (book.Worksheet("Summary").Cell(1, 2).GetString(), book.Worksheet("Summary").Cell(2, 2).GetString()).Should().Be(("All dates", "All dates"));
        book.Worksheet("Product Profitability").LastRowUsed()!.RowNumber().Should().Be(1);
    }

    [Fact]
    public void Both_exporters_refuse_nothing_to_export()
    {
        FluentActions.Invoking(() => ProductLedgerPdfExporter.Export(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ProductLedgerExcelExporter.Export(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ProductProfitabilityPdfExporter.Export(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => ProductProfitabilityExcelExporter.Export(null!)).Should().Throw<ArgumentNullException>();
    }
}
