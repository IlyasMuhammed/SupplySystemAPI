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
/// A29-P9-02 / P9-03 §15 R2 and R3 — the four documents the receivables reports are exported as. The PDFs
/// are read back page by page with PdfPig and the workbooks cell by cell with ClosedXML, so what is
/// asserted is what a person who opens the file sees, not what the exporter thinks it wrote.
/// </summary>
public class ReceivablesExportTests
{
    private static readonly DateTime Generated = new(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(pdf);

    private static string TextOf(byte[] pdf)
    {
        using var doc = Open(pdf);
        return string.Join('\n', doc.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))));
    }

    private static XLWorkbook Workbook(byte[] xlsx) => new(new MemoryStream(xlsx));

    // ── Builders ─────────────────────────────────────────────────────────────

    private static CustomerLedgerReportEntry Entry(
        int seq, DateTime date, string type, string reference, string? narration, string currency, decimal debit, decimal credit, decimal balance) =>
        new()
        {
            SequenceNo = seq, EntryDate = date, EntryType = type, ReferenceType = "Doc", ReferenceId = Guid.NewGuid(), ReferenceNumber = reference,
            Narration = narration, CurrencyCode = currency, DebitAmount = debit, CreditAmount = credit, Balance = balance
        };

    private static readonly CustomerLedgerReportEntry[] LedgerEntries =
    [
        Entry(1, new DateTime(2026, 9, 1, 14, 20, 0), "INVOICE", "SINV-20260901-0001", "Invoice SINV-20260901-0001 for sale order SO-2026-00007", "PKR", 1000m, 0m, 1250m),
        Entry(2, new DateTime(2026, 9, 5), "INVOICE", "SINV-20260905-0002", null, "PKR", 500.50m, 0m, 1750.50m),
        Entry(3, new DateTime(2026, 9, 10), "PAYMENT", "CPAY-20260910-0001", "Payment CPAY-20260910-0001", "PKR", 0m, 300m, 1450.50m),
        Entry(4, new DateTime(2026, 9, 12), "CREDIT_NOTE", "CN-0001", "Goods returned", "PKR", 0m, 50m, 1400.50m),
        Entry(5, new DateTime(2026, 9, 12), "INVOICE", "SINV-USD-0001", "Export order", "USD", 20m, 0m, 20m),
    ];

    private static readonly CustomerLedgerReportSummary[] LedgerSummaries =
    [
        new() { CurrencyCode = "PKR", OpeningBalance = 250m, TotalDebit = 1500.50m, TotalCredit = 350m, ClosingBalance = 1400.50m, EntryCount = 4 },
        new() { CurrencyCode = "USD", OpeningBalance = 0m, TotalDebit = 20m, TotalCredit = 0m, ClosingBalance = 20m, EntryCount = 1 },
    ];

    private static readonly CustomerLedgerReportCriteria LedgerCriteria = new()
    {
        PartnerId = Guid.NewGuid(), CustomerName = "Acme Ltd", DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 30)
    };

    private static CustomerLedgerReport Ledger(
        IReadOnlyList<CustomerLedgerReportEntry>? entries = null, IReadOnlyList<CustomerLedgerReportSummary>? summaries = null,
        CustomerLedgerReportCriteria? criteria = null) =>
        new()
        {
            CompanyName = "Northwind Trading", GeneratedAt = Generated, Criteria = criteria ?? LedgerCriteria,
            Summaries = [.. summaries ?? LedgerSummaries], Entries = [.. entries ?? LedgerEntries],
            TotalRecords = (entries ?? LedgerEntries).Count, Page = 1, PageSize = Math.Max(1, (entries ?? LedgerEntries).Count), TotalPages = 1
        };

    private static AgingReceivablesInvoice Bill(
        string number, string? customer, string currency, DateTime due, int days, decimal total, decimal paid, DateTime? invoiced = null) =>
        new()
        {
            InvoiceUuid = Guid.NewGuid(), InvoiceNumber = number, SaleOrderNumber = "SO-1", PartnerId = Guid.NewGuid(), CustomerName = customer,
            CurrencyCode = currency, InvoiceDate = invoiced ?? due.AddDays(-30), DueDate = due, DaysPastDue = days, Bucket = AgingBuckets.For(days),
            GrandTotal = total, AmountPaid = paid, Outstanding = total - paid
        };

    private static readonly AgingReceivablesInvoice[] Bills =
    [
        Bill("SINV-20260810-0001", "Acme Ltd", "PKR", new DateTime(2026, 9, 15), 5, 100m, 0m),
        Bill("SINV-20260701-0002", "Acme Ltd", "PKR", new DateTime(2026, 8, 11), 40, 250m, 50m),
        Bill("SINV-20260601-0003", "Acme Ltd", "PKR", new DateTime(2026, 7, 12), 70, 300m, 0m),
        Bill("SINV-20260301-0004", "Acme Ltd", "PKR", new DateTime(2026, 5, 22), 121, 400m, 0m),
        Bill("SINV-USD-0005", "Acme Ltd", "USD", new DateTime(2026, 8, 31), 20, 10m, 0m),
        Bill("SINV-20260810-0006", null, "PKR", new DateTime(2026, 9, 1), 19, 50m, 0m),
    ];

    private static readonly AgingReceivablesCustomer[] BillCustomers =
    [
        new() { CustomerName = "Acme Ltd", CurrencyCode = "PKR", InvoiceCount = 4, Days0To30 = 100m, Days31To60 = 200m, Days61To90 = 300m, Over90 = 400m, Total = 1000m },
        new() { CustomerName = "Acme Ltd", CurrencyCode = "USD", InvoiceCount = 1, Days0To30 = 10m, Total = 10m },
        new() { CustomerName = null, CurrencyCode = "PKR", InvoiceCount = 1, Days0To30 = 50m, Total = 50m },
    ];

    private static readonly AgingReceivablesTotal[] BillTotals =
    [
        new() { CurrencyCode = "PKR", InvoiceCount = 5, Days0To30 = 150m, Days31To60 = 200m, Days61To90 = 300m, Over90 = 400m, Total = 1050m },
        new() { CurrencyCode = "USD", InvoiceCount = 1, Days0To30 = 10m, Total = 10m },
    ];

    private static AgingReceivablesReport Aging(
        IReadOnlyList<AgingReceivablesInvoice>? invoices = null, IReadOnlyList<AgingReceivablesCustomer>? customers = null,
        IReadOnlyList<AgingReceivablesTotal>? totals = null, AgingReceivablesCriteria? criteria = null) =>
        new()
        {
            CompanyName = "Northwind Trading", GeneratedAt = Generated,
            Criteria = criteria ?? new AgingReceivablesCriteria { AsOf = new DateTime(2026, 9, 20) },
            Invoices = [.. invoices ?? Bills], Customers = [.. customers ?? BillCustomers], Totals = [.. totals ?? BillTotals],
            TotalRecords = (invoices ?? Bills).Count, Page = 1, PageSize = Math.Max(1, (invoices ?? Bills).Count), TotalPages = 1
        };

    private static AgingReceivablesReport EmptyAging() => Aging([], [], []);

    // ═════════════════════════════ R2 Customer ledger ═════════════════════════════

    [Fact]
    public void The_ledger_pdf_is_a_real_pdf_on_landscape_A4()
    {
        var pdf = CustomerLedgerPdfExporter.Export(Ledger());

        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        using var doc = Open(pdf);
        doc.NumberOfPages.Should().Be(1);
        (Math.Round(doc.GetPage(1).Width), Math.Round(doc.GetPage(1).Height)).Should().Be((842d, 595d));
    }

    [Fact]
    public void The_ledger_pdf_names_the_company_the_report_the_customer_and_the_period()
    {
        var text = TextOf(CustomerLedgerPdfExporter.Export(Ledger()));

        text.Should().Contain("Northwind Trading").And.Contain("CUSTOMER LEDGER")
            .And.Contain("Customer: Acme Ltd").And.Contain("Period: 01 Sep 2026 to 30 Sep 2026")
            .And.Contain("Balances by business date").And.Contain("Generated 2026-09-20 10:30 UTC");
    }

    [Theory]
    [InlineData("2026-09-01", null, "From 01 Sep 2026")]
    [InlineData(null, "2026-09-30", "Up to 30 Sep 2026")]
    [InlineData(null, null, "All dates")]
    public void A_half_open_period_is_worded_as_one(string? from, string? to, string words)
    {
        var criteria = new CustomerLedgerReportCriteria
        {
            CustomerName = "Acme Ltd", DateFrom = from is null ? null : DateTime.Parse(from), DateTo = to is null ? null : DateTime.Parse(to)
        };

        CustomerLedgerPdfExporter.CriteriaLines(criteria)[1].Should().Be($"Period: {words}");
    }

    [Fact]
    public void A_customer_whose_record_is_gone_is_named_by_id_and_a_missing_one_by_a_dash()
    {
        var id = Guid.NewGuid();

        CustomerLedgerPdfExporter.CriteriaLines(new CustomerLedgerReportCriteria { PartnerId = id })[0].Should().Be($"Customer: {id}");
        CustomerLedgerPdfExporter.CriteriaLines(new CustomerLedgerReportCriteria())[0].Should().Be("Customer: -");
    }

    [Fact]
    public void The_ledger_pdf_summarises_each_currency_from_where_it_began_to_where_it_ended()
    {
        var text = TextOf(CustomerLedgerPdfExporter.Export(Ledger()));

        text.Should().Contain("SUMMARY").And.Contain("Opening balance").And.Contain("Closing balance");
        text.Should().Contain("PKR 250.00 1,500.50 350.00 1,400.50 4");
        text.Should().Contain("USD 0.00 20.00 0.00 20.00 1");
    }

    [Fact]
    public void The_ledger_pdf_lists_every_entry_with_its_date_type_reference_narration_and_money()
    {
        var text = TextOf(CustomerLedgerPdfExporter.Export(Ledger()));

        text.Should().Contain("ENTRIES")
            .And.Contain("01 Sep 2026").And.Contain("05 Sep 2026").And.Contain("10 Sep 2026")
            .And.Contain("SINV-20260901-0001").And.Contain("CPAY-20260910-0001").And.Contain("CN-0001")
            .And.Contain("CREDIT NOTE").And.Contain("PAYMENT").And.Contain("Goods returned")
            .And.Contain("1,000.00").And.Contain("500.50").And.Contain("300.00").And.Contain("1,750.50").And.Contain("1,400.50");
    }

    [Fact]
    public void Entries_are_printed_in_the_order_they_were_given_which_is_oldest_first()
    {
        var text = TextOf(CustomerLedgerPdfExporter.Export(Ledger()));

        var at = new[] { "SINV-20260901-0001", "SINV-20260905-0002", "CPAY-20260910-0001", "CN-0001", "SINV-USD-0001" }
            .Select(n => text.IndexOf(n, StringComparison.Ordinal)).ToArray();
        at.Should().OnlyContain(i => i >= 0).And.BeInAscendingOrder();
    }

    [Fact]
    public void A_zero_debit_or_credit_is_left_blank_and_a_missing_narration_is_a_dash()
    {
        var one = Entry(1, new DateTime(2026, 9, 1), "INVOICE", "SINV-X", null, "PKR", 40m, 0m, 40m);

        var text = TextOf(CustomerLedgerPdfExporter.Export(Ledger([one], [LedgerSummaries[0]])));

        // the credit column of a debit is empty, not 0.00: nothing sits between the currency and the two amounts
        text.Should().Contain("SINV-X - PKR 40.00 40.00");
    }

    [Fact]
    public void A_credit_leaves_the_debit_column_empty()
    {
        var credit = Entry(1, new DateTime(2026, 9, 1), "PAYMENT", "CPAY-X", null, "PKR", 0m, 300m, 260m);

        var text = TextOf(CustomerLedgerPdfExporter.Export(Ledger([credit], [LedgerSummaries[0]])));

        // nothing sits between the currency and the credit: the debit is empty, not 0.00
        text.Should().Contain("CPAY-X - PKR 300.00 260.00");
    }

    [Fact]
    public void A_period_with_no_entries_says_so_and_still_shows_where_the_account_stood()
    {
        var summary = new CustomerLedgerReportSummary { CurrencyCode = "PKR", OpeningBalance = 100m, ClosingBalance = 100m };

        var text = TextOf(CustomerLedgerPdfExporter.Export(Ledger([], [summary])));

        text.Should().Contain("No ledger entries in this period.").And.Contain("PKR 100.00 0.00 0.00 100.00 0");
        text.Should().NotContain("ENTRIES");
    }

    [Fact]
    public void A_customer_with_no_history_at_all_is_a_page_that_says_so()
    {
        var text = TextOf(CustomerLedgerPdfExporter.Export(Ledger([], [])));

        text.Should().Contain("No ledger entries in this period.").And.NotContain("SUMMARY");
    }

    [Fact]
    public void The_ledger_pdf_runs_over_pages_each_with_the_column_heads_and_a_page_number_and_prints_every_entry_once()
    {
        var entries = Enumerable.Range(1, 200)
            .Select(i => Entry(i, new DateTime(2026, 9, 1).AddHours(i), "INVOICE", $"SINV-{i:000000}", "Invoice", "PKR", 10m, 0m, 10m * i)).ToList();

        var pdf  = CustomerLedgerPdfExporter.Export(Ledger(entries, [LedgerSummaries[0]]));
        var text = TextOf(pdf);

        using var doc = Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(3);
        foreach (var page in doc.GetPages())
        {
            var words = string.Join(' ', page.GetWords().Select(w => w.Text));
            if (page.Number > 1) words.Should().Contain("Reference", $"page {page.Number} repeats the table head");
            words.Should().Contain($"{page.Number} of {doc.NumberOfPages}");
        }

        foreach (var entry in entries)
            System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(entry.ReferenceNumber)).Count.Should().Be(1, entry.ReferenceNumber);
    }

    [Fact]
    public void A_long_narration_wraps_within_its_cell_and_is_never_cut_off()
    {
        var long_ = Entry(1, new DateTime(2026, 9, 1), "INVOICE", "SINV-X", "Invoice SINV-X for sale order SO-2026-00007 (delivery DLV-2026-00003) raised against the standing agreement of last year", "PKR", 40m, 0m, 40m);

        var text = TextOf(CustomerLedgerPdfExporter.Export(Ledger([long_], [LedgerSummaries[0]])));

        foreach (var word in "standing agreement of last year DLV-2026-00003".Split(' ')) text.Should().Contain(word);
    }

    [Fact]
    public void The_ledger_pdf_carries_a_full_size_statement()
    {
        var entries = Enumerable.Range(1, ReceivablesReportService.MaxRows)
            .Select(i => Entry(i, new DateTime(2026, 1, 1).AddMinutes(i), "INVOICE", $"SINV-{i:000000}", "Invoice", "PKR", 1m, 0m, i)).ToList();

        var pdf = CustomerLedgerPdfExporter.Export(Ledger(entries, [LedgerSummaries[0]]));

        using var doc = Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(100);
    }

    [Fact]
    public void A_null_ledger_report_is_a_programming_error_in_either_document()
    {
        ((Action)(() => CustomerLedgerPdfExporter.Export(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => CustomerLedgerExcelExporter.Export(null!))).Should().Throw<ArgumentNullException>();
    }

    // ── Excel ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_ledger_workbook_has_the_entries_first_and_a_summary_second()
    {
        using var wb = Workbook(CustomerLedgerExcelExporter.Export(Ledger()));

        wb.Worksheets.Select(w => w.Name).Should().Equal("Customer Ledger", "Summary");
        var ws = wb.Worksheet("Customer Ledger");
        Enumerable.Range(1, 8).Select(c => ws.Cell(1, c).GetString()).Should().Equal(
            "Date", "Entry Type", "Reference", "Narration", "Currency", "Debit", "Credit", "Balance");
        ws.Range(1, 1, 1, 8).Cells().Should().OnlyContain(c => c.Style.Font.Bold);
    }

    [Fact]
    public void Each_entry_is_a_row_in_the_order_given_with_typed_cells_and_the_date_without_its_time()
    {
        using var wb = Workbook(CustomerLedgerExcelExporter.Export(Ledger()));
        var ws = wb.Worksheet("Customer Ledger");

        ws.Cell(2, 1).DataType.Should().Be(XLDataType.DateTime);
        ws.Cell(2, 1).GetDateTime().Should().Be(new DateTime(2026, 9, 1), "the 14:20 posting time is not shown");
        ws.Cell(2, 2).GetString().Should().Be("INVOICE");
        ws.Cell(2, 3).GetString().Should().Be("SINV-20260901-0001");
        ws.Cell(2, 4).GetString().Should().Be("Invoice SINV-20260901-0001 for sale order SO-2026-00007");
        ws.Cell(2, 5).GetString().Should().Be("PKR");
        foreach (var (column, value) in new[] { (6, 1000m), (7, 0m), (8, 1250m) })
        {
            ws.Cell(2, column).DataType.Should().Be(XLDataType.Number);
            ((decimal)ws.Cell(2, column).GetDouble()).Should().Be(value);
        }

        ws.Cell(3, 4).GetString().Should().BeEmpty("no narration");
        ((decimal)ws.Cell(3, 6).GetDouble()).Should().Be(500.50m);
        ws.Cell(5, 2).GetString().Should().Be("CREDIT_NOTE");
        ws.Cell(6, 5).GetString().Should().Be("USD");
        ws.LastRowUsed()!.RowNumber().Should().Be(6);
    }

    [Fact]
    public void The_ledger_workbook_formats_dates_and_amounts()
    {
        using var wb = Workbook(CustomerLedgerExcelExporter.Export(Ledger()));
        var ws = wb.Worksheet("Customer Ledger");

        ws.Cell(2, 1).Style.DateFormat.Format.Should().Be("yyyy-mm-dd");
        foreach (var column in new[] { 6, 7, 8 }) ws.Cell(2, column).Style.NumberFormat.Format.Should().Be("#,##0.00");
    }

    [Fact]
    public void The_ledger_summary_sheet_says_whose_account_and_what_period_and_what_each_currency_did()
    {
        using var wb = Workbook(CustomerLedgerExcelExporter.Export(Ledger()));
        var ws = wb.Worksheet("Summary");

        ws.Cell(1, 2).GetString().Should().Be("Acme Ltd");
        ws.Cell(2, 2).GetDateTime().Should().Be(new DateTime(2026, 9, 1));
        ws.Cell(3, 2).GetDateTime().Should().Be(new DateTime(2026, 9, 30));
        Enumerable.Range(1, 6).Select(c => ws.Cell(5, c).GetString()).Should().Equal(
            "Currency", "Opening Balance", "Total Debit", "Total Credit", "Closing Balance", "Entries");

        ws.Cell(6, 1).GetString().Should().Be("PKR");
        (((decimal)ws.Cell(6, 2).GetDouble()), ((decimal)ws.Cell(6, 3).GetDouble()), ((decimal)ws.Cell(6, 4).GetDouble()), ((decimal)ws.Cell(6, 5).GetDouble()), ws.Cell(6, 6).GetDouble())
            .Should().Be((250m, 1500.50m, 350m, 1400.50m, 4d));
        ws.Cell(7, 1).GetString().Should().Be("USD");
        ws.Cell(6, 2).Style.NumberFormat.Format.Should().Be("#,##0.00");
    }

    [Fact]
    public void A_ledger_without_a_range_says_all_dates_and_one_without_a_name_gives_the_id()
    {
        var id = Guid.NewGuid();
        using var wb = Workbook(CustomerLedgerExcelExporter.Export(Ledger(criteria: new CustomerLedgerReportCriteria { PartnerId = id })));
        var ws = wb.Worksheet("Summary");

        ws.Cell(1, 2).GetString().Should().Be(id.ToString());
        ws.Cell(2, 2).GetString().Should().Be("All dates");
        ws.Cell(3, 2).GetString().Should().Be("All dates");
    }

    [Fact]
    public void An_empty_ledger_is_a_sheet_of_column_heads_and_a_summary_of_where_it_stood()
    {
        var summary = new CustomerLedgerReportSummary { CurrencyCode = "PKR", OpeningBalance = 100m, ClosingBalance = 100m };

        using var wb = Workbook(CustomerLedgerExcelExporter.Export(Ledger([], [summary])));

        wb.Worksheet("Customer Ledger").LastRowUsed()!.RowNumber().Should().Be(1);
        ((decimal)wb.Worksheet("Summary").Cell(6, 5).GetDouble()).Should().Be(100m);
    }

    [Fact]
    public void A_full_size_ledger_writes_every_row()
    {
        var entries = Enumerable.Range(1, ReceivablesReportService.MaxRows)
            .Select(i => Entry(i, new DateTime(2026, 1, 1).AddMinutes(i), "INVOICE", $"SINV-{i:000000}", "Invoice", "PKR", 1m, 0m, i)).ToList();

        using var wb = Workbook(CustomerLedgerExcelExporter.Export(Ledger(entries, [LedgerSummaries[0]])));

        wb.Worksheet("Customer Ledger").Cell(1 + ReceivablesReportService.MaxRows, 3).GetString().Should().Be($"SINV-{ReceivablesReportService.MaxRows:000000}");
    }

    // ═════════════════════════════ R3 Aging receivables ═══════════════════════════

    [Fact]
    public void The_aging_pdf_is_a_real_pdf_on_landscape_A4()
    {
        var pdf = AgingReceivablesPdfExporter.Export(Aging());

        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
        using var doc = Open(pdf);
        (Math.Round(doc.GetPage(1).Width), Math.Round(doc.GetPage(1).Height)).Should().Be((842d, 595d));
    }

    [Fact]
    public void The_aging_pdf_names_the_company_the_report_the_day_the_customer_and_how_it_is_aged()
    {
        var text = TextOf(AgingReceivablesPdfExporter.Export(Aging()));

        text.Should().Contain("Northwind Trading").And.Contain("AGING RECEIVABLES")
            .And.Contain("As of: 20 Sep 2026").And.Contain("Customer: All customers")
            .And.Contain("Aged by days past the due date").And.Contain("Generated 2026-09-20 10:30 UTC");
    }

    [Fact]
    public void A_named_customer_is_in_the_heading_by_name_or_failing_that_by_id()
    {
        var id = Guid.NewGuid();

        AgingReceivablesPdfExporter.CriteriaLines(new AgingReceivablesCriteria { AsOf = new DateTime(2026, 9, 20), PartnerId = id, CustomerName = "Acme Ltd" })[1]
            .Should().Be("Customer: Acme Ltd");
        AgingReceivablesPdfExporter.CriteriaLines(new AgingReceivablesCriteria { AsOf = new DateTime(2026, 9, 20), PartnerId = id })[1]
            .Should().Be($"Customer: {id}");
    }

    [Fact]
    public void The_aging_pdf_has_a_row_per_customer_across_the_four_buckets_and_a_bold_total_per_currency()
    {
        var text = TextOf(AgingReceivablesPdfExporter.Export(Aging()));

        text.Should().Contain("OUTSTANDING BY CUSTOMER")
            .And.Contain("0-30 days").And.Contain("31-60 days").And.Contain("61-90 days").And.Contain("90+ days");
        text.Should().Contain("Acme Ltd PKR 4 100.00 200.00 300.00 400.00 1,000.00");
        text.Should().Contain("Acme Ltd USD 1 10.00 0.00 0.00 0.00 10.00");
        text.Should().Contain("- PKR 1 50.00 0.00 0.00 0.00 50.00", "a customer with no name on record is a dash");
        text.Should().Contain("Total PKR 5 150.00 200.00 300.00 400.00 1,050.00");
        text.Should().Contain("Total USD 1 10.00 0.00 0.00 0.00 10.00");
    }

    [Fact]
    public void The_aging_pdf_lists_each_outstanding_invoice_with_its_dates_age_bucket_and_amounts()
    {
        var text = TextOf(AgingReceivablesPdfExporter.Export(Aging()));

        text.Should().Contain("OUTSTANDING INVOICES");
        text.Should().Contain("SINV-20260701-0002 Acme Ltd 12 Jul 2026 11 Aug 2026 40 31-60 PKR 250.00 50.00 200.00");
        text.Should().Contain("SINV-20260301-0004").And.Contain("121 90+ PKR 400.00 0.00 400.00");
        text.Should().Contain("SINV-USD-0005").And.Contain("USD 10.00 0.00 10.00");
    }

    [Fact]
    public void Nothing_outstanding_is_a_page_that_says_which_day_and_has_no_tables()
    {
        var text = TextOf(AgingReceivablesPdfExporter.Export(EmptyAging()));

        text.Should().Contain("No invoices were outstanding on 20 Sep 2026.");
        text.Should().NotContain("OUTSTANDING BY CUSTOMER").And.NotContain("OUTSTANDING INVOICES");
    }

    [Fact]
    public void The_aging_pdf_runs_over_pages_and_prints_every_invoice_once()
    {
        var bills = Enumerable.Range(1, 150)
            .Select(i => Bill($"SINV-{i:000000}", "Acme Ltd", "PKR", new DateTime(2026, 9, 1), 19, 10m, 0m)).ToList();
        var customers = new[] { new AgingReceivablesCustomer { CustomerName = "Acme Ltd", CurrencyCode = "PKR", InvoiceCount = 150, Days0To30 = 1500m, Total = 1500m } };
        var totals    = new[] { new AgingReceivablesTotal { CurrencyCode = "PKR", InvoiceCount = 150, Days0To30 = 1500m, Total = 1500m } };

        var pdf  = AgingReceivablesPdfExporter.Export(Aging(bills, customers, totals));
        var text = TextOf(pdf);

        using var doc = Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(2);
        foreach (var bill in bills)
            System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(bill.InvoiceNumber)).Count.Should().Be(1, bill.InvoiceNumber);
    }

    [Fact]
    public void The_aging_pdf_carries_a_full_size_report()
    {
        var bills = Enumerable.Range(1, ReceivablesReportService.MaxRows)
            .Select(i => Bill($"SINV-{i:000000}", "Acme Ltd", "PKR", new DateTime(2026, 9, 1), 19, 10m, 0m)).ToList();

        var pdf = AgingReceivablesPdfExporter.Export(Aging(bills, [BillCustomers[0]], [BillTotals[0]]));

        using var doc = Open(pdf);
        doc.NumberOfPages.Should().BeGreaterThan(100);
    }

    [Fact]
    public void A_null_aging_report_is_a_programming_error_in_either_document()
    {
        ((Action)(() => AgingReceivablesPdfExporter.Export(null!))).Should().Throw<ArgumentNullException>();
        ((Action)(() => AgingReceivablesExcelExporter.Export(null!))).Should().Throw<ArgumentNullException>();
    }

    // ── Excel ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_aging_workbook_has_the_summary_first_and_the_invoices_second()
    {
        using var wb = Workbook(AgingReceivablesExcelExporter.Export(Aging()));

        wb.Worksheets.Select(w => w.Name).Should().Equal("Summary", "Invoices");
    }

    [Fact]
    public void The_aging_summary_sheet_says_the_day_the_customer_and_how_it_is_aged()
    {
        using var wb = Workbook(AgingReceivablesExcelExporter.Export(Aging()));
        var ws = wb.Worksheet("Summary");

        ws.Cell(1, 2).GetDateTime().Should().Be(new DateTime(2026, 9, 20));
        ws.Cell(1, 2).Style.DateFormat.Format.Should().Be("yyyy-mm-dd");
        ws.Cell(2, 2).GetString().Should().Be("All customers");
        ws.Cell(3, 2).GetString().Should().Be("Days past the due date");
    }

    [Fact]
    public void The_aging_summary_sheet_is_a_row_per_customer_and_currency_then_a_bold_row_per_currency()
    {
        using var wb = Workbook(AgingReceivablesExcelExporter.Export(Aging()));
        var ws = wb.Worksheet("Summary");

        Enumerable.Range(1, 8).Select(c => ws.Cell(5, c).GetString()).Should().Equal(
            "Customer", "Currency", "Invoices", "0-30 days", "31-60 days", "61-90 days", "90+ days", "Total");
        ws.Range(5, 1, 5, 8).Cells().Should().OnlyContain(c => c.Style.Font.Bold);

        (ws.Cell(6, 1).GetString(), ws.Cell(6, 2).GetString(), ws.Cell(6, 3).GetDouble()).Should().Be(("Acme Ltd", "PKR", 4d));
        foreach (var (column, value) in new[] { (4, 100m), (5, 200m), (6, 300m), (7, 400m), (8, 1000m) })
        {
            ws.Cell(6, column).DataType.Should().Be(XLDataType.Number);
            ((decimal)ws.Cell(6, column).GetDouble()).Should().Be(value);
            ws.Cell(6, column).Style.NumberFormat.Format.Should().Be("#,##0.00");
        }

        ws.Cell(8, 1).GetString().Should().BeEmpty("no name on record");

        // rows 6-8 are customers, 9 is blank, 10-11 are the currencies
        ws.Cell(9, 1).IsEmpty().Should().BeTrue();
        (ws.Cell(10, 1).GetString(), ws.Cell(10, 2).GetString(), ws.Cell(10, 3).GetDouble()).Should().Be(("Total", "PKR", 5d));
        ((decimal)ws.Cell(10, 8).GetDouble()).Should().Be(1050m);
        (ws.Cell(11, 1).GetString(), ws.Cell(11, 2).GetString()).Should().Be(("Total", "USD"));
        ws.Range(10, 1, 11, 8).Cells().Should().OnlyContain(c => c.Style.Font.Bold);
        ws.LastRowUsed()!.RowNumber().Should().Be(11);
    }

    [Fact]
    public void The_aging_invoices_sheet_is_a_typed_row_per_invoice_in_the_order_given()
    {
        using var wb = Workbook(AgingReceivablesExcelExporter.Export(Aging()));
        var ws = wb.Worksheet("Invoices");

        Enumerable.Range(1, 11).Select(c => ws.Cell(1, c).GetString()).Should().Equal(
            "Invoice Number", "Sale Order", "Customer", "Invoice Date", "Due Date", "Days Past Due", "Bucket", "Currency", "Total", "Paid", "Outstanding");
        ws.Range(1, 1, 1, 11).Cells().Should().OnlyContain(c => c.Style.Font.Bold);

        ws.Cell(3, 1).GetString().Should().Be("SINV-20260701-0002");
        ws.Cell(3, 2).GetString().Should().Be("SO-1");
        ws.Cell(3, 3).GetString().Should().Be("Acme Ltd");
        ws.Cell(3, 4).DataType.Should().Be(XLDataType.DateTime);
        ws.Cell(3, 4).GetDateTime().Should().Be(new DateTime(2026, 7, 12));
        ws.Cell(3, 5).GetDateTime().Should().Be(new DateTime(2026, 8, 11));
        ws.Cell(3, 6).GetDouble().Should().Be(40d);
        ws.Cell(3, 7).GetString().Should().Be("31-60");
        ws.Cell(3, 8).GetString().Should().Be("PKR");
        (((decimal)ws.Cell(3, 9).GetDouble()), ((decimal)ws.Cell(3, 10).GetDouble()), ((decimal)ws.Cell(3, 11).GetDouble())).Should().Be((250m, 50m, 200m));
        ws.Cell(3, 4).Style.DateFormat.Format.Should().Be("yyyy-mm-dd");
        ws.Cell(3, 9).Style.NumberFormat.Format.Should().Be("#,##0.00");
        ws.Cell(7, 3).GetString().Should().BeEmpty("no name on record");
        ws.LastRowUsed()!.RowNumber().Should().Be(7);
    }

    [Fact]
    public void An_empty_aging_is_a_summary_of_column_heads_and_an_invoice_sheet_of_the_same()
    {
        using var wb = Workbook(AgingReceivablesExcelExporter.Export(EmptyAging()));

        wb.Worksheet("Summary").LastRowUsed()!.RowNumber().Should().Be(5);
        wb.Worksheet("Invoices").LastRowUsed()!.RowNumber().Should().Be(1);
    }

    [Fact]
    public void A_named_customer_is_on_the_summary_sheet_by_name_or_id()
    {
        var id = Guid.NewGuid();

        using var named = Workbook(AgingReceivablesExcelExporter.Export(Aging(criteria:
            new AgingReceivablesCriteria { AsOf = new DateTime(2026, 9, 20), PartnerId = id, CustomerName = "Acme Ltd" })));
        using var unnamed = Workbook(AgingReceivablesExcelExporter.Export(Aging(criteria:
            new AgingReceivablesCriteria { AsOf = new DateTime(2026, 9, 20), PartnerId = id })));

        named.Worksheet("Summary").Cell(2, 2).GetString().Should().Be("Acme Ltd");
        unnamed.Worksheet("Summary").Cell(2, 2).GetString().Should().Be(id.ToString());
    }

    [Fact]
    public void A_full_size_aging_writes_every_invoice_row()
    {
        var bills = Enumerable.Range(1, ReceivablesReportService.MaxRows)
            .Select(i => Bill($"SINV-{i:000000}", "Acme Ltd", "PKR", new DateTime(2026, 9, 1), 19, 10m, 0m)).ToList();

        using var wb = Workbook(AgingReceivablesExcelExporter.Export(Aging(bills, [BillCustomers[0]], [BillTotals[0]])));

        wb.Worksheet("Invoices").Cell(1 + ReceivablesReportService.MaxRows, 1).GetString().Should().Be($"SINV-{ReceivablesReportService.MaxRows:000000}");
    }
}
