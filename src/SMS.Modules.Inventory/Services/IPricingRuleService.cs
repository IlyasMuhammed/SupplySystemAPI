using SMS.Modules.Inventory.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Inventory.Services;

// A29-P2-04 — CRUD over inventory.PricingRules. Resolving which rule actually applies to a sale
// is IPricingService's job (SMS.Shared.Common), not this one.
public interface IPricingRuleService
{
    Task<Guid> CreateAsync(CreatePricingRuleRequest req, int createdBy);
    Task<bool> UpdateAsync(Guid uuid, UpdatePricingRuleRequest req);
    Task<bool> DeleteAsync(Guid uuid);
    Task<PricingRuleModel?> GetByIdAsync(Guid uuid);
    Task<PaginatedResponse<PricingRuleModel>> GetListAsync(PricingRuleListFilter filter);
}
