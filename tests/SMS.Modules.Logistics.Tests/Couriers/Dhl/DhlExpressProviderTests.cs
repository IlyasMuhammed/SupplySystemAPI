using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Dhl;
using SMS.Modules.Logistics.Couriers.Manual;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers.Dhl;

/// <summary>Every adapter must pass this. It is the definition of what implementing the interface means.</summary>
public class DhlExpressContractTests : CourierProviderContractTests
{
    protected override ICourierProvider CreateProvider() =>
        new DhlExpressCourierProvider(new FakeDhlServer(), NullLogger<DhlExpressCourierProvider>.Instance);

    protected override IReadOnlyDictionary<string, string> ValidCredentials() => DhlFixtures.Credentials();

    protected override CourierBookingRequest BookableRequest(string? idempotencyKey = null) =>
        base.BookableRequest(idempotencyKey) with { ServiceCode = "N" };

    /// <summary>DHL's answer to a ship-to with no postal code is no — and it is the adapter that says so first.</summary>
    protected override CourierBookingRequest? RefusableRequest() =>
        BookableRequest() with { ShipTo = BookableRequest().ShipTo with { PostalCode = null } };
}

internal static class DhlFixtures
{
    internal static readonly DateTime Now = new(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc);

    internal sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    internal static Dictionary<string, string> Credentials(Action<Dictionary<string, string>>? change = null)
    {
        var credentials = new Dictionary<string, string>
        {
            ["ApiKey"] = "key-123", ["ApiSecret"] = "secret-456", ["AccountNumber"] = "123456789",
            ["ShipperCompanyName"] = "Acme Traders"
        };

        change?.Invoke(credentials);
        return credentials;
    }

    internal static CourierBookingRequest Booking(Func<CourierBookingRequest, CourierBookingRequest>? change = null)
    {
        var request = new CourierBookingRequest(
            IdempotencyKey:    Guid.NewGuid().ToString("N"),
            ConsignmentNumber: "SHP-2026-00001",
            ServiceCode:       "N",
            ShipFrom:          new CourierAddress("Central Warehouse", "+922111222333", null, "12 Dock Road", null, "Karachi", "Sindh", "74000", "PK"),
            ShipTo:            new CourierAddress("Ali Raza", "+923001234567", "ali@example.com", "4 Industrial Estate", null, "Lahore", "Punjab", "54000", "PK"),
            Packages:          [new CourierPackage("HU-2026-00001", "BOX", 40m, 30m, 20m, 12.5m, 1500m)],
            FreightTerms:      "PREPAID",
            CodAmount:         null,
            CodCurrency:       null,
            PickupWindowStart: null,
            PickupWindowEnd:   null,
            ReferenceNumbers:  ["DLV-2026-00001", "SO-2026-00002"],
            Credentials:       Credentials());

        return change?.Invoke(request) ?? request;
    }

    internal static CourierRateRequest Rating(Func<CourierRateRequest, CourierRateRequest>? change = null)
    {
        var booking = Booking();

        var request = new CourierRateRequest(
            booking.ConsignmentNumber, null, booking.ShipFrom, booking.ShipTo, booking.Packages,
            booking.FreightTerms, null, null, null, booking.Credentials);

        return change?.Invoke(request) ?? request;
    }
}

public class DhlExpressProviderTests
{
    private sealed record Harness(FakeDhlServer Server, DhlExpressCourierProvider Provider);

    private static Harness NewHarness()
    {
        var server = new FakeDhlServer();

        return new Harness(server, new DhlExpressCourierProvider(
            server, NullLogger<DhlExpressCourierProvider>.Instance, new DhlFixtures.FixedClock(DhlFixtures.Now)));
    }

    private static CourierBookingRequest WithCredentials(
        CourierBookingRequest request, Action<Dictionary<string, string>> change) =>
        request with { Credentials = DhlFixtures.Credentials(change) };

    // ── Identity ──────────────────────────────────────────────────────────────

    [Fact]
    public void It_says_which_dhl_it_is_and_what_it_needs_in_the_vault()
    {
        var provider = NewHarness().Provider;

        provider.Key.Should().Be("DHL_EXPRESS");

        var required = provider.Credentials.Where(c => c.Required).Select(c => c.Key);
        required.Should().BeEquivalentTo("ApiKey", "ApiSecret", "AccountNumber");

        provider.Credentials.Where(c => c.IsSecret).Select(c => c.Key)
                .Should().BeEquivalentTo("ApiKey", "ApiSecret");
    }

    [Fact]
    public void It_claims_only_what_the_myDHL_api_can_do()
    {
        var capabilities = NewHarness().Provider.Capabilities;

        capabilities.SupportsBooking.Should().BeTrue();
        capabilities.SupportsRating.Should().BeTrue();
        capabilities.SupportsTracking.Should().BeTrue();
        capabilities.SupportsMultiPiece.Should().BeTrue();
        capabilities.SupportsCancellation.Should().BeFalse("MyDHL has no call to cancel a shipment");
        capabilities.SupportsLabels.Should().BeFalse("the label comes back once, with the booking");
        capabilities.SupportsCod.Should().BeFalse();
        capabilities.HonoursIdempotencyKey.Should().BeFalse("DHL has no idempotency key, so the ledger is the guard");
    }

    [Fact]
    public void The_container_can_build_it_and_the_registry_finds_it_by_key()
    {
        // The unit tests construct the provider by hand. What they cannot see is whether the container can:
        // it has an optional TimeProvider that nothing registers, and it needs the HTTP client factory.
        var services = new ServiceCollection();
        services.AddSingleton<ICourierProvider, ManualCourierProvider>();
        services.AddLogging();
        services.AddHttpClient(DhlExpressCourierProvider.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(60));
        services.AddSingleton<ICourierProvider, DhlExpressCourierProvider>();
        services.AddSingleton<ICourierProviderRegistry, CourierProviderRegistry>();

        using var container = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var registry = container.GetRequiredService<ICourierProviderRegistry>();

        registry.Find("dhl_express").Should().BeOfType<DhlExpressCourierProvider>();
        registry.All.Select(p => p.Key).Should().Contain("DHL_EXPRESS").And.Contain("MANUAL");
    }

    // ── How it talks to DHL ───────────────────────────────────────────────────

    [Fact]
    public async Task It_authenticates_with_basic_auth_and_defaults_to_the_test_environment()
    {
        var h = NewHarness();

        await h.Provider.BookAsync(DhlFixtures.Booking());

        var call = h.Server.Calls.Single();
        call.Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("key-123:secret-456")));
        call.Uri.ToString().Should().Be("https://express.api.dhl.com/mydhlapi/test/shipments",
            "an account that never said LIVE must never book a real parcel");
    }

    [Fact]
    public async Task Live_is_only_used_when_the_account_says_so()
    {
        var h = NewHarness();

        await h.Provider.BookAsync(WithCredentials(DhlFixtures.Booking(), c => c["Environment"] = "live"));

        h.Server.Calls.Single().Uri.ToString().Should().Be("https://express.api.dhl.com/mydhlapi/shipments");
    }

    [Fact]
    public async Task An_environment_that_is_neither_test_nor_live_is_refused_without_a_call()
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(WithCredentials(DhlFixtures.Booking(), c => c["Environment"] = "STAGING"));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("TEST or LIVE");
        h.Server.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData("http://insecure.example.com")]
    [InlineData("ftp://x")]
    public async Task A_base_url_that_is_not_https_is_refused(string url)
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(WithCredentials(DhlFixtures.Booking(), c => c["BaseUrl"] = url));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("https");
        h.Server.Calls.Should().BeEmpty("credentials must never be sent over an unencrypted address");
    }

    [Theory]
    [InlineData("ApiKey")]
    [InlineData("ApiSecret")]
    [InlineData("AccountNumber")]
    public async Task A_missing_credential_is_refused_by_name_before_anything_is_sent(string missing)
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(WithCredentials(DhlFixtures.Booking(), c => c.Remove(missing)));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain(missing);
        h.Server.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Tracking_needs_no_account_number()
    {
        var h = NewHarness();
        var booked = await h.Provider.BookAsync(DhlFixtures.Booking());

        var credentials = DhlFixtures.Credentials(c => c.Remove("AccountNumber"));
        var tracked = await h.Provider.TrackAsync(new CourierTrackingRequest(booked.AwbNumber!, null, credentials));

        tracked.Outcome.Should().Be(CourierOutcome.Succeeded);
    }

    // ── What it sends ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_shipment_carries_everything_dhl_makes_mandatory()
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking());

        result.Outcome.Should().Be(CourierOutcome.Succeeded, result.Message);

        var body = h.Server.Calls.Single().Json!;

        body["productCode"]!.GetValue<string>().Should().Be("N");
        body["accounts"]![0]!["typeCode"]!.GetValue<string>().Should().Be("shipper");
        body["accounts"]![0]!["number"]!.GetValue<string>().Should().Be("123456789");

        var from = body["customerDetails"]!["shipperDetails"]!;
        from["postalAddress"]!["postalCode"]!.GetValue<string>().Should().Be("74000");
        from["postalAddress"]!["countryCode"]!.GetValue<string>().Should().Be("PK");
        from["postalAddress"]!["addressLine1"]!.GetValue<string>().Should().Be("12 Dock Road");
        from["contactInformation"]!["companyName"]!.GetValue<string>().Should().Be("Acme Traders");
        from["contactInformation"]!["fullName"]!.GetValue<string>().Should().Be("Central Warehouse");

        var to = body["customerDetails"]!["receiverDetails"]!;
        to["contactInformation"]!["email"]!.GetValue<string>().Should().Be("ali@example.com");
        to["contactInformation"]!["companyName"]!.GetValue<string>().Should().Be("Ali Raza",
            "a person with no company is their own — DHL insists on both");

        var content = body["content"]!;
        content["isCustomsDeclarable"]!.GetValue<bool>().Should().BeFalse();
        content["incoterm"]!.GetValue<string>().Should().Be("DAP");
        content["unitOfMeasurement"]!.GetValue<string>().Should().Be("metric");
        content["description"]!.GetValue<string>().Should().Be("DLV-2026-00001, SO-2026-00002");

        var piece = content["packages"]![0]!;
        piece["weight"]!.GetValue<decimal>().Should().Be(12.5m);
        piece["dimensions"]!["length"]!.GetValue<decimal>().Should().Be(40m);
        piece["customerReferences"]![0]!["value"]!.GetValue<string>().Should().Be("HU-2026-00001",
            "the barcode rides along so a scan can be traced to our package");

        body["customerReferences"]![0]!["value"]!.GetValue<string>().Should().Be("SHP-2026-00001");
    }

    [Fact]
    public async Task The_ship_time_is_in_dhls_format_and_never_in_the_past()
    {
        var h = NewHarness();

        await h.Provider.BookAsync(DhlFixtures.Booking(r => r with { PickupWindowStart = DhlFixtures.Now.AddHours(-5) }));

        var stamp = h.Server.Calls.Single().Json!["plannedShippingDateAndTime"]!.GetValue<string>();

        stamp.Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2} GMT\+00:00$");
        stamp.Should().Be("2026-09-21T09:30:00 GMT+00:00", "a request stamped in the past arrives already gone, so it is moved to now + 30 minutes");
    }

    [Fact]
    public async Task A_pickup_window_asks_dhl_to_collect_and_no_window_does_not()
    {
        var h = NewHarness();

        await h.Provider.BookAsync(DhlFixtures.Booking());
        await h.Provider.BookAsync(DhlFixtures.Booking(r => r with { PickupWindowStart = DhlFixtures.Now.AddDays(1) }));

        h.Server.Calls[0].Json!["pickup"]!["isRequested"]!.GetValue<bool>().Should().BeFalse();
        h.Server.Calls[1].Json!["pickup"]!["isRequested"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task A_long_street_is_wrapped_at_a_space_across_dhls_three_lines()
    {
        var h = NewHarness();
        var street = "Plot 14, Block C, Gulberg Industrial Estate, Main Boulevard Near The Old Railway Crossing";

        await h.Provider.BookAsync(DhlFixtures.Booking(r => r with { ShipTo = r.ShipTo with { Line1 = street } }));

        var to = h.Server.Calls.Single().Json!["customerDetails"]!["receiverDetails"]!["postalAddress"]!;
        var lines = new[] { "addressLine1", "addressLine2", "addressLine3" }
            .Select(k => to[k]?.GetValue<string>()).Where(l => l is not null).ToList();

        lines.Should().OnlyContain(l => l!.Length <= 45);
        string.Join(" ", lines).Should().Be(street, "nothing may be cut from an address");
    }

    [Fact]
    public async Task An_address_that_will_not_fit_is_refused_and_never_cut()
    {
        var h = NewHarness();
        var absurd = string.Join(" ", Enumerable.Repeat("Somewhere-Extraordinarily-Long", 6));

        var result = await h.Provider.BookAsync(DhlFixtures.Booking(r => r with { ShipTo = r.ShipTo with { Line1 = absurd } }));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("ship-to").And.Contain("Shorten");
        h.Server.Calls.Should().BeEmpty();
    }

    // ── What it refuses before asking ─────────────────────────────────────────

    [Fact]
    public async Task A_missing_ship_to_postal_code_is_refused_and_says_where_to_add_it()
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking(r => r with { ShipTo = r.ShipTo with { PostalCode = null } }));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("ship-to").And.Contain("postal code");
        h.Server.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_warehouse_with_no_postal_code_is_collected_from_using_the_accounts_shipper_postal_code()
    {
        // The warehouse master has no postal-code field, so an address built from it never has one.
        var h = NewHarness();
        var request = WithCredentials(
            DhlFixtures.Booking(r => r with { ShipFrom = r.ShipFrom with { PostalCode = null } }),
            c => c["ShipperPostalCode"] = "38000");

        var result = await h.Provider.BookAsync(request);

        result.Outcome.Should().Be(CourierOutcome.Succeeded, result.Message);
        h.Server.Calls.Single().Json!["customerDetails"]!["shipperDetails"]!["postalAddress"]!["postalCode"]!
            .GetValue<string>().Should().Be("38000");
    }

    [Fact]
    public async Task Without_either_the_ship_from_refusal_names_the_credential_that_would_fix_it()
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking(r => r with { ShipFrom = r.ShipFrom with { PostalCode = null } }));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("ShipperPostalCode");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("P")]
    public async Task A_country_code_that_is_missing_or_not_two_letters_is_refused(string? iso)
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking(r => r with { ShipTo = r.ShipTo with { CountryIsoCode = iso } }));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("country code");
    }

    [Fact]
    public async Task A_contact_with_no_phone_is_refused()
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking(r => r with { ShipTo = r.ShipTo with { ContactPhone = " " } }));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("phone");
    }

    [Fact]
    public async Task A_package_with_no_weight_is_refused_because_dhl_prices_by_weight()
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking(r => r with
        {
            Packages = [new CourierPackage("HU-2026-00009", "BOX", 10m, 10m, 10m, null, null)]
        }));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("HU-2026-00009").And.Contain("weight");
    }

    [Fact]
    public async Task A_booking_with_no_service_is_refused_asking_for_one()
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking(r => r with { ServiceCode = null }));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("product code");
    }

    [Theory]
    [InlineData("COLLECT")]
    [InlineData("THIRD_PARTY")]
    public async Task Only_prepaid_freight_can_be_booked_because_the_payers_account_is_not_held(string terms)
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking(r => r with { FreightTerms = terms }));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("prepaid");
        h.Server.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_parcel_that_crosses_a_border_is_refused_rather_than_sent_with_an_invented_customs_declaration()
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking(r => r with
        {
            ShipTo = r.ShipTo with { CountryIsoCode = "AE", City = "Dubai", PostalCode = "00000" }
        }));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("customs").And.Contain("HS code").And.Contain("portal");
        result.AwbNumber.Should().BeNull();
        h.Server.Calls.Should().BeEmpty("a false declaration on a real parcel is worse than a refusal");
    }

    // ── What DHL answers ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_booking_returns_the_airway_bill_the_piece_reference_and_the_label()
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking());

        result.Outcome.Should().Be(CourierOutcome.Succeeded);
        result.AwbNumber.Should().Be("1000000000");
        result.CarrierReference.Should().StartWith("JD01460000");
        result.TrackingUrl.Should().BeNull("DHL's own trackingUrl is an API address nobody can open");

        result.Label.Should().NotBeNull();
        result.Label!.ContentType.Should().Be("application/pdf");
        result.Label.FileName.Should().Be("1000000000.pdf");
        Encoding.ASCII.GetString(result.Label.Content).Should().StartWith("%PDF-");
    }

    [Fact]
    public async Task The_kept_diagnostic_copy_of_the_answer_does_not_carry_the_label_image()
    {
        var h = NewHarness();

        var result = await h.Provider.BookAsync(DhlFixtures.Booking());

        result.RawResponse.Should().Contain("shipmentTrackingNumber");
        result.RawResponse.Should().Contain("base64 removed").And.NotContain("JVBER");
    }

    [Fact]
    public async Task A_validation_error_is_dhls_answer_and_carries_dhls_own_words()
    {
        var h = NewHarness();
        h.Server.Respond = _ => FakeDhlServer.Reply((HttpStatusCode)422, new JsonObject
        {
            ["title"] = "Validation error", ["detail"] = "#/content: required key [incoterm] not found",
            ["additionalDetails"] = new JsonArray("Product code N is not valid for this account")
        });

        var result = await h.Provider.BookAsync(DhlFixtures.Booking());

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("required key [incoterm]").And.Contain("Product code N is not valid");
        result.CarrierErrorCode.Should().Be("422");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Every_other_4xx_is_a_refusal_and_never_an_unknown(int status)
    {
        // 429 too: DHL did not look at the request, so trying again is safe.
        var h = NewHarness();
        h.Server.Respond = _ => FakeDhlServer.Reply((HttpStatusCode)status, FakeDhlServer.Error("Nope", "DHL says no", status));

        (await h.Provider.BookAsync(DhlFixtures.Booking())).Outcome.Should().Be(CourierOutcome.Refused);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task A_server_error_is_unknown_because_a_parcel_may_have_been_created(int status)
    {
        // DHL has no idempotency key. Calling this "refused" would invite a second booking.
        var h = NewHarness();
        h.Server.Respond = _ => FakeDhlServer.Reply((HttpStatusCode)status,
            FakeDhlServer.Error("Internal Server Error", "999: Process failure occurred", status));

        var result = await h.Provider.BookAsync(DhlFixtures.Booking());

        result.Outcome.Should().Be(CourierOutcome.Failed);
        result.AwbNumber.Should().BeNull();
    }

    [Fact]
    public async Task A_dropped_connection_is_unknown_not_refused()
    {
        var h = NewHarness();
        h.Server.Throw = new HttpRequestException("connection reset");

        var result = await h.Provider.BookAsync(DhlFixtures.Booking());

        result.Outcome.Should().Be(CourierOutcome.Failed);
        result.Message.Should().Contain("not known");
    }

    [Fact]
    public async Task A_timeout_is_unknown_not_a_cancellation()
    {
        // HttpClient reports its own timeout as a cancelled task. It is not the caller cancelling.
        var h = NewHarness();
        h.Server.Throw = new TaskCanceledException("timed out");

        var result = await h.Provider.BookAsync(DhlFixtures.Booking());

        result.Outcome.Should().Be(CourierOutcome.Failed);
    }

    [Fact]
    public async Task A_success_with_no_tracking_number_is_unknown_and_never_a_refusal()
    {
        var h = NewHarness();
        h.Server.Respond = _ => FakeDhlServer.Reply(HttpStatusCode.Created, new JsonObject { ["packages"] = new JsonArray() });

        var result = await h.Provider.BookAsync(DhlFixtures.Booking());

        result.Outcome.Should().Be(CourierOutcome.Failed);
        result.Message.Should().Contain("portal");
    }

    [Fact]
    public async Task A_booking_still_succeeds_when_dhl_sends_no_label()
    {
        var h = NewHarness();
        h.Server.Respond = _ => FakeDhlServer.Reply(HttpStatusCode.Created,
            new JsonObject { ["shipmentTrackingNumber"] = "5555555555" });

        var result = await h.Provider.BookAsync(DhlFixtures.Booking());

        result.Outcome.Should().Be(CourierOutcome.Succeeded);
        result.Label.Should().BeNull();
    }

    // ── Rating ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rating_quotes_what_would_be_billed_in_the_billing_currency()
    {
        var h = NewHarness();

        var result = await h.Provider.RateAsync(DhlFixtures.Rating());

        result.Outcome.Should().Be(CourierOutcome.Succeeded);

        var domestic = result.Options.Single(o => o.ServiceCode == "N");
        domestic.TotalAmount.Should().Be(1500m, "BILLC, not the 9.99 base-currency figure");
        domestic.Currency.Should().Be("PKR");
        domestic.ServiceName.Should().Be("EXPRESS DOMESTIC");
        domestic.TransitDays.Should().Be(1);
        domestic.IsGuaranteed.Should().BeTrue("QDDC is DHL's service commitment");
        result.Options.Single(o => o.ServiceCode == "P").IsGuaranteed.Should().BeFalse("QDDF is only its fastest transit time");
        domestic.ChargeableWeightKg.Should().Be(12.5m);
    }

    [Fact]
    public async Task A_quote_is_itemised_when_the_lines_add_up_to_the_total()
    {
        var result = await NewHarness().Provider.RateAsync(DhlFixtures.Rating());

        var domestic = result.Options.Single(o => o.ServiceCode == "N");

        domestic.BaseAmount.Should().Be(1200m);
        domestic.Surcharges.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new CourierSurcharge("FF", "FUEL SURCHARGE", 300m));
    }

    [Fact]
    public async Task A_quote_whose_lines_do_not_add_up_is_given_with_a_total_and_no_invented_split()
    {
        var h = NewHarness();
        h.Server.Respond = call =>
        {
            if (!call.Path.EndsWith("/rates")) return null;

            // Base 1200 + fuel 300 = 1500, but DHL says 1700: something is missing from the breakdown.
            return FakeDhlServer.Reply(HttpStatusCode.OK, new JsonObject
            {
                ["products"] = new JsonArray(FakeDhlServer.Product("N", "EXPRESS DOMESTIC", 1700m, 1200m, 300m, "QDDC", 1))
            });
        };

        var option = (await h.Provider.RateAsync(DhlFixtures.Rating())).Options.Single();

        option.TotalAmount.Should().Be(1700m);
        option.BaseAmount.Should().BeNull("a split that does not reconcile is a dispute manufactured here");
        option.Surcharges.Should().BeNull();
    }

    [Fact]
    public async Task Tax_is_a_named_line_so_a_taxed_quote_still_adds_up()
    {
        var h = NewHarness();
        h.Server.Respond = call =>
        {
            if (!call.Path.EndsWith("/rates")) return null;

            var product = FakeDhlServer.Product("N", "EXPRESS DOMESTIC", 1740m, 1200m, 300m, "QDDC", 1);
            product["totalPriceBreakdown"]![0]!["priceBreakdown"]![0]!["price"] = 240; // STTXA

            return FakeDhlServer.Reply(HttpStatusCode.OK, new JsonObject { ["products"] = new JsonArray(product) });
        };

        var option = (await h.Provider.RateAsync(DhlFixtures.Rating())).Options.Single();

        option.BaseAmount.Should().Be(1200m);
        option.Surcharges!.Select(s => s.Code).Should().BeEquivalentTo("FF", "TAX");
        (option.BaseAmount!.Value + option.Surcharges!.Sum(s => s.Amount)).Should().Be(option.TotalAmount);
    }

    [Fact]
    public async Task Naming_a_service_asks_dhl_for_that_service_and_returns_only_it()
    {
        var h = NewHarness();

        var result = await h.Provider.RateAsync(DhlFixtures.Rating(r => r with { ServiceCode = "P" }));

        result.Options.Should().ContainSingle().Which.ServiceCode.Should().Be("P");
        h.Server.Calls.Single().Json!["productsAndServices"]![0]!["productCode"]!.GetValue<string>().Should().Be("P");
    }

    [Fact]
    public async Task A_service_dhl_does_not_offer_is_a_refusal_naming_it()
    {
        var result = await NewHarness().Provider.RateAsync(DhlFixtures.Rating(r => r with { ServiceCode = "Z" }));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Message.Should().Contain("'Z'");
        result.Options.Should().BeEmpty();
    }

    [Fact]
    public async Task Rating_sends_dhls_flat_address_shape_and_marks_a_border_crossing_dutiable()
    {
        var h = NewHarness();

        await h.Provider.RateAsync(DhlFixtures.Rating());
        await h.Provider.RateAsync(DhlFixtures.Rating(r => r with { ShipTo = r.ShipTo with { CountryIsoCode = "AE", City = "Dubai" } }));

        var domestic = h.Server.Calls[0].Json!;
        domestic["customerDetails"]!["shipperDetails"]!["postalCode"]!.GetValue<string>().Should().Be("74000");
        domestic["isCustomsDeclarable"]!.GetValue<bool>().Should().BeFalse();
        domestic["packages"]![0]!["weight"]!.GetValue<decimal>().Should().Be(12.5m);

        h.Server.Calls[1].Json!["isCustomsDeclarable"]!.GetValue<bool>().Should().BeTrue(
            "rating across a border is allowed — it needs no item list, only the booking does");
    }

    [Fact]
    public async Task A_rating_read_failure_is_failed_so_the_caller_falls_back_to_its_rate_card()
    {
        var h = NewHarness();
        h.Server.Throw = new HttpRequestException("no route to host");

        var result = await h.Provider.RateAsync(DhlFixtures.Rating());

        result.Outcome.Should().Be(CourierOutcome.Failed);
        result.Options.Should().BeEmpty();
    }

    [Fact]
    public async Task An_answer_with_no_products_is_a_refusal_not_a_success_that_priced_nothing()
    {
        var h = NewHarness();
        h.Server.Respond = call => call.Path.EndsWith("/rates")
            ? FakeDhlServer.Reply(HttpStatusCode.OK, new JsonObject { ["products"] = new JsonArray() })
            : null;

        var result = await h.Provider.RateAsync(DhlFixtures.Rating());

        result.Outcome.Should().Be(CourierOutcome.Refused);
    }

    // ── Cancellation and labels ───────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_says_plainly_that_dhls_api_cannot_and_touches_nothing()
    {
        var h = NewHarness();

        var result = await h.Provider.CancelAsync(new CourierCancelRequest("k", "SHP-1", "1000000000", DhlFixtures.Credentials()));

        result.Outcome.Should().Be(CourierOutcome.Unsupported);
        result.Message.Should().Contain("portal");
        h.Server.Calls.Should().BeEmpty();
    }

    // ── Tracking ──────────────────────────────────────────────────────────────

    private static HttpResponseMessage TrackingWith(params JsonObject[] events) =>
        FakeDhlServer.Reply(HttpStatusCode.OK, new JsonObject
        {
            ["shipments"] = new JsonArray(new JsonObject
            {
                ["shipmentTrackingNumber"] = "1000000000", ["events"] = new JsonArray(events.Cast<JsonNode>().ToArray())
            })
        });

    private static Task<CourierTrackingResult> Track(Harness h, string awb = "1000000000") =>
        h.Provider.TrackAsync(new CourierTrackingRequest(awb, null, DhlFixtures.Credentials()));

    [Fact]
    public async Task Events_come_back_oldest_first_however_dhl_lists_them()
    {
        var h = NewHarness();
        var booked = await h.Provider.BookAsync(DhlFixtures.Booking());

        var result = await Track(h, booked.AwbNumber!);

        result.Outcome.Should().Be(CourierOutcome.Succeeded);
        result.Events.Select(e => e.CarrierStatus).Should().Equal("PU", "DF", "WC");
    }

    [Fact]
    public async Task Local_event_times_are_turned_into_utc_with_the_offset_dhl_sends()
    {
        // 14:05 in Karachi (+05:00) is 09:05 UTC. Read as UTC it would be five hours ahead of the truth, past
        // the recorder's tolerance for the future, and thrown away as invalid until the clock caught up.
        var h = NewHarness();
        var booked = await h.Provider.BookAsync(DhlFixtures.Booking());

        var pickedUp = (await Track(h, booked.AwbNumber!)).Events.First();

        pickedUp.OccurredAt.Should().Be(new DateTime(2026, 9, 18, 9, 5, 0, DateTimeKind.Utc));
        pickedUp.Milestone.Should().Be("PICKED_UP");
        pickedUp.Location.Should().Be("Karachi-PK");
    }

    [Theory]
    [InlineData("-05:00", 19)]
    [InlineData("+00:00", 14)]
    [InlineData("+09:30", 4)]
    [InlineData("nonsense", 14)]
    [InlineData("+99:00", 14)]
    public async Task Every_offset_is_honoured_and_a_malformed_one_is_read_as_none(string offset, int expectedUtcHour)
    {
        var h = NewHarness();
        h.Server.Respond = _ => TrackingWith(FakeDhlServer.Event("2026-09-18", "14:00:00", offset, "PU", "Picked up", "Lahore-PK"));

        var when = (await Track(h)).Events.Single().OccurredAt;

        when.Hour.Should().Be(expectedUtcHour);
    }

    [Theory]
    [InlineData("PU", "PICKED_UP")]
    [InlineData("AF", "ARRIVED_AT_HUB")]
    [InlineData("AR", "ARRIVED_AT_HUB")]
    [InlineData("PL", "IN_TRANSIT")]
    [InlineData("DF", "DEPARTED_HUB")]
    [InlineData("WC", "OUT_FOR_DELIVERY")]
    [InlineData("OK", "DELIVERED")]
    [InlineData("RR", "INFO_RECEIVED")]
    [InlineData("CR", "INFO_RECEIVED")]
    public void The_codes_dhls_own_sample_documents_map_as_documented(string code, string milestone)
    {
        LogisticsCodeOf(DhlTrackingMap.MilestoneFor(code, "anything")).Should().Be(milestone);
    }

    [Fact]
    public void Delivered_comes_from_the_code_OK_and_from_nothing_else()
    {
        // DELIVERED is what lets a sale order be invoiced. No description, however hopeful, can set it.
        foreach (var words in new[] { "Shipment delivered", "Delivered to neighbour", "DELIVERED", "Successfully delivered" })
            LogisticsCodeOf(DhlTrackingMap.MilestoneFor("ZZ", words)).Should().NotBe("DELIVERED", words);
    }

    [Theory]
    [InlineData("Clearance delay - customs hold", "CUSTOMS_HOLD")]
    [InlineData("Delivery attempted, recipient not home", "DELIVERY_ATTEMPTED")]
    [InlineData("Shipment on hold", "EXCEPTION")]
    [InlineData("Returning to shipper", "RETURN_INITIATED")]
    [InlineData("Something nobody documented", "INFO_RECEIVED")]
    public void A_code_dhl_has_not_documented_gets_the_most_cautious_milestone_its_words_support(string words, string milestone)
    {
        LogisticsCodeOf(DhlTrackingMap.MilestoneFor("XX", words)).Should().Be(milestone);
    }

    [Fact]
    public void An_undocumented_code_never_ends_a_consignments_life_by_guesswork()
    {
        foreach (var words in new[] { "returned to shipper", "return complete", "shipment returned" })
            LogisticsCodeOf(DhlTrackingMap.MilestoneFor("XX", words)).Should().NotBe("RETURNED", words);
    }

    [Fact]
    public async Task The_person_who_signed_is_kept_when_dhl_says_who_and_left_out_when_it_does_not()
    {
        var h = NewHarness();
        var signed   = FakeDhlServer.Event("2026-09-19", "11:00:00", "+05:00", "OK", "Delivered", "Lahore-PK");
        signed["signedBy"] = "A. Khan";
        var unsigned = FakeDhlServer.Event("2026-09-18", "11:00:00", "+05:00", "PU", "Picked up", "Karachi-PK");

        h.Server.Respond = _ => TrackingWith(signed, unsigned);

        var events = (await Track(h)).Events;

        events[0].SignedBy.Should().BeNull("an empty string is not a name");
        events[1].SignedBy.Should().Be("A. Khan");
        events[1].Milestone.Should().Be("DELIVERED");
    }

    [Fact]
    public async Task A_number_dhl_does_not_know_is_a_refusal_whether_it_says_404_or_200_with_nothing()
    {
        var h = NewHarness();

        (await Track(h, "9999999999")).Outcome.Should().Be(CourierOutcome.Refused);

        h.Server.Respond = _ => FakeDhlServer.Reply(HttpStatusCode.OK, new JsonObject { ["shipments"] = new JsonArray() });

        var result = await Track(h);
        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task An_event_with_an_unreadable_time_is_skipped_and_does_not_lose_the_others()
    {
        var h = NewHarness();
        var broken = FakeDhlServer.Event("not-a-date", "??", "+05:00", "PU", "Picked up", "Karachi-PK");
        var good   = FakeDhlServer.Event("2026-09-18", "11:00:00", "+05:00", "AF", "Arrived", "Lahore-PK");

        h.Server.Respond = _ => TrackingWith(broken, good);

        (await Track(h)).Events.Should().ContainSingle().Which.CarrierStatus.Should().Be("AF");
    }

    [Fact]
    public async Task Tracking_asks_for_every_checkpoint_of_the_one_shipment()
    {
        var h = NewHarness();
        var booked = await h.Provider.BookAsync(DhlFixtures.Booking());

        await Track(h, booked.AwbNumber!);

        var uri = h.Server.Calls.Last().Uri.ToString();
        uri.Should().Contain("shipmentTrackingNumber=1000000000").And.Contain("trackingView=all-checkpoints");
    }

    private static string LogisticsCodeOf(SMS.Modules.Logistics.Domain.TrackingMilestone m) =>
        SMS.Modules.Logistics.Domain.LogisticsCode.Of(m);
}
