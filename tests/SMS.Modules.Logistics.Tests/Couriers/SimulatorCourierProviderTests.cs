using FluentAssertions;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Simulator;
using SMS.Modules.Logistics.Domain;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

/// <summary>The contract suite, against the first adapter anybody would actually use.</summary>
public class SimulatorCourierProviderContractTests : CourierProviderContractTests
{
    protected override ICourierProvider CreateProvider() => new SimulatorCourierProvider();

    protected override CourierBookingRequest? RefusableRequest() =>
        BookableRequest() with { ServiceCode = "SIM-REFUSE" };
}

// T-32 — what is particular to the simulator, on top of the contract it shares with every adapter.
public class SimulatorCourierProviderTests
{
    private static SimulatorCourierProvider Provider() => new();

    private static CourierBookingRequest Request(
        string? idempotencyKey = null, string? serviceCode = null,
        decimal? cod = null, string? codCurrency = null,
        IReadOnlyList<CourierPackage>? packages = null) =>
        new(
            IdempotencyKey:    idempotencyKey ?? Guid.NewGuid().ToString("N"),
            ConsignmentNumber: "SHP-2026-00001",
            ServiceCode:       serviceCode,
            ShipFrom:          new CourierAddress("Warehouse", null, null, "12 Dock Road", null,
                                                  "Karachi", "Sindh", "74000", "PK"),
            ShipTo:            new CourierAddress("Acme Ltd", null, null, "4 Industrial Estate", null,
                                                  "Lahore", "Punjab", "54000", "PK"),
            Packages:          packages ?? [new CourierPackage("HU-1", "BOX", 40m, 30m, 20m, 12.5m, 1500m)],
            FreightTerms:      "PREPAID",
            CodAmount:         cod,
            CodCurrency:       codCurrency,
            PickupWindowStart: null,
            PickupWindowEnd:   null,
            ReferenceNumbers:  ["DLV-2026-00001"],
            Credentials:       new Dictionary<string, string>());

    private static IReadOnlyDictionary<string, string> NoCredentials =>
        new Dictionary<string, string>();

    // ── Statelessness ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_same_key_books_the_same_airway_bill_on_a_different_instance()
    {
        // The reason the airway bill is a pure function of the key: a simulator that remembered
        // its bookings in a dictionary would lose them on the first app recycle — which is
        // precisely when a demo is being watched — and would disagree between instances.
        var key = "order-42";

        var first  = await Provider().BookAsync(Request(key));
        var second = await Provider().BookAsync(Request(key));

        first.AwbNumber.Should().Be(second.AwbNumber);
    }

    [Fact]
    public async Task A_booking_can_be_tracked_by_an_instance_that_never_saw_it()
    {
        var booking = await Provider().BookAsync(Request());

        var result = await Provider().TrackAsync(
            new CourierTrackingRequest(booking.AwbNumber!, null, NoCredentials));

        result.Succeeded().Should().BeTrue();
        result.Events.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Changing_anything_but_the_key_does_not_change_the_airway_bill()
    {
        // It is keyed on idempotency, not on content. Two retries of one booking that differ in
        // some incidental field are still the same booking.
        var key = "order-42";

        var plain  = await Provider().BookAsync(Request(key));
        var heavier = await Provider().BookAsync(Request(key,
            packages: [new CourierPackage("HU-9", "CRATE", 90m, 90m, 90m, 60m, 9000m)]));

        heavier.AwbNumber.Should().Be(plain.AwbNumber);
    }

    // ── Scenarios ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_refusal_scenario_is_an_answer_with_a_reason()
    {
        var result = await Provider().BookAsync(Request(serviceCode: "SIM-REFUSE"));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.AwbNumber.Should().BeNull();
        result.CarrierErrorCode.Should().Be("SIM_UNSERVICEABLE");
    }

    [Fact]
    public async Task A_failure_scenario_says_the_outcome_is_unknown_rather_than_throwing()
    {
        // The T-36 case. Throwing would make a call that may have created a real parcel look
        // like one that did nothing.
        var result = await Provider().BookAsync(Request(serviceCode: "SIM-FAIL"));

        result.Outcome.Should().Be(CourierOutcome.Failed);
        result.AwbNumber.Should().BeNull();
        result.Message.Should().Contain("unknown");
    }

    [Theory]
    [InlineData("SIM-DELIVERED",  "DELIVERED")]
    [InlineData("SIM-IN-TRANSIT", "IN_TRANSIT")]
    [InlineData("SIM-EXCEPTION",  "DELIVERED")]
    [InlineData("SIM-RETURNED",   "RETURNED")]
    public async Task Each_journey_scenario_ends_where_it_says_it_does(string code, string lastMilestone)
    {
        var booking = await Provider().BookAsync(Request(serviceCode: code));
        booking.Succeeded.Should().BeTrue();

        var result = await Provider().TrackAsync(
            new CourierTrackingRequest(booking.AwbNumber!, null, NoCredentials));

        result.Events.Should().NotBeEmpty();
        result.Events[^1].Milestone.Should().Be(lastMilestone);
    }

    [Fact]
    public async Task The_exception_journey_actually_passes_through_an_exception()
    {
        var booking = await Provider().BookAsync(Request(serviceCode: "SIM-EXCEPTION"));

        var result = await Provider().TrackAsync(
            new CourierTrackingRequest(booking.AwbNumber!, null, NoCredentials));

        result.Events.Select(e => e.Milestone).Should().Contain("CUSTOMS_HOLD");
    }

    [Fact]
    public async Task Service_codes_are_matched_however_they_are_typed()
    {
        var result = await Provider().BookAsync(Request(serviceCode: "  sim-refuse  "));

        result.Outcome.Should().Be(CourierOutcome.Refused);
    }

    [Fact]
    public async Task An_unrecognised_service_code_books_normally_rather_than_failing()
    {
        // A carrier's real service codes will be passed through here during a demo. Treating an
        // unknown one as an error would make the simulator unusable with realistic data.
        var result = await Provider().BookAsync(Request(serviceCode: "EXPRESS-0900"));

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Bookings_with_no_scenario_are_spread_across_journeys()
    {
        // So a demo with no special setup shows parcels at a mix of stages rather than fifty
        // identical ones.
        var provider = Provider();
        var endings = new HashSet<string>();

        foreach (var i in Enumerable.Range(0, 40))
        {
            var booking = await provider.BookAsync(Request($"key-{i}"));
            var track = await provider.TrackAsync(
                new CourierTrackingRequest(booking.AwbNumber!, null, NoCredentials));
            endings.Add(track.Events[^1].Milestone);
        }

        endings.Count.Should().BeGreaterThan(1);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_cod_amount_without_a_currency_is_refused()
    {
        var result = await Provider().BookAsync(Request(cod: 5000m));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.CarrierErrorCode.Should().Be("COD_CURRENCY_MISSING");
    }

    [Fact]
    public async Task A_cod_amount_with_a_currency_is_accepted()
    {
        var result = await Provider().BookAsync(Request(cod: 5000m, codCurrency: "PKR"));

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("SIM-A123456789")]      // wrong scenario letter position / length
    [InlineData("SIM-AZZZZZZZZZZ")]     // not hexadecimal
    [InlineData("SIM-Z0123456789")]     // unknown scenario letter
    [InlineData("AWB-NEVER-EXISTED")]
    [InlineData("")]
    public async Task An_airway_bill_it_never_issued_is_refused_everywhere(string awb)
    {
        // How a stateless adapter still refuses to label or track a number it has never seen.
        var provider = Provider();

        (await provider.GetLabelAsync(awb, NoCredentials)).Outcome
            .Should().NotBe(CourierOutcome.Succeeded);

        (await provider.TrackAsync(new CourierTrackingRequest(awb, null, NoCredentials))).Outcome
            .Should().NotBe(CourierOutcome.Succeeded);

        (await provider.CancelAsync(new CourierCancelRequest(
            "k", "SHP-1", awb, NoCredentials))).Outcome
            .Should().NotBe(CourierOutcome.Succeeded);
    }

    [Fact]
    public async Task Its_own_airway_bill_is_accepted_however_it_is_cased()
    {
        var booking = await Provider().BookAsync(Request());

        var result = await Provider().TrackAsync(new CourierTrackingRequest(
            booking.AwbNumber!.ToLowerInvariant(), null, NoCredentials));

        result.Succeeded().Should().BeTrue();
    }

    // ── The label ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_label_is_a_real_pdf_somebody_can_open()
    {
        // The label flow ends with a human opening it. A placeholder that will not open makes
        // that path impossible to demonstrate.
        var booking = await Provider().BookAsync(Request());

        var result = await Provider().GetLabelAsync(booking.AwbNumber!, NoCredentials);

        result.Succeeded().Should().BeTrue();
        result.Label!.ContentType.Should().Be("application/pdf");
        System.Text.Encoding.ASCII.GetString(result.Label.Content, 0, 5).Should().Be("%PDF-");
        result.Label.Content.Length.Should().BeGreaterThan(1000);
        result.Label.FileName.Should().Be($"{booking.AwbNumber}.pdf");
    }

    [Fact]
    public async Task The_label_is_the_same_every_time_it_is_fetched()
    {
        // Re-fetching a label must not look like issuing a new one.
        var booking = await Provider().BookAsync(Request());

        var first  = await Provider().GetLabelAsync(booking.AwbNumber!, NoCredentials);
        var second = await Provider().GetLabelAsync(booking.AwbNumber!, NoCredentials);

        first.Label!.FileName.Should().Be(second.Label!.FileName);
        first.Label.Content.Length.Should().Be(second.Label.Content.Length);
    }

    // ── It never pretends to be real ──────────────────────────────────────────

    [Fact]
    public void The_display_name_says_it_ships_nothing()
    {
        // The one genuinely dangerous failure here is somebody shipping a real parcel against a
        // carrier that was quietly a simulation.
        Provider().DisplayName.Should().ContainEquivalentOf("nothing is actually shipped");
    }

    [Fact]
    public async Task The_booking_and_its_tracking_url_both_admit_to_being_simulated()
    {
        var result = await Provider().BookAsync(Request());

        result.Message.Should().ContainEquivalentOf("simulated");
        result.RawResponse.Should().Contain("\"simulated\":true");
        // .invalid is reserved by RFC 2606 and can never resolve, so nobody reaches a real site.
        result.TrackingUrl.Should().Contain(".invalid/");
    }

    [Fact]
    public async Task Every_tracking_event_uses_a_milestone_this_system_knows()
    {
        // The simulator is the reference other adapters get compared against, so it has to be
        // exemplary rather than merely passing.
        var known = LogisticsCode.Codes<TrackingMilestone>().ToList();
        var provider = Provider();

        foreach (var code in SimulatorScenarios.ServiceCodes.Where(c => c is not ("SIM-REFUSE" or "SIM-FAIL")))
        {
            var booking = await provider.BookAsync(Request(serviceCode: code));
            var track = await provider.TrackAsync(
                new CourierTrackingRequest(booking.AwbNumber!, null, NoCredentials));

            foreach (var evt in track.Events)
                known.Should().Contain(evt.Milestone, $"{code} emitted '{evt.Milestone}'");
        }
    }

    [Fact]
    public async Task Tracking_events_are_recent_rather_than_dated_from_an_epoch()
    {
        // A parcel booked a moment ago reading as having travelled last year makes every screen
        // over it look broken.
        var booking = await Provider().BookAsync(Request());

        var result = await Provider().TrackAsync(
            new CourierTrackingRequest(booking.AwbNumber!, null, NoCredentials));

        result.Events[^1].OccurredAt.Should().BeBefore(DateTime.UtcNow);
        result.Events[^1].OccurredAt.Should().BeAfter(DateTime.UtcNow.AddDays(-3));
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public void It_registers_under_its_own_key()
    {
        var registry = new CourierProviderRegistry([new SimulatorCourierProvider()]);

        registry.Require(SimulatorCourierProvider.ProviderKey)
                .Should().BeOfType<SimulatorCourierProvider>();
    }
}

internal static class CourierResultExtensions
{
    internal static bool Succeeded(this CourierTrackingResult result) =>
        result.Outcome == CourierOutcome.Succeeded;

    internal static bool Succeeded(this CourierLabelResult result) =>
        result.Outcome == CourierOutcome.Succeeded;
}
