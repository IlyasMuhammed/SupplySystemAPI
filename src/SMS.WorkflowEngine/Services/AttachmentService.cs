using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Files;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Domain;
using SMS.WorkflowEngine.Models;

namespace SMS.WorkflowEngine.Services;

/// <summary>
/// Every read here is limited to the caller's own organization explicitly, as well as through the tenant
/// filter: a super admin bypasses the filter, and an attachment is reached by nothing more than a GUID, so
/// without this one organization's files could be listed, opened or removed while acting in another.
/// </summary>
internal sealed class AttachmentService : IAttachmentService
{
    /// <summary>Why a document the system filed cannot be removed — the same words wherever it is refused.</summary>
    internal const string FiledDocumentCannotBeRemoved =
        "This document was generated and filed by the system, so it cannot be removed. It stays on file as the record of what was issued.";

    private readonly WorkflowDbContext _db;
    private readonly IUserQueryService _userQuery;

    public AttachmentService(WorkflowDbContext db, IUserQueryService userQuery)
    {
        _db        = db;
        _userQuery = userQuery;
    }

    private IQueryable<DocumentAttachment> OwnAttachments()
    {
        var org = _db.TenantContext.OrganizationId;
        return _db.DocumentAttachments.Where(a => a.OrganizationId == org);
    }

    private IQueryable<DocumentAttachmentContent> OwnContents()
    {
        var org = _db.TenantContext.OrganizationId;
        return _db.DocumentAttachmentContents.Where(c => c.OrganizationId == org);
    }

    /// <summary>Nothing is kept under a code with no access rule: nobody could ever list it.</summary>
    private static void RequireKnownKind(string interfaceCode)
    {
        if (AttachmentAccessPolicy.Find(interfaceCode) is null)
            throw new BadRequestException("Attachments cannot be kept on this kind of document.");
    }

    public async Task<Guid> CreateAsync(CreateAttachmentRequest req, int uploadedBy)
    {
        if (string.IsNullOrWhiteSpace(req.InterfaceCode))
            throw new BadRequestException("InterfaceCode is required.");
        RequireKnownKind(req.InterfaceCode);
        if (req.DocumentId == Guid.Empty)
            throw new BadRequestException("DocumentId is required.");
        if (string.IsNullOrWhiteSpace(req.FileUrl))
            throw new BadRequestException("FileUrl is required.");

        // The endpoints check these before writing the file; checked again here for every other caller,
        // so an over-long value is a clear 400 rather than SQL Server's truncation error.
        var fileName = UploadRules.DisplayName(req.FileName)
            ?? throw new BadRequestException("FileName is required.");
        if (req.ContentType is { Length: > MaxContentTypeLength })
            throw new BadRequestException("The file's content type is not recognised.");
        if (req.Notes is { Length: > MaxNotesLength })
            throw new BadRequestException($"Notes must not exceed {MaxNotesLength} characters.");

        var entity = new DocumentAttachment
        {
            UUID          = Guid.NewGuid(),
            InterfaceCode = req.InterfaceCode,
            DocumentId    = req.DocumentId,
            FileName      = fileName,
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

    // The columns of workflow_schema.document_attachments (DocumentAttachmentMap). Over-long values used to
    // reach SQL Server and come back as a 500 naming the table and column, with the file already on disk.
    internal const int MaxContentTypeLength = 100;
    internal const int MaxNotesLength       = 300;

    /// <summary>The same ceiling the upload endpoint applies.</summary>
    internal const int MaxGeneratedBytes = (int)UploadRules.MaxFileBytes;

    public async Task<StoredAttachment> StoreGeneratedAsync(GeneratedAttachmentRequest req, int uploadedBy)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (string.IsNullOrWhiteSpace(req.InterfaceCode))
            throw new BadRequestException("InterfaceCode is required.");
        if (req.InterfaceCode.Length > 30)
            throw new BadRequestException("InterfaceCode is longer than 30 characters.");
        RequireKnownKind(req.InterfaceCode);
        if (req.DocumentId == Guid.Empty)
            throw new BadRequestException("DocumentId is required.");

        var fileName = UploadRules.DisplayName(req.FileName)
            ?? throw new BadRequestException("FileName is required.");

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

        // Own organization only: a filing done in a super admin's context must never be answered with
        // another organization's copy of the same bytes.
        var existing = await (
            from attachment in OwnAttachments()
            join file in OwnContents() on attachment.Id equals file.DocumentAttachmentId
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
            from attachment in OwnAttachments()
            join file in OwnContents() on attachment.Id equals file.DocumentAttachmentId
            where attachment.UUID == uuid && !attachment.IsDelete
            select new AttachmentContent(
                file.Content, attachment.FileName, attachment.ContentType ?? "application/octet-stream", file.RequiredPermission,
                attachment.InterfaceCode))
        .FirstOrDefaultAsync();

    public async Task<List<AttachmentModel>> GetByDocumentAsync(string interfaceCode, Guid documentId)
    {
        var contents = OwnContents();
        var rows = await OwnAttachments()
            .Where(a => a.InterfaceCode == interfaceCode && a.DocumentId == documentId && !a.IsDelete)
            .OrderByDescending(a => a.UploadedDate)
            .Select(a => new { Attachment = a, IsGenerated = contents.Any(c => c.DocumentAttachmentId == a.Id) })
            .ToListAsync();

        var names = (await _userQuery.GetUsersAsync(rows.Select(r => r.Attachment.UploadedBy).Distinct().ToList()))
            .ToDictionary(u => u.UserId, u => u.DisplayName);

        return rows.Select(r => ToModel(r.Attachment, r.IsGenerated, names.GetValueOrDefault(r.Attachment.UploadedBy, $"User #{r.Attachment.UploadedBy}"))).ToList();
    }

    public async Task<AttachmentModel?> FindAsync(Guid uuid)
    {
        var contents = OwnContents();
        var row = await OwnAttachments()
            .Where(a => a.UUID == uuid && !a.IsDelete)
            .Select(a => new { Attachment = a, IsGenerated = contents.Any(c => c.DocumentAttachmentId == a.Id) })
            .FirstOrDefaultAsync();

        return row is null ? null : ToModel(row.Attachment, row.IsGenerated, uploadedByName: string.Empty);
    }

    private static AttachmentModel ToModel(DocumentAttachment a, bool isGenerated, string uploadedByName) => new()
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
        UploadedByName = uploadedByName,
        UploadedDate   = a.UploadedDate,
        IsGenerated    = isGenerated
    };

    /// <summary>
    /// Removes an uploaded file from its document. A document the system generated and filed is refused
    /// (<see cref="ConflictException"/>) whoever asks: it is the record of what was issued — an invoice as the
    /// customer was sent it, the gate pass the goods left on — and filing it again would not bring back the
    /// copy that was removed.
    /// </summary>
    public async Task DeleteAsync(Guid uuid, int deletedBy)
    {
        var entity = await OwnAttachments()
            .FirstOrDefaultAsync(a => a.UUID == uuid && !a.IsDelete)
            ?? throw new NotFoundException("Attachment", uuid);

        if (await OwnContents().AnyAsync(c => c.DocumentAttachmentId == entity.Id))
            throw new ConflictException(FiledDocumentCannotBeRemoved);

        entity.IsDelete = true;
        await _db.SaveChangesAsync();
    }

    public async Task<Dictionary<Guid, int>> GetCountsByDocumentIdsAsync(string interfaceCode, IEnumerable<Guid> documentIds)
    {
        var ids = documentIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, int>();

        return await OwnAttachments()
            .Where(a => a.InterfaceCode == interfaceCode && !a.IsDelete && ids.Contains(a.DocumentId))
            .GroupBy(a => a.DocumentId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }
}
