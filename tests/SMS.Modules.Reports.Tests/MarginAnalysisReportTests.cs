using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Services;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Reports.Tests.MarginWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-05 §15 R7 — the margin sale orders are expected to make: what each order line sells for against
/// what the purchase orders raised for it cost, by product, customer or sale order and per currency.
/// </summary>
public class MarginAnalysisReportTests
{
    private readonly MarginWorld _w = new();

    private static readonly Guid Laptop = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Mouse  = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid LaptopBlack  = Guid.NewGuid();
    private static readonly Guid LaptopSilver = Guid.NewGuid();
    private static readonly Guid MouseVariant = Guid.NewGuid();
    private static readonly Guid Unknown      = Guid.NewGuid();

    public MarginAnalysisReportTests()
    {
        _w.Catalog(LaptopBlack, Laptop, "Laptop");
        _w.Catalog(LaptopSilver, Laptop, "Laptop");
        _w.Catalog(MouseVariant, Mouse, "Mouse");
    }

    private static MarginAnalysisFilter On(string? groupBy = null, DateTime? from = null, DateTime? to = null, int page = 1, int pageSize = 20) =>
        new() { GroupBy = groupBy, DateFrom = from, DateTo = to, Page = page, PageSize = pageSize };

    // ── What a row says ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_bought_in_line_is_what_it_sells_for_less_what_the_purchase_order_cost()
    {
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, 10m, 100m, new Buy(10m, 60m))]);

        var report = await _w.Service().GetMarginAnalysisAsync(On());

        var row = report.Items.Should().ContainSingle().Subject;
        (row.Name, row.CurrencyCode, row.LineCount, row.Quantity, row.SellingValue, row.Cost, row.Margin, row.MarginPercent, row.AverageSellingPrice, row.AverageCost)
            .Should().Be(("Laptop", "PKR", 1, (decimal?)10m, 1000m, 600m, 400m, (decimal?)40m, (decimal?)100m, (decimal?)60m));
        var total = report.Totals.Single();
        (total.CurrencyCode, total.GroupCount, total.LineCount, total.UncostedLineCount, total.SellingValue, total.Cost, total.Margin, total.MarginPercent)
            .Should().Be(("PKR", 1, 1, 0, 1000m, 600m, 400m, (decimal?)40m));
    }

    [Fact]
    public async Task The_selling_price_is_after_the_line_discount_so_a_discounted_line_shows_the_margin_it_will_really_make()
    {
        _w.Order("SO-1", D(9, 5), [new Sold(LaptopBlack, 10m, 100m, Discount: 10m, Buys: [new Buy(10m, 60m)])]);

        var row = (await _w.Service().GetMarginAnalysisAsync(On())).Items.Single();

        (row.SellingValue, row.Cost, row.Margin, row.MarginPercent).Should().Be((900m, 600m, 300m, (decimal?)33.33m));
    }

    [Fact]
    public async Task A_line_bought_from_several_suppliers_costs_the_blend_of_their_prices()
    {
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, 10m, 100m, new Buy(6m, 50m), new Buy(4m, 70m))]);

        var row = (await _w.Service().GetMarginAnalysisAsync(On())).Items.Single();

        (row.Quantity, row.Cost, row.AverageCost).Should().Be(((decimal?)10m, 580m, (decimal?)58m));
    }

    [Fact]
    public async Task A_line_that_is_part_stock_is_analysed_on_the_part_that_was_bought()
    {
        // 100 ordered, 60 from stock, 40 bought at 60: there is a cost for those 40 and none for the rest.
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, 100m, 100m, new Buy(40m, 60m))]);

        var row = (await _w.Service().GetMarginAnalysisAsync(On())).Items.Single();

        (row.Quantity, row.SellingValue, row.Cost, row.Margin).Should().Be(((decimal?)40m, 4000m, 2400m, 1600m));
    }

    [Fact]
    public async Task A_line_bought_in_more_units_than_it_was_ordered_is_analysed_on_the_units_it_was_ordered()
    {
        // 15 bought at 60 for a line of 10: the extra five went to stock, and are not this order's cost.
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, 10m, 100m, new Buy(15m, 60m))]);

        var row = (await _w.Service().GetMarginAnalysisAsync(On())).Items.Single();

        (row.Quantity, row.SellingValue, row.Cost).Should().Be(((decimal?)10m, 1000m, 600m));
    }

    [Fact]
    public async Task Cost_above_the_selling_price_is_a_negative_margin()
    {
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, 1m, 100m, new Buy(1m, 150m))]);

        var row = (await _w.Service().GetMarginAnalysisAsync(On())).Items.Single();

        (row.Margin, row.MarginPercent).Should().Be((-50m, (decimal?)-50m));
    }

    [Fact]
    public async Task A_line_given_away_has_a_cost_and_no_percent_because_there_is_nothing_to_take_it_of()
    {
        _w.Order("SO-1", D(9, 5), [new Sold(LaptopBlack, 1m, 100m, Discount: 100m, Buys: [new Buy(1m, 30m)])]);

        var row = (await _w.Service().GetMarginAnalysisAsync(On())).Items.Single();

        (row.SellingValue, row.Cost, row.Margin, row.MarginPercent).Should().Be((0m, 30m, -30m, (decimal?)null));
    }

    // ── Lines that are not costed ────────────────────────────────────────────

    [Fact]
    public async Task A_line_with_no_purchase_order_is_counted_and_not_costed()
    {
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, 10m, 100m, new Buy(10m, 60m)), Line(MouseVariant, 5m, 20m)]);

        var report = await _w.Service().GetMarginAnalysisAsync(On());

        report.Items.Should().ContainSingle().Which.Name.Should().Be("Laptop");
        var total = report.Totals.Single();
        (total.LineCount, total.UncostedLineCount, total.SellingValue).Should().Be((1, 1, 1000m));
    }

    [Fact]
    public async Task Only_uncosted_lines_leave_a_total_that_says_so_and_no_rows()
    {
        _w.Order("SO-1", D(9, 5), [Line(MouseVariant, 5m, 20m)]);

        var report = await _w.Service().GetMarginAnalysisAsync(On());

        report.Items.Should().BeEmpty();
        report.TotalRecords.Should().Be(0);
        var total = report.Totals.Single();
        (total.CurrencyCode, total.GroupCount, total.LineCount, total.UncostedLineCount, total.SellingValue, total.MarginPercent)
            .Should().Be(("PKR", 0, 0, 1, 0m, (decimal?)null));
    }

    [Fact]
    public async Task A_purchase_order_that_is_dead_or_for_something_else_is_not_a_cost_of_the_line()
    {
        _w.Order("SO-1", D(9, 5),
        [
            Line(LaptopBlack, 10m, 100m,
                new Buy(10m, 60m),
                new Buy(5m, 999m, Status: "CANCELLED"),
                new Buy(5m, 999m, Status: "REJECTED"),
                new Buy(5m, 999m, Deleted: true),
                new Buy(5m, 999m, OtherVariant: true),
                new Buy(5m, 999m, OtherLine: true)),
            Line(MouseVariant, 5m, 20m, new Buy(5m, 999m, Status: "CANCELLED"))
        ]);

        var report = await _w.Service().GetMarginAnalysisAsync(On());

        report.Items.Should().ContainSingle().Which.Cost.Should().Be(600m);
        report.Totals.Single().UncostedLineCount.Should().Be(1, "the mouse line's only purchase order was cancelled");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("CANCELLED")]
    public async Task A_draft_or_cancelled_order_is_not_analysed_and_not_counted_as_uncosted(string status)
    {
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, 10m, 100m, new Buy(10m, 60m)), Line(MouseVariant, 1m, 1m)], status: status);

        var report = await _w.Service().GetMarginAnalysisAsync(On());

        report.Items.Should().BeEmpty();
        report.Totals.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CONFIRMED")]
    [InlineData("PARTIALLY_FULFILLED")]
    [InlineData("FULFILLED")]
    [InlineData("INVOICED")]
    [InlineData("CLOSED")]
    public async Task An_order_that_has_been_confirmed_stays_in_the_analysis_however_far_it_has_come(string status)
    {
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, 10m, 100m, new Buy(10m, 60m))], status: status);

        (await _w.Service().GetMarginAnalysisAsync(On())).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task A_deleted_order_a_cancelled_line_and_another_organizations_order_are_left_out()
    {
        _w.Order("SO-LIVE", D(9, 5), [Line(LaptopBlack, 10m, 100m, new Buy(10m, 60m)), new Sold(MouseVariant, 9m, 9m, Status: "CANCELLED", Buys: [new Buy(9m, 1m)])]);
        _w.Order("SO-DELETED", D(9, 5), [Line(LaptopBlack, 99m, 999m, new Buy(99m, 1m))], deleted: true);
        _w.Order("SO-THEIRS", D(9, 5), [Line(LaptopBlack, 99m, 999m, new Buy(99m, 1m))], org: OtherOrg);

        var report = await _w.Service().GetMarginAnalysisAsync(On());

        report.Items.Should().ContainSingle().Which.SellingValue.Should().Be(1000m);
        report.Totals.Single().UncostedLineCount.Should().Be(0);
    }

    // ── Groupings ────────────────────────────────────────────────────────────

    private void TwoCustomersAndProducts()
    {
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, 10m, 100m, new Buy(10m, 60m)), Line(MouseVariant, 10m, 20m, new Buy(10m, 15m))], partner: _w.Acme);
        _w.Order("SO-2", D(9, 6), [Line(LaptopSilver, 5m, 110m, new Buy(5m, 70m))], partner: _w.Acme);
        _w.Order("SO-3", D(9, 7), [Line(LaptopBlack, 2m, 120m, new Buy(2m, 100m))], partner: _w.Globex);
    }

    [Fact]
    public async Task By_product_every_variant_of_a_product_is_one_row_over_every_order_and_units_add()
    {
        TwoCustomersAndProducts();

        var report = await _w.Service().GetMarginAnalysisAsync(On("product"));

        report.Criteria.GroupBy.Should().Be("PRODUCT");
        report.Items.Select(i => (i.GroupId, i.Name, i.LineCount, i.Quantity, i.SellingValue, i.Cost, i.Margin, i.Detail))
            .Should().Equal((Laptop, "Laptop", 3, (decimal?)17m, 1790m, 1150m, 640m, (string?)null), (Mouse, "Mouse", 1, (decimal?)10m, 200m, 150m, 50m, (string?)null));
    }

    [Fact]
    public async Task By_customer_a_customers_orders_are_one_row_and_units_are_not_added_across_products()
    {
        TwoCustomersAndProducts();

        var report = await _w.Service().GetMarginAnalysisAsync(On("CUSTOMER"));

        report.Items.Select(i => (i.GroupId, i.Name, i.LineCount, i.SellingValue, i.Cost, i.Margin, i.Quantity, i.AverageCost, i.Detail))
            .Should().Equal(
                (_w.Acme, "Acme Ltd", 3, 1750m, 1100m, 650m, (decimal?)null, (decimal?)null, (string?)null),
                (_w.Globex, "Globex Corp", 1, 240m, 200m, 40m, (decimal?)null, (decimal?)null, (string?)null));
    }

    [Fact]
    public async Task By_order_each_order_is_a_row_named_by_its_number_with_its_customer_beside_it()
    {
        TwoCustomersAndProducts();

        var report = await _w.Service().GetMarginAnalysisAsync(On("Order"));

        report.Items.Select(i => (i.Name, i.Detail, i.LineCount, i.SellingValue, i.Margin))
            .Should().Equal(("SO-1", "Acme Ltd", 2, 1200m, 450m), ("SO-2", "Acme Ltd", 1, 550m, 200m), ("SO-3", "Globex Corp", 1, 240m, 40m));
    }

    [Fact]
    public async Task Product_is_the_default_and_a_word_in_any_case_is_understood()
    {
        TwoCustomersAndProducts();

        (await _w.Service().GetMarginAnalysisAsync(On())).Criteria.GroupBy.Should().Be("PRODUCT");
        (await _w.Service().GetMarginAnalysisAsync(On("  "))).Criteria.GroupBy.Should().Be("PRODUCT");
        (await _w.Service().GetMarginAnalysisAsync(On(" customer "))).Criteria.GroupBy.Should().Be("CUSTOMER");
    }

    [Fact]
    public async Task Something_that_is_not_a_grouping_is_refused_with_the_valid_ones()
    {
        var act = () => _w.Service().GetMarginAnalysisAsync(On("supplier"));

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("'supplier'").And.Contain("PRODUCT, CUSTOMER, ORDER");
    }

    [Fact]
    public async Task A_variant_inventory_cannot_name_is_its_own_product_with_no_name_and_a_customer_the_lookup_cannot_name_has_none()
    {
        _w.Order("SO-1", D(9, 5), [Line(Unknown, 1m, 10m, new Buy(1m, 4m))], partner: StrangerId);

        var product  = (await _w.Service().GetMarginAnalysisAsync(On("product"))).Items.Single();
        var customer = (await _w.Service().GetMarginAnalysisAsync(On("customer"))).Items.Single();

        (product.GroupId, product.Name).Should().Be((Unknown, (string?)null));
        (customer.GroupId, customer.Name).Should().Be((StrangerId, (string?)null));
    }

    // ── Currencies, ranking, totals ──────────────────────────────────────────

    [Fact]
    public async Task Each_currency_has_its_own_rows_and_total_and_a_currency_the_catalog_lost_is_a_question_mark()
    {
        _w.Order("SO-PKR", D(9, 5), [Line(LaptopBlack, 1m, 100m, new Buy(1m, 60m))]);
        _w.Order("SO-USD", D(9, 6), [Line(LaptopBlack, 1m, 30m, new Buy(1m, 20m))], currency: _w.Usd);
        _w.Order("SO-LOST", D(9, 7), [Line(LaptopBlack, 1m, 10m, new Buy(1m, 9m))], currency: Guid.NewGuid());
        _w.Order("SO-LOST-2", D(9, 8), [Line(LaptopBlack, 1m, 10m, new Buy(1m, 8m))], currency: Guid.NewGuid());

        var report = await _w.Service().GetMarginAnalysisAsync(On());

        report.Items.Select(i => (i.CurrencyCode, i.SellingValue, i.Margin)).Should().BeEquivalentTo(new[] { ("PKR", 100m, 40m), ("USD", 30m, 10m), ("?", 20m, 3m) });
        report.Totals.Select(t => (t.CurrencyCode, t.GroupCount, t.SellingValue, t.Cost, t.Margin))
            .Should().Equal(("?", 1, 20m, 17m, 3m), ("PKR", 1, 100m, 60m, 40m), ("USD", 1, 30m, 20m, 10m));
    }

    [Fact]
    public async Task Highest_margin_first_and_the_totals_are_over_every_row_not_the_page()
    {
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, 1m, 100m, new Buy(1m, 90m))]);
        _w.Order("SO-2", D(9, 5), [Line(LaptopBlack, 1m, 100m, new Buy(1m, 10m))]);
        _w.Order("SO-3", D(9, 5), [Line(LaptopBlack, 1m, 100m, new Buy(1m, 50m))]);

        var page = await _w.Service().GetMarginAnalysisAsync(On("order", page: 2, pageSize: 1));

        page.Items.Select(i => (i.Name, i.Margin)).Should().Equal(("SO-3", 50m));
        (page.TotalRecords, page.TotalPages, page.Page, page.PageSize).Should().Be((3, 3, 2, 1));
        page.Totals.Single().Margin.Should().Be(150m);

        var export = await _w.Service().GetMarginAnalysisForExportAsync(On("order", page: 2, pageSize: 1));
        export.Items.Select(i => i.Name).Should().Equal("SO-2", "SO-3", "SO-1");
        (export.Page, export.PageSize, export.TotalPages).Should().Be((1, 3, 1));
    }

    [Fact]
    public async Task A_page_size_is_held_to_one_to_a_hundred_and_a_page_below_one_is_the_first()
    {
        foreach (var i in Enumerable.Range(1, 3)) _w.Order($"SO-{i}", D(9, 5), [Line(LaptopBlack, 1m, 100m, new Buy(1m, 10m * i))]);

        var report = await _w.Service().GetMarginAnalysisAsync(On("order", page: 0, pageSize: 1000));

        (report.Page, report.PageSize, report.Items.Count).Should().Be((1, 100, 3));
    }

    // ── The range ────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_range_is_the_order_date_in_whole_days_inclusive_at_both_ends_and_either_end_may_be_left_open()
    {
        _w.Order("SO-BEFORE", D(9, 4).AddDays(1).AddTicks(-1), [Line(LaptopBlack, 1m, 1m, new Buy(1m, 0.5m))]);
        _w.Order("SO-FIRST", D(9, 5), [Line(LaptopBlack, 1m, 10m, new Buy(1m, 5m))]);
        _w.Order("SO-LAST-TICK", D(9, 10).AddDays(1).AddTicks(-1), [Line(LaptopBlack, 1m, 100m, new Buy(1m, 50m))]);
        _w.Order("SO-NEXT-DAY", D(9, 11), [Line(LaptopBlack, 1m, 1000m, new Buy(1m, 500m))]);

        (await _w.Service().GetMarginAnalysisAsync(On(from: D(9, 5), to: D(9, 10)))).Totals.Single().SellingValue.Should().Be(110m);
        (await _w.Service().GetMarginAnalysisAsync(On(from: D(9, 5)))).Totals.Single().SellingValue.Should().Be(1110m);
        (await _w.Service().GetMarginAnalysisAsync(On(to: D(9, 10)))).Totals.Single().SellingValue.Should().Be(111m);
    }

    [Fact]
    public async Task The_range_also_bounds_the_lines_counted_as_uncosted()
    {
        _w.Order("SO-IN", D(9, 5), [Line(MouseVariant, 1m, 1m)]);
        _w.Order("SO-OUT", D(9, 20), [Line(MouseVariant, 1m, 1m)]);

        (await _w.Service().GetMarginAnalysisAsync(On(from: D(9, 1), to: D(9, 10)))).Totals.Single().UncostedLineCount.Should().Be(1);
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var act = () => _w.Service().GetMarginAnalysisAsync(On(from: D(9, 10), to: D(9, 5)));

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("start date is after its end date");
    }

    [Fact]
    public async Task The_criteria_echo_the_range_and_the_company_and_date_are_the_clocks()
    {
        var report = await _w.Service().GetMarginAnalysisAsync(On("order", D(9, 1).AddHours(5), D(9, 30).AddHours(9)));

        (report.Criteria.DateFrom, report.Criteria.DateTo, report.Criteria.GroupBy).Should().Be(((DateTime?)D(9, 1), (DateTime?)D(9, 30), "ORDER"));
        (report.CompanyName, report.GeneratedAt).Should().Be(("Northwind Trading", FixedClock.Start));
    }

    [Fact]
    public async Task Nothing_to_analyse_is_an_empty_report_not_an_error()
    {
        var report = await _w.Service().GetMarginAnalysisAsync(On());

        report.Items.Should().BeEmpty();
        report.Totals.Should().BeEmpty();
        (report.TotalRecords, report.TotalPages).Should().Be((0, 0));
    }

    [Fact]
    public async Task Inventory_is_only_asked_when_the_report_is_by_product()
    {
        TwoCustomersAndProducts();
        var resolver = new Mock<IProductVariantResolver>();

        await _w.Service(resolver: resolver).GetMarginAnalysisAsync(On("customer"));
        await _w.Service(resolver: resolver).GetMarginAnalysisAsync(On("order"));
        resolver.Verify(r => r.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Never);

        await _w.Service(resolver: resolver).GetMarginAnalysisAsync(On("product"));
        resolver.Verify(r => r.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
    }

    // ── Agreement with what the order line stores ────────────────────────────

    [Theory]
    [InlineData(10.00, 7.3333, 3)]
    [InlineData(100.00, 60.00, 10)]
    [InlineData(19.99, 24.50, 7)]
    [InlineData(1234.56, 0.01, 2)]
    public async Task On_an_undiscounted_line_the_percent_is_the_one_the_line_itself_stores(double price, double cost, int units)
    {
        _w.Order("SO-1", D(9, 5), [Line(LaptopBlack, units, (decimal)price, new Buy(units, (decimal)cost))]);

        var row = (await _w.Service().GetMarginAnalysisAsync(On())).Items.Single();
        var (storedMargin, storedPercent) = SaleOrderMargin.Compute((decimal)price, (decimal)cost);

        // The line stores its margin per unit, to the cent; the report keeps it in money, and so to the cent of the total.
        Math.Abs(row.MarginPercent!.Value - storedPercent!.Value).Should().BeLessThanOrEqualTo(0.01m);
        Math.Abs(row.Margin - storedMargin * units).Should().BeLessThanOrEqualTo(0.005m * units + 0.005m);
    }

    // ── Many rows, checked against the books added up another way ────────────

    private sealed record Sums(int Lines, int Uncosted, decimal Selling, decimal Cost);

    /// <summary>What the scattered books say per currency, worked out by hand from the raw rows and without any of the service's queries.</summary>
    private Dictionary<string, Sums> Oracle()
    {
        using var db = _w.Db(Org);
        var orders = db.SaleOrders.AsNoTracking().Include(o => o.Lines).Where(o => o.OrganizationId == Org).ToList();
        var pos    = db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).Where(p => p.OrganizationId == Org).ToList();

        var result = new Dictionary<string, (int Lines, int Uncosted, decimal Selling, decimal Cost)>();
        foreach (var order in orders.Where(o => !o.IsDeleted && o.Status != "DRAFT" && o.Status != "CANCELLED"))
        {
            var currency = _w.Currencies.FirstOrDefault(c => c.Id == order.CurrencyId)?.Code ?? "?";
            foreach (var line in order.Lines.Where(l => l.Status != "CANCELLED"))
            {
                var bought = pos.Where(p => p.LinkedSoLineId == line.Id && !p.IsDelete && p.Status != "CANCELLED" && p.Status != "REJECTED")
                    .SelectMany(p => p.Lines).Where(l => l.VariantUuid == line.VariantUuid).ToList();
                var qty = bought.Sum(b => b.Quantity);

                var (lines, uncosted, selling, cost) = result.GetValueOrDefault(currency);
                if (qty <= 0m) { result[currency] = (lines, uncosted + 1, selling, cost); continue; }

                var units = Math.Min(qty, line.Quantity);
                result[currency] = (lines + 1, uncosted,
                    selling + units * line.UnitPrice * (1m - line.DiscountPercent / 100m),
                    cost + bought.Sum(b => b.Quantity * b.UnitPrice) / qty * units);
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => new Sums(kv.Value.Lines, kv.Value.Uncosted, kv.Value.Selling, kv.Value.Cost));
    }

    [Theory]
    [InlineData("product")]
    [InlineData("customer")]
    [InlineData("order")]
    public async Task On_sixty_scattered_orders_every_grouping_adds_up_to_what_the_raw_rows_add_up_to(string groupBy)
    {
        _w.SeedScatter();
        var expected = Oracle();

        var report = await _w.Service().GetMarginAnalysisForExportAsync(On(groupBy));

        expected.Count.Should().Be(3, "guards the comparison: two currencies and a lost one");
        expected.Values.Sum(e => e.Lines).Should().BeGreaterThan(30);
        expected.Values.Sum(e => e.Uncosted).Should().BeGreaterThan(5);
        report.Totals.Select(t => t.CurrencyCode).Should().BeEquivalentTo(expected.Keys);

        foreach (var total in report.Totals)
        {
            var want = expected[total.CurrencyCode];
            var rows = report.Items.Where(i => i.CurrencyCode == total.CurrencyCode).ToList();
            var slack = 0.005m * rows.Count + 0.01m; // each row is rounded to the cent on its own

            (total.LineCount, total.UncostedLineCount).Should().Be((want.Lines, want.Uncosted), total.CurrencyCode);
            Math.Abs(total.SellingValue - want.Selling).Should().BeLessThanOrEqualTo(slack, $"{groupBy} {total.CurrencyCode} selling");
            Math.Abs(total.Cost - want.Cost).Should().BeLessThanOrEqualTo(slack, $"{groupBy} {total.CurrencyCode} cost");
            (total.GroupCount, total.SellingValue, total.Cost, total.Margin)
                .Should().Be((rows.Count, rows.Sum(r => r.SellingValue), rows.Sum(r => r.Cost), rows.Sum(r => r.Margin)));
        }

        report.Items.Should().OnlyContain(i => i.Margin == i.SellingValue - i.Cost);
        report.Items.Select(i => i.Margin).Should().BeInDescendingOrder();
    }
}
