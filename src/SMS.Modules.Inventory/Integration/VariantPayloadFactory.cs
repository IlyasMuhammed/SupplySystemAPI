using SMS.Modules.Inventory.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Inventory.Integration;

/// <summary>
/// A product variant as the QuickBooks gateway wants to hear about it. The variant is what is bought and
/// sold (every invoice line names a variant), so one variant is one QuickBooks item; the parent product
/// supplies the name, description, kind and the sold/purchased flags.
/// </summary>
internal static class VariantPayloadFactory
{
    /// <param name="variant">The variant, with <see cref="ProductVariant.Product"/> loaded.</param>
    /// <param name="variantsOnProduct">
    /// How many variants of the product count when naming this one — see <see cref="NamingCount"/>. More than
    /// one and the variant's own name is sent too, so two variants of one product never share an item name.
    /// </param>
    public static ItemPayload Build(ProductVariant variant, int variantsOnProduct)
    {
        var product = variant.Product
            ?? throw new InvalidOperationException($"Variant {variant.Uuid} was loaded without its product.");

        return new ItemPayload
        {
            ExternalId   = variant.Uuid.ToString(),
            Name         = product.Name?.Trim() ?? string.Empty,
            VariantName  = variantsOnProduct > 1 ? DistinguishingName(variant, product) : null,
            Sku          = variant.Sku?.Trim() ?? string.Empty,
            Description  = Clean(product.Description),
            Kind         = string.Equals(product.ProductType, SMS.Shared.Common.ProductType.Service, StringComparison.OrdinalIgnoreCase)
                               ? ItemPayloadKind.Service
                               : ItemPayloadKind.Goods,
            SalesPrice   = variant.SellingPrice,
            PurchaseCost = variant.PurchasePrice,
            IsSold       = product.IsSaleable,
            IsPurchased  = product.IsPurchasable,
            IsActive     = variant.IsActive && product.IsActive
        };
    }

    /// <summary>
    /// The variants that count for naming: the product's active ones (a soft-deleted variant is inactive),
    /// plus this one when it is itself inactive. So a variant being retired keeps the name QuickBooks
    /// already knows it by, while its last remaining sibling goes back to the plain product name.
    /// </summary>
    internal static int NamingCount(int activeVariantsOfProduct, ProductVariant variant) =>
        activeVariantsOfProduct + (variant.IsActive ? 0 : 1);

    /// <summary>
    /// The variant's name, unless it only repeats the product's — a product's auto-created default variant is
    /// named after the product, and "Cement - Cement" tells the accountant nothing "Cement" does not.
    /// </summary>
    private static string? DistinguishingName(ProductVariant variant, Product product)
    {
        var name = Clean(variant.VariantName);
        return name is null || string.Equals(name, product.Name?.Trim(), StringComparison.OrdinalIgnoreCase)
            ? null
            : name;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
