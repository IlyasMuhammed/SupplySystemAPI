namespace SMS.Shared.Common;

// REQ-4.x — shared interface so SMS.Modules.Warehouse can read the per-organization
// acknowledgment-link expiry setting without referencing SMS.Modules.Tenancy directly.
// The implementation lives in SMS.Modules.Tenancy and is resolved via the DI container —
// same pattern as ISupplierContactLookupService/IOrganizationStatusService.
public interface IOrganizationSettingsService
{
    // Always returns a value within [AckLinkExpiryMinDays, AckLinkExpiryMaxDays] — falls back
    // to AckLinkExpiryDefaultDays when the organization has no override or a stored value is
    // somehow out of range.
    Task<int> GetAckLinkExpiryDaysAsync(Guid organizationId);

    // Validates days is within range before upserting. Returns false (no throw) on an
    // out-of-range value so the caller can translate it into a 400 response.
    Task<bool> UpdateAckLinkExpiryDaysAsync(Guid organizationId, int days, int modifiedBy);
}
