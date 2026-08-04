namespace SMS.Shared.Common;

// For the one entity (Lookups' LookupValue) that can be either a global reference row
// (IsGlobal=true, OrganizationId=null — seeded catalog values like currencies/UOMs) or a
// tenant-owned custom row (IsGlobal=false, OrganizationId set). Kept distinct from
// ITenantScopedEntity because OrganizationId is nullable here — a global row has no owner.
public interface IGloballyExemptTenantScopedEntity
{
    Guid? OrganizationId { get; set; }
    bool IsGlobal { get; set; }
}
