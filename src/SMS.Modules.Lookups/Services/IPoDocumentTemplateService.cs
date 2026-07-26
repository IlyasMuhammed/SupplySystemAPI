using SMS.Modules.Lookups.Models;

namespace SMS.Modules.Lookups.Services;

// Public (not internal) so SMS.Modules.Demand can consume it directly via DI for PO PDF
// generation without needing InternalsVisibleTo — same reasoning as IUserLookupService in
// SMS.Shared.Common. SMS.Modules.Demand -> SMS.Modules.Lookups is a safe reference direction
// (Lookups has no dependency back on Demand), unlike Suppliers -> Demand which is circular.
public interface IPoDocumentTemplateService
{
    Task<PoDocumentTemplateModel?> GetActiveAsync();
    Task<Guid> UpsertAsync(UpsertPoDocumentTemplateRequest req, int userId);
    IReadOnlyList<PoDocumentTokenModel> GetAvailableTokens();
}