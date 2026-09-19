using FluentAssertions;
using SMS.Modules.Logistics.Couriers;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

// T-31 — resolving a carrier's ProviderKey to the adapter that implements it.
public class CourierProviderRegistryTests
{
    /// <summary>A provider that exists only to occupy a key.</summary>
    private sealed class NamedProvider(string key) : ICourierProvider
    {
        public string Key => key;
        public string DisplayName => $"{key} carrier";
        public CourierCapabilities Capabilities { get; } = new();

        public Task<CourierRateResult> RateAsync(CourierRateRequest r, CancellationToken ct = default) =>
            Task.FromResult(new CourierRateResult(CourierOutcome.Unsupported, Key, []));
        public Task<CourierBookingResult> BookAsync(CourierBookingRequest r, CancellationToken ct = default) =>
            Task.FromResult(new CourierBookingResult(CourierOutcome.Unsupported, Key));
        public Task<CourierCancelResult> CancelAsync(CourierCancelRequest r, CancellationToken ct = default) =>
            Task.FromResult(new CourierCancelResult(CourierOutcome.Unsupported, Key));
        public Task<CourierLabelResult> GetLabelAsync(string a, IReadOnlyDictionary<string, string> c, CancellationToken ct = default) =>
            Task.FromResult(new CourierLabelResult(CourierOutcome.Unsupported, Key));
        public Task<CourierTrackingResult> TrackAsync(CourierTrackingRequest r, CancellationToken ct = default) =>
            Task.FromResult(new CourierTrackingResult(CourierOutcome.Unsupported, Key, []));
    }

    private static CourierProviderRegistry Registry(params string[] keys) =>
        new(keys.Select(k => new NamedProvider(k)));

    // ── Resolving ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_registered_key_resolves_to_its_provider()
    {
        var registry = Registry("STUB", "DHL");

        registry.Find("DHL")!.Key.Should().Be("DHL");
        registry.Require("STUB").Key.Should().Be("STUB");
    }

    [Theory]
    [InlineData("dhl")]
    [InlineData("DhL")]
    [InlineData("  DHL  ")]
    public void A_key_resolves_however_it_was_typed_into_the_carrier_row(string typed)
    {
        // Provider keys are configuration somebody enters by hand. Matching exactly would make
        // "dhl" a silently unbookable carrier.
        Registry("DHL").Find(typed)!.Key.Should().Be("DHL");
    }

    [Fact]
    public void An_unregistered_key_is_simply_absent_when_asked_softly()
    {
        var registry = Registry("DHL");

        registry.Find("FEDEX").Should().BeNull();
        registry.Find(null).Should().BeNull();
        registry.Find("   ").Should().BeNull();
    }

    [Fact]
    public void An_unregistered_key_explains_itself_and_lists_what_is_available()
    {
        // The realistic failure: a carrier row configured for an adapter nobody deployed. "Not
        // found" alone leaves an administrator guessing at spelling.
        var registry = Registry("DHL", "STUB");

        var act = () => registry.Require("FEDEX");

        act.Should().Throw<ConflictException>()
           .WithMessage("*FEDEX*")
           .WithMessage("*DHL*")
           .WithMessage("*STUB*");
    }

    [Fact]
    public void A_carrier_with_no_provider_is_told_to_use_the_manual_path()
    {
        // A null ProviderKey means MANUAL, which is the default for every carrier that predates
        // the column. The message has to point somewhere useful rather than just refusing.
        var act = () => Registry("DHL").Require(null);

        act.Should().Throw<ConflictException>()
           .WithMessage("*no courier provider configured*")
           .WithMessage("*manual*");
    }

    [Fact]
    public void An_empty_registry_says_so_rather_than_naming_nothing()
    {
        var act = () => Registry().Require("DHL");

        act.Should().Throw<ConflictException>().WithMessage("*none*");
    }

    // ── Building it ───────────────────────────────────────────────────────────

    [Fact]
    public void Two_providers_claiming_one_key_is_caught_at_startup()
    {
        // Case-insensitive resolution means DHL and dhl are the same key. Left alone, which one
        // won would depend on assembly load order — a bug that reproduces on one machine in three.
        var act = () => Registry("DHL", "dhl");

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Two courier providers claim the key*");
    }

    [Fact]
    public void A_provider_with_no_key_is_caught_at_startup()
    {
        // It could never be reached, so shipping it is worse than failing to start.
        var act = () => Registry("   ");

        act.Should().Throw<InvalidOperationException>().WithMessage("*no provider key*");
    }

    [Fact]
    public void Every_provider_is_listed_in_a_stable_order()
    {
        // An admin screen offers these. Order that varies by DI registration makes the list jump
        // around between deployments for no reason.
        var registry = Registry("STUB", "aramex", "DHL");

        registry.All.Select(p => p.Key).Should().Equal(["aramex", "DHL", "STUB"]);
    }

    [Fact]
    public void An_empty_registry_is_usable_rather_than_broken()
    {
        var registry = Registry();

        registry.All.Should().BeEmpty();
        registry.Find("ANY").Should().BeNull();
    }

    // ── The stub honours what it claims ───────────────────────────────────────

    [Fact]
    public void The_stub_provider_registers_under_its_own_key()
    {
        var registry = new CourierProviderRegistry([new StubCourierProvider()]);

        registry.Require(StubCourierProvider.ProviderKey)
                .Should().BeOfType<StubCourierProvider>();
    }
}
