using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Modules.Reports.Services.Exports;
using UglyToad.PdfPig;
using Xunit;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-01 §15 R1 — the two documents the register is exported as. The PDF is read back page by page
/// with PdfPig and the workbook cell by cell with ClosedXML, so what is asserted is what a person who opens
/// the file sees, not what the exporter thinks it wrote.
/// </summary>
public class SalesOrderRegisterExportTests
{
    private static readonly SalesOrderRegisterItem[] Sample =
    [
        RegisterWorld.Item("SO-2026-00003", "Globex Corp", "PARTIALLY_FULFILLED", "SELF_PICKUP", "USD", 1000m, 50m, 152m, 1102m, 3, new DateTime(2026, 9, 12), new DateTime(2026, 9, 30)),
        RegisterWorld.Item("SO-2026-00002", "Acme Ltd", "CONFIRMED", "SHIP", "PKR", 2500000.5m, 0m, 0m, 2500000.5m, 1, new DateTime(2026, 9, 10)),
        RegisterWorld.Item("SO-2026-00001", null, "DRAFT", "SHIP", "PKR", 10m, 0m, 1.6m, 11.6m, 0, new DateTime(2026, 9, 1)),
    ];

    private static readonly SalesOrderRegisterCriteria SampleCriteria = new()
    {
        DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30), Status = "PARTIALLY_FULFILLED",
        PartnerId = Guid.NewGuid(), CustomerName = "Globex Corp", DeliveryMode = "SELF_PICKUP"
    };

    // ── PDF ──────────────────────────────────────────────────────────────────

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(pdf);

    private static string TextOf(byte[] pdf)
    {
        using var doc = Open(pdf);
        return string.Join('\n', doc.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))));
    }

    [Fact]
    public void The_pdf_is_a_real_pdf_on_landscape_A4()
    {
        var pdf = SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(Sample));

        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        using var doc = Open(pdf);
        doc.NumberOfPages.Should().Be(1);
        var page = doc.GetPage(1);
        page.Width.Should().BeGreaterThan(page.Height, "a register with eleven columns is printed landscape");
        (Math.Round(page.Width), Math.Round(page.Height)).Should().Be((842d, 595d));
    }

    [Fact]
    public void The_pdf_names_the_company_and_the_report_and_when_it_was_generated()
    {
        var text = TextOf(SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(Sample)));

        text.Should().Contain("Northwind Trading").And.Contain("SALES ORDER REGISTER").And.Contain("Generated 2026-09-20 10:30 UTC");
    }

    [Fact]
    public void The_pdf_says_what_the_register_is_of()
    {
        var text = TextOf(SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(Sample, SampleCriteria)));

        text.Should().Contain("Order date: 01 Sep 2026 to 30 Sep 2026")
            .And.Contain("Status: PARTIALLY FULFILLED")
            .And.Contain("Customer: Globex Corp")
            .And.Contain("Delivery mode: SELF PICKUP");
    }

    [Fact]
    public void An_unfiltered_register_says_all_of_everything()
    {
        var text = TextOf(SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(Sample)));

        text.Should().Contain("Order date: All dates").And.Contain("Status: All").And.Contain("Customer: All customers").And.Contain("Delivery mode: All");
    }

    [Theory]
    [InlineData("2026-09-01", null, "From 01 Sep 2026")]
    [InlineData(null, "2026-09-30", "Up to 30 Sep 2026")]
    [InlineData("2026-09-10", "2026-09-10", "10 Sep 2026 to 10 Sep 2026")]
    public void A_half_open_range_is_worded_as_one(string? from, string? to, string words)
    {
        var criteria = new SalesOrderRegisterCriteria
        {
            DateFrom = from is null ? null : DateTime.Parse(from), DateTo = to is null ? null : DateTime.Parse(to)
        };

        SalesOrderRegisterPdfExporter.CriteriaLines(criteria)[0].Should().Be($"Order date: {words}");
    }

    [Fact]
    public void A_named_customer_whose_record_is_gone_is_identified_by_id_rather_than_left_blank()
    {
        var id = Guid.NewGuid();

        SalesOrderRegisterPdfExporter.CriteriaLines(new SalesOrderRegisterCriteria { PartnerId = id, CustomerName = null })[2]
            .Should().Be($"Customer: {id}");
    }

    [Fact]
    public void The_pdf_lists_every_order_with_its_customer_status_delivery_mode_and_money()
    {
        var text = TextOf(SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(Sample)));

        text.Should().Contain("SO-2026-00003").And.Contain("SO-2026-00002").And.Contain("SO-2026-00001")
            .And.Contain("Globex").And.Contain("Acme")
            .And.Contain("PARTIALLY FULFILLED").And.Contain("SELF PICKUP").And.Contain("CONFIRMED").And.Contain("DRAFT")
            .And.Contain("12 Sep 2026").And.Contain("10 Sep 2026").And.Contain("01 Sep 2026")
            .And.Contain("1,000.00").And.Contain("152.00").And.Contain("1,102.00")
            .And.Contain("2,500,000.50").And.Contain("11.60");
    }

    [Fact]
    public void The_longest_status_and_a_nine_figure_amount_each_stay_on_one_line()
    {
        var big  = RegisterWorld.Item("SO-2026-00009", "A customer with a fairly long registered name Pvt Ltd", "PARTIALLY_FULFILLED", "SELF_PICKUP",
            "PKR", 123456789.12m, 1234567.89m, 20000000m, 142222221.23m);

        var text = TextOf(SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report([big])));

        text.Should().Contain("PARTIALLY FULFILLED").And.Contain("SELF PICKUP")
            .And.Contain("123,456,789.12").And.Contain("1,234,567.89").And.Contain("20,000,000.00").And.Contain("142,222,221.23");
        // A long name wraps within its cell, so its words are checked one by one: none may be cut off.
        foreach (var word in "A customer with a fairly long registered name Pvt Ltd".Split(' ').Where(w => w.Length > 2))
            text.Should().Contain(word);
    }

    [Fact]
    public void An_order_is_printed_in_the_order_it_was_given_which_is_the_orders_newest_first()
    {
        var text = TextOf(SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(Sample)));

        var at = new[] { "SO-2026-00003", "SO-2026-00002", "SO-2026-00001" }.Select(n => text.IndexOf(n, StringComparison.Ordinal)).ToArray();
        at.Should().OnlyContain(i => i >= 0).And.BeInAscendingOrder();
    }

    [Fact]
    public void A_customer_that_could_not_be_found_prints_a_dash_not_the_word_null()
    {
        var text = TextOf(SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report([Sample[2]])));

        text.Should().NotContain("null");
        text.Should().Contain("SO-2026-00001");
    }

    [Fact]
    public void The_pdf_totals_are_one_row_per_currency_with_the_count_of_orders()
    {
        var text = TextOf(SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(Sample)));

        text.Should().Contain("TOTALS (3 orders)");
        var totals = text[text.IndexOf("TOTALS", StringComparison.Ordinal)..];
        // PKR: 2 orders, 2,500,010.50 subtotal, 1.60 tax, 2,500,012.10 total. USD: 1 order.
        totals.Should().Contain("PKR 2 2,500,010.50 0.00 1.60 2,500,012.10");
        totals.Should().Contain("USD 1 1,000.00 50.00 152.00 1,102.00");
    }

    [Fact]
    public void Totals_say_how_many_orders_matched_not_how_many_are_on_the_page()
    {
        var report = RegisterWorld.Report(Sample);
        report.TotalRecords = 250;
        report.Totals[0].OrderCount = 248;

        TextOf(SalesOrderRegisterPdfExporter.Export(report)).Should().Contain("TOTALS (250 orders)");
    }

    [Fact]
    public void An_empty_register_is_a_document_that_says_nothing_matched_and_has_no_table()
    {
        var text = TextOf(SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report([])));

        text.Should().Contain("No sale orders match these filters.").And.Contain("SALES ORDER REGISTER");
        text.Should().NotContain("TOTALS").And.NotContain("SO No.");
    }

    [Fact]
    public void Without_a_letterhead_company_the_header_has_a_placeholder_rather_than_nothing()
    {
        var report = RegisterWorld.Report(Sample);
        report.CompanyName = null;

        TextOf(SalesOrderRegisterPdfExporter.Export(report)).Should().Contain("Company Name");
    }

    [Fact]
    public void A_long_register_runs_over_several_pages_each_with_the_column_heads_and_a_page_number()
    {
        var items = Enumerable.Range(1, 200).Select(i => RegisterWorld.Item($"SO-2026-{i:00000}", "Acme Ltd", currency: "PKR")).ToList();

        var pdf = SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(items));

        using var doc = Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(3);
        foreach (var page in doc.GetPages())
        {
            var words = string.Join(' ', page.GetWords().Select(w => w.Text));
            words.Should().Contain("SO No.", $"page {page.Number} repeats the table head");
            words.Should().Contain($"{page.Number} of {doc.NumberOfPages}");
        }

        var all = TextOf(pdf);
        all.Should().Contain("SO-2026-00001").And.Contain("SO-2026-00200").And.Contain("TOTALS (200 orders)");
    }

    [Fact]
    public void Every_order_is_printed_exactly_once_however_many_pages_it_takes()
    {
        var items = Enumerable.Range(1, 150).Select(i => RegisterWorld.Item($"SO-2026-{i:00000}")).ToList();

        var text = TextOf(SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(items)));

        foreach (var item in items)
            System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(item.SoNumber)).Count.Should().Be(1, item.SoNumber);
    }

    [Fact]
    public void The_export_limit_is_a_register_that_QuestPDF_can_actually_lay_out()
    {
        // The service refuses an export above MaxExportRows; the exporter must be able to print exactly that many.
        var items = Enumerable.Range(1, SalesReportService.MaxExportRows).Select(i => RegisterWorld.Item($"SO-2026-{i:00000}")).ToList();

        var pdf = SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(items));

        using var doc = Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(100);
        doc.GetPage(doc.NumberOfPages).Text.Should().Contain("TOTALS");
    }

    [Fact]
    public void A_null_report_is_a_programming_error()
    {
        var act = () => SalesOrderRegisterPdfExporter.Export(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Excel ────────────────────────────────────────────────────────────────

    private static XLWorkbook Workbook(byte[] xlsx) => new(new MemoryStream(xlsx));

    [Fact]
    public void The_workbook_has_one_sheet_with_the_column_heads_in_bold_on_row_one()
    {
        using var wb = Workbook(SalesOrderRegisterExcelExporter.Export(RegisterWorld.Report(Sample)));

        wb.Worksheets.Count.Should().Be(1);
        var ws = wb.Worksheet("Sales Order Register");
        Enumerable.Range(1, 12).Select(c => ws.Cell(1, c).GetString()).Should().Equal(
            "SO Number", "Order Date", "Expected Delivery", "Customer", "Status", "Delivery Mode", "Currency", "Lines", "Subtotal", "Discount", "Tax", "Total");
        ws.Range(1, 1, 1, 12).Cells().Should().OnlyContain(c => c.Style.Font.Bold);
    }

    [Fact]
    public void Each_order_is_a_row_in_the_order_given_with_typed_cells()
    {
        using var wb = Workbook(SalesOrderRegisterExcelExporter.Export(RegisterWorld.Report(Sample)));
        var ws = wb.Worksheet(1);

        ws.Cell(2, 1).GetString().Should().Be("SO-2026-00003");
        ws.Cell(2, 2).DataType.Should().Be(XLDataType.DateTime);
        ws.Cell(2, 2).GetDateTime().Should().Be(new DateTime(2026, 9, 12));
        ws.Cell(2, 3).GetDateTime().Should().Be(new DateTime(2026, 9, 30));
        ws.Cell(2, 4).GetString().Should().Be("Globex Corp");
        ws.Cell(2, 5).GetString().Should().Be("PARTIALLY_FULFILLED");
        ws.Cell(2, 6).GetString().Should().Be("SELF_PICKUP");
        ws.Cell(2, 7).GetString().Should().Be("USD");
        ws.Cell(2, 8).DataType.Should().Be(XLDataType.Number);
        ws.Cell(2, 8).GetDouble().Should().Be(3);
        foreach (var (column, value) in new[] { (9, 1000m), (10, 50m), (11, 152m), (12, 1102m) })
        {
            ws.Cell(2, column).DataType.Should().Be(XLDataType.Number, "amounts are numbers, so the sheet can be summed");
            ((decimal)ws.Cell(2, column).GetDouble()).Should().Be(value);
        }

        ws.Cell(3, 1).GetString().Should().Be("SO-2026-00002");
        ((decimal)ws.Cell(3, 12).GetDouble()).Should().Be(2500000.5m);
        ws.Cell(4, 1).GetString().Should().Be("SO-2026-00001");
    }

    [Fact]
    public void An_order_with_no_expected_delivery_date_or_customer_leaves_those_cells_empty()
    {
        using var wb = Workbook(SalesOrderRegisterExcelExporter.Export(RegisterWorld.Report(Sample)));
        var ws = wb.Worksheet(1);

        ws.Cell(3, 3).IsEmpty().Should().BeTrue("no expected delivery date");
        ws.Cell(4, 4).GetString().Should().BeEmpty("no customer name");
    }

    [Fact]
    public void Amounts_and_dates_carry_formats_that_read_as_money_and_days()
    {
        using var wb = Workbook(SalesOrderRegisterExcelExporter.Export(RegisterWorld.Report(Sample)));
        var ws = wb.Worksheet(1);

        ws.Cell(2, 2).Style.DateFormat.Format.Should().Be("yyyy-mm-dd");
        ws.Cell(2, 3).Style.DateFormat.Format.Should().Be("yyyy-mm-dd");
        foreach (var column in new[] { 9, 10, 11, 12 })
            ws.Cell(2, column).Style.NumberFormat.Format.Should().Be("#,##0.00");
    }

    [Fact]
    public void The_totals_sit_below_the_orders_one_bold_row_per_currency_and_are_not_one_grand_sum_of_mixed_currencies()
    {
        using var wb = Workbook(SalesOrderRegisterExcelExporter.Export(RegisterWorld.Report(Sample)));
        var ws = wb.Worksheet(1);

        // rows 2-4 are orders, 5 is blank, 6 says what the block is, 7-8 are the currencies
        ws.Cell(5, 1).IsEmpty().Should().BeTrue();
        ws.Cell(6, 1).GetString().Should().Be("Totals for 3 orders");

        var pkr = 7; var usd = 8;
        ws.Cell(pkr, 1).GetString().Should().Be("Total");
        ws.Cell(pkr, 7).GetString().Should().Be("PKR");
        ws.Cell(pkr, 8).GetDouble().Should().Be(2);
        ((decimal)ws.Cell(pkr, 9).GetDouble()).Should().Be(2500010.5m);
        ((decimal)ws.Cell(pkr, 11).GetDouble()).Should().Be(1.6m);
        ((decimal)ws.Cell(pkr, 12).GetDouble()).Should().Be(2500012.1m);
        ws.Cell(usd, 7).GetString().Should().Be("USD");
        ((decimal)ws.Cell(usd, 12).GetDouble()).Should().Be(1102m);
        ws.Range(pkr, 1, usd, 12).Cells().Should().OnlyContain(c => c.Style.Font.Bold);
        ws.LastRowUsed()!.RowNumber().Should().Be(usd);
    }

    [Fact]
    public void The_totals_say_how_many_orders_matched_even_when_the_rows_given_are_fewer()
    {
        var report = RegisterWorld.Report(Sample);
        report.TotalRecords = 90;

        using var wb = Workbook(SalesOrderRegisterExcelExporter.Export(report));

        wb.Worksheet(1).Cell(6, 1).GetString().Should().Be("Totals for 90 orders");
    }

    [Fact]
    public void An_empty_register_is_a_sheet_of_column_heads_and_a_zero_total_line_and_nothing_else()
    {
        using var wb = Workbook(SalesOrderRegisterExcelExporter.Export(RegisterWorld.Report([])));
        var ws = wb.Worksheet(1);

        ws.Cell(1, 1).GetString().Should().Be("SO Number");
        ws.Cell(3, 1).GetString().Should().Be("Totals for 0 orders");
        ws.LastRowUsed()!.RowNumber().Should().Be(3);
    }

    [Fact]
    public void The_workbook_opens_as_a_valid_xlsx_package()
    {
        var bytes = SalesOrderRegisterExcelExporter.Export(RegisterWorld.Report(Sample));

        (bytes[0], bytes[1]).Should().Be(((byte)'P', (byte)'K'), "an xlsx is a zip");
        var act = () => Workbook(bytes).Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void A_full_size_export_writes_every_row()
    {
        var items = Enumerable.Range(1, SalesReportService.MaxExportRows).Select(i => RegisterWorld.Item($"SO-2026-{i:00000}")).ToList();

        using var wb = Workbook(SalesOrderRegisterExcelExporter.Export(RegisterWorld.Report(items)));
        var ws = wb.Worksheet(1);

        ws.Cell(1 + SalesReportService.MaxExportRows, 1).GetString().Should().Be($"SO-2026-{SalesReportService.MaxExportRows:00000}");
        ws.Cell(SalesReportService.MaxExportRows + 3, 1).GetString().Should().Be($"Totals for {SalesReportService.MaxExportRows} orders");
    }

    [Fact]
    public void A_null_report_is_a_programming_error_here_too()
    {
        var act = () => SalesOrderRegisterExcelExporter.Export(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}
