using System.Security.Cryptography;
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

    /// <summary>The same ceiling the upload endpoint applies.</summary>
    internal const int MaxGeneratedBytes = 20 * 1024 * 1024;

    public async Task<StoredAttachment> StoreGeneratedAsync(GeneratedAttachmentRequest req, int uploadedBy)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (string.IsNullOrWhiteSpace(req.InterfaceCode))
            throw new BadRequestException("InterfaceCode is required.");
        if (req.InterfaceCode.Length > 30)
            throw new BadRequestException("InterfaceCode is longer than 30 characters.");
        if (req.DocumentId == Guid.Empty)
            throw new BadRequestException("DocumentId is required.");

        var fileName = SafeFileName(req.FileName);

        // Only PDFs, and only if the bytes really are one: this is served back from our own origin,
        // so a file that claims to be a PDF and is a web page is script running in a user's session.
        if (!string.Equals(req.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("Only application/pdf documents can be filed this way.");
        if (req.Content is null || req.Content.Length == 0)
            throw new BadRequestException("The document is empty.");
        if (req.Content.Length > MaxGeneratedBytes)
            throw new BadRequestException("The document is larger than 20 MB.");
        if (!req.Content.AsSpan().StartsWith("%PDF-"u8))
            throw new BadRequestException("The document is not a PDF.");
        if (req.Notes is { Length: > 300 })
            throw new BadRequestException("Notes are longer than 300 characters.");
        if (req.RequiredPermission is { Length: > 100 })
            throw new BadRequestException("RequiredPermission is longer than 100 characters.");

        var sha256 = Convert.ToHexString(SHA256.HashData(req.Content)).ToLowerInvariant();

        var existing = await (
            from attachment in _db.DocumentAttachments
            join file in _db.DocumentAttachmentContents on attachment.Id equals file.DocumentAttachmentId
            where attachment.InterfaceCode == req.InterfaceCode
               && attachment.DocumentId    == req.DocumentId
               && !attachment.IsDelete
               && file.Sha256 == sha256
            select attachment.UUID).FirstOrDefaultAsync();

        if (existing != Guid.Empty)
            return new StoredAttachment(existing, AlreadyStored: true);

        var uuid = Guid.NewGuid();
        var entity = new DocumentAttachment
        {
            UUID          = uuid,
            InterfaceCode = req.InterfaceCode,
            DocumentId    = req.DocumentId,
            FileName      = fileName,
            FileUrl       = $"/api/attachments/{uuid}/content",
            FileSize      = req.Content.Length,
            ContentType   = "application/pdf",
            Notes         = req.Notes,
            UploadedBy    = uploadedBy,
            UploadedDate  = DateTime.UtcNow
        };

        _db.DocumentAttachments.Add(entity);
        _db.DocumentAttachmentContents.Add(new DocumentAttachmentContent
        {
            DocumentAttachment = entity,
            Content            = req.Content,
            Sha256             = sha256,
            RequiredPermission = req.RequiredPermission
        });

        await _db.SaveChangesAsync();
        return new StoredAttachment(uuid, AlreadyStored: false);
    }

    public async Task<AttachmentContent?> GetContentAsync(Guid uuid) =>
        await (
            from attachment in _db.DocumentAttachments
            join file in _db.DocumentAttachmentContents on attachment.Id equals file.DocumentAttachmentId
            where attachment.UUID == uuid && !attachment.IsDelete
            select new AttachmentContent(
                file.Content, attachment.FileName, attachment.ContentType ?? "application/octet-stream", file.RequiredPermission))
        .FirstOrDefaultAsync();

    /// <summary>Built by the filing module, but still made safe to put in a header and on a disk.</summary>
    private static string SafeFileName(string? name)
    {
        var leaf = Path.GetFileName((name ?? string.Empty).Trim());
        var cleaned = new string(leaf.Where(c => !char.IsControl(c) && Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0).ToArray());

        if (string.IsNullOrWhiteSpace(cleaned))
            throw new BadRequestException("FileName is required.");

        return cleaned.Length <= 255 ? cleaned : cleaned[..255];
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