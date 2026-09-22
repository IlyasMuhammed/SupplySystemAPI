using FluentAssertions;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

using static SMS.Modules.Finance.Tests.ProductLedgerRig;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P8-06 §11 — the product ledger's arithmetic, one test per thing the task lists, worked by hand
/// with literal figures. The rule-by-rule tests live beside the code (<c>WeightedAverageCostingTests</c>,
/// <c>ProductLedgerServiceTests</c>, <c>SalesInvoiceCostOfSalesTests</c>); these are the ones a person
/// can check on paper, and each is also held to <see cref="ProductLedgerInvariants"/>, which states the
/// same rules without the service's formula in them.
/// </summary>
public class ProductLedgerAcceptanceTests
{
    private const int User = ProductLedgerRig.User;

    private static async Task<List<ProductLedgerEntry>> LedgerOf(ProductLedgerRig rig, Guid variant)
    {
        var entries = await rig.EntriesAsync(variant);
        ProductLedgerInvariants.Violations(entries).Should().BeEmpty();
        return entries;
    }

    private static ProductLedgerPosting Entry(Guid variant, string type, string direction, decimal qty, decimal? cost, string number = "DOC-1") =>
        new(variant, type, direction, qty, cost, "Doc", Guid.NewGuid(), number, User);

    // ── WAC across several GRNs at different prices ──────────────────────────

    [Fact]
    public async Task Three_receipts_at_three_prices_give_a_weighted_average_after_each()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        var first  = await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));
        var second = await rig.Service().AppendEntryAsync(Buy(variant, 50m, 16m));
        var third  = await rig.Service().AppendEntryAsync(Buy(variant, 30m, 20m));

        // new_value = prev_value + qty × cost; new_WAC = new_value / new_qty.
        (first.RunningQty,  first.RunningValue,  first.WeightedAverageCost).Should().Be((100m, 1000m, 10m));
        (second.RunningQty, second.RunningValue, second.WeightedAverageCost).Should().Be((150m, 1800m, 12m));
        (third.RunningQty,  third.RunningValue,  third.WeightedAverageCost).Should().Be((180m, 2400m, 13.3333m));

        await LedgerOf(rig, variant);
    }

    [Fact]
    public async Task Interleaved_sales_change_the_quantity_never_the_average_and_the_next_receipt_blends_into_what_is_left()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        var service = rig.Service();

        await service.AppendEntryAsync(Buy(variant, 100m, 10m));
        await service.AppendEntryAsync(Sell(variant, 20m));
        await service.AppendEntryAsync(Buy(variant, 100m, 20m));
        await service.AppendEntryAsync(Sell(variant, 30m));
        await service.AppendEntryAsync(Buy(variant, 50m, 10m));
        await service.AppendEntryAsync(Sell(variant, 200m));

        var entries = await LedgerOf(rig, variant);

        entries.Select(e => (e.EntryType, e.Quantity, e.UnitCost, e.TotalCost, e.RunningQty, e.RunningValue)).Should().Equal(
            ("PURCHASE", 100m, 10m,      1000m,   100m, 1000m),
            ("SALE",      20m, 10m,       200m,    80m,  800m),          // 80 left, still at 10
            ("PURCHASE", 100m, 20m,      2000m,   180m, 2800m),          // 2800 / 180 = 15.5556
            ("SALE",      30m, 15.5556m,  466.67m, 150m, 2333.33m),      // 30 × 2800 / 180
            ("PURCHASE",  50m, 10m,       500m,    200m, 2833.33m),      // 2833.33 / 200 = 14.1667
            ("SALE",     200m, 14.1667m, 2833.33m, 0m,   0m));           // all of it: all of the value
    }

    [Theory]
    [InlineData(10, 100, 20, 50, 13.3333)]   // dearer stock pulls the average up: 2000 over 150 …
    [InlineData(20, 100, 10, 100, 15)]       // … cheaper stock pulls it down …
    [InlineData(10, 100, 10, 500, 10)]       // … stock at the same price leaves it where it was …
    [InlineData(10, 1, 1000, 1, 505)]        // … and by exactly as much as its share of the stock.
    public async Task New_stock_moves_the_average_toward_its_own_price_in_proportion_to_its_share(
        decimal firstCost, decimal firstQty, decimal secondCost, decimal secondQty, decimal wanted)
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        await rig.Service().AppendEntryAsync(Buy(variant, firstQty, firstCost));
        var second = await rig.Service().AppendEntryAsync(Buy(variant, secondQty, secondCost));

        second.WeightedAverageCost.Should().Be(wanted);
        await LedgerOf(rig, variant);
    }

    [Fact]
    public async Task Receipts_in_separate_requests_at_separate_prices_blend_the_same_as_if_they_had_arrived_together()
    {
        var together = new ProductLedgerRig();
        var apart = new ProductLedgerRig();
        var (a, _) = together.Variants.New();
        var (b, _) = apart.Variants.New();

        var svc = together.Service();
        foreach (var (qty, cost) in new[] { (12m, 9.5m), (7.25m, 11m), (30m, 8.1234m) })
            await svc.AppendEntryAsync(Buy(a, qty, cost));
        foreach (var (qty, cost) in new[] { (12m, 9.5m), (7.25m, 11m), (30m, 8.1234m) })
            await apart.Service().AppendEntryAsync(Buy(b, qty, cost));

        // 12 × 9.5 = 114.00, 7.25 × 11 = 79.75, 30 × 8.1234 = 243.702 → 243.70: 437.45 over 49.25.
        (await LedgerOf(together, a)).Last().Should().Match<ProductLedgerEntry>(e => e.RunningQty == 49.25m && e.RunningValue == 437.45m);
        (await LedgerOf(apart, b)).Last().Should().Match<ProductLedgerEntry>(e => e.RunningQty == 49.25m && e.RunningValue == 437.45m);
    }

    // ── COGS on a sale ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_sale_costs_the_average_not_the_last_price_paid_and_not_the_price_it_was_sold_at()
    {
        var world = new ReceivablesWorld();
        var desk = world.For(Guid.NewGuid());
        var customer = desk.NewCustomer();
        var order = await desk.PlaceOrderAsync(customer, new ReceivablesDesk.OrderLine(Qty: 30m, Price: 50m, Stocked: false));
        var variant = order.VariantUuids[0];

        await desk.StockAsync(variant, 100m, 10m);
        await desk.StockAsync(variant, 50m, 16m);                                       // the last price paid is 16; the average is 12

        var invoiced = await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 30m)));

        invoiced.Amount.Should().Be(1500m, "sold at 50");
        var sale = (await desk.ProductLedgerAsync(variant)).Last();
        (sale.UnitCost, sale.TotalCost).Should().Be((12m, 360m));
    }

    [Fact]
    public async Task What_a_unit_cost_does_not_depend_on_what_it_sold_for()
    {
        // The same stock sold at a loss, at cost and at a great profit, with and without discount and tax.
        var world = new ReceivablesWorld();
        var desk = world.For(Guid.NewGuid());
        var customer = desk.NewCustomer();
        var variant = Guid.NewGuid();
        await desk.StockAsync(variant, 100m, 10m);

        foreach (var (price, discount, tax) in new[] { (1m, 0m, 0m), (10m, 0m, 17m), (500m, 25m, 0m), (12345.67m, 10m, 17m) })
        {
            var order = await desk.PlaceOrderAsync(customer, new ReceivablesDesk.OrderLine(Qty: 5m, Price: price, Discount: discount, Tax: tax, Stocked: false, Variant: variant));
            await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 5m)));
        }

        var sales = (await desk.ProductLedgerAsync(variant)).Where(e => e.EntryType == "SALE").ToList();
        sales.Should().HaveCount(4);
        sales.Should().OnlyContain(s => s.UnitCost == 10m && s.TotalCost == 50m);
    }

    [Fact]
    public async Task Selling_a_variant_a_unit_at_a_time_costs_as_much_in_all_as_selling_it_at_once()
    {
        var oneAtATime = new ProductLedgerRig();
        var atOnce = new ProductLedgerRig();
        var (a, _) = oneAtATime.Variants.New();
        var (b, _) = atOnce.Variants.New();

        // 7 units worth 100.00: an average of 14.2857…, so every single unit costs a rounded 14.29 — but
        // the seven together cost what the seven were worth, not seven roundings of it.
        await oneAtATime.Service().AppendEntryAsync(Buy(a, 7m, 14.2857m));
        await atOnce.Service().AppendEntryAsync(Buy(b, 7m, 14.2857m));
        for (var i = 0; i < 7; i++) await oneAtATime.Service().AppendEntryAsync(Sell(a, 1m));
        await atOnce.Service().AppendEntryAsync(Sell(b, 7m));

        var single = (await LedgerOf(oneAtATime, a)).Where(e => e.EntryType == "SALE").Sum(e => e.TotalCost);
        var whole  = (await LedgerOf(atOnce, b)).Single(e => e.EntryType == "SALE").TotalCost;

        single.Should().Be(whole, "the value that leaves is the value that was there, however many sales it leaves in");
        whole.Should().Be(100m);      // 7 × 14.2857 = 99.9999, rounded to the cent
    }

    // ── running_qty / running_value ──────────────────────────────────────────

    [Fact]
    public async Task A_month_of_every_kind_of_entry_keeps_the_running_totals_right_at_every_step()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        var svc = rig.Service();

        await svc.AppendEntryAsync(Entry(variant, "PURCHASE",   "IN",  200m, 5m));
        await svc.AppendEntryAsync(Entry(variant, "PURCHASE",   "IN",  100m, 8m));
        await svc.AppendEntryAsync(Entry(variant, "SALE",       "OUT", 60m, null));
        await svc.AppendEntryAsync(Entry(variant, "RETURN_IN",  "IN",  10m, 6m));
        await svc.AppendEntryAsync(Entry(variant, "WRITE_OFF",  "OUT", 5m, null));
        await svc.AppendEntryAsync(Entry(variant, "ADJUSTMENT", "IN",  5m, 0m));
        await svc.AppendEntryAsync(Entry(variant, "RETURN_OUT", "OUT", 50m, null));
        await svc.AppendEntryAsync(Entry(variant, "ADJUSTMENT", "OUT", 0.5m, null));
        await svc.AppendEntryAsync(Entry(variant, "PURCHASE",   "IN",  0.5m, 12.3456m));

        var entries = await LedgerOf(rig, variant);

        entries.Select(e => (e.SequenceNo, e.EntryType, e.Direction, e.Quantity, e.UnitCost, e.TotalCost, e.RunningQty, e.RunningValue)).Should().Equal(
            (1, "PURCHASE",   "IN",  200m, 5m,       1000m,  200m,   1000m),
            (2, "PURCHASE",   "IN",  100m, 8m,       800m,   300m,   1800m),       // average 6
            (3, "SALE",       "OUT", 60m,  6m,       360m,   240m,   1440m),
            (4, "RETURN_IN",  "IN",  10m,  6m,       60m,    250m,   1500m),       // taken back at the cost it left at
            (5, "WRITE_OFF",  "OUT", 5m,   6m,       30m,    245m,   1470m),
            (6, "ADJUSTMENT", "IN",  5m,   0m,       0m,     250m,   1470m),       // found stock, no cost: average 5.88
            (7, "RETURN_OUT", "OUT", 50m,  5.88m,    294m,   200m,   1176m),
            (8, "ADJUSTMENT", "OUT", 0.5m, 5.88m,    2.94m,  199.5m, 1173.06m),
            (9, "PURCHASE",   "IN",  0.5m, 12.3456m, 6.17m,  200m,   1179.23m));   // 0.5 × 12.3456 = 6.1728

        // And the same figures as sums, worked separately: everything in less everything out.
        entries.Sum(e => e.Direction == "IN" ? e.Quantity : -e.Quantity).Should().Be(entries[^1].RunningQty);
        entries.Sum(e => e.Direction == "IN" ? e.TotalCost : -e.TotalCost).Should().Be(entries[^1].RunningValue);
    }

    [Fact]
    public async Task What_each_call_returns_is_what_was_stored()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        var svc = rig.Service();

        var posted = new List<ProductLedgerPosted>
        {
            await svc.AppendEntryAsync(Buy(variant, 3.3333m, 10.0025m)),
            await svc.AppendEntryAsync(Sell(variant, 1.1111m)),
            await svc.AppendEntryAsync(Buy(variant, 0.0001m, 9999.9999m))
        };

        var stored = await LedgerOf(rig, variant);

        posted.Select(p => (p.SequenceNo, p.Quantity, p.UnitCost, p.TotalCost, p.RunningQty, p.RunningValue))
              .Should().Equal(stored.Select(e => (e.SequenceNo, e.Quantity, e.UnitCost, e.TotalCost, e.RunningQty, e.RunningValue)));
    }

    // ── The zero-stock edge ──────────────────────────────────────────────────

    [Fact]
    public async Task Selling_the_last_unit_leaves_no_stock_no_value_and_no_average()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 3m, 3.3333m));                // 10.00 — an average that does not divide evenly

        for (var i = 0; i < 2; i++) await rig.Service().AppendEntryAsync(Sell(variant, 1m));
        var last = await rig.Service().AppendEntryAsync(Sell(variant, 1m));

        (last.RunningQty, last.RunningValue, last.WeightedAverageCost).Should().Be((0m, 0m, 0m));
        var sales = (await LedgerOf(rig, variant)).Where(e => e.EntryType == "SALE").Sum(e => e.TotalCost);
        sales.Should().Be(10m, "three units that came in worth 10.00 leave worth exactly 10.00, not 9.99 or 10.01");
    }

    [Fact]
    public async Task Nothing_can_be_sold_from_a_ledger_that_never_held_it()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        var act = () => rig.Service().AppendEntryAsync(Sell(variant, 1m));

        await act.Should().ThrowAsync<ConflictException>();
        (await rig.EntriesAsync(variant)).Should().BeEmpty();
    }

    [Fact]
    public async Task Nothing_can_be_sold_from_a_ledger_that_has_been_sold_down_to_nothing()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 5m, 10m));
        await rig.Service().AppendEntryAsync(Sell(variant, 5m));

        var act = () => rig.Service().AppendEntryAsync(Sell(variant, 0.0001m));

        await act.Should().ThrowAsync<ConflictException>();
        (await LedgerOf(rig, variant)).Should().HaveCount(2, "the refused sale wrote nothing");
    }

    [Fact]
    public async Task One_hundredth_of_a_cent_more_than_is_held_is_refused_and_exactly_what_is_held_is_not()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 4m));

        await FluentActions.Awaiting(() => rig.Service().AppendEntryAsync(Sell(variant, 10.0001m))).Should().ThrowAsync<ConflictException>();
        var all = await rig.Service().AppendEntryAsync(Sell(variant, 10m));

        all.RunningQty.Should().Be(0m);
    }

    [Fact]
    public async Task The_smallest_quantity_the_column_holds_can_be_bought_and_sold()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        var bought = await rig.Service().AppendEntryAsync(Buy(variant, 0.0001m, 10_000m));     // a ten-thousandth of a unit, at ten thousand
        var sold   = await rig.Service().AppendEntryAsync(Sell(variant, 0.0001m));

        bought.TotalCost.Should().Be(1m);
        (sold.TotalCost, sold.RunningQty, sold.RunningValue).Should().Be((1m, 0m, 0m));
        await LedgerOf(rig, variant);
    }

    [Fact]
    public async Task Stock_that_came_in_free_costs_nothing_to_sell_and_the_next_receipt_averages_against_its_share_only()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        var svc = rig.Service();

        var free = await svc.AppendEntryAsync(Buy(variant, 10m, 0m));
        var sold = await svc.AppendEntryAsync(Sell(variant, 4m));
        var paid = await svc.AppendEntryAsync(Buy(variant, 6m, 5m));
        var rest = await svc.AppendEntryAsync(Sell(variant, 12m));

        (free.RunningQty, free.RunningValue, free.WeightedAverageCost).Should().Be((10m, 0m, 0m));
        (sold.UnitCost, sold.TotalCost, sold.RunningQty).Should().Be((0m, 0m, 6m));
        (paid.RunningQty, paid.RunningValue, paid.WeightedAverageCost).Should().Be((12m, 30m, 2.5m), "6 free and 6 at 5 average 2.50");
        (rest.TotalCost, rest.RunningQty, rest.RunningValue).Should().Be((30m, 0m, 0m));
        await LedgerOf(rig, variant);
    }

    [Fact]
    public async Task After_being_sold_out_a_new_receipt_starts_the_average_afresh_with_no_memory_of_the_old_price()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 3m));
        await rig.Service().AppendEntryAsync(Sell(variant, 10m));

        var restart = await rig.Service().AppendEntryAsync(Buy(variant, 5m, 7m));

        (restart.RunningQty, restart.RunningValue, restart.WeightedAverageCost).Should().Be((5m, 35m, 7m), "not a blend of 7 and the 3 it once was");
    }

    [Fact]
    public async Task A_customers_return_into_an_empty_ledger_puts_the_stock_back_at_the_cost_it_left_at()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 4m));
        var sale = await rig.Service().AppendEntryAsync(Sell(variant, 10m));

        var back = await rig.Service().AppendEntryAsync(Entry(variant, "RETURN_IN", "IN", 3m, sale.UnitCost));

        (back.RunningQty, back.RunningValue, back.WeightedAverageCost).Should().Be((3m, 12m, 4m));
        await LedgerOf(rig, variant);
    }

    // ── TC-10 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TC10_a_grn_of_100_at_10_and_a_sale_of_30_at_15_gives_a_wac_of_10_a_cogs_of_300_and_revenue_of_450()
    {
        var world = new ReceivablesWorld();
        var desk = world.For(Guid.NewGuid());
        var customer = desk.NewCustomer();
        var order = await desk.PlaceOrderAsync(customer, new ReceivablesDesk.OrderLine(Qty: 30m, Price: 15m, Stocked: false));
        var variant = order.VariantUuids[0];

        await desk.StockAsync(variant, 100m, 10m);
        var invoiced = await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 30m)));

        var entries = await desk.ProductLedgerAsync(variant);
        ProductLedgerInvariants.Violations(entries).Should().BeEmpty();

        entries.Select(e => (e.EntryType, e.Quantity, e.UnitCost, e.TotalCost, e.RunningQty, e.RunningValue)).Should().Equal(
            ("PURCHASE", 100m, 10m, 1000m, 100m, 1000m),
            ("SALE",      30m, 10m,  300m,  70m,  700m));
        (entries[^1].RunningValue / entries[^1].RunningQty).Should().Be(10m, "WAC");
        invoiced.Amount.Should().Be(450m, "revenue");
    }
}
