using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services.LeadTimes;

/// <summary>An organization's six defaulted components (D-10): its row, or the system defaults when it has none.</summary>
internal sealed record LeadTimeDefaultValues(
    int  PickPackDays,
    int  ShippingLeadTimeDays,
    int  SalesBufferDays,
    int  ManufacturingBufferDays,
    int  QualityInspectionDays,
    int  InternalTransferDays,
    bool IsSaved)
{
    public static LeadTimeDefaultValues System { get; } = new(
        LeadTimeDefaults.SystemDefaults.PickPackDays, LeadTimeDefaults.SystemDefaults.ShippingLeadTimeDays,
        LeadTimeDefaults.SystemDefaults.SalesBufferDays, LeadTimeDefaults.SystemDefaults.ManufacturingBufferDays,
        LeadTimeDefaults.SystemDefaults.QualityInspectionDays, LeadTimeDefaults.SystemDefaults.InternalTransferDays,
        IsSaved: false);

    public static LeadTimeDefaultValues From(LeadTimeDefaults row) => new(
        row.PickPackDays, row.ShippingLeadTimeDays, row.SalesBufferDays, row.ManufacturingBufferDays,
        row.QualityInspectionDays, row.InternalTransferDays, IsSaved: true);

    /// <summary>The default of one of the six defaulted codes; 0 for SUPPLIER / MANUFACTURING (they have no org default).</summary>
    public int For(string code) => code switch
    {
        LeadTimeComponentCode.PickPack    => PickPackDays,
        LeadTimeComponentCode.Shipping    => ShippingLeadTimeDays,
        LeadTimeComponentCode.SalesBuffer => SalesBufferDays,
        LeadTimeComponentCode.MfgBuffer   => ManufacturingBufferDays,
        LeadTimeComponentCode.Qc          => QualityInspectionDays,
        LeadTimeComponentCode.Transfer    => InternalTransferDays,
        _                                 => 0
    };
}

/// <summary>A variant's 8 overrides; null = use the fallback (BR-C3-02). Supplier = <c>ProductVariant.LeadTimeDays</c> (D-11).</summary>
internal sealed record VariantLeadTimeOverrides(
    int? Supplier,
    int? Manufacturing,
    int? MfgBuffer,
    int? Qc,
    int? Transfer,
    int? PickPack,
    int? Shipping,
    int? SalesBuffer)
{
    public static VariantLeadTimeOverrides None { get; } = new(null, null, null, null, null, null, null, null);

    public static VariantLeadTimeOverrides Of(ProductVariant v) => new(
        v.LeadTimeDays, v.ManufacturingLeadTimeDays, v.ManufacturingBufferDays, v.QualityInspectionDays,
        v.InternalTransferDays, v.PickPackDays, v.ShippingLeadTimeDays, v.SalesBufferDays);

    public int? For(string code) => code switch
    {
        LeadTimeComponentCode.Supplier      => Supplier,
        LeadTimeComponentCode.Manufacturing => Manufacturing,
        LeadTimeComponentCode.MfgBuffer     => MfgBuffer,
        LeadTimeComponentCode.Qc            => Qc,
        LeadTimeComponentCode.Transfer      => Transfer,
        LeadTimeComponentCode.PickPack      => PickPack,
        LeadTimeComponentCode.Shipping      => Shipping,
        LeadTimeComponentCode.SalesBuffer   => SalesBuffer,
        _                                   => null
    };
}

/// <summary>
/// D-11 tiers 2–4 of one variant's supplier lead, already loaded: the preferred active rate row, the default supplier's
/// active rate row, and that supplier's own record. Days are null where the tier has nothing (no row, or no days on it).
/// </summary>
internal sealed record SupplierLeadFacts(
    int?    PreferredRateDays,
    string? PreferredSupplierName,
    int?    DefaultSupplierRateDays,
    string? DefaultSupplierName,
    int?    SupplierRecordDays,
    string? SupplierRecordName)
{
    public static SupplierLeadFacts None { get; } = new(null, null, null, null, null, null);
}

/// <summary>Everything the resolver needs for one variant.</summary>
/// <param name="ProductLeadTimeDays">Product.LeadTimeDays: supplier tier 5 (D-11), and the manufacturing days of a MANUFACTURE product (D-12).</param>
/// <param name="ProductIsManufactured">The product's SupplyMethod is MANUFACTURE.</param>
internal sealed record LeadTimeInputs(
    VariantLeadTimeOverrides Overrides,
    LeadTimeDefaultValues    Defaults,
    SupplierLeadFacts        Supplier,
    int?                     ProductLeadTimeDays,
    bool                     ProductIsManufactured);

/// <summary>One component's days and where they came from (a <see cref="LeadTimeSource"/> code).</summary>
internal sealed record ResolvedLeadTime(int Days, string Source, string? Detail = null);

/// <summary>
/// A34 PB-06 — the pure part of lead-time resolution (no I/O; the loader feeds it):
/// <list type="bullet">
/// <item>the six defaulted components: variant → organization default (BR-C3-02);</item>
/// <item>SUPPLIER: variant override → preferred rate → default supplier's rate → supplier record → product → 0 (D-11);</item>
/// <item>MANUFACTURING (the days of one BOM level): variant → Product.LeadTimeDays of a MANUFACTURE product → 1 (D-12);</item>
/// <item>visibility by route (BR-C3-04/05).</item>
/// </list>
/// </summary>
internal static class LeadTimeResolver
{
    /// <summary>A variant override's upper bound (PUT api/variants/{uuid}/lead-times).</summary>
    public const int MaxVariantDays = 3650;
    /// <summary>An organization default's upper bound (PUT api/lead-time/defaults).</summary>
    public const int MaxDefaultDays = 365;
    /// <summary>D-12: one BOM level takes a day when nothing is configured.</summary>
    public const int SystemManufacturingDays = 1;

    /// <summary>The override when set (0 included: it is a real value), else <see cref="Default"/>.</summary>
    public static ResolvedLeadTime Resolve(string code, LeadTimeInputs inputs) =>
        inputs.Overrides.For(code) is int days
            ? new ResolvedLeadTime(days, LeadTimeSource.Variant)
            : Default(code, inputs);

    /// <summary>What applies with no override. Never <see cref="LeadTimeSource.Variant"/>.</summary>
    public static ResolvedLeadTime Default(string code, LeadTimeInputs inputs) => code switch
    {
        LeadTimeComponentCode.Supplier      => SupplierChain(inputs.Supplier, inputs.ProductLeadTimeDays),
        LeadTimeComponentCode.Manufacturing => ManufacturingLevel(inputs.ProductLeadTimeDays, inputs.ProductIsManufactured),
        // BR-C3-02 tier 2. Also when the organization has no row: those values are the system defaults (D-10).
        _                                   => new ResolvedLeadTime(inputs.Defaults.For(code), LeadTimeSource.OrgDefault)
    };

    /// <summary>D-11 tiers 2–5: preferred rate → default supplier's rate → supplier record → product → 0.</summary>
    private static ResolvedLeadTime SupplierChain(SupplierLeadFacts f, int? productLeadTimeDays)
    {
        if (f.PreferredRateDays is int preferred)
            return new ResolvedLeadTime(preferred, LeadTimeSource.SupplierRate, $"{f.PreferredSupplierName ?? "Supplier"} (preferred)");
        if (f.DefaultSupplierRateDays is int byDefault)
            return new ResolvedLeadTime(byDefault, LeadTimeSource.SupplierRate, $"{f.DefaultSupplierName ?? "Supplier"} (default supplier)");
        if (f.SupplierRecordDays is int record)
            return new ResolvedLeadTime(record, LeadTimeSource.SupplierRecord, $"{f.SupplierRecordName ?? "Supplier"} (supplier record)");
        if (productLeadTimeDays is int product)
            return new ResolvedLeadTime(product, LeadTimeSource.Product, "Product lead time");
        return new ResolvedLeadTime(0, LeadTimeSource.SystemDefault, "No supplier lead time is set");
    }

    /// <summary>
    /// D-12: Product.LeadTimeDays counts as manufacturing days only for a MANUFACTURE product — for a purchased one it
    /// is the supplier lead (tier 5 above), not a production time.
    /// </summary>
    private static ResolvedLeadTime ManufacturingLevel(int? productLeadTimeDays, bool productIsManufactured) =>
        productIsManufactured && productLeadTimeDays is int days
            ? new ResolvedLeadTime(days, LeadTimeSource.Product, "Product lead time")
            : new ResolvedLeadTime(SystemManufacturingDays, LeadTimeSource.SystemDefault, "1 day per BOM level");

    /// <summary>BR-C3-04/05: MANUFACTURING and MFG_BUFFER only on a MANUFACTURE route, SHIPPING only with SHIP; the rest always.</summary>
    public static bool IsVisible(string code, bool manufactureRoute, bool requiresShipping) => code switch
    {
        LeadTimeComponentCode.Manufacturing or LeadTimeComponentCode.MfgBuffer => manufactureRoute,
        LeadTimeComponentCode.Shipping                                         => requiresShipping,
        _                                                                      => true
    };

    /// <summary>Visible and counted: the supplier lead on a MANUFACTURE route is shown but not counted (the BOM covers materials).</summary>
    public static bool IsIncludedInTotal(string code, bool manufactureRoute, bool requiresShipping) =>
        IsVisible(code, manufactureRoute, requiresShipping)
        && !(manufactureRoute && code == LeadTimeComponentCode.Supplier);

    public static string Name(string code) => code switch
    {
        LeadTimeComponentCode.Supplier      => "Supplier lead time",
        LeadTimeComponentCode.Manufacturing => "Manufacturing",
        LeadTimeComponentCode.MfgBuffer     => "Manufacturing buffer",
        LeadTimeComponentCode.Qc            => "Quality inspection",
        LeadTimeComponentCode.Transfer      => "Internal transfer",
        LeadTimeComponentCode.PickPack      => "Pick & pack",
        LeadTimeComponentCode.Shipping      => "Shipping",
        LeadTimeComponentCode.SalesBuffer   => "Sales safety buffer",
        _                                   => code
    };

    /// <summary>The request property that edits the component (camelCase, as the API spells it).</summary>
    public static string Field(string code) => code switch
    {
        LeadTimeComponentCode.Supplier      => "supplierLeadTimeDays",
        LeadTimeComponentCode.Manufacturing => "manufacturingLeadTimeDays",
        LeadTimeComponentCode.MfgBuffer     => "manufacturingBufferDays",
        LeadTimeComponentCode.Qc            => "qualityInspectionDays",
        LeadTimeComponentCode.Transfer      => "internalTransferDays",
        LeadTimeComponentCode.PickPack      => "pickPackDays",
        LeadTimeComponentCode.Shipping      => "shippingLeadTimeDays",
        LeadTimeComponentCode.SalesBuffer   => "salesBufferDays",
        _                                   => code
    };
}
