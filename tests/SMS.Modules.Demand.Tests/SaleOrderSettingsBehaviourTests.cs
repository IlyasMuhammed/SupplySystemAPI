using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// What the organization's sale order settings do when an order is taken (§3.2, §8.1): customer pickup,
/// the default delivery mode, the shipment flag, the notification department and the default
/// fulfilment of new lines. The screen that sets them is only worth having if these hold.
/// </summary>
public class SaleOrderSettingsBehaviourTests
{
    private const int User = 7;
    private static readonly Guid Currency = Guid.NewGuid();

    private sealed record Harness(
        DemandDbContext Db, SaleOrderService Service, Mock<IAvailabilityCheckService> Availability, Guid OrgId);

    private static async Task<Harness> NewHarness(Action<SaleOrderConfig>? configure = null)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>())).ReturnsAsync(Currency);

        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync("SO", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => $"SO-{Guid.NewGuid():N}"[..12]);

        var pricing = new Mock<IPricingService>();
        pricing.Setup(p => p.ResolveSalePriceAsync(
                It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SalePriceResolution(true, 100m, Currency, PriceResolutionTier.DefaultSelling, null));

        var availability = new Mock<IAvailabilityCheckService>();
        availability.Setup(a => a.CheckAndReserveAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ReturnsAsync((IReadOnlyList<LineReservation>)new List<LineReservation>());

        var service = new SaleOrderService(
            db, tenant, orgCurrency.Object, numbers.Object, pricing.Object, Mock.Of<IStockReservationService>(),
            Mock.Of<ITimelineService>(), Mock.Of<IBackgroundJobClient>(), availability.Object,
            Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>());

        if (configure is not null)
        {
            var config = new SaleOrderConfig { OrganizationId = tenant.OrganizationId };
            configure(config);
            db.SaleOrderConfigs.Add(config);
            await db.SaveChangesAsync();
        }

        return new Harness(db, service, availability, tenant.OrganizationId);
    }

    private static CreateSaleOrderRequest Order(string? mode, int? department = null) => new()
    {
        PartnerId = Guid.NewGuid(), CurrencyId = Currency, DeliveryMode = mode ?? string.Empty,
        // A shipped order needs somewhere to go; a collected one simply does not use it.
        ShippingAddressId = Guid.NewGuid(), IntimationDepartmentId = department,
        Lines = [new CreateSaleOrderLineRequest { VariantUuid = Guid.NewGuid(), Quantity = 2 }]
    };

    private static UpdateSaleOrderRequest Change(string? mode, int? department = null) => new()
    {
        CurrencyId = Currency, DeliveryMode = mode ?? string.Empty, ShippingAddressId = Guid.NewGuid(),
        IntimationDepartmentId = department,
        Lines = [new CreateSaleOrderLineRequest { VariantUuid = Guid.NewGuid(), Quantity = 3 }]
    };

    private static Task<SaleOrder> Stored(Harness h, Guid uuid) =>
        h.Db.SaleOrders.Include(o => o.Lines).AsNoTracking().SingleAsync(o => o.UUID == uuid);

    // ── Customer pickup ────────────────────────────────────────────────────────

    [Fact]
    public async Task With_customer_pickup_off_a_collected_order_is_refused_and_says_to_ship_it()
    {
        var h = await NewHarness(c => c.SelfPickupEnabled = false);

        var act = () => h.Service.CreateAsync(Order("SELF_PICKUP"), User);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message
            .Should().Contain("Customer pickup is switched off").And.Contain("has to be shipped");
        (await h.Db.SaleOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task With_customer_pickup_off_a_shipped_order_is_taken_as_ever()
    {
        var h = await NewHarness(c => c.SelfPickupEnabled = false);

        var uuid = await h.Service.CreateAsync(Order("SHIP"), User);

        (await Stored(h, uuid)).DeliveryMode.Should().Be("SHIP");
    }

    [Fact]
    public async Task With_customer_pickup_on_a_collected_order_is_taken()
    {
        var h = await NewHarness(c => c.SelfPickupEnabled = true);

        var uuid = await h.Service.CreateAsync(Order("SELF_PICKUP"), User);

        (await Stored(h, uuid)).DeliveryMode.Should().Be("SELF_PICKUP");
    }

    [Fact]
    public async Task An_order_cannot_be_changed_to_pickup_once_pickup_is_off_but_can_be_changed_to_shipping()
    {
        var h = await NewHarness(c => c.SelfPickupEnabled = true);
        var uuid = await h.Service.CreateAsync(Order("SELF_PICKUP"), User);
        var config = await h.Db.SaleOrderConfigs.SingleAsync();
        config.SelfPickupEnabled = false;
        await h.Db.SaveChangesAsync();

        var act = () => h.Service.UpdateAsync(uuid, Change("SELF_PICKUP"), User);
        await act.Should().ThrowAsync<BadRequestException>();

        (await h.Service.UpdateAsync(uuid, Change("SHIP"), User)).Should().BeTrue();
        var stored = await Stored(h, uuid);
        stored.DeliveryMode.Should().Be("SHIP");
        stored.RequiresShipment.Should().BeTrue();
    }

    [Fact]
    public async Task A_collected_draft_cannot_be_confirmed_once_pickup_is_off()
    {
        var h = await NewHarness(c => c.SelfPickupEnabled = true);
        var uuid = await h.Service.CreateAsync(Order("SELF_PICKUP"), User);
        var config = await h.Db.SaleOrderConfigs.SingleAsync();
        config.SelfPickupEnabled = false;
        await h.Db.SaveChangesAsync();

        var act = () => h.Service.ConfirmAsync(uuid, User);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("Customer pickup is switched off");
        h.Availability.Verify(a => a.CheckAndReserveAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
        (await Stored(h, uuid)).Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task A_shipped_draft_is_confirmed_whatever_the_pickup_setting()
    {
        var h = await NewHarness(c => c.SelfPickupEnabled = false);
        var uuid = await h.Service.CreateAsync(Order("SHIP"), User);

        (await h.Service.ConfirmAsync(uuid, User)).Should().BeTrue();

        (await Stored(h, uuid)).Status.Should().Be("CONFIRMED");
    }

    // ── The delivery mode a new order starts as ────────────────────────────────

    [Theory]
    [InlineData(true,  true,  "SHIP")]
    [InlineData(true,  false, "SHIP")]
    [InlineData(false, true,  "SELF_PICKUP")]
    [InlineData(false, false, "SHIP")]
    public async Task An_order_that_names_no_mode_starts_as_the_organizations_default(
        bool shipmentRequiredDefault, bool pickupEnabled, string expected)
    {
        var h = await NewHarness(c => { c.ShipmentRequiredDefault = shipmentRequiredDefault; c.SelfPickupEnabled = pickupEnabled; });

        var uuid = await h.Service.CreateAsync(Order(null), User);

        (await Stored(h, uuid)).DeliveryMode.Should().Be(expected);
    }

    [Theory]
    [InlineData(true,  true,  "SHIP")]
    [InlineData(false, true,  "SELF_PICKUP")]
    [InlineData(false, false, "SHIP")]
    public async Task The_defaults_a_form_opens_on_are_the_same_ones(bool shipmentRequiredDefault, bool pickupEnabled, string expected)
    {
        var h = await NewHarness(c => { c.ShipmentRequiredDefault = shipmentRequiredDefault; c.SelfPickupEnabled = pickupEnabled; });

        var defaults = await h.Service.GetDefaultsAsync();

        defaults.DeliveryMode.Should().Be(expected);
        defaults.SelfPickupEnabled.Should().Be(pickupEnabled);
    }

    [Fact]
    public async Task An_organization_that_has_never_opened_its_settings_gets_the_specs_defaults_without_a_row_being_written()
    {
        var h = await NewHarness();

        var defaults = await h.Service.GetDefaultsAsync();
        var uuid = await h.Service.CreateAsync(Order(null), User);

        defaults.DeliveryMode.Should().Be("SHIP");
        defaults.SelfPickupEnabled.Should().BeTrue();
        (await Stored(h, uuid)).DeliveryMode.Should().Be("SHIP");
        (await h.Db.SaleOrderConfigs.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_order_that_is_edited_without_naming_a_mode_keeps_the_one_it_has()
    {
        var h = await NewHarness(c => c.ShipmentRequiredDefault = true);
        var uuid = await h.Service.CreateAsync(Order("SELF_PICKUP"), User);

        await h.Service.UpdateAsync(uuid, Change(null), User);

        (await Stored(h, uuid)).DeliveryMode.Should().Be("SELF_PICKUP");
    }

    [Fact]
    public async Task A_mode_that_is_not_a_mode_is_still_refused()
    {
        var h = await NewHarness();

        var act = () => h.Service.CreateAsync(Order("DRONE"), User);

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Be("'DRONE' is not a valid delivery mode.");
    }

    // ── Does the order need a shipment ─────────────────────────────────────────

    [Fact]
    public async Task An_order_needs_a_shipment_exactly_when_it_is_shipped()
    {
        var h = await NewHarness();

        var shipped   = await Stored(h, await h.Service.CreateAsync(Order("SHIP"), User));
        var collected = await Stored(h, await h.Service.CreateAsync(Order("SELF_PICKUP"), User));

        shipped.RequiresShipment.Should().BeTrue();
        collected.RequiresShipment.Should().BeFalse();
        (await h.Service.GetByIdAsync(collected.UUID))!.RequiresShipment.Should().BeFalse();
    }

    [Fact]
    public async Task Changing_the_mode_of_a_draft_changes_whether_it_needs_a_shipment()
    {
        var h = await NewHarness();
        var uuid = await h.Service.CreateAsync(Order("SHIP"), User);

        await h.Service.UpdateAsync(uuid, Change("SELF_PICKUP"), User);

        (await Stored(h, uuid)).RequiresShipment.Should().BeFalse();
    }

    // ── The notification department ────────────────────────────────────────────

    [Fact]
    public async Task An_order_that_names_no_department_is_notified_through_the_organizations()
    {
        var h = await NewHarness(c => c.IntimationDepartmentId = 12);

        var uuid = await h.Service.CreateAsync(Order("SHIP"), User);

        (await Stored(h, uuid)).IntimationDepartmentId.Should().Be(12);
    }

    [Fact]
    public async Task An_order_that_names_a_department_keeps_it()
    {
        var h = await NewHarness(c => c.IntimationDepartmentId = 12);

        var uuid = await h.Service.CreateAsync(Order("SHIP", department: 5), User);

        (await Stored(h, uuid)).IntimationDepartmentId.Should().Be(5);
    }

    [Fact]
    public async Task With_no_organization_department_an_order_has_none()
    {
        var h = await NewHarness();

        var uuid = await h.Service.CreateAsync(Order("SHIP"), User);

        (await Stored(h, uuid)).IntimationDepartmentId.Should().BeNull();
    }

    [Fact]
    public async Task An_edited_order_that_names_no_department_falls_back_to_the_organizations_too()
    {
        var h = await NewHarness();
        var uuid = await h.Service.CreateAsync(Order("SHIP"), User);
        var config = new SaleOrderConfig { OrganizationId = h.OrgId, IntimationDepartmentId = 9 };
        h.Db.SaleOrderConfigs.Add(config);
        await h.Db.SaveChangesAsync();

        await h.Service.UpdateAsync(uuid, Change("SHIP"), User);

        (await Stored(h, uuid)).IntimationDepartmentId.Should().Be(9);
    }

    // ── What a new line is taken to be ─────────────────────────────────────────

    [Theory]
    [InlineData("IN_STOCK",     false, "SHIP",        null)]
    [InlineData("IN_STOCK",     true,  "SHIP",        null)]
    [InlineData("BACK_TO_BACK", false, "SHIP",        "BACK_TO_BACK")]
    [InlineData("BACK_TO_BACK", false, "SELF_PICKUP", "BACK_TO_BACK")]
    [InlineData("DROP_SHIP",    true,  "SHIP",        "DROP_SHIP")]
    // Drop ship needs somewhere to ship to, and an organization that has it off cannot have it.
    [InlineData("DROP_SHIP",    true,  "SELF_PICKUP", null)]
    [InlineData("DROP_SHIP",    false, "SHIP",        null)]
    public async Task New_lines_start_as_the_organizations_default_fulfilment_where_that_can_apply(
        string defaultMode, bool dropShipEnabled, string orderMode, string? expectedLineMode)
    {
        var h = await NewHarness(c => { c.DefaultFulfillmentMode = defaultMode; c.DropShipEnabled = dropShipEnabled; });

        var uuid = await h.Service.CreateAsync(Order(orderMode), User);

        (await Stored(h, uuid)).Lines.Should().ContainSingle().Which.FulfillmentMode.Should().Be(expectedLineMode);
    }

    [Fact]
    public async Task Editing_a_draft_gives_its_new_lines_the_default_fulfilment_too()
    {
        var h = await NewHarness(c => c.DefaultFulfillmentMode = "BACK_TO_BACK");
        var uuid = await h.Service.CreateAsync(Order("SHIP"), User);
        var config = await h.Db.SaleOrderConfigs.SingleAsync();
        config.DefaultFulfillmentMode = "IN_STOCK";
        await h.Db.SaveChangesAsync();

        await h.Service.UpdateAsync(uuid, Change("SHIP"), User);

        (await Stored(h, uuid)).Lines.Should().ContainSingle().Which.FulfillmentMode.Should().BeNull();
    }

    [Fact]
    public async Task A_default_fulfilment_never_changes_a_lines_price_or_status()
    {
        var h = await NewHarness(c => c.DefaultFulfillmentMode = "BACK_TO_BACK");

        var uuid = await h.Service.CreateAsync(Order("SHIP"), User);

        var line = (await Stored(h, uuid)).Lines.Single();
        line.UnitPrice.Should().Be(100m);
        line.Status.Should().Be("OPEN");
        line.DeficitQty.Should().BeNull("what has to be bought is worked out at confirmation, not on taking the order");
    }
}
