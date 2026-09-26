namespace SMS.Shared.Common;

/// <summary>
/// What kind of thing a product is (A30 §6.1). Stored as the string code on
/// <c>inventory.Products.ProductType</c>; shared here because BOMs (Material) and fulfilment
/// (Demand) read it without a project reference to Inventory.
/// </summary>
public static class ProductType
{
    public const string StockItem    = "STOCK_ITEM";
    public const string RawMaterial  = "RAW_MATERIAL";
    public const string Component    = "COMPONENT";
    public const string SemiFinished = "SEMI_FINISHED";
    public const string FinishedGood = "FINISHED_GOOD";
    public const string Consumable   = "CONSUMABLE";
    public const string Service      = "SERVICE";
    public const string Asset        = "ASSET";

    public static readonly IReadOnlyList<string> All =
        [StockItem, RawMaterial, Component, SemiFinished, FinishedGood, Consumable, Service, Asset];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}

/// <summary>How a product is obtained when it is short (A30 §6.2).</summary>
public static class SupplyMethod
{
    public const string Purchase    = "PURCHASE";
    public const string Manufacture = "MANUFACTURE";
    public const string Transfer    = "TRANSFER";
    public const string Service     = "SERVICE";

    public static readonly IReadOnlyList<string> All = [Purchase, Manufacture, Transfer, Service];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}

/// <summary>
/// The §6.1 capability table, one row per <see cref="ProductType"/>. <c>CanSell</c>,
/// <c>CanPurchase</c> and <c>CanStock</c> are the <b>defaults</b> for a new product's flags and can
/// be overridden; <c>CanManufacture</c> and <c>CanBeBomInput</c> are hard rules.
/// </summary>
public sealed record ProductTypeCapabilities(
    bool CanSell,
    bool CanPurchase,
    bool CanStock,
    bool CanManufacture,
    bool CanBeBomInput);

public static class ProductTypeRules
{
    private static readonly IReadOnlyDictionary<string, ProductTypeCapabilities> Table =
        new Dictionary<string, ProductTypeCapabilities>(StringComparer.Ordinal)
        {
            [ProductType.StockItem]    = new(CanSell: true,  CanPurchase: true,  CanStock: true,  CanManufacture: false, CanBeBomInput: false),
            [ProductType.RawMaterial]  = new(CanSell: false, CanPurchase: true,  CanStock: true,  CanManufacture: false, CanBeBomInput: true),
            [ProductType.Component]    = new(CanSell: false, CanPurchase: true,  CanStock: true,  CanManufacture: false, CanBeBomInput: true),
            [ProductType.SemiFinished] = new(CanSell: false, CanPurchase: false, CanStock: true,  CanManufacture: true,  CanBeBomInput: true),
            // A finished good can feed another BOM: that is chained manufacturing (§6.4.1).
            [ProductType.FinishedGood] = new(CanSell: true,  CanPurchase: false, CanStock: true,  CanManufacture: true,  CanBeBomInput: true),
            [ProductType.Consumable]   = new(CanSell: false, CanPurchase: true,  CanStock: true,  CanManufacture: false, CanBeBomInput: true),
            [ProductType.Service]      = new(CanSell: true,  CanPurchase: true,  CanStock: false, CanManufacture: false, CanBeBomInput: true),
            [ProductType.Asset]        = new(CanSell: false, CanPurchase: true,  CanStock: false, CanManufacture: false, CanBeBomInput: false)
        };

    public static ProductTypeCapabilities For(string productType) =>
        Table.TryGetValue(productType, out var capabilities)
            ? capabilities
            : throw new ArgumentOutOfRangeException(nameof(productType), productType, "Unknown product type.");

    /// <summary>The supply method a product of this type gets when none is chosen.</summary>
    public static string DefaultSupplyMethod(string productType) => productType switch
    {
        ProductType.SemiFinished or ProductType.FinishedGood => SupplyMethod.Manufacture,
        ProductType.Service                                  => SupplyMethod.Service,
        _                                                    => SupplyMethod.Purchase
    };
}
