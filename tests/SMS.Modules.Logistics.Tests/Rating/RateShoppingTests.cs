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

// T-48 — comparing what every carrier would charge, and saying why one won.
public class RateShoppingTests
{
    private const int User = 42;
    private static readonly DateTime T0  = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A Friday, so estimated deliveries in these tests are easy to read.</summary>
    private static readonly DateTime ShipDate = new(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        LogisticsDbContext Db,
        RateShoppingService Shopping,
        ConsignmentRatingService Rating,
        RateCardService Cards,
        CarrierServiceRepository Services,
        ScriptedCourierProvider Api,
        string DbName);

    private static Harness NewHarness()
    {
        var (db, _, dbName) = LogisticsTestDb.New();

        var api      = new ScriptedCourierProvider("SCRIPTED", rating: true);
        var registry = new CourierProviderRegistry([new ManualCourierProvider(), api]);
        var accounts = new CarrierAccountResolver(db, registry, new CarrierCredentialVault(db, TestEncryption.New()));
        var services = new CarrierServiceRepository(db);
        var cards    = new RateCardService(new RateCardRepository(db));
        var weights  = new ChargeableWeightService(db, services);
        var rating   = new ConsignmentRatingService(db, weights, accounts, cards);

        return new Harness(
            db, new RateShoppingService(db, weights, accounts, services, cards, rating),
            rating, cards, services, api, dbName);
    }

    private static async Task<Guid> NewCarrier(Harness h, string name, bool api)
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = name, Code = $"C{Guid.NewGuid():N}"[..6],
            IntegrationMode = api ? "API" : null, ProviderKey = api ? "SCRIPTED" : null, IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        if (api)
            await new CarrierAccountRepository(h.Db, new CourierProviderRegistry(
                    [new ManualCourierProvider(), h.Api]))
                .CreateAsync(new CreateCarrierAccountRequest
                {
                    CarrierUuid = carrier.UUID, AccountName = "Main"
                }, User);

        return carrier.UUID;
    }

    private static Task<Guid> NewService(
        Harness h, Guid carrier, string code, string name, int? transitDays) =>
        h.Services.CreateAsync(new CreateCarrierServiceRequest
        {
            CarrierUuid = carrier, ServiceCode = code, ServiceName = name, TransitDays = transitDays
        }, User);

    private static Task<Guid> NewCard(
        Harness h, Guid carrier, decimal perKg, string? serviceCode = null,
        string name = "Tariff", string currency = "PKR") =>
        h.Cards.CreateAsync(new CreateRateCardRequest
        {
            CarrierUuid = carrier, ServiceCode = serviceCode, Name = name, Currency = currency,
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

    /// <summary>One 12.5 kg package, addressed Karachi to Lahore.</summary>
    private static async Task<Guid> NewConsignment(Harness h, Guid? carrier = null)
    {
        var carrierId = carrier is null
            ? (int?)null
            : (await h.Db.Carriers.AsNoTracking().SingleAsync(c => c.UUID == carrier)).Id;

        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), DeliveryNumber = $"DLV-2026-{Random.Shared.Next(1, 99_999):D5}",
            Direction  = LogisticsCode.Of(DeliveryDirection.Outbound),
            SourceType = LogisticsCode.Of(DeliverySourceType.Manual),
            CreatedBy  = User, CreatedDate = T0
        };
        delivery.Packages.Add(new ShipmentPackage
        {
            UUID = Guid.NewGuid(), PackageBarcode = $"PKG-{Guid.NewGuid():N}"[..10],
            GrossWeightKg = 12.5m, LengthCm = 40m, WidthCm = 30m, HeightCm = 20m,
            CreatedBy = User, CreatedDate = T0
        });

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrierId,
            ShipFromAddress = Address("12 Dock Road", "Karachi", "74000"),
            ShipToAddress   = Address("4 Industrial Estate", "Lahore", "54000"),
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

    private static CourierRateOption Option(
        string code, decimal total, int? transitDays = null, string currency = "PKR") =>
        new(code, $"Scripted {code}", total, currency, total, [],
            ChargeableWeightKg: 12.5m,
            EstimatedDelivery: transitDays is { } d ? ShipDate.Date.AddDays(d) : null,
            TransitDays: transitDays);

    private static RateShopRequest Req(
        string? strategy = null, DateTime? requiredBy = null, bool cardOnly = false,
        string? serviceCode = null, params Guid[] carriers) =>
        new()
        {
            Strategy = strategy, RequiredBy = requiredBy, RateCardOnly = cardOnly,
            ServiceCode = serviceCode, ShipDate = ShipDate,
            CarrierUuids = carriers.Length > 0 ? [.. carriers] : null
        };

    // ── Ranking ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_carrier_is_compared_and_the_cheapest_wins()
    {
        var h = NewHarness();
        var api  = await NewCarrier(h, "Alpha Air", api: true);
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewCard(h, road, perKg: 60m);            // 750
        var consignment = await NewConsignment(h);

        h.Api.ThenRates(Option("EXPRESS", 2000m, 2));

        var shop = await h.Shopping.ShopAsync(consignment, Req());

        shop!.Options.Select(o => o.CarrierName).Should().Equal(["Beta Road", "Alpha Air"]);
        shop.Options[0].Rank.Should().Be(1);
        shop.Options[0].TotalAmount.Should().Be(750m);
        shop.Recommended!.CarrierUuid.Should().Be(road);
        _ = api;
    }

    [Fact]
    public async Task The_winner_says_why_it_won_and_by_how_much()
    {
        // A ranked list with no reasoning on it is a list somebody has to re-derive before they can
        // defend the choice.
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        var air  = await NewCarrier(h, "Alpha Air", api: false);
        await NewCard(h, road, perKg: 60m);    // 750
        await NewCard(h, air,  perKg: 100m);   // 1250
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(consignment, Req());

        shop!.Recommendation.Should()
            .Contain("Cheapest of 2").And
            .Contain("Beta Road").And
            .Contain("750").And
            .Contain("500.00 below");

        shop.Options[1].Note.Should().Contain("500.00 more than the recommendation");
        shop.Options[1].MoreThanBest.Should().Be(500m);
        shop.Options[1].MoreThanBestPercent.Should().Be(66.7m);
    }

    [Fact]
    public async Task A_single_comparable_quote_is_described_as_such()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(consignment, Req());

        shop!.Recommendation.Should().Contain("the only comparable quote");
    }

    [Fact]
    public async Task Shopping_for_speed_ranks_by_transit_not_by_price()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        var air  = await NewCarrier(h, "Alpha Air", api: false);

        await NewService(h, road, "ROAD", "Road freight", transitDays: 4);
        await NewService(h, air,  "AIR",  "Air freight",  transitDays: 1);
        await NewCard(h, road, perKg: 60m);     // 750, 4 days
        await NewCard(h, air,  perKg: 200m);    // 2500, 1 day
        var consignment = await NewConsignment(h);

        var cheapest = await h.Shopping.ShopAsync(consignment, Req());
        cheapest!.Options[0].CarrierName.Should().Be("Beta Road");

        var fastest = await h.Shopping.ShopAsync(consignment, Req(strategy: "FASTEST"));

        fastest!.Options[0].CarrierName.Should().Be("Alpha Air");
        fastest.Recommendation.Should().Contain("Fastest of 2").And.Contain("1 day");
        fastest.Options[1].Note.Should().Contain("3 day(s) slower");
    }

    [Fact]
    public async Task An_unrecognised_strategy_falls_back_to_cheapest_rather_than_guessing()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        (await h.Shopping.ShopAsync(consignment, Req(strategy: "BEST_VALUE")))!
            .Strategy.Should().Be("CHEAPEST");
    }

    // ── The deadline ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_option_that_arrives_too_late_is_excluded_but_still_shown_with_its_price()
    {
        // The cheapest option that misses a deadline by a day is exactly what somebody needs to see
        // before accepting the one that does not.
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        var air  = await NewCarrier(h, "Alpha Air", api: false);

        await NewService(h, road, "ROAD", "Road freight", transitDays: 4);
        await NewService(h, air,  "AIR",  "Air freight",  transitDays: 1);
        await NewCard(h, road, perKg: 60m);
        await NewCard(h, air,  perKg: 200m);
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(
            consignment, Req(requiredBy: ShipDate.AddDays(2)));

        shop!.Options.Should().ContainSingle().Which.CarrierName.Should().Be("Alpha Air");

        var late = shop.Excluded.Should().ContainSingle().Subject;
        late.CarrierName.Should().Be("Beta Road");
        late.TotalAmount.Should().Be(750m, "the price is shown so the shortfall can be weighed against it");
        late.Reason.Should().Contain("2026-09-22").And.Contain("after the 2026-09-20");

        shop.Recommendation.Should().Contain("inside the 2026-09-20 deadline");
    }

    [Fact]
    public async Task When_nothing_arrives_in_time_everything_is_excluded_and_said_so()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewService(h, road, "ROAD", "Road freight", transitDays: 4);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(consignment, Req(requiredBy: ShipDate.AddDays(1)));

        shop!.Options.Should().BeEmpty();
        shop.Recommended.Should().BeNull();
        shop.Excluded.Should().ContainSingle();
        shop.Warnings.Should().ContainMatch("*Nothing quoted arrives by*");
    }

    [Fact]
    public async Task An_option_with_no_transit_time_is_not_ruled_out_by_a_deadline()
    {
        // A card that says nothing about transit has not promised to be late.
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(consignment, Req(requiredBy: ShipDate.AddDays(1)));

        shop!.Options.Should().ContainSingle();
        shop.Excluded.Should().BeEmpty();
    }

    // ── Currencies ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Quotes_in_another_currency_are_listed_unranked_rather_than_mis_ranked()
    {
        // This module holds no exchange rate, and a quote ranked against a currency it is not in
        // would be far worse than one plainly set aside.
        var h = NewHarness();
        var local  = await NewCarrier(h, "Beta Road", api: false);
        var local2 = await NewCarrier(h, "Gamma Road", api: false);
        var export = await NewCarrier(h, "Alpha Air", api: false);

        await NewCard(h, local,  perKg: 60m);
        await NewCard(h, local2, perKg: 80m);
        await NewCard(h, export, perKg: 5m, currency: "USD");
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(consignment, Req());

        shop!.Options.Where(o => o.Rank is not null).Should().HaveCount(2);

        var unranked = shop.Options.Single(o => o.Rank is null);
        unranked.Currency.Should().Be("USD");
        unranked.Note.Should().Contain("not ranked against PKR");

        shop.Warnings.Should().ContainMatch("*cannot be ranked against PKR without an exchange rate*");
        shop.Recommended!.Currency.Should().Be("PKR");
    }

    // ── Where a price comes from ──────────────────────────────────────────────

    [Fact]
    public async Task A_carrier_that_quotes_is_not_also_priced_from_its_card()
    {
        // Two prices for one carrier, with no way to tell which is real.
        var h = NewHarness();
        var api = await NewCarrier(h, "Alpha Air", api: true);
        await NewCard(h, api, perKg: 10m);   // 125 — far cheaper, and deliberately not used
        var consignment = await NewConsignment(h);

        h.Api.ThenRates(Option("EXPRESS", 2000m, 2));

        var shop = await h.Shopping.ShopAsync(consignment, Req());

        shop!.Options.Should().ContainSingle();
        shop.Options[0].Source.Should().Be("CARRIER");
        shop.Options[0].TotalAmount.Should().Be(2000m);
    }

    [Fact]
    public async Task A_carrier_that_quotes_several_services_offers_all_of_them()
    {
        var h = NewHarness();
        await NewCarrier(h, "Alpha Air", api: true);
        var consignment = await NewConsignment(h);

        h.Api.ThenRates(
            Option("ECONOMY",   900m, 4),
            Option("EXPRESS",  2000m, 2),
            Option("OVERNIGHT", 3000m, 1));

        var shop = await h.Shopping.ShopAsync(consignment, Req());

        shop!.Options.Select(o => o.ServiceCode).Should().Equal(["ECONOMY", "EXPRESS", "OVERNIGHT"]);
        shop.Options.Should().OnlyContain(o => o.CarrierAccountName == "Main");
    }

    [Fact]
    public async Task A_carrier_with_no_API_is_priced_once_per_service_it_sells()
    {
        // Which is what makes a carrier booked by telephone comparable on speed as well as price.
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewService(h, road, "ROAD",    "Road freight",  transitDays: 4);
        await NewService(h, road, "EXPRESS", "Road express",  transitDays: 2);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(consignment, Req());

        shop!.Options.Should().HaveCount(2);
        shop.Options.Should().OnlyContain(o => o.Source == "RATE_CARD");
        shop.Options.Select(o => o.TransitDays).Should().BeEquivalentTo([2, 4]);
    }

    [Fact]
    public async Task A_carrier_that_can_be_priced_by_nothing_is_listed_with_the_reason()
    {
        // A shop that silently omits a carrier looks like a shop that found nothing there, and the
        // two lead to very different next actions.
        var h = NewHarness();
        var road    = await NewCarrier(h, "Beta Road", api: false);
        var nothing = await NewCarrier(h, "Gamma Road", api: false);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(consignment, Req());

        shop!.Options.Should().ContainSingle();
        var excluded = shop.Excluded.Should().ContainSingle().Subject;
        excluded.CarrierUuid.Should().Be(nothing);
        excluded.Reason.Should().Contain("No rate card is in effect");
    }

    [Fact]
    public async Task The_card_can_be_shopped_on_its_own_without_asking_a_single_carrier()
    {
        var h = NewHarness();
        var api = await NewCarrier(h, "Alpha Air", api: true);
        await NewCard(h, api, perKg: 10m);
        var consignment = await NewConsignment(h);

        h.Api.ThenRates(Option("EXPRESS", 2000m, 2));

        var shop = await h.Shopping.ShopAsync(consignment, Req(cardOnly: true));

        shop!.Options.Should().ContainSingle().Which.Source.Should().Be("RATE_CARD");
        h.Api.RateCalls.Should().BeEmpty();
    }

    // ── Narrowing ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Only_the_named_carriers_are_compared()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        var air  = await NewCarrier(h, "Alpha Air", api: false);
        await NewCard(h, road, perKg: 60m);
        await NewCard(h, air,  perKg: 200m);
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(consignment, Req(carriers: air));

        shop!.Options.Should().ContainSingle().Which.CarrierUuid.Should().Be(air);
    }

    [Fact]
    public async Task A_named_service_narrows_the_comparison_and_names_who_does_not_sell_it()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        var air  = await NewCarrier(h, "Alpha Air", api: false);

        await NewService(h, road, "ROAD",    "Road freight", transitDays: 4);
        await NewService(h, road, "EXPRESS", "Road express", transitDays: 2);
        await NewService(h, air,  "AIR",     "Air freight",  transitDays: 1);
        await NewCard(h, road, perKg: 60m);
        await NewCard(h, air,  perKg: 200m);
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(consignment, Req(serviceCode: "EXPRESS"));

        shop!.Options.Should().ContainSingle().Which.ServiceCode.Should().Be("EXPRESS");
        shop.Excluded.Should().ContainSingle(e => e.Reason.Contains("does not sell 'EXPRESS'"));
    }

    [Fact]
    public async Task With_no_carriers_at_all_it_says_so_rather_than_returning_an_empty_list()
    {
        var h = NewHarness();
        var consignment = await NewConsignment(h);

        var shop = await h.Shopping.ShopAsync(consignment, Req());

        shop!.Options.Should().BeEmpty();
        shop.Warnings.Should().ContainMatch("*no active carriers*");
    }

    // ── Shopping changes nothing ──────────────────────────────────────────────

    [Fact]
    public async Task Shopping_compares_prices_without_choosing_one()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        await h.Shopping.ShopAsync(consignment, Req());

        var stored = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignment);
        stored.Status.Should().Be("DRAFT");
        stored.FreightCost.Should().BeNull();
        stored.CarrierId.Should().BeNull();

        // Unlike rating, it does not settle the package weights either.
        (await h.Db.ShipmentPackages.AsNoTracking().SingleAsync()).WeightRatedAt.Should().BeNull();
    }

    [Fact]
    public async Task An_incomplete_weight_makes_every_price_a_floor_and_says_the_comparison_is_still_fair()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        h.Db.ShipmentPackages.Add(new ShipmentPackage
        {
            UUID = Guid.NewGuid(), PackageBarcode = "PKG-UNMEASURED",
            DeliveryOrderId = (await h.Db.ShipmentPackages.AsNoTracking().FirstAsync()).DeliveryOrderId,
            CreatedBy = User, CreatedDate = T0
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var shop = await h.Shopping.ShopAsync(consignment, Req());

        shop!.Warnings.Should().ContainMatch("*understated by the same packages*");
        shop.Options.Should().ContainSingle();
    }

    // ── Accepting ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accepting_an_option_points_the_consignment_at_that_carrier_and_rates_it()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        var air  = await NewCarrier(h, "Alpha Air", api: false);
        await NewService(h, air, "AIR", "Air freight", transitDays: 1);
        await NewCard(h, road, perKg: 60m);
        await NewCard(h, air,  perKg: 200m);
        var consignment = await NewConsignment(h);

        var rate = await h.Shopping.AcceptAsync(consignment, new AcceptRateRequest
        {
            CarrierUuid = air, ServiceCode = "AIR", ShipDate = ShipDate
        }, User);

        rate!.FreightCost.Should().Be(2500m);
        rate.Status.Should().Be("RATED");
        rate.Source.Should().Be("RATE_CARD");
        rate.Note.Should().Contain("Chosen by rate shopping").And.Contain("Alpha Air");

        var stored = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignment);
        stored.CarrierName.Should().Be("Alpha Air");
        stored.CarrierServiceCode.Should().Be("AIR");
    }

    [Fact]
    public async Task Accepting_a_carrier_quote_records_the_account_it_came_from()
    {
        var h = NewHarness();
        var api = await NewCarrier(h, "Alpha Air", api: true);
        var consignment = await NewConsignment(h);

        // Once for the shop inside Accept.
        h.Api.ThenRates(Option("EXPRESS", 2000m, 2));

        var rate = await h.Shopping.AcceptAsync(consignment, new AcceptRateRequest
        {
            CarrierUuid = api, ServiceCode = "EXPRESS", ShipDate = ShipDate
        }, User);

        rate!.Source.Should().Be("CARRIER");
        rate.FreightCost.Should().Be(2000m);

        var stored = await h.Db.Consignments.AsNoTracking()
            .Include(c => c.CarrierAccount).SingleAsync(c => c.UUID == consignment);

        stored.CarrierAccount!.AccountName.Should().Be("Main");
    }

    [Fact]
    public async Task An_option_that_can_no_longer_be_quoted_is_refused_rather_than_taken_on_trust()
    {
        // The price is re-quoted on accept. A figure that went out to a browser and came back
        // changed is not a price any carrier gave — and this is where it would become the number an
        // invoice gets reconciled against.
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        var consignment = await NewConsignment(h);

        var act = async () => await h.Shopping.AcceptAsync(consignment, new AcceptRateRequest
        {
            CarrierUuid = road, ServiceCode = "GONE", ShipDate = ShipDate
        }, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*could not be quoted*")
            .WithMessage("*Shop again*");
    }

    [Fact]
    public async Task An_option_with_no_service_of_its_own_can_still_be_accepted()
    {
        // A carrier priced from a general rate card with no services configured produces exactly
        // one option, and it has no service code. Refusing that would make a legitimate quote
        // impossible to take — Consignment.CarrierServiceCode has always been nullable.
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        var rate = await h.Shopping.AcceptAsync(consignment, new AcceptRateRequest
        {
            CarrierUuid = road, ServiceCode = "  ", ShipDate = ShipDate
        }, User);

        rate!.FreightCost.Should().Be(750m);

        var stored = await h.Db.Consignments.AsNoTracking().SingleAsync(c => c.UUID == consignment);
        stored.CarrierName.Should().Be("Beta Road");
        stored.CarrierServiceCode.Should().BeNull();
    }

    [Fact]
    public async Task Naming_no_service_where_every_option_is_named_is_refused()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewService(h, road, "ROAD", "Road freight", transitDays: 4);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        var act = async () => await h.Shopping.AcceptAsync(
            consignment, new AcceptRateRequest { CarrierUuid = road, ServiceCode = "", ShipDate = ShipDate },
            User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*(no service)*");
    }

    // ── Not found, and not yours ──────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_consignment_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Shopping.ShopAsync(Guid.NewGuid(), Req())).Should().BeNull();
        (await h.Shopping.AcceptAsync(
            Guid.NewGuid(), new AcceptRateRequest { CarrierUuid = Guid.NewGuid(), ServiceCode = "X" }, User))
            .Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_consignment_does_not_exist_here()
    {
        var h = NewHarness();
        var road = await NewCarrier(h, "Beta Road", api: false);
        await NewCard(h, road, perKg: 60m);
        var consignment = await NewConsignment(h);

        var otherDb   = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var services  = new CarrierServiceRepository(otherDb);
        var weights   = new ChargeableWeightService(otherDb, services);
        var cards     = new RateCardService(new RateCardRepository(otherDb));
        var accounts  = new CarrierAccountResolver(
            otherDb, new CourierProviderRegistry([h.Api]), new CarrierCredentialVault(otherDb, TestEncryption.New()));

        var other = new RateShoppingService(
            otherDb, weights, accounts, services, cards,
            new ConsignmentRatingService(otherDb, weights, accounts, cards));

        (await other.ShopAsync(consignment, Req())).Should().BeNull();
    }
}
