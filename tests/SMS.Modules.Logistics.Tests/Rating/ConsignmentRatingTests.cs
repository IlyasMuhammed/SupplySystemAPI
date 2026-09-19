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

// T-47 — pricing a consignment: where G9's two halves meet, and where RATED finally becomes
// reachable (finding F38) and a freight cost lands on the consignment (finding F37).
public class ConsignmentRatingTests
{
    private const int User = 42;
    private static readonly DateTime T0  = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(
        LogisticsDbContext Db,
        ConsignmentRatingService Rating,
        RateCardService Cards,
        ScriptedCourierProvider Carrier,
        string DbName);

    private static Harness NewHarness(bool carrierRates = true)
    {
        var (db, _, dbName) = LogisticsTestDb.New();

        var provider = new ScriptedCourierProvider("SCRIPTED", rating: carrierRates);
        var registry = new CourierProviderRegistry([new ManualCourierProvider(), provider]);
        var vault    = new CarrierCredentialVault(db, TestEncryption.New());
        var accounts = new CarrierAccountResolver(db, registry, vault);
        var cards    = new RateCardService(new RateCardRepository(db));
        var weights  = new ChargeableWeightService(db, new CarrierServiceRepository(db));

        return new Harness(db, new ConsignmentRatingService(db, weights, accounts, cards),
                           cards, provider, dbName);
    }

    private sealed record Seeded(Guid Consignment, Guid Carrier);

    /// <summary>A packed, addressed consignment on an API carrier with a default account.</summary>
    private static async Task<Seeded> Seed(
        Harness h, string? providerKey = "SCRIPTED", string? serviceCode = null,
        bool packed = true, decimal? cod = null, string status = "DRAFT")
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = "Scripted Express", Code = $"C{Guid.NewGuid():N}"[..6],
            IntegrationMode = providerKey is null ? null : "API", ProviderKey = providerKey, IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();

        if (providerKey is not null)
            await new CarrierAccountRepository(h.Db, new CourierProviderRegistry(
                    [new ManualCourierProvider(), h.Carrier]))
                .CreateAsync(new CreateCarrierAccountRequest
                {
                    CarrierUuid = carrier.UUID, AccountName = "Main"
                }, User);

        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), DeliveryNumber = $"DLV-2026-{Random.Shared.Next(1, 99_999):D5}",
            Direction  = LogisticsCode.Of(DeliveryDirection.Outbound),
            SourceType = LogisticsCode.Of(DeliverySourceType.Manual),
            CreatedBy  = User, CreatedDate = T0
        };

        if (packed)
            // 40 × 30 × 20, 12.5 kg — no carrier service configured, so no divisor and the scale wins.
            delivery.Packages.Add(new ShipmentPackage
            {
                UUID = Guid.NewGuid(), PackageBarcode = $"PKG-{Guid.NewGuid():N}"[..10],
                GrossWeightKg = 12.5m, LengthCm = 40m, WidthCm = 30m, HeightCm = 20m,
                CreatedBy = User, CreatedDate = T0
            });

        var consignment = new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrier.Id, CarrierName = carrier.Name, CarrierServiceCode = serviceCode,
            CodAmount = cod, CodCurrency = cod is null ? null : "PKR",
            Status = status,
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

        return new Seeded(consignment.UUID, carrier.UUID);
    }

    private static Address Address(string line1, string city, string postcode) => new()
    {
        UUID = Guid.NewGuid(), Line1 = line1, CityName = city, CountryName = "Pakistan",
        CountryIsoCode = "PK", PostalCode = postcode, ContactName = "Receiving",
        ContactPhone = "+923001234567", CreatedBy = User, CreatedDate = T0
    };

    private static Task<Guid> NewCard(
        Harness h, Guid carrier, decimal perKg = 100m, decimal? fuel = null, string? serviceCode = null,
        string name = "Domestic 2026") =>
        h.Cards.CreateAsync(new CreateRateCardRequest
        {
            CarrierUuid = carrier, ServiceCode = serviceCode, Name = name, Currency = "PKR",
            EffectiveFrom = Jan, FuelSurchargePercent = fuel,
            Lanes =
            [
                new RateCardLaneRequest
                {
                    Name = "Anywhere",
                    Breaks = [new RateCardBreakRequest { FromWeightKg = 0m, Amount = perKg, Basis = "PER_KG" }]
                }
            ]
        }, User);

    private static Task<Consignment> Reload(Harness h, Guid uuid) =>
        h.Db.Consignments.AsNoTracking().Include(c => c.Charges).SingleAsync(c => c.UUID == uuid);

    private static CourierRateOption Option(
        string code = "SCRIPTED-STD", decimal total = 2000m, decimal basic = 1800m, decimal fuel = 200m) =>
        new(code, $"Scripted {code}", total, "PKR", basic,
            [new CourierSurcharge("FUEL", "Fuel surcharge", fuel)], ChargeableWeightKg: 12.5m,
            TransitDays: 2);

    // ── The carrier is asked first ────────────────────────────────────────────

    [Fact]
    public async Task A_carrier_that_quotes_is_believed()
    {
        // Its own quote is the only number that is actually true, surcharges included.
        var h = NewHarness();
        var s = await Seed(h);
        await NewCard(h, s.Carrier, perKg: 100m);   // would have said 1250 — and is not used

        h.Carrier.ThenRates(Option());

        var rate = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        rate!.IsRated.Should().BeTrue();
        rate.Source.Should().Be("CARRIER");
        rate.FreightCost.Should().Be(2000m);
        rate.FreightCurrency.Should().Be("PKR");
        rate.Charges.Select(c => c.Code).Should().Equal(["BASE", "FUEL"]);
        rate.Charges.Sum(c => c.Amount).Should().Be(2000m);
        rate.Note.Should().Contain("Scripted");
    }

    [Fact]
    public async Task Rating_makes_RATED_reachable()
    {
        // Finding F38: the status existed in the state machine and nothing transitioned into it.
        var h = NewHarness();
        var s = await Seed(h);
        h.Carrier.ThenRates(Option());

        (await Reload(h, s.Consignment)).Status.Should().Be("DRAFT");

        await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        (await Reload(h, s.Consignment)).Status.Should().Be("RATED");
    }

    [Fact]
    public async Task The_freight_cost_lands_on_the_consignment_itemised()
    {
        // Finding F37: before this, the only record of a price was on the command ledger, so "what
        // did this cost to ship" meant digging through carrier commands.
        var h = NewHarness();
        var s = await Seed(h);
        h.Carrier.ThenRates(Option());

        await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        var consignment = await Reload(h, s.Consignment);

        consignment.FreightCost.Should().Be(2000m);
        consignment.FreightCurrency.Should().Be("PKR");
        consignment.FreightRatedAt.Should().NotBeNull();
        consignment.RatedChargeableWeightKg.Should().Be(12.5m);
        consignment.Charges.Should().HaveCount(2);
        consignment.Charges.OrderBy(c => c.Sequence).First().Code.Should().Be("BASE");
    }

    [Fact]
    public async Task The_named_service_is_taken_when_the_carrier_quoted_it()
    {
        var h = NewHarness();
        var s = await Seed(h, serviceCode: "EXPRESS");

        h.Carrier.ThenRates(
            Option("ECONOMY", 900m, 900m, 0m),
            Option("EXPRESS", 2000m, 2000m, 0m));

        var rate = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        rate!.ServiceCode.Should().Be("EXPRESS");
        rate.FreightCost.Should().Be(2000m);
    }

    [Fact]
    public async Task With_no_service_named_the_cheapest_option_wins()
    {
        // Picking the dearest by accident is the failure worth avoiding here. Comparing across
        // carriers and explaining which won is T-48.
        var h = NewHarness();
        var s = await Seed(h);

        h.Carrier.ThenRates(
            Option("OVERNIGHT", 3000m, 3000m, 0m),
            Option("ECONOMY",    900m,  900m, 0m),
            Option("EXPRESS",   2000m, 2000m, 0m));

        var rate = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        rate!.ServiceCode.Should().Be("ECONOMY");
        rate.FreightCost.Should().Be(900m);
    }

    // ── The card answers when the carrier will not ────────────────────────────

    [Fact]
    public async Task A_carrier_that_does_not_quote_falls_through_to_the_card()
    {
        var h = NewHarness(carrierRates: false);
        var s = await Seed(h);
        await NewCard(h, s.Carrier, perKg: 100m, fuel: 12m);

        var rate = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        rate!.Source.Should().Be("RATE_CARD");
        rate.FreightCost.Should().Be(1400m, "12.5 kg at 100, plus 12% fuel");
        rate.Note.Should().Contain("Domestic 2026");
    }

    [Fact]
    public async Task A_carrier_that_refuses_falls_through_to_the_card()
    {
        var h = NewHarness();
        var s = await Seed(h);
        await NewCard(h, s.Carrier);

        h.Carrier.ThenRates((_, _) => Task.FromResult(new CourierRateResult(
            CourierOutcome.Refused, "SCRIPTED", [], "Postcode not serviceable.")));

        var rate = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        rate!.Source.Should().Be("RATE_CARD");
        rate.FreightCost.Should().Be(1250m);
    }

    [Fact]
    public async Task A_rate_call_that_comes_apart_falls_through_rather_than_failing_the_request()
    {
        // A rate call is a read, so an adapter throwing costs nothing and the card takes over. This
        // is precisely the case a *booking* must never treat this way.
        var h = NewHarness();
        var s = await Seed(h);
        await NewCard(h, s.Carrier);

        h.Carrier.ThenRates((_, _) => Task.FromException<CourierRateResult>(new HttpRequestException("gateway")));

        var rate = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        rate!.Source.Should().Be("RATE_CARD");
        rate.Attempts.Should().ContainSingle(a => a.Source == "CARRIER" && !a.Succeeded)
            .Which.Message.Should().Contain("gateway");
    }

    [Fact]
    public async Task Both_attempts_are_reported_even_when_the_first_one_worked()
    {
        // "The carrier quoted this" and "the carrier would not answer, so the card was used" are
        // different facts about the same number, and only one of them is worth chasing.
        var h = NewHarness();
        var s = await Seed(h);
        h.Carrier.ThenRates(Option());

        var rate = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        rate!.Attempts.Should().ContainSingle();
        rate.Attempts[0].Source.Should().Be("CARRIER");
        rate.Attempts[0].Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task The_card_can_be_asked_on_its_own()
    {
        // What Phase 4 asks of every invoice: what does the tariff say this should have cost?
        var h = NewHarness();
        var s = await Seed(h);
        await NewCard(h, s.Carrier);

        h.Carrier.ThenRates(Option());

        var rate = await h.Rating.RateAsync(
            s.Consignment, new RateConsignmentRequest { RateCardOnly = true }, User);

        rate!.Source.Should().Be("RATE_CARD");
        rate.Attempts.Should().NotContain(a => a.Source == "CARRIER");
        h.Carrier.RateCalls.Should().BeEmpty("the carrier was deliberately not asked");
    }

    [Fact]
    public async Task With_neither_a_quote_nor_a_card_it_refuses_rather_than_pricing_at_nothing()
    {
        var h = NewHarness(carrierRates: false);
        var s = await Seed(h);

        var act = async () => await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*Nothing could price this consignment*")
            .WithMessage("*record the price by hand*");

        (await Reload(h, s.Consignment)).Status.Should().Be("DRAFT", "nothing was decided");
    }

    [Fact]
    public async Task A_consignment_with_no_carrier_has_nobody_to_price_it()
    {
        var h = NewHarness();
        var s = await Seed(h);

        var consignment = await h.Db.Consignments.SingleAsync(c => c.UUID == s.Consignment);
        consignment.CarrierId = null;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*no carrier*");
    }

    // ── Weights ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rating_settles_the_package_weights_it_prices_on()
    {
        // A quote that cannot be reproduced from stored figures is a quote nobody can dispute.
        var h = NewHarness(carrierRates: false);
        var s = await Seed(h);
        await NewCard(h, s.Carrier);

        await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        var package = await h.Db.ShipmentPackages.AsNoTracking().SingleAsync();

        package.ChargeableWeightKg.Should().Be(12.5m);
        package.WeightRatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task An_unpacked_consignment_is_flagged_rather_than_quietly_priced_at_nothing()
    {
        var h = NewHarness(carrierRates: false);
        var s = await Seed(h, packed: false);
        await NewCard(h, s.Carrier);

        var rate = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        rate!.Warnings.Should().ContainMatch("*has been packed*");
    }

    [Fact]
    public async Task A_package_that_cannot_be_weighed_makes_the_price_a_floor()
    {
        var h = NewHarness(carrierRates: false);
        var s = await Seed(h);
        await NewCard(h, s.Carrier);

        h.Db.ShipmentPackages.Add(new ShipmentPackage
        {
            UUID = Guid.NewGuid(), PackageBarcode = "PKG-UNMEASURED",
            DeliveryOrderId = (await h.Db.ShipmentPackages.AsNoTracking().FirstAsync()).DeliveryOrderId,
            CreatedBy = User, CreatedDate = T0
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var rate = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        rate!.Warnings.Should().ContainMatch("*floor rather than a quote*");
    }

    // ── Re-rating and clearing ────────────────────────────────────────────────

    [Fact]
    public async Task Re_rating_replaces_the_quote_in_place_without_a_status_change()
    {
        // Routing it back through DRAFT would blank the price for as long as the re-rate took, and
        // a consignment that momentarily costs nothing is worse than one priced yesterday.
        var h = NewHarness();
        var s = await Seed(h);

        h.Carrier.ThenRates(Option(total: 2000m, basic: 1800m, fuel: 200m));
        await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        h.Carrier.ThenRates(Option(total: 2400m, basic: 2200m, fuel: 200m));
        var again = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        again!.FreightCost.Should().Be(2400m);
        again.Status.Should().Be("RATED");

        var consignment = await Reload(h, s.Consignment);
        consignment.Charges.Should().HaveCount(2, "the old lines were replaced, not added to");
        consignment.Charges.Single(c => c.Code == "BASE").Amount.Should().Be(2200m);
    }

    [Fact]
    public async Task Discarding_a_quote_puts_it_back_to_draft_with_nothing_owing()
    {
        var h = NewHarness();
        var s = await Seed(h);
        h.Carrier.ThenRates(Option());
        await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        (await h.Rating.ClearAsync(s.Consignment, User)).Should().BeTrue();

        var consignment = await Reload(h, s.Consignment);

        consignment.Status.Should().Be("DRAFT");
        consignment.FreightCost.Should().BeNull();
        consignment.Charges.Should().BeEmpty();
    }

    [Fact]
    public async Task A_quote_that_was_never_made_cannot_be_discarded()
    {
        var h = NewHarness();
        var s = await Seed(h);

        var act = async () => await h.Rating.ClearAsync(s.Consignment, User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*not RATED*");
    }

    [Fact]
    public async Task A_consignment_already_with_the_carrier_cannot_be_re_priced()
    {
        // What it costs is what was booked, not what a quote says today.
        var h = NewHarness();
        var s = await Seed(h, status: "BOOKED");
        h.Carrier.ThenRates(Option());

        var act = async () => await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*cannot be re-priced*")
            .WithMessage("*what was booked*");
    }

    [Fact]
    public async Task A_rated_consignment_can_still_be_booked_by_hand()
    {
        // The gap T-47 exposed: RATED had no transition to BOOKED, so rating a consignment would
        // quietly have made it unbookable by a carrier with no API — which is most of them, and
        // which is exactly the carrier a rate card exists to price.
        var h = NewHarness();
        var s = await Seed(h, providerKey: null);
        await NewCard(h, s.Carrier);

        var rate = await h.Rating.RateAsync(s.Consignment, new RateConsignmentRequest(), User);
        rate!.Source.Should().Be("RATE_CARD");

        var booked = await new ConsignmentRepository(h.Db, null!).BookManuallyAsync(
            s.Consignment, new ManualBookingRequest { Awb = "AWB-123456" }, User);

        booked.Should().BeTrue();
        (await Reload(h, s.Consignment)).Status.Should().Be("BOOKED");
    }

    // ── A price somebody was given ────────────────────────────────────────────

    [Fact]
    public async Task A_price_obtained_by_telephone_can_be_recorded()
    {
        var h = NewHarness(carrierRates: false);
        var s = await Seed(h);

        var rate = await h.Rating.SetManualRateAsync(s.Consignment, new ManualRateRequest
        {
            Amount = 3250m, Currency = "pkr", Note = "Quotation Q-2026-881, emailed 18 Sep"
        }, User);

        rate!.Source.Should().Be("MANUAL");
        rate.FreightCost.Should().Be(3250m);
        rate.FreightCurrency.Should().Be("PKR");
        rate.Status.Should().Be("RATED");
        rate.Charges.Should().ContainSingle().Which.Code.Should().Be("BASE");
    }

    [Fact]
    public async Task A_keyed_in_price_has_to_say_where_it_came_from()
    {
        // A figure with no provenance cannot be defended when the invoice disagrees with it.
        var h = NewHarness();
        var s = await Seed(h);

        var act = async () => await h.Rating.SetManualRateAsync(
            s.Consignment, new ManualRateRequest { Amount = 100m, Currency = "PKR", Note = "  " }, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*where this price came from*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    public async Task A_keyed_in_price_has_to_be_a_price(decimal amount)
    {
        var h = NewHarness();
        var s = await Seed(h);

        var act = async () => await h.Rating.SetManualRateAsync(
            s.Consignment, new ManualRateRequest { Amount = amount, Currency = "PKR", Note = "Q-1" }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_keyed_in_price_needs_a_real_currency()
    {
        var h = NewHarness();
        var s = await Seed(h);

        var act = async () => await h.Rating.SetManualRateAsync(
            s.Consignment, new ManualRateRequest { Amount = 100m, Currency = "RUPEES", Note = "Q-1" }, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*three-letter ISO*");
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unrated_consignment_reads_as_unrated_rather_than_free()
    {
        var h = NewHarness();
        var s = await Seed(h);

        var rate = await h.Rating.GetAsync(s.Consignment);

        rate!.IsRated.Should().BeFalse();
        rate.FreightCost.Should().BeNull();
        rate.Charges.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_consignment_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Rating.GetAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Rating.RateAsync(Guid.NewGuid(), new RateConsignmentRequest(), User)).Should().BeNull();
        (await h.Rating.SetManualRateAsync(
            Guid.NewGuid(), new ManualRateRequest { Amount = 1m, Currency = "PKR", Note = "x" }, User))
            .Should().BeNull();
        (await h.Rating.ClearAsync(Guid.NewGuid(), User)).Should().BeFalse();
    }

    [Fact]
    public async Task Another_organizations_consignment_does_not_exist_here()
    {
        var h = NewHarness();
        var s = await Seed(h);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other   = new ConsignmentRatingService(
            otherDb,
            new ChargeableWeightService(otherDb, new CarrierServiceRepository(otherDb)),
            new CarrierAccountResolver(otherDb, new CourierProviderRegistry([h.Carrier]),
                                       new CarrierCredentialVault(otherDb, TestEncryption.New())),
            new RateCardService(new RateCardRepository(otherDb)));

        (await other.GetAsync(s.Consignment)).Should().BeNull();
    }
}
