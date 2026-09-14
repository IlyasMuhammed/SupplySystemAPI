namespace SMS.Shared.Common;

// RC-001 — shared interface so SMS.Modules.Inventory can resolve an organization's default
// currency (for a VariantSupplier rate card's `currency` field) without a project reference to
// SMS.Modules.Tenancy. The implementation lives in SMS.Modules.Tenancy and is resolved via the DI
// container — same pattern as IOrganizationStatusService.
public interface IOrganizationCurrencyService
{
    /// <summary>Null if the organization has never configured a base currency.</summary>
    Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId);
}
