using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services.LeadTimes;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Inventory.Tests.LeadTimeKit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A34-PB-05 (T-C3-01..06, D-11, D-26): <c>GET/PUT api/variants/{uuid}/lead-times</c> — values, sources, defaults,
/// visibility by route, the supplier chain as loaded from rates / supplier records / product, validation and tenancy.
/// </summary>
public class VariantLeadTimeServiceTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static VariantLeadTimeService Service(
        string name, Guid org, FakeFulfillmentRouteLookup? routes = null, bool superAdmin = false,
        FakeSupplierLeadTimeLookup? records = null, FakeSupplierNames? names = null)
    {
        var db = Db(name, org, superAdmin);
        return new VariantLeadTimeService(db, new LeadTimeInputsLoader(db, records, names), routes);
    }

    private static VariantLeadTimeComponentModel C(VariantLeadTimesModel m, string code) => m.Components.Single(c => c.Code == code);

    [Fact]
    public async Task T_C3_01_a_manufacture_variant_shows_all_8_from_variant_and_org_defaults()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var mfg = routes.Add(OrgA, "MFG_PICK_PACK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        await DefaultsAsync(Db(name, OrgA), pickPack: 1, shipping: 3, sales: 1, mfgBuffer: 0, qc: 1, transfer: 0);
        var v = await VariantAsync(Db(name, OrgA), SupplyMethod.Manufacture, productLeadTimeDays: 4, route: mfg.Uuid,
            configure: x => { x.ManufacturingLeadTimeDays = 5; x.ManufacturingBufferDays = 2; });

        var m = await Service(name, OrgA, routes).GetAsync(v.Uuid);

        m.VariantUuid.Should().Be(v.Uuid);
        m.RouteUuid.Should().Be(mfg.Uuid);
        m.RouteCode.Should().Be("MFG_PICK_PACK_SHIP");
        m.RouteCategory.Should().Be(FulfillmentRouteCategory.Manufacture);
        m.RouteFromOrgDefault.Should().BeFalse();
        m.RequiresShipping.Should().BeTrue();
        m.Components.Select(c => c.Code).Should().Equal(LeadTimeComponentCode.All);
        m.Components.Should().OnlyContain(c => c.Visible);

        C(m, LeadTimeComponentCode.Manufacturing).Should().BeEquivalentTo(new
        {
            Name = "Manufacturing", Field = "manufacturingLeadTimeDays", StoredDays = (int?)5, ResolvedDays = 5,
            Source = LeadTimeSource.Variant, DefaultDays = 4, DefaultSource = LeadTimeSource.Product, IncludedInTotal = true
        });
        C(m, LeadTimeComponentCode.MfgBuffer).Should().BeEquivalentTo(new { StoredDays = (int?)2, ResolvedDays = 2, Source = LeadTimeSource.Variant });
        C(m, LeadTimeComponentCode.Qc).Should().BeEquivalentTo(new { StoredDays = (int?)null, ResolvedDays = 1, Source = LeadTimeSource.OrgDefault });
        C(m, LeadTimeComponentCode.Shipping).ResolvedDays.Should().Be(3);
        C(m, LeadTimeComponentCode.Supplier).IncludedInTotal.Should().BeFalse("on a MANUFACTURE route materials come through the BOM");
        // The spec's §11.1 wireframe: 5 + 2 + 1 + 0 + 1 + 3 + 1.
        m.TotalDays.Should().Be(13);
    }

    [Fact]
    public async Task T_C3_02_a_stock_variant_hides_the_manufacturing_components_and_counts_the_supplier_lead()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var ship = routes.Add(OrgA, "PICK_AND_SHIP");
        var v = await VariantAsync(Db(name, OrgA), route: ship.Uuid, configure: x => x.LeadTimeDays = 6);

        var m = await Service(name, OrgA, routes).GetAsync(v.Uuid);

        m.RouteCategory.Should().Be(FulfillmentRouteCategory.Stock);
        m.Components.Where(c => c.Visible).Select(c => c.Code).Should().Equal(
            LeadTimeComponentCode.Supplier, LeadTimeComponentCode.Qc, LeadTimeComponentCode.Transfer,
            LeadTimeComponentCode.PickPack, LeadTimeComponentCode.Shipping, LeadTimeComponentCode.SalesBuffer);
        C(m, LeadTimeComponentCode.Supplier).Should().BeEquivalentTo(new { Field = "supplierLeadTimeDays", StoredDays = (int?)6, ResolvedDays = 6, Source = LeadTimeSource.Variant });
        // No org row: system defaults 1/3/1/0/0/0 → 6 + 0 + 0 + 1 + 3 + 1.
        m.TotalDays.Should().Be(11);
    }

    [Fact]
    public async Task T_C3_03_a_route_without_SHIP_hides_shipping()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var pickOnly = routes.Add(OrgA, "PICK_ONLY");
        var v = await VariantAsync(Db(name, OrgA), route: pickOnly.Uuid);

        var m = await Service(name, OrgA, routes).GetAsync(v.Uuid);

        m.RequiresShipping.Should().BeFalse();
        C(m, LeadTimeComponentCode.Shipping).Visible.Should().BeFalse();
        C(m, LeadTimeComponentCode.Shipping).IncludedInTotal.Should().BeFalse();
        m.TotalDays.Should().Be(2, "pick/pack 1 + sales buffer 1");
    }

    [Fact]
    public async Task A_variant_without_a_route_is_judged_by_the_orgs_SHIP_default_and_without_any_route_as_stock_without_SHIP()
    {
        var name = Guid.NewGuid().ToString();
        var routes = new FakeFulfillmentRouteLookup();
        var v = await VariantAsync(Db(name, OrgA));

        var none = await Service(name, OrgA, routes).GetAsync(v.Uuid);
        none.Should().BeEquivalentTo(new { RouteUuid = (Guid?)null, RouteCategory = "STOCK", RouteFromOrgDefault = true, RequiresShipping = false });

        var dflt = routes.Add(OrgA, "PICK_AND_SHIP");
        routes.SetShippingDefault(OrgA, dflt);
        var withDefault = await Service(name, OrgA, routes).GetAsync(v.Uuid);
        withDefault.Should().BeEquivalentTo(new { RouteUuid = (Guid?)dflt.Uuid, RouteCode = "PICK_AND_SHIP", RouteFromOrgDefault = true, RequiresShipping = true });
        C(withDefault, LeadTimeComponentCode.Shipping).Visible.Should().BeTrue();
    }

    [Fact]
    public async Task T_C3_04_and_05_setting_then_clearing_pick_pack()
    {
        var name = Guid.NewGuid().ToString();
        var v = await VariantAsync(Db(name, OrgA));

        var set = await Service(name, OrgA).UpdateAsync(v.Uuid, new UpdateVariantLeadTimesRequest { PickPackDays = 2 });
        C(set, LeadTimeComponentCode.PickPack).Should().BeEquivalentTo(new
        {
            StoredDays = (int?)2, ResolvedDays = 2, Source = LeadTimeSource.Variant, DefaultDays = 1, DefaultSource = LeadTimeSource.OrgDefault
        });

        var cleared = await Service(name, OrgA).UpdateAsync(v.Uuid, new UpdateVariantLeadTimesRequest { PickPackDays = null });
        C(cleared, LeadTimeComponentCode.PickPack).Should().BeEquivalentTo(new { StoredDays = (int?)null, ResolvedDays = 1, Source = LeadTimeSource.OrgDefault });
    }

    [Fact]
    public async Task PUT_replaces_all_eight_and_stores_the_supplier_override_on_LeadTimeDays()
    {
        var name = Guid.NewGuid().ToString();
        var v = await VariantAsync(Db(name, OrgA), configure: x => { x.QualityInspectionDays = 4; x.LeadTimeDays = 9; });

        await Service(name, OrgA).UpdateAsync(v.Uuid, new UpdateVariantLeadTimesRequest
        {
            SupplierLeadTimeDays = 12, ManufacturingLeadTimeDays = 0, ManufacturingBufferDays = 3650, InternalTransferDays = 1,
            PickPackDays = 2, ShippingLeadTimeDays = 3, SalesBufferDays = 4
        });

        var stored = await Db(name, OrgA).ProductVariants.SingleAsync(x => x.Uuid == v.Uuid);
        new int?[] { stored.LeadTimeDays, stored.ManufacturingLeadTimeDays, stored.ManufacturingBufferDays, stored.QualityInspectionDays,
                     stored.InternalTransferDays, stored.PickPackDays, stored.ShippingLeadTimeDays, stored.SalesBufferDays }
            .Should().Equal(12, 0, 3650, null, 1, 2, 3, 4);
    }

    [Theory]
    [InlineData(3651)]
    [InlineData(-1)]
    public async Task Values_outside_0_to_3650_are_refused_and_nothing_changes(int days)
    {
        var name = Guid.NewGuid().ToString();
        var v = await VariantAsync(Db(name, OrgA), configure: x => x.ShippingLeadTimeDays = 5);

        var act = () => Service(name, OrgA).UpdateAsync(v.Uuid, new UpdateVariantLeadTimesRequest { ShippingLeadTimeDays = days, PickPackDays = 1 });

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Be("Shipping days must be between 0 and 3650.");
        var stored = await Db(name, OrgA).ProductVariants.SingleAsync(x => x.Uuid == v.Uuid);
        stored.ShippingLeadTimeDays.Should().Be(5);
        stored.PickPackDays.Should().BeNull();
    }

    [Fact]
    public async Task T_C3_06_a_change_of_the_org_defaults_reaches_every_variant_without_an_override()
    {
        var name = Guid.NewGuid().ToString();
        var plain = await VariantAsync(Db(name, OrgA));
        var own = await VariantAsync(Db(name, OrgA), configure: x => x.SalesBufferDays = 7);

        await new LeadTimeDefaultsService(Db(name, OrgA)).UpdateAsync(new UpdateLeadTimeDefaultsRequest
        {
            PickPackDays = 1, ShippingLeadTimeDays = 3, SalesBufferDays = 5, ManufacturingBufferDays = 0,
            QualityInspectionDays = 0, InternalTransferDays = 0
        }, 1);

        C(await Service(name, OrgA).GetAsync(plain.Uuid), LeadTimeComponentCode.SalesBuffer).ResolvedDays.Should().Be(5);
        C(await Service(name, OrgA).GetAsync(own.Uuid), LeadTimeComponentCode.SalesBuffer).ResolvedDays.Should().Be(7);
    }

    // ── D-11 as loaded ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_supplier_lead_comes_from_the_preferred_active_rate_named_and_ignores_inactive_or_expired_rows()
    {
        var name = Guid.NewGuid().ToString();
        var acme = Guid.NewGuid();
        var bolt = Guid.NewGuid();
        var v = await VariantAsync(Db(name, OrgA), productLeadTimeDays: 40, configure: x => x.DefaultSupplierId = bolt);
        await RateAsync(Db(name, OrgA), v.Id, acme, 3, preferred: true, active: false);                                    // inactive
        await RateAsync(Db(name, OrgA), v.Id, acme, 4, preferred: true, to: DateTime.UtcNow.Date.AddDays(-1));           // expired
        await RateAsync(Db(name, OrgA), v.Id, acme, 5, preferred: true, from: DateTime.UtcNow.Date.AddDays(3));          // not yet
        await RateAsync(Db(name, OrgA), v.Id, acme, 8, preferred: true);
        await RateAsync(Db(name, OrgA), v.Id, bolt, 20);

        var m = await Service(name, OrgA, names: new FakeSupplierNames().Set(acme, "ACME Ltd")).GetAsync(v.Uuid);

        C(m, LeadTimeComponentCode.Supplier).Should().BeEquivalentTo(new
        {
            StoredDays = (int?)null, ResolvedDays = 8, Source = LeadTimeSource.SupplierRate, Detail = "ACME Ltd (preferred)",
            DefaultDays = 8, DefaultSource = LeadTimeSource.SupplierRate
        });
    }

    [Fact]
    public async Task Then_the_default_suppliers_rate_then_its_record_then_the_product()
    {
        var name = Guid.NewGuid().ToString();
        var bolt = Guid.NewGuid();
        var records = new FakeSupplierLeadTimeLookup().Set(OrgA, bolt, 30);
        var withRate = await VariantAsync(Db(name, OrgA), productLeadTimeDays: 40, configure: x => x.DefaultSupplierId = bolt);
        await RateAsync(Db(name, OrgA), withRate.Id, bolt, 20);
        await RateAsync(Db(name, OrgA), withRate.Id, Guid.NewGuid(), null, preferred: true);   // preferred, but no days
        var recordOnly = await VariantAsync(Db(name, OrgA), productLeadTimeDays: 40, configure: x => x.DefaultSupplierId = bolt);
        var productOnly = await VariantAsync(Db(name, OrgA), productLeadTimeDays: 40);
        var nothing = await VariantAsync(Db(name, OrgA));

        async Task<VariantLeadTimeComponentModel> SupplierOf(Guid uuid) =>
            C(await Service(name, OrgA, records: records).GetAsync(uuid), LeadTimeComponentCode.Supplier);

        (await SupplierOf(withRate.Uuid)).Should().BeEquivalentTo(new { ResolvedDays = 20, Source = LeadTimeSource.SupplierRate });
        (await SupplierOf(recordOnly.Uuid)).Should().BeEquivalentTo(new { ResolvedDays = 30, Source = LeadTimeSource.SupplierRecord });
        (await SupplierOf(productOnly.Uuid)).Should().BeEquivalentTo(new { ResolvedDays = 40, Source = LeadTimeSource.Product });
        (await SupplierOf(nothing.Uuid)).Should().BeEquivalentTo(new { ResolvedDays = 0, Source = LeadTimeSource.SystemDefault });
    }

    // ── Tenancy ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_variant_is_not_found_for_GET_and_PUT_super_admin_included()
    {
        var name = Guid.NewGuid().ToString();
        var v = await VariantAsync(Db(name, OrgA), configure: x => x.PickPackDays = 3);

        await FluentActions.Awaiting(() => Service(name, OrgB).GetAsync(v.Uuid)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => Service(name, OrgB, superAdmin: true).GetAsync(v.Uuid)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => Service(name, OrgB, superAdmin: true)
            .UpdateAsync(v.Uuid, new UpdateVariantLeadTimesRequest { PickPackDays = 9 })).Should().ThrowAsync<NotFoundException>();

        (await Db(name, OrgA).ProductVariants.SingleAsync(x => x.Uuid == v.Uuid)).PickPackDays.Should().Be(3);
    }

    [Fact]
    public async Task A_super_admin_reads_their_own_orgs_defaults_never_another_orgs_row()
    {
        var name = Guid.NewGuid().ToString();
        await DefaultsAsync(Db(name, OrgA), pickPack: 9, shipping: 9, sales: 9, mfgBuffer: 9, qc: 9, transfer: 9);
        var v = await VariantAsync(Db(name, OrgB));

        var m = await Service(name, OrgB, superAdmin: true).GetAsync(v.Uuid);

        C(m, LeadTimeComponentCode.PickPack).ResolvedDays.Should().Be(1);
    }
}
