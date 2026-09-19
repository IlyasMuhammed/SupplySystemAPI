namespace SMS.Shared.Common;

public sealed record DefaultVariantResult(
    Guid   ProductUuid,
    Guid   VariantUuid,
    string Sku,
    string VariantName);

/// <summary>
/// What a document line needs to say about a variant it references only by id.
/// </summary>
/// <param name="IsDefault">
/// True for the variant that stands in for its product, whose name is then just the product's.
/// </param>
public sealed record VariantDescription(
    Guid    VariantUuid,
    Guid    ProductUuid,
    string  Sku,
    string  VariantName,
    string  ProductName,
    bool    IsDefault,
    string? UomCode)
{
    /// <summary>
    /// "Product (SKU)" for a default variant, "Product - Variant (SKU)" otherwise — the same
    /// wording Demand writes onto an auto-generated purchase order line.
    /// </summary>
    public string DisplayName => IsDefault
        ? $"{ProductName} ({Sku})"
        : $"{ProductName} - {VariantName} ({Sku})";
}

/// <summary>
/// Resolves a product to the variant that stands in for it.
/// <para>
/// Needed because the system is inconsistent about which of the two a document line carries:
/// purchase-order and material-issue lines reference a <c>VariantUuid</c>, but supplier-return
/// lines are still product-scoped and carry a <c>ProductUuid</c>. Anything that has to touch
/// stock — which is always variant-level — has to bridge that gap.
/// </para>
/// <para>
/// The rule is the one <c>SroRepository.DispatchAsync</c> already applies when it deducts stock
/// for a return: a multi-variant product resolves to its default variant. Keeping that rule in
/// one place means the delivery and the dispatch cannot disagree about which variant moved.
/// </para>
/// <para>
/// Implemented in SMS.Modules.Inventory and resolved through DI, so consumers need no project
/// reference to it — the same arrangement as <see cref="ICityLookupService"/>.
/// </para>
/// </summary>
public interface IProductVariantResolver
{
    /// <summary>
    /// Maps each product UUID to its default variant. Products with no default (or no active
    /// variant at all) are simply absent from the result, so callers must decide what that means
    /// for them rather than receiving a silent null.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DefaultVariantResult>> ResolveDefaultVariantsAsync(
        IReadOnlyList<Guid> productUuids);

    /// <summary>
    /// Describes variants a document line references only by id — a sale order line carries
    /// nothing but a <c>VariantUuid</c>, while a delivery line has to be readable on a pick list.
    /// Unknown or inactive variants are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, VariantDescription>> DescribeVariantsAsync(
        IReadOnlyList<Guid> variantUuids);
}
