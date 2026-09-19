using FluentAssertions;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Rating;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Rating;

// T-46 — picking the lane, picking the break, and working out the money. Pure, like T-44's
// calculator, because it decides money and money has to be checkable without a database in the way.
public class RateCardPricerTests
{
    private static int _nextId = 1;

    private static RateCardLane Lane(
        string? originCountry = null, string? originPrefix = null,
        string? destCountry = null, string? destPrefix = null,
        string? name = null, params RateCardBreak[] breaks) =>
        new()
        {
            Id                        = _nextId++,
            Name                      = name,
            OriginCountryIso          = originCountry,
            OriginPostcodePrefix      = originPrefix,
            DestinationCountryIso     = destCountry,
            DestinationPostcodePrefix = destPrefix,
            Breaks                    = [.. breaks]
        };

    private static RateCardBreak Break(decimal from, decimal amount, RateBasis basis = RateBasis.PerKg) =>
        new() { Id = _nextId++, FromWeightKg = from, Amount = amount, Basis = LogisticsCode.Of(basis) };

    private static RateCard Card(
        decimal? minimum = null, decimal? fuel = null,
        decimal? codPercent = null, decimal? codMinimum = null,
        string currency = "PKR", params RateCardLane[] lanes) =>
        new()
        {
            Name                 = "Test tariff",
            Currency             = currency,
            MinimumCharge        = minimum,
            FuelSurchargePercent = fuel,
            CodFeePercent        = codPercent,
            CodFeeMinimum        = codMinimum,
            Lanes                = [.. lanes]
        };

    private static readonly RateLaneKey KarachiToLahore = new("PK", "74000", "PK", "54000");

    // ── Picking the lane ──────────────────────────────────────────────────────

    [Fact]
    public void A_lane_that_states_nothing_matches_anything()
    {
        var anywhere = Lane(name: "Anywhere");

        RateCardPricer.BestLane([anywhere], KarachiToLahore).Should().Be(anywhere);
        RateCardPricer.BestLane([anywhere], new RateLaneKey(null, null, null, null)).Should().Be(anywhere);
    }

    [Fact]
    public void The_most_specific_lane_wins()
    {
        // The whole point of holding several lanes on one card: a general tariff with exceptions.
        var anywhere = Lane(name: "Anywhere");
        var domestic = Lane(originCountry: "PK", destCountry: "PK", name: "Domestic");
        var upcountry = Lane(originCountry: "PK", originPrefix: "74", destCountry: "PK", destPrefix: "54",
                             name: "Karachi to Lahore");

        RateCardPricer.BestLane([anywhere, domestic, upcountry], KarachiToLahore)
            .Should().Be(upcountry);

        RateCardPricer.BestLane([anywhere, domestic], KarachiToLahore)
            .Should().Be(domestic);
    }

    [Fact]
    public void A_longer_postcode_prefix_beats_a_shorter_one()
    {
        // "540" is a narrower statement about where a parcel is going than "54".
        var broad  = Lane(destCountry: "PK", destPrefix: "54",  name: "Punjab");
        var narrow = Lane(destCountry: "PK", destPrefix: "540", name: "Lahore city");

        RateCardPricer.BestLane([broad, narrow], KarachiToLahore).Should().Be(narrow);
    }

    [Fact]
    public void A_lane_that_asks_for_a_postcode_does_not_match_an_address_without_one()
    {
        // Letting it through would price an unaddressed consignment on a lane nobody checked it
        // belonged to — and an address with no postcode is common enough to matter.
        var lane = Lane(destCountry: "PK", destPrefix: "54");

        RateCardPricer.BestLane([lane], new RateLaneKey("PK", null, "PK", null)).Should().BeNull();
    }

    [Theory]
    [InlineData("pk", "54000")]
    [InlineData("PK", "54000")]
    [InlineData("  PK  ", "  54000  ")]
    public void Country_and_postcode_are_matched_however_they_were_typed(string country, string postcode)
    {
        var lane = Lane(destCountry: "PK", destPrefix: "54");

        RateCardPricer.BestLane([lane], new RateLaneKey("PK", "74000", country, postcode))
            .Should().Be(lane);
    }

    [Fact]
    public void A_lane_for_somewhere_else_does_not_match()
    {
        var export = Lane(destCountry: "AE", name: "Export to UAE");

        RateCardPricer.BestLane([export], KarachiToLahore).Should().BeNull();
    }

    [Fact]
    public void A_deleted_lane_is_not_matched()
    {
        var lane = Lane(name: "Retired");
        lane.IsDelete = true;

        RateCardPricer.BestLane([lane], KarachiToLahore).Should().BeNull();
    }

    // ── Picking the break ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.5, 200)]
    [InlineData(4.9, 200)]
    [InlineData(5.0, 150)]
    [InlineData(29.9, 150)]
    [InlineData(30.0, 110)]
    [InlineData(5000, 110)]
    public void The_break_that_applies_is_the_highest_one_the_weight_reaches(decimal weight, decimal expected)
    {
        RateCardBreak[] breaks = [Break(0m, 200m), Break(5m, 150m), Break(30m, 110m)];

        RateCardPricer.BreakFor(breaks, weight)!.Amount.Should().Be(expected);
    }

    [Fact]
    public void A_weight_below_every_break_finds_none()
    {
        // Validation stops a saved lane looking like this. It is still handled rather than assumed
        // away, because the alternative is pricing a consignment at nothing.
        RateCardPricer.BreakFor([Break(5m, 150m)], 1m).Should().BeNull();
    }

    // ── The money ─────────────────────────────────────────────────────────────

    private static readonly RateLaneKey Anywhere = new(null, null, null, null);

    [Fact]
    public void A_per_kilogram_break_multiplies_by_the_chargeable_weight()
    {
        var card = Card(lanes: Lane(breaks: Break(0m, 150m)));

        var option = RateCardPricer.Price(card, Anywhere, 12.5m, null, "ECON", "Economy", null, DateTime.UtcNow);

        option!.BaseAmount.Should().Be(1875m, "12.5 kg at 150");
        option.TotalAmount.Should().Be(1875m);
        option.ChargeableWeightKg.Should().Be(12.5m);
        option.Currency.Should().Be("PKR");
    }

    [Fact]
    public void A_flat_break_charges_the_same_whatever_it_weighs()
    {
        var card = Card(lanes: Lane(breaks: Break(0m, 900m, RateBasis.Flat)));

        RateCardPricer.Price(card, Anywhere, 1m,  null, "E", null, null, DateTime.UtcNow)!
            .TotalAmount.Should().Be(900m);
        RateCardPricer.Price(card, Anywhere, 25m, null, "E", null, null, DateTime.UtcNow)!
            .TotalAmount.Should().Be(900m);
    }

    [Fact]
    public void The_cards_minimum_charge_floors_the_base_rate()
    {
        var card = Card(minimum: 500m, lanes: Lane(breaks: Break(0m, 150m)));

        RateCardPricer.Price(card, Anywhere, 1m, null, "E", null, null, DateTime.UtcNow)!
            .BaseAmount.Should().Be(500m, "1 kg at 150 is below the floor");
    }

    [Fact]
    public void Fuel_is_charged_on_top_of_the_floored_base_not_underneath_it()
    {
        // A minimum charge is a floor on the carriage. Fuel is a percentage of whatever the carriage
        // came to — so applying the floor after fuel would quote a figure no carrier ever bills.
        var card = Card(minimum: 500m, fuel: 12m, lanes: Lane(breaks: Break(0m, 150m)));

        var option = RateCardPricer.Price(card, Anywhere, 1m, null, "E", null, null, DateTime.UtcNow);

        option!.BaseAmount.Should().Be(500m);
        option.Surcharges.Should().ContainSingle(s => s.Code == "FUEL").Which.Amount.Should().Be(60m);
        option.TotalAmount.Should().Be(560m);
    }

    [Fact]
    public void A_quote_adds_up_to_its_own_total()
    {
        var card = Card(fuel: 12m, codPercent: 1.5m, codMinimum: 150m, lanes: Lane(breaks: Break(0m, 100m)));

        var option = RateCardPricer.Price(card, Anywhere, 10m, 20_000m, "E", null, null, DateTime.UtcNow);

        option!.BaseAmount.Should().Be(1000m);
        option.Surcharges!.Select(s => s.Code).Should().Equal(["FUEL", "COD"]);
        (option.BaseAmount!.Value + option.Surcharges!.Sum(s => s.Amount))
            .Should().Be(option.TotalAmount);
        option.TotalAmount.Should().Be(1420m, "1000 + 120 fuel + 300 COD");
    }

    [Fact]
    public void A_cash_on_delivery_fee_takes_the_greater_of_its_percentage_and_its_minimum()
    {
        var card = Card(codPercent: 1.5m, codMinimum: 150m, lanes: Lane(breaks: Break(0m, 100m)));

        RateCardPricer.Price(card, Anywhere, 10m, 1000m, "E", null, null, DateTime.UtcNow)!
            .Surcharges!.Single(s => s.Code == "COD").Amount
            .Should().Be(150m, "1.5% of 1,000 is 15, and the minimum is 150");

        RateCardPricer.Price(card, Anywhere, 10m, 40_000m, "E", null, null, DateTime.UtcNow)!
            .Surcharges!.Single(s => s.Code == "COD").Amount
            .Should().Be(600m);
    }

    [Fact]
    public void No_cash_on_delivery_means_no_handling_fee()
    {
        var card = Card(codPercent: 1.5m, codMinimum: 150m, lanes: Lane(breaks: Break(0m, 100m)));

        RateCardPricer.Price(card, Anywhere, 10m, null, "E", null, null, DateTime.UtcNow)!
            .Surcharges.Should().NotContain(s => s.Code == "COD");
    }

    [Fact]
    public void A_card_with_no_surcharges_quotes_a_bare_base_rate()
    {
        var card = Card(lanes: Lane(breaks: Break(0m, 100m)));

        var option = RateCardPricer.Price(card, Anywhere, 10m, 5000m, "E", null, null, DateTime.UtcNow);

        option!.Surcharges.Should().BeEmpty();
        option.TotalAmount.Should().Be(option.BaseAmount);
    }

    [Fact]
    public void A_typed_in_tariff_is_never_a_guaranteed_service()
    {
        // Whatever the card promises, it is not a service-level agreement the carrier signed.
        var card = Card(lanes: Lane(breaks: Break(0m, 100m)));

        RateCardPricer.Price(card, Anywhere, 10m, null, "E", null, 2, DateTime.UtcNow)!
            .IsGuaranteed.Should().BeFalse();
    }

    [Fact]
    public void Transit_days_give_an_estimated_delivery_and_their_absence_gives_none()
    {
        var card = Card(lanes: Lane(breaks: Break(0m, 100m)));
        var friday = new DateTime(2026, 9, 18, 16, 0, 0, DateTimeKind.Utc);

        RateCardPricer.Price(card, Anywhere, 10m, null, "E", null, 3, friday)!
            .EstimatedDelivery.Should().Be(new DateTime(2026, 9, 21));

        RateCardPricer.Price(card, Anywhere, 10m, null, "E", null, null, friday)!
            .EstimatedDelivery.Should().BeNull("a card that says nothing about transit must not guess");
    }

    [Fact]
    public void A_card_with_no_lane_for_this_consignment_prices_nothing_rather_than_zero()
    {
        var card = Card(lanes: Lane(destCountry: "AE", breaks: Break(0m, 100m)));

        RateCardPricer.Price(card, KarachiToLahore, 10m, null, "E", null, null, DateTime.UtcNow)
            .Should().BeNull();
    }
}
