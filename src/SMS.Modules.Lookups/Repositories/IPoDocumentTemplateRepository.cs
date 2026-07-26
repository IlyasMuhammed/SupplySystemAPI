using SMS.Modules.Lookups.Models;

namespace SMS.Modules.Lookups.Repositories;

internal interface IPoDocumentTemplateRepository
{
    Task<PoDocumentTemplateModel?> GetActiveAsync();
    Task<Guid> UpsertAsync(UpsertPoDocumentTemplateRequest req, int userId);
}