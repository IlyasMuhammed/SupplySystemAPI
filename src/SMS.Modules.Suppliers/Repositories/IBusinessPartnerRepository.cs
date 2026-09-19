using SMS.Modules.Suppliers.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Repositories;

// P1-04 (Addendum 29 §1.3/§1.5). Deliberately narrow: this is the new partner-type-aware surface
// (create/update the flags, list by type, soft delete), not a re-implementation of the full vendor
// onboarding workflow (status state machine, contacts, bank details, documents) that
// ISuppliersRepository already owns and keeps owning — see the P1-03 task notes on why the new and
// legacy surfaces run in parallel until P1-06 addresses consolidation.
internal interface IBusinessPartnerRepository
{
    Task<Guid> CreateAsync(BusinessPartnerModel model, int createdBy);
    Task<BusinessPartnerModel?> GetByIdAsync(Guid uuid);
    Task<bool> UpdateAsync(Guid uuid, BusinessPartnerModel model, int modifiedBy);
    Task<bool> DeleteAsync(Guid uuid, int deletedBy);

    /// <summary>P1-05 — the general filtered/paginated list GET /api/partners itself reads from.</summary>
    Task<PaginatedResponse<BusinessPartnerModel>> GetAllAsync(BusinessPartnerFilter filter);

    // ── §1.3 filter methods — the four capability views the backward-compat aliases
    //    (GET /api/suppliers, /api/customers, /api/carriers, /api/service-providers) read from ──
    Task<List<BusinessPartnerModel>> GetVendorsAsync();
    Task<List<BusinessPartnerModel>> GetCustomersAsync();
    Task<List<BusinessPartnerModel>> GetCarriersAsync();
    Task<List<BusinessPartnerModel>> GetServiceProvidersAsync();
}
