using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Rating;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Rating;

// T-46 — configuring a negotiated tariff, and quoting from it.
public class RateCardTests
{
    private const int User = 42;

    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jun = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(LogisticsDbContext Db, RateCardService Cards, string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        return new Harness(db, new RateCardService(new RateCardRepository(db)), dbName);
    }

    private static async Task<Guid> NewCarrier(Harness h, string name = "Simcourier")
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = name, Code = $"C{Guid.NewGuid():N}"[..6], IsActive = true
        };

        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return carrier.UUID;
    }

    private static RateCardLaneRequest LaneReq(
        string? destCountry = null, string? destPrefix = null, string? name = null,
        params RateCardBreakRequest[] breaks) =>
        new()
        {
            Name = name, DestinationCountryIso = destCountry, DestinationPostcodePrefix = destPrefix,
            Breaks = [.. breaks.Length > 0 ? breaks : [BreakReq(0m, 100m)]]
        };

    private static RateCardBreakRequest BreakReq(decimal from, decimal amount, string basis = "PER_KG") =>
        new() { FromWeightKg = from, Amount = amount, Basis = basis };

    private static CreateRateCardRequest CardReq(
        Guid carrier, string? serviceCode = null, string name = "Domestic 2026",
        DateTime? from = null, DateTime? to = null,
        decimal? minimum = null, decimal? fuel = null,
        params RateCardLaneRequest[] lanes) =>
        new()
        {
            CarrierUuid          = carrier,
            ServiceCode          = serviceCode,
            Name                 = name,
            Currency             = "PKR",
            EffectiveFrom        = from ?? Jan,
            EffectiveTo          = to,
            MinimumCharge        = minimum,
            FuelSurchargePercent = fuel,
            Lanes                = [.. lanes.Length > 0 ? lanes : [LaneReq(name: "Anywhere")]]
        };

    private static RateCardQuoteRequest QuoteReq(
        Guid carrier, decimal weight = 10m, string? serviceCode = null,
        string? destCountry = "PK", string? destPostcode = "54000",
        decimal? cod = null, int? transitDays = null, DateTime? on = null) =>
        new(carrier, serviceCode, weight, "PK", "74000", destCountry, destPostcode, cod, transitDays, on ?? Jun);

    // ── Creating ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_card_holds_its_lanes_and_their_weight_breaks()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Cards.CreateAsync(CardReq(carrier, fuel: 12m, minimum: 500m, lanes:
        [
            LaneReq(name: "Anywhere", breaks: [BreakReq(0m, 200m), BreakReq(5m, 150m), BreakReq(30m, 110m)]),
            LaneReq("PK", "54", "Lahore", BreakReq(0m, 180m))
        ]), User);

        var card = await h.Cards.GetByUuidAsync(uuid, Jun);

        card!.Name.Should().Be("Domestic 2026");
        card.Currency.Should().Be("PKR");
        card.FuelSurchargePercent.Should().Be(12m);
        card.MinimumCharge.Should().Be(500m);
        card.IsInEffect.Should().BeTrue();
        card.CarrierName.Should().Be("Simcourier");

        card.Lanes.Should().HaveCount(2);
        card.Lanes.Single(l => l.Name == "Anywhere").Breaks
            .Select(b => b.FromWeightKg).Should().Equal([0m, 5m, 30m]);
    }

    [Fact]
    public async Task A_card_needs_a_name_a_currency_and_an_existing_carrier()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var noName = async () => await h.Cards.CreateAsync(CardReq(carrier, name: "  "), User);
        (await noName.Should().ThrowAsync<BadRequestException>()).WithMessage("*needs a name*");

        var badCurrency = CardReq(carrier);
        badCurrency.Currency = "RUPEES";
        var wrongCurrency = async () => await h.Cards.CreateAsync(badCurrency, User);
        (await wrongCurrency.Should().ThrowAsync<BadRequestException>()).WithMessage("*three-letter ISO*");

        var noCarrier = async () => await h.Cards.CreateAsync(CardReq(Guid.NewGuid()), User);
        await noCarrier.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task A_card_with_no_lanes_prices_nothing_and_is_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var req = CardReq(carrier);
        req.Lanes = [];

        var act = async () => await h.Cards.CreateAsync(req, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*prices nothing*");
    }

    [Fact]
    public async Task A_lane_with_no_weight_breaks_is_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var lane = LaneReq(name: "Empty");
        lane.Breaks = [];

        var act = async () => await h.Cards.CreateAsync(CardReq(carrier, lanes: lane), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*no weight breaks*");
    }

    [Fact]
    public async Task Weight_breaks_must_start_at_zero()
    {
        // A lane whose lowest break is above zero has a hole under it, and a consignment falling
        // into that hole prices at nothing at all while every other card looks fine.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Cards.CreateAsync(
            CardReq(carrier, lanes: LaneReq(name: "Gappy", breaks: [BreakReq(5m, 150m), BreakReq(30m, 110m)])),
            User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*must start at 0*")
            .WithMessage("*falls through the card*");
    }

    [Fact]
    public async Task Two_breaks_cannot_start_at_the_same_weight()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Cards.CreateAsync(
            CardReq(carrier, lanes: LaneReq(name: "L", breaks: [BreakReq(0m, 200m), BreakReq(5m, 150m), BreakReq(5m, 140m)])),
            User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*One weight, one rate*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-150)]
    public async Task A_rate_has_to_be_a_positive_amount(decimal amount)
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Cards.CreateAsync(
            CardReq(carrier, lanes: LaneReq(name: "L", breaks: [BreakReq(0m, amount)])), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*is not a rate*");
    }

    [Fact]
    public async Task An_unknown_rate_basis_is_refused_and_the_message_lists_the_real_ones()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Cards.CreateAsync(
            CardReq(carrier, lanes: LaneReq(name: "L", breaks: [BreakReq(0m, 100m, "PER_POUND")])), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*PER_KG*").WithMessage("*FLAT*");
    }

    [Fact]
    public async Task Two_lanes_matching_on_identical_criteria_are_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Cards.CreateAsync(CardReq(carrier, lanes:
        [
            LaneReq("PK", "54", "Lahore"),
            LaneReq("pk", "54", "Lahore again")
        ]), User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*depend on row order*");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task A_surcharge_percentage_outside_nought_to_a_hundred_is_refused(decimal percent)
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Cards.CreateAsync(CardReq(carrier, fuel: percent), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*between 0 and 100*");
    }

    [Fact]
    public async Task A_card_cannot_stop_applying_before_it_starts()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Cards.CreateAsync(CardReq(carrier, from: Jun, to: Jan), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── One card per carrier, service and period ──────────────────────────────

    [Fact]
    public async Task Two_cards_that_could_price_the_same_day_are_refused()
    {
        // Otherwise the price depends on which row was read first.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Cards.CreateAsync(CardReq(carrier, name: "2026", from: Jan), User);

        var act = async () => await h.Cards.CreateAsync(CardReq(carrier, name: "Also 2026", from: Jun), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already prices every service*")
            .WithMessage("*depend on which row was read first*");
    }

    [Fact]
    public async Task Consecutive_periods_are_fine()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Cards.CreateAsync(
            CardReq(carrier, name: "H1", from: Jan, to: new DateTime(2026, 5, 31)), User);

        var act = async () => await h.Cards.CreateAsync(CardReq(carrier, name: "H2", from: Jun), User);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_service_specific_card_may_sit_alongside_the_carrier_wide_one()
    {
        // They are not competitors: the general card is the fallback.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Cards.CreateAsync(CardReq(carrier, name: "All services"), User);

        var act = async () => await h.Cards.CreateAsync(
            CardReq(carrier, serviceCode: "ECON", name: "Economy only"), User);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Two_cards_for_the_same_service_still_clash()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Cards.CreateAsync(CardReq(carrier, serviceCode: "ECON", name: "Economy"), User);

        var act = async () => await h.Cards.CreateAsync(
            CardReq(carrier, serviceCode: "econ", name: "Economy again"), User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*service 'econ'*");
    }

    [Fact]
    public async Task Different_carriers_do_not_clash()
    {
        var h = NewHarness();
        var one = await NewCarrier(h, "Carrier One");
        var two = await NewCarrier(h, "Carrier Two");

        await h.Cards.CreateAsync(CardReq(one), User);

        var act = async () => await h.Cards.CreateAsync(CardReq(two), User);

        await act.Should().NotThrowAsync();
    }

    // ── Quoting ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_card_quotes_in_the_same_shape_a_carrier_would()
    {
        // Which is the point: a caller pricing from a card and a caller pricing from an API are the
        // same caller.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Cards.CreateAsync(CardReq(carrier, fuel: 12m, lanes:
            LaneReq(name: "Anywhere", breaks: [BreakReq(0m, 200m), BreakReq(5m, 150m)])), User);

        var quote = await h.Cards.QuoteAsync(QuoteReq(carrier, weight: 10m, transitDays: 3));

        quote.Quoted.Should().BeTrue();
        quote.CardName.Should().Be("Domestic 2026");
        quote.LaneName.Should().Be("Anywhere");

        var option = quote.Option!;
        option.BaseAmount.Should().Be(1500m, "10 kg at the 5 kg break of 150");
        option.Surcharges!.Single(s => s.Code == "FUEL").Amount.Should().Be(180m);
        option.TotalAmount.Should().Be(1680m);
        option.Currency.Should().Be("PKR");
        option.EstimatedDelivery.Should().Be(new DateTime(2026, 6, 4));
    }

    [Fact]
    public async Task A_service_specific_card_beats_the_carrier_wide_one()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Cards.CreateAsync(CardReq(carrier, name: "General", lanes:
            LaneReq(name: "Anywhere", breaks: [BreakReq(0m, 100m)])), User);
        await h.Cards.CreateAsync(CardReq(carrier, serviceCode: "ECON", name: "Economy", lanes:
            LaneReq(name: "Anywhere", breaks: [BreakReq(0m, 80m)])), User);

        (await h.Cards.QuoteAsync(QuoteReq(carrier, serviceCode: "ECON"))).CardName.Should().Be("Economy");

        // A service with no card of its own falls back to the general one rather than to nothing.
        (await h.Cards.QuoteAsync(QuoteReq(carrier, serviceCode: "OVERNIGHT"))).CardName.Should().Be("General");
    }

    [Fact]
    public async Task Last_years_consignment_prices_at_last_years_rates()
    {
        // The whole of what makes an old carrier invoice checkable.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Cards.CreateAsync(CardReq(carrier, name: "2025", from: new DateTime(2025, 1, 1),
            to: new DateTime(2025, 12, 31), lanes: LaneReq(name: "A", breaks: [BreakReq(0m, 80m)])), User);
        await h.Cards.CreateAsync(CardReq(carrier, name: "2026", from: Jan,
            lanes: LaneReq(name: "A", breaks: [BreakReq(0m, 100m)])), User);

        (await h.Cards.QuoteAsync(QuoteReq(carrier, on: new DateTime(2025, 7, 1))))
            .Option!.BaseAmount.Should().Be(800m);

        (await h.Cards.QuoteAsync(QuoteReq(carrier, on: Jun)))
            .Option!.BaseAmount.Should().Be(1000m);
    }

    [Fact]
    public async Task A_date_no_card_covers_is_reported_as_having_no_card()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Cards.CreateAsync(CardReq(carrier, from: Jan, to: new DateTime(2026, 3, 31)), User);

        var quote = await h.Cards.QuoteAsync(QuoteReq(carrier, on: Jun));

        quote.Status.Should().Be(RateCardQuoteStatus.NoCard);
        quote.Option.Should().BeNull("no price and a price of nothing are opposite things");
        quote.Explanation.Should().Contain("2026-06-01");
    }

    [Fact]
    public async Task A_deactivated_card_does_not_quote()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Cards.CreateAsync(CardReq(carrier), User);
        await h.Cards.PatchAsync(uuid, new PatchRateCardRequest { IsActive = false }, User);

        (await h.Cards.QuoteAsync(QuoteReq(carrier))).Status.Should().Be(RateCardQuoteStatus.NoCard);
    }

    [Fact]
    public async Task A_destination_no_lane_covers_is_reported_as_having_no_lane()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Cards.CreateAsync(CardReq(carrier, lanes: LaneReq("PK", "54", "Lahore only")), User);

        var quote = await h.Cards.QuoteAsync(QuoteReq(carrier, destCountry: "AE", destPostcode: "00000"));

        quote.Status.Should().Be(RateCardQuoteStatus.NoLane);
        quote.CardName.Should().Be("Domestic 2026");
        quote.Explanation.Should().Contain("AE 00000");
    }

    [Fact]
    public async Task An_unknown_carrier_is_reported_rather_than_priced()
    {
        var h = NewHarness();

        (await h.Cards.QuoteAsync(QuoteReq(Guid.NewGuid()))).Status
            .Should().Be(RateCardQuoteStatus.NoCarrier);
    }

    // ── Editing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sending_lanes_replaces_the_whole_tariff()
    {
        // Edited as a whole so the card never spends a moment with a hole in it.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Cards.CreateAsync(CardReq(carrier, lanes:
            LaneReq(name: "Old", breaks: [BreakReq(0m, 100m)])), User);

        await h.Cards.PatchAsync(uuid, new PatchRateCardRequest
        {
            Lanes = [LaneReq(name: "New", breaks: [BreakReq(0m, 250m), BreakReq(10m, 200m)])]
        }, User);

        var card = await h.Cards.GetByUuidAsync(uuid, Jun);

        card!.Lanes.Should().ContainSingle().Which.Name.Should().Be("New");
        (await h.Cards.QuoteAsync(QuoteReq(carrier, weight: 20m))).Option!.BaseAmount.Should().Be(4000m);
    }

    [Fact]
    public async Task A_replacement_tariff_is_validated_like_a_new_one()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Cards.CreateAsync(CardReq(carrier), User);

        var act = async () => await h.Cards.PatchAsync(uuid, new PatchRateCardRequest
        {
            Lanes = [LaneReq(name: "Gappy", breaks: [BreakReq(5m, 150m)])]
        }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_term_can_be_cleared_back_to_nothing()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Cards.CreateAsync(
            CardReq(carrier, from: Jan, to: new DateTime(2026, 12, 31), minimum: 500m, fuel: 12m), User);

        await h.Cards.PatchAsync(uuid, new PatchRateCardRequest
        {
            ClearTerms = ["fuel_surcharge", "MINIMUM_CHARGE", "EFFECTIVE_TO"]
        }, User);

        var card = await h.Cards.GetByUuidAsync(uuid, Jun);

        card!.FuelSurchargePercent.Should().BeNull();
        card.MinimumCharge.Should().BeNull();
        card.EffectiveTo.Should().BeNull();
    }

    [Fact]
    public async Task Clearing_something_that_is_not_a_term_is_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Cards.CreateAsync(CardReq(carrier), User);

        var act = async () => await h.Cards.PatchAsync(
            uuid, new PatchRateCardRequest { ClearTerms = ["DISCOUNT"] }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*DISCOUNT*").WithMessage("*FUEL_SURCHARGE*");
    }

    [Fact]
    public async Task Extending_a_card_into_anothers_period_is_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var first = await h.Cards.CreateAsync(
            CardReq(carrier, name: "H1", from: Jan, to: new DateTime(2026, 5, 31)), User);
        await h.Cards.CreateAsync(CardReq(carrier, name: "H2", from: Jun), User);

        var act = async () => await h.Cards.PatchAsync(
            first, new PatchRateCardRequest { ClearTerms = ["EFFECTIVE_TO"] }, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*H2*");
    }

    [Fact]
    public async Task A_removed_card_is_kept_so_an_old_price_stays_explainable()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Cards.CreateAsync(CardReq(carrier), User);

        (await h.Cards.DeleteAsync(uuid, User)).Should().BeTrue();

        (await h.Cards.GetForCarrierAsync(carrier)).Should().BeEmpty();
        (await h.Cards.QuoteAsync(QuoteReq(carrier))).Status.Should().Be(RateCardQuoteStatus.NoCard);
        (await h.Db.RateCards.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_removed_cards_period_is_free_again()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Cards.CreateAsync(CardReq(carrier, name: "Wrong"), User);
        await h.Cards.DeleteAsync(uuid, User);

        var act = async () => await h.Cards.CreateAsync(CardReq(carrier, name: "Right"), User);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unknown_card_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Cards.GetByUuidAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Cards.PatchAsync(Guid.NewGuid(), new PatchRateCardRequest(), User)).Should().BeFalse();
        (await h.Cards.DeleteAsync(Guid.NewGuid(), User)).Should().BeFalse();
        (await h.Cards.GetForCarrierAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task Another_organizations_card_does_not_exist_here()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Cards.CreateAsync(CardReq(carrier), User);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other   = new RateCardService(new RateCardRepository(otherDb));

        (await other.GetByUuidAsync(uuid)).Should().BeNull();
        (await other.QuoteAsync(QuoteReq(carrier))).Status.Should().Be(RateCardQuoteStatus.NoCarrier);
    }
}
