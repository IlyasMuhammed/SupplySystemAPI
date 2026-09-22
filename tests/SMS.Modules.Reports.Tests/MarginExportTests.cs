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
/// A29-P9-05 §15 R7 and R8 — the four documents the two reports are exported as. The PDFs are read back page by
/// page with PdfPig and the workbooks cell by cell with ClosedXML, so what is asserted is what a person who opens
/// the file sees, not what the exporter thinks it wrote.
/// </summary>
public class MarginExportTests
{
    private static readonly DateTime Generated = new(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

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

    // ═════════════════════════════ R7 Margin analysis ═════════════════════════════

    private static MarginAnalysisItem Row(
        string? name, string currency, decimal selling, decimal cost, string? detail = null, int lines = 1, decimal? units = null, Guid? id = null) =>
        new()
        {
            GroupId = id ?? Guid.NewGuid(), Name = name, Detail = detail, CurrencyCode = currency, LineCount = lines, Quantity = units,
            SellingValue = selling, Cost = cost, Margin = selling - cost,
            MarginPercent = selling > 0m ? Math.Round((selling - cost) / selling * 100m, 2, MidpointRounding.AwayFromZero) : null,
            AverageSellingPrice = units is > 0m ? Math.Round(selling / units.Value, 2) : null,
            AverageCost = units is > 0m ? Math.Round(cost / units.Value, 2) : null
        };

    private static MarginAnalysisReport Margin(
        string groupBy, IReadOnlyList<MarginAnalysisItem> items, IReadOnlyList<MarginAnalysisTotal>? totals = null, MarginAnalysisCriteria? criteria = null) =>
        new()
        {
            CompanyName = "Northwind Trading", GeneratedAt = Generated,
            Criteria = criteria ?? new MarginAnalysisCriteria { DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30), GroupBy = groupBy },
            Items = [.. items],
            Totals = [.. totals ?? items.GroupBy(i => i.CurrencyCode).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new MarginAnalysisTotal
            {
                CurrencyCode = g.Key, GroupCount = g.Count(), LineCount = g.Sum(i => i.LineCount), SellingValue = g.Sum(i => i.SellingValue),
                Cost = g.Sum(i => i.Cost), Margin = g.Sum(i => i.Margin),
                MarginPercent = g.Sum(i => i.SellingValue) > 0m ? Math.Round(g.Sum(i => i.Margin) / g.Sum(i => i.SellingValue) * 100m, 2, MidpointRounding.AwayFromZero) : null
            })],
            TotalRecords = items.Count, Page = 1, PageSize = Math.Max(1, items.Count), TotalPages = items.Count == 0 ? 0 : 1
        };

    private static readonly MarginAnalysisItem[] ProductRows =
    [
        Row("Laptop", "PKR", 1790m, 1150m, units: 17m, lines: 3),
        Row("Mouse", "PKR", 200m, 150m, units: 10.5m),
        Row(null, "PKR", 60m, 90m, units: 2m),
        Row("Laptop", "USD", 900m, 600m, units: 1m),
    ];

    [Fact]
    public void The_margin_pdf_is_a_landscape_pdf_that_names_the_company_report_period_grouping_and_basis()
    {
        var pdf = MarginAnalysisPdfExporter.Export(Margin("PRODUCT", ProductRows));

        ShouldBeLandscapeA4(pdf);
        TextOf(pdf).Should().Contain("Northwind Trading").And.Contain("MARGIN ANALYSIS")
            .And.Contain("Orders dated: 01 Sep 2026 to 30 Sep 2026").And.Contain("Grouped by: PRODUCT")
            .And.Contain("Selling price after discount against purchase order price, before tax")
            .And.Contain("Generated 2026-09-20 10:30 UTC");
    }

    [Fact]
    public void An_open_range_reads_in_words()
    {
        MarginAnalysisPdfExporter.CriteriaLines(new MarginAnalysisCriteria { GroupBy = "ORDER" })[0].Should().Be("Orders dated: All dates");
        MarginAnalysisPdfExporter.CriteriaLines(new MarginAnalysisCriteria { DateFrom = new DateTime(2026, 9, 1) })[0].Should().Be("Orders dated: From 01 Sep 2026");
        MarginAnalysisPdfExporter.CriteriaLines(new MarginAnalysisCriteria { DateTo = new DateTime(2026, 9, 30) })[0].Should().Be("Orders dated: Up to 30 Sep 2026");
        MarginAnalysisPdfExporter.CriteriaLines(new MarginAnalysisCriteria { GroupBy = "ORDER" })[1].Should().Be("Grouped by: ORDER");
    }

    [Fact]
    public void By_product_the_table_has_units_and_the_average_prices_a_dash_for_a_missing_name_and_a_bold_total_per_currency()
    {
        var text = TextOf(MarginAnalysisPdfExporter.Export(Margin("PRODUCT", ProductRows)));

        text.Should().Contain("Product Cur. Lines Units Avg selling Avg cost Selling value Cost Margin Margin %");
        text.Should().Contain("Laptop PKR 3 17 105.29 67.65 1,790.00 1,150.00 640.00 35.75%");
        text.Should().Contain("Mouse PKR 1 10.5 19.05 14.29 200.00 150.00 50.00 25.00%");
        text.Should().Contain("- PKR 1 2 30.00 45.00 60.00 90.00 -30.00 -50.00%");
        text.Should().Contain("Laptop USD 1 1 900.00 600.00 900.00 600.00 300.00 33.33%");
        text.Should().Contain("Total, 3 products PKR 5 2,050.00 1,390.00 660.00 32.20%");
        text.Should().Contain("Total, 1 products USD 1 900.00 600.00 300.00 33.33%");
    }

    [Fact]
    public void By_customer_there_are_no_unit_columns_because_units_of_different_products_do_not_add()
    {
        var rows = new[] { Row("Acme Ltd", "PKR", 1750m, 1100m, lines: 3), Row("Globex Corp", "PKR", 240m, 200m) };

        var text = TextOf(MarginAnalysisPdfExporter.Export(Margin("CUSTOMER", rows)));

        text.Should().Contain("Customer Cur. Lines Selling value Cost Margin Margin %").And.NotContain("Units").And.NotContain("Avg selling");
        text.Should().Contain("Acme Ltd PKR 3 1,750.00 1,100.00 650.00 37.14%");
        text.Should().Contain("Total, 2 customers PKR 4 1,990.00 1,300.00 690.00 34.67%");
    }

    [Fact]
    public void By_order_each_row_names_the_order_and_its_customer()
    {
        var rows = new[] { Row("SO-2026-00001", "PKR", 1200m, 750m, detail: "Acme Ltd", lines: 2), Row("SO-2026-00002", "USD", 30m, 20m, detail: null) };

        var text = TextOf(MarginAnalysisPdfExporter.Export(Margin("ORDER", rows)));

        text.Should().Contain("Sale order Customer Cur. Lines Selling value Cost Margin Margin %");
        text.Should().Contain("SO-2026-00001 Acme Ltd PKR 2 1,200.00 750.00 450.00 37.50%");
        text.Should().Contain("SO-2026-00002 - USD 1 30.00 20.00 10.00 33.33%");
        text.Should().Contain("Total, 1 orders PKR 2 1,200.00 750.00 450.00 37.50%");
    }

    [Fact]
    public void A_row_that_sold_at_no_price_prints_a_dash_for_its_percent()
    {
        var text = TextOf(MarginAnalysisPdfExporter.Export(Margin("ORDER", [Row("SO-1", "PKR", 0m, 30m)])));

        text.Should().Contain("SO-1 - PKR 1 0.00 30.00 -30.00 -");
    }

    [Fact]
    public void Lines_left_out_for_want_of_a_purchase_order_are_said_so_under_the_table()
    {
        var totals = new[] { new MarginAnalysisTotal { CurrencyCode = "PKR", GroupCount = 1, LineCount = 1, UncostedLineCount = 4, SellingValue = 1000m, Cost = 600m, Margin = 400m, MarginPercent = 40m } };

        var text = TextOf(MarginAnalysisPdfExporter.Export(Margin("PRODUCT", [Row("Laptop", "PKR", 1000m, 600m, units: 10m)], totals)));

        text.Should().Contain("4 order lines have no purchase order behind them").And.Contain("They are not in these figures");
        MarginAnalysisPdfExporter.UncostedNote(Margin("PRODUCT", ProductRows)).Should().BeNull("nothing was left out");
        TextOf(MarginAnalysisPdfExporter.Export(Margin("PRODUCT", ProductRows))).Should().NotContain("no purchase order behind them");
    }

    [Fact]
    public void Nothing_to_analyse_is_a_page_that_says_so_and_has_no_table_but_still_says_what_was_left_out()
    {
        var totals = new[] { new MarginAnalysisTotal { CurrencyCode = "PKR", UncostedLineCount = 2 } };

        var text = TextOf(MarginAnalysisPdfExporter.Export(Margin("PRODUCT", [], totals)));

        text.Should().Contain("No order lines with a purchase order behind them match these filters.").And.NotContain("Selling value");
        text.Should().Contain("2 order lines have no purchase order behind them");
        TextOf(MarginAnalysisPdfExporter.Export(Margin("PRODUCT", [], []))).Should().NotContain("order lines have no purchase order behind them");
    }

    [Fact]
    public void A_currency_with_only_uncosted_lines_has_no_total_row_in_the_table()
    {
        var totals = new[]
        {
            new MarginAnalysisTotal { CurrencyCode = "PKR", GroupCount = 1, LineCount = 1, SellingValue = 1000m, Cost = 600m, Margin = 400m, MarginPercent = 40m },
            new MarginAnalysisTotal { CurrencyCode = "USD", UncostedLineCount = 3 }
        };

        var text = TextOf(MarginAnalysisPdfExporter.Export(Margin("PRODUCT", [Row("Laptop", "PKR", 1000m, 600m, units: 10m)], totals)));

        text.Should().Contain("Total, 1 products PKR").And.NotContain("Total, 0 products");
    }

    [Fact]
    public void A_long_margin_pdf_runs_over_pages_and_prints_every_row_once()
    {
        var items = Enumerable.Range(1, 200).Select(i => Row($"Product {i:000}", "PKR", i * 10m, i * 6m, units: 1m)).ToList();

        var pdf  = MarginAnalysisPdfExporter.Export(Margin("PRODUCT", items));
        var text = TextOf(pdf);

        using var doc = PdfDocument.Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(3);
        foreach (var item in items)
            Regex.Matches(text, Regex.Escape(item.Name!) + " PKR").Count.Should().Be(1, item.Name);
    }

    [Theory]
    [InlineData("PRODUCT")]
    [InlineData("CUSTOMER")]
    [InlineData("ORDER")]
    public void Every_grouping_renders_and_names_its_own_column(string grouping)
    {
        var name = grouping switch { "PRODUCT" => "Product", "CUSTOMER" => "Customer", _ => "Sale order Customer" };

        TextOf(MarginAnalysisPdfExporter.Export(Margin(grouping, [Row("First", "PKR", 100m, 60m, units: 1m)]))).Should().Contain($"{name} Cur.");
    }

    [Fact]
    public void The_margin_workbook_has_the_rows_then_the_summary_and_the_columns_of_its_grouping()
    {
        using var book = Workbook(MarginAnalysisExcelExporter.Export(Margin("PRODUCT", ProductRows)));

        book.Worksheets.Select(w => w.Name).Should().Equal("Margin Analysis", "Summary");
        var ws = book.Worksheet("Margin Analysis");
        Enumerable.Range(1, 10).Select(c => ws.Cell(1, c).GetString()).Should().Equal(
            "Product", "Cur.", "Lines", "Units", "Avg selling", "Avg cost", "Selling value", "Cost", "Margin", "Margin %");
        ws.Cell(1, 11).IsEmpty().Should().BeTrue();
        ws.Cell(1, 1).Style.Font.Bold.Should().BeTrue();

        using var byOrder = Workbook(MarginAnalysisExcelExporter.Export(Margin("ORDER", [Row("SO-1", "PKR", 100m, 60m, detail: "Acme Ltd")])));
        Enumerable.Range(1, 8).Select(c => byOrder.Worksheet("Margin Analysis").Cell(1, c).GetString()).Should().Equal(
            "Sale order", "Customer", "Cur.", "Lines", "Selling value", "Cost", "Margin", "Margin %");
    }

    [Fact]
    public void The_margin_workbook_cells_are_typed_so_they_sum_and_filter()
    {
        using var book = Workbook(MarginAnalysisExcelExporter.Export(Margin("PRODUCT", ProductRows)));
        var ws = book.Worksheet("Margin Analysis");

        ws.Cell(2, 1).GetString().Should().Be("Laptop");
        ws.Cell(2, 2).GetString().Should().Be("PKR");
        ws.Cell(2, 3).Value.IsNumber.Should().BeTrue();
        (ws.Cell(2, 3).GetDouble(), ws.Cell(2, 4).GetDouble(), ws.Cell(2, 7).GetDouble(), ws.Cell(2, 8).GetDouble(), ws.Cell(2, 9).GetDouble(), ws.Cell(2, 10).GetDouble())
            .Should().Be((3d, 17d, 1790d, 1150d, 640d, 35.75d));
        ws.Cell(2, 4).Style.NumberFormat.Format.Should().Be("#,##0.####");
        ws.Cell(2, 7).Style.NumberFormat.Format.Should().Be("#,##0.00");
        ws.Cell(2, 10).Style.NumberFormat.Format.Should().Be("0.00");
        ws.Cell(4, 1).GetString().Should().BeEmpty("a product with no name is an empty cell, not a dash");
        ws.LastRowUsed()!.RowNumber().Should().Be(5, "a row to a product and nothing else: totals are on the summary");
        ws.Cell(5, 4).GetDouble().Should().Be(1d);
    }

    [Fact]
    public void A_margin_percent_with_nothing_to_take_it_of_is_an_empty_cell()
    {
        using var book = Workbook(MarginAnalysisExcelExporter.Export(Margin("ORDER", [Row("SO-1", "PKR", 0m, 30m)])));

        book.Worksheet("Margin Analysis").Cell(2, 8).IsEmpty().Should().BeTrue();
        book.Worksheet("Margin Analysis").Cell(2, 7).GetDouble().Should().Be(-30d);
    }

    [Fact]
    public void The_margin_summary_says_what_was_asked_for_and_has_a_total_per_currency_with_the_lines_left_out()
    {
        var totals = new[]
        {
            new MarginAnalysisTotal { CurrencyCode = "PKR", GroupCount = 3, LineCount = 5, UncostedLineCount = 2, SellingValue = 2050m, Cost = 1390m, Margin = 660m, MarginPercent = 32.2m },
            new MarginAnalysisTotal { CurrencyCode = "USD", UncostedLineCount = 1 }
        };

        using var book = Workbook(MarginAnalysisExcelExporter.Export(Margin("CUSTOMER", ProductRows, totals)));
        var ws = book.Worksheet("Summary");

        (ws.Cell(1, 1).GetString(), ws.Cell(1, 2).GetDateTime()).Should().Be(("Orders from", new DateTime(2026, 9, 1)));
        (ws.Cell(2, 1).GetString(), ws.Cell(2, 2).GetDateTime()).Should().Be(("Orders to", new DateTime(2026, 9, 30)));
        (ws.Cell(3, 1).GetString(), ws.Cell(3, 2).GetString()).Should().Be(("Grouped by", "CUSTOMER"));
        Enumerable.Range(1, 8).Select(c => ws.Cell(5, c).GetString()).Should().Equal(
            "Currency", "Groups", "Lines", "Lines Without Cost", "Selling Value", "Cost", "Margin", "Margin %");
        Enumerable.Range(1, 8).Select(c => ws.Cell(6, c).Value.IsNumber ? ws.Cell(6, c).GetDouble().ToString("0.##") : ws.Cell(6, c).GetString()).Should().Equal(
            "PKR", "3", "5", "2", "2050", "1390", "660", "32.2");
        (ws.Cell(7, 1).GetString(), ws.Cell(7, 4).GetDouble(), ws.Cell(7, 8).IsEmpty()).Should().Be(("USD", 1d, true));
    }

    [Fact]
    public void An_unfiltered_margin_summary_says_all_dates()
    {
        using var book = Workbook(MarginAnalysisExcelExporter.Export(Margin("PRODUCT", [], [], new MarginAnalysisCriteria())));
        var ws = book.Worksheet("Summary");

        (ws.Cell(1, 2).GetString(), ws.Cell(2, 2).GetString(), ws.Cell(3, 2).GetString()).Should().Be(("All dates", "All dates", "PRODUCT"));
        book.Worksheet("Margin Analysis").LastRowUsed()!.RowNumber().Should().Be(1, "only the heads");
    }

    // ═════════════════════════════ R8 Sales vs purchase ═════════════════════════════

    private static SalesVsPurchaseItem Period(
        string label, string currency, decimal revenue, decimal cost, int invoices = 1, decimal uncosted = 0m, DateTime? start = null) =>
        new()
        {
            PeriodStart = start ?? new DateTime(2026, 9, 1), PeriodLabel = label, CurrencyCode = currency, InvoiceCount = invoices,
            Revenue = revenue, CostOfGoodsSold = cost, GrossMargin = revenue - cost,
            GrossMarginPercent = revenue > 0m ? Math.Round((revenue - cost) / revenue * 100m, 2, MidpointRounding.AwayFromZero) : null,
            UncostedRevenue = uncosted
        };

    private static SalesVsPurchaseReport Vs(
        IReadOnlyList<SalesVsPurchaseItem> items, SalesVsPurchaseCriteria? criteria = null) =>
        new()
        {
            CompanyName = "Northwind Trading", GeneratedAt = Generated,
            Criteria = criteria ?? new SalesVsPurchaseCriteria { DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30), Period = "MONTH" },
            Items = [.. items],
            Totals = [.. items.GroupBy(i => i.CurrencyCode).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new SalesVsPurchaseTotal
            {
                CurrencyCode = g.Key, PeriodCount = g.Count(), InvoiceCount = g.Sum(i => i.InvoiceCount), Revenue = g.Sum(i => i.Revenue),
                CostOfGoodsSold = g.Sum(i => i.CostOfGoodsSold), GrossMargin = g.Sum(i => i.GrossMargin), UncostedRevenue = g.Sum(i => i.UncostedRevenue),
                GrossMarginPercent = g.Sum(i => i.Revenue) > 0m ? Math.Round(g.Sum(i => i.GrossMargin) / g.Sum(i => i.Revenue) * 100m, 2, MidpointRounding.AwayFromZero) : null
            })],
            TotalRecords = items.Count, Page = 1, PageSize = Math.Max(1, items.Count), TotalPages = items.Count == 0 ? 0 : 1
        };

    private static readonly SalesVsPurchaseItem[] Periods =
    [
        Period("2026-08", "PKR", 78303.61m, 68195.16m, invoices: 13, uncosted: 4031.38m, start: new DateTime(2026, 8, 1)),
        Period("2026-09", "PKR", 82419.25m, 47362.00m, invoices: 14),
        Period("2026-09", "USD", 8554.50m, 7386.48m, invoices: 2),
    ];

    [Fact]
    public void The_sales_vs_purchase_pdf_is_a_landscape_pdf_that_names_the_company_report_range_and_period()
    {
        var pdf = SalesVsPurchasePdfExporter.Export(Vs(Periods));

        ShouldBeLandscapeA4(pdf);
        TextOf(pdf).Should().Contain("Northwind Trading").And.Contain("SALES VS PURCHASE")
            .And.Contain("Issued: 01 Sep 2026 to 30 Sep 2026").And.Contain("By month")
            .And.Contain("Revenue before tax; cost as booked when each invoice was issued")
            .And.Contain("Generated 2026-09-20 10:30 UTC");
    }

    [Fact]
    public void An_open_range_and_each_period_read_in_words()
    {
        SalesVsPurchasePdfExporter.CriteriaLines(new SalesVsPurchaseCriteria { Period = "WEEK" }).Take(2).Should().Equal("Issued: All dates", "By week");
        SalesVsPurchasePdfExporter.CriteriaLines(new SalesVsPurchaseCriteria { DateFrom = new DateTime(2026, 9, 1), Period = "DAY" }).Take(2).Should().Equal("Issued: From 01 Sep 2026", "By day");
        SalesVsPurchasePdfExporter.CriteriaLines(new SalesVsPurchaseCriteria { DateTo = new DateTime(2026, 9, 30) })[0].Should().Be("Issued: Up to 30 Sep 2026");
    }

    [Fact]
    public void The_sales_vs_purchase_pdf_has_a_row_per_period_and_currency_and_a_bold_total_per_currency()
    {
        var text = TextOf(SalesVsPurchasePdfExporter.Export(Vs(Periods)));

        text.Should().Contain("Period Cur. Invoices Revenue Cost of goods Gross margin Margin % Revenue, no cost");
        text.Should().Contain("2026-08 PKR 13 78,303.61 68,195.16 10,108.45 12.91% 4,031.38");
        text.Should().Contain("2026-09 PKR 14 82,419.25 47,362.00 35,057.25 42.54% 0.00");
        text.Should().Contain("2026-09 USD 2 8,554.50 7,386.48 1,168.02 13.65% 0.00");
        text.Should().Contain("Total, 2 periods PKR 27 160,722.86 115,557.16 45,165.70 28.10% 4,031.38");
        text.Should().Contain("Total, 1 periods USD 2 8,554.50 7,386.48 1,168.02 13.65% 0.00");
    }

    [Fact]
    public void Revenue_with_no_cost_behind_it_is_said_so_under_the_table_and_only_when_there_is_some()
    {
        TextOf(SalesVsPurchasePdfExporter.Export(Vs(Periods))).Should().Contain("Some invoices have no cost of sales booked").And.Contain("overstated");

        var clean = Vs([Period("2026-09", "PKR", 100m, 60m)]);
        SalesVsPurchasePdfExporter.UncostedNote(clean).Should().BeNull();
        TextOf(SalesVsPurchasePdfExporter.Export(clean)).Should().NotContain("no cost of sales booked");
    }

    [Fact]
    public void A_period_with_no_revenue_prints_a_dash_for_its_percent()
    {
        var text = TextOf(SalesVsPurchasePdfExporter.Export(Vs([Period("2026-09", "PKR", 0m, 30m)])));

        text.Should().Contain("2026-09 PKR 1 0.00 30.00 -30.00 - 0.00");
        SalesVsPurchasePdfExporter.Percent(null).Should().Be("-");
        SalesVsPurchasePdfExporter.Percent(-3.5m).Should().Be("-3.50%");
    }

    [Fact]
    public void No_sales_is_a_page_that_says_so_and_has_no_table()
    {
        var text = TextOf(SalesVsPurchasePdfExporter.Export(Vs([])));

        text.Should().Contain("No sales were issued in this period.").And.NotContain("Cost of goods");
    }

    [Fact]
    public void A_long_sales_vs_purchase_pdf_runs_over_pages_and_prints_every_period_once()
    {
        var days = Enumerable.Range(0, 200).Select(i => Period($"2027-{i:000}", "PKR", i * 10m + 1m, i * 6m, start: new DateTime(2027, 1, 1).AddDays(i))).ToList();

        var pdf  = SalesVsPurchasePdfExporter.Export(Vs(days));
        var text = TextOf(pdf);

        using var doc = PdfDocument.Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(3);
        foreach (var day in days) Regex.Matches(text, Regex.Escape(day.PeriodLabel) + " PKR").Count.Should().Be(1, day.PeriodLabel);
    }

    [Fact]
    public void The_sales_vs_purchase_workbook_has_the_periods_then_the_summary_with_typed_cells()
    {
        using var book = Workbook(SalesVsPurchaseExcelExporter.Export(Vs(Periods)));

        book.Worksheets.Select(w => w.Name).Should().Equal("Sales vs Purchase", "Summary");
        var ws = book.Worksheet("Sales vs Purchase");
        Enumerable.Range(1, 9).Select(c => ws.Cell(1, c).GetString()).Should().Equal(
            "Period", "Period Start", "Currency", "Invoices", "Revenue", "Cost of Goods Sold", "Gross Margin", "Margin %", "Revenue With No Cost");

        ws.Cell(2, 1).GetString().Should().Be("2026-08");
        ws.Cell(2, 2).GetDateTime().Should().Be(new DateTime(2026, 8, 1));
        ws.Cell(2, 2).Style.DateFormat.Format.Should().Be("yyyy-mm-dd");
        ws.Cell(2, 3).GetString().Should().Be("PKR");
        (ws.Cell(2, 4).GetDouble(), ws.Cell(2, 5).GetDouble(), ws.Cell(2, 6).GetDouble(), ws.Cell(2, 7).GetDouble(), ws.Cell(2, 8).GetDouble(), ws.Cell(2, 9).GetDouble())
            .Should().Be((13d, 78303.61d, 68195.16d, 10108.45d, 12.91d, 4031.38d));
        ws.Cell(2, 5).Style.NumberFormat.Format.Should().Be("#,##0.00");
        ws.LastRowUsed()!.RowNumber().Should().Be(4);
    }

    [Fact]
    public void A_sales_vs_purchase_percent_with_no_revenue_is_an_empty_cell()
    {
        using var book = Workbook(SalesVsPurchaseExcelExporter.Export(Vs([Period("2026-09", "PKR", 0m, 30m)])));

        book.Worksheet("Sales vs Purchase").Cell(2, 8).IsEmpty().Should().BeTrue();
        book.Worksheet("Sales vs Purchase").Cell(2, 7).GetDouble().Should().Be(-30d);
    }

    [Fact]
    public void The_sales_vs_purchase_summary_says_what_was_asked_for_and_totals_each_currency()
    {
        using var book = Workbook(SalesVsPurchaseExcelExporter.Export(Vs(Periods)));
        var ws = book.Worksheet("Summary");

        (ws.Cell(1, 1).GetString(), ws.Cell(1, 2).GetDateTime()).Should().Be(("Issued from", new DateTime(2026, 9, 1)));
        (ws.Cell(2, 1).GetString(), ws.Cell(2, 2).GetDateTime()).Should().Be(("Issued to", new DateTime(2026, 9, 30)));
        (ws.Cell(3, 1).GetString(), ws.Cell(3, 2).GetString()).Should().Be(("Period", "MONTH"));
        Enumerable.Range(1, 8).Select(c => ws.Cell(5, c).GetString()).Should().Equal(
            "Currency", "Periods", "Invoices", "Revenue", "Cost of Goods Sold", "Gross Margin", "Margin %", "Revenue With No Cost");
        (ws.Cell(6, 1).GetString(), ws.Cell(6, 2).GetDouble(), ws.Cell(6, 3).GetDouble(), ws.Cell(6, 4).GetDouble(), ws.Cell(6, 8).GetDouble())
            .Should().Be(("PKR", 2d, 27d, 160722.86d, 4031.38d));
        ws.Cell(7, 1).GetString().Should().Be("USD");
    }

    [Fact]
    public void An_unfiltered_sales_vs_purchase_summary_says_all_dates()
    {
        using var book = Workbook(SalesVsPurchaseExcelExporter.Export(Vs([], new SalesVsPurchaseCriteria { Period = "WEEK" })));
        var ws = book.Worksheet("Summary");

        (ws.Cell(1, 2).GetString(), ws.Cell(2, 2).GetString(), ws.Cell(3, 2).GetString()).Should().Be(("All dates", "All dates", "WEEK"));
    }

    [Fact]
    public void Both_exporters_refuse_nothing_to_export()
    {
        FluentActions.Invoking(() => MarginAnalysisPdfExporter.Export(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => MarginAnalysisExcelExporter.Export(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SalesVsPurchasePdfExporter.Export(null!)).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => SalesVsPurchaseExcelExporter.Export(null!)).Should().Throw<ArgumentNullException>();
    }
}
