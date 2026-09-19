namespace SMS.Shared.Common;

/// <summary>The tier that produced a <see cref="SalePriceResolution"/>, in priority order.</summary>
public static class PriceResolutionTier
{
    public const string Contract        = "CONTRACT";
    public const string Promotional     = "PROMOTIONAL";
    public const string PartnerSelling  = "PARTNER_SELLING";
    public const string DefaultSelling  = "DEFAULT_SELLING";
    public const string VariantDefault  = "VARIANT_DEFAULT";
}

/// <param name="Found">False when nothing matched at any tier, including the variant's own selling price.</param>
/// <param name="UnitPrice">The resolved price. Null when <paramref name="Found"/> is false.</param>
/// <param name="CurrencyId">
/// The currency the price is quoted in. Null for <see cref="PriceResolutionTier.VariantDefault"/> —
/// <c>ProductVariant.SellingPrice</c> carries no currency of its own, so a null here means "the
/// org's base currency", for the caller to resolve.
/// </param>
/// <param name="Tier">Which <see cref="PriceResolutionTier"/> matched, or null when not found.</param>
/// <param name="PricingRuleUuid">
/// The matched row's identity, for audit/display. Null for <see cref="PriceResolutionTier.VariantDefault"/>,
/// since that tier reads the variant itself, not a <c>PricingRule</c> row.
/// </param>
public sealed record SalePriceResolution(
    bool     Found,
    decimal? UnitPrice,
    Guid?    CurrencyId,
    string?  Tier,
    Guid?    PricingRuleUuid);

/// <summary>
/// Resolves what a variant sells for, addendum 29 §2.3's five-tier waterfall (highest priority first):
/// an active <c>CONTRACT</c> rate for this exact partner, an active <c>PROMOTIONAL</c> rate for the
/// variant (partner-targeted promotions win over general ones; a promotion scoped to a different
/// partner never applies here), an active partner-specific <c>SELLING</c> rate, an active org-wide
/// (partner <c>NULL</c>) <c>SELLING</c> rate, and finally the variant's own <c>SellingPrice</c>.
/// <para>
/// Every <c>PricingRule</c> tier also requires the requested quantity to fall within the rule's
/// <c>MinQty</c>/<c>MaxQty</c> band (a null bound is open) and the requested date to fall within its
/// <c>EffectiveFrom</c>/<c>EffectiveTo</c> window (a null <c>EffectiveTo</c> is open-ended).
/// </para>
/// </summary>
public interface IPricingService
{
    Task<SalePriceResolution> ResolveSalePriceAsync(
        Guid variantUuid, Guid? partnerUuid, decimal qty, DateTime date, CancellationToken ct = default);
}
