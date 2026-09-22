using FluentAssertions;
using Moq;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Reports.Tests.ReceivablesReportWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-02 §15 R2 — the customer ledger report: one customer's account over a range of days, oldest
/// entry first, each with the balance after it, opened from what was owed when the range began.
/// </summary>
public class CustomerLedgerReportTests
{
    private readonly ReceivablesReportWorld _w = new();

    private static CustomerLedgerReportFilter For(Guid partner, DateTime? from = null, DateTime? to = null, int page = 1, int pageSize = 20) =>
        new() { PartnerId = partner, DateFrom = from, DateTo = to, Page = page, PageSize = pageSize };

    private static string[] Refs(CustomerLedgerReport r) => [.. r.Entries.Select(e => e.ReferenceNumber)];
    private static decimal[] Balances(CustomerLedgerReport r) => [.. r.Entries.Select(e => e.Balance)];

    /// <summary>Invoices of 1000 on 1 Sept and 500 on 5 Sept, and a payment of 300 on 10 Sept.</summary>
    private void SeptemberAccount()
    {
        var first = _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 1000m);
        _w.Invoice(_w.Acme, "SINV-2", D(9, 5), 500m);
        _w.Payment(_w.Acme, "CPAY-1", D(9, 10), 300m, [(first, 300m)]);
    }

    // ── What the report is of ────────────────────────────────────────────────

    [Fact]
    public async Task Every_entry_is_listed_oldest_first_with_the_balance_after_it()
    {
        SeptemberAccount();

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme));

        Refs(report).Should().Equal("SINV-1", "SINV-2", "CPAY-1");
        Balances(report).Should().Equal(1000m, 1500m, 1200m);
        report.Entries.Select(e => e.SequenceNo).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task An_entry_carries_its_type_reference_narration_currency_and_amounts()
    {
        SeptemberAccount();

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme));

        var invoice = report.Entries[0];
        (invoice.EntryType, invoice.ReferenceType, invoice.ReferenceNumber, invoice.Narration, invoice.CurrencyCode)
            .Should().Be(("INVOICE", "SalesInvoice", "SINV-1", "Invoice SINV-1", "PKR"));
        (invoice.DebitAmount, invoice.CreditAmount, invoice.EntryDate).Should().Be((1000m, 0m, D(9, 1)));
        invoice.ReferenceId.Should().NotBeEmpty();

        var payment = report.Entries[2];
        (payment.EntryType, payment.ReferenceType, payment.DebitAmount, payment.CreditAmount).Should().Be(("PAYMENT", "CustomerPayment", 0m, 300m));
    }

    [Fact]
    public async Task The_summary_says_where_the_account_began_what_moved_and_where_it_ended()
    {
        SeptemberAccount();

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme));

        var s = report.Summaries.Should().ContainSingle().Subject;
        (s.CurrencyCode, s.OpeningBalance, s.TotalDebit, s.TotalCredit, s.ClosingBalance, s.EntryCount)
            .Should().Be(("PKR", 0m, 1500m, 300m, 1200m, 3));
    }

    [Fact]
    public async Task Only_that_customers_entries_are_in_it()
    {
        SeptemberAccount();
        _w.Invoice(_w.Globex, "SINV-OTHER", D(9, 3), 9999m);

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme));

        Refs(report).Should().NotContain("SINV-OTHER");
        report.Summaries.Single().TotalDebit.Should().Be(1500m);
    }

    [Fact]
    public async Task The_criteria_name_the_customer_and_the_days_as_whole_days_and_the_report_says_when_and_for_whom_it_was_made()
    {
        SeptemberAccount();

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, new DateTime(2026, 9, 1, 18, 0, 0), new DateTime(2026, 9, 30, 3, 0, 0)));

        (report.Criteria.PartnerId, report.Criteria.CustomerName, report.Criteria.DateFrom, report.Criteria.DateTo)
            .Should().Be(((Guid?)_w.Acme, "Acme Ltd", (DateTime?)D(9, 1), (DateTime?)D(9, 30)));
        report.CompanyName.Should().Be("Northwind Trading");
        report.GeneratedAt.Should().Be(_w.Now);
    }

    // ── The range ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_range_that_starts_later_opens_from_what_was_owed_the_day_before()
    {
        SeptemberAccount();

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, from: D(9, 5)));

        Refs(report).Should().Equal("SINV-2", "CPAY-1");
        Balances(report).Should().Equal(1500m, 1200m);
        var s = report.Summaries.Single();
        (s.OpeningBalance, s.TotalDebit, s.TotalCredit, s.ClosingBalance, s.EntryCount).Should().Be((1000m, 500m, 300m, 1200m, 2));
    }

    [Fact]
    public async Task A_range_that_ends_earlier_closes_at_what_was_owed_then_and_ignores_what_came_after()
    {
        SeptemberAccount();

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, to: D(9, 7)));

        Refs(report).Should().Equal("SINV-1", "SINV-2");
        report.Summaries.Single().ClosingBalance.Should().Be(1500m);
    }

    [Fact]
    public async Task The_range_is_whole_days_inclusive_at_both_ends_whatever_time_of_day_an_entry_was_posted()
    {
        _w.Invoice(_w.Acme, "SINV-BEFORE", new DateTime(2026, 9, 4, 23, 59, 59), 10m);
        _w.Invoice(_w.Acme, "SINV-FIRST", new DateTime(2026, 9, 5, 0, 0, 0), 20m);
        _w.Invoice(_w.Acme, "SINV-FIRST-LATE", new DateTime(2026, 9, 5, 16, 45, 0), 30m);
        _w.Invoice(_w.Acme, "SINV-LAST", new DateTime(2026, 9, 10, 23, 59, 59), 40m);
        _w.Invoice(_w.Acme, "SINV-AFTER", new DateTime(2026, 9, 11, 0, 0, 0), 50m);

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, new DateTime(2026, 9, 5, 18, 0, 0), new DateTime(2026, 9, 10, 3, 0, 0)));

        Refs(report).Should().Equal("SINV-FIRST", "SINV-FIRST-LATE", "SINV-LAST");
        report.Summaries.Single().OpeningBalance.Should().Be(10m, "the entry of 4 Sept 23:59 is before the range and is in what was owed when it began");
        report.Summaries.Single().ClosingBalance.Should().Be(100m);
    }

    [Fact]
    public async Task One_day_can_be_asked_for_by_giving_it_as_both_ends()
    {
        SeptemberAccount();

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, D(9, 5), D(9, 5)));

        Refs(report).Should().Equal("SINV-2");
        var s = report.Summaries.Single();
        (s.OpeningBalance, s.ClosingBalance).Should().Be((1000m, 1500m));
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var act = () => _w.Service().GetCustomerLedgerAsync(For(_w.Acme, D(9, 20), D(9, 1)));

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("start date is after its end date");
    }

    // ── Business date, not posting order ─────────────────────────────────────

    [Fact]
    public async Task A_receipt_keyed_late_but_dated_earlier_sits_where_its_money_came_in()
    {
        var invoice = _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 1000m);
        _w.Invoice(_w.Acme, "SINV-2", D(10, 5), 400m);
        _w.Payment(_w.Acme, "CPAY-1", D(9, 20), 600m, [(invoice, 600m)], keyedAt: D(10, 6));

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme));

        // the receipt was posted last but dated between the two invoices
        Refs(report).Should().Equal("SINV-1", "CPAY-1", "SINV-2");
        Balances(report).Should().Equal(1000m, 400m, 800m);
    }

    [Fact]
    public async Task A_period_that_ended_before_a_late_entry_was_keyed_does_not_count_entries_posted_after_it_but_dated_after()
    {
        var invoice = _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 1000m);
        _w.Invoice(_w.Acme, "SINV-2", D(10, 5), 400m);
        _w.Payment(_w.Acme, "CPAY-1", D(9, 20), 600m, [(invoice, 600m)], keyedAt: D(10, 6));

        var september = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, D(9, 1), D(9, 30)));

        Refs(september).Should().Equal("SINV-1", "CPAY-1");
        september.Summaries.Single().ClosingBalance.Should().Be(400m,
            "September closed at 1000 less the 600 received in it; the October invoice posted between them is not September's");
    }

    [Fact]
    public async Task With_no_range_the_closing_balance_is_the_customers_current_balance_as_the_ledger_holds_it()
    {
        var invoice = _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 1000m);
        _w.Invoice(_w.Acme, "SINV-2", D(10, 5), 400m);
        var last = _w.Payment(_w.Acme, "CPAY-1", D(9, 20), 600m, [(invoice, 600m)], keyedAt: D(10, 6));

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme));

        using var db = _w.Db();
        var stored = db.CustomerLedgerEntries.Where(e => e.PartnerId == _w.Acme).OrderBy(e => e.SequenceNo).Last().RunningBalance;
        report.Summaries.Single().ClosingBalance.Should().Be(stored).And.Be(800m);
        last.PaymentNumber.Should().Be("CPAY-1");
    }

    [Fact]
    public async Task Entries_of_one_day_stay_in_the_order_they_were_posted_so_a_credit_never_runs_ahead_of_its_invoice()
    {
        var invoice = _w.Invoice(_w.Acme, "SINV-1", new DateTime(2026, 9, 8, 15, 0, 0), 1000m);
        _w.Payment(_w.Acme, "CPAY-1", D(9, 8), 600m, [(invoice, 600m)]);

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme));

        // the payment is dated midnight, before the invoice's 15:00, but was posted after it
        Refs(report).Should().Equal("SINV-1", "CPAY-1");
        Balances(report).Should().Equal(1000m, 400m);
    }

    // ── Currencies ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Each_currency_has_its_own_running_balance_and_its_own_summary()
    {
        _w.Invoice(_w.Acme, "SINV-PKR", D(9, 1), 1000m, currency: "PKR");
        _w.Invoice(_w.Acme, "SINV-USD", D(9, 2), 50m, currency: "USD");
        var pkr = _w.Invoice(_w.Acme, "SINV-PKR-2", D(9, 2), 300m, currency: "PKR");
        _w.Payment(_w.Acme, "CPAY-1", D(9, 3), 200m, [(pkr, 200m)], currency: "PKR");

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme));

        report.Entries.Select(e => (e.ReferenceNumber, e.CurrencyCode, e.Balance)).Should().Equal(
            ("SINV-PKR", "PKR", 1000m), ("SINV-USD", "USD", 50m), ("SINV-PKR-2", "PKR", 1300m), ("CPAY-1", "PKR", 1100m));
        report.Summaries.Select(s => (s.CurrencyCode, s.OpeningBalance, s.TotalDebit, s.TotalCredit, s.ClosingBalance, s.EntryCount)).Should().Equal(
            ("PKR", 0m, 1300m, 200m, 1100m, 3), ("USD", 0m, 50m, 0m, 50m, 1));
    }

    [Fact]
    public async Task A_currency_still_owed_from_before_the_range_is_a_line_even_if_nothing_moved_in_it()
    {
        _w.Invoice(_w.Acme, "SINV-PKR", D(8, 1), 1000m, currency: "PKR");
        _w.Invoice(_w.Acme, "SINV-USD", D(9, 5), 10m, currency: "USD");

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, D(9, 1), D(9, 30)));

        report.Summaries.Select(s => (s.CurrencyCode, s.OpeningBalance, s.EntryCount, s.ClosingBalance))
              .Should().Equal(("PKR", 1000m, 0, 1000m), ("USD", 0m, 1, 10m));
    }

    [Fact]
    public async Task A_currency_settled_to_nothing_before_the_range_and_untouched_in_it_is_not_a_line()
    {
        var eur = _w.Invoice(_w.Acme, "SINV-EUR", D(8, 1), 5m, currency: "EUR");
        _w.Payment(_w.Acme, "CPAY-EUR", D(8, 2), 5m, [(eur, 5m)], currency: "EUR");
        _w.Invoice(_w.Acme, "SINV-PKR", D(9, 5), 10m);

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, D(9, 1), D(9, 30)));

        report.Summaries.Select(s => s.CurrencyCode).Should().Equal("PKR");
    }

    // ── Paging and volume ────────────────────────────────────────────────────

    [Fact]
    public async Task A_page_is_a_slice_of_the_statement_whose_balances_and_summary_are_still_those_of_the_whole()
    {
        for (var i = 1; i <= 5; i++) _w.Invoice(_w.Acme, $"SINV-{i}", D(9, i), 100m);

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, page: 2, pageSize: 2));

        Refs(report).Should().Equal("SINV-3", "SINV-4");
        Balances(report).Should().Equal(300m, 400m);
        (report.Page, report.PageSize, report.TotalRecords, report.TotalPages).Should().Be((2, 2, 5, 3));
        var s = report.Summaries.Single();
        (s.TotalDebit, s.ClosingBalance, s.EntryCount).Should().Be((500m, 500m, 5));
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_but_still_says_how_many_there_are()
    {
        _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 100m);

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, page: 9, pageSize: 10));

        report.Entries.Should().BeEmpty();
        (report.TotalRecords, report.TotalPages).Should().Be((1, 1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public async Task Page_size_is_kept_between_one_and_a_hundred(int asked, int used)
    {
        _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 100m);

        (await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, pageSize: asked))).PageSize.Should().Be(used);
    }

    [Fact]
    public async Task The_export_carries_every_entry_whatever_the_paging_asked_for()
    {
        for (var i = 1; i <= 12; i++) _w.Invoice(_w.Acme, $"SINV-{i:00}", D(9, 1).AddDays(i), 10m);

        var report = await _w.Service().GetCustomerLedgerForExportAsync(For(_w.Acme, page: 3, pageSize: 2));

        report.Entries.Should().HaveCount(12);
        (report.Page, report.PageSize, report.TotalRecords, report.TotalPages).Should().Be((1, 12, 12, 1));
        Balances(report).Last().Should().Be(120m);
    }

    [Fact]
    public async Task An_export_of_an_empty_period_is_an_empty_statement_not_a_failure()
    {
        _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 100m);

        var report = await _w.Service().GetCustomerLedgerForExportAsync(For(_w.Acme, D(11, 1), D(11, 30)));

        report.Entries.Should().BeEmpty();
        (report.TotalRecords, report.Page, report.PageSize).Should().Be((0, 1, 1));
        report.Summaries.Single().Should().BeEquivalentTo(new CustomerLedgerReportSummary { CurrencyCode = "PKR", OpeningBalance = 100m, ClosingBalance = 100m });
    }

    [Fact]
    public async Task A_statement_takes_exactly_the_most_it_allows_and_refuses_one_more()
    {
        _w.BulkLedger(_w.Acme, ReceivablesReportService.MaxRows, new DateTime(2026, 1, 1));

        var atTheLimit = await _w.Service().GetCustomerLedgerForExportAsync(For(_w.Acme));
        atTheLimit.Entries.Should().HaveCount(ReceivablesReportService.MaxRows);
        atTheLimit.Summaries.Single().ClosingBalance.Should().Be(ReceivablesReportService.MaxRows);

        _w.Ledger(_w.Acme, "INVOICE", new DateTime(2026, 6, 1), 1m, 0m);

        foreach (var ask in new Func<Task>[]
        {
            () => _w.Service().GetCustomerLedgerAsync(For(_w.Acme)),
            () => _w.Service().GetCustomerLedgerForExportAsync(For(_w.Acme))
        })
        {
            var message = (await ask.Should().ThrowAsync<BadRequestException>()).Which.Message;
            message.Should().Contain($"{ReceivablesReportService.MaxRows + 1} ledger entries").And.Contain("Narrow the date range");
        }
    }

    [Fact]
    public async Task Narrowing_the_range_brings_an_over_large_ledger_back_within_the_limit_and_keeps_the_opening_balance()
    {
        _w.BulkLedger(_w.Acme, ReceivablesReportService.MaxRows + 5, new DateTime(2026, 1, 1));

        // one entry a minute from 1 Jan: the first 1440 are 1 Jan, the next 1440 are 2 Jan
        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme, new DateTime(2026, 1, 2), new DateTime(2026, 1, 2)));

        report.TotalRecords.Should().Be(1440);
        var s = report.Summaries.Single();
        (s.OpeningBalance, s.ClosingBalance).Should().Be((1440m, 2880m));
    }

    // ── Tenancy and who ──────────────────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_entries_are_neither_listed_nor_in_the_opening_or_the_totals()
    {
        _w.Invoice(_w.Acme, "SINV-MINE", D(9, 5), 100m);
        _w.Invoice(_w.Acme, "SINV-THEIRS-EARLY", D(8, 1), 7000m, org: OtherOrg);
        _w.Invoice(_w.Acme, "SINV-THEIRS", D(9, 6), 9000m, org: OtherOrg);

        var report = await _w.Service(Org).GetCustomerLedgerAsync(For(_w.Acme, D(9, 1), D(9, 30)));

        Refs(report).Should().Equal("SINV-MINE");
        var s = report.Summaries.Single();
        (s.OpeningBalance, s.TotalDebit, s.ClosingBalance).Should().Be((0m, 100m, 100m));
    }

    [Fact]
    public async Task The_export_is_tenant_scoped_too()
    {
        _w.Invoice(_w.Acme, "SINV-MINE", D(9, 5), 100m);
        _w.Invoice(_w.Acme, "SINV-THEIRS", D(9, 6), 9000m, org: OtherOrg);

        Refs(await _w.Service(Org).GetCustomerLedgerForExportAsync(For(_w.Acme))).Should().Equal("SINV-MINE");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_customer_is_a_bad_request_because_a_ledger_is_one_customers_account(bool empty)
    {
        var filter = new CustomerLedgerReportFilter { PartnerId = empty ? Guid.Empty : null };

        var act = () => _w.Service().GetCustomerLedgerAsync(filter);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("Name the customer");
    }

    [Fact]
    public async Task A_customer_nobody_has_heard_of_is_not_found_rather_than_an_empty_account()
    {
        var act = () => _w.Service().GetCustomerLedgerAsync(For(Guid.NewGuid()));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Another_organizations_customer_is_not_found_because_it_has_no_entries_here()
    {
        var theirs = Guid.NewGuid();
        _w.Ledger(theirs, "INVOICE", D(9, 1), 100m, 0m, org: OtherOrg);

        var act = () => _w.Service(Org).GetCustomerLedgerAsync(For(theirs));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_known_customer_with_no_entries_gets_an_empty_account_that_names_them()
    {
        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Globex));

        report.Entries.Should().BeEmpty();
        report.Summaries.Should().BeEmpty();
        report.Criteria.CustomerName.Should().Be("Globex Corp");
        (report.TotalRecords, report.TotalPages).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_customer_with_entries_but_no_name_on_record_still_gets_their_account_with_no_name()
    {
        var stranger = Guid.NewGuid();
        _w.Ledger(stranger, "INVOICE", D(9, 1), 100m, 0m);

        var report = await _w.Service().GetCustomerLedgerAsync(For(stranger));

        report.Entries.Should().HaveCount(1);
        report.Criteria.CustomerName.Should().BeNull();
    }

    [Fact]
    public async Task A_customer_with_entries_only_before_the_range_is_an_empty_period_not_a_missing_customer()
    {
        var stranger = Guid.NewGuid();
        _w.Ledger(stranger, "INVOICE", D(8, 1), 100m, 0m);

        var report = await _w.Service().GetCustomerLedgerAsync(For(stranger, D(9, 1), D(9, 30)));

        report.Entries.Should().BeEmpty();
        report.Summaries.Single().ClosingBalance.Should().Be(100m);
    }

    [Fact]
    public async Task With_no_letterhead_company_the_name_is_null_rather_than_blank()
    {
        _w.CompanyName = "  ";
        _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 100m);

        (await _w.Service().GetCustomerLedgerAsync(For(_w.Acme))).CompanyName.Should().BeNull();
    }

    [Fact]
    public async Task The_customers_name_is_looked_up_once()
    {
        _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 100m);
        var names = new Mock<ISupplierNameLookupService>();

        await _w.Service(names: names).GetCustomerLedgerAsync(For(_w.Acme));

        names.Verify(n => n.GetNamesAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == _w.Acme)), Times.Once);
        names.Verify(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
    }

    [Fact]
    public async Task A_null_filter_is_a_programming_error()
    {
        var act = () => _w.Service().GetCustomerLedgerAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── The rules, over many entries ─────────────────────────────────────────

    private sealed record Posted(DateTime Date, decimal Debit, decimal Credit, int Sequence);

    /// <summary>
    /// Eighty entries posted in a fixed pseudo-random order onto dates between 1 Aug and 31 Oct, some dated
    /// before entries already posted, as receipts keyed late are. What the rules say the report must show is
    /// worked out from this list alone, not from anything the service returns.
    /// </summary>
    private List<Posted> Scatter()
    {
        var random = new Random(20260920);
        var posted = new List<Posted>();

        for (var i = 1; i <= 80; i++)
        {
            var date   = new DateTime(2026, 8, 1).AddDays(random.Next(0, 92)).AddMinutes(random.Next(0, 3) * 480);
            var amount = random.Next(1, 500) + random.Next(0, 100) / 100m;
            var debit  = random.Next(0, 3) != 0;

            _w.Ledger(_w.Acme, debit ? "INVOICE" : "PAYMENT", date, debit ? amount : 0m, debit ? 0m : amount);
            posted.Add(new Posted(date, debit ? amount : 0m, debit ? 0m : amount, i));
        }

        return posted;
    }

    public static IEnumerable<object?[]> Ranges() =>
    [
        [null, null],
        [new DateTime(2026, 9, 1), new DateTime(2026, 9, 30)],
        [new DateTime(2026, 8, 15), new DateTime(2026, 8, 15)],
        [new DateTime(2026, 10, 1), null],
        [null, new DateTime(2026, 9, 15)],
        [new DateTime(2027, 1, 1), null],
    ];

    [Theory]
    [MemberData(nameof(Ranges))]
    public async Task Whatever_the_range_the_statement_adds_up_and_agrees_with_the_entries_it_was_worked_from(DateTime? from, DateTime? to)
    {
        var posted = Scatter();

        var report = await _w.Service().GetCustomerLedgerForExportAsync(For(_w.Acme, from, to));

        var start = from?.Date;
        var end   = to?.Date.AddDays(1);
        var inRange = posted.Where(p => (start is null || p.Date >= start) && (end is null || p.Date < end)).ToList();
        var expectedOpening = start is null ? 0m : posted.Where(p => p.Date < start).Sum(p => p.Debit - p.Credit);

        // Every entry in the range, once, by business date and then posting order.
        report.Entries.Select(e => e.SequenceNo).Should().Equal(inRange.OrderBy(p => p.Date.Date).ThenBy(p => p.Sequence).Select(p => p.Sequence));

        // Each balance is the one before it plus its own debit less its credit, starting from the opening.
        var before = expectedOpening;
        foreach (var e in report.Entries)
        {
            e.Balance.Should().Be(before + e.DebitAmount - e.CreditAmount, $"entry {e.SequenceNo}");
            before = e.Balance;
        }

        // And the summary says the same as the entries: opening + debits - credits = closing.
        if (report.Summaries.Count == 0)
        {
            inRange.Should().BeEmpty();
            expectedOpening.Should().Be(0m);
            return;
        }

        var s = report.Summaries.Single();
        s.OpeningBalance.Should().Be(expectedOpening);
        s.TotalDebit.Should().Be(inRange.Sum(p => p.Debit));
        s.TotalCredit.Should().Be(inRange.Sum(p => p.Credit));
        s.ClosingBalance.Should().Be(s.OpeningBalance + s.TotalDebit - s.TotalCredit);
        s.ClosingBalance.Should().Be(posted.Where(p => end is null || p.Date < end).Sum(p => p.Debit - p.Credit),
            "the account closes at everything dated up to the end of the range");
        s.EntryCount.Should().Be(inRange.Count);
    }

    [Fact]
    public async Task With_no_range_the_closing_balance_is_always_the_ledgers_last_running_balance_however_the_entries_were_dated()
    {
        var posted = Scatter();

        var report = await _w.Service().GetCustomerLedgerAsync(For(_w.Acme));

        using var db = _w.Db();
        var stored = db.CustomerLedgerEntries.Where(e => e.PartnerId == _w.Acme).OrderBy(e => e.SequenceNo).Last().RunningBalance;
        report.Summaries.Single().ClosingBalance.Should().Be(stored).And.Be(posted.Sum(p => p.Debit - p.Credit));
    }
}
