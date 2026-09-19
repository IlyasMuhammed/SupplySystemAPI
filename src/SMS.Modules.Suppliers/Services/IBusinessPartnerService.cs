using SMS.Modules.Suppliers.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Suppliers.Services;

// Public, unlike IBusinessPartnerRepository — this one is consumed directly by the (necessarily
// public) PartnersController constructors, matching ISuppliersService's own visibility for the
// same reason.
public interface IBusinessPartnerService
{
    Task<Guid> CreateAsync(BusinessPartnerModel model, int createdBy);
    Task<BusinessPartnerModel?> GetByIdAsync(Guid uuid);
    Task<bool> UpdateAsync(Guid uuid, BusinessPartnerModel model, int modifiedBy);
    Task<bool> DeleteAsync(Guid uuid, int deletedBy);
    Task<PaginatedResponse<BusinessPartnerModel>> GetAllAsync(BusinessPartnerFilter filter);
    Task<List<BusinessPartnerModel>> GetVendorsAsync();
    Task<List<BusinessPartnerModel>> GetCustomersAsync();
    Task<List<BusinessPartnerModel>> GetCarriersAsync();
    Task<List<BusinessPartnerModel>> GetServiceProvidersAsync();
}
