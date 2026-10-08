namespace SMS.Modules.Inventory.Models;

// A34 C3/C4 — lead-time defaults, a variant's lead-time components and the calculator endpoints
// (docs/route-classification/API-CONTRACT.md §4.4–§4.6).

/// <summary>
/// <c>GET/PUT api/lead-time/defaults</c> — the organization's defaults (D-10). <see cref="IsSaved"/> is false when the
/// organization has no row yet: the values are then the system defaults 1/3/1/0/0/0.
/// </summary>
public class LeadTimeDefaultsModel
{
    public int PickPackDays { get; set; }
    public int ShippingLeadTimeDays { get; set; }
    public int SalesBufferDays { get; set; }
    public int ManufacturingBufferDays { get; set; }
    public int QualityInspectionDays { get; set; }
    public int InternalTransferDays { get; set; }
    public bool IsSaved { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>Body of <c>PUT api/lead-time/defaults</c>. Every value is required, 0–365 days (BR-C3-01).</summary>
public class UpdateLeadTimeDefaultsRequest
{
    public int? PickPackDays { get; set; }
    public int? ShippingLeadTimeDays { get; set; }
    public int? SalesBufferDays { get; set; }
    public int? ManufacturingBufferDays { get; set; }
    public int? QualityInspectionDays { get; set; }
    public int? InternalTransferDays { get; set; }
}

/// <summary>
/// <c>GET/PUT api/variants/{uuid}/lead-times</c> — the variant's 8 lead-time components (D-26). Visibility follows the
/// variant's route, else the organization's SHIP default (BR-C3-04/05).
/// </summary>
public class VariantLeadTimesModel
{
    public Guid VariantUuid { get; set; }
    public int ProductId { get; set; }
    public string? Sku { get; set; }
    public string? VariantName { get; set; }

    public Guid? RouteUuid { get; set; }
    public string? RouteCode { get; set; }
    public string? RouteName { get; set; }
    /// <summary>The variant's route, else the org's SHIP default, else STOCK.</summary>
    public string RouteCategory { get; set; } = "STOCK";
    /// <summary>The variant has no route of its own.</summary>
    public bool RouteFromOrgDefault { get; set; }
    /// <summary>The judged route has SHIP; false when no route at all.</summary>
    public bool RequiresShipping { get; set; }

    /// <summary>Always all 8, in component-code order (SUPPLIER … SALES_BUFFER).</summary>
    public List<VariantLeadTimeComponentModel> Components { get; set; } = [];
    /// <summary>
    /// Sum of <c>resolvedDays</c> over the components with <c>includedInTotal</c> — the visible ones, except the supplier
    /// lead on a MANUFACTURE route (the calculator counts materials through the BOM there). No stock check, no BOM
    /// recursion: those are the calculator's.
    /// </summary>
    public int TotalDays { get; set; }
}

public class VariantLeadTimeComponentModel
{
    /// <summary>SUPPLIER, MANUFACTURING, MFG_BUFFER, QC, TRANSFER, PICK_PACK, SHIPPING, SALES_BUFFER.</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>The request property that edits it: supplierLeadTimeDays, manufacturingLeadTimeDays, …</summary>
    public string Field { get; set; } = string.Empty;
    /// <summary>The variant's own override; null = not set.</summary>
    public int? StoredDays { get; set; }
    /// <summary>What the calculator would use (stock-unaware).</summary>
    public int ResolvedDays { get; set; }
    /// <summary>VARIANT, ORG_DEFAULT, SUPPLIER_RATE, SUPPLIER_RECORD, PRODUCT, SYSTEM_DEFAULT.</summary>
    public string Source { get; set; } = string.Empty;
    public string? Detail { get; set; }
    /// <summary>BR-C3-04/05. Hidden components are still returned and still editable.</summary>
    public bool Visible { get; set; }
    /// <summary>Counted in <see cref="VariantLeadTimesModel.TotalDays"/>.</summary>
    public bool IncludedInTotal { get; set; }
    /// <summary>What would apply if <see cref="StoredDays"/> were null ("Reset to org defaults" preview). Never VARIANT.</summary>
    public int DefaultDays { get; set; }
    public string DefaultSource { get; set; } = string.Empty;
}

/// <summary>
/// Body of <c>PUT api/variants/{uuid}/lead-times</c> — all 8 overrides, each 0–3650 days or null ("use the default").
/// A full replacement: a null clears that override. <see cref="SupplierLeadTimeDays"/> is stored on the variant's
/// existing <c>LeadTimeDays</c> (D-11).
/// </summary>
public class UpdateVariantLeadTimesRequest
{
    public int? SupplierLeadTimeDays { get; set; }
    public int? ManufacturingLeadTimeDays { get; set; }
    public int? ManufacturingBufferDays { get; set; }
    public int? QualityInspectionDays { get; set; }
    public int? InternalTransferDays { get; set; }
    public int? PickPackDays { get; set; }
    public int? ShippingLeadTimeDays { get; set; }
    public int? SalesBufferDays { get; set; }
}

/// <summary>Body of <c>POST api/lead-time/calculate</c> and <c>…/calculate-manufacturing</c> (the latter ignores route and date).</summary>
public class LeadTimeCalculateRequest
{
    public Guid VariantUuid { get; set; }
    public decimal Quantity { get; set; }
    public Guid? RouteUuid { get; set; }
    /// <summary>Date-only.</summary>
    public DateTime? RequestedDate { get; set; }
}

/// <summary><c>POST api/lead-time/calculate-manufacturing</c> — one BOM level of the manufacturing lead time (§4.6).</summary>
public class ManufacturingLeadTimeNodeModel
{
    public Guid VariantUuid { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public int LevelDays { get; set; }
    /// <summary>VARIANT, PRODUCT or SYSTEM_DEFAULT.</summary>
    public string LevelDaysSource { get; set; } = string.Empty;
    public Guid? BomUuid { get; set; }
    public string? BomNumber { get; set; }
    public int? BomVersion { get; set; }
    /// <summary>LevelDays + the longest input wait.</summary>
    public int TotalDays { get; set; }
    public List<ManufacturingLeadTimeInputModel> Inputs { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public class ManufacturingLeadTimeInputModel
{
    public Guid VariantUuid { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public decimal RequiredQty { get; set; }
    /// <summary>Free stock in the parent's production warehouse (else the best single warehouse).</summary>
    public decimal FreeQty { get; set; }
    public decimal ShortfallQty { get; set; }
    public bool IsManufactured { get; set; }
    /// <summary>0 when <see cref="ShortfallQty"/> is 0.</summary>
    public int WaitDays { get; set; }
    /// <summary>IN_STOCK, BOM (recursed), SUPPLIER_RATE, SUPPLIER_RECORD, VARIANT, PRODUCT, SYSTEM_DEFAULT.</summary>
    public string Source { get; set; } = string.Empty;
    public string? Detail { get; set; }
    /// <summary>Only when the input was recursed into.</summary>
    public ManufacturingLeadTimeNodeModel? Node { get; set; }
}
