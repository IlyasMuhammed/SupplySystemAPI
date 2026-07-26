using Microsoft.EntityFrameworkCore;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Models;

namespace SMS.WorkflowEngine.Services;

internal sealed class AttachmentService : IAttachmentService
{
    private readonly WorkflowDbContext _db;
    private readonly IUserQueryService _userQuery;

    public AttachmentService(WorkflowDbContext db, IUserQueryService userQuery)
    {
        _db        = db;
        _userQuery = userQuery;
    }

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
        var entities = await _db.DocumentAttachments
            .Where(a => a.InterfaceCode == interfaceCode && a.DocumentId == documentId && !a.IsDelete)
            .OrderByDescending(a => a.UploadedDate)
            .ToListAsync();

        var names = (await _userQuery.GetUsersAsync(entities.Select(a => a.UploadedBy).Distinct().ToList()))
            .ToDictionary(u => u.UserId, u => u.DisplayName);

        return entities.Select(a => new AttachmentModel
        {
            UUID           = a.UUID,
            InterfaceCode  = a.InterfaceCode,
            DocumentId     = a.DocumentId,
            FileName       = a.FileName,
            FileUrl        = a.FileUrl,
            FileSize       = a.FileSize,
            ContentType    = a.ContentType,
            Notes          = a.Notes,
            UploadedBy     = a.UploadedBy,
            UploadedByName = names.GetValueOrDefault(a.UploadedBy, $"User #{a.UploadedBy}"),
            UploadedDate   = a.UploadedDate
        }).ToList();
    }

    public async Task DeleteAsync(Guid uuid, int deletedBy)
    {
        var entity = await _db.DocumentAttachments
            .FirstOrDefaultAsync(a => a.UUID == uuid && !a.IsDelete)
            ?? throw new NotFoundException("Attachment", uuid);

        entity.IsDelete = true;
        await _db.SaveChangesAsync();
    }

    public async Task<Dictionary<Guid, int>> GetCountsByDocumentIdsAsync(string interfaceCode, IEnumerable<Guid> documentIds)
    {
        var ids = documentIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, int>();

        return await _db.DocumentAttachments
            .Where(a => a.InterfaceCode == interfaceCode && !a.IsDelete && ids.Contains(a.DocumentId))
            .GroupBy(a => a.DocumentId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }
}