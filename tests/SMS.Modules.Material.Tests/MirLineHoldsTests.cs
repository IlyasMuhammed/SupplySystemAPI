using FluentAssertions;
using SMS.Modules.Material.Services;
using Xunit;
using Held = SMS.Modules.Material.Services.MirLineHolds.Held;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// Where a material issue voucher takes a quantity from, out of what the shared reservation ledger holds
/// for a line. The order matters: the ledger consumes holds oldest first when a voucher posts, so a voucher
/// that named a different stock row would take units off one shelf while the ledger stopped holding them on
/// another, and the two rows' reserved counters would drift apart.
/// </summary>
public class MirLineHoldsTests
{
    [Fact]
    public void A_line_held_on_one_row_is_issued_from_that_row()
    {
        MirLineHolds.Take([new Held(7, 30m)], 10m).Should().Equal(new Held(7, 10m));
    }

    [Fact]
    public void Issuing_all_of_a_hold_takes_all_of_it()
    {
        MirLineHolds.Take([new Held(7, 30m)], 30m).Should().Equal(new Held(7, 30m));
    }

    [Fact]
    public void A_quantity_that_spans_holds_is_taken_from_them_in_order_oldest_first()
    {
        MirLineHolds.Take([new Held(1, 5m), new Held(2, 20m), new Held(3, 9m)], 12m)
            .Should().Equal(new Held(1, 5m), new Held(2, 7m));
    }

    [Fact]
    public void It_stops_as_soon_as_the_quantity_is_met_and_does_not_touch_a_later_hold()
    {
        MirLineHolds.Take([new Held(1, 10m), new Held(2, 10m)], 10m).Should().Equal(new Held(1, 10m));
    }

    [Fact]
    public void A_quantity_taken_from_two_holds_on_the_same_row_is_one_source()
    {
        // 4 from the first hold, 3 from the second, and the last 5 from the third, which is on the first row again.
        MirLineHolds.Take([new Held(1, 4m), new Held(2, 3m), new Held(1, 6m)], 12m)
            .Should().Equal(new Held(1, 9m), new Held(2, 3m));
    }

    [Fact]
    public void Asking_for_more_than_is_held_returns_everything_held()
    {
        MirLineHolds.Take([new Held(1, 5m), new Held(2, 5m)], 50m).Should().Equal(new Held(1, 5m), new Held(2, 5m));
    }

    [Fact]
    public void Asking_for_nothing_or_holding_nothing_returns_nothing()
    {
        MirLineHolds.Take([new Held(1, 5m)], 0m).Should().BeEmpty();
        MirLineHolds.Take([new Held(1, 5m)], -3m).Should().BeEmpty();
        MirLineHolds.Take([], 5m).Should().BeEmpty();
    }

    [Fact]
    public void A_hold_with_nothing_left_is_skipped()
    {
        MirLineHolds.Take([new Held(1, 0m), new Held(2, 6m)], 4m).Should().Equal(new Held(2, 4m));
    }

    [Fact]
    public void Fractions_are_kept_exactly()
    {
        MirLineHolds.Take([new Held(1, 0.25m), new Held(2, 1.5m)], 1m)
            .Should().Equal(new Held(1, 0.25m), new Held(2, 0.75m));
    }

    [Fact]
    public void The_total_is_what_is_held_across_every_hold()
    {
        MirLineHolds.Total([new Held(1, 5m), new Held(2, 20.5m), new Held(1, 0.5m)]).Should().Be(26m);
        MirLineHolds.Total([]).Should().Be(0m);
    }

    [Fact]
    public void What_is_taken_never_exceeds_what_is_asked_for_or_held()
    {
        var holds = new[] { new Held(1, 3m), new Held(2, 8m), new Held(3, 1m) };

        foreach (var quantity in new[] { 0.5m, 3m, 4m, 11m, 12m, 100m })
        {
            var taken = MirLineHolds.Take(holds, quantity).Sum(s => s.Quantity);
            taken.Should().Be(Math.Min(quantity, MirLineHolds.Total(holds)), $"asked for {quantity}");
        }
    }
}
