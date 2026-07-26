using SMS.WorkflowEngine.Models;

namespace SMS.WorkflowEngine.Services;

public interface IAttachmentService
{
    Task<Guid> CreateAsync(CreateAttachmentRequest req, int uploadedBy);
    Task<List<AttachmentModel>> GetByDocumentAsync(string interfaceCode, Guid documentId);
    Task DeleteAsync(Guid uuid, int deletedBy);
    Task<Dictionary<Guid, int>> GetCountsByDocumentIdsAsync(string interfaceCode, IEnumerable<Guid> documentIds);
}