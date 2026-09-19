using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

/// <summary>
/// Addendum 29 §2.3's sale-price waterfall. See <see cref="IPricingService"/> for the five tiers;
/// this class is only the query for each one, evaluated in order and stopping at the first hit.
/// </summary>
internal sealed class PricingService : IPricingService
{
    private readonly InventoryDbContext _db;

    public PricingService(InventoryDbContext db) => _db = db;

    public async Task<SalePriceResolution> ResolveSalePriceAsync(
        Guid variantUuid, Guid? partnerUuid, decimal qty, DateTime date, CancellationToken ct = default)
    {
        var variant = await _db.ProductVariants.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Uuid == variantUuid, ct);
        if (variant is null)
            return new SalePriceResolution(false, null, null, null, null);

        // The global tenant query filter already scopes this to the caller's org.
        var candidates = _db.PricingRules.AsNoTracking()
            .Where(r => r.VariantId == variant.Id && r.IsActive)
            .Where(r => r.MinQty == null || r.MinQty <= qty)
            .Where(r => r.MaxQty == null || r.MaxQty >= qty)
            .Where(r => r.EffectiveFrom <= date)
            .Where(r => r.EffectiveTo == null || r.EffectiveTo >= date);

        // 1. CONTRACT — inherently partner-specific; with no partner there is nothing to match.
        if (partnerUuid is { } contractPartner)
        {
            var contract = await Best(candidates.Where(r =>
                r.PriceType == PricingRuleType.Contract && r.PartnerId == contractPartner), ct);
            if (contract is not null)
                return Resolved(contract, PriceResolutionTier.Contract);
        }

        // 2. PROMOTIONAL — a promotion targeted at this partner wins over a general one; a
        // promotion scoped to a *different* partner must never leak into this resolution.
        var promotional = await Best(candidates.Where(r =>
            r.PriceType == PricingRuleType.Promotional &&
            (r.PartnerId == null || r.PartnerId == partnerUuid)), ct);
        if (promotional is not null)
            return Resolved(promotional, PriceResolutionTier.Promotional);

        // 3. Partner-specific SELLING.
        if (partnerUuid is { } sellingPartner)
        {
            var partnerSelling = await Best(candidates.Where(r =>
                r.PriceType == PricingRuleType.Selling && r.PartnerId == sellingPartner), ct);
            if (partnerSelling is not null)
                return Resolved(partnerSelling, PriceResolutionTier.PartnerSelling);
        }

        // 4. Default SELLING (org-wide, partner NULL).
        var defaultSelling = await Best(candidates.Where(r =>
            r.PriceType == PricingRuleType.Selling && r.PartnerId == null), ct);
        if (defaultSelling is not null)
            return Resolved(defaultSelling, PriceResolutionTier.DefaultSelling);

        // 5. The variant's own list price — no PricingRule row, so no currency or rule id.
        if (variant.SellingPrice is { } sellingPrice)
            return new SalePriceResolution(true, sellingPrice, null, PriceResolutionTier.VariantDefault, null);

        return new SalePriceResolution(false, null, null, null, null);
    }

    // Ties within a tier resolve to the most recently effective rule — the same "newest
    // effective_from wins" rule §2.4 uses for purchase-price resolution, kept consistent here.
    private static Task<PricingRule?> Best(IQueryable<PricingRule> query, CancellationToken ct) =>
        query.OrderByDescending(r => r.EffectiveFrom).FirstOrDefaultAsync(ct);

    private static SalePriceResolution Resolved(PricingRule rule, string tier) =>
        new(true, rule.UnitPrice, rule.CurrencyId, tier, rule.Uuid);
}
