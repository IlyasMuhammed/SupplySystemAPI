using FluentAssertions;
using SMS.Modules.Demand.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P5-10 §14.2 — <c>margin = selling − purchase</c>, <c>margin_percent = margin / selling × 100</c>.</summary>
public class SaleOrderMarginTests
{
    [Fact]
    public void Sells_for_40_bought_for_25_is_15_and_37_point_5_percent()
    {
        var (margin, percent) = SaleOrderMargin.Compute(40m, 25m);

        margin.Should().Be(15.00m);
        percent.Should().Be(37.50m);
    }

    [Fact]
    public void Selling_below_cost_is_a_negative_margin_and_a_negative_percent()
    {
        var (margin, percent) = SaleOrderMargin.Compute(20m, 25m);

        margin.Should().Be(-5.00m);
        percent.Should().Be(-25.00m);
    }

    [Fact]
    public void Selling_at_cost_is_zero()
    {
        var (margin, percent) = SaleOrderMargin.Compute(25m, 25m);

        margin.Should().Be(0m);
        percent.Should().Be(0m);
    }

    [Fact]
    public void Free_stock_has_a_margin_but_no_percentage_to_take_it_of()
    {
        var (margin, percent) = SaleOrderMargin.Compute(0m, 25m);

        margin.Should().Be(-25.00m);
        percent.Should().BeNull();
    }

    [Fact]
    public void Both_figures_round_to_two_places_away_from_zero()
    {
        // 10 / 30 = 33.333…% ; 1.005 − 0 rounds up to 1.01 rather than to even.
        SaleOrderMargin.Compute(30m, 20m).Percent.Should().Be(33.33m);
        SaleOrderMargin.Compute(1.005m, 0m).Margin.Should().Be(1.01m);
        SaleOrderMargin.Compute(3m, 1m).Percent.Should().Be(66.67m);
    }

    [Fact]
    public void A_cost_absurdly_above_the_price_is_clamped_to_fit_the_column_rather_than_overflowing_it()
    {
        // MarginPercent is decimal(5,2): anything past ±999.99 would fail the whole save.
        var (margin, percent) = SaleOrderMargin.Compute(1m, 500m);

        margin.Should().Be(-499.00m);
        percent.Should().Be(-999.99m);
    }

    [Fact]
    public void A_zero_cost_is_exactly_a_hundred_percent_however_large_the_price()
    {
        // Margin percent is bounded above by the price itself, so only a loss can leave the column's range.
        SaleOrderMargin.Compute(100_000m, 0m).Percent.Should().Be(100.00m);
    }
}
