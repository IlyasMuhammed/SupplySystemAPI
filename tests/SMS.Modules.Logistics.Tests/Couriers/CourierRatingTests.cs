using FluentAssertions;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Couriers.Simulator;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

// T-45 — RateAsync on ICourierProvider: asking a carrier what the carriage would cost.
public class CourierRatingTests
{
    private static CourierRateRequest Request(
        string? serviceCode = null,
        decimal? cod = null, string? codCurrency = null,
        DateTime? shipDate = null,
        IReadOnlyList<CourierPackage>? packages = null) =>
        new(
            ConsignmentNumber: "SHP-2026-00001",
            ServiceCode:       serviceCode,
            ShipFrom:          new CourierAddress("Warehouse", null, null, "12 Dock Road", null,
                                                  "Karachi", "Sindh", "74000", "PK"),
            ShipTo:            new CourierAddress("Acme Ltd", null, null, "4 Industrial Estate", null,
                                                  "Lahore", "Punjab", "54000", "PK"),
            // 40 × 30 × 20 = 24,000 cm³ — 4.8 kg volumetric at the simulator's 5000 divisor, so the
            // 12.5 kg on the scale is what gets charged.
            Packages:          packages ?? [new CourierPackage("HU-1", "BOX", 40m, 30m, 20m, 12.5m, 1500m)],
            FreightTerms:      "PREPAID",
            CodAmount:         cod,
            CodCurrency:       codCurrency,
            ShipDate:          shipDate,
            Credentials:       new Dictionary<string, string>());

    private static SimulatorCourierProvider Simulator() => new();
    private static ManualCourierProvider    Manual()    => new();

    private static CourierRateOption Option(CourierRateResult result, string serviceCode) =>
        result.Options.Single(o => o.ServiceCode == serviceCode);

    // ── The manual carrier ────────────────────────────────────────────────────

    [Fact]
    public async Task A_carrier_with_no_API_says_it_cannot_rate_and_says_where_the_price_comes_from()
    {
        // Not a silent empty success. A caller told "succeeded, no options" would conclude the
        // carrier priced this consignment at nothing.
        var result = await Manual().RateAsync(Request());

        result.Outcome.Should().Be(CourierOutcome.Unsupported);
        result.Options.Should().BeEmpty();
        result.Message.Should().ContainEquivalentOf("rate card");
    }

    [Fact]
    public void The_manual_carrier_does_not_claim_to_rate()
    {
        // The capability is what puts a "get a quote" button on the screen. Claiming it would give
        // the button nothing to come back with.
        Manual().Capabilities.SupportsRating.Should().BeFalse();
    }

    // ── What the simulator quotes ─────────────────────────────────────────────

    [Fact]
    public async Task Quoting_nothing_in_particular_prices_every_service_the_carrier_sells()
    {
        var result = await Simulator().RateAsync(Request());

        result.Succeeded.Should().BeTrue();
        result.Options.Select(o => o.ServiceCode)
              .Should().Equal(["SIM-ECONOMY", "SIM-EXPRESS", "SIM-OVERNIGHT"]);
    }

    [Fact]
    public async Task A_faster_service_costs_more_and_arrives_sooner()
    {
        var result = await Simulator().RateAsync(Request());

        var economy   = Option(result, "SIM-ECONOMY");
        var overnight = Option(result, "SIM-OVERNIGHT");

        overnight.TotalAmount.Should().BeGreaterThan(economy.TotalAmount);
        overnight.TransitDays.Should().BeLessThan(economy.TransitDays!.Value);
        overnight.IsGuaranteed.Should().BeTrue();
        economy.IsGuaranteed.Should().BeFalse();
    }

    [Fact]
    public async Task A_quote_says_what_weight_it_priced_on()
    {
        // Worth having even though T-44 works it out locally: where the two disagree, the carrier's
        // figure is the one that ends up on the invoice.
        var result = await Simulator().RateAsync(Request());

        Option(result, "SIM-ECONOMY").ChargeableWeightKg.Should().Be(12.5m);
    }

    [Fact]
    public async Task A_bulky_light_consignment_is_priced_on_its_volume()
    {
        // A metre cube of packing foam: 1,000,000 cm³ ÷ 5000 = 200 kg, against 5 kg on the scale.
        var result = await Simulator().RateAsync(Request(
            packages: [new CourierPackage("HU-1", "BOX", 100m, 100m, 100m, 5m, null)]));

        var economy = Option(result, "SIM-ECONOMY");

        economy.ChargeableWeightKg.Should().Be(200m);
        economy.BaseAmount.Should().Be(18_000m, "200 kg at 90/kg, not 5 kg at 90/kg");
    }

    [Fact]
    public async Task A_very_small_consignment_is_charged_the_services_minimum()
    {
        var result = await Simulator().RateAsync(Request(
            packages: [new CourierPackage("HU-1", "ENVELOPE", 10m, 10m, 10m, 0.1m, null)]));

        Option(result, "SIM-ECONOMY").BaseAmount
            .Should().Be(400m, "0.5 kg at 90/kg is 45, and the service will not bill below 400");
    }

    [Fact]
    public async Task Each_piece_is_priced_and_the_pieces_are_summed()
    {
        // Never the consolidated dimensions rated once — that lets two bulky parcels subsidise each
        // other and quotes below what the carrier invoices.
        var result = await Simulator().RateAsync(Request(packages:
        [
            new CourierPackage("HU-1", "BOX", 100m, 100m, 100m, 5m, null),   // 200 kg volumetric
            new CourierPackage("HU-2", "BOX", 100m, 100m, 100m, 5m, null)    // 200 kg volumetric
        ]));

        Option(result, "SIM-ECONOMY").ChargeableWeightKg.Should().Be(400m);
    }

    // ── Surcharges ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fuel_is_quoted_as_its_own_line_rather_than_buried_in_the_total()
    {
        // Phase 4 compares an itemised carrier invoice against this. A total on its own can say
        // that the two disagree, never where.
        var result = await Simulator().RateAsync(Request());

        var economy = Option(result, "SIM-ECONOMY");

        economy.BaseAmount.Should().Be(1125m, "12.5 kg at 90/kg");
        economy.Surcharges.Should().ContainSingle(s => s.Code == "FUEL")
               .Which.Amount.Should().Be(135m, "12% of the base rate");
        economy.TotalAmount.Should().Be(1260m);
    }

    [Fact]
    public async Task Cash_on_delivery_adds_a_handling_charge_and_nothing_else_does()
    {
        var withoutCod = await Simulator().RateAsync(Request());
        var withCod    = await Simulator().RateAsync(Request(cod: 20_000m, codCurrency: "PKR"));

        Option(withoutCod, "SIM-ECONOMY").Surcharges!.Select(s => s.Code).Should().Equal(["FUEL"]);

        var codded = Option(withCod, "SIM-ECONOMY");
        codded.Surcharges!.Select(s => s.Code).Should().Equal(["FUEL", "COD"]);
        codded.Surcharges!.Single(s => s.Code == "COD").Amount.Should().Be(300m, "1.5% of 20,000");
        codded.TotalAmount.Should().Be(1560m, "1125 base + 135 fuel + 300 COD");
    }

    [Fact]
    public async Task A_cash_on_delivery_amount_with_no_currency_is_refused()
    {
        var result = await Simulator().RateAsync(Request(cod: 5000m, codCurrency: null));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().ContainEquivalentOf("currency");
    }

    // ── Naming a service ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("SIM-EXPRESS")]
    [InlineData("sim-express")]
    [InlineData("  SIM-EXPRESS  ")]
    public async Task Naming_a_service_quotes_only_that_one_however_it_was_typed(string code)
    {
        var result = await Simulator().RateAsync(Request(serviceCode: code));

        result.Options.Should().ContainSingle();
        result.Options[0].ServiceCode.Should().Be("SIM-EXPRESS");
        result.Options[0].TotalAmount.Should().Be(1680m, "1500 base + 180 fuel");
    }

    [Fact]
    public async Task A_service_this_carrier_does_not_sell_is_refused_and_the_message_lists_what_it_does()
    {
        var result = await Simulator().RateAsync(Request(serviceCode: "NEXT-FLIGHT-OUT"));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Options.Should().BeEmpty();
        result.Message.Should().Contain("SIM-ECONOMY").And.Contain("SIM-OVERNIGHT");
    }

    // ── Scenarios ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refusal_scenario_refuses_the_quote()
    {
        var result = await Simulator().RateAsync(Request(serviceCode: "SIM-REFUSE"));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Options.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_scenario_comes_back_as_failed_rather_than_refused()
    {
        // The distinction the whole outcome enum exists for. On a rate call it matters less than on
        // a booking — nothing was created either way — but an adapter that collapsed the two here
        // would be modelling them as the same thing.
        var result = await Simulator().RateAsync(Request(serviceCode: "SIM-FAIL"));

        result.Outcome.Should().Be(CourierOutcome.Failed);
        result.Options.Should().BeEmpty();
    }

    [Fact]
    public async Task A_journey_scenario_is_not_a_service_and_still_quotes_the_whole_list()
    {
        // SIM-DELIVERED says what happens after booking. It has nothing to do with price.
        var result = await Simulator().RateAsync(Request(serviceCode: "SIM-DELIVERED"));

        result.Succeeded.Should().BeTrue();
        result.Options.Should().HaveCount(3);
    }

    // ── Dates ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Estimated_delivery_runs_from_the_ship_date_it_was_given()
    {
        var friday = new DateTime(2026, 9, 18, 14, 30, 0, DateTimeKind.Utc);

        var result = await Simulator().RateAsync(Request(shipDate: friday));

        Option(result, "SIM-OVERNIGHT").EstimatedDelivery.Should().Be(new DateTime(2026, 9, 19));
        Option(result, "SIM-ECONOMY").EstimatedDelivery.Should().Be(new DateTime(2026, 9, 22));
    }

    // ── It is a read ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Rating_commits_nothing_and_hands_back_no_airway_bill()
    {
        // There is no airway bill field on a rate result at all, and that is the point: rating
        // cannot accidentally become booking. What it can be is repeated, freely.
        var simulator = Simulator();

        var first  = await simulator.RateAsync(Request());
        var second = await simulator.RateAsync(Request());

        first.Options.Select(o => o.TotalAmount)
             .Should().Equal(second.Options.Select(o => o.TotalAmount));
    }

    [Fact]
    public async Task A_booking_costs_what_the_same_service_was_quoted()
    {
        // One pricing model. A consignment quoted and then booked coming back with two different
        // numbers is the kind of discrepancy nobody can explain to a supplier.
        var simulator = Simulator();

        var quote = await simulator.RateAsync(Request(serviceCode: "SIM-EXPRESS"));

        var booking = await simulator.BookAsync(new CourierBookingRequest(
            IdempotencyKey:    Guid.NewGuid().ToString("N"),
            ConsignmentNumber: "SHP-2026-00001",
            ServiceCode:       "SIM-EXPRESS",
            ShipFrom:          Request().ShipFrom,
            ShipTo:            Request().ShipTo,
            Packages:          Request().Packages,
            FreightTerms:      "PREPAID",
            CodAmount:         null,
            CodCurrency:       null,
            PickupWindowStart: null,
            PickupWindowEnd:   null,
            ReferenceNumbers:  [],
            Credentials:       new Dictionary<string, string>()));

        booking.Succeeded.Should().BeTrue();
        booking.Cost.Should().Be(quote.Options[0].TotalAmount);
        booking.CostCurrency.Should().Be("PKR");
    }

    [Fact]
    public async Task The_booked_cost_is_in_the_tariffs_currency_not_the_cash_collected_on_delivery()
    {
        // Collecting cash in dollars says nothing about what the carriage is billed in, and taking
        // the COD currency here would label a rupee figure as dollars.
        var booking = await Simulator().BookAsync(new CourierBookingRequest(
            IdempotencyKey:    Guid.NewGuid().ToString("N"),
            ConsignmentNumber: "SHP-2026-00001",
            ServiceCode:       "SIM-EXPRESS",
            ShipFrom:          Request().ShipFrom,
            ShipTo:            Request().ShipTo,
            Packages:          Request().Packages,
            FreightTerms:      "PREPAID",
            CodAmount:         500m,
            CodCurrency:       "USD",
            PickupWindowStart: null,
            PickupWindowEnd:   null,
            ReferenceNumbers:  [],
            Credentials:       new Dictionary<string, string>()));

        booking.CostCurrency.Should().Be("PKR");
    }
}
