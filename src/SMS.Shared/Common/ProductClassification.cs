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

/// <summary>A36 D-2 — how a service product is invoiced. Stored on <c>inventory.Products.ServiceInvoicingPolicy</c>.</summary>
public static class ServiceInvoicingPolicy
{
    public const string FixedPrice       = "FIXED_PRICE";
    public const string CostPlus         = "COST_PLUS";
    public const string TimeAndMaterial  = "TIME_AND_MATERIAL";

    public static readonly IReadOnlyList<string> All = [FixedPrice, CostPlus, TimeAndMaterial];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}

/// <summary>A36 D-2 — whether materials are included in the service price or passed through to the customer.</summary>
public static class ServiceBillingModel
{
    public const string Inclusive   = "INCLUSIVE";
    public const string PassThrough = "PASS_THROUGH";

    public static readonly IReadOnlyList<string> All = [Inclusive, PassThrough];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}

/// <summary>
/// A36 D-4 — where a BOM line's input comes from. Stored on <c>material.BillOfMaterialLines.SourceType</c>.
/// Only a service BOM may carry non-STOCK lines; manufacturing BOMs stay STOCK only (SVC-BOM-06).
/// </summary>
public static class BomLineSourceType
{
    public const string Stock         = "STOCK";
    public const string Subcontract   = "SUBCONTRACT";
    public const string InternalLabor = "INTERNAL_LABOR";

    public static readonly IReadOnlyList<string> All = [Stock, Subcontract, InternalLabor];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);

    /// <summary>The unit of measure an INTERNAL_LABOR line must use (SVC-BOM-05).</summary>
    public const string LaborUom = "HR";
}

/// <summary>A37 D-10 — what kind of service a SERVICE product is (classification only, service products only).</summary>
public static class ServiceCategory
{
    public const string General      = "GENERAL";
    public const string Installation = "INSTALLATION";
    public const string Repair       = "REPAIR";
    public const string Maintenance  = "MAINTENANCE";
    public const string Consulting   = "CONSULTING";

    public static readonly IReadOnlyList<string> All = [General, Installation, Repair, Maintenance, Consulting];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}

/// <summary>A37 D-11 — which pickers a BOM is offered to first (advisory; nothing is hidden, BOM-SHR-04).</summary>
public static class BomUsage
{
    public const string Universal           = "UNIVERSAL";
    public const string ProductionPreferred = "PRODUCTION_PREFERRED";
    public const string ServicePreferred    = "SERVICE_PREFERRED";

    public static readonly IReadOnlyList<string> All = [Universal, ProductionPreferred, ServicePreferred];

    public static bool IsKnown(string? code) => code is not null && All.Contains(code);
}

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
