using SMS.WorkflowEngine.Models;

namespace SMS.WorkflowEngine.Services;

public interface IAttachmentService
{
    Task<Guid> CreateAsync(CreateAttachmentRequest req, int uploadedBy);

    /// <summary>
    /// Files a document the system generated (a PDF) on another document, keeping its bytes in the
    /// database rather than under <c>wwwroot</c>. The attachment's <c>FileUrl</c> is the authenticated
    /// <c>/api/attachments/{uuid}/content</c>, which also checks the request's
    /// <see cref="GeneratedAttachmentRequest.RequiredPermission"/>. Filing identical bytes on the same
    /// document again returns the attachment already there.
    /// </summary>
    Task<StoredAttachment> StoreGeneratedAsync(GeneratedAttachmentRequest req, int uploadedBy);

    /// <summary>The bytes of a generated attachment, or <c>null</c> if there is none (unknown, deleted, or an uploaded file kept elsewhere).</summary>
    Task<AttachmentContent?> GetContentAsync(Guid uuid);
    Task<List<AttachmentModel>> GetByDocumentAsync(string interfaceCode, Guid documentId);
    Task DeleteAsync(Guid uuid, int deletedBy);
    Task<Dictionary<Guid, int>> GetCountsByDocumentIdsAsync(string interfaceCode, IEnumerable<Guid> documentIds);
}