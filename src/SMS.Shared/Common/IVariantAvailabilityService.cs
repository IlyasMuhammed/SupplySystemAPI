namespace SMS.Shared.Common;

/// <summary>
/// The channels/documents a variant's "Available For" checkboxes (ProductVariant.IsAvailableForX)
/// say it may be transacted through. Retail and MirMiv are enforced; Pos, Production and Services
/// exist as classification only until those flows are built.
/// </summary>
public static class VariantAvailabilityChannel
{
    public const string Retail     = "RETAIL";
    public const string Pos        = "POS";
    public const string MirMiv     = "MIR_MIV";
    public const string Production = "PRODUCTION";
    public const string Services   = "SERVICES";
}

/// <param name="DisplayName">"Product (SKU)" for a default variant, "Product - Variant (SKU)"
/// otherwise — the same wording IProductVariantResolver uses, so an error naming a variant reads
/// the same way everywhere.</param>
/// <remarks>Named distinctly from IStockReservationService's own VariantAvailability (that one is
/// stock quantity; this one is which channels a variant may be sold/issued through) even though
/// both live in this namespace.</remarks>
public sealed record VariantChannelAvailability(
    string DisplayName,
    bool   IsAvailableForRetail,
    bool   IsAvailableForPos,
    bool   IsAvailableForMirMiv,
    bool   IsAvailableForProduction,
    bool   IsAvailableForServices)
{
    public bool ForChannel(string channel) => channel switch
    {
        VariantAvailabilityChannel.Retail     => IsAvailableForRetail,
        VariantAvailabilityChannel.Pos        => IsAvailableForPos,
        VariantAvailabilityChannel.MirMiv     => IsAvailableForMirMiv,
        VariantAvailabilityChannel.Production => IsAvailableForProduction,
        VariantAvailabilityChannel.Services   => IsAvailableForServices,
        _ => false
    };
}

/// <summary>
/// Cross-module lookup of a variant's channel availability. Implemented in SMS.Modules.Inventory;
/// resolved through DI, so a caller in another module needs no project reference to it — the same
/// arrangement as <see cref="IProductVariantResolver"/>.
/// </summary>
public interface IVariantAvailabilityService
{
    /// <summary>Null when the variant does not exist, or is inactive, here.</summary>
    Task<VariantChannelAvailability?> GetAvailabilityAsync(Guid variantUuid);
}
