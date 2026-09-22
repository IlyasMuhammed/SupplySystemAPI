namespace SMS.WorkflowEngine.Models;

public class CreateAttachmentRequest
{
    public string InterfaceCode { get; set; } = string.Empty;
    public Guid   DocumentId    { get; set; }
    public string FileName      { get; set; } = string.Empty;
    public string FileUrl       { get; set; } = string.Empty;
    public long?  FileSize      { get; set; }
    public string? ContentType  { get; set; }
    public string? Notes        { get; set; }
}

/// <summary>A document the system generated and wants filed on another document.</summary>
public class GeneratedAttachmentRequest
{
    /// <summary>The document it is filed on, e.g. <c>SALES_INVOICE</c> or <c>DELIVERY</c> — the same code the attachment panel is opened with.</summary>
    public string InterfaceCode { get; set; } = string.Empty;
    public Guid   DocumentId    { get; set; }
    public string FileName      { get; set; } = string.Empty;
    /// <summary>Only <c>application/pdf</c> is accepted, and the bytes must be a PDF.</summary>
    public string ContentType   { get; set; } = string.Empty;
    public byte[] Content       { get; set; } = [];
    public string? Notes        { get; set; }

    /// <summary>
    /// The permission needed to read the file back — the filing module names it, because this service
    /// cannot know what an invoice or a delivery should require. Leave <c>null</c> only for a file
    /// any signed-in user of the organization may read.
    /// </summary>
    public string? RequiredPermission { get; set; }
}

/// <param name="AlreadyStored">True when the same bytes were already filed on this document and no second copy was made.</param>
public sealed record StoredAttachment(Guid Uuid, bool AlreadyStored);

/// <summary>A filed document's bytes and how to serve them.</summary>
public sealed record AttachmentContent(byte[] Content, string FileName, string ContentType, string? RequiredPermission);

public class AttachmentModel
{
    public Guid     UUID          { get; set; }
    public string   InterfaceCode { get; set; } = string.Empty;
    public Guid     DocumentId    { get; set; }
    public string   FileName      { get; set; } = string.Empty;
    public string   FileUrl       { get; set; } = string.Empty;
    public long?    FileSize      { get; set; }
    public string?  ContentType   { get; set; }
    public string?  Notes         { get; set; }
    public int      UploadedBy    { get; set; }
    public string   UploadedByName { get; set; } = string.Empty;
    public DateTime UploadedDate  { get; set; }
}