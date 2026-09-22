using FluentAssertions;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Reports.Models;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Reports.Tests.ReceivablesReportWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-06 §15 R10 — what each product earned against what it cost, ranked by margin: A29-P8-05's ranking with a
/// rank, totals and documents. Its numbers are P8-05's, so the tests below hold it to them.
/// </summary>
public class ProductProfitabilityReportTests
{
    private readonly ReceivablesReportWorld _w = new();

    private static readonly Guid Laptop = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Mouse  = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid Cable  = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid LaptopBlack  = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid LaptopSilver = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid MouseV = Guid.Parse("b0000000-0000-0000-0000-000000000003");
    private static readonly Guid CableV = Guid.Parse("b0000000-0000-0000-0000-000000000004");

    public ProductProfitabilityReportTests()
    {
        _w.Catalog(LaptopBlack, Laptop, "Laptop", variantName: "Black");
        _w.Catalog(LaptopSilver, Laptop, "Laptop", variantName: "Silver", isDefault: false);
        _w.Catalog(MouseV, Mouse, "Mouse");
        _w.Catalog(CableV, Cable, "Cable");
    }

    private static ProfitabilityReportFilter On(DateTime? from = null, DateTime? to = null, int page = 1, int pageSize = 20) =>
        new() { DateFrom = from, DateTo = to, Page = page, PageSize = pageSize };

    /// <summary>An invoice of one line whose cost of sales was booked when it was issued.</summary>
    private SalesInvoice Sell(
        string number, DateTime issued, Guid variant, decimal qty, decimal price, decimal cost, string currency = "PKR", string status = "ISSUED",
        decimal discount = 0m, decimal tax = 0m)
    {
        var invoice = _w.Sale(_w.Acme, number, issued, [new SoldLine(variant, qty, price, discount, tax)], currency, status);
        _w.BookCost(invoice, cost);
        return invoice;
    }

    // ── What a row says ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_product_is_its_revenue_before_tax_against_the_cost_the_ledger_booked_and_the_margin_between()
    {
        // 10 at 40 is 400; 10% off is 360; the 17% tax on that is not revenue.
        Sell("SINV-1", D(9, 5), LaptopBlack, 10m, 40m, cost: 200m, discount: 10m, tax: 17m);

        var report = await _w.LedgerService().GetProductProfitabilityAsync(On());

        var row = report.Items.Should().ContainSingle().Subject;
        (row.Rank, row.ProductUuid, row.ProductName, row.CurrencyCode, row.QuantitySold, row.Revenue, row.CostOfGoodsSold, row.GrossProfit, row.MarginPercent)
            .Should().Be((1, Laptop, "Laptop", "PKR", 10m, 360m, 200m, 160m, (decimal?)44.44m));
        var total = report.Totals.Single();
        (total.CurrencyCode, total.ProductCount, total.Revenue, total.CostOfGoodsSold, total.GrossProfit, total.MarginPercent)
            .Should().Be(("PKR", 1, 360m, 200m, 160m, (decimal?)44.44m));
    }

    [Fact]
    public async Task Every_variant_of_a_product_is_one_row()
    {
        Sell("SINV-1", D(9, 5), LaptopBlack, 2m, 1000m, cost: 1200m);
        Sell("SINV-2", D(9, 6), LaptopSilver, 3m, 1100m, cost: 2000m);

        var row = (await _w.LedgerService().GetProductProfitabilityAsync(On())).Items.Should().ContainSingle().Subject;

        (row.QuantitySold, row.Revenue, row.CostOfGoodsSold, row.GrossProfit).Should().Be((5m, 5300m, 3200m, 2100m));
    }

    [Fact]
    public async Task Cost_above_revenue_is_a_loss_and_no_revenue_has_no_percent()
    {
        Sell("SINV-LOSS", D(9, 5), MouseV, 1m, 100m, cost: 150m);
        Sell("SINV-FREE", D(9, 6), CableV, 1m, 100m, cost: 30m, discount: 100m);

        var report = await _w.LedgerService().GetProductProfitabilityAsync(On());

        report.Items.Select(i => (i.ProductName, i.GrossProfit, i.MarginPercent)).Should().Equal(("Cable", -30m, (decimal?)null), ("Mouse", -50m, (decimal?)-50m));
    }

    // ── Ranking ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Products_are_ranked_by_the_margin_they_made_not_by_what_they_sold_for_or_their_percent()
    {
        Sell("SINV-1", D(9, 5), LaptopBlack, 1m, 1000m, cost: 900m);   // most revenue, 100 profit, 10%
        Sell("SINV-2", D(9, 5), MouseV, 1m, 500m, cost: 300m);         // 200 profit, 40%
        Sell("SINV-3", D(9, 5), CableV, 1m, 60m, cost: 10m);           // least revenue, 50 profit, 83.33%

        var report = await _w.LedgerService().GetProductProfitabilityAsync(On());

        report.Items.Select(i => (i.Rank, i.ProductName, i.GrossProfit, i.MarginPercent)).Should().Equal(
            (1, "Mouse", 200m, (decimal?)40m), (2, "Laptop", 100m, (decimal?)10m), (3, "Cable", 50m, (decimal?)83.33m));
    }

    [Fact]
    public async Task Each_currency_is_ranked_on_its_own_because_there_is_no_exchange_rate_to_compare_them_with()
    {
        Sell("SINV-PKR-1", D(9, 5), LaptopBlack, 1m, 100000m, cost: 60000m);
        Sell("SINV-PKR-2", D(9, 5), MouseV, 1m, 5000m, cost: 4000m);
        Sell("SINV-USD-1", D(9, 5), LaptopBlack, 1m, 300m, cost: 200m, currency: "USD");
        Sell("SINV-USD-2", D(9, 5), MouseV, 1m, 90m, cost: 10m, currency: "USD");

        var report = await _w.LedgerService().GetProductProfitabilityAsync(On());

        report.Items.Select(i => (i.CurrencyCode, i.ProductName, i.GrossProfit, i.Rank)).Should().Equal(
            ("PKR", "Laptop", 40000m, 1), ("PKR", "Mouse", 1000m, 2), ("USD", "Laptop", 100m, 1), ("USD", "Mouse", 80m, 2));
        report.Totals.Select(t => (t.CurrencyCode, t.ProductCount, t.GrossProfit)).Should().Equal(("PKR", 2, 41000m), ("USD", 2, 180m));
    }

    [Fact]
    public async Task Ranks_run_on_across_pages_and_the_totals_are_over_every_product_not_the_page()
    {
        foreach (var (variant, cost) in new[] { (LaptopBlack, 10m), (MouseV, 20m), (CableV, 30m) })
            Sell($"SINV-{variant:N}", D(9, 5), variant, 1m, 100m, cost);

        var page = await _w.LedgerService().GetProductProfitabilityAsync(On(page: 2, pageSize: 2));

        page.Items.Should().ContainSingle().Which.Rank.Should().Be(3);
        (page.TotalRecords, page.TotalPages, page.Page, page.PageSize).Should().Be((3, 2, 2, 2));
        (page.Totals.Single().ProductCount, page.Totals.Single().GrossProfit).Should().Be((3, 240m));

        var export = await _w.LedgerService().GetProductProfitabilityForExportAsync(On(page: 2, pageSize: 2));
        export.Items.Select(i => i.Rank).Should().Equal(1, 2, 3);
        (export.Page, export.PageSize, export.TotalPages).Should().Be((1, 3, 1));
    }

    // ── Which sales count ────────────────────────────────────────────────────

    [Fact]
    public async Task A_sale_whose_cost_was_never_booked_is_left_out_not_shown_at_a_hundred_percent()
    {
        Sell("SINV-COSTED", D(9, 5), LaptopBlack, 1m, 100m, cost: 60m);
        _w.Sale(_w.Acme, "SINV-LEGACY", D(9, 6), [new SoldLine(LaptopBlack, 1m, 900m)]);

        var report = await _w.LedgerService().GetProductProfitabilityAsync(On());

        (report.Items.Single().Revenue, report.Items.Single().CostOfGoodsSold).Should().Be((100m, 60m));
    }

    [Fact]
    public async Task Only_invoices_that_stand_count_even_when_the_cost_of_one_that_was_undone_is_still_on_the_ledger()
    {
        Sell("SINV-LIVE", D(9, 5), LaptopBlack, 1m, 100m, cost: 60m);
        Sell("SINV-DRAFT", D(9, 6), LaptopBlack, 1m, 999m, cost: 999m, status: "DRAFT");
        Sell("SINV-CANCELLED", D(9, 7), LaptopBlack, 1m, 999m, cost: 999m, status: "CANCELLED");
        Sell("SINV-CREDITED", D(9, 8), LaptopBlack, 1m, 999m, cost: 999m, status: "CREDIT_NOTE");

        var row = (await _w.LedgerService().GetProductProfitabilityAsync(On())).Items.Single();

        (row.Revenue, row.CostOfGoodsSold).Should().Be((100m, 60m));
    }

    [Fact]
    public async Task The_range_is_whole_days_inclusive_at_both_ends_and_either_end_may_be_left_open()
    {
        Sell("SINV-BEFORE", D(9, 4).AddDays(1).AddTicks(-1), LaptopBlack, 1m, 1m, cost: 1m);
        Sell("SINV-FIRST", D(9, 5), LaptopBlack, 1m, 10m, cost: 1m);
        Sell("SINV-LAST", D(9, 10), LaptopBlack, 1m, 100m, cost: 1m);
        Sell("SINV-NEXT", D(9, 11), LaptopBlack, 1m, 1000m, cost: 1m);

        (await _w.LedgerService().GetProductProfitabilityAsync(On(D(9, 5), D(9, 10)))).Totals.Single().Revenue.Should().Be(110m);
        (await _w.LedgerService().GetProductProfitabilityAsync(On(D(9, 5)))).Totals.Single().Revenue.Should().Be(1110m);
        (await _w.LedgerService().GetProductProfitabilityAsync(On(to: D(9, 10)))).Totals.Single().Revenue.Should().Be(111m);
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var act = () => _w.LedgerService().GetProductProfitabilityAsync(On(D(9, 10), D(9, 5)));

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Another_organizations_sales_and_cost_never_reach_the_ranking()
    {
        Sell("SINV-MINE", D(9, 5), LaptopBlack, 1m, 100m, cost: 60m);
        var theirs = _w.Sale(_w.Acme, "SINV-THEIRS", D(9, 5), [new SoldLine(LaptopBlack, 1m, 999m)], org: OtherOrg);
        _w.BookCost(theirs, 1m);

        var report = await _w.LedgerService(Org).GetProductProfitabilityAsync(On());

        (report.Items.Single().Revenue, report.Items.Single().CostOfGoodsSold).Should().Be((100m, 60m));
    }

    [Fact]
    public async Task Nothing_sold_is_an_empty_report_not_an_error()
    {
        var report = await _w.LedgerService().GetProductProfitabilityAsync(On());

        report.Items.Should().BeEmpty();
        report.Totals.Should().BeEmpty();
        (report.TotalRecords, report.TotalPages).Should().Be((0, 0));
    }

    [Fact]
    public async Task A_product_inventory_cannot_name_has_no_name_but_is_still_ranked()
    {
        var invoice = _w.Sale(_w.Acme, "SINV-1", D(9, 5), [new SoldLine(Guid.NewGuid(), 1m, 100m)]);
        _w.BookCost(invoice, 60m);

        var row = (await _w.LedgerService().GetProductProfitabilityAsync(On())).Items.Single();

        (row.ProductName, row.Rank, row.GrossProfit).Should().Be(((string?)null, 1, 40m));
    }

    [Fact]
    public async Task The_criteria_echo_the_range_and_the_company_and_date_are_the_clocks()
    {
        var report = await _w.LedgerService().GetProductProfitabilityAsync(On(D(9, 1).AddHours(5), D(9, 30).AddHours(9)));

        (report.Criteria.DateFrom, report.Criteria.DateTo).Should().Be(((DateTime?)D(9, 1), (DateTime?)D(9, 30)));
        (report.CompanyName, report.GeneratedAt).Should().Be(("Northwind Trading", _w.Now));
    }

    // ── Agreement with P8-05 and with R8 ─────────────────────────────────────

    /// <summary>What P8-05's endpoint says over the same books, a page of a hundred at a time until it has said it all.</summary>
    private async Task<List<ProductProfitabilityItemModel>> WhatTheEndpointSays(ProductProfitabilityFilter filter)
    {
        var all = new List<ProductProfitabilityItemModel>();
        for (var page = 1; ; page++)
        {
            filter.Page = page;
            filter.PageSize = 100;
            var result = await _w.ProfitabilityQuery().GetProfitabilityAsync(filter);
            all.AddRange(result.Data);
            if (page >= result.TotalPages) return all;
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(8, 8)]
    [InlineData(9, null)]
    public async Task On_fifty_scattered_invoices_the_report_is_the_endpoints_ranking_row_for_row(int? fromMonth, int? toMonth)
    {
        _w.SeedSalesWithCosts();
        DateTime? from = fromMonth is { } f ? new DateTime(2026, f, 1) : null;
        DateTime? to   = toMonth is { } t ? new DateTime(2026, t, DateTime.DaysInMonth(2026, t)) : null;

        var report   = await _w.LedgerService().GetProductProfitabilityForExportAsync(On(from, to));
        var endpoint = await WhatTheEndpointSays(new ProductProfitabilityFilter { DateFrom = from, DateTo = to });

        endpoint.Count.Should().BeGreaterThan(3, "guards the comparison: books that matched nothing would agree with anything");
        report.Items.Select(i => (i.ProductUuid, i.ProductName, i.CurrencyCode, i.QuantitySold, i.Revenue, i.CostOfGoodsSold, i.GrossProfit, i.MarginPercent))
            .Should().Equal(endpoint.Select(p => (p.ProductUuid, p.ProductName, p.CurrencyCode, p.QuantitySold, p.Revenue, p.CostOfGoodsSold, p.GrossProfit, p.MarginPercent)));
    }

    [Fact]
    public async Task On_fifty_scattered_invoices_ranks_run_one_to_n_in_each_currency_and_the_totals_add_up()
    {
        _w.SeedSalesWithCosts();

        var report = await _w.LedgerService().GetProductProfitabilityForExportAsync(On());

        report.Totals.Should().HaveCountGreaterThan(1, "two currencies");
        foreach (var total in report.Totals)
        {
            var mine = report.Items.Where(i => i.CurrencyCode == total.CurrencyCode).ToList();

            mine.Select(i => i.Rank).Should().Equal(Enumerable.Range(1, mine.Count));
            mine.Select(i => i.GrossProfit).Should().BeInDescendingOrder();
            (total.ProductCount, total.Revenue, total.CostOfGoodsSold, total.GrossProfit)
                .Should().Be((mine.Count, mine.Sum(i => i.Revenue), mine.Sum(i => i.CostOfGoodsSold), mine.Sum(i => i.GrossProfit)));
            total.GrossProfit.Should().Be(total.Revenue - total.CostOfGoodsSold);
        }
    }

    [Fact]
    public async Task When_every_sale_has_its_cost_R10_and_R8_are_the_same_money_to_within_rounding()
    {
        Sell("SINV-1", D(9, 5), LaptopBlack, 3m, 33.33m, cost: 61.5m, discount: 12.5m);
        Sell("SINV-2", D(9, 6), MouseV, 7m, 19.99m, cost: 100.25m);
        Sell("SINV-3", D(10, 7), LaptopSilver, 2m, 500m, cost: 800m);
        Sell("SINV-4", D(10, 8), CableV, 4m, 12.5m, cost: 20m, currency: "USD");

        var r10 = await _w.LedgerService().GetProductProfitabilityForExportAsync(On());
        var r8  = await _w.AnalysisService().GetSalesVsPurchaseForExportAsync(new SalesVsPurchaseFilter());

        r8.Totals.Sum(t => t.UncostedRevenue).Should().Be(0m);
        r10.Totals.Select(t => (t.CurrencyCode, t.CostOfGoodsSold)).Should().Equal(r8.Totals.Select(t => (t.CurrencyCode, t.CostOfGoodsSold)));
        foreach (var total in r10.Totals)
            Math.Abs(total.Revenue - r8.Totals.Single(t => t.CurrencyCode == total.CurrencyCode).Revenue).Should().BeLessThanOrEqualTo(0.05m, total.CurrencyCode);
    }
}
