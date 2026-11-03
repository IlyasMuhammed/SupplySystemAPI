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

    /// <summary>A49 — an upload kept in the database, not on disk (<see cref="AttachmentAccessPolicy.KeepsUploadsPrivate"/>). The caller has validated the file.</summary>
    Task<Guid> StorePrivateUploadAsync(CreateAttachmentRequest req, byte[] content, int uploadedBy) => throw new NotSupportedException();

    /// <summary>A49 — erases a private upload's bytes and retires its row. Only for a kind that <see cref="AttachmentAccessPolicy.KeepsUploadsPrivate"/>; the caller has checked that nothing holds it.</summary>
    Task RemovePrivateUploadAsync(Guid uuid) => throw new NotSupportedException();

    /// <summary>The bytes of a generated attachment, or <c>null</c> if there is none (unknown, deleted, or an uploaded file kept elsewhere).</summary>
    Task<AttachmentContent?> GetContentAsync(Guid uuid);
    Task<List<AttachmentModel>> GetByDocumentAsync(string interfaceCode, Guid documentId);
    Task<AttachmentModel?> FindAsync(Guid uuid);
    Task DeleteAsync(Guid uuid, int deletedBy);
    Task<Dictionary<Guid, int>> GetCountsByDocumentIdsAsync(string interfaceCode, IEnumerable<Guid> documentIds);
}