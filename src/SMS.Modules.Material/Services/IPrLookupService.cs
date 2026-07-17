using SMS.Modules.Material.Models;

namespace SMS.Modules.Material.Services;

public interface IPrLookupService
{
    Task<List<PrLineSearchResult>> SearchAsync(Guid productId, string status);
    Task<List<PrLineDisbursementModel>> GetDisbursementsAsync(Guid prId, Guid lineId);
}
