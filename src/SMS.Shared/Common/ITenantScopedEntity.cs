namespace SMS.Shared.Common;

// Implemented by every ordinary tenant-scoped entity. Drives both the auto-stamp-on-create
// (TenantScopingExtensions.StampTenantScopedEntities) and the global query filter
// (TenantScopingExtensions.ApplyTenantQueryFilters).
public interface ITenantScopedEntity
{
    Guid OrganizationId { get; set; }
}
