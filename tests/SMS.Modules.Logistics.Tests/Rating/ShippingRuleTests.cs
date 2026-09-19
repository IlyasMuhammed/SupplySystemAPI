using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Rating;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Tests.Couriers;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Rating;

// T-49 — standing decisions about how goods ship, applied automatically and audited.
public class ShippingRuleTests
{
    private const int User = 42;
    private static readonly DateTime T0  = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ShipDate = new(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        LogisticsDbContext Db,
        ShippingRuleService Rules,
        RateCardService Cards,
        CarrierServiceRepository Services,
        string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();

        var api      = new ScriptedCourierProvider("SCRIPTED", rating: false);
        var registry = new CourierProviderRegistry([new ManualCourierProvider(), api]);
        var accounts = new CarrierAccountResolver(db, registry, new CarrierCredentialVault(db, TestEncryption.New()));
        var services = new CarrierServiceRepository(db);
        var cards    = new RateCardService(new RateCardRepository(db));
        var weights  = new ChargeableWeightService(db, services);
        var rating   = new ConsignmentRatingService(db, weights, accounts, cards);
        var shopping = new RateShoppingService(db, weights, accounts, services, cards, rating);

        return new Harness(
            db,
            new ShippingRuleService(db, new ShippingRuleRepository(db), weights, shopping, services),
            cards, services, dbName);
    }

    private static async Task<Guid> NewCarrier(Harness h, string name)
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

    private static Task<Guid> NewService(
        Harness h, Guid carrier, string code, string name, int? transitDays = null,
        bool supportsHazardous = false) =>
        h.Services.CreateAsync(new CreateCarrierServiceRequest
        {
            CarrierUuid = carrier, ServiceCode = code, ServiceName = name,
            TransitDays = transitDays, SupportsHazardous = supportsHazardous
        }, User);

    private static Task<Guid> NewCard(Harness h, Guid carrier, decimal perKg) =>
        h.Cards.CreateAsync(new CreateRateCardRequest
        {
            CarrierUuid = carrier, Name = $"Tariff {Guid.NewGuid():N}"[..12], Currency = "PKR",
            EffectiveFrom = Jan,
            Lanes =
            [
                new RateCardLaneRequest
                {
                    Name = "Anywhere",
                    Breaks = [new RateCardBreakRequest { FromWeightKg = 0m, Amount = perKg, Basis = "PER_KG" }]
                }
            ]
        }, User);

    private static CreateShippingRuleRequest RuleReq(
        string name, int priority, Guid? carrier = null, string? serviceCode = null,
        decimal? minWeight = null, decimal? maxWeight = null,
        string? destCountry = null, string? destPrefix = null,
        decimal? minValue = null, decimal? maxValue = null,
        bool? hazardous = null, bool? cod = null, string? strategy = null) =>
        new()
        {
            Name = name, Priority = priority, CarrierUuid = carrier, ServiceCode = serviceCode,
            MinChargeableWeightKg = minWeight, MaxChargeableWeightKg = maxWeight,
            DestinationCountryIso = destCountry, DestinationPostcodePrefix = destPrefix,
            MinDeclaredValue = minValue, MaxDeclaredValue = maxValue,
            AppliesToHazardous = hazardous, AppliesToCod = cod, Strategy = strategy
        };

    /// <summary>One 12.5 kg package worth 1,500, Karachi to Lahore.</summary>
    private static async Task<Guid> NewConsignment(
        Harness h, bool hazardous = false, decimal? cod = null, decimal? declaredValue = 1500m,
        string destPostcode = "54000")
    {
        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), DeliveryNumber = $"DLV-2026-{Random.Shared.Next(1, 99_999):D5}",
            Direction  = LogisticsCode.Of(DeliveryDirection.Outbound),
            SourceType = LogisticsCode.Of(DeliverySourceType.Manual),
            CreatedBy  = User, CreatedDate = T0
        };
        delivery.Lines.Add(new DeliveryOrderLine
        {
            UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "Goods",
            QtyOrdered = 1m, UnitOfMeasure = "EA", IsHazardous = hazardous,
            CreatedBy = User, CreatedDate = T0
        });
        delivery.Packages.Add(new ShipmentPackage
        {
            UUID = Guid.NewGuid(), PackageBarcode = $"PKG-{Guid.NewGuid():N}"[..10],
            GrossWeightKg = 12.5m, LengthCm = 40m, WidthCm = 30m, HeightCm = 20m,
            DeclaredValue = declaredValue, CreatedBy = User, CreatedDate = T0
        });

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CodAmount = cod, CodCurrency = cod is null ? null : "PKR",
            ShipFromAddress = Address("12 Dock Road", "Karachi", "74000"),
            ShipToAddress   = Address("4 Industrial Estate", "Lahore", destPostcode),
            CreatedBy = User, CreatedDate = T0
        };
        consignment.Deliveries.Add(new ConsignmentDelivery
        {
            UUID = Guid.NewGuid(), DeliveryOrder = delivery, Sequence = 1, CreatedBy = User, CreatedDate = T0
        });

        h.Db.Consignments.Add(consignment);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return consignment.UUID;
    }

    private static Address Address(string line1, string city, string postcode) => new()
    {
        UUID = Guid.NewGuid(), Line1 = line1, CityName = city, CountryName = "Pakistan",
        CountryIsoCode = "PK", PostalCode = postcode, ContactName = "Receiving",
        ContactPhone = "+923001234567", CreatedBy = User, CreatedDate = T0
    };

    // ── Configuring ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_rule_reads_back_as_a_sentence()
    {
        // A rule list that has to be decoded column by column is a rule list nobody audits.
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");

        var uuid = await h.Rules.CreateAsync(
            RuleReq("Light domestic", 10, carrier, "ROAD", maxWeight: 5m, destCountry: "PK", destPrefix: "54"),
            User);

        var rule = await h.Rules.GetByUuidAsync(uuid);

        rule!.Summary.Should().Be("Up to 5 kg, to PK 54 → Beta Road ROAD.");
        rule.Strategy.Should().Be("CHEAPEST", "a rule that names no strategy still has to pick");
    }

    [Fact]
    public async Task A_rule_with_no_conditions_reads_as_a_catch_all()
    {
        var h = NewHarness();

        var uuid = await h.Rules.CreateAsync(RuleReq("Everything else", 99, strategy: "FASTEST"), User);

        (await h.Rules.GetByUuidAsync(uuid))!.Summary
            .Should().Be("Anything → the fastest carrier.");
    }

    [Fact]
    public async Task Two_rules_cannot_share_a_priority()
    {
        var h = NewHarness();
        await h.Rules.CreateAsync(RuleReq("First", 10), User);

        var act = async () => await h.Rules.CreateAsync(RuleReq("Second", 10), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already priority 10*")
            .WithMessage("*parcel on the wrong carrier*");
    }

    [Fact]
    public async Task A_rule_that_matches_nothing_at_all_is_refused()
    {
        var h = NewHarness();

        var weights = async () => await h.Rules.CreateAsync(
            RuleReq("Impossible", 10, minWeight: 30m, maxWeight: 5m), User);
        (await weights.Should().ThrowAsync<BadRequestException>()).WithMessage("*nothing at all*");

        var values = async () => await h.Rules.CreateAsync(
            RuleReq("Also impossible", 11, minValue: 5000m, maxValue: 100m), User);
        await values.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_service_without_a_carrier_is_refused()
    {
        // The same code means different things at two carriers, so it resolves against nothing.
        var h = NewHarness();

        var act = async () => await h.Rules.CreateAsync(RuleReq("Orphan", 10, serviceCode: "EXPRESS"), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*without a carrier*");
    }

    [Fact]
    public async Task An_unknown_strategy_is_refused_and_the_message_lists_the_real_ones()
    {
        var h = NewHarness();

        var act = async () => await h.Rules.CreateAsync(RuleReq("Odd", 10, strategy: "GREENEST"), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*CHEAPEST*").WithMessage("*FASTEST*");
    }

    [Fact]
    public async Task A_condition_can_be_cleared_back_to_not_caring()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");

        var uuid = await h.Rules.CreateAsync(
            RuleReq("Narrow", 10, carrier, "ROAD", maxWeight: 5m, hazardous: true), User);

        await h.Rules.PatchAsync(uuid, new PatchShippingRuleRequest
        {
            ClearConditions = ["max_weight", "HAZARDOUS", "SERVICE"]
        }, User);

        var rule = await h.Rules.GetByUuidAsync(uuid);

        rule!.MaxChargeableWeightKg.Should().BeNull();
        rule.AppliesToHazardous.Should().BeNull();
        rule.ServiceCode.Should().BeNull();
        rule.CarrierName.Should().Be("Beta Road", "only the service was cleared");
    }

    [Fact]
    public async Task Clearing_the_carrier_clears_the_service_with_it()
    {
        // A service code with no carrier resolves against nothing, and leaving one behind would
        // fail validation on the very next save.
        var h = NewHarness();
        var carrier = await NewCarrier(h, "Beta Road");
        var uuid = await h.Rules.CreateAsync(RuleReq("Narrow", 10, carrier, "ROAD"), User);

        await h.Rules.PatchAsync(uuid, new PatchShippingRuleRequest { ClearConditions = ["CARRIER"] }, User);

        var rule = await h.Rules.GetByUuidAsync(uuid);
        rule!.CarrierUuid.Should().BeNull();
        rule.ServiceCode.Should().BeNull();
    }

    [Fact]
    public async Task Clearing_something_that_is_not_a_condition_is_refused()
    {
        var h = NewHarness();
        var uuid = await h.Rules.CreateAsync(RuleReq("Rule", 10), User);

        var act = async () => await h.Rules.PatchAsync(
            uuid, new PatchShippingRuleRequest { ClearConditions = ["COLOUR"] }, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*MIN_WEIGHT*");
    }

    [Fact]
    public async Task A_removed_rules_priority_is_free_again()
    {
        // Deletion is soft, so without a filtered index the database would refuse a rule the
        // repository had already accepted.
        var h = NewHarness();
        var uuid = await h.Rules.CreateAsync(RuleReq("Wrong", 10), User);

        await h.Rules.DeleteAsync(uuid, User);

        var act = async () => await h.Rules.CreateAsync(RuleReq("Right", 10), User);
        await act.Should().NotThrowAsync();

        (await h.Rules.GetAllAsync()).Should().ContainSingle().Which.Name.Should().Be("Right");
    }

    // ── Which rule fires ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_matching_rule_wins_and_says_why()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        var air  = await NewCarrier(h, "Alpha Air");
        await NewCard(h, road, 60m);
        await NewCard(h, air, 200m);

        await h.Rules.CreateAsync(RuleReq("Featherweight", 10, air, maxWeight: 2m), User);
        await h.Rules.CreateAsync(RuleReq("Domestic parcels", 20, road, maxWeight: 30m, destCountry: "PK"), User);
        await h.Rules.CreateAsync(RuleReq("Everything else", 99, air), User);

        var consignment = await NewConsignment(h);

        var decision = await h.Rules.EvaluateAsync(consignment, ShipDate);

        decision!.MatchedRule!.Name.Should().Be("Domestic parcels");
        decision.MatchedRule.Reason.Should()
            .Contain("Every condition holds").And
            .Contain("12.5 kg is at most 30 kg").And
            .Contain("destination country is PK");

        decision.Selection.Should().Be("Beta Road, cheapest of its services.");
        decision.Recommended!.CarrierName.Should().Be("Beta Road");
        decision.Recommended.TotalAmount.Should().Be(750m);
    }

    [Fact]
    public async Task Every_rule_that_did_not_match_says_why_not()
    {
        // A decision that names only the winner leaves somebody reading conditions in a database at
        // the moment a parcel has gone out on the wrong carrier.
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);

        await h.Rules.CreateAsync(RuleReq("Featherweight", 10, road, maxWeight: 2m), User);
        await h.Rules.CreateAsync(RuleReq("Export only", 20, road, destCountry: "AE"), User);
        await h.Rules.CreateAsync(RuleReq("Everything else", 99, road), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate);

        decision!.Considered.Should().HaveCount(3);
        decision.Considered[0].Matched.Should().BeFalse();
        decision.Considered[0].Reason.Should().Contain("12.5 kg is at most 2 kg").And.Contain("not true of this");
        decision.Considered[1].Reason.Should().Contain("destination country is AE");
        decision.Considered[2].Matched.Should().BeTrue();
    }

    [Fact]
    public async Task Rules_after_the_winner_are_not_even_looked_at()
    {
        // Which is what priority is for.
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);

        await h.Rules.CreateAsync(RuleReq("Catch-all", 10, road), User);
        await h.Rules.CreateAsync(RuleReq("Never reached", 20, road), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate);

        decision!.Considered.Should().ContainSingle().Which.Name.Should().Be("Catch-all");
    }

    [Fact]
    public async Task A_rule_with_no_conditions_matches_everything_and_says_so()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);
        await h.Rules.CreateAsync(RuleReq("Catch-all", 10, road), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate);

        decision!.MatchedRule!.Reason.Should().Be("It states no conditions, so it applies to everything.");
    }

    [Fact]
    public async Task An_inactive_rule_is_not_tried()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);

        var off = await h.Rules.CreateAsync(RuleReq("Switched off", 10, road), User);
        await h.Rules.PatchAsync(off, new PatchShippingRuleRequest { IsActive = false }, User);
        await h.Rules.CreateAsync(RuleReq("Live", 20, road), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate);

        decision!.MatchedRule!.Name.Should().Be("Live");
    }

    [Theory]
    [InlineData(true,  "Dangerous goods")]
    [InlineData(false, "Ordinary goods")]
    public async Task Hazardous_and_non_hazardous_consignments_take_different_rules(
        bool hazardous, string expected)
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);

        await h.Rules.CreateAsync(RuleReq("Dangerous goods", 10, road, hazardous: true), User);
        await h.Rules.CreateAsync(RuleReq("Ordinary goods", 20, road, hazardous: false), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h, hazardous: hazardous), ShipDate);

        decision!.MatchedRule!.Name.Should().Be(expected);
        decision.IsHazardous.Should().Be(hazardous);
    }

    [Fact]
    public async Task Cash_on_delivery_can_route_a_consignment_of_its_own()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);

        await h.Rules.CreateAsync(RuleReq("Cash jobs", 10, road, cod: true), User);
        await h.Rules.CreateAsync(RuleReq("Everything else", 20, road), User);

        (await h.Rules.EvaluateAsync(await NewConsignment(h, cod: 5000m), ShipDate))!
            .MatchedRule!.Name.Should().Be("Cash jobs");

        (await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate))!
            .MatchedRule!.Name.Should().Be("Everything else");
    }

    [Fact]
    public async Task Declared_value_is_summed_across_the_packages()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);

        await h.Rules.CreateAsync(RuleReq("High value", 10, road, minValue: 1000m), User);
        await h.Rules.CreateAsync(RuleReq("Everything else", 20, road), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h, declaredValue: 1500m), ShipDate);

        decision!.DeclaredValue.Should().Be(1500m);
        decision.MatchedRule!.Name.Should().Be("High value");
        decision.MatchedRule.Reason.Should().Contain("1,500.00 is at least 1,000.00");
    }

    [Fact]
    public async Task A_value_condition_does_not_match_a_consignment_with_no_declared_value()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);

        await h.Rules.CreateAsync(RuleReq("High value", 10, road, minValue: 1000m), User);
        await h.Rules.CreateAsync(RuleReq("Everything else", 20, road), User);

        var decision = await h.Rules.EvaluateAsync(
            await NewConsignment(h, declaredValue: null), ShipDate);

        decision!.MatchedRule!.Name.Should().Be("Everything else");
        decision.Considered[0].Reason.Should().Contain("unknown");
    }

    [Fact]
    public async Task A_postcode_condition_does_not_match_an_address_without_one()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);
        await h.Rules.CreateAsync(RuleReq("Lahore", 10, road, destPrefix: "54"), User);

        var consignment = await NewConsignment(h);
        var address = await h.Db.Addresses.SingleAsync(a => a.PostalCode == "54000");
        address.PostalCode = null;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await h.Rules.EvaluateAsync(consignment, ShipDate))!.MatchedRule.Should().BeNull();
    }

    [Fact]
    public async Task When_nothing_matches_it_says_what_to_do_about_it()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);
        await h.Rules.CreateAsync(RuleReq("Export only", 10, road, destCountry: "AE"), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate);

        decision!.MatchedRule.Should().BeNull();
        decision.Recommended.Should().BeNull();
        decision.Warnings.Should().ContainMatch("*catch-all rule with no conditions*");
    }

    [Fact]
    public async Task With_no_rules_at_all_it_says_so_rather_than_failing()
    {
        var h = NewHarness();

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate);

        decision!.MatchedRule.Should().BeNull();
        decision.Considered.Should().BeEmpty();
        decision.Warnings.Should().ContainMatch("*No shipping rules are configured*");
    }

    // ── Rules narrow; shopping prices ─────────────────────────────────────────

    [Fact]
    public async Task A_rule_naming_no_carrier_shops_every_carrier()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        var air  = await NewCarrier(h, "Alpha Air");
        await NewCard(h, road, 60m);
        await NewCard(h, air, 200m);

        await h.Rules.CreateAsync(RuleReq("Cheapest of anyone", 10), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate);

        decision!.Selection.Should().Be("Any carrier, cheapest first.");
        decision.Options.Should().HaveCount(2);
        decision.Recommended!.CarrierName.Should().Be("Beta Road");
    }

    [Fact]
    public async Task A_rule_can_ask_for_the_fastest_rather_than_the_cheapest()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        var air  = await NewCarrier(h, "Alpha Air");
        await NewService(h, road, "ROAD", "Road freight", transitDays: 4);
        await NewService(h, air,  "AIR",  "Air freight",  transitDays: 1);
        await NewCard(h, road, 60m);
        await NewCard(h, air, 200m);

        await h.Rules.CreateAsync(RuleReq("Get it there", 10, strategy: "FASTEST"), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate);

        decision!.Recommended!.CarrierName.Should().Be("Alpha Air");
        decision.Recommended.Note.Should().Contain("Fastest of 2");
    }

    [Fact]
    public async Task A_rule_that_matches_but_can_price_nothing_says_which_rule_and_why()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");   // no rate card at all
        await h.Rules.CreateAsync(RuleReq("Road it", 10, road), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate);

        decision!.MatchedRule!.Name.Should().Be("Road it");
        decision.Recommended.Should().BeNull();
        decision.Warnings.Should().ContainMatch("*'Road it' matched, but nothing it allows could be priced*");
    }

    // ── Hazardous goods ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_service_that_will_not_carry_hazardous_goods_is_set_aside()
    {
        // CarrierService.SupportsHazardous has existed since T-43 and nothing read it. This is the
        // first thing that can — and it has to be a refusal, because a rule is applied without
        // anybody looking at it.
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        var air  = await NewCarrier(h, "Alpha Air");

        await NewService(h, road, "ROAD", "Road freight", supportsHazardous: true);
        await NewService(h, air,  "AIR",  "Air freight",  supportsHazardous: false);
        await NewCard(h, road, 200m);   // 2500 — dearer
        await NewCard(h, air,   60m);   // 750  — cheaper, and cannot legally carry it

        await h.Rules.CreateAsync(RuleReq("Dangerous goods", 10, hazardous: true), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h, hazardous: true), ShipDate);

        decision!.Recommended!.CarrierName.Should().Be("Beta Road");
        decision.Recommended.Rank.Should().Be(1, "the list was re-ranked after the cheaper one was set aside");

        decision.Excluded.Should().ContainSingle(e => e.CarrierName == "Alpha Air")
            .Which.Reason.Should().Contain("does not carry hazardous goods");

        decision.Warnings.Should().ContainMatch("*does not carry hazardous goods*");
    }

    [Fact]
    public async Task A_service_nobody_has_configured_is_not_treated_as_a_refusal()
    {
        // Silence is not a refusal. A carrier with no service rows has said nothing about hazard.
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);

        await h.Rules.CreateAsync(RuleReq("Dangerous goods", 10, hazardous: true), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h, hazardous: true), ShipDate);

        decision!.Recommended.Should().NotBeNull();
        decision.Excluded.Should().BeEmpty();
    }

    [Fact]
    public async Task Hazard_is_only_checked_when_the_goods_are_hazardous()
    {
        var h = NewHarness();
        var air = await NewCarrier(h, "Alpha Air");
        await NewService(h, air, "AIR", "Air freight", supportsHazardous: false);
        await NewCard(h, air, 60m);

        await h.Rules.CreateAsync(RuleReq("Anything", 10), User);

        var decision = await h.Rules.EvaluateAsync(await NewConsignment(h), ShipDate);

        decision!.Recommended!.CarrierName.Should().Be("Alpha Air");
        decision.Excluded.Should().BeEmpty();
    }

    // ── Applying ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Applying_a_rule_rates_the_consignment_on_what_it_chose()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        var air  = await NewCarrier(h, "Alpha Air");
        await NewCard(h, road, 60m);
        await NewCard(h, air, 200m);
        await h.Rules.CreateAsync(RuleReq("Cheapest of anyone", 10), User);

        var consignment = await NewConsignment(h);

        var rate = await h.Rules.ApplyAsync(consignment, ShipDate, User);

        rate!.Status.Should().Be("RATED");
        rate.FreightCost.Should().Be(750m);

        var stored = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignment);
        stored.CarrierName.Should().Be("Beta Road");
        _ = road;
    }

    [Fact]
    public async Task Evaluating_changes_nothing()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);
        await h.Rules.CreateAsync(RuleReq("Catch-all", 10, road), User);

        var consignment = await NewConsignment(h);
        await h.Rules.EvaluateAsync(consignment, ShipDate);

        var stored = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignment);
        stored.Status.Should().Be("DRAFT");
        stored.CarrierId.Should().BeNull();
        stored.FreightCost.Should().BeNull();
    }

    [Fact]
    public async Task Applying_when_no_rule_matches_is_refused_rather_than_guessed()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await NewCard(h, road, 60m);
        await h.Rules.CreateAsync(RuleReq("Export only", 10, road, destCountry: "AE"), User);

        var act = async () => await h.Rules.ApplyAsync(await NewConsignment(h), ShipDate, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*No shipping rule matches*");
    }

    [Fact]
    public async Task Applying_a_rule_that_can_price_nothing_is_refused_and_names_the_rule()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        await h.Rules.CreateAsync(RuleReq("Road it", 10, road), User);

        var act = async () => await h.Rules.ApplyAsync(await NewConsignment(h), ShipDate, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*'Road it' matched*");
    }

    // ── Not found, and not yours ──────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_rule_or_consignment_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Rules.GetByUuidAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Rules.PatchAsync(Guid.NewGuid(), new PatchShippingRuleRequest(), User)).Should().BeFalse();
        (await h.Rules.DeleteAsync(Guid.NewGuid(), User)).Should().BeFalse();
        (await h.Rules.EvaluateAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Rules.ApplyAsync(Guid.NewGuid(), null, User)).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_rules_do_not_exist_here()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road");
        var uuid = await h.Rules.CreateAsync(RuleReq("Catch-all", 10, road), User);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());

        (await new ShippingRuleRepository(otherDb).GetByUuidAsync(uuid)).Should().BeNull();
        (await new ShippingRuleRepository(otherDb).GetAllAsync()).Should().BeEmpty();
    }
}
