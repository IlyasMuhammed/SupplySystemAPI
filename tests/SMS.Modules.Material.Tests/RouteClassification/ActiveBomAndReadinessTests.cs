using FluentAssertions;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Material.Tests.RouteClassification;

/// <summary>
/// A34 — the one active-BOM rule (ActiveBomResolver) shared by production order creation, <see cref="IBomStructureReader"/>
/// (the lead-time calculator's recursion) and <see cref="IManufacturingReadiness"/> (DEM's D-5 confirm gate), so the gate,
/// the calculator and PO creation can never disagree. T-C2-03 (MFG side).
/// </summary>
public class ActiveBomAndReadinessTests
{
    private static readonly DateTime Today = A34Harness.Today;

    // ── The rule itself ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_variant_specific_bom_beats_a_product_general_one_even_with_a_lower_version()
    {
        var product = Guid.NewGuid(); var variant = Guid.NewGuid();
        var general  = new BillOfMaterial { ProductUuid = product, Version = 5, Status = BomStatus.Active };
        var specific = new BillOfMaterial { ProductUuid = product, ProductVariantUuid = variant, Version = 1, Status = BomStatus.Active };

        ActiveBomResolver.Pick([general, specific], product, variant).Should().BeSameAs(specific);
    }

    [Fact]
    public void Among_equals_the_highest_version_wins_and_another_variants_bom_is_never_picked()
    {
        var product = Guid.NewGuid(); var variant = Guid.NewGuid();
        var v2    = new BillOfMaterial { ProductUuid = product, Version = 2, Status = BomStatus.Active };
        var v3    = new BillOfMaterial { ProductUuid = product, Version = 3, Status = BomStatus.Active };
        var other = new BillOfMaterial { ProductUuid = product, ProductVariantUuid = Guid.NewGuid(), Version = 9, Status = BomStatus.Active };

        ActiveBomResolver.Pick([v2, other, v3], product, variant).Should().BeSameAs(v3);
        ActiveBomResolver.Pick([other], product, variant).Should().BeNull();
    }

    [Fact]
    public void Only_active_boms_effective_today_are_candidates()
    {
        var product = Guid.NewGuid();
        var boms = new[]
        {
            new BillOfMaterial { ProductUuid = product, Version = 1, Status = BomStatus.Active },
            new BillOfMaterial { ProductUuid = product, Version = 2, Status = BomStatus.Approved },
            new BillOfMaterial { ProductUuid = product, Version = 3, Status = BomStatus.Active, EffectiveFrom = Today.AddDays(1) },
            new BillOfMaterial { ProductUuid = product, Version = 4, Status = BomStatus.Active, EffectiveTo = Today.AddDays(-1) },
            new BillOfMaterial { ProductUuid = product, Version = 5, Status = BomStatus.Active, EffectiveFrom = Today, EffectiveTo = Today },
            new BillOfMaterial { ProductUuid = Guid.NewGuid(), Version = 6, Status = BomStatus.Active }
        };

        ActiveBomResolver.Candidates(boms.AsQueryable(), [product], Today).Select(b => b.Version)
            .Should().BeEquivalentTo([1, 5]);
    }

    // ── IBomStructureReader ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_reader_returns_the_active_bom_with_its_inputs_and_leaves_out_variants_without_one()
    {
        var h = new A34Harness();
        var (kit, kitV)       = h.Product("Bolt Kit", manufactured: true);
        var (rod, rodV)       = h.Product("Steel Rod", manufactured: false);
        var (_, loneV)        = h.Product("No BOM", manufactured: true);
        var bomUuid = await h.ActiveBomAsync(kit, 2m, (rodV, 4m, 10m));

        var result = await h.Get<IBomStructureReader>().GetActiveBomsAsync(h.Org, [kitV, loneV]);

        result.Keys.Should().BeEquivalentTo([kitV]);
        var bom = result[kitV];
        bom.BomUuid.Should().Be(bomUuid);
        bom.BaseQuantity.Should().Be(2m);
        bom.Version.Should().Be(1);
        bom.Inputs.Should().ContainSingle()
           .Which.Should().Be(new BomInput(rodV, rod, 4m, 10m));
    }

    [Fact]
    public async Task The_reader_and_production_order_creation_pick_the_same_bom()
    {
        var h = new A34Harness();
        var (kit, kitV) = h.Product("Bolt Kit", manufactured: true);
        var (rod, rodV) = h.Product("Steel Rod", manufactured: false);
        h.RawBom(kit, null, version: 7, lines: (rodV, rod, 1m, 0m));
        var specific = h.RawBom(kit, kitV, version: 2, lines: (rodV, rod, 3m, 0m));

        var read = await h.Get<IBomStructureReader>().GetActiveBomsAsync(h.Org, [kitV]);
        var poUuid = await h.Orders.CreateAsync(new CreateProductionOrderRequest
        {
            ProductUuid = kit, ProductVariantUuid = kitV, PlannedQuantity = 1, RequiredDate = Today.AddDays(2)
        }, A34Harness.Author);
        var po = await h.Orders.GetByUuidAsync(poUuid);

        read[kitV].BomUuid.Should().Be(specific.UUID);
        po!.BomNumber.Should().Be(specific.BomNumber);
    }

    [Fact]
    public async Task Another_organizations_variant_and_bom_read_as_absent()
    {
        var h = new A34Harness();
        var (kit, kitV) = h.Product("Their Kit", manufactured: true, organizationId: h.OtherOrg);
        var (rod, rodV) = h.Product("Their Rod", manufactured: false, organizationId: h.OtherOrg);
        h.RawBom(kit, null, version: 1, organizationId: h.OtherOrg, lines: (rodV, rod, 1m, 0m));

        (await h.Get<IBomStructureReader>().GetActiveBomsAsync(h.Org, [kitV])).Should().BeEmpty();
        (await h.Get<IManufacturingReadiness>().CheckAsync(h.Org, [kitV])).Should().BeEmpty();

        // Asked for their own organization explicitly (a Hangfire job, a super admin), the rows are found.
        (await h.Get<IBomStructureReader>().GetActiveBomsAsync(h.OtherOrg, [kitV])).Keys.Should().BeEquivalentTo([kitV]);
        (await h.Get<IManufacturingReadiness>().CheckAsync(h.OtherOrg, [kitV]))[kitV].HasActiveBom.Should().BeTrue();
    }

    // ── IManufacturingReadiness (D-5) ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Readiness_reports_each_of_the_three_material_side_blockers()
    {
        var h = new A34Harness();
        var (rod, rodV)           = h.Product("Steel Rod", manufactured: false);
        var (ready, readyV)       = h.Product("Bolt Kit", manufactured: true);
        var (noBom, noBomV)       = h.Product("Loose Kit", manufactured: true);
        var (noWh, noWhV)         = h.Product("Field Kit", manufactured: true, productionWarehouse: false);
        var blueV                 = h.Variant(ready, "Blue");
        await h.ActiveBomAsync(ready, 1m, (rodV, 1m, 0m));
        h.RawBom(noWh, null, version: 1, lines: (rodV, rod, 1m, 0m));
        _ = noBom;

        var result = await h.Get<IManufacturingReadiness>().CheckAsync(h.Org, [readyV, noBomV, noWhV, rodV, blueV]);

        result[readyV].Should().Be(new ManufacturingReadinessInfo(readyV, "Bolt Kit", true, true, true));
        result[blueV].Should().Be(new ManufacturingReadinessInfo(blueV, "Bolt Kit — Blue", true, true, true),
            "a product-general BOM covers every variant");
        result[noBomV].HasActiveBom.Should().BeFalse();
        result[noWhV].HasProductionWarehouse.Should().BeFalse();
        result[noWhV].HasActiveBom.Should().BeTrue();
        result[rodV].IsManufactured.Should().BeFalse();
    }
}
