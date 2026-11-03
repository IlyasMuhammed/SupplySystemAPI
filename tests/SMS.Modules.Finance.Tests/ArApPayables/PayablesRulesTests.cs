using FluentAssertions;
using SMS.Modules.Finance.ArAp.Payables;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApPayables;

/// <summary>
/// A48 AP — the pure rules: BR-MT-001..004 (tolerance comparison), BR-EP-001/002/004 (early-payment discount) and the
/// cash-flow week boundaries. No database.
/// </summary>
public class PayablesRulesTests
{
    private static readonly MatchTolerance TwoPercent = new(2m, 0m, 0m, 0m, 0m, false);

    private static MatchLineInput Line(decimal poQty, decimal poPrice, decimal? reference, decimal invQty, decimal invPrice, decimal? grn = null) =>
        new(1, "Steel bar", Guid.NewGuid(), Guid.NewGuid(), null, poQty, poPrice, grn ?? reference, reference, invQty, invPrice);

    // ── BR-MT-003 price ──────────────────────────────────────────────────────

    [Fact]
    public void TS_MT_001_every_line_exact_is_Matched()
    {
        var eval = MatchEvaluator.Evaluate([Line(10, 100, 10, 10, 100), Line(5, 20, 5, 5, 20)], TwoPercent, twoWay: false);
        eval.Status.Should().Be(MatchStatuses.Matched);
        eval.WithinTolerance.Should().BeTrue();
        eval.Lines.Should().OnlyContain(l => l.Status == MatchStatuses.Matched);
        eval.VarianceAmount.Should().Be(0m);
    }

    [Fact]
    public void TS_MT_002_price_3_percent_over_with_2_percent_tolerance_is_PriceMismatch()
    {
        var eval = MatchEvaluator.Evaluate([Line(10, 100, 10, 10, 103)], TwoPercent, twoWay: false);
        eval.Lines.Single().Status.Should().Be(MatchStatuses.PriceMismatch);
        eval.Lines.Single().PriceVariance.Should().Be(3m);
        eval.Status.Should().Be(MatchStatuses.Mismatch);
        eval.WithinTolerance.Should().BeFalse();
    }

    [Fact]
    public void TS_MT_003_price_1_5_percent_over_with_2_percent_tolerance_is_Matched()
    {
        var eval = MatchEvaluator.Evaluate([Line(10, 100, 10, 10, 101.5m)], TwoPercent, twoWay: false);
        eval.Status.Should().Be(MatchStatuses.Matched);
        eval.Lines.Single().TotalVariance.Should().Be(15m, "10 × 1.50 more than the PO price");
        eval.VariancePercent.Should().Be(1.5m);
    }

    [Fact]
    public void Price_amount_tolerance_passes_a_line_the_percent_would_fail()
    {
        var tolerance = new MatchTolerance(1m, 5m, 0m, 0m, 0m, false);
        MatchEvaluator.PriceWithin(104m, 100m, tolerance).Should().BeTrue("|4| ≤ 5, BR-MT-003's OR");
        MatchEvaluator.PriceWithin(106m, 100m, tolerance).Should().BeFalse();
    }

    [Fact]
    public void Price_below_the_PO_is_judged_on_the_absolute_difference()
    {
        MatchEvaluator.PriceWithin(97m, 100m, TwoPercent).Should().BeFalse();
        MatchEvaluator.PriceWithin(98.5m, 100m, TwoPercent).Should().BeTrue();
    }

    [Fact]
    public void A_zero_PO_price_only_passes_on_the_amount()
    {
        MatchEvaluator.PriceWithin(0m, 0m, TwoPercent).Should().BeTrue();
        MatchEvaluator.PriceWithin(1m, 0m, TwoPercent).Should().BeFalse();
    }

    // ── BR-MT-004 quantity ───────────────────────────────────────────────────

    [Fact]
    public void TS_MT_004_invoice_quantity_above_GRN_quantity_is_QuantityMismatch()
    {
        var eval = MatchEvaluator.Evaluate([Line(10, 100, 8, 10, 100, grn: 8)], TwoPercent, twoWay: false);
        var line = eval.Lines.Single();
        line.Status.Should().Be(MatchStatuses.QuantityMismatch);
        line.QuantityVariance.Should().Be(2m);
        eval.Status.Should().Be(MatchStatuses.Mismatch);
    }

    [Fact]
    public void Quantity_wins_over_price_when_both_are_out()
    {
        MatchEvaluator.EvaluateLine(Line(10, 100, 8, 10, 110), TwoPercent).Status.Should().Be(MatchStatuses.QuantityMismatch);
    }

    [Fact]
    public void Quantity_tolerance_percent_allows_a_small_overbill()
    {
        MatchEvaluator.QuantityWithin(10.4m, 10m, 5m).Should().BeTrue();
        MatchEvaluator.QuantityWithin(10.6m, 10m, 5m).Should().BeFalse();
    }

    [Fact]
    public void TS_MT_010_partial_delivery_invoiced_for_what_arrived_is_Matched()
    {
        // PO 10, GRN 6, invoice 6.
        var eval = MatchEvaluator.Evaluate([Line(10, 100, 6, 6, 100, grn: 6)], TwoPercent, twoWay: false);
        eval.Status.Should().Be(MatchStatuses.Matched);
        eval.TotalPo.Should().Be(1000m);
        eval.TotalGrn.Should().Be(600m);
        eval.TotalInvoice.Should().Be(600m);
        eval.VarianceAmount.Should().Be(0m, "against what this invoice bills, not the whole PO line");
    }

    [Fact]
    public void TS_MT_009_two_way_has_no_GRN_total()
    {
        var eval = MatchEvaluator.Evaluate([new MatchLineInput(1, "x", null, Guid.NewGuid(), null, 10, 100, null, 10, 10, 100)], TwoPercent, twoWay: true);
        eval.Status.Should().Be(MatchStatuses.Matched);
        eval.TotalGrn.Should().BeNull();
    }

    [Fact]
    public void A_line_no_PO_line_stands_behind_is_Unmatched()
    {
        var line = new MatchLineInput(1, "Freight", null, null, null, 0, 0, null, null, 1, 50);
        MatchEvaluator.EvaluateLine(line, TwoPercent).Status.Should().Be(MatchStatuses.Unmatched);
        MatchEvaluator.Evaluate([line], TwoPercent, twoWay: false).Status.Should().Be(MatchStatuses.Mismatch);
    }

    // ── Header ───────────────────────────────────────────────────────────────

    [Fact]
    public void Some_lines_matched_and_some_not_is_PartialMatch()
    {
        var eval = MatchEvaluator.Evaluate([Line(10, 100, 10, 10, 100), Line(10, 100, 10, 10, 120)], TwoPercent, twoWay: false);
        eval.Status.Should().Be(MatchStatuses.PartialMatch);
    }

    [Fact]
    public void Header_total_outside_its_tolerance_is_Mismatch_even_when_every_line_passes()
    {
        var tolerance = new MatchTolerance(2m, 0m, 0m, 1m, 0m, false);
        var eval = MatchEvaluator.Evaluate([Line(10, 100, 10, 10, 101.5m)], tolerance, twoWay: false);
        eval.Lines.Single().Status.Should().Be(MatchStatuses.Matched);
        eval.Status.Should().Be(MatchStatuses.Mismatch, "1.5% over on the total, 1% allowed");
    }

    [Fact]
    public void No_total_tolerance_means_the_header_total_is_not_checked()
    {
        MatchEvaluator.TotalWithin(1015m, 1000m, TwoPercent).Should().BeTrue();
        MatchEvaluator.TotalWithin(1015m, 1000m, new MatchTolerance(0, 0, 0, 0m, 20m, false)).Should().BeTrue();
        MatchEvaluator.TotalWithin(1025m, 1000m, new MatchTolerance(0, 0, 0, 0m, 20m, false)).Should().BeFalse();
    }

    [Fact]
    public void Override_and_approve_eligibility()
    {
        MatchStatuses.IsOverridable(MatchStatuses.Mismatch).Should().BeTrue();
        MatchStatuses.IsOverridable(MatchStatuses.PartialMatch).Should().BeTrue();
        MatchStatuses.IsOverridable(MatchStatuses.Matched).Should().BeFalse();
        MatchStatuses.IsApprovable(MatchStatuses.Matched).Should().BeTrue();
        MatchStatuses.IsApprovable(MatchStatuses.Overridden).Should().BeTrue();
        MatchStatuses.IsApprovable(MatchStatuses.Mismatch).Should().BeFalse();
    }

    // ── C15 early-payment discount ───────────────────────────────────────────

    [Fact]
    public void TS_EP_001_paying_within_the_window_takes_the_discount()
    {
        var calc = PayablesMath.Discount(1000m, 2m, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 10));
        calc.IsEligible.Should().BeTrue("on the deadline itself is still within it");
        calc.DiscountAmount.Should().Be(20m);
        calc.NetPaymentAmount.Should().Be(980m);
        calc.DaysUntilDeadline.Should().Be(0);
    }

    [Fact]
    public void TS_EP_002_paying_after_the_window_pays_in_full()
    {
        var calc = PayablesMath.Discount(1000m, 2m, new DateOnly(2026, 10, 10), new DateOnly(2026, 10, 11));
        calc.IsEligible.Should().BeFalse();
        calc.DiscountAmount.Should().Be(0m);
        calc.NetPaymentAmount.Should().Be(1000m);
        calc.DaysUntilDeadline.Should().Be(-1);
    }

    [Fact]
    public void BR_EP_002_discount_is_rounded_to_the_cent()
    {
        PayablesMath.Discount(333.33m, 2m, new DateOnly(2026, 1, 31), new DateOnly(2026, 1, 1)).DiscountAmount.Should().Be(6.67m);
    }

    [Fact]
    public void No_term_no_discount()
    {
        PayablesMath.Discount(1000m, null, null, new DateOnly(2026, 1, 1)).IsEligible.Should().BeFalse();
    }

    [Theory]
    [InlineData(2, 10, 30, null)]
    [InlineData(2, 30, 30, "fewer than net days")]
    [InlineData(2, 40, 30, "fewer than net days")]
    [InlineData(0, 10, 30, "percent")]
    [InlineData(100, 10, 30, "percent")]
    [InlineData(2, -1, 30, "negative")]
    public void BR_EP_004_discount_days_must_be_less_than_net_days(decimal pct, int discountDays, int netDays, string? problem)
    {
        var result = PayablesMath.TermProblem(pct, discountDays, netDays);
        if (problem is null) result.Should().BeNull();
        else result.Should().Contain(problem);
    }

    [Fact]
    public void BR_EP_001_the_deadline_is_invoice_date_plus_discount_days()
    {
        PayablesMath.DiscountDate(new DateOnly(2026, 10, 1), 10).Should().Be(new DateOnly(2026, 10, 11));
    }

    // ── Cash-flow weeks ──────────────────────────────────────────────────────

    [Fact]
    public void Forecast_weeks_run_Monday_to_Sunday()
    {
        var wednesday = new DateOnly(2026, 10, 21);
        PayablesMath.WeekStart(wednesday).Should().Be(new DateOnly(2026, 10, 19));
        PayablesMath.WeekStart(new DateOnly(2026, 10, 19)).Should().Be(new DateOnly(2026, 10, 19));
        PayablesMath.WeekStart(new DateOnly(2026, 10, 25)).Should().Be(new DateOnly(2026, 10, 19), "Sunday ends the week");
    }

    [Fact]
    public void Week_index_counts_from_the_week_of_the_start_date()
    {
        var from = new DateOnly(2026, 10, 21);
        PayablesMath.WeekIndex(new DateOnly(2026, 10, 18), from, 8).Should().Be(-1, "before the first week: overdue");
        PayablesMath.WeekIndex(new DateOnly(2026, 10, 19), from, 8).Should().Be(0);
        PayablesMath.WeekIndex(new DateOnly(2026, 10, 25), from, 8).Should().Be(0);
        PayablesMath.WeekIndex(new DateOnly(2026, 10, 26), from, 8).Should().Be(1, "the next Monday starts week 2");
        PayablesMath.WeekIndex(new DateOnly(2027, 1, 1), from, 8).Should().Be(8, "after the horizon");
    }
}
