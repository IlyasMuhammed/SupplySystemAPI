using Microsoft.EntityFrameworkCore;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Models;

namespace SMS.WorkflowEngine.Services;

internal sealed class AttachmentService : IAttachmentService
{
    private readonly WorkflowDbContext _db;

    public AttachmentService(WorkflowDbContext db) => _db = db;

    public async Task<Guid> CreateAsync(CreateAttachmentRequest req, int uploadedBy)
    {
        if (string.IsNullOrWhiteSpace(req.InterfaceCode))
            throw new BadRequestException("InterfaceCode is required.");
        if (req.DocumentId == Guid.Empty)
            throw new BadRequestException("DocumentId is required.");
        if (string.IsNullOrWhiteSpace(req.FileUrl))
            throw new BadRequestException("FileUrl is required.");

        var entity = new DocumentAttachment
        {
            UUID          = Guid.NewGuid(),
            InterfaceCode = req.InterfaceCode,
            DocumentId    = req.DocumentId,
            FileName      = req.FileName,
            FileUrl       = req.FileUrl,
            FileSize      = req.FileSize,
            ContentType   = req.ContentType,
            Notes         = req.Notes,
            UploadedBy    = uploadedBy,
            UploadedDate  = DateTime.UtcNow
        };

        _db.DocumentAttachments.Add(entity);
        await _db.SaveChangesAsync();
        return entity.UUID;
    }

    public async Task<List<AttachmentModel>> GetByDocumentAsync(string interfaceCode, Guid documentId)
    {
        return await _db.DocumentAttachments
            .Where(a => a.InterfaceCode == interfaceCode && a.DocumentId == documentId && !a.IsDelete)
            .OrderByDescending(a => a.UploadedDate)
            .Select(a => new AttachmentModel
            {
                UUID          = a.UUID,
                InterfaceCode = a.InterfaceCode,
                DocumentId    = a.DocumentId,
                FileName      = a.FileName,
                FileUrl       = a.FileUrl,
                FileSize      = a.FileSize,
                ContentType   = a.ContentType,
                Notes         = a.Notes,
                UploadedBy    = a.UploadedBy,
                UploadedDate  = a.UploadedDate
            })
            .ToListAsync();
    }

    public async Task DeleteAsync(Guid uuid, int deletedBy)
    {
        var entity = await _db.DocumentAttachments
            .FirstOrDefaultAsync(a => a.UUID == uuid && !a.IsDelete)
            ?? throw new NotFoundException("Attachment", uuid);

        entity.IsDelete = true;
        await _db.SaveChangesAsync();
    }
}