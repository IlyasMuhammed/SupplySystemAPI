using FluentAssertions;
using SMS.Modules.Inventory.Services.LeadTimes;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>A34-PB-06: the pure resolver — BR-C3-02 (variant → org default), the D-11 supplier chain, the D-12 level days, BR-C3-04/05.</summary>
public class LeadTimeResolverTests
{
    private static readonly LeadTimeDefaultValues OrgRow = new(
        PickPackDays: 2, ShippingLeadTimeDays: 4, SalesBufferDays: 3, ManufacturingBufferDays: 5,
        QualityInspectionDays: 6, InternalTransferDays: 7, IsSaved: true);

    private static LeadTimeInputs Inputs(
        VariantLeadTimeOverrides? overrides = null, LeadTimeDefaultValues? defaults = null, SupplierLeadFacts? supplier = null,
        int? productLeadTimeDays = null, bool manufactured = false) =>
        new(overrides ?? VariantLeadTimeOverrides.None, defaults ?? OrgRow, supplier ?? SupplierLeadFacts.None,
            productLeadTimeDays, manufactured);

    // ── BR-C3-02: the six defaulted components ─────────────────────────────────────────────────

    [Theory]
    [InlineData(LeadTimeComponentCode.PickPack, 2)]
    [InlineData(LeadTimeComponentCode.Shipping, 4)]
    [InlineData(LeadTimeComponentCode.SalesBuffer, 3)]
    [InlineData(LeadTimeComponentCode.MfgBuffer, 5)]
    [InlineData(LeadTimeComponentCode.Qc, 6)]
    [InlineData(LeadTimeComponentCode.Transfer, 7)]
    public void T_C3_05_a_null_override_reads_the_org_default(string code, int expected)
    {
        LeadTimeResolver.Resolve(code, Inputs()).Should().Be(new ResolvedLeadTime(expected, LeadTimeSource.OrgDefault));
    }

    [Fact]
    public void T_C3_04_a_set_override_wins_with_source_VARIANT_and_zero_is_a_real_value()
    {
        var overrides = VariantLeadTimeOverrides.None with { PickPack = 9, Shipping = 0 };

        LeadTimeResolver.Resolve(LeadTimeComponentCode.PickPack, Inputs(overrides)).Should().Be(new ResolvedLeadTime(9, LeadTimeSource.Variant));
        LeadTimeResolver.Resolve(LeadTimeComponentCode.Shipping, Inputs(overrides)).Should().Be(new ResolvedLeadTime(0, LeadTimeSource.Variant));
        LeadTimeResolver.Default(LeadTimeComponentCode.PickPack, Inputs(overrides)).Should().Be(new ResolvedLeadTime(2, LeadTimeSource.OrgDefault));
    }

    [Fact]
    public void An_org_without_a_row_reads_the_system_defaults_1_3_1_0_0_0()
    {
        var inputs = Inputs(defaults: LeadTimeDefaultValues.System);

        new[] { LeadTimeComponentCode.PickPack, LeadTimeComponentCode.Shipping, LeadTimeComponentCode.SalesBuffer,
                LeadTimeComponentCode.MfgBuffer, LeadTimeComponentCode.Qc, LeadTimeComponentCode.Transfer }
            .Select(c => LeadTimeResolver.Resolve(c, inputs).Days).Should().Equal(1, 3, 1, 0, 0, 0);
        LeadTimeResolver.Resolve(LeadTimeComponentCode.PickPack, inputs).Source.Should().Be(LeadTimeSource.OrgDefault);
    }

    // ── D-11: the supplier chain ───────────────────────────────────────────────────────────────

    private static readonly SupplierLeadFacts AllTiers = new(
        PreferredRateDays: 10, PreferredSupplierName: "ACME Ltd",
        DefaultSupplierRateDays: 20, DefaultSupplierName: "Bolt Co",
        SupplierRecordDays: 30, SupplierRecordName: "Bolt Co");

    [Fact]
    public void Supplier_tier_1_the_variant_override()
    {
        var r = LeadTimeResolver.Resolve(LeadTimeComponentCode.Supplier,
            Inputs(VariantLeadTimeOverrides.None with { Supplier = 4 }, supplier: AllTiers, productLeadTimeDays: 40));

        r.Should().Be(new ResolvedLeadTime(4, LeadTimeSource.Variant));
    }

    [Fact]
    public void Supplier_tier_2_the_preferred_rate_names_the_supplier()
    {
        var r = LeadTimeResolver.Resolve(LeadTimeComponentCode.Supplier, Inputs(supplier: AllTiers, productLeadTimeDays: 40));

        r.Days.Should().Be(10);
        r.Source.Should().Be(LeadTimeSource.SupplierRate);
        r.Detail.Should().Contain("ACME Ltd").And.Contain("preferred");
    }

    [Fact]
    public void Supplier_tier_3_the_default_suppliers_rate()
    {
        var r = LeadTimeResolver.Resolve(LeadTimeComponentCode.Supplier,
            Inputs(supplier: AllTiers with { PreferredRateDays = null }, productLeadTimeDays: 40));

        r.Days.Should().Be(20);
        r.Source.Should().Be(LeadTimeSource.SupplierRate);
        r.Detail.Should().Contain("Bolt Co");
    }

    [Fact]
    public void Supplier_tier_4_the_supplier_record()
    {
        var r = LeadTimeResolver.Resolve(LeadTimeComponentCode.Supplier,
            Inputs(supplier: AllTiers with { PreferredRateDays = null, DefaultSupplierRateDays = null }, productLeadTimeDays: 40));

        r.Days.Should().Be(30);
        r.Source.Should().Be(LeadTimeSource.SupplierRecord);
    }

    [Fact]
    public void Supplier_tier_5_the_product_then_0()
    {
        LeadTimeResolver.Resolve(LeadTimeComponentCode.Supplier, Inputs(productLeadTimeDays: 40))
            .Should().Match<ResolvedLeadTime>(r => r.Days == 40 && r.Source == LeadTimeSource.Product);
        LeadTimeResolver.Resolve(LeadTimeComponentCode.Supplier, Inputs())
            .Should().Match<ResolvedLeadTime>(r => r.Days == 0 && r.Source == LeadTimeSource.SystemDefault);
    }

    // ── D-12: manufacturing days per BOM level ─────────────────────────────────────────────────

    [Fact]
    public void Manufacturing_days_variant_then_the_product_of_a_manufactured_product_then_1()
    {
        LeadTimeResolver.Resolve(LeadTimeComponentCode.Manufacturing,
                Inputs(VariantLeadTimeOverrides.None with { Manufacturing = 5 }, productLeadTimeDays: 8, manufactured: true))
            .Should().Be(new ResolvedLeadTime(5, LeadTimeSource.Variant));
        LeadTimeResolver.Resolve(LeadTimeComponentCode.Manufacturing, Inputs(productLeadTimeDays: 8, manufactured: true))
            .Should().Match<ResolvedLeadTime>(r => r.Days == 8 && r.Source == LeadTimeSource.Product);
        LeadTimeResolver.Resolve(LeadTimeComponentCode.Manufacturing, Inputs(productLeadTimeDays: 8, manufactured: false))
            .Should().Match<ResolvedLeadTime>(r => r.Days == 1 && r.Source == LeadTimeSource.SystemDefault,
                "Product.LeadTimeDays of a purchased product is its supplier lead, not a production time");
        LeadTimeResolver.Resolve(LeadTimeComponentCode.Manufacturing, Inputs(manufactured: true))
            .Should().Match<ResolvedLeadTime>(r => r.Days == 1 && r.Source == LeadTimeSource.SystemDefault);
    }

    [Fact]
    public void A_default_is_never_VARIANT()
    {
        var everything = new VariantLeadTimeOverrides(1, 2, 3, 4, 5, 6, 7, 8);
        foreach (var code in LeadTimeComponentCode.All)
            LeadTimeResolver.Default(code, Inputs(everything, supplier: AllTiers, productLeadTimeDays: 3, manufactured: true))
                .Source.Should().NotBe(LeadTimeSource.Variant, code);
    }

    // ── BR-C3-04/05: visibility ────────────────────────────────────────────────────────────────

    [Fact]
    public void T_C3_02_a_stock_route_with_SHIP_hides_the_two_manufacturing_components()
    {
        LeadTimeComponentCode.All.Where(c => LeadTimeResolver.IsVisible(c, manufactureRoute: false, requiresShipping: true))
            .Should().Equal(LeadTimeComponentCode.Supplier, LeadTimeComponentCode.Qc, LeadTimeComponentCode.Transfer,
                LeadTimeComponentCode.PickPack, LeadTimeComponentCode.Shipping, LeadTimeComponentCode.SalesBuffer);
    }

    [Fact]
    public void T_C3_03_a_route_without_SHIP_hides_shipping()
    {
        LeadTimeResolver.IsVisible(LeadTimeComponentCode.Shipping, manufactureRoute: false, requiresShipping: false).Should().BeFalse();
        LeadTimeResolver.IsVisible(LeadTimeComponentCode.Shipping, manufactureRoute: true, requiresShipping: false).Should().BeFalse();
        LeadTimeResolver.IsVisible(LeadTimeComponentCode.PickPack, manufactureRoute: false, requiresShipping: false).Should().BeTrue();
    }

    [Fact]
    public void T_C3_01_a_manufacture_route_with_SHIP_shows_all_8_but_does_not_count_the_supplier_lead()
    {
        LeadTimeComponentCode.All.Should().OnlyContain(c => LeadTimeResolver.IsVisible(c, true, true));
        LeadTimeComponentCode.All.Where(c => LeadTimeResolver.IsIncludedInTotal(c, true, true))
            .Should().NotContain(LeadTimeComponentCode.Supplier).And.HaveCount(7);
        LeadTimeResolver.IsIncludedInTotal(LeadTimeComponentCode.Supplier, false, true).Should().BeTrue();
        LeadTimeResolver.IsIncludedInTotal(LeadTimeComponentCode.Manufacturing, false, true).Should().BeFalse("hidden");
    }
}
