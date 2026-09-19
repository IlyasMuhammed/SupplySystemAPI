using FluentAssertions;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Rating;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Rating;

// T-44 — the number every tariff multiplies and every carrier invoice is checked against.
// Pure arithmetic: no database, no consignment, no carrier.
public class ChargeableWeightTests
{
    // 40 × 30 × 20 = 24,000 cm³ — 4.8 kg at a 5000 divisor, 4.0 at 6000.
    private static ParcelMeasurements Parcel(
        decimal? weight = 5m, decimal? l = 40m, decimal? w = 30m, decimal? h = 20m) =>
        new(l, w, h, weight);

    private static ServiceWeightTerms Terms(
        int? divisor = 5000, decimal? minimum = null, decimal? rounding = null,
        decimal? maxWeight = null, decimal? maxLength = null, decimal? maxGirth = null) =>
        new(divisor, minimum, rounding, maxWeight, maxLength, maxGirth);

    private static ChargeableWeightResult Rate(ParcelMeasurements parcel, ServiceWeightTerms terms) =>
        ChargeableWeight.ForParcel(parcel, terms);

    // ── Which figure wins ─────────────────────────────────────────────────────

    [Fact]
    public void A_dense_parcel_is_charged_on_the_scale()
    {
        var result = Rate(Parcel(weight: 20m), Terms());

        result.ActualKg.Should().Be(20m);
        result.VolumetricKg.Should().Be(4.8m);
        result.ChargeableKg.Should().Be(20m);
        result.Basis.Should().Be(ChargeableWeightBasis.Actual);
    }

    [Fact]
    public void A_bulky_light_parcel_is_charged_on_its_volume()
    {
        // The whole reason dimensional weight exists: a box of packing foam costs a courier the
        // same space as a box of bolts, and is charged for it.
        var result = Rate(Parcel(weight: 1m), Terms());

        result.ChargeableKg.Should().Be(4.8m);
        result.Basis.Should().Be(ChargeableWeightBasis.Volumetric);
        result.DivisorUsed.Should().Be(5000);
    }

    [Fact]
    public void A_tie_is_charged_as_actual()
    {
        // Volume did not change the answer, so saying it decided would be misleading on an invoice
        // dispute.
        var result = Rate(Parcel(weight: 4.8m), Terms());

        result.ChargeableKg.Should().Be(4.8m);
        result.Basis.Should().Be(ChargeableWeightBasis.Actual);
    }

    [Theory]
    [InlineData(5000, 4.8)]
    [InlineData(6000, 4.0)]
    [InlineData(4000, 6.0)]
    public void The_same_parcel_costs_different_amounts_on_different_divisors(int divisor, decimal expected)
    {
        // Which is why the divisor belongs to the service, not to the carrier and not to the parcel.
        Rate(Parcel(weight: 1m), Terms(divisor: divisor)).ChargeableKg.Should().Be(expected);
    }

    [Fact]
    public void A_service_that_does_not_charge_on_volume_produces_no_volumetric_weight()
    {
        var result = Rate(Parcel(weight: 1m), Terms(divisor: null));

        result.VolumetricKg.Should().BeNull();
        result.DivisorUsed.Should().BeNull();
        result.ChargeableKg.Should().Be(1m);
        result.Basis.Should().Be(ChargeableWeightBasis.Actual);
        result.Warnings.Should().BeEmpty("not charging on volume is an arrangement, not a gap");
    }

    [Fact]
    public void With_no_service_terms_at_all_a_parcel_still_rates_on_actual_weight()
    {
        // A consignment whose service matches nothing must still show a weight, not an error.
        var result = Rate(Parcel(weight: 7.25m), ServiceWeightTerms.None);

        result.ChargeableKg.Should().Be(7.25m);
        result.Basis.Should().Be(ChargeableWeightBasis.Actual);
    }

    // ── Rounding ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10.2, 1.0, 11.0)]
    [InlineData(10.2, 0.5, 10.5)]
    [InlineData(10.0, 0.5, 10.0)]  // already on the step
    [InlineData(10.0, 1.0, 10.0)]
    [InlineData(0.1, 1.0, 1.0)]
    public void Chargeable_weight_rounds_up_to_the_services_step(decimal weight, decimal step, decimal expected)
    {
        // Never down. A carrier billing in whole kilos invoices 11 for a 10.2 kg parcel, and a
        // system that quotes 10.2 has produced a number the invoice will never match.
        Rate(Parcel(weight: weight, l: null, w: null, h: null), Terms(divisor: null, rounding: step))
            .ChargeableKg.Should().Be(expected);
    }

    [Fact]
    public void Rounding_applies_to_whichever_figure_won()
    {
        // 4.8 volumetric, rounded up to the half kilo.
        Rate(Parcel(weight: 1m), Terms(rounding: 0.5m)).ChargeableKg.Should().Be(5.0m);
    }

    [Fact]
    public void No_rounding_step_bills_the_exact_figure()
    {
        Rate(Parcel(weight: 10.237m, l: null, w: null, h: null), Terms(divisor: null))
            .ChargeableKg.Should().Be(10.237m);
    }

    // ── The minimum ───────────────────────────────────────────────────────────

    [Fact]
    public void A_parcel_under_the_services_floor_is_charged_at_the_floor()
    {
        var result = Rate(Parcel(weight: 0.1m, l: 10m, w: 10m, h: 10m), Terms(minimum: 0.5m));

        result.ActualKg.Should().Be(0.1m);
        result.ChargeableKg.Should().Be(0.5m);
        result.Basis.Should().Be(ChargeableWeightBasis.Minimum);
    }

    [Fact]
    public void A_parcel_exactly_on_the_floor_is_not_credited_to_the_floor()
    {
        // The minimum did not decide anything, so the basis must not claim it did.
        Rate(Parcel(weight: 0.5m, l: 10m, w: 10m, h: 10m), Terms(minimum: 0.5m))
            .Basis.Should().Be(ChargeableWeightBasis.Actual);
    }

    [Fact]
    public void The_minimum_has_the_last_word_over_the_rounding_step()
    {
        // Rounding runs first, then the floor. The other order would quote a 0.7 kg minimum at 1 kg
        // on a half-kilo step — a figure the carrier never asked for.
        var result = Rate(
            Parcel(weight: 0.2m, l: null, w: null, h: null),
            Terms(divisor: null, minimum: 0.7m, rounding: 0.5m));

        result.ChargeableKg.Should().Be(0.7m);
        result.Basis.Should().Be(ChargeableWeightBasis.Minimum);
    }

    [Fact]
    public void A_minimum_does_not_pull_a_heavy_parcel_down()
    {
        Rate(Parcel(weight: 20m), Terms(minimum: 0.5m)).ChargeableKg.Should().Be(20m);
    }

    // ── Measurements that are not measurements ────────────────────────────────

    [Fact]
    public void A_parcel_with_neither_a_weight_nor_dimensions_cannot_be_rated()
    {
        // And says so, rather than coming back as zero — which would quote a free shipment.
        var result = Rate(new ParcelMeasurements(null, null, null, null), Terms());

        result.ChargeableKg.Should().BeNull();
        result.Basis.Should().Be(ChargeableWeightBasis.Unknown);
        result.Warnings.Should().ContainMatch("*neither a weight nor a full set of dimensions*");
    }

    [Fact]
    public void A_measured_but_unweighed_parcel_is_charged_on_volume_and_flagged()
    {
        var result = Rate(Parcel(weight: null), Terms());

        result.ChargeableKg.Should().Be(4.8m);
        result.Basis.Should().Be(ChargeableWeightBasis.Volumetric);
        result.Warnings.Should().ContainMatch("*No weight is recorded*");
    }

    [Fact]
    public void A_weighed_but_unmeasured_parcel_on_a_volumetric_service_is_flagged_as_possibly_under_quoted()
    {
        var result = Rate(Parcel(weight: 5m, l: null, w: null, h: null), Terms());

        result.ChargeableKg.Should().Be(5m);
        result.VolumetricKg.Should().BeNull();
        result.Warnings.Should().ContainMatch("*no dimensions*under-quoted*");
    }

    [Fact]
    public void Half_a_set_of_dimensions_is_no_set_of_dimensions()
    {
        var result = Rate(Parcel(weight: 5m, h: null), Terms());

        result.VolumetricKg.Should().BeNull();
        result.LengthPlusGirthCm.Should().BeNull();
        result.Warnings.Should().ContainMatch("*Only some of this package's dimensions*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-20)]
    public void A_zero_or_negative_dimension_is_treated_as_unmeasured_not_as_no_volume(decimal height)
    {
        // Treating it as a real measurement gives a volumetric weight of zero, which reads as a
        // cheap parcel rather than an unmeasured one — and quietly under-charges every one of them.
        var result = Rate(Parcel(weight: 5m, h: height), Terms());

        result.VolumetricKg.Should().BeNull();
        result.ChargeableKg.Should().Be(5m);
    }

    [Fact]
    public void A_zero_weight_is_treated_as_unweighed()
    {
        var result = Rate(Parcel(weight: 0m, l: null, w: null, h: null), Terms(divisor: null));

        result.ActualKg.Should().BeNull();
        result.Basis.Should().Be(ChargeableWeightBasis.Unknown);
    }

    // ── What the service will not carry ───────────────────────────────────────

    [Fact]
    public void The_longest_side_is_the_longest_side_whichever_column_it_was_typed_into()
    {
        // A 30 × 30 × 120 carton is 120 cm long even when the field called "LengthCm" says 30.
        // Checking the limit against that field is the classic way an oversize parcel passes.
        var result = Rate(Parcel(weight: 5m, l: 30m, w: 30m, h: 120m), Terms(maxLength: 100m));

        result.LongestSideCm.Should().Be(120m);
        result.Warnings.Should().ContainMatch("*longest side is 120 cm*limit is 100 cm*");
    }

    [Fact]
    public void Length_plus_girth_is_measured_around_the_parcel()
    {
        // 120 + 2 × (30 + 30) = 240.
        Rate(Parcel(weight: 5m, l: 30m, w: 30m, h: 120m), Terms()).LengthPlusGirthCm.Should().Be(240m);
    }

    [Fact]
    public void A_long_thin_parcel_is_flagged_although_every_single_dimension_passes()
    {
        // Each side is inside a 150 cm limit and the parcel is still refused at the counter.
        var result = Rate(
            Parcel(weight: 5m, l: 140m, w: 40m, h: 40m),
            Terms(maxLength: 150m, maxGirth: 300m));

        result.LengthPlusGirthCm.Should().Be(300m);
        result.Warnings.Should().BeEmpty("300 is the limit, not past it");

        var over = Rate(Parcel(weight: 5m, l: 145m, w: 40m, h: 40m), Terms(maxLength: 150m, maxGirth: 300m));

        over.LengthPlusGirthCm.Should().Be(305m);
        over.Warnings.Should().ContainMatch("*length plus girth*limit is 300 cm*");
    }

    [Fact]
    public void A_package_over_the_services_weight_limit_is_flagged()
    {
        var result = Rate(Parcel(weight: 42m), Terms(maxWeight: 30m));

        result.Warnings.Should().ContainMatch("*weighs 42 kg*not carry more than 30 kg*");
        result.ChargeableKg.Should().Be(42m, "it is a warning, not a refusal — the parcel exists either way");
    }

    [Fact]
    public void A_limit_that_is_met_exactly_is_not_a_breach()
    {
        Rate(Parcel(weight: 30m), Terms(maxWeight: 30m, maxLength: 40m)).Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Limits_are_only_checked_where_the_service_sets_one()
    {
        Rate(Parcel(weight: 500m, l: 400m, w: 300m, h: 200m), Terms()).Warnings.Should().BeEmpty();
    }

    // ── Precision ─────────────────────────────────────────────────────────────

    [Fact]
    public void Figures_are_settled_at_three_decimal_places()
    {
        // The columns are decimal(18,3). A figure that does not survive its own column is a figure
        // that will disagree with itself after a round trip.
        var result = Rate(Parcel(weight: 1m, l: 10m, w: 10m, h: 7m), Terms(divisor: 5000));

        result.VolumetricKg.Should().Be(0.14m);
        result.ChargeableKg.Should().Be(1m);
    }

    [Fact]
    public void A_recurring_division_is_rounded_rather_than_carried()
    {
        // 1000 ÷ 6000 = 0.1666…
        Rate(Parcel(weight: null, l: 10m, w: 10m, h: 10m), Terms(divisor: 6000))
            .VolumetricKg.Should().Be(0.167m);
    }
}
