using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Couriers.Simulator;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

// T-34 — carrier accounts, and what each may be asked for.
public class CarrierAccountTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db,
        CarrierAccountRepository Accounts,
        CarrierAccountResolver Resolver,
        CourierProviderRegistry Registry);

    private static Harness NewHarness(params ICourierProvider[] providers)
    {
        var (db, _, _) = LogisticsTestDb.New();

        var registry = new CourierProviderRegistry(
            providers.Length > 0
                ? providers
                : [new ManualCourierProvider(), new SimulatorCourierProvider()]);

        return new Harness(db, new CarrierAccountRepository(db, registry),
                           NewResolver(db, registry), registry);
    }

    // T-35 gave the resolver a vault. No credentials are set in these tests, so it resolves empty.
    private static CarrierAccountResolver NewResolver(LogisticsDbContext db, CourierProviderRegistry registry) =>
        new(db, registry, new CarrierCredentialVault(db, TestEncryption.New()));

    /// <summary>A carrier row. Null integration mode is what every pre-Phase-2 carrier looks like.</summary>
    private static async Task<Guid> NewCarrier(
        Harness h, string name = "Simcourier", string? mode = "API", string? providerKey = "SIMULATOR")
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = name, Code = $"C{Guid.NewGuid():N}"[..6],
            IntegrationMode = mode, ProviderKey = providerKey, IsActive = true
        };

        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return carrier.UUID;
    }

    private static CreateCarrierAccountRequest NewAccount(
        Guid carrierUuid, string name = "Domestic", bool isDefault = false) =>
        new() { CarrierUuid = carrierUuid, AccountName = name, IsDefault = isDefault };

    // ── Creating ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_first_account_is_the_default_whether_or_not_anybody_said_so()
    {
        // An only account that is not the default is a carrier nothing can book on.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Accounts.CreateAsync(NewAccount(carrier, isDefault: false), User);

        (await h.Accounts.GetByUuidAsync(uuid))!.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Making_a_new_account_the_default_clears_the_old_one()
    {
        // Two defaults means the account a booking goes out on depends on row order.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var first  = await h.Accounts.CreateAsync(NewAccount(carrier, "Domestic"), User);
        var second = await h.Accounts.CreateAsync(NewAccount(carrier, "International", isDefault: true), User);

        (await h.Accounts.GetByUuidAsync(first))!.IsDefault.Should().BeFalse();
        (await h.Accounts.GetByUuidAsync(second))!.IsDefault.Should().BeTrue();

        (await h.Db.CarrierAccounts.CountAsync(a => a.IsDefault)).Should().Be(1);
    }

    [Fact]
    public async Task Two_accounts_of_one_carrier_cannot_share_a_name()
    {
        // How somebody picks the wrong one from a dropdown and books on the wrong contract.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Accounts.CreateAsync(NewAccount(carrier, "Domestic"), User);

        var act = async () => await h.Accounts.CreateAsync(NewAccount(carrier, "  Domestic  "), User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already has an account called*");
    }

    [Fact]
    public async Task Different_carriers_may_use_the_same_account_name()
    {
        var h = NewHarness();
        var one = await NewCarrier(h, "Carrier One");
        var two = await NewCarrier(h, "Carrier Two");

        await h.Accounts.CreateAsync(NewAccount(one, "Domestic"), User);

        var act = async () => await h.Accounts.CreateAsync(NewAccount(two, "Domestic"), User);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_account_needs_a_name_and_an_existing_carrier()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var noName = async () => await h.Accounts.CreateAsync(NewAccount(carrier, "  "), User);
        await noName.Should().ThrowAsync<BadRequestException>();

        var noCarrier = async () => await h.Accounts.CreateAsync(NewAccount(Guid.NewGuid()), User);
        await noCarrier.Should().ThrowAsync<NotFoundException>();
    }

    // ── Capability narrowing ──────────────────────────────────────────────────

    [Fact]
    public async Task An_account_can_switch_a_capability_off()
    {
        // No cash-on-delivery contract with this carrier, even though the adapter can do it.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Accounts.CreateAsync(
            new CreateCarrierAccountRequest
            {
                CarrierUuid = carrier, AccountName = "Domestic", CodEnabled = false
            }, User);

        var cod = (await h.Accounts.GetByUuidAsync(uuid))!.Capabilities.Single(c => c.Name == "COD");

        cod.SupportedByProvider.Should().BeTrue();
        cod.EnabledOnAccount.Should().BeFalse();
        cod.Effective.Should().BeFalse();
    }

    [Fact]
    public async Task An_account_can_never_switch_on_what_the_adapter_cannot_do()
    {
        // The invariant. Configuration promising a capability no code implements would surface as
        // a carrier refusal nobody can explain.
        var h = NewHarness(new ManualCourierProvider());
        var carrier = await NewCarrier(h, mode: "MANUAL", providerKey: null);

        var uuid = await h.Accounts.CreateAsync(
            new CreateCarrierAccountRequest
            {
                CarrierUuid = carrier, AccountName = "Phone bookings",
                LabelsEnabled = true, TrackingEnabled = true, CancellationEnabled = true
            }, User);

        var capabilities = (await h.Accounts.GetByUuidAsync(uuid))!.Capabilities;

        foreach (var name in new[] { "LABELS", "TRACKING", "CANCELLATION" })
        {
            var capability = capabilities.Single(c => c.Name == name);
            capability.SupportedByProvider.Should().BeFalse($"the manual adapter cannot do {name}");
            capability.EnabledOnAccount.Should().BeTrue("somebody asked for it");
            capability.Effective.Should().BeFalse("but asking does not make it possible");
        }
    }

    [Fact]
    public void Narrowing_leaves_the_non_negotiable_capabilities_alone()
    {
        // Booking is what an adapter is for; multi-piece and idempotency are statements about how
        // the carrier behaves, and configuration cannot change how a carrier behaves.
        var declared = new CourierCapabilities(
            SupportsBooking: true, SupportsMultiPiece: true, HonoursIdempotencyKey: true,
            SupportsCod: true);

        var account = new CarrierAccount { CodEnabled = false };

        var effective = CarrierCapabilityResolver.Resolve(declared, account);

        effective.SupportsBooking.Should().BeTrue();
        effective.SupportsMultiPiece.Should().BeTrue();
        effective.HonoursIdempotencyKey.Should().BeTrue();
        effective.SupportsCod.Should().BeFalse();
    }

    [Fact]
    public void No_account_means_the_adapter_speaks_for_itself()
    {
        var declared = new CourierCapabilities(SupportsCod: true, SupportsLabels: true);

        CarrierCapabilityResolver.Resolve(declared, null).Should().Be(declared);
    }

    [Fact]
    public async Task An_override_can_be_cleared_back_to_whatever_the_adapter_says()
    {
        // A null on a patch means "leave alone", so there has to be another way to stop overriding.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Accounts.CreateAsync(
            new CreateCarrierAccountRequest
            {
                CarrierUuid = carrier, AccountName = "Domestic", CodEnabled = false
            }, User);

        await h.Accounts.PatchAsync(uuid, new PatchCarrierAccountRequest
        {
            ClearOverrides = ["cod"]
        }, User);

        var cod = (await h.Accounts.GetByUuidAsync(uuid))!.Capabilities.Single(c => c.Name == "COD");

        cod.EnabledOnAccount.Should().BeNull();
        cod.Effective.Should().BeTrue("the adapter supports it and nothing overrides it now");
    }

    [Fact]
    public async Task Clearing_something_that_is_not_a_capability_is_refused()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var uuid = await h.Accounts.CreateAsync(NewAccount(carrier), User);

        var act = async () => await h.Accounts.PatchAsync(
            uuid, new PatchCarrierAccountRequest { ClearOverrides = ["FLYING"] }, User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*FLYING*")
            .WithMessage("*PICKUP_BOOKING*");
    }

    // ── Resolving which account a booking goes out on ─────────────────────────

    [Fact]
    public async Task A_booking_goes_out_on_the_default_account()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Accounts.CreateAsync(NewAccount(carrier, "Domestic"), User);
        await h.Accounts.CreateAsync(NewAccount(carrier, "International", isDefault: true), User);

        var resolved = await h.Resolver.ResolveAsync(carrier);

        resolved.AccountName.Should().Be("International");
        resolved.Provider.Key.Should().Be("SIMULATOR");
    }

    [Fact]
    public async Task A_named_account_overrides_the_default()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var domestic = await h.Accounts.CreateAsync(NewAccount(carrier, "Domestic"), User);
        await h.Accounts.CreateAsync(NewAccount(carrier, "International", isDefault: true), User);

        (await h.Resolver.ResolveAsync(carrier, domestic)).AccountName.Should().Be("Domestic");
    }

    [Fact]
    public async Task An_account_belonging_to_another_carrier_is_refused()
    {
        // It would put the consignment on somebody else's contract.
        var h = NewHarness();
        var mine   = await NewCarrier(h, "Mine");
        var theirs = await NewCarrier(h, "Theirs");

        var theirAccount = await h.Accounts.CreateAsync(NewAccount(theirs), User);

        var act = async () => await h.Resolver.ResolveAsync(mine, theirAccount);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*different carrier*");
    }

    [Fact]
    public async Task An_inactive_account_cannot_be_booked_on()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Accounts.CreateAsync(NewAccount(carrier, "Primary", isDefault: true), User);
        var spare = await h.Accounts.CreateAsync(NewAccount(carrier, "Spare"), User);

        await h.Accounts.PatchAsync(spare, new PatchCarrierAccountRequest { IsActive = false }, User);

        var act = async () => await h.Resolver.ResolveAsync(carrier, spare);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*inactive*");
    }

    [Fact]
    public async Task Several_active_accounts_and_no_default_is_a_refusal_not_a_guess()
    {
        // Picking one silently means the contract a parcel ships under depends on row order.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Accounts.CreateAsync(NewAccount(carrier, "Domestic"), User);
        await h.Accounts.CreateAsync(NewAccount(carrier, "International"), User);

        // Force the state the API will not let you create.
        foreach (var account in await h.Db.CarrierAccounts.ToListAsync()) account.IsDefault = false;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var act = async () => await h.Resolver.ResolveAsync(carrier);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*none is marked default*");
    }

    [Fact]
    public async Task A_single_account_resolves_even_if_nothing_marked_it_default()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Accounts.CreateAsync(NewAccount(carrier, "Only"), User);

        foreach (var account in await h.Db.CarrierAccounts.ToListAsync()) account.IsDefault = false;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        (await h.Resolver.ResolveAsync(carrier)).AccountName.Should().Be("Only");
    }

    // ── Which adapter carries a carrier ───────────────────────────────────────

    [Fact]
    public async Task A_manual_carrier_goes_to_the_manual_adapter_and_needs_no_account()
    {
        // There is no carrier system to authenticate against, so an unconfigured manual carrier
        // still books.
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: "MANUAL", providerKey: null);

        var resolved = await h.Resolver.ResolveAsync(carrier);

        resolved.Provider.Key.Should().Be("MANUAL");
        resolved.AccountUuid.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task A_carrier_with_no_integration_mode_is_treated_as_manual()
    {
        // Every carrier that predates the column looks like this.
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: null, providerKey: null);

        (await h.Resolver.ResolveAsync(carrier)).Provider.Key.Should().Be("MANUAL");
    }

    [Fact]
    public async Task Integration_mode_beats_a_stale_provider_key()
    {
        // The mode is the operator's statement of intent. Honouring a leftover provider key over
        // it would send a real booking to an API they have decided not to use.
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: "MANUAL", providerKey: "SIMULATOR");

        (await h.Resolver.ResolveAsync(carrier)).Provider.Key.Should().Be("MANUAL");
    }

    [Fact]
    public async Task An_api_carrier_with_no_account_cannot_be_booked_on()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Resolver.ResolveAsync(carrier);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*no active account*");
    }

    [Fact]
    public async Task An_api_carrier_naming_no_provider_says_so()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: "API", providerKey: null);

        var act = async () => await h.Resolver.ResolveAsync(carrier);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*names no courier provider*");
    }

    [Fact]
    public async Task An_unrecognised_integration_mode_is_refused_rather_than_assumed()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: "CARRIER_PIGEON", providerKey: null);

        var act = async () => await h.Resolver.ResolveAsync(carrier);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*unrecognised integration mode*");
    }

    // ── Reading configuration that is broken ──────────────────────────────────

    [Fact]
    public async Task An_account_whose_adapter_is_missing_still_reads_with_a_warning()
    {
        // This is exactly the configuration somebody opens the screen to fix. Failing the read
        // would leave them unable to see what is wrong.
        var h = NewHarness(new ManualCourierProvider());
        var carrier = await NewCarrier(h, mode: "API", providerKey: "DHL");

        var uuid = await h.Accounts.CreateAsync(NewAccount(carrier), User);

        var model = await h.Accounts.GetByUuidAsync(uuid);

        model!.ProviderKey.Should().BeNull();
        model.ProviderWarning.Should().Contain("DHL");
        model.Capabilities.Should().OnlyContain(c => !c.Effective);
    }

    // ── Deactivating and removing ─────────────────────────────────────────────

    [Fact]
    public async Task The_default_account_cannot_be_deactivated_or_removed_while_others_exist()
    {
        // Bookings would have nowhere to go.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var primary = await h.Accounts.CreateAsync(NewAccount(carrier, "Primary"), User);
        await h.Accounts.CreateAsync(NewAccount(carrier, "Spare"), User);

        var deactivate = async () => await h.Accounts.PatchAsync(
            primary, new PatchCarrierAccountRequest { IsActive = false }, User);
        (await deactivate.Should().ThrowAsync<ConflictException>()).WithMessage("*default account*");

        var remove = async () => await h.Accounts.DeleteAsync(primary, User);
        (await remove.Should().ThrowAsync<ConflictException>()).WithMessage("*default account*");
    }

    [Fact]
    public async Task The_last_account_can_be_removed()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var only = await h.Accounts.CreateAsync(NewAccount(carrier), User);

        (await h.Accounts.DeleteAsync(only, User)).Should().BeTrue();
        (await h.Accounts.GetForCarrierAsync(carrier)).Should().BeEmpty();
    }

    [Fact]
    public async Task Unsetting_a_default_directly_is_refused_and_says_what_to_do_instead()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);
        var only = await h.Accounts.CreateAsync(NewAccount(carrier), User);

        var act = async () => await h.Accounts.PatchAsync(
            only, new PatchCarrierAccountRequest { IsDefault = false }, User);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*Make another account the default instead*");
    }

    [Fact]
    public async Task An_unknown_account_is_reported_as_not_found()
    {
        var h = NewHarness();

        (await h.Accounts.GetByUuidAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Accounts.PatchAsync(Guid.NewGuid(), new PatchCarrierAccountRequest(), User))
            .Should().BeFalse();
        (await h.Accounts.DeleteAsync(Guid.NewGuid(), User)).Should().BeFalse();
        (await h.Accounts.GetForCarrierAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Accounts_list_with_the_default_first()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Accounts.CreateAsync(NewAccount(carrier, "Alpha"), User);
        await h.Accounts.CreateAsync(NewAccount(carrier, "Zulu", isDefault: true), User);

        var accounts = await h.Accounts.GetForCarrierAsync(carrier);

        accounts.Select(a => a.AccountName).Should().Equal(["Zulu", "Alpha"]);
    }

    [Fact]
    public async Task A_sandbox_account_says_it_is_one()
    {
        // A sandbox account in production is a legitimate setup while an integration is being
        // proven, so the screens have to be able to say so loudly.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var uuid = await h.Accounts.CreateAsync(new CreateCarrierAccountRequest
        {
            CarrierUuid = carrier, AccountName = "Sandbox", IsSandbox = true,
            AccountNumber = "TEST-001", DefaultServiceCode = "SIM-DELIVERED"
        }, User);

        var model = await h.Accounts.GetByUuidAsync(uuid);

        model!.IsSandbox.Should().BeTrue();
        model.AccountNumber.Should().Be("TEST-001");
        model.DefaultServiceCode.Should().Be("SIM-DELIVERED");

        (await h.Resolver.ResolveAsync(carrier)).IsSandbox.Should().BeTrue();
    }

    [Fact]
    public async Task Another_organizations_account_does_not_exist_here()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        var registry = new CourierProviderRegistry(
            [new ManualCourierProvider(), new SimulatorCourierProvider()]);

        var h = new Harness(db, new CarrierAccountRepository(db, registry),
                            NewResolver(db, registry), registry);

        var carrier = await NewCarrier(h);
        var uuid = await h.Accounts.CreateAsync(NewAccount(carrier), User);

        var otherDb = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        var other = new CarrierAccountRepository(otherDb, registry);

        (await other.GetByUuidAsync(uuid)).Should().BeNull();
        (await other.GetForCarrierAsync(carrier)).Should().BeEmpty();
    }
}
