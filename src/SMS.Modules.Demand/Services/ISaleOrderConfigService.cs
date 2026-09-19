using SMS.Modules.Demand.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Services;

// A29-P3-03 §3.2/§3.5. Org scoping comes from the tenant-scoped DbContext's query filter (same
// convention every other service in this addendum uses), not an explicit orgId parameter — the
// literal GetConfig(orgId)/UpdateConfig(orgId, dto) signatures would let a caller name a different
// org's id and read/write across tenants, which is exactly what the query filter exists to prevent.
public interface ISaleOrderConfigService
{
    Task<SaleOrderConfigModel> GetConfigAsync();
    Task<SaleOrderConfigModel> UpdateConfigAsync(UpdateSaleOrderConfigRequest req, int updatedBy);
    Task<PaginatedResponse<SaleOrderConfigAuditModel>> GetAuditAsync(SaleOrderConfigAuditFilter filter);
}
