namespace SMS.Modules.Suppliers.Models;

// P1-03 (Addendum 29 §1.2/§1.3). A separate, new model rather than an addition to
// SupplierDetailModel — that one is the legacy vendor-only CRUD surface (kept working verbatim,
// per §1.7's backward-compatibility requirement); this is the new partner-facing shape the P1-05
// API controller will expose. AutoMapper (BusinessPartnerMappingProfile) maps the entity's
// SupplierName/SupplierCode onto CompanyName/PartnerCode here.
public class BusinessPartnerModel
{
    public Guid Uuid { get; set; }
    public string PartnerCode { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string PartnerType { get; set; } = string.Empty;
    public bool IsVendor { get; set; }
    public bool IsCustomer { get; set; }
    public bool IsCarrier { get; set; }
    public bool IsServiceProvider { get; set; }
    public string? VehicleTypes { get; set; }
    public string? ServiceCategories { get; set; }
    public bool IsActive { get; set; }
}

// P1-05 (Addendum 29 §1.6) — GET /api/partners' filter set. Mirrors SupplierListFilter's shape
// (Search/Page/PageSize) rather than inventing a different pagination convention.
public class BusinessPartnerFilter
{
    /// <summary>A PartnerType code (e.g. "VENDOR", "VENDOR_CARRIER") — exact match, not a flag.</summary>
    public string? Type { get; set; }
    public bool? IsVendor { get; set; }
    public bool? IsCustomer { get; set; }
    public bool? IsCarrier { get; set; }
    public bool? IsServiceProvider { get; set; }
    public bool? Active { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
