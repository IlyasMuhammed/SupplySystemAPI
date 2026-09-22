using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Logistics.Controllers;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Dhl;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

// Until this existed nothing could write Carrier.IntegrationMode or ProviderKey, so pointing a carrier at
// an adapter — the simulator, and now DHL — meant a hand-written SQL update against the shared database.
public class CarrierIntegrationTests
{
    private const int User = 42;

    private sealed record Harness(
        LogisticsDbContext Db, CarrierIntegrationService Service, StaticTenantContext Tenant, string DbName);

    private static Harness NewHarness()
    {
        var (db, tenant, dbName) = LogisticsTestDb.New();

        var registry = new CourierProviderRegistry(
        [
            new ManualCourierProvider(),
            new DhlExpressCourierProvider(new Dhl.FakeDhlServer(), NullLogger<DhlExpressCourierProvider>.Instance),
            new ScriptedCourierProvider("SCRIPTED")
        ]);

        return new Harness(db, new CarrierIntegrationService(db, registry), tenant, dbName);
    }

    private static async Task<Guid> NewCarrier(
        Harness h, string? mode = null, string? key = null, string name = "DHL")
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = name, Code = $"C{Guid.NewGuid():N}"[..6],
            IntegrationMode = mode, ProviderKey = key, IsActive = true
        };

        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return carrier.UUID;
    }

    private static async Task AddConsignment(Harness h, Guid carrierUuid, string status)
    {
        var carrier = await h.Db.Carriers.AsNoTracking().SingleAsync(c => c.UUID == carrierUuid);

        h.Db.Consignments.Add(new Consignment
        {
            UUID = Guid.NewGuid(), ConsignmentNumber = $"SHP-2026-{Random.Shared.Next(1, 99_999):D5}",
            CarrierId = carrier.Id, Status = status, CreatedBy = User, CreatedDate = DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static SetCarrierIntegrationRequest Api(string key) => new() { IntegrationMode = "API", ProviderKey = key };

    private static Task<Carrier> Reload(Harness h, Guid uuid) =>
        h.Db.Carriers.AsNoTracking().SingleAsync(c => c.UUID == uuid);

    // ── What can be chosen ────────────────────────────────────────────────────

    [Fact]
    public void The_adapters_on_offer_leave_out_manual_which_is_a_mode_and_not_a_choice()
    {
        var providers = NewHarness().Service.GetProviders();

        providers.Select(p => p.Key).Should().BeEquivalentTo("DHL_EXPRESS", "SCRIPTED");
    }

    [Fact]
    public void An_adapter_says_what_it_can_do_and_which_credentials_to_enter()
    {
        var dhl = NewHarness().Service.GetProviders().Single(p => p.Key == "DHL_EXPRESS");

        dhl.DisplayName.Should().Contain("DHL");
        dhl.SupportsBooking.Should().BeTrue();
        dhl.SupportsRating.Should().BeTrue();
        dhl.SupportsTracking.Should().BeTrue();
        dhl.SupportsCancellation.Should().BeFalse();
        dhl.SupportsCod.Should().BeFalse();

        dhl.Credentials.Where(c => c.Required).Select(c => c.Key)
           .Should().BeEquivalentTo("ApiKey", "ApiSecret", "AccountNumber");
        dhl.Credentials.Single(c => c.Key == "ApiSecret").IsSecret.Should().BeTrue();
    }

    [Fact]
    public void An_adapter_that_needs_nothing_declares_no_credentials()
    {
        NewHarness().Service.GetProviders().Single(p => p.Key == "SCRIPTED").Credentials.Should().BeEmpty();
    }

    // ── Setting it ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_manual_carrier_can_be_pointed_at_dhl()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: "MANUAL", key: "MANUAL");

        var result = await h.Service.SetAsync(carrier, Api("DHL_EXPRESS"), User);

        result!.IntegrationMode.Should().Be("API");
        result.ProviderKey.Should().Be("DHL_EXPRESS");
        result.ProviderDisplayName.Should().Contain("DHL");
        result.Warning.Should().BeNull();

        var stored = await Reload(h, carrier);
        stored.IntegrationMode.Should().Be("API");
        stored.ProviderKey.Should().Be("DHL_EXPRESS");
        stored.ModifiedBy.Should().Be(User);
        stored.ModifiedDate.Should().NotBeNull();
    }

    [Theory]
    [InlineData("dhl_express")]
    [InlineData("  Dhl_Express ")]
    public async Task The_key_is_stored_in_the_registrys_own_spelling(string typed)
    {
        // A row holding "dhl_express" beside one holding "DHL_EXPRESS" is two spellings of one adapter.
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        await h.Service.SetAsync(carrier, Api(typed), User);

        (await Reload(h, carrier)).ProviderKey.Should().Be("DHL_EXPRESS");
    }

    [Fact]
    public async Task Going_back_to_manual_sets_the_manual_key_every_existing_carrier_uses()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: "API", key: "DHL_EXPRESS");

        await h.Service.SetAsync(carrier, new SetCarrierIntegrationRequest { IntegrationMode = "manual", ProviderKey = "IGNORED" }, User);

        var stored = await Reload(h, carrier);
        stored.IntegrationMode.Should().Be("MANUAL");
        stored.ProviderKey.Should().Be("MANUAL", "a provider key sent with MANUAL is ignored, not stored");
    }

    [Fact]
    public async Task Saying_what_is_already_true_changes_nothing()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: "API", key: "DHL_EXPRESS");

        await h.Service.SetAsync(carrier, Api("DHL_EXPRESS"), User);

        (await Reload(h, carrier)).ModifiedBy.Should().BeNull("nothing was modified, so nobody modified it");
    }

    [Fact]
    public async Task A_carrier_with_no_mode_at_all_is_manual_and_can_be_moved_from_there()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: null, key: null);

        (await h.Service.GetAsync(carrier))!.IntegrationMode.Should().Be("MANUAL");

        await h.Service.SetAsync(carrier, Api("SCRIPTED"), User);

        (await Reload(h, carrier)).ProviderKey.Should().Be("SCRIPTED");
    }

    // ── What is refused ───────────────────────────────────────────────────────

    [Fact]
    public async Task An_adapter_nothing_registers_is_refused_and_the_ones_that_exist_are_listed()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Service.SetAsync(carrier, Api("FEDEX"), User);

        (await act.Should().ThrowAsync<BadRequestException>())
            .WithMessage("*'FEDEX'*DHL_EXPRESS*SCRIPTED*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task API_without_saying_which_adapter_is_refused(string? key)
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Service.SetAsync(carrier, Api(key!), User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*needs a provider*");
    }

    [Fact]
    public async Task The_manual_adapter_cannot_be_chosen_as_an_API_provider()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Service.SetAsync(carrier, Api("MANUAL"), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Theory]
    [InlineData("FILE")]
    [InlineData("SMOKE_SIGNALS")]
    [InlineData("")]
    public async Task A_mode_that_is_not_manual_or_API_is_refused(string mode)
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var act = async () => await h.Service.SetAsync(
            carrier, new SetCarrierIntegrationRequest { IntegrationMode = mode, ProviderKey = "DHL_EXPRESS" }, User);

        await act.Should().ThrowAsync<BadRequestException>();
        (await Reload(h, carrier)).IntegrationMode.Should().BeNull("a refused change leaves the carrier as it was");
    }

    // ── Not while parcels are on the road ─────────────────────────────────────

    [Theory]
    [InlineData("BOOKING")]
    [InlineData("BOOKED")]
    [InlineData("LABEL_READY")]
    [InlineData("PICKED_UP")]
    [InlineData("IN_TRANSIT")]
    [InlineData("OUT_FOR_DELIVERY")]
    [InlineData("DELIVERY_ATTEMPTED")]
    [InlineData("EXCEPTION")]
    public async Task A_carrier_with_a_consignment_on_the_road_cannot_change_adapter(string status)
    {
        // Its tracking would be asked of an adapter that never heard of the airway bill.
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: "API", key: "SCRIPTED");
        await AddConsignment(h, carrier, status);

        var act = async () => await h.Service.SetAsync(carrier, Api("DHL_EXPRESS"), User);

        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("*1 consignment(s)*");
        (await Reload(h, carrier)).ProviderKey.Should().Be("SCRIPTED");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("RATED")]
    [InlineData("BOOKING_FAILED")]
    [InlineData("DELIVERED")]
    [InlineData("CANCELLED")]
    [InlineData("RETURNED_TO_ORIGIN")]
    [InlineData("LOST")]
    public async Task Consignments_that_never_went_or_have_finished_do_not_hold_a_carrier_in_place(string status)
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: "API", key: "SCRIPTED");
        await AddConsignment(h, carrier, status);

        await h.Service.SetAsync(carrier, Api("DHL_EXPRESS"), User);

        (await Reload(h, carrier)).ProviderKey.Should().Be("DHL_EXPRESS");
    }

    [Fact]
    public async Task Another_carriers_parcels_on_the_road_do_not_matter()
    {
        var h = NewHarness();
        var mine  = await NewCarrier(h, name: "Mine");
        var other = await NewCarrier(h, name: "Other");
        await AddConsignment(h, other, "IN_TRANSIT");

        await h.Service.SetAsync(mine, Api("DHL_EXPRESS"), User);

        (await Reload(h, mine)).ProviderKey.Should().Be("DHL_EXPRESS");
    }

    // ── Reading, and who may ──────────────────────────────────────────────────

    [Fact]
    public async Task A_carrier_naming_an_adapter_nobody_registers_gets_a_warning_and_no_display_name()
    {
        // The state a screen is opened to fix: an adapter that was removed, or a typo in a row.
        var h = NewHarness();
        var carrier = await NewCarrier(h, mode: "API", key: "GHOST");

        var model = await h.Service.GetAsync(carrier);

        model!.ProviderKey.Should().Be("GHOST");
        model.ProviderDisplayName.Should().BeNull();
        model.Warning.Should().Contain("GHOST");
    }

    [Fact]
    public async Task An_unknown_carrier_is_null_not_an_error()
    {
        var h = NewHarness();

        (await h.Service.GetAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Service.SetAsync(Guid.NewGuid(), Api("DHL_EXPRESS"), User)).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_carrier_cannot_be_read_or_changed()
    {
        var h = NewHarness();
        var carrier = await NewCarrier(h);

        var stranger = new CarrierIntegrationService(
            LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid()),
            new CourierProviderRegistry([new ManualCourierProvider(), new ScriptedCourierProvider("SCRIPTED")]));

        (await stranger.GetAsync(carrier)).Should().BeNull();
        (await stranger.SetAsync(carrier, Api("SCRIPTED"), User)).Should().BeNull();
        (await Reload(h, carrier)).ProviderKey.Should().BeNull();
    }

    [Fact]
    public void Choosing_who_books_a_carrier_needs_the_permission_that_decides_what_money_is_spent()
    {
        var actions = typeof(CarrierAccountsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>()
                         .Any(a => a.Template is not null
                                && (a.Template.Contains("integration") || a.Template == "providers")))
            .ToList();

        actions.Should().HaveCount(3);
        actions.Should().OnlyContain(m =>
            m.GetCustomAttribute<RequirePermissionAttribute>()!.Policy == $"Permission:{PermissionCodes.CARRIER_MANAGE}");
    }
}
