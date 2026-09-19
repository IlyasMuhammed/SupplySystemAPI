using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// Addendum 29 §2.3's five-tier sale-price waterfall: CONTRACT (partner-specific) &gt;
/// PROMOTIONAL &gt; partner SELLING &gt; org-wide SELLING &gt; the variant's own list price.
/// Every PricingRule tier also has to fall within its quantity band and its effective-date window.
/// </summary>
public class PricingServiceTests
{
    private static readonly DateTime Today = new(2026, 9, 19);

    private sealed record Harness(InventoryDbContext Db, PricingService Service, Guid OrgId, string DbName);

    private static Harness NewHarness()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(dbName).Options, tenant);

        return new Harness(db, new PricingService(db), tenant.OrganizationId, dbName);
    }

    private static async Task<(Guid VariantUuid, int VariantId)> SeedVariant(Harness h, decimal? sellingPrice)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = "4mm cable", Sku = $"SKU{Guid.NewGuid():N}"[..12],
            Status = "ACTIVE", IsActive = true
        };
        h.Db.Products.Add(product);
        await h.Db.SaveChangesAsync();

        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12],
            VariantName = "Default", IsDefault = true, IsActive = true, SellingPrice = sellingPrice
        };
        h.Db.ProductVariants.Add(variant);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        return (variant.Uuid, variant.Id);
    }

    private static PricingRule Rule(
        int variantId, string priceType, decimal unitPrice, Guid? partnerId = null,
        decimal? minQty = null, decimal? maxQty = null,
        DateTime? from = null, DateTime? to = null, bool isActive = true) =>
        new()
        {
            Uuid = Guid.NewGuid(), VariantId = variantId, PartnerId = partnerId, PriceType = priceType,
            UnitPrice = unitPrice, CurrencyId = Guid.NewGuid(), MinQty = minQty, MaxQty = maxQty,
            EffectiveFrom = from ?? Today.AddDays(-30), EffectiveTo = to, IsActive = isActive
        };

    [Fact]
    public async Task Falls_back_to_the_variants_own_selling_price_when_no_rule_matches()
    {
        var h = NewHarness();
        var (variantUuid, _) = await SeedVariant(h, sellingPrice: 42m);

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, partnerUuid: null, qty: 5, date: Today);

        result.Found.Should().BeTrue();
        result.UnitPrice.Should().Be(42m);
        result.Tier.Should().Be(PriceResolutionTier.VariantDefault);
        result.PricingRuleUuid.Should().BeNull();
        result.CurrencyId.Should().BeNull();
    }

    [Fact]
    public async Task Not_found_when_no_rule_matches_and_the_variant_has_no_selling_price()
    {
        var h = NewHarness();
        var (variantUuid, _) = await SeedVariant(h, sellingPrice: null);

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, partnerUuid: null, qty: 1, date: Today);

        result.Found.Should().BeFalse();
        result.UnitPrice.Should().BeNull();
    }

    [Fact]
    public async Task Default_selling_rule_beats_the_variants_own_price()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 40m, partnerId: null));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, partnerUuid: null, qty: 1, date: Today);

        result.UnitPrice.Should().Be(40m);
        result.Tier.Should().Be(PriceResolutionTier.DefaultSelling);
    }

    [Fact]
    public async Task Partner_specific_selling_rule_beats_the_default_selling_rule()
    {
        var h = NewHarness();
        var partner = Guid.NewGuid();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 40m, partnerId: null));
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 35m, partnerId: partner));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, partnerUuid: partner, qty: 1, date: Today);

        result.UnitPrice.Should().Be(35m);
        result.Tier.Should().Be(PriceResolutionTier.PartnerSelling);
    }

    [Fact]
    public async Task A_partner_specific_selling_rule_never_applies_to_a_different_partner()
    {
        var h = NewHarness();
        var partnerA = Guid.NewGuid();
        var partnerB = Guid.NewGuid();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 40m, partnerId: null));
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 35m, partnerId: partnerA));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, partnerUuid: partnerB, qty: 1, date: Today);

        // Partner B has no rule of its own, so it falls through to the org-wide default, not A's rate.
        result.UnitPrice.Should().Be(40m);
        result.Tier.Should().Be(PriceResolutionTier.DefaultSelling);
    }

    [Fact]
    public async Task Promotional_rule_beats_both_selling_rules()
    {
        var h = NewHarness();
        var partner = Guid.NewGuid();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 40m, partnerId: null));
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 35m, partnerId: partner));
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Promotional, 25m, partnerId: null));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, partnerUuid: partner, qty: 1, date: Today);

        result.UnitPrice.Should().Be(25m);
        result.Tier.Should().Be(PriceResolutionTier.Promotional);
    }

    [Fact]
    public async Task A_promotional_rule_targeted_at_a_different_partner_does_not_apply()
    {
        var h = NewHarness();
        var partnerA = Guid.NewGuid();
        var partnerB = Guid.NewGuid();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 40m, partnerId: null));
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Promotional, 25m, partnerId: partnerA));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, partnerUuid: partnerB, qty: 1, date: Today);

        result.UnitPrice.Should().Be(40m);
        result.Tier.Should().Be(PriceResolutionTier.DefaultSelling);
    }

    [Fact]
    public async Task Contract_rule_beats_everything_including_promotional()
    {
        var h = NewHarness();
        var partner = Guid.NewGuid();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 40m, partnerId: null));
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Promotional, 25m, partnerId: null));
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Contract, 18m, partnerId: partner));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, partnerUuid: partner, qty: 1, date: Today);

        result.UnitPrice.Should().Be(18m);
        result.Tier.Should().Be(PriceResolutionTier.Contract);
        result.PricingRuleUuid.Should().NotBeNull();
    }

    [Fact]
    public async Task With_no_partner_a_contract_rule_can_never_match()
    {
        var h = NewHarness();
        var partner = Guid.NewGuid();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 40m, partnerId: null));
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Contract, 18m, partnerId: partner));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, partnerUuid: null, qty: 1, date: Today);

        result.UnitPrice.Should().Be(40m);
        result.Tier.Should().Be(PriceResolutionTier.DefaultSelling);
    }

    [Fact]
    public async Task A_rule_outside_the_quantity_band_is_skipped()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 30m, minQty: 10, maxQty: 20));
        await h.Db.SaveChangesAsync();

        var tooFew  = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 5, date: Today);
        var inBand  = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 15, date: Today);
        var tooMany = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 25, date: Today);

        tooFew.Tier.Should().Be(PriceResolutionTier.VariantDefault);
        inBand.Tier.Should().Be(PriceResolutionTier.DefaultSelling);
        inBand.UnitPrice.Should().Be(30m);
        tooMany.Tier.Should().Be(PriceResolutionTier.VariantDefault);
    }

    [Fact]
    public async Task A_rule_with_no_max_qty_covers_any_quantity_above_its_minimum()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 30m, minQty: 10, maxQty: null));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 10_000, date: Today);

        result.UnitPrice.Should().Be(30m);
    }

    [Fact]
    public async Task A_rule_outside_its_effective_date_window_is_skipped()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 30m,
            from: Today.AddDays(-10), to: Today.AddDays(-1)));
        await h.Db.SaveChangesAsync();

        var expired = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 1, date: Today);

        expired.Tier.Should().Be(PriceResolutionTier.VariantDefault);
    }

    [Fact]
    public async Task A_rule_not_yet_effective_is_skipped()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 30m, from: Today.AddDays(5)));
        await h.Db.SaveChangesAsync();

        var notYet = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 1, date: Today);

        notYet.Tier.Should().Be(PriceResolutionTier.VariantDefault);
    }

    [Fact]
    public async Task An_open_ended_rule_with_null_effective_to_still_applies_far_in_the_future()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 30m, to: null));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 1, date: Today.AddYears(5));

        result.UnitPrice.Should().Be(30m);
    }

    [Fact]
    public async Task An_inactive_rule_is_never_matched()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 30m, isActive: false));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 1, date: Today);

        result.Tier.Should().Be(PriceResolutionTier.VariantDefault);
    }

    [Fact]
    public async Task When_two_rules_in_the_same_tier_match_the_most_recently_effective_one_wins()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 40m, from: Today.AddDays(-60)));
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 33m, from: Today.AddDays(-5)));
        await h.Db.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 1, date: Today);

        result.UnitPrice.Should().Be(33m);
    }

    [Fact]
    public async Task An_unknown_variant_uuid_is_not_found()
    {
        var h = NewHarness();

        var result = await h.Service.ResolveSalePriceAsync(Guid.NewGuid(), null, qty: 1, date: Today);

        result.Found.Should().BeFalse();
    }

    [Fact]
    public async Task A_quantity_exactly_at_the_minimum_or_maximum_boundary_still_matches()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 30m, minQty: 10, maxQty: 20));
        await h.Db.SaveChangesAsync();

        var atMin = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 10, date: Today);
        var atMax = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 20, date: Today);

        atMin.Tier.Should().Be(PriceResolutionTier.DefaultSelling);
        atMax.Tier.Should().Be(PriceResolutionTier.DefaultSelling);
    }

    [Fact]
    public async Task A_date_exactly_on_the_effective_from_or_effective_to_boundary_still_matches()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 30m,
            from: Today.AddDays(-5), to: Today.AddDays(5)));
        await h.Db.SaveChangesAsync();

        var onStart = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 1, date: Today.AddDays(-5));
        var onEnd   = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 1, date: Today.AddDays(5));

        onStart.Tier.Should().Be(PriceResolutionTier.DefaultSelling);
        onEnd.Tier.Should().Be(PriceResolutionTier.DefaultSelling);
    }

    [Fact]
    public async Task Overlapping_quantity_ranges_on_two_active_rules_each_apply_only_where_they_actually_cover()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);
        // A covers 1-10, B covers 8-20 — 8, 9, 10 are the overlap.
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 40m, minQty: 1, maxQty: 10,
            from: Today.AddDays(-60)));
        h.Db.PricingRules.Add(Rule(variantId, PricingRuleType.Selling, 35m, minQty: 8, maxQty: 20,
            from: Today.AddDays(-5)));
        await h.Db.SaveChangesAsync();

        var onlyInA      = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 3, date: Today);
        var onlyInB      = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 15, date: Today);
        var inBothRanges = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 9, date: Today);

        onlyInA.UnitPrice.Should().Be(40m);
        onlyInB.UnitPrice.Should().Be(35m);
        // Both rules genuinely match at qty 9 — the same "most recently effective wins" tie-break
        // used everywhere else in this resolver decides it, not an arbitrary or undefined pick.
        inBothRanges.UnitPrice.Should().Be(35m);
    }

    [Fact]
    public async Task Rules_scoped_to_a_different_organization_are_invisible()
    {
        var h = NewHarness();
        var (variantUuid, variantId) = await SeedVariant(h, sellingPrice: 42m);

        // A different org's rule, written through a second context over the same in-memory DB
        // with a different tenant, is the only way to actually exercise the global query filter
        // rather than assume it applies.
        var otherOrg = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        await using var otherDb = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(h.DbName).Options, otherOrg);
        otherDb.PricingRules.Add(new PricingRule
        {
            Uuid = Guid.NewGuid(), OrganizationId = otherOrg.OrganizationId, VariantId = variantId,
            PriceType = PricingRuleType.Selling, UnitPrice = 1m, CurrencyId = Guid.NewGuid(),
            EffectiveFrom = Today.AddDays(-1), IsActive = true
        });
        await otherDb.SaveChangesAsync();

        var result = await h.Service.ResolveSalePriceAsync(variantUuid, null, qty: 1, date: Today);

        result.Tier.Should().Be(PriceResolutionTier.VariantDefault);
    }
}
