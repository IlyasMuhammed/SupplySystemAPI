using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

using static SMS.Modules.Finance.Tests.ProductLedgerRig;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P8-02 §11.3 — the weighted-average cost arithmetic on its own, then the service that applies it:
/// what an IN adds, what an OUT costs, what is refused, and how two writers for one variant stay chained.
/// </summary>
public class WeightedAverageCostingTests
{
    [Fact]
    public void An_incoming_entry_adds_quantity_times_cost_to_the_value()
    {
        var step = WeightedAverageCosting.In(0m, 0m, 100m, 10m);

        step.Should().Be(new CostingStep(UnitCost: 10m, TotalCost: 1000m, RunningQty: 100m, RunningValue: 1000m));
    }

    [Fact]
    public void The_weighted_average_is_the_value_over_the_quantity()
    {
        var afterFirst  = WeightedAverageCosting.In(0m, 0m, 100m, 10m);
        var afterSecond = WeightedAverageCosting.In(afterFirst.RunningQty, afterFirst.RunningValue, 50m, 16m);

        afterSecond.RunningQty.Should().Be(150m);
        afterSecond.RunningValue.Should().Be(1800m);
        WeightedAverageCosting.Wac(afterSecond.RunningQty, afterSecond.RunningValue).Should().Be(12m);
    }

    [Fact]
    public void An_outgoing_entry_leaves_at_the_current_weighted_average_cost()
    {
        var step = WeightedAverageCosting.Out(previousQty: 150m, previousValue: 1800m, qty: 30m);

        step.Should().Be(new CostingStep(UnitCost: 12m, TotalCost: 360m, RunningQty: 120m, RunningValue: 1440m));
    }

    [Fact]
    public void A_total_is_rounded_to_the_cent_half_away_from_zero_and_the_unit_cost_is_kept_to_four_places()
    {
        WeightedAverageCosting.In(0m, 0m, 3.3333m, 10.0025m).TotalCost.Should().Be(33.34m);     // 33.34067…
        WeightedAverageCosting.In(0m, 0m, 1m, 0.005m).TotalCost.Should().Be(0.01m);              // half rounds up
        WeightedAverageCosting.Wac(3m, 10m).Should().Be(3.3333m);
        WeightedAverageCosting.Wac(0m, 0m).Should().Be(0m, "nothing on hand has no cost");
    }

    [Fact]
    public void Selling_down_a_fractional_average_keeps_the_value_to_the_cent_and_the_last_unit_takes_the_remainder()
    {
        // 3 units worth 10.00 — 3.3333 each. Sold one at a time: 3.33, 3.33, and then whatever is left.
        var qty = 3m;
        var value = 10m;
        var costs = new List<decimal>();

        while (qty > 0)
        {
            var step = WeightedAverageCosting.Out(qty, value, 1m);
            costs.Add(step.TotalCost);
            (qty, value) = (step.RunningQty, step.RunningValue);
        }

        costs.Should().Equal(3.33m, 3.34m, 3.33m);
        value.Should().Be(0m, "a variant sold down to nothing is worth exactly nothing");
        costs.Sum().Should().Be(10m, "what left is exactly what came in");
    }

    [Fact]
    public void An_outgoing_entry_is_costed_from_the_exact_average_not_from_the_four_place_figure_it_is_shown_at()
    {
        // 300 units worth 100.00 average 0.33333…, shown as 0.3333. 299 of them leave at 99.6667, which is
        // 99.67 — not 299 × 0.3333 = 99.66, a cent short on every such sale.
        var step = WeightedAverageCosting.Out(previousQty: 300m, previousValue: 100m, qty: 299m);

        step.UnitCost.Should().Be(0.3333m);
        step.TotalCost.Should().Be(99.67m);
        step.RunningValue.Should().Be(0.33m);
    }

    [Fact]
    public void Taking_out_the_largest_position_the_columns_can_hold_does_not_overflow()
    {
        const decimal qty = 99_999_999_999_999.9999m;      // decimal(18,4)
        const decimal value = 9_999_999_999_999_999.99m;   // decimal(18,2)

        var step = WeightedAverageCosting.Out(qty, value, qty);

        step.TotalCost.Should().Be(value);
        (step.RunningQty, step.RunningValue).Should().Be((0m, 0m));
    }

    [Fact]
    public void Taking_everything_out_takes_the_whole_value_whatever_the_rounding()
    {
        var step = WeightedAverageCosting.Out(previousQty: 7m, previousValue: 100m, qty: 7m);

        step.TotalCost.Should().Be(100m);
        step.RunningQty.Should().Be(0m);
        step.RunningValue.Should().Be(0m);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Over_any_run_of_entries_the_value_is_never_negative_and_always_the_sum_of_what_moved(int seed)
    {
        var rng = new Random(seed);
        decimal qty = 0m, value = 0m, movedIn = 0m, movedOut = 0m;

        for (var i = 0; i < 200; i++)
        {
            var goingOut = qty > 0m && rng.Next(100) < 45;

            if (goingOut)
            {
                // Sometimes everything, else a random part, to four places.
                var take = rng.Next(4) == 0 ? qty : Math.Max(0.0001m, Math.Round(qty * (rng.Next(1, 100) / 100m), 4));
                take = Math.Min(take, qty);

                var step = WeightedAverageCosting.Out(qty, value, take);
                step.UnitCost.Should().Be(WeightedAverageCosting.Wac(qty, value), "an OUT leaves at the average of the moment");
                step.RunningQty.Should().Be(qty - take);
                step.RunningValue.Should().Be(value - step.TotalCost);
                step.TotalCost.Should().BeInRange(0m, value);
                movedOut += step.TotalCost;
                (qty, value) = (step.RunningQty, step.RunningValue);
            }
            else
            {
                var inQty  = Math.Round(rng.Next(1, 100_000) / 100m, 4);
                var inCost = Math.Round(rng.Next(0, 500_000) / 1000m, 4);
                var step   = WeightedAverageCosting.In(qty, value, inQty, inCost);
                movedIn += step.TotalCost;
                (qty, value) = (step.RunningQty, step.RunningValue);
            }

            value.Should().BeGreaterThanOrEqualTo(0m);
            if (qty == 0m) value.Should().Be(0m, "no stock, no value");
            value.Should().Be(movedIn - movedOut, "the value is only ever what the entries moved");
        }
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 20).Select(s => new object[] { s });
}

public class ProductLedgerServiceTests
{
    // ── The weighted-average cost, entry by entry ────────────────────────────

    [Fact]
    public async Task A_purchase_of_100_at_10_and_a_sale_of_30_costs_the_sale_at_300_and_leaves_a_wac_of_10()
    {
        // TC-10 (the cost side): GRN 100 @ 10, sell 30.
        var rig = new ProductLedgerRig();
        var (variant, product) = rig.Variants.New();

        var bought = await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));
        var sold   = await rig.Service().AppendEntryAsync(Sell(variant, 30m));

        bought.Should().Match<ProductLedgerPosted>(p =>
            p.SequenceNo == 1 && p.Direction == "IN" && p.Quantity == 100m && p.UnitCost == 10m && p.TotalCost == 1000m
            && p.RunningQty == 100m && p.RunningValue == 1000m && p.WeightedAverageCost == 10m
            && p.VariantUuid == variant && p.ProductUuid == product);

        sold.Should().Match<ProductLedgerPosted>(p =>
            p.SequenceNo == 2 && p.Direction == "OUT" && p.Quantity == 30m && p.UnitCost == 10m && p.TotalCost == 300m
            && p.RunningQty == 70m && p.RunningValue == 700m && p.WeightedAverageCost == 10m);

        var stored = await rig.EntriesAsync(variant);
        stored.Select(e => (e.EntryType, e.Direction, e.Quantity, e.UnitCost, e.TotalCost, e.RunningQty, e.RunningValue)).Should().Equal(
            ("PURCHASE", "IN",  100m, 10m, 1000m, 100m, 1000m),
            ("SALE",     "OUT", 30m,  10m, 300m,  70m,  700m));
    }

    [Fact]
    public async Task Purchases_at_different_prices_blend_and_sales_are_costed_at_the_blend()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));
        var second = await rig.Service().AppendEntryAsync(Buy(variant, 50m, 16m));
        second.RunningQty.Should().Be(150m);
        second.RunningValue.Should().Be(1800m);
        second.WeightedAverageCost.Should().Be(12m, "new_WAC = new_value / new_qty");

        var sale = await rig.Service().AppendEntryAsync(Sell(variant, 30m));
        sale.UnitCost.Should().Be(12m, "COGS = the current WAC");
        sale.TotalCost.Should().Be(360m);
        (sale.RunningQty, sale.RunningValue, sale.WeightedAverageCost).Should().Be((120m, 1440m, 12m), "selling does not move the average");

        var third = await rig.Service().AppendEntryAsync(Buy(variant, 30m, 20m));
        (third.RunningQty, third.RunningValue).Should().Be((150m, 2040m));
        third.WeightedAverageCost.Should().Be(13.6m);
    }

    [Fact]
    public async Task Goods_that_came_in_free_pull_the_average_down()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));

        var freebie = await rig.Service().AppendEntryAsync(Buy(variant, 100m, 0m));

        freebie.TotalCost.Should().Be(0m);
        (freebie.RunningQty, freebie.RunningValue, freebie.WeightedAverageCost).Should().Be((200m, 1000m, 5m));
    }

    [Fact]
    public async Task Selling_everything_leaves_no_value_and_the_next_purchase_starts_a_fresh_average()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 3m, 3.3333m));      // 10.00, a fractional average
        var sellOut = await rig.Service().AppendEntryAsync(Sell(variant, 3m));

        (sellOut.RunningQty, sellOut.RunningValue, sellOut.WeightedAverageCost).Should().Be((0m, 0m, 0m));
        sellOut.TotalCost.Should().Be(10m, "3 × 3.3333 booked in, so all of it goes out");

        var fresh = await rig.Service().AppendEntryAsync(Buy(variant, 10m, 7m));
        (fresh.RunningQty, fresh.RunningValue, fresh.WeightedAverageCost).Should().Be((10m, 70m, 7m));
    }

    [Fact]
    public async Task Every_entrys_running_value_is_the_one_before_it_plus_or_minus_its_own_total()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        await rig.Service().AppendEntryAsync(Buy(variant, 7.5m, 3.1234m));
        await rig.Service().AppendEntryAsync(Sell(variant, 2.25m));
        await rig.Service().AppendEntryAsync(Buy(variant, 11m, 4.9999m));
        await rig.Service().AppendEntryAsync(Sell(variant, 0.0001m));
        await rig.Service().AppendEntryAsync(Sell(variant, 16.2499m));     // 7.5 − 2.25 + 11 − 0.0001 = all of it

        var entries = await rig.EntriesAsync(variant);
        entries.Should().HaveCount(5);

        decimal qty = 0m, value = 0m;
        foreach (var e in entries)
        {
            qty   += e.Direction == "IN" ? e.Quantity : -e.Quantity;
            value += e.Direction == "IN" ? e.TotalCost : -e.TotalCost;
            e.RunningQty.Should().Be(qty);
            e.RunningValue.Should().Be(value);
        }
        entries[^1].RunningQty.Should().Be(0m);
        entries[^1].RunningValue.Should().Be(0m);
    }

    // ── Which entries there are, and which way they go ───────────────────────

    [Fact]
    public async Task Returns_adjustments_and_write_offs_are_ledger_entries_too()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));

        var customerReturn = await rig.Service().AppendEntryAsync(new ProductLedgerPosting(
            variant, "RETURN_IN", "IN", 5m, 10m, "SalesReturn", Guid.NewGuid(), "SRET-1", User));
        var supplierReturn = await rig.Service().AppendEntryAsync(new ProductLedgerPosting(
            variant, "RETURN_OUT", "OUT", 10m, null, "SupplierReturn", Guid.NewGuid(), "SRO-1", User));
        var found = await rig.Service().AppendEntryAsync(new ProductLedgerPosting(
            variant, "ADJUSTMENT", "IN", 2m, 10m, "StockAdjustment", Guid.NewGuid(), "ADJ-1", User));
        var lost = await rig.Service().AppendEntryAsync(new ProductLedgerPosting(
            variant, "ADJUSTMENT", "OUT", 1m, null, "StockAdjustment", Guid.NewGuid(), "ADJ-2", User));
        var writeOff = await rig.Service().AppendEntryAsync(new ProductLedgerPosting(
            variant, "WRITE_OFF", "OUT", 4m, null, "WriteOff", Guid.NewGuid(), "WO-1", User));

        customerReturn.RunningQty.Should().Be(105m);
        supplierReturn.TotalCost.Should().Be(100m, "a return to the supplier goes out at the average");
        found.RunningQty.Should().Be(97m);
        lost.RunningQty.Should().Be(96m);
        (writeOff.RunningQty, writeOff.RunningValue).Should().Be((92m, 920m));

        (await rig.EntriesAsync(variant)).Select(e => e.EntryType).Should().Equal(
            "PURCHASE", "RETURN_IN", "RETURN_OUT", "ADJUSTMENT", "ADJUSTMENT", "WRITE_OFF");
    }

    [Theory]
    [InlineData("PURCHASE", "OUT")]
    [InlineData("SALE", "IN")]
    [InlineData("RETURN_IN", "OUT")]
    [InlineData("RETURN_OUT", "IN")]
    [InlineData("WRITE_OFF", "IN")]
    public async Task An_entry_type_cannot_go_the_wrong_way(string type, string direction)
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 1m));

        var act = () => rig.Service().AppendEntryAsync(new ProductLedgerPosting(
            variant, type, direction, 1m, direction == "IN" ? 1m : null, "X", Guid.NewGuid(), "X-1", User));

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain($"always {(direction == "IN" ? "OUT" : "IN")}");
        (await rig.EntriesAsync(variant)).Should().ContainSingle();
    }

    // ── What is refused ──────────────────────────────────────────────────────

    [Fact]
    public async Task Taking_out_more_than_the_ledger_holds_is_refused_and_nothing_is_written()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 5m));

        var act = () => rig.Service().AppendEntryAsync(Sell(variant, 10.0001m));

        (await act.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Contain("holds 10");
        (await rig.EntriesAsync(variant)).Should().ContainSingle("the refused sale left no entry behind");
    }

    [Fact]
    public async Task Selling_what_the_ledger_never_held_is_refused_without_asking_inventory_anything()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        var act = () => rig.Service().AppendEntryAsync(Sell(variant, 1m));

        await act.Should().ThrowAsync<ConflictException>();
        rig.Variants.Calls.Should().Be(0);
        (await rig.EntriesAsync(variant)).Should().BeEmpty();
    }

    [Fact]
    public async Task Exactly_what_the_ledger_holds_can_be_taken_out()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 5m));

        var posted = await rig.Service().AppendEntryAsync(Sell(variant, 10m));

        posted.RunningQty.Should().Be(0m);
    }

    public static IEnumerable<object[]> Malformed()
    {
        var v = Guid.NewGuid();
        var r = Guid.NewGuid();
        ProductLedgerPosting Buying(
            Guid? variant = null, string type = "PURCHASE", string direction = "IN", decimal qty = 1m, decimal? cost = 1m,
            string refType = "GRN", Guid? refId = null, string refNo = "GRN-1", Guid? product = null, Guid? partner = null,
            string? narration = null) =>
            new(variant ?? v, type, direction, qty, cost, refType, refId ?? r, refNo, User, product, partner, narration);

        yield return new object[] { "no variant",          Buying(variant: Guid.Empty) };
        yield return new object[] { "unknown type",        Buying(type: "TRANSFER") };
        yield return new object[] { "lower-case type",     Buying(type: "purchase") };
        yield return new object[] { "unknown direction",   Buying(direction: "SIDEWAYS") };
        yield return new object[] { "zero quantity",       Buying(qty: 0m) };
        yield return new object[] { "negative quantity",   Buying(qty: -1m) };
        yield return new object[] { "five-place quantity", Buying(qty: 1.00001m) };
        yield return new object[] { "in without a cost",   Buying(cost: null) };
        yield return new object[] { "negative cost",       Buying(cost: -0.01m) };
        yield return new object[] { "five-place cost",     Buying(cost: 1.00001m) };
        yield return new object[] { "out with a cost",     Buying(type: "SALE", direction: "OUT", cost: 5m) };
        yield return new object[] { "no document id",      Buying(refId: Guid.Empty) };
        yield return new object[] { "no reference type",   Buying(refType: " ") };
        yield return new object[] { "long reference type", Buying(refType: new string('x', 31)) };
        yield return new object[] { "no reference number", Buying(refNo: "") };
        yield return new object[] { "long reference no",   Buying(refNo: new string('x', 51)) };
        yield return new object[] { "long narration",      Buying(narration: new string('x', 501)) };
        yield return new object[] { "empty partner",       Buying(partner: Guid.Empty) };
        yield return new object[] { "empty product",       Buying(product: Guid.Empty) };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task A_malformed_posting_is_refused_before_anything_is_looked_up_or_written(string why, ProductLedgerPosting posting)
    {
        var rig = new ProductLedgerRig();
        rig.Variants.Products[posting.VariantUuid == Guid.Empty ? Guid.NewGuid() : posting.VariantUuid] = Guid.NewGuid();
        _ = why;

        var act = () => rig.Service().AppendEntryAsync(posting);

        await act.Should().ThrowAsync<ArgumentException>();
        rig.Variants.Calls.Should().Be(0);
        await using var audit = Receivables.Auditor(rig.DbName);
        (await audit.ProductLedgerEntries.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_boundary_posting_is_accepted_four_places_two_hundred_narration_and_the_longest_references()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        var posted = await rig.Service().AppendEntryAsync(new ProductLedgerPosting(
            variant, "PURCHASE", "IN", 0.0001m, 0.0001m, new string('t', 30), Guid.NewGuid(), new string('n', 50), User,
            Narration: new string('x', 500)));

        posted.TotalCost.Should().Be(0m, "a hundredth of a cent rounds to nothing");
        posted.RunningQty.Should().Be(0.0001m);
    }

    // ── The product ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_product_is_looked_up_in_inventory_once_and_after_that_taken_from_the_ledger()
    {
        var rig = new ProductLedgerRig();
        var (variant, product) = rig.Variants.New();

        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 1m));
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 1m));
        await rig.Service().AppendEntryAsync(Sell(variant, 5m));

        rig.Variants.Calls.Should().Be(1);
        (await rig.EntriesAsync(variant)).Should().OnlyContain(e => e.ProductUuid == product);
    }

    [Fact]
    public async Task A_caller_that_knows_the_product_spares_inventory_the_question()
    {
        var rig = new ProductLedgerRig();
        var variant = Guid.NewGuid();          // Inventory does not know it — and is not asked
        var product = Guid.NewGuid();

        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 1m) with { ProductUuid = product });

        rig.Variants.Calls.Should().Be(0);
        (await rig.EntriesAsync(variant)).Single().ProductUuid.Should().Be(product);
    }

    [Fact]
    public async Task A_variant_inventory_does_not_know_and_the_ledger_never_saw_is_not_found()
    {
        var rig = new ProductLedgerRig();

        var act = () => rig.Service().AppendEntryAsync(Buy(Guid.NewGuid(), 10m, 1m));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_variant_already_on_the_ledger_stays_postable_after_inventory_stops_knowing_it()
    {
        var rig = new ProductLedgerRig();
        var (variant, product) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 1m));

        rig.Variants.Products.Remove(variant);     // deactivated since

        var posted = await rig.Service().AppendEntryAsync(Sell(variant, 4m));

        posted.ProductUuid.Should().Be(product);
    }

    [Fact]
    public async Task A_product_that_contradicts_the_ledgers_history_is_refused()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 1m));

        var act = () => rig.Service().AppendEntryAsync(Buy(variant, 5m, 1m) with { ProductUuid = Guid.NewGuid() });

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("not");
        (await rig.EntriesAsync(variant)).Should().ContainSingle();
    }

    // ── What is stored ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_entry_carries_its_document_partner_narration_dates_and_author()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        var supplier = Guid.NewGuid();
        var grn      = Guid.NewGuid();

        await rig.Service().AppendEntryAsync(new ProductLedgerPosting(
            variant, "PURCHASE", "IN", 5m, 2m, "GRN", grn, "GRN-20260920-0007", 7,
            PartnerId: supplier, Narration: "Received against PO-9", EntryDate: new DateTime(2026, 9, 1)));
        await rig.Service().AppendEntryAsync(Sell(variant, 1m));

        var entries = await rig.EntriesAsync(variant);

        var purchase = entries[0];
        purchase.ReferenceType.Should().Be("GRN");
        purchase.ReferenceId.Should().Be(grn);
        purchase.ReferenceNumber.Should().Be("GRN-20260920-0007");
        purchase.PartnerId.Should().Be(supplier);
        purchase.Narration.Should().Be("Received against PO-9");
        purchase.EntryDate.Should().Be(new DateTime(2026, 9, 1), "the business date the caller gave");
        purchase.CreatedBy.Should().Be(7);
        purchase.CreatedDate.Should().Be(rig.Clock.Value);
        purchase.OrganizationId.Should().Be(rig.Org, "stamped on write");

        entries[1].EntryDate.Should().Be(rig.Clock.Value, "now, when the caller gave none");
        entries[1].PartnerId.Should().BeNull();
        entries.Select(e => e.UUID).Should().OnlyHaveUniqueItems().And.NotContain(Guid.Empty);
    }

    [Fact]
    public async Task Entries_are_numbered_per_variant_and_each_variant_keeps_its_own_totals()
    {
        var rig = new ProductLedgerRig();
        var (first, product) = rig.Variants.New();
        var (second, _)      = rig.Variants.New(product);

        await rig.Service().AppendEntryAsync(Buy(first, 10m, 4m));
        await rig.Service().AppendEntryAsync(Buy(second, 5m, 8m));
        await rig.Service().AppendEntryAsync(Buy(first, 10m, 6m));
        await rig.Service().AppendEntryAsync(Sell(second, 2m));

        (await rig.EntriesAsync(first)).Select(e => (e.SequenceNo, e.RunningQty, e.RunningValue)).Should().Equal((1, 10m, 40m), (2, 20m, 100m));
        (await rig.EntriesAsync(second)).Select(e => (e.SequenceNo, e.RunningQty, e.RunningValue)).Should().Equal((1, 5m, 40m), (2, 3m, 24m));
    }

    // ── Organizations ────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_stock_and_cost_are_invisible_and_cannot_be_sold()
    {
        var rig = new ProductLedgerRig();
        var (variant, product) = rig.Variants.New();
        var otherOrg = Guid.NewGuid();

        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));

        // The other organization holds none of it: no sale, and its own purchase starts at sequence 1.
        var sell = () => rig.Service(otherOrg).AppendEntryAsync(Sell(variant, 1m));
        await sell.Should().ThrowAsync<ConflictException>();

        var theirs = await rig.Service(otherOrg).AppendEntryAsync(Buy(variant, 10m, 99m) with { ProductUuid = product });
        (theirs.SequenceNo, theirs.RunningQty, theirs.RunningValue, theirs.WeightedAverageCost).Should().Be((1, 10m, 990m, 99m));

        (await rig.EntriesAsync(variant)).Should().ContainSingle().Which.RunningValue.Should().Be(1000m);
        (await rig.EntriesAsync(variant, otherOrg)).Should().ContainSingle().Which.OrganizationId.Should().Be(otherOrg);
    }

    // ── Tracking, for the caller that owns the save ──────────────────────────

    [Fact]
    public async Task Tracking_an_entry_saves_nothing_until_the_caller_saves()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        var (db, service) = rig.Open();

        var entry = await service.TrackEntryAsync(Buy(variant, 10m, 3m));

        db.Entry(entry).State.Should().Be(EntityState.Added);
        (await rig.EntriesAsync(variant)).Should().BeEmpty("nothing is committed by tracking");

        await db.SaveChangesAsync();
        (await rig.EntriesAsync(variant)).Should().ContainSingle().Which.UUID.Should().Be(entry.UUID);
    }

    [Fact]
    public async Task Entries_tracked_together_for_one_variant_chain_and_commit_in_one_save()
    {
        // An invoice with two lines of the same variant: the second sees the first, unsaved.
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));

        var (db, service) = rig.Open();
        var first  = await service.TrackEntryAsync(Sell(variant, 30m));
        var second = await service.TrackEntryAsync(Sell(variant, 20m));

        (first.SequenceNo, first.RunningQty, first.TotalCost).Should().Be((2, 70m, 300m));
        (second.SequenceNo, second.RunningQty, second.RunningValue, second.TotalCost).Should().Be((3, 50m, 500m, 200m));

        await db.SaveChangesAsync();
        (await rig.EntriesAsync(variant)).Select(e => e.SequenceNo).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task A_tracked_entry_is_committed_with_the_business_change_in_a_single_save_or_not_at_all()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));

        var invoice = Receivables.Invoice(rig.Org, Guid.NewGuid(), "SINV-1", new DateTime(2026, 9, 20), 450m, status: "DRAFT");
        await using (var seed = Receivables.Db(rig.Org, rig.DbName))
            await Receivables.Seed(seed, invoice);

        // Issuing: the invoice's status and the SALE entry go in one SaveChanges.
        var saves = new List<(int Invoices, int Entries)>();
        var recorder = new SaveCounter(saves);
        var (db, service) = rig.Open(recorder);

        var tracked = await db.SalesInvoices.SingleAsync();
        tracked.Status = "ISSUED";
        var cogs = (await service.TrackEntryAsync(Sell(variant, 30m))).TotalCost;
        await db.SaveChangesAsync();

        cogs.Should().Be(300m);
        saves.Should().ContainSingle().Which.Should().Be((Invoices: 1, Entries: 1));
        await using var audit = Receivables.Auditor(rig.DbName);
        (await audit.SalesInvoices.SingleAsync()).Status.Should().Be("ISSUED");
        (await audit.ProductLedgerEntries.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_failed_save_of_a_tracked_entry_leaves_nothing_committed()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));

        var failing = new AlwaysFail();
        var (db, service) = rig.Open(failing);
        await service.TrackEntryAsync(Sell(variant, 30m));

        var act = () => db.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
        (await rig.EntriesAsync(variant)).Should().ContainSingle("only the purchase is committed");
    }

    private sealed class SaveCounter(List<(int Invoices, int Entries)> saves) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToList();
            saves.Add((
                entries.Count(e => e.Entity is SalesInvoice && e.State == EntityState.Modified),
                entries.Count(e => e.Entity is ProductLedgerEntry && e.State == EntityState.Added)));
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    // ── Two writers for one variant ──────────────────────────────────────────

    [Fact]
    public async Task Losing_the_race_for_the_next_sequence_retries_against_the_totals_that_moved_on()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));

        // Another writer commits a purchase of 100 @ 20 after this sale has read the last entry.
        var winner = new LoseTheRaceOnce(
            db => db.ChangeTracker.Entries<ProductLedgerEntry>().Any(e => e.State == EntityState.Added),
            () => rig.Service().AppendEntryAsync(Buy(variant, 100m, 20m)));

        var (_, service) = rig.Open(winner);
        var sale = await service.AppendEntryAsync(Sell(variant, 50m));

        winner.Fired.Should().BeTrue();
        // 100 @ 10 and 100 @ 20 make 200 worth 3000, so 50 leave at 15 — not at the 10 the sale first saw.
        (sale.SequenceNo, sale.UnitCost, sale.TotalCost, sale.RunningQty, sale.RunningValue).Should().Be((3, 15m, 750m, 150m, 2250m));
        (await rig.EntriesAsync(variant)).Select(e => e.SequenceNo).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task A_write_that_keeps_failing_gives_up_after_five_tries_and_leaves_nothing_tracked()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        var failing = new AlwaysFail();
        var (db, service) = rig.Open(failing);

        var act = () => service.AppendEntryAsync(Buy(variant, 10m, 1m));

        await act.Should().ThrowAsync<DbUpdateException>();
        failing.Attempts.Should().Be(5);
        db.ChangeTracker.Entries().Should().BeEmpty("nothing half-done may wait for a later save on this context to commit");
        (await rig.EntriesAsync(variant)).Should().BeEmpty();
    }

    [Fact]
    public async Task Whatever_order_two_variants_are_written_in_the_books_of_each_reconcile()
    {
        // A seeded run of purchases and sales over three variants, checked against a plain model.
        var rig = new ProductLedgerRig();
        var variants = Enumerable.Range(0, 3).Select(_ => rig.Variants.New().Variant).ToList();
        var model = variants.ToDictionary(v => v, _ => (Qty: 0m, Value: 0m));
        var rng = new Random(7);

        for (var i = 0; i < 60; i++)
        {
            var v = variants[rng.Next(variants.Count)];
            var (qty, value) = model[v];

            if (qty > 0m && rng.Next(3) == 0)
            {
                var take   = Math.Min(qty, Math.Round(rng.Next(1, 500) / 10m, 1));
                var posted = await rig.Service().AppendEntryAsync(Sell(v, take));
                var expected = WeightedAverageCosting.Out(qty, value, take);
                posted.TotalCost.Should().Be(expected.TotalCost);
                model[v] = (expected.RunningQty, expected.RunningValue);
            }
            else
            {
                var inQty  = Math.Round(rng.Next(1, 1000) / 10m, 1);
                var inCost = Math.Round(rng.Next(100, 9000) / 100m, 2);
                await rig.Service().AppendEntryAsync(Buy(v, inQty, inCost));
                var expected = WeightedAverageCosting.In(qty, value, inQty, inCost);
                model[v] = (expected.RunningQty, expected.RunningValue);
            }
        }

        foreach (var v in variants)
        {
            var entries = await rig.EntriesAsync(v);
            entries.Select(e => e.SequenceNo).Should().Equal(Enumerable.Range(1, entries.Count));
            (entries[^1].RunningQty, entries[^1].RunningValue).Should().Be(model[v]);
        }
    }
}
