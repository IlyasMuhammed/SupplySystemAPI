using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Domain;

// P1-03 (Addendum 29 §1.2/§1.3) — renamed from Supplier. Deferred out of P1-01 deliberately: this
// class is `internal`, so the rename is contained entirely to this module and its test project —
// nothing outside SMS.Modules.Suppliers can reference it directly (see the module's public
// surface: ISuppliersService/ISuppliersRepository expose only DTOs, never this type).
internal class BusinessPartner : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid OrganizationId { get; set; }

    // ── Identification ────────────────────────────────────────────────────────
    public string SupplierName { get; set; } = string.Empty;
    public string SupplierCode { get; set; } = string.Empty;
    public string? RegistrationNo { get; set; }
    public string? TaxId { get; set; }

    // ── Address ───────────────────────────────────────────────────────────────
    public string? Country { get; set; }
    public string? ProvinceState { get; set; }
    public string? City { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? PostalCode { get; set; }

    // ── Contact ───────────────────────────────────────────────────────────────
    public string? Phone { get; set; }
    public string? Fax { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }

    // ── Primary contact (inline) ──────────────────────────────────────────────
    public string? PrimaryContactName { get; set; }
    public string? PrimaryContactTitle { get; set; }
    public string? PrimaryContactPhone { get; set; }
    public string? PrimaryContactEmail { get; set; }

    // ── Preferences (optional) ────────────────────────────────────────────────
    public Guid? PreferredPaymentTerms { get; set; }
    public Guid? PreferredCurrency { get; set; }

    // ── Financial ─────────────────────────────────────────────────────────────
    public decimal? CreditLimit { get; set; }
    public int? LeadTimeDays { get; set; }

    // ── Partner type (P1-02, Addendum 29 §1.2/§1.3) ───────────────────────────
    // PartnerType is derived from the four flags below, not an independent choice — kept as its
    // own column because it is what every existing query/report/UI reads, and recomputing it on
    // every read would be slower than recomputing it on the rare write. Every row created through
    // the legacy Suppliers path defaults to VENDOR/is_vendor=1 (column defaults, not app code),
    // which is also how the P1-01 backfill for pre-existing rows is satisfied — SQL Server applies
    // a column's DEFAULT to existing rows when the column is added as NOT NULL.
    public string PartnerType       { get; set; } = "VENDOR";
    public bool   IsVendor          { get; set; } = true;
    public bool   IsCustomer        { get; set; }
    public bool   IsCarrier         { get; set; }
    public bool   IsServiceProvider { get; set; }
    public string? VehicleTypes      { get; set; }
    public string? ServiceCategories { get; set; }

    // ── Status & workflow ─────────────────────────────────────────────────────
    public string Status { get; set; } = "PENDING";
    public decimal? Rating { get; set; }
    public DateTime? OnboardingDate { get; set; }
    public DateTime? LastReviewDate { get; set; }
    public int? ApprovedBy { get; set; }
    public string? RejectedReason { get; set; }
    public string? BlacklistedReason { get; set; }
    public string? SuspendedReason { get; set; }
    public DateTime? SuspendedReviewDate { get; set; }
    public int? StatusChangedBy { get; set; }
    public DateTime? StatusChangedAt { get; set; }

    // ── Misc ──────────────────────────────────────────────────────────────────
    public string? Notes { get; set; }
    public bool IsPreferredSupplier { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDelete { get; set; }

    // ── Audit ─────────────────────────────────────────────────────────────────
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }

    // ── Navigations ───────────────────────────────────────────────────────────
    public ICollection<SupplierTypeMapping> TypeMappings { get; set; } = new List<SupplierTypeMapping>();
    public ICollection<SupplierIndustryMapping> IndustryMappings { get; set; } = new List<SupplierIndustryMapping>();
    public ICollection<SupplierContact> Contacts { get; set; } = new List<SupplierContact>();
    public ICollection<SupplierDocument> Documents { get; set; } = new List<SupplierDocument>();
    public SupplierBankDetail? BankDetail { get; set; }
}

internal class SupplierTypeMapping : ITenantScopedEntity
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public Guid LookupValueId { get; set; }
    public bool IsPrimary { get; set; }
    public int AssignedBy { get; set; }
    public DateTime AssignedAt { get; set; }
    public string? Notes { get; set; }
    public Guid OrganizationId { get; set; }

    public BusinessPartner Supplier { get; set; } = null!;
}

internal class SupplierIndustryMapping : ITenantScopedEntity
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public Guid LookupValueId { get; set; }
    public bool IsPrimary { get; set; }
    public int AssignedBy { get; set; }
    public DateTime AssignedAt { get; set; }
    public string? Notes { get; set; }
    public Guid OrganizationId { get; set; }

    public BusinessPartner Supplier { get; set; } = null!;
}

internal class SupplierContact : ITenantScopedEntity
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string ContactName { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid OrganizationId { get; set; }

    public BusinessPartner Supplier { get; set; } = null!;
}

internal class SupplierDocument : ITenantScopedEntity
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
    public string? DocumentType { get; set; }
    public DateTime UploadedAt { get; set; }
    public int UploadedBy { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid OrganizationId { get; set; }

    public BusinessPartner Supplier { get; set; } = null!;
}

internal class SupplierBankDetail : ITenantScopedEntity
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string? BankName { get; set; }
    public string? BankAccountNo { get; set; }   // AES-256 encrypted
    public string? BankIban { get; set; }         // AES-256 encrypted
    public string? BankSwift { get; set; }        // AES-256 encrypted
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public int? UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid OrganizationId { get; set; }

    public BusinessPartner Supplier { get; set; } = null!;
}

// ── Legacy-looking entities (verified live — full CRUD via SuppliersController) ─
// Per-org taxonomy (an org's own supplier-type/category list), unlike Lookups' Currency/
// PaymentTerm/etc which are objective global facts — so these ARE tenant-scoped.

internal class SupplierType : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public Guid OrganizationId { get; set; }
}

internal class SupplierCategory : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid OrganizationId { get; set; }
}
