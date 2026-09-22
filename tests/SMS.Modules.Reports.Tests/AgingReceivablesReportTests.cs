using FluentAssertions;
using Moq;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Reports.Tests.ReceivablesReportWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-03 §15 R3 — aging receivables: the invoices still owed on a day, aged by days past their due date
/// into 0-30, 31-60, 61-90 and 90+, added up per currency and per customer.
/// </summary>
public class AgingReceivablesReportTests
{
    private readonly ReceivablesReportWorld _w = new();

    private static readonly DateTime AsOf = new(2026, 9, 20);

    private static AgingReceivablesFilter On(DateTime? asOf = null, Guid? partner = null, int page = 1, int pageSize = 20) =>
        new() { AsOf = asOf, PartnerId = partner, Page = page, PageSize = pageSize };

    private static string[] Numbers(AgingReceivablesReport r) => [.. r.Invoices.Select(i => i.InvoiceNumber)];

    /// <summary>An invoice issued long ago that fell due <paramref name="daysAgo"/> days before 20 Sept.</summary>
    private SalesInvoice DueDaysAgo(string number, int daysAgo, decimal grand = 100m, Guid? partner = null, string currency = "PKR") =>
        _w.Invoice(partner ?? _w.Acme, number, new DateTime(2026, 1, 1), grand, currency: currency, dueDate: AsOf.AddDays(-daysAgo));

    // ── The buckets ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-10, 0, "0-30")]
    [InlineData(0, 0, "0-30")]
    [InlineData(1, 1, "0-30")]
    [InlineData(30, 30, "0-30")]
    [InlineData(31, 31, "31-60")]
    [InlineData(60, 60, "31-60")]
    [InlineData(61, 61, "61-90")]
    [InlineData(90, 90, "61-90")]
    [InlineData(91, 91, "90+")]
    [InlineData(400, 400, "90+")]
    public async Task An_invoice_is_aged_by_the_days_it_is_past_its_due_date(int daysPastDue, int shownAs, string bucket)
    {
        DueDaysAgo("SINV-1", daysPastDue);

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf));

        var invoice = report.Invoices.Should().ContainSingle().Subject;
        (invoice.DaysPastDue, invoice.Bucket).Should().Be((shownAs, bucket));
    }

    [Fact]
    public async Task An_invoice_not_yet_due_is_in_the_first_bucket_with_no_days_past_due_and_its_due_date_shows_when_it_falls_due()
    {
        DueDaysAgo("SINV-1", -15);

        var invoice = (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).Invoices.Single();

        (invoice.Bucket, invoice.DaysPastDue, invoice.DueDate).Should().Be(("0-30", 0, new DateTime(2026, 10, 5)));
    }

    [Fact]
    public void The_bucket_names_are_the_ones_the_spec_gives()
    {
        AgingBuckets.All.Should().Equal("0-30", "31-60", "61-90", "90+");
        AgingBuckets.For(30).Should().Be("0-30");
        AgingBuckets.For(91).Should().Be("90+");
    }

    // ── What is outstanding ──────────────────────────────────────────────────

    [Fact]
    public async Task A_row_carries_the_invoice_customer_dates_currency_and_amounts()
    {
        var invoice = _w.Invoice(_w.Globex, "SINV-1", new DateTime(2026, 8, 10, 14, 0, 0), 1000m, currency: "USD");
        _w.Payment(_w.Globex, "CPAY-1", D(8, 20), 300m, [(invoice, 300m)], currency: "USD");

        var row = (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).Invoices.Single();

        row.InvoiceUuid.Should().Be(invoice.UUID);
        (row.InvoiceNumber, row.SaleOrderNumber, row.PartnerId, row.CustomerName, row.CurrencyCode)
            .Should().Be(("SINV-1", "SO-SINV-1", _w.Globex, "Globex Corp", "USD"));
        (row.InvoiceDate, row.DueDate, row.DaysPastDue, row.Bucket).Should().Be((D(8, 10), D(9, 9), 11, "0-30"));
        (row.GrandTotal, row.AmountPaid, row.Outstanding).Should().Be((1000m, 300m, 700m));
    }

    [Fact]
    public async Task A_fully_paid_invoice_is_not_outstanding_and_a_part_paid_one_is_for_what_is_left()
    {
        var paid = DueDaysAgo("SINV-PAID", 10, 500m);
        var part = DueDaysAgo("SINV-PART", 10, 500m);
        DueDaysAgo("SINV-UNPAID", 10, 500m);
        _w.Payment(_w.Acme, "CPAY-1", D(9, 1), 500m, [(paid, 500m)]);
        _w.Payment(_w.Acme, "CPAY-2", D(9, 2), 120m, [(part, 120m)]);

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf));

        report.Invoices.Select(i => (i.InvoiceNumber, i.Outstanding)).Should().BeEquivalentTo(new[] { ("SINV-PART", 380m), ("SINV-UNPAID", 500m) });
    }

    [Fact]
    public async Task Several_payments_on_one_invoice_all_reduce_it()
    {
        var invoice = DueDaysAgo("SINV-1", 10, 1000m);
        _w.Payment(_w.Acme, "CPAY-1", D(9, 1), 200m, [(invoice, 200m)]);
        _w.Payment(_w.Acme, "CPAY-2", D(9, 5), 300m, [(invoice, 300m)]);

        var row = (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).Invoices.Single();

        (row.AmountPaid, row.Outstanding).Should().Be((500m, 500m));
    }

    [Fact]
    public async Task One_payment_spread_over_two_invoices_reduces_each_by_its_share()
    {
        var a = DueDaysAgo("SINV-A", 10, 600m);
        var b = DueDaysAgo("SINV-B", 10, 600m);
        _w.Payment(_w.Acme, "CPAY-1", D(9, 1), 700m, [(a, 600m), (b, 100m)]);

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf));

        report.Invoices.Select(i => (i.InvoiceNumber, i.Outstanding)).Should().Equal(("SINV-B", 500m));
    }

    [Fact]
    public async Task Money_on_account_that_was_never_applied_to_an_invoice_reduces_nothing()
    {
        DueDaysAgo("SINV-1", 10, 500m);
        _w.Payment(_w.Acme, "CPAY-ADVANCE", D(9, 1), 500m, []);

        (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).Invoices.Single().Outstanding.Should().Be(500m);
    }

    [Theory]
    [InlineData("DRAFT", false)]
    [InlineData("CANCELLED", false)]
    [InlineData("CREDIT_NOTE", false)]
    [InlineData("ISSUED", true)]
    [InlineData("PARTIALLY_PAID", true)]
    [InlineData("OVERDUE", true)]
    [InlineData("PAID", true)]
    public async Task Only_invoices_that_were_receivables_count(string status, bool counted)
    {
        // Booked in the ledger in every case, so the status alone decides. PAID has no allocations here, so it was never settled.
        _w.Invoice(_w.Acme, "SINV-1", new DateTime(2026, 8, 1), 100m, status: status);

        (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).Invoices.Any().Should().Be(counted);
    }

    [Fact]
    public async Task An_invoice_never_issued_has_no_ledger_entry_and_is_not_a_receivable()
    {
        _w.Invoice(_w.Acme, "SINV-DRAFT", new DateTime(2026, 8, 1), 100m, status: "DRAFT", booked: false);
        // Even one whose status says it was issued: without the ledger debit the customer was never charged.
        _w.Invoice(_w.Acme, "SINV-UNBOOKED", new DateTime(2026, 8, 1), 100m, booked: false);

        (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).Invoices.Should().BeEmpty();
    }

    [Fact]
    public async Task A_soft_deleted_invoice_is_not_outstanding()
    {
        _w.Invoice(_w.Acme, "SINV-GONE", new DateTime(2026, 8, 1), 100m, deleted: true);

        (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).Invoices.Should().BeEmpty();
    }

    // ── As of a day ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_invoice_counts_from_the_day_it_was_issued_not_the_day_the_draft_was_dated()
    {
        _w.Invoice(_w.Acme, "SINV-1", issuedOn: new DateTime(2026, 9, 5, 15, 0, 0), grand: 100m, invoiceDate: D(9, 1));

        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 4)))).Invoices.Should().BeEmpty();
        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 5)))).Invoices.Should().ContainSingle("an invoice issued at 15:00 counts on its own day, whatever time of day it is");
    }

    [Fact]
    public async Task A_payment_reduces_an_invoice_from_the_day_the_money_came_in_and_not_before()
    {
        var invoice = _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 1000m);
        _w.Payment(_w.Acme, "CPAY-1", D(9, 10), 1000m, [(invoice, 1000m)]);

        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 9)))).Invoices.Single().Outstanding.Should().Be(1000m);
        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 10)))).Invoices.Should().BeEmpty();
        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 30)))).Invoices.Should().BeEmpty();
    }

    [Fact]
    public async Task A_receipt_keyed_late_but_dated_earlier_counts_from_its_date_as_the_ledger_does()
    {
        var invoice = _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 1000m);
        _w.Payment(_w.Acme, "CPAY-1", D(9, 3), 1000m, [(invoice, 1000m)], keyedAt: D(9, 8));

        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 2)))).Invoices.Should().ContainSingle();
        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 5)))).Invoices.Should().BeEmpty();
    }

    [Fact]
    public async Task A_bounced_cheque_is_paid_until_the_day_it_bounced_and_owed_again_from_that_day()
    {
        var invoice = _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 1000m);
        var cheque  = _w.Payment(_w.Acme, "CPAY-1", D(9, 10), 1000m, [(invoice, 1000m)], method: "CHEQUE");
        _w.Bounce(cheque, new DateTime(2026, 9, 15, 11, 0, 0));

        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 9)))).Invoices.Single().Outstanding.Should().Be(1000m);
        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 12)))).Invoices.Should().BeEmpty("paid, and not yet bounced");
        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 14)))).Invoices.Should().BeEmpty();
        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 15)))).Invoices.Single().Outstanding.Should().Be(1000m);
        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 20)))).Invoices.Single().Outstanding.Should().Be(1000m);
    }

    [Fact]
    public async Task A_bounce_of_one_payment_gives_back_only_that_payments_share()
    {
        var invoice = _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 1000m);
        _w.Payment(_w.Acme, "CPAY-1", D(9, 5), 400m, [(invoice, 400m)]);
        var cheque = _w.Payment(_w.Acme, "CPAY-2", D(9, 6), 600m, [(invoice, 600m)], method: "CHEQUE");
        _w.Bounce(cheque, D(9, 10));

        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 8)))).Invoices.Should().BeEmpty();
        (await _w.Service().GetAgingReceivablesAsync(On(D(9, 10)))).Invoices.Single().Outstanding.Should().Be(600m);
    }

    [Fact]
    public async Task A_reversed_payment_is_not_money_received()
    {
        var invoice = _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 1000m);
        var payment = _w.Payment(_w.Acme, "CPAY-1", D(9, 3), 1000m, [(invoice, 1000m)]);
        _w.SetPaymentStatus(payment, "REVERSED");

        (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).Invoices.Single().Outstanding.Should().Be(1000m);
    }

    [Fact]
    public async Task A_day_can_be_given_with_any_time_of_day_and_the_report_says_the_day()
    {
        _w.Invoice(_w.Acme, "SINV-1", new DateTime(2026, 9, 5, 15, 0, 0), 100m);

        var report = await _w.Service().GetAgingReceivablesAsync(On(new DateTime(2026, 9, 5, 3, 0, 0)));

        report.Invoices.Should().ContainSingle();
        report.Criteria.AsOf.Should().Be(D(9, 5));
    }

    [Fact]
    public async Task With_no_day_given_it_is_today_by_the_clock_whatever_day_that_is()
    {
        _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 100m, dueDate: D(9, 1));
        _w.Now = new DateTime(2031, 3, 14, 23, 59, 0, DateTimeKind.Utc);

        var report = await _w.Service().GetAgingReceivablesAsync(On());

        report.Criteria.AsOf.Should().Be(new DateTime(2031, 3, 14));
        report.Invoices.Single().DaysPastDue.Should().Be((new DateTime(2031, 3, 14) - D(9, 1)).Days);
        report.Invoices.Single().Bucket.Should().Be("90+");
        report.GeneratedAt.Should().Be(_w.Now);
    }

    [Fact]
    public async Task A_day_in_the_future_ages_everything_on_as_if_nothing_more_is_paid()
    {
        _w.Invoice(_w.Acme, "SINV-1", D(9, 1), 100m, dueDate: D(10, 1));

        var report = await _w.Service().GetAgingReceivablesAsync(On(D(12, 31)));

        report.Invoices.Single().Bucket.Should().Be("90+");
    }

    // ── Totals ───────────────────────────────────────────────────────────────

    private void Spread()
    {
        DueDaysAgo("SINV-1", 5, 100m);                      // 0-30
        DueDaysAgo("SINV-2", 40, 200m);                     // 31-60
        DueDaysAgo("SINV-3", 70, 300m);                     // 61-90
        DueDaysAgo("SINV-4", 120, 400m);                    // 90+
        DueDaysAgo("SINV-5", 20, 50m, partner: _w.Globex);  // 0-30, another customer
        DueDaysAgo("SINV-6", 20, 10m, currency: "USD");     // 0-30, another currency
    }

    [Fact]
    public async Task The_totals_are_one_row_per_currency_with_each_bucket_and_the_whole()
    {
        Spread();

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf));

        report.Totals.Select(t => (t.CurrencyCode, t.InvoiceCount, t.Days0To30, t.Days31To60, t.Days61To90, t.Over90, t.Total)).Should().Equal(
            ("PKR", 5, 150m, 200m, 300m, 400m, 1050m),
            ("USD", 1, 10m, 0m, 0m, 0m, 10m));
    }

    [Fact]
    public async Task The_customers_are_a_row_per_customer_and_currency_by_name()
    {
        Spread();

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf));

        report.Customers.Select(c => (c.CustomerName, c.CurrencyCode, c.InvoiceCount, c.Days0To30, c.Days31To60, c.Days61To90, c.Over90, c.Total)).Should().Equal(
            ("Acme Ltd", "PKR", 4, 100m, 200m, 300m, 400m, 1000m),
            ("Acme Ltd", "USD", 1, 10m, 0m, 0m, 0m, 10m),
            ("Globex Corp", "PKR", 1, 50m, 0m, 0m, 0m, 50m));
    }

    [Fact]
    public async Task Every_bucket_adds_up_to_the_total_in_every_row_and_the_rows_add_up_to_the_totals()
    {
        Spread();

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf));

        foreach (var t in report.Totals) t.Total.Should().Be(t.Days0To30 + t.Days31To60 + t.Days61To90 + t.Over90);
        foreach (var c in report.Customers) c.Total.Should().Be(c.Days0To30 + c.Days31To60 + c.Days61To90 + c.Over90);
        foreach (var t in report.Totals)
        {
            report.Customers.Where(c => c.CurrencyCode == t.CurrencyCode).Sum(c => c.Total).Should().Be(t.Total);
            report.Invoices.Where(i => i.CurrencyCode == t.CurrencyCode).Sum(i => i.Outstanding).Should().Be(t.Total);
        }
    }

    [Fact]
    public async Task The_invoices_run_customer_by_customer_and_the_longest_overdue_first_within_each()
    {
        DueDaysAgo("SINV-A-RECENT", 5, partner: _w.Acme);
        DueDaysAgo("SINV-G", 50, partner: _w.Globex);
        DueDaysAgo("SINV-A-OLD", 80, partner: _w.Acme);
        DueDaysAgo("SINV-A-MID", 30, partner: _w.Acme);

        Numbers(await _w.Service().GetAgingReceivablesAsync(On(AsOf))).Should().Equal("SINV-A-OLD", "SINV-A-MID", "SINV-A-RECENT", "SINV-G");
    }

    [Fact]
    public async Task Customers_the_lookup_does_not_know_come_last_with_no_name_but_are_still_counted()
    {
        var stranger = Guid.NewGuid();
        DueDaysAgo("SINV-STRANGER", 10, 70m, partner: stranger);
        DueDaysAgo("SINV-ZED", 10, 30m, partner: _w.Globex);

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf));

        Numbers(report).Should().Equal("SINV-ZED", "SINV-STRANGER");
        report.Invoices.Last().CustomerName.Should().BeNull();
        report.Customers.Last().Should().Match<AgingReceivablesCustomer>(c => c.CustomerName == null && c.PartnerId == stranger && c.Total == 70m);
        report.Totals.Single().Total.Should().Be(100m);
    }

    [Fact]
    public async Task Two_customers_with_the_same_name_stay_two_rows()
    {
        var twin = Guid.NewGuid();
        _w.Names[twin] = "Acme Ltd";
        DueDaysAgo("SINV-1", 10, 100m, partner: _w.Acme);
        DueDaysAgo("SINV-2", 10, 200m, partner: twin);

        (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).Customers.Should().HaveCount(2);
    }

    [Fact]
    public async Task With_nothing_outstanding_there_are_no_totals_no_customers_and_no_pages()
    {
        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf));

        report.Totals.Should().BeEmpty();
        report.Customers.Should().BeEmpty();
        report.Invoices.Should().BeEmpty();
        (report.TotalRecords, report.TotalPages).Should().Be((0, 0));
    }

    // ── Filtering by customer ────────────────────────────────────────────────

    [Fact]
    public async Task The_customer_filter_reports_only_that_customer_and_names_them_in_the_criteria()
    {
        Spread();

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf, _w.Globex));

        Numbers(report).Should().Equal("SINV-5");
        report.Totals.Single().Total.Should().Be(50m);
        (report.Criteria.PartnerId, report.Criteria.CustomerName).Should().Be(((Guid?)_w.Globex, "Globex Corp"));
    }

    [Fact]
    public async Task A_customer_who_owes_nothing_gets_an_empty_report_that_names_them()
    {
        DueDaysAgo("SINV-1", 10, 100m, partner: _w.Acme);

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf, _w.Globex));

        report.Invoices.Should().BeEmpty();
        report.Criteria.CustomerName.Should().Be("Globex Corp");
    }

    [Fact]
    public async Task A_customer_nobody_has_heard_of_is_not_found_rather_than_a_clean_bill()
    {
        var act = () => _w.Service().GetAgingReceivablesAsync(On(AsOf, Guid.NewGuid()));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_customer_with_no_name_on_record_who_owes_something_gets_their_report_with_no_name()
    {
        var stranger = Guid.NewGuid();
        DueDaysAgo("SINV-1", 10, 100m, partner: stranger);

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf, stranger));

        report.Invoices.Should().ContainSingle();
        (report.Criteria.PartnerId, report.Criteria.CustomerName).Should().Be(((Guid?)stranger, (string?)null));
    }

    [Fact]
    public async Task A_customer_with_history_but_nothing_owed_and_no_name_is_an_empty_report_not_a_missing_customer()
    {
        var stranger = Guid.NewGuid();
        var invoice  = _w.Invoice(stranger, "SINV-1", D(8, 1), 100m);
        _w.Payment(stranger, "CPAY-1", D(8, 5), 100m, [(invoice, 100m)]);

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf, stranger));

        report.Invoices.Should().BeEmpty();
        report.Criteria.CustomerName.Should().BeNull();
    }

    // ── Tenancy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_invoices_and_payments_are_neither_listed_nor_totalled_nor_deducted()
    {
        var mine = _w.Invoice(_w.Acme, "SINV-MINE", D(8, 1), 100m);
        _w.Invoice(_w.Acme, "SINV-THEIRS", D(8, 1), 9000m, org: OtherOrg);
        var theirs = _w.Invoice(_w.Acme, "SINV-THEIRS-2", D(8, 1), 500m, org: OtherOrg);
        _w.Payment(_w.Acme, "CPAY-THEIRS", D(8, 5), 500m, [(theirs, 500m)], org: OtherOrg);

        var report = await _w.Service(Org).GetAgingReceivablesAsync(On(AsOf));

        Numbers(report).Should().Equal("SINV-MINE");
        report.Totals.Single().Total.Should().Be(100m);
        mine.InvoiceNumber.Should().Be("SINV-MINE");
    }

    [Fact]
    public async Task The_export_is_tenant_scoped_too()
    {
        _w.Invoice(_w.Acme, "SINV-MINE", D(8, 1), 100m);
        _w.Invoice(_w.Acme, "SINV-THEIRS", D(8, 1), 9000m, org: OtherOrg);

        Numbers(await _w.Service(Org).GetAgingReceivablesForExportAsync(On(AsOf))).Should().Equal("SINV-MINE");
    }

    // ── Paging and volume ────────────────────────────────────────────────────

    [Fact]
    public async Task A_page_is_a_slice_of_the_invoices_whose_totals_and_customers_are_still_those_of_all_of_them()
    {
        for (var i = 1; i <= 7; i++) DueDaysAgo($"SINV-{i}", 10 * i, 100m);

        var report = await _w.Service().GetAgingReceivablesAsync(On(AsOf, page: 2, pageSize: 3));

        Numbers(report).Should().Equal("SINV-4", "SINV-3", "SINV-2");
        (report.Page, report.PageSize, report.TotalRecords, report.TotalPages).Should().Be((2, 3, 7, 3));
        report.Totals.Single().Total.Should().Be(700m);
        report.Customers.Single().InvoiceCount.Should().Be(7);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public async Task Page_size_is_kept_between_one_and_a_hundred(int asked, int used)
    {
        DueDaysAgo("SINV-1", 10);

        (await _w.Service().GetAgingReceivablesAsync(On(AsOf, pageSize: asked))).PageSize.Should().Be(used);
    }

    [Fact]
    public async Task The_export_carries_every_outstanding_invoice_whatever_the_paging_asked_for()
    {
        for (var i = 1; i <= 12; i++) DueDaysAgo($"SINV-{i:00}", i);

        var report = await _w.Service().GetAgingReceivablesForExportAsync(On(AsOf, page: 3, pageSize: 2));

        report.Invoices.Should().HaveCount(12);
        (report.Page, report.PageSize, report.TotalRecords, report.TotalPages).Should().Be((1, 12, 12, 1));
    }

    [Fact]
    public async Task An_export_of_nothing_is_an_empty_report_not_a_failure()
    {
        var report = await _w.Service().GetAgingReceivablesForExportAsync(On(AsOf));

        (report.Invoices.Count, report.TotalRecords, report.Page, report.PageSize).Should().Be((0, 0, 1, 1));
    }

    [Fact]
    public async Task A_report_takes_exactly_the_most_it_allows_and_refuses_one_more_until_narrowed_to_a_customer()
    {
        SeedManyOutstanding(_w.Acme, ReceivablesReportService.MaxRows);
        SeedManyOutstanding(_w.Globex, 1);

        var atTheLimit = await _w.Service().GetAgingReceivablesForExportAsync(On(AsOf, _w.Acme));
        atTheLimit.Invoices.Should().HaveCount(ReceivablesReportService.MaxRows);

        foreach (var ask in new Func<Task>[]
        {
            () => _w.Service().GetAgingReceivablesAsync(On(AsOf)),
            () => _w.Service().GetAgingReceivablesForExportAsync(On(AsOf))
        })
        {
            var message = (await ask.Should().ThrowAsync<BadRequestException>()).Which.Message;
            message.Should().Contain($"More than {ReceivablesReportService.MaxRows} invoices").And.Contain("Narrow it to one customer");
        }

        (await _w.Service().GetAgingReceivablesAsync(On(AsOf, _w.Globex))).TotalRecords.Should().Be(1);
    }

    private void SeedManyOutstanding(Guid partner, int count)
    {
        using var db = _w.Db();
        var offset = db.SalesInvoices.Count();
        for (var i = 0; i < count; i++)
        {
            var invoice = new SalesInvoice
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, TraceId = Guid.NewGuid(), InvoiceNumber = $"SINV-{offset + i:000000}",
                SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = "SO-1", PartnerId = partner, PartnerName = "x",
                InvoiceDate = D(8, 1), DueDate = D(8, 31), Subtotal = 1m, GrandTotal = 1m, BalanceDue = 1m, Status = "ISSUED", CurrencyCode = "PKR", CreatedBy = 1, CreatedDate = D(8, 1)
            };
            db.SalesInvoices.Add(invoice);
            db.CustomerLedgerEntries.Add(new CustomerLedgerEntry
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, PartnerId = partner, SequenceNo = offset + i + 1, EntryDate = D(8, 1), EntryType = "INVOICE",
                ReferenceType = "SalesInvoice", ReferenceId = invoice.UUID, ReferenceNumber = invoice.InvoiceNumber, DebitAmount = 1m, CurrencyCode = "PKR",
                RunningBalance = 1m, CreatedBy = 1, CreatedDate = D(8, 1)
            });
        }
        db.SaveChanges();
    }

    // ── Names and letterhead ─────────────────────────────────────────────────

    [Fact]
    public async Task Customer_names_are_asked_for_once_for_everyone_outstanding_and_the_named_customer()
    {
        DueDaysAgo("SINV-1", 10, partner: _w.Acme);
        DueDaysAgo("SINV-2", 10, partner: _w.Acme);
        DueDaysAgo("SINV-3", 10, partner: _w.Globex);
        var names = new Mock<ISupplierNameLookupService>();

        await _w.Service(names: names).GetAgingReceivablesAsync(On(AsOf));

        names.Verify(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
        names.Verify(n => n.GetNamesAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2)), Times.Once);
    }

    [Fact]
    public async Task No_customer_lookup_is_made_when_there_is_nobody_to_name()
    {
        var names = new Mock<ISupplierNameLookupService>();

        await _w.Service(names: names).GetAgingReceivablesAsync(On(AsOf));

        names.Verify(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Never);
    }

    [Fact]
    public async Task The_company_name_comes_from_the_letterhead_and_is_null_when_there_is_none()
    {
        (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).CompanyName.Should().Be("Northwind Trading");

        _w.CompanyName = " ";
        (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).CompanyName.Should().BeNull();
    }

    [Fact]
    public async Task The_company_name_is_trimmed()
    {
        _w.CompanyName = "  Northwind Trading  ";

        (await _w.Service().GetAgingReceivablesAsync(On(AsOf))).CompanyName.Should().Be("Northwind Trading");
    }

    [Fact]
    public async Task A_null_filter_is_a_programming_error()
    {
        var act = () => _w.Service().GetAgingReceivablesAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── The rules, over many invoices ────────────────────────────────────────

    public static IEnumerable<object[]> Days() =>
        [.. new[] { "2026-07-15", "2026-08-01", "2026-08-15", "2026-09-01", "2026-09-10", "2026-09-20", "2026-10-15", "2026-12-31" }
            .Select(d => new object[] { DateTime.Parse(d) })];

    [Theory]
    [MemberData(nameof(Days))]
    public async Task Whatever_the_day_the_report_says_what_the_books_said_that_day_and_the_buckets_add_up(DateTime asOf)
    {
        var (bills, receipts) = _w.SeedScatter();

        var report = await _w.Service().GetAgingReceivablesForExportAsync(On(asOf));

        // The independent account of the day: an invoice from the day it was issued, a payment from the day it
        // was received until the day its cheque bounced, both by whole days.
        var expected = new Dictionary<string, decimal>();
        foreach (var bill in bills.Where(b => b.IssuedOn.Date <= asOf))
        {
            var paid = receipts
                .Where(r => r.Date.Date <= asOf && !(r.BouncedOn is { } b && b.Date <= asOf))
                .SelectMany(r => r.Applied.Where(a => a.Invoice == bill.Number))
                .Sum(a => a.Amount);

            if (bill.Grand - paid > 0m) expected[bill.Number] = bill.Grand - paid;
        }

        expected.Should().NotBeEmpty("guards the comparison: a day with nothing owing would agree with anything");
        report.Invoices.ToDictionary(i => i.InvoiceNumber, i => i.Outstanding).Should().BeEquivalentTo(expected);

        foreach (var row in report.Invoices)
        {
            var bill = bills.Single(b => b.Number == row.InvoiceNumber);
            row.DaysPastDue.Should().Be(Math.Max(0, (int)(asOf - bill.Due).TotalDays), row.InvoiceNumber);
            row.Bucket.Should().Be(AgingBuckets.For(row.DaysPastDue), row.InvoiceNumber);
            (row.GrandTotal - row.AmountPaid).Should().Be(row.Outstanding, row.InvoiceNumber);
        }

        foreach (var currency in new[] { "PKR", "USD" })
        {
            var mine  = report.Invoices.Where(i => i.CurrencyCode == currency).ToList();
            var total = report.Totals.SingleOrDefault(t => t.CurrencyCode == currency);

            if (mine.Count == 0) { total.Should().BeNull(); continue; }

            total!.Total.Should().Be(mine.Sum(i => i.Outstanding));
            total.Total.Should().Be(total.Days0To30 + total.Days31To60 + total.Days61To90 + total.Over90);
            total.InvoiceCount.Should().Be(mine.Count);
            report.Customers.Where(c => c.CurrencyCode == currency).Sum(c => c.Total).Should().Be(total.Total);
        }
    }

    [Fact]
    public async Task After_everything_has_happened_what_is_outstanding_is_what_the_invoices_themselves_say_is_owing()
    {
        var (_, receipts) = _w.SeedScatter();
        receipts.Count(r => r.BouncedOn is not null).Should().BeGreaterThan(2, "the books must contain bounced cheques for this to mean anything");
        receipts.Count.Should().BeGreaterThan(10);

        var report = await _w.Service().GetAgingReceivablesForExportAsync(On(new DateTime(2027, 6, 30)));

        using var db = _w.Db();
        var stored = db.SalesInvoices.Where(i => i.BalanceDue > 0m).ToDictionary(i => i.InvoiceNumber, i => i.BalanceDue);
        report.Invoices.ToDictionary(i => i.InvoiceNumber, i => i.Outstanding).Should().BeEquivalentTo(stored);
        stored.Count.Should().BeGreaterThan(5, "guards the comparison: a book with nothing owing would agree with anything");
    }
}
