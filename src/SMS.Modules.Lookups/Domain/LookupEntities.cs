using SMS.Shared.Common;

namespace SMS.Modules.Lookups.Domain;

internal class City
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid CountryId { get; set; }
    public bool IsActive { get; set; }
}

internal class Country
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public bool IsActive { get; set; }
}

internal class Currency
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Symbol { get; set; }
}

internal class DeliveryTerm
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

internal class PaymentTerm
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? Days { get; set; }
}

internal class LookupType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }

    public ICollection<LookupValue> Values { get; set; } = new List<LookupValue>();
}

// Either a global reference row (IsGlobal=true, OrganizationId=null — the seeded catalog, e.g.
// currencies/UOMs) or a tenant-owned custom row (IsGlobal=false, OrganizationId set). LookupType
// itself stays global/shared in all cases — the distinguishing signal is per-row provenance, not
// which LookupType a value hangs off.
internal class LookupValue : IGloballyExemptTenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid TypeId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public bool IsGlobal { get; set; } = true;
    public Guid? OrganizationId { get; set; }

    public LookupType Type { get; set; } = null!;
}

// Single-row (per IsActive=true) configurable letterhead/T&C/signature template used to render
// the Purchase Order PDF. Lives in Lookups (small admin-configurable master data, same home as
// Countries/Currencies/Payment Terms) rather than Demand, since Demand -> Lookups is a safe
// reference direction (Lookups has no dependency back on Demand), avoiding a circular reference.
internal class PoDocumentTemplate : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string? CompanyName { get; set; }
    public string? CompanyAddress { get; set; }
    public string? CompanyLogoUrl { get; set; }
    public string? CompanyTaxId { get; set; }
    public string? CompanyPhone { get; set; }
    public string? CompanyEmail { get; set; }
    // Mail-merge style letter body — free text authored via the rich-text editor, with
    // {{Token}} placeholders (e.g. {{SupplierName}}, {{PoNumber}}) resolved against live PO data,
    // plus two special markers, {{LineItemsTable}} and {{SignatureBlock}}, which the renderer
    // replaces with the actual PO lines table / signature composition rather than text.
    public string? BodyHtml { get; set; }
    public bool ShowSignatureBlock { get; set; } = true;
    public string? SignatureDisclaimer { get; set; }
    public string? PreparedByLabel { get; set; }
    public string? ApprovedByLabel { get; set; }
    public string? AuthorizedSignatoryLabel { get; set; }
    public string? FooterText { get; set; }
    public bool IsActive { get; set; } = true;
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
