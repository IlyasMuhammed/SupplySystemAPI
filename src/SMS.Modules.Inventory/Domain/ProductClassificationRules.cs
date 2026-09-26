using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Domain;

/// <summary>
/// The valid combinations of product type, supply method and flags (A30 §6.5), and the one rule
/// that reaches down to variants: only a product whose type can be a BOM input may have a
/// variant available for production (the spec's <c>is_bom_input</c>, kept on the variant as
/// <c>IsAvailableForProduction</c> by decision D2).
/// </summary>
internal static class ProductClassificationRules
{
    /// <summary>The first violation, or null when the combination is allowed.</summary>
    public static string? Problem(string productType, string supplyMethod, bool isStockable)
    {
        if (!ProductType.IsKnown(productType))
            return $"'{productType}' is not a product type. Use one of: {string.Join(", ", ProductType.All)}.";

        if (!SupplyMethod.IsKnown(supplyMethod))
            return $"'{supplyMethod}' is not a supply method. Use one of: {string.Join(", ", SupplyMethod.All)}.";

        var capabilities = ProductTypeRules.For(productType);

        if (supplyMethod == SupplyMethod.Manufacture && !capabilities.CanManufacture)
            return $"A {Describe(productType)} cannot be manufactured. Only a semi-finished or finished good can have supply method MANUFACTURE.";

        if (productType == ProductType.FinishedGood && supplyMethod != SupplyMethod.Manufacture)
            return "A finished good is manufactured; its supply method must be MANUFACTURE.";

        if (productType == ProductType.Service && isStockable)
            return "A service is not held in stock; it cannot be stockable.";

        return null;
    }

    /// <summary>
    /// Why a variant of this product may not be available for production, or null when it may.
    /// </summary>
    public static string? ProductionInputProblem(string productType, string productName) =>
        ProductType.IsKnown(productType) && ProductTypeRules.For(productType).CanBeBomInput
            ? null
            : $"'{productName}' is a {Describe(productType)} and cannot be a production input. " +
              "Only raw materials, components, semi-finished goods, finished goods, consumables and services can be used on a bill of materials.";

    public static string Describe(string productType) => productType switch
    {
        ProductType.StockItem    => "stock item",
        ProductType.RawMaterial  => "raw material",
        ProductType.Component    => "component",
        ProductType.SemiFinished => "semi-finished good",
        ProductType.FinishedGood => "finished good",
        ProductType.Consumable   => "consumable",
        ProductType.Service      => "service",
        ProductType.Asset        => "asset",
        _                        => productType
    };

    public static string Normalise(string? code) => (code ?? string.Empty).Trim().ToUpperInvariant();
}
