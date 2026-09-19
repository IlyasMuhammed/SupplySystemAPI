using FluentAssertions;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Manual;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

/// <summary>
/// The contract suite, against the carrier that has no API.
/// <para>
/// It is here to prove the manual path is a first-class adapter and not a special case: the same
/// suite that holds the simulator to account holds this to account too. The only override needed
/// is supplying the airway bill a human would have keyed in — which is exactly the difference
/// between the two, and nothing else.
/// </para>
/// </summary>
public class ManualCourierProviderContractTests : CourierProviderContractTests
{
    protected override ICourierProvider CreateProvider() => new ManualCourierProvider();

    protected override CourierBookingRequest BookableRequest(string? idempotencyKey = null)
    {
        var request = base.BookableRequest(idempotencyKey);

        // Derived from the key so that "different keys are different bookings" means something
        // here: a manual adapter echoes the number it was given, and the guard that test really
        // provides is that it is not returning a constant.
        return request with { SuppliedAwbNumber = $"AWB-{request.IdempotencyKey[..8].ToUpperInvariant()}" };
    }

    protected override CourierBookingRequest? RefusableRequest() =>
        // The one thing a manual booking cannot do without.
        BookableRequest() with { SuppliedAwbNumber = null };
}

// T-33 — what is particular to the manual adapter.
public class ManualCourierProviderTests
{
    private static ManualCourierProvider Provider() => new();

    private static IReadOnlyDictionary<string, string> NoCredentials =>
        new Dictionary<string, string>();

    private static CourierBookingRequest Request(
        string? suppliedAwb = "AWB-123456",
        IReadOnlyList<CourierPackage>? packages = null) =>
        new(
            IdempotencyKey:    Guid.NewGuid().ToString("N"),
            ConsignmentNumber: "SHP-2026-00001",
            ServiceCode:       null,
            ShipFrom:          new CourierAddress("Warehouse", null, null, "12 Dock Road", null,
                                                  "Karachi", "Sindh", "74000", "PK"),
            ShipTo:            new CourierAddress("Acme Ltd", null, null, "4 Industrial Estate", null,
                                                  "Lahore", "Punjab", "54000", "PK"),
            Packages:          packages ?? [new CourierPackage("HU-1", "BOX", 40m, 30m, 20m, 12.5m, 1500m)],
            FreightTerms:      "PREPAID",
            CodAmount:         null,
            CodCurrency:       null,
            PickupWindowStart: null,
            PickupWindowEnd:   null,
            ReferenceNumbers:  ["DLV-2026-00001"],
            Credentials:       NoCredentials,
            SuppliedAwbNumber: suppliedAwb);

    // ── Booking ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task It_records_the_number_a_person_obtained()
    {
        var result = await Provider().BookAsync(Request("AWB-998877"));

        result.Outcome.Should().Be(CourierOutcome.Succeeded);
        result.AwbNumber.Should().Be("AWB-998877");
        result.ProviderKey.Should().Be("MANUAL");
        result.Message.Should().Contain("Nothing was sent to the carrier");
    }

    [Fact]
    public async Task The_number_is_trimmed_before_it_is_recorded()
    {
        // It was typed by a human, from a paper note or a phone call.
        var result = await Provider().BookAsync(Request("  AWB-998877  "));

        result.AwbNumber.Should().Be("AWB-998877");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Without_a_number_there_is_nothing_to_record(string? awb)
    {
        // T-21 already refused this; the rule survives the move behind the contract. A consignment
        // BOOKED with no airway bill has nothing to track and nothing to prove it.
        var result = await Provider().BookAsync(Request(awb));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.AwbNumber.Should().BeNull();
        result.CarrierErrorCode.Should().Be("AWB_REQUIRED");
        result.Message.Should().Contain("airway bill");
    }

    [Fact]
    public async Task A_consignment_with_no_packages_is_refused()
    {
        var result = await Provider().BookAsync(Request(packages: []));

        result.Outcome.Should().Be(CourierOutcome.Refused);
        result.CarrierErrorCode.Should().Be("NO_PACKAGES");
    }

    [Fact]
    public async Task It_invents_no_tracking_url()
    {
        // The carrier row's TrackingUrlTemplate builds that. An adapter guessing at a carrier's
        // URL shape would be inventing one, and it would be wrong for every carrier but one.
        var result = await Provider().BookAsync(Request());

        result.TrackingUrl.Should().BeNull();
    }

    [Fact]
    public async Task Recording_the_same_number_twice_records_the_same_number()
    {
        // It echoes its input, so it cannot tell a resubmission from a second consignment that
        // genuinely shares a number. That is why it declares no idempotency guarantee and why
        // T-36's ledger is what actually protects this path.
        var provider = Provider();

        var first  = await provider.BookAsync(Request("AWB-SAME"));
        var second = await provider.BookAsync(Request("AWB-SAME"));

        first.AwbNumber.Should().Be(second.AwbNumber);
        provider.Capabilities.HonoursIdempotencyKey.Should().BeFalse(
            "nothing here deduplicates — the ledger has to");
    }

    // ── What it honestly cannot do ────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_says_a_human_has_to_do_it_rather_than_quietly_succeeding()
    {
        // A caller told the cancellation succeeded would believe a real parcel had been stopped,
        // when nobody has told the carrier anything.
        var result = await Provider().CancelAsync(
            new CourierCancelRequest("k", "SHP-1", "AWB-123456", NoCredentials));

        result.Outcome.Should().Be(CourierOutcome.Unsupported);
        result.Message.Should().Contain("contact the carrier");
    }

    [Fact]
    public async Task There_is_no_label_to_fetch()
    {
        var result = await Provider().GetLabelAsync("AWB-123456", NoCredentials);

        result.Outcome.Should().Be(CourierOutcome.Unsupported);
        result.Label.Should().BeNull();
        result.Message.Should().Contain("issues its own labels");
    }

    [Fact]
    public async Task There_is_no_tracking_feed_to_poll()
    {
        // Claiming tracking would make T-40's poll ask this adapter questions it cannot answer,
        // every few minutes, forever.
        var result = await Provider().TrackAsync(
            new CourierTrackingRequest("AWB-123456", null, NoCredentials));

        result.Outcome.Should().Be(CourierOutcome.Unsupported);
        result.Events.Should().BeEmpty();
    }

    [Fact]
    public void Its_capabilities_say_only_what_it_can_do()
    {
        var capabilities = Provider().Capabilities;

        capabilities.SupportsBooking.Should().BeTrue();
        capabilities.SupportsCancellation.Should().BeFalse();
        capabilities.SupportsLabels.Should().BeFalse();
        capabilities.SupportsTracking.Should().BeFalse();

        // Cash on delivery is a commercial term between shipper and carrier, not an API feature.
        // It works perfectly well with a carrier booked by telephone.
        capabilities.SupportsCod.Should().BeTrue();
    }

    // ── Registration ──────────────────────────────────────────────────────────

    [Fact]
    public void It_registers_under_its_own_key_alongside_the_simulator()
    {
        var registry = new CourierProviderRegistry(
        [
            new ManualCourierProvider(),
            new SMS.Modules.Logistics.Couriers.Simulator.SimulatorCourierProvider()
        ]);

        registry.Require("MANUAL").Should().BeOfType<ManualCourierProvider>();
        registry.All.Select(p => p.Key).Should().Contain(["MANUAL", "SIMULATOR"]);
    }

    [Fact]
    public void Its_display_name_says_where_the_number_comes_from()
    {
        Provider().DisplayName.Should().ContainEquivalentOf("keyed in by hand");
    }
}
