using FluentAssertions;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using Xunit;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A35 C7 (spec §8, T-C8-01..08) — the arithmetic of an exchange difference, on its own. Receivable: more base money
/// coming in than was booked is a gain. Payable: more base money going out than was booked is a loss (the sign is the
/// accountant's, not the spec's raw formula — see the tracker's "Built as" note on T-C8-07).
/// </summary>
public class ExchangeDifferenceMathTests
{
    [Fact] // T-C8-01
    public void Realized_gain_on_a_receivable_paid_at_a_higher_rate()
    {
        var r = ExchangeDifferenceMath.Compute(10_550m, 76.30m, 76.45m, 2, ExchangeDifferenceSides.Receivable);

        r.BookedBase.Should().Be(804_965.00m);
        r.SettledBase.Should().Be(806_547.50m);
        r.Difference.Should().Be(1_582.50m);
        r.IsGain.Should().BeTrue();
    }

    [Fact] // T-C8-02
    public void Realized_loss_on_a_receivable_paid_at_a_lower_rate()
    {
        var r = ExchangeDifferenceMath.Compute(5_000m, 316.48m, 315.90m, 2, ExchangeDifferenceSides.Receivable);

        r.Difference.Should().Be(-2_900.00m);
        r.IsLoss.Should().BeTrue();
    }

    [Fact] // T-C8-03
    public void The_same_rate_is_no_difference()
    {
        var r = ExchangeDifferenceMath.Compute(10_550m, 76.30m, 76.30m, 2, ExchangeDifferenceSides.Receivable);

        r.Difference.Should().Be(0m);
        r.IsGain.Should().BeFalse();
        r.IsLoss.Should().BeFalse();
    }

    [Fact] // T-C8-05 — only the part paid is settled; the rest stays at the invoice's rate
    public void A_partial_payment_settles_only_the_amount_paid()
    {
        var r = ExchangeDifferenceMath.Compute(4_000m, 76.30m, 76.50m, 2, ExchangeDifferenceSides.Receivable);

        r.BookedBase.Should().Be(305_200m);
        r.SettledBase.Should().Be(306_000m);
        r.Difference.Should().Be(800m);
    }

    [Fact] // T-C8-06
    public void Unrealized_gain_on_an_open_receivable_when_the_rate_rises()
    {
        ExchangeDifferenceMath.Compute(5_000m, 316.48m, 317.00m, 2, ExchangeDifferenceSides.Receivable)
            .Difference.Should().Be(2_600m);
    }

    [Fact] // T-C8-07 — owing USD 10,000 booked at 278.05; at 277.50 it now costs 5,500 less to pay: a gain
    public void A_payable_whose_currency_falls_is_a_gain_and_one_whose_currency_rises_is_a_loss()
    {
        ExchangeDifferenceMath.Compute(10_000m, 278.05m, 277.50m, 2, ExchangeDifferenceSides.Payable)
            .Difference.Should().Be(5_500m);
        ExchangeDifferenceMath.Compute(10_000m, 277.50m, 278.05m, 2, ExchangeDifferenceSides.Payable)
            .Difference.Should().Be(-5_500m);
    }

    [Fact]
    public void Base_amounts_are_rounded_at_the_base_currencys_decimals_half_away_from_zero()
    {
        // 3 × 0.3333335 = 1.0000005 → 1.000 / 1.001 at three places
        var r = ExchangeDifferenceMath.Compute(3m, 0.333333m, 0.3333335m, 3, ExchangeDifferenceSides.Receivable);

        r.BookedBase.Should().Be(1.000m);   // 0.999999 → 1.000
        r.SettledBase.Should().Be(1.000m);  // 1.0000005 → 1.000 (rounded at 3)
        r.Difference.Should().Be(0m);

        ExchangeDifferenceMath.Compute(1m, 1.0005m, 1m, 3, ExchangeDifferenceSides.Receivable)
            .BookedBase.Should().Be(1.001m, "half away from zero (D-13)");
    }

    [Fact]
    public void An_unknown_side_is_refused()
    {
        var act = () => ExchangeDifferenceMath.Compute(1m, 1m, 2m, 2, "SIDEWAYS");
        act.Should().Throw<ArgumentException>();
    }
}
