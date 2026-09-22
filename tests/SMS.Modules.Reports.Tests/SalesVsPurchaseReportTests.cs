using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Reports.Models;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Reports.Tests.ReceivablesReportWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-05 §15 R8 — sales against the cost of the goods sold: revenue from the invoices that stand, cost of
/// goods sold from the SALE entries the product ledger booked when each was issued, and the gross margin between
/// them, by day, week or month and per currency.
/// </summary>
public class SalesVsPurchaseReportTests
{
    private readonly ReceivablesReportWorld _w = new();

    private static readonly Guid Thing        = Guid.NewGuid();
    private static readonly Guid OtherThing   = Guid.NewGuid();

    public SalesVsPurchaseReportTests()
    {
        _w.Stock(Thing, Guid.NewGuid(), "Thing");
        _w.Stock(OtherThing, Guid.NewGuid(), "Other thing");
    }

    private static SalesVsPurchaseFilter On(DateTime? from = null, DateTime? to = null, string? period = null, int page = 1, int pageSize = 20) =>
        new() { DateFrom = from, DateTo = to, Period = period, Page = page, PageSize = pageSize };

    /// <summary>An invoice of one line, and what issuing it cost; a null cost is an invoice whose cost of sales was never booked.</summary>
    private SalesInvoice Sell(
        string number, DateTime issued, decimal qty, decimal price, decimal? cost, string currency = "PKR", string status = "ISSUED",
        Guid? partner = null, decimal discount = 0m, decimal tax = 0m, bool booked = true, bool deleted = false, Guid? org = null)
    {
        var invoice = _w.Sale(partner ?? _w.Acme, number, issued, [new SoldLine(Thing, qty, price, discount, tax)], currency, status,
            booked: booked, deleted: deleted, org: org);
        if (cost is { } c) _w.BookCost(invoice, c);
        return invoice;
    }

    // ── What a row says ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_month_is_its_revenue_less_the_cost_the_ledger_booked_and_the_margin_between()
    {
        Sell("SINV-1", D(9, 5), 10m, 100m, cost: 600m);
        Sell("SINV-2", D(9, 20), 5m, 80m, cost: 250m);

        var report = await _w.AnalysisService().GetSalesVsPurchaseAsync(On());

        var row = report.Items.Should().ContainSingle().Subject;
        (row.PeriodLabel, row.PeriodStart, row.CurrencyCode, row.InvoiceCount, row.Revenue, row.CostOfGoodsSold, row.GrossMargin, row.GrossMarginPercent, row.UncostedRevenue)
            .Should().Be(("2026-09", D(9, 1), "PKR", 2, 1400m, 850m, 550m, 39.29m, 0m));
        (report.Totals.Single().Revenue, report.Totals.Single().CostOfGoodsSold, report.Totals.Single().GrossMargin).Should().Be((1400m, 850m, 550m));
    }

    [Fact]
    public async Task Revenue_is_before_tax_and_after_the_discount_like_every_other_sales_report()
    {
        // 10 at 40 is 400; 10% off is 360; 17% tax on that is not revenue.
        Sell("SINV-1", D(9, 5), 10m, 40m, cost: 200m, discount: 10m, tax: 17m);

        var row = (await _w.AnalysisService().GetSalesVsPurchaseAsync(On())).Items.Single();

        (row.Revenue, row.CostOfGoodsSold, row.GrossMargin).Should().Be((360m, 200m, 160m));
    }

    [Fact]
    public async Task An_invoice_of_several_lines_costs_the_sum_of_its_lines()
    {
        var invoice = _w.Sale(_w.Acme, "SINV-1", D(9, 5), [new SoldLine(Thing, 2m, 50m), new SoldLine(OtherThing, 1m, 30m)]);
        _w.BookCost(invoice, 70m, 12.34m);

        var row = (await _w.AnalysisService().GetSalesVsPurchaseAsync(On())).Items.Single();

        (row.Revenue, row.CostOfGoodsSold, row.GrossMargin).Should().Be((130m, 82.34m, 47.66m));
    }

    [Fact]
    public async Task Cost_above_revenue_is_a_negative_margin_and_a_negative_percent()
    {
        Sell("SINV-1", D(9, 5), 1m, 100m, cost: 150m);

        var row = (await _w.AnalysisService().GetSalesVsPurchaseAsync(On())).Items.Single();

        (row.GrossMargin, row.GrossMarginPercent).Should().Be((-50m, -50m));
    }

    [Fact]
    public async Task Goods_given_away_have_a_cost_and_no_percent_because_there_is_no_revenue_to_take_it_of()
    {
        Sell("SINV-1", D(9, 5), 1m, 100m, cost: 30m, discount: 100m);

        var row = (await _w.AnalysisService().GetSalesVsPurchaseAsync(On())).Items.Single();

        (row.Revenue, row.CostOfGoodsSold, row.GrossMargin, row.GrossMarginPercent).Should().Be((0m, 30m, -30m, (decimal?)null));
    }

    // ── Cost that was never booked ───────────────────────────────────────────

    [Fact]
    public async Task An_invoice_with_no_cost_of_sales_is_revenue_with_no_cost_and_is_called_out()
    {
        Sell("SINV-COSTED", D(9, 5), 1m, 100m, cost: 60m);
        Sell("SINV-LEGACY", D(9, 6), 1m, 500m, cost: null);

        var report = await _w.AnalysisService().GetSalesVsPurchaseAsync(On());

        var row = report.Items.Single();
        (row.Revenue, row.CostOfGoodsSold, row.GrossMargin, row.UncostedRevenue).Should().Be((600m, 60m, 540m, 500m));
        report.Totals.Single().UncostedRevenue.Should().Be(500m);
    }

    [Fact]
    public async Task Only_a_sale_entry_is_cost_of_goods_sold_even_when_another_kind_names_the_same_invoice()
    {
        var invoice = Sell("SINV-1", D(9, 5), 1m, 100m, cost: 60m);
        await using (var db = _w.Db())
        {
            // Goods coming back against the invoice is not a cost of the sale.
            db.ProductLedgerEntries.Add(new ProductLedgerEntry
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, VariantUuid = Thing, ProductUuid = Guid.NewGuid(), SequenceNo = 99,
                EntryDate = D(9, 6), EntryType = "RETURN_IN", ReferenceType = "SalesInvoice", ReferenceId = invoice.UUID, ReferenceNumber = "SINV-1",
                Direction = "IN", Quantity = 1m, UnitCost = 999m, TotalCost = 999m, RunningQty = 1m, RunningValue = 999m, CreatedBy = 1, CreatedDate = D(9, 6)
            });
            await db.SaveChangesAsync();
        }

        var row = (await _w.AnalysisService().GetSalesVsPurchaseAsync(On())).Items.Single();

        row.CostOfGoodsSold.Should().Be(60m);
    }

    [Fact]
    public async Task An_invoice_costed_at_nothing_is_still_costed()
    {
        Sell("SINV-FREE-STOCK", D(9, 5), 1m, 100m, cost: 0m);

        var row = (await _w.AnalysisService().GetSalesVsPurchaseAsync(On())).Items.Single();

        (row.CostOfGoodsSold, row.UncostedRevenue).Should().Be((0m, 0m));
    }

    // ── Which invoices count ─────────────────────────────────────────────────

    [Fact]
    public async Task Only_invoices_that_stand_count_and_the_cost_of_one_that_was_undone_does_not()
    {
        Sell("SINV-LIVE", D(9, 5), 1m, 100m, cost: 60m);
        Sell("SINV-PAID", D(9, 6), 1m, 100m, cost: 60m, status: "PAID");
        Sell("SINV-DRAFT", D(9, 7), 1m, 999m, cost: 999m, status: "DRAFT");
        Sell("SINV-CANCELLED", D(9, 8), 1m, 999m, cost: 999m, status: "CANCELLED");
        Sell("SINV-CREDITED", D(9, 9), 1m, 999m, cost: 999m, status: "CREDIT_NOTE");
        Sell("SINV-DELETED", D(9, 10), 1m, 999m, cost: 999m, deleted: true);
        Sell("SINV-UNBOOKED", D(9, 11), 1m, 999m, cost: 999m, booked: false);
        Sell("SINV-THEIRS", D(9, 12), 1m, 999m, cost: 999m, org: OtherOrg);

        var row = (await _w.AnalysisService().GetSalesVsPurchaseAsync(On())).Items.Single();

        (row.InvoiceCount, row.Revenue, row.CostOfGoodsSold).Should().Be((2, 200m, 120m));
    }

    [Fact]
    public async Task Another_organizations_cost_never_reaches_this_ones()
    {
        var mine   = Sell("SINV-MINE", D(9, 5), 1m, 100m, cost: 60m);
        var theirs = Sell("SINV-THEIRS", D(9, 5), 1m, 100m, cost: 999m, org: OtherOrg);

        (await _w.AnalysisService(Org).GetSalesVsPurchaseAsync(On())).Items.Single().CostOfGoodsSold.Should().Be(60m);
        (await _w.AnalysisService(OtherOrg).GetSalesVsPurchaseAsync(On())).Items.Single().CostOfGoodsSold.Should().Be(999m);
        mine.Should().NotBeNull();
        theirs.Should().NotBeNull();
    }

    // ── Periods ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Month_is_the_default_period_and_a_word_in_any_case_is_understood()
    {
        Sell("SINV-1", D(9, 5), 1m, 100m, cost: 60m);

        (await _w.AnalysisService().GetSalesVsPurchaseAsync(On())).Criteria.Period.Should().Be("MONTH");
        (await _w.AnalysisService().GetSalesVsPurchaseAsync(On(period: "  "))).Criteria.Period.Should().Be("MONTH");

        var week = await _w.AnalysisService().GetSalesVsPurchaseAsync(On(period: " week "));
        week.Criteria.Period.Should().Be("WEEK");
        week.Items.Single().PeriodLabel.Should().Be("2026-W36");
    }

    [Fact]
    public async Task A_week_runs_monday_to_sunday_and_is_named_by_its_ISO_number()
    {
        Sell("SINV-SUN", D(9, 6), 1m, 100m, cost: 60m);
        Sell("SINV-MON", D(9, 7), 1m, 10m, cost: 6m);
        Sell("SINV-SUN-2", D(9, 13), 1m, 1m, cost: 0.5m);

        var report = await _w.AnalysisService().GetSalesVsPurchaseAsync(On(period: "WEEK"));

        report.Items.Select(i => (i.PeriodLabel, i.PeriodStart, i.Revenue)).Should().Equal(("2026-W36", D(8, 31), 100m), ("2026-W37", D(9, 7), 11m));
    }

    [Fact]
    public async Task A_week_belongs_to_the_ISO_year_it_is_in_not_the_calendar_year_of_its_first_day()
    {
        // Monday 29 December 2025 starts the first ISO week of 2026.
        Sell("SINV-1", new DateTime(2025, 12, 29), 1m, 100m, cost: 60m);
        Sell("SINV-2", new DateTime(2026, 1, 4), 1m, 10m, cost: 6m);

        var report = await _w.AnalysisService().GetSalesVsPurchaseAsync(On(period: "week"));

        report.Items.Select(i => (i.PeriodLabel, i.PeriodStart, i.Revenue)).Should().Equal(("2026-W01", new DateTime(2025, 12, 29), 110m));
    }

    [Fact]
    public async Task An_invoice_belongs_to_the_day_it_was_issued_not_the_day_it_was_dated()
    {
        // Dated 30 August, and left in draft until 2 September: a sale of September.
        var invoice = Sell("SINV-WAITED", D(9, 2), 1m, 100m, cost: 60m);
        await using (var db = _w.Db())
        {
            var stored = await db.SalesInvoices.SingleAsync(i => i.UUID == invoice.UUID);
            stored.InvoiceDate = D(8, 30);
            await db.SaveChangesAsync();
        }

        var report = await _w.AnalysisService().GetSalesVsPurchaseAsync(On(D(9, 1), D(9, 30), "day"));

        report.Items.Should().ContainSingle().Which.PeriodLabel.Should().Be("2026-09-02");
        (await _w.AnalysisService().GetSalesVsPurchaseAsync(On(D(8, 1), D(8, 31)))).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_day_is_named_by_its_date_and_only_days_with_sales_have_a_row()
    {
        Sell("SINV-1", D(9, 5), 1m, 100m, cost: 60m);
        Sell("SINV-2", D(9, 5), 1m, 20m, cost: 10m);
        Sell("SINV-3", D(9, 9), 1m, 3m, cost: 1m);

        var report = await _w.AnalysisService().GetSalesVsPurchaseAsync(On(period: "day"));

        report.Items.Select(i => (i.PeriodLabel, i.PeriodStart, i.InvoiceCount, i.Revenue)).Should().Equal(("2026-09-05", D(9, 5), 2, 120m), ("2026-09-09", D(9, 9), 1, 3m));
    }

    [Fact]
    public async Task Months_and_years_roll_over_in_order()
    {
        Sell("SINV-DEC", new DateTime(2025, 12, 31), 1m, 1m, cost: 1m);
        Sell("SINV-JAN", new DateTime(2026, 1, 1), 1m, 2m, cost: 1m);
        Sell("SINV-OCT", D(10, 15), 1m, 3m, cost: 1m);

        var report = await _w.AnalysisService().GetSalesVsPurchaseAsync(On());

        report.Items.Select(i => i.PeriodLabel).Should().Equal("2025-12", "2026-01", "2026-10");
    }

    [Fact]
    public async Task An_unknown_period_is_refused_with_the_valid_ones()
    {
        var act = () => _w.AnalysisService().GetSalesVsPurchaseAsync(On(period: "fortnight"));

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("'fortnight'").And.Contain("DAY, WEEK, MONTH");
    }

    // ── Currencies and totals ────────────────────────────────────────────────

    [Fact]
    public async Task Each_currency_has_its_own_rows_and_its_own_total_and_they_are_never_added_together()
    {
        Sell("SINV-PKR", D(9, 5), 1m, 100m, cost: 60m);
        Sell("SINV-USD", D(9, 6), 1m, 30m, cost: 20m, currency: "USD");
        Sell("SINV-USD-2", D(10, 6), 1m, 10m, cost: 5m, currency: "USD");

        var report = await _w.AnalysisService().GetSalesVsPurchaseAsync(On());

        report.Items.Select(i => (i.PeriodLabel, i.CurrencyCode, i.Revenue)).Should().Equal(("2026-09", "PKR", 100m), ("2026-09", "USD", 30m), ("2026-10", "USD", 10m));
        report.Totals.Select(t => (t.CurrencyCode, t.PeriodCount, t.InvoiceCount, t.Revenue, t.CostOfGoodsSold, t.GrossMargin, t.GrossMarginPercent))
            .Should().Equal(("PKR", 1, 1, 100m, 60m, 40m, (decimal?)40m), ("USD", 2, 2, 40m, 25m, 15m, (decimal?)37.5m));
    }

    [Fact]
    public async Task The_totals_are_over_every_period_not_the_page_and_the_export_carries_them_all()
    {
        foreach (var month in Enumerable.Range(1, 5))
            Sell($"SINV-{month}", D(month, 10), 1m, 100m, cost: 60m);

        var page = await _w.AnalysisService().GetSalesVsPurchaseAsync(On(page: 2, pageSize: 2));
        page.Items.Select(i => i.PeriodLabel).Should().Equal("2026-03", "2026-04");
        (page.TotalRecords, page.TotalPages, page.Page, page.PageSize).Should().Be((5, 3, 2, 2));
        page.Totals.Single().Revenue.Should().Be(500m);

        var export = await _w.AnalysisService().GetSalesVsPurchaseForExportAsync(On(page: 2, pageSize: 2));
        (export.Items.Count, export.Page, export.PageSize, export.TotalPages).Should().Be((5, 1, 5, 1));
    }

    [Fact]
    public async Task Nothing_sold_is_an_empty_report_not_an_error()
    {
        var report = await _w.AnalysisService().GetSalesVsPurchaseAsync(On());

        report.Items.Should().BeEmpty();
        report.Totals.Should().BeEmpty();
        (report.TotalRecords, report.TotalPages).Should().Be((0, 0));
    }

    // ── The range ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_range_is_whole_days_inclusive_at_both_ends_and_either_end_may_be_left_open()
    {
        Sell("SINV-BEFORE", D(9, 4).AddDays(1).AddTicks(-1), 1m, 1m, cost: 1m);
        Sell("SINV-FIRST", D(9, 5), 1m, 10m, cost: 1m);
        Sell("SINV-LAST-TICK", D(9, 10).AddDays(1).AddTicks(-1), 1m, 100m, cost: 1m);
        Sell("SINV-NEXT-DAY", D(9, 11), 1m, 1000m, cost: 1m);

        (await _w.AnalysisService().GetSalesVsPurchaseAsync(On(D(9, 5), D(9, 10)))).Totals.Single().Revenue.Should().Be(110m);
        (await _w.AnalysisService().GetSalesVsPurchaseAsync(On(D(9, 5), null))).Totals.Single().Revenue.Should().Be(1110m);
        (await _w.AnalysisService().GetSalesVsPurchaseAsync(On(null, D(9, 10)))).Totals.Single().Revenue.Should().Be(111m);
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var act = () => _w.AnalysisService().GetSalesVsPurchaseAsync(On(D(9, 10), D(9, 5)));

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("start date is after its end date");
    }

    [Fact]
    public async Task The_criteria_echo_the_range_and_the_company_and_date_are_the_clocks()
    {
        var report = await _w.AnalysisService().GetSalesVsPurchaseAsync(On(D(9, 1).AddHours(5), D(9, 30).AddHours(9), "day"));

        (report.Criteria.DateFrom, report.Criteria.DateTo, report.Criteria.Period).Should().Be(((DateTime?)D(9, 1), (DateTime?)D(9, 30), "DAY"));
        (report.CompanyName, report.GeneratedAt).Should().Be(("Northwind Trading", _w.Now));
    }

    // ── Agreement with the other sales reports ───────────────────────────────

    /// <summary>What the books say, worked out by hand from the raw rows, without any of the service's queries.</summary>
    private sealed record Expected(string Month, string Currency, int Invoices, decimal Revenue, decimal Cost, decimal Uncosted);

    /// <summary>One organization's books: the other's are in the same store, and are exactly what must not be counted.</summary>
    private List<Expected> Oracle()
    {
        using var db = _w.Db(Org);
        var invoices = db.SalesInvoices.AsNoTracking().Where(i => i.OrganizationId == Org).ToList();
        var booked   = db.CustomerLedgerEntries.AsNoTracking().Where(e => e.OrganizationId == Org && e.EntryType == "INVOICE").ToList();
        var costs    = db.ProductLedgerEntries.AsNoTracking().Where(e => e.OrganizationId == Org && e.EntryType == "SALE").ToList();

        string[] live = ["ISSUED", "PARTIALLY_PAID", "PAID", "OVERDUE"];
        var rows = new List<(string Month, string Currency, decimal Revenue, decimal Cost, bool Costed)>();
        foreach (var invoice in invoices.Where(i => !i.IsDelete && live.Contains(i.Status)))
        {
            var entry = booked.Where(e => e.ReferenceId == invoice.UUID).OrderBy(e => e.EntryDate).FirstOrDefault();
            if (entry is null) continue;

            var mine = costs.Where(c => c.ReferenceId == invoice.UUID).ToList();
            rows.Add((entry.EntryDate.ToString("yyyy-MM"), invoice.CurrencyCode, invoice.Subtotal - invoice.DiscountAmount, mine.Sum(c => c.TotalCost), mine.Count > 0));
        }

        return [.. rows.GroupBy(r => (r.Month, r.Currency))
            .OrderBy(g => g.Key.Month, StringComparer.Ordinal).ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
            .Select(g => new Expected(g.Key.Month, g.Key.Currency, g.Count(), Cents(g.Sum(r => r.Revenue)), Cents(g.Sum(r => r.Cost)),
                Cents(g.Where(r => !r.Costed).Sum(r => r.Revenue))))];
    }

    private static decimal Cents(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    [Fact]
    public async Task On_fifty_scattered_invoices_every_month_agrees_with_the_books_added_up_by_hand()
    {
        _w.SeedSalesWithCosts();

        var report = await _w.AnalysisService().GetSalesVsPurchaseForExportAsync(On());
        var expected = Oracle();

        expected.Count.Should().BeGreaterThan(3, "guards the comparison: books that matched nothing would agree with anything");
        expected.Sum(e => e.Uncosted).Should().BeGreaterThan(0m, "the dataset has invoices with no cost booked");
        expected.Sum(e => e.Invoices).Should().BeGreaterThan(15);
        report.Items.Select(i => (i.PeriodLabel, i.CurrencyCode, i.InvoiceCount, i.Revenue, i.CostOfGoodsSold, i.UncostedRevenue))
            .Should().Equal(expected.Select(e => (e.Month, e.Currency, e.Invoices, e.Revenue, e.Cost, e.Uncosted)));
    }

    [Fact]
    public async Task Its_revenue_is_the_revenue_the_customer_report_gives_for_the_same_days()
    {
        _w.SeedSalesWithCosts();

        foreach (var (from, to) in new (DateTime?, DateTime?)[] { (null, null), (D(8, 1), D(8, 31)), (D(9, 1), null), (D(8, 15), D(9, 15)) })
        {
            var vs       = await _w.AnalysisService().GetSalesVsPurchaseForExportAsync(On(from, to));
            var customer = await _w.AnalysisService().GetSalesByCustomerForExportAsync(new SalesByCustomerFilter { DateFrom = from, DateTo = to });

            vs.Totals.Select(t => (t.CurrencyCode, t.InvoiceCount, t.Revenue))
                .Should().Equal(customer.Totals.Select(t => (t.CurrencyCode, t.InvoiceCount, t.Revenue)), $"{from:d} to {to:d}");
        }
    }

    [Fact]
    public async Task Every_period_adds_up_and_the_periods_add_up_to_the_total()
    {
        _w.SeedSalesWithCosts();

        var report = await _w.AnalysisService().GetSalesVsPurchaseForExportAsync(On(period: "week"));

        report.Items.Should().OnlyContain(i => i.GrossMargin == i.Revenue - i.CostOfGoodsSold);
        foreach (var total in report.Totals)
        {
            var mine = report.Items.Where(i => i.CurrencyCode == total.CurrencyCode).ToList();
            (total.Revenue, total.CostOfGoodsSold, total.GrossMargin, total.InvoiceCount, total.PeriodCount)
                .Should().Be((mine.Sum(i => i.Revenue), mine.Sum(i => i.CostOfGoodsSold), mine.Sum(i => i.GrossMargin), mine.Sum(i => i.InvoiceCount), mine.Count));
        }
    }
}
