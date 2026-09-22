using FluentAssertions;
using Moq;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Reports.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Reports.Tests.ReceivablesReportWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-06 §15 R9 — a product's stock account: every movement of every variant of it, with the quantity and
/// value the product held after each, and where it stood when the range began and ended.
/// </summary>
public class ProductLedgerReportTests
{
    private readonly ReceivablesReportWorld _w = new();

    private static readonly Guid Laptop = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Mouse  = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid Black  = Guid.Parse("b0000000-0000-0000-0000-000000000001");
    private static readonly Guid Silver = Guid.Parse("b0000000-0000-0000-0000-000000000002");
    private static readonly Guid MouseV = Guid.Parse("b0000000-0000-0000-0000-000000000003");

    public ProductLedgerReportTests()
    {
        _w.Catalog(Black, Laptop, "Laptop", sku: "LAP-BLK", variantName: "Black");
        _w.Catalog(Silver, Laptop, "Laptop", sku: "LAP-SLV", variantName: "Silver", isDefault: false);
        _w.Catalog(MouseV, Mouse, "Mouse", sku: "MOU-001");
    }

    private static ProductLedgerReportFilter For(
        Guid product, DateTime? from = null, DateTime? to = null, Guid? variant = null, int page = 1, int pageSize = 20) =>
        new() { ProductId = product, VariantId = variant, DateFrom = from, DateTo = to, Page = page, PageSize = pageSize };

    private ProductLedgerEntry Buy(Guid variant, decimal qty, decimal cost, DateTime date, Guid? product = null, Guid? partner = null, Guid? org = null) =>
        _w.Move(variant, product ?? Laptop, "PURCHASE", qty, date, unitCost: cost, partner: partner, org: org);

    private ProductLedgerEntry Sell(Guid variant, decimal qty, DateTime date, Guid? product = null, Guid? partner = null, Guid? org = null) =>
        _w.Move(variant, product ?? Laptop, "SALE", qty, date, partner: partner, org: org);

    // ── What a row says ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_products_movements_carry_the_quantity_and_value_it_held_after_each()
    {
        Buy(Black, 100m, 10m, D(9, 1));
        Sell(Black, 40m, D(9, 3));
        _w.Move(Black, Laptop, "RETURN_IN", 5m, D(9, 4), unitCost: 10m);
        _w.Move(Black, Laptop, "WRITE_OFF", 5m, D(9, 5));

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop));

        report.Items.Select(i => (i.EntryType, i.Direction, i.Quantity, i.TotalCost, i.RunningQty, i.RunningValue, i.WeightedAverageCost))
            .Should().Equal(
                ("PURCHASE", "IN", 100m, 1000m, 100m, 1000m, 10m),
                ("SALE", "OUT", 40m, 400m, 60m, 600m, 10m),
                ("RETURN_IN", "IN", 5m, 50m, 65m, 650m, 10m),
                ("WRITE_OFF", "OUT", 5m, 50m, 60m, 600m, 10m));
        report.Items.Should().OnlyContain(i => i.VariantRunningQty == i.RunningQty && i.VariantRunningValue == i.RunningValue, "one variant: the product's figures are the variant's");
    }

    [Fact]
    public async Task The_summary_is_where_it_started_what_came_in_and_went_out_and_where_it_ended()
    {
        Buy(Black, 100m, 10m, D(9, 1));
        Sell(Black, 40m, D(9, 3));
        _w.Move(Black, Laptop, "RETURN_IN", 5m, D(9, 4), unitCost: 10m);
        _w.Move(Black, Laptop, "WRITE_OFF", 5m, D(9, 5));

        var s = (await _w.LedgerService().GetProductLedgerAsync(For(Laptop))).Summary;

        (s.OpeningQuantity, s.OpeningValue, s.QuantityIn, s.ValueIn, s.QuantityOut, s.ValueOut, s.ClosingQuantity, s.ClosingValue, s.ClosingWeightedAverageCost, s.MovementCount)
            .Should().Be((0m, 0m, 105m, 1050m, 45m, 450m, 60m, 600m, 10m, 4));
    }

    [Fact]
    public async Task A_product_of_several_variants_runs_over_all_of_them_and_each_row_also_shows_its_own_variants_figures()
    {
        Buy(Black, 10m, 100m, D(9, 1));    // product: 10 for 1,000
        Buy(Silver, 5m, 120m, D(9, 2));    // product: 15 for 1,600
        Sell(Black, 4m, D(9, 3));          // black leaves at 100: product 11 for 1,200
        Sell(Silver, 1m, D(9, 3));         // silver leaves at 120: product 10 for 1,080

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop));

        report.Items.Select(i => (i.Sku, i.EntryType, i.RunningQty, i.RunningValue, i.VariantRunningQty, i.VariantRunningValue))
            .Should().Equal(
                ("LAP-BLK", "PURCHASE", 10m, 1000m, 10m, 1000m),
                ("LAP-SLV", "PURCHASE", 15m, 1600m, 5m, 600m),
                ("LAP-BLK", "SALE", 11m, 1200m, 6m, 600m),
                ("LAP-SLV", "SALE", 10m, 1080m, 4m, 480m));
        report.Items.Last().WeightedAverageCost.Should().Be(108m);
        report.Summary.ClosingQuantity.Should().Be(10m);
    }

    [Fact]
    public async Task The_product_ends_where_its_variants_ledgers_end_added_together()
    {
        Buy(Black, 10m, 100m, D(9, 1));
        Buy(Silver, 5m, 120m, D(9, 2));
        Sell(Black, 4m, D(9, 3));
        _w.Move(Silver, Laptop, "ADJUSTMENT", 2m, D(9, 4), unitCost: 90m);
        Sell(Silver, 1m, D(9, 5));

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop));

        await using var db = _w.Db();
        var last = new[] { Black, Silver }.Select(v => db.ProductLedgerEntries.Where(e => e.VariantUuid == v).OrderByDescending(e => e.SequenceNo).First()).ToList();
        (report.Summary.ClosingQuantity, report.Summary.ClosingValue).Should().Be((last.Sum(e => e.RunningQty), last.Sum(e => e.RunningValue)));
        (report.Items.Last().RunningQty, report.Items.Last().RunningValue).Should().Be((report.Summary.ClosingQuantity, report.Summary.ClosingValue));
    }

    [Fact]
    public async Task Movements_read_by_business_date_and_a_back_dated_one_lands_where_its_goods_moved()
    {
        Buy(Black, 10m, 100m, D(9, 5), partner: null);
        Buy(Silver, 5m, 120m, D(9, 2));   // posted after, dated before

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop));

        report.Items.Select(i => (i.Sku, i.EntryDate, i.RunningQty)).Should().Equal(("LAP-SLV", D(9, 2), 5m), ("LAP-BLK", D(9, 5), 15m));
    }

    [Fact]
    public async Task Within_a_day_movements_keep_the_order_they_were_posted_in()
    {
        Buy(Black, 10m, 10m, D(9, 5));
        Sell(Black, 4m, D(9, 5));
        Buy(Black, 1m, 10m, D(9, 5));

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop));

        report.Items.Select(i => i.EntryType).Should().Equal("PURCHASE", "SALE", "PURCHASE");
        report.Items.Select(i => i.RunningQty).Should().Equal(10m, 6m, 7m);
    }

    [Fact]
    public async Task On_one_day_movements_of_different_variants_keep_the_order_they_were_posted_in_not_their_own_sequence_numbers()
    {
        Buy(Black, 10m, 10m, D(9, 5));     // black's first
        Sell(Black, 4m, D(9, 5));          // black's second
        Buy(Silver, 5m, 10m, D(9, 5));     // silver's first, but posted last

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop));

        report.Items.Select(i => (i.Sku, i.EntryType, i.RunningQty)).Should().Equal(("LAP-BLK", "PURCHASE", 10m), ("LAP-BLK", "SALE", 6m), ("LAP-SLV", "PURCHASE", 11m));
    }

    // ── The range ────────────────────────────────────────────────────────────

    private void ThreeMonthsOfHistory()
    {
        Buy(Black, 100m, 10m, D(8, 20));
        Sell(Black, 30m, D(9, 5));
        Sell(Black, 20m, D(9, 10).AddHours(15));
        Sell(Black, 10m, D(10, 2));
    }

    [Fact]
    public async Task A_range_starts_from_what_the_product_held_the_day_before_it()
    {
        ThreeMonthsOfHistory();

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop, D(9, 1), D(9, 30)));

        (report.Summary.OpeningQuantity, report.Summary.OpeningValue).Should().Be((100m, 1000m));
        report.Items.Select(i => (i.RunningQty, i.RunningValue)).Should().Equal((70m, 700m), (50m, 500m));
        (report.Summary.ClosingQuantity, report.Summary.ClosingValue, report.Summary.MovementCount).Should().Be((50m, 500m, 2));
    }

    [Fact]
    public async Task The_range_is_whole_days_inclusive_at_both_ends_and_either_end_may_be_left_open()
    {
        ThreeMonthsOfHistory();

        (await _w.LedgerService().GetProductLedgerAsync(For(Laptop, D(9, 10), D(9, 10)))).Items.Should().ContainSingle("the 10th's movement is at 15:00");
        (await _w.LedgerService().GetProductLedgerAsync(For(Laptop, D(9, 6), null))).Summary.OpeningQuantity.Should().Be(70m);
        (await _w.LedgerService().GetProductLedgerAsync(For(Laptop, null, D(9, 5)))).Summary.OpeningQuantity.Should().Be(0m, "an open start begins with nothing");
        (await _w.LedgerService().GetProductLedgerAsync(For(Laptop, null, D(9, 5)))).Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task The_range_takes_the_first_instant_of_its_first_day_and_the_last_tick_of_its_last_and_no_more()
    {
        Buy(Black, 1m, 1m, D(9, 4).AddDays(1).AddTicks(-1));      // the last tick before the range: it is the opening balance
        Buy(Black, 10m, 1m, D(9, 5));                              // the first instant of the range
        Buy(Black, 100m, 1m, D(9, 10).AddDays(1).AddTicks(-1));   // the last tick of the range
        Buy(Black, 1000m, 1m, D(9, 11));                           // the first instant after it

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop, D(9, 5), D(9, 10)));

        report.Items.Select(i => i.Quantity).Should().Equal(10m, 100m);
        (report.Summary.OpeningQuantity, report.Summary.ClosingQuantity).Should().Be((1m, 111m));
    }

    [Fact]
    public async Task A_range_with_no_movements_is_the_opening_balance_carried_across()
    {
        ThreeMonthsOfHistory();

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop, D(12, 1), D(12, 31)));

        report.Items.Should().BeEmpty();
        (report.Summary.OpeningQuantity, report.Summary.ClosingQuantity, report.Summary.ClosingValue, report.Summary.MovementCount).Should().Be((40m, 40m, 400m, 0));
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var act = () => _w.LedgerService().GetProductLedgerAsync(For(Laptop, D(9, 10), D(9, 5)));

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("start date is after its end date");
    }

    // ── One variant, or another product ──────────────────────────────────────

    [Fact]
    public async Task Naming_a_variant_narrows_the_account_to_it_opening_balance_included()
    {
        Buy(Black, 10m, 100m, D(8, 1));
        Buy(Silver, 5m, 120m, D(8, 2));
        Sell(Silver, 1m, D(9, 3));
        Sell(Black, 4m, D(9, 4));

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop, D(9, 1), variant: Silver));

        (report.Summary.OpeningQuantity, report.Summary.OpeningValue).Should().Be((5m, 600m));
        report.Items.Should().ContainSingle().Which.Sku.Should().Be("LAP-SLV");
        (report.Criteria.VariantUuid, report.Criteria.VariantName).Should().Be(((Guid?)Silver, "Silver"));
    }

    [Fact]
    public async Task Another_products_movements_never_appear_in_this_ones_ledger()
    {
        Buy(Black, 10m, 100m, D(9, 1));
        _w.Move(MouseV, Mouse, "PURCHASE", 99m, D(9, 1), unitCost: 1m);

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop));

        report.Items.Should().ContainSingle().Which.VariantUuid.Should().Be(Black);
        report.Summary.ClosingQuantity.Should().Be(10m);
    }

    [Fact]
    public async Task Another_organizations_movements_never_appear_and_never_open_the_balance()
    {
        Buy(Black, 10m, 100m, D(8, 1));
        Buy(Black, 999m, 1m, D(8, 1), org: OtherOrg);
        Buy(Black, 7m, 100m, D(9, 5), org: OtherOrg);

        var mine   = await _w.LedgerService(Org).GetProductLedgerAsync(For(Laptop, D(9, 1)));
        var theirs = await _w.LedgerService(OtherOrg).GetProductLedgerAsync(For(Laptop, D(9, 1)));

        (mine.Summary.OpeningQuantity, mine.Items.Count).Should().Be((10m, 0));
        (theirs.Summary.OpeningQuantity, theirs.Items.Count).Should().Be((999m, 1));
    }

    // ── Names ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_product_the_variants_and_the_partners_are_named_and_a_movement_with_no_partner_has_none()
    {
        Buy(Black, 10m, 100m, D(9, 1), partner: _w.Acme);
        _w.Move(Black, Laptop, "WRITE_OFF", 1m, D(9, 2));
        Buy(Silver, 5m, 120m, D(9, 3), partner: Stranger);

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop));

        (report.Criteria.ProductUuid, report.Criteria.ProductName).Should().Be((Laptop, "Laptop"));
        report.Criteria.VariantUuid.Should().BeNull();
        report.Items.Select(i => (i.Sku, i.VariantName, i.PartnerId, i.PartnerName)).Should().Equal(
            ("LAP-BLK", "Black", (Guid?)_w.Acme, "Acme Ltd"),
            ("LAP-BLK", "Black", (Guid?)null, (string?)null),
            ("LAP-SLV", "Silver", (Guid?)Stranger, (string?)null));
        (report.CompanyName, report.GeneratedAt).Should().Be(("Northwind Trading", _w.Now));
    }

    [Fact]
    public async Task Only_the_partners_on_the_page_are_looked_up()
    {
        Buy(Black, 10m, 100m, D(9, 1), partner: _w.Acme);
        Buy(Black, 10m, 100m, D(9, 2), partner: _w.Globex);
        var names = new Mock<ISupplierNameLookupService>();

        await _w.LedgerService(names: names).GetProductLedgerAsync(For(Laptop, page: 2, pageSize: 1));

        names.Invocations.Should().ContainSingle().Which.Arguments[0].Should().BeAssignableTo<IReadOnlyList<Guid>>().Which.Should().Equal(_w.Globex);
    }

    // ── Paging and totals ────────────────────────────────────────────────────

    [Fact]
    public async Task Every_pages_running_figures_follow_from_the_whole_range_and_the_summary_is_over_all_of_it()
    {
        foreach (var day in Enumerable.Range(1, 5)) Buy(Black, 10m, 10m, D(9, day));

        var page2 = await _w.LedgerService().GetProductLedgerAsync(For(Laptop, page: 2, pageSize: 2));

        page2.Items.Select(i => (i.EntryDate, i.RunningQty)).Should().Equal((D(9, 3), 30m), (D(9, 4), 40m));
        (page2.TotalRecords, page2.TotalPages, page2.Page, page2.PageSize).Should().Be((5, 3, 2, 2));
        (page2.Summary.ClosingQuantity, page2.Summary.QuantityIn, page2.Summary.MovementCount).Should().Be((50m, 50m, 5));
    }

    [Fact]
    public async Task The_export_carries_every_movement_whatever_the_page_says()
    {
        foreach (var day in Enumerable.Range(1, 5)) Buy(Black, 10m, 10m, D(9, day));

        var export = await _w.LedgerService().GetProductLedgerForExportAsync(For(Laptop, page: 2, pageSize: 2));

        (export.Items.Count, export.Page, export.PageSize, export.TotalPages).Should().Be((5, 1, 5, 1));
    }

    [Fact]
    public async Task A_page_size_is_held_to_one_to_a_hundred_and_a_page_below_one_is_the_first()
    {
        Buy(Black, 10m, 10m, D(9, 1));

        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop, page: 0, pageSize: 1000));

        (report.Page, report.PageSize).Should().Be((1, 100));
    }

    // ── What is refused ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_product_ledger_is_of_one_product_so_naming_none_is_refused()
    {
        foreach (var filter in new[] { new ProductLedgerReportFilter(), new ProductLedgerReportFilter { ProductId = Guid.Empty } })
        {
            var act = () => _w.LedgerService().GetProductLedgerAsync(filter);
            (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("Name the product");
        }
    }

    [Fact]
    public async Task An_empty_variant_id_is_refused()
    {
        var act = () => _w.LedgerService().GetProductLedgerAsync(For(Laptop, variant: Guid.Empty));

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_product_nobody_knows_is_not_found()
    {
        var act = () => _w.LedgerService().GetProductLedgerAsync(For(Guid.NewGuid()));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_product_inventory_knows_that_has_never_moved_is_an_empty_named_ledger_not_a_missing_one()
    {
        var report = await _w.LedgerService().GetProductLedgerAsync(For(Laptop));

        report.Items.Should().BeEmpty();
        (report.Criteria.ProductName, report.Summary.ClosingQuantity, report.Summary.ClosingValue, report.TotalRecords, report.TotalPages).Should().Be(("Laptop", 0m, 0m, 0, 0));
    }

    [Fact]
    public async Task A_product_that_has_moved_but_inventory_no_longer_names_is_still_a_ledger_with_no_name()
    {
        var gone = Guid.NewGuid();
        _w.Move(Guid.NewGuid(), gone, "PURCHASE", 5m, D(9, 1), unitCost: 10m);

        var report = await _w.LedgerService().GetProductLedgerAsync(For(gone));

        (report.Criteria.ProductName, report.Items.Count).Should().Be(((string?)null, 1));
        report.Items[0].Sku.Should().BeNull();
    }

    // ── Many movements, checked against the books added up another way ───────

    [Fact]
    public async Task On_a_scattered_history_every_running_figure_is_the_signed_sum_of_what_came_before_it()
    {
        _w.SeedLedgerScatter();

        var report = await _w.LedgerService().GetProductLedgerForExportAsync(For(ReceivablesReportWorld.ScatterProducts[0], D(8, 15), D(9, 15)));

        report.Items.Count.Should().BeGreaterThan(15, "guards the comparison: a report of nothing agrees with anything");
        report.Items.Select(i => i.EntryType).Distinct().Should().HaveCountGreaterThan(3);
        var qty = report.Summary.OpeningQuantity;
        var value = report.Summary.OpeningValue;
        foreach (var i in report.Items)
        {
            (qty, value) = i.Direction == "IN" ? (qty + i.Quantity, value + i.TotalCost) : (qty - i.Quantity, value - i.TotalCost);
            (i.RunningQty, i.RunningValue).Should().Be((qty, value));
        }

        (report.Summary.ClosingQuantity, report.Summary.ClosingValue).Should().Be((qty, value));
        report.Items.Select(i => i.EntryDate.Date).Should().BeInAscendingOrder();
        report.Items.Should().OnlyContain(i => i.RunningQty >= 0m, "a ledger never goes below nothing");
    }

    [Fact]
    public async Task Over_the_whole_history_the_product_ends_where_its_variants_ledgers_end()
    {
        _w.SeedLedgerScatter();
        var product = ReceivablesReportWorld.ScatterProducts[0];

        var report = await _w.LedgerService().GetProductLedgerForExportAsync(For(product));

        await using var db = _w.Db();
        var lasts = db.ProductLedgerEntries.Where(e => e.ProductUuid == product).ToList()
            .GroupBy(e => e.VariantUuid).Select(g => g.OrderByDescending(e => e.SequenceNo).First()).ToList();
        lasts.Count.Should().BeGreaterThan(1);
        (report.Summary.ClosingQuantity, report.Summary.ClosingValue).Should().Be((lasts.Sum(e => e.RunningQty), lasts.Sum(e => e.RunningValue)));
    }
}
