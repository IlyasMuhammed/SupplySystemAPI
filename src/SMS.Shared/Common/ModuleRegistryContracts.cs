namespace SMS.Shared.Common;

// A37 — Module Registry & Enablement (docs/module-registry/ADDENDUM-37-ANALYSIS.md). The registry is the existing
// feature catalog (tenant.FeatureDefinitions / OrganizationFeatures); these are the codes and the cross-module seams.
// Owner: REG (Tenancy). Changes go to REG by message.

/// <summary>Feature codes other modules reference (D-2, D-11, D-13). Existing codes are repeated here so callers stop
/// hard-coding strings; the catalog itself lives in SMS.Modules.Tenancy.Data.TenancyFeatureCatalog.</summary>
public static class ModuleCodes
{
    public const string MasterData     = "MODULE_MASTER_DATA";
    public const string WorkflowEngine = "MODULE_WORKFLOW_ENGINE";
    public const string Inventory      = "MODULE_INVENTORY";
    public const string Finance        = "MODULE_FINANCE";
    public const string Customers      = "MODULE_CUSTOMERS";
    public const string Suppliers      = "MODULE_SUPPLIERS";
    /// <summary>Procure-to-pay (and, today, order-to-cash — R-1).</summary>
    public const string Demand         = "MODULE_DEMAND";
    public const string Warehouse      = "MODULE_WAREHOUSE";
    public const string Logistics      = "MODULE_LOGISTICS";
    public const string Manufacturing  = "MODULE_MANUFACTURING";
    public const string Services       = "MODULE_SERVICES";
    public const string Mir            = "MODULE_MIR";
    public const string Reports        = "MODULE_REPORTS";
    public const string Notifications  = "MODULE_NOTIFICATIONS";
    public const string Integration    = "MODULE_INTEGRATION";

    // Sub-features that are enforced on real endpoints (API-CONTRACT §1.3).
    public const string BomManagement      = "FEATURE_BOM_MANAGEMENT";
    public const string RfqManagement      = "FEATURE_RFQ_MANAGEMENT";
    public const string PickLists          = "FEATURE_PICK_LISTS";
    public const string QualityInspection  = "FEATURE_QUALITY_INSPECTION";
    public const string ShipmentTracking   = "FEATURE_SHIPMENT_TRACKING";
    public const string PurchaseReturns    = "FEATURE_PURCHASE_RETURNS";
    public const string CreditManagement   = "FEATURE_CREDIT_MANAGEMENT";
}

/// <summary>
/// A37 D-14 — which module a permission code or a workflow interface code belongs to. Derived in code (no column on the
/// workflow side; Auth stores the permission's module in auth.Permissions.ModuleCode, backfilled from
/// <see cref="ForPermission"/> by its seeder). Null = not tied to a switchable module (always shown as enabled).
/// </summary>
public static class ModuleCodeMap
{
    // Order matters: the first matching prefix wins (CUSTOMER_PAYMENT_ before CUSTOMER_).
    private static readonly (string Prefix, string Module)[] PermissionPrefixes =
    [
        ("MODULES_", ModuleCodes.MasterData), ("SYSTEM_", ModuleCodes.MasterData), ("USER_", ModuleCodes.MasterData),
        ("AUDIT_", ModuleCodes.MasterData), ("PLATFORM_", ModuleCodes.MasterData), ("LOCATION_", ModuleCodes.MasterData),
        ("CURRENCY_", ModuleCodes.MasterData), ("ORG_CURRENCY_", ModuleCodes.MasterData), ("FINANCE_SETUP_", ModuleCodes.MasterData),
        ("CUSTOMER_PAYMENT_", ModuleCodes.Finance), ("CUSTOMER_LEDGER_", ModuleCodes.Finance), ("CUSTOMER_", ModuleCodes.Customers),
        ("SUPPLIER_", ModuleCodes.Suppliers), ("CONTRACT_", ModuleCodes.Suppliers),
        ("RFQ_", ModuleCodes.Demand), ("PO_", ModuleCodes.Demand), ("REQUISITION_", ModuleCodes.Demand), ("SALE_", ModuleCodes.Demand),
        ("BUDGET_", ModuleCodes.Finance), ("INVOICE_", ModuleCodes.Finance), ("PAYMENT_", ModuleCodes.Finance),
        ("RECONCILIATION", ModuleCodes.Finance), ("SALES_INVOICE_", ModuleCodes.Finance), ("PRODUCT_LEDGER_", ModuleCodes.Finance),
        ("EXCHANGE_REVALUATION_", ModuleCodes.Finance),
        ("INVENTORY_", ModuleCodes.Inventory), ("STOCK_", ModuleCodes.Inventory), ("REORDER_", ModuleCodes.Inventory),
        ("LEAD_TIME_", ModuleCodes.Inventory), ("WAREHOUSE_TRANSFER", ModuleCodes.Inventory), ("ALLOCATION_", ModuleCodes.Inventory),
        ("BOM_", ModuleCodes.Inventory),
        ("GOODS_RECEIVE", ModuleCodes.Warehouse), ("PUTAWAY", ModuleCodes.Warehouse), ("GRN_", ModuleCodes.Warehouse),
        ("PICKING", ModuleCodes.Logistics), ("DISPATCH", ModuleCodes.Logistics), ("DELIVERY_", ModuleCodes.Logistics),
        ("SHIPMENT_", ModuleCodes.Logistics), ("CARRIER_", ModuleCodes.Logistics), ("RATE_CARD_", ModuleCodes.Logistics),
        ("SHIPPING_RULE_", ModuleCodes.Logistics), ("FREIGHT_", ModuleCodes.Logistics), ("POD_", ModuleCodes.Logistics),
        ("FULFILLMENT_ROUTE_", ModuleCodes.Logistics),
        ("MATERIAL_", ModuleCodes.Mir),
        ("PROD_", ModuleCodes.Manufacturing), ("MI_", ModuleCodes.Manufacturing), ("SUPPLY_", ModuleCodes.Manufacturing),
        ("QI_", ModuleCodes.Manufacturing), ("FGR_", ModuleCodes.Manufacturing),
        ("SERVICE_ORDER_", ModuleCodes.Services),
        ("REPORT_", ModuleCodes.Reports), ("WORKFLOW_", ModuleCodes.WorkflowEngine), ("INTEGRATION_", ModuleCodes.Integration),
    ];

    /// <summary>The module of a permission code (D-14 prefix map), or null.</summary>
    public static string? ForPermission(string permissionCode) =>
        PermissionPrefixes.FirstOrDefault(p => permissionCode.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).Module;

    /// <summary>The module of a workflow interface code (D-14), or null.</summary>
    public static string? ForWorkflowInterface(string interfaceCode) => interfaceCode.ToUpperInvariant() switch
    {
        "PR" or "RFQ" or "RFQ_RESPONSE" or "QUOTATION" or "PO" or "SO" or "CUSTOMER_PO" => ModuleCodes.Demand,
        var c when c.StartsWith("SALE_")                                         => ModuleCodes.Demand,
        "GRN" or "GRN_QC" or "SRO"                                               => ModuleCodes.Warehouse,
        var c when c.StartsWith("MIR")                                           => ModuleCodes.Mir,
        "INVOICE" or "PAYMENT" or "SALES_INVOICE" or "SUPPLIER_INVOICE"          => ModuleCodes.Finance,
        "DELIVERY"                                                               => ModuleCodes.Logistics,
        "BOM" or "PRODUCTION_ORDER"                                              => ModuleCodes.Manufacturing,
        "SERVICE_ORDER"                                                          => ModuleCodes.Services,
        _                                                                        => null
    };
}

/// <summary>
/// A37 D-9 — "is this module/feature switched on for that organization right now" for code that runs outside a request
/// (Hangfire jobs, listeners). Takes the organization explicitly (the tenant filter is off in jobs). Implemented by
/// Tenancy over the cached tenant snapshot. A feature counts as enabled only while its parent module is enabled.
/// A module in its grace period counts as <b>disabled</b> here (jobs are skipped during grace, spec §4.3).
/// </summary>
public interface IModuleGate
{
    Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default);
}

/// <summary>One line of the pre-disable impact check (D-18), e.g. ("Production orders in progress", 3).</summary>
public sealed record ModuleImpactItem(string Label, int Count);

/// <summary>
/// A37 D-18 — each module that owns in-progress documents reports them for <c>GET /api/tenant/modules/{code}/impact</c>.
/// Register as many as needed (IEnumerable); Tenancy calls those whose <see cref="ModuleCode"/> matches.
/// </summary>
public interface IModuleImpactProvider
{
    string ModuleCode { get; }
    Task<IReadOnlyList<ModuleImpactItem>> GetInProgressAsync(Guid organizationId, CancellationToken ct = default);
}
