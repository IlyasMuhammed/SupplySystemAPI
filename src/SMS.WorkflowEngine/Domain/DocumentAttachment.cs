namespace SMS.WorkflowEngine.Domain;

/// <summary>
/// Generic, document-agnostic file attachment — keyed by InterfaceCode + DocumentId, same
/// convention as DocumentTimeline. The actual file bytes live in SMS.FileStore blob storage;
/// this row only tracks metadata + the URL returned by POST /api/files/upload.
/// </summary>
internal class DocumentAttachment
{
    public int      Id            { get; set; }
    public Guid     UUID          { get; set; }

    /// <summary>Interface code of the owning document/line, e.g. "PR_LINE", "GRN", "SUPPLIER_QUOTATION".</summary>
    public string   InterfaceCode { get; set; } = string.Empty;
    public Guid     DocumentId    { get; set; }

    public string   FileName      { get; set; } = string.Empty;
    public string   FileUrl       { get; set; } = string.Empty;
    public long?    FileSize      { get; set; }
    public string?  ContentType   { get; set; }
    public string?  Notes         { get; set; }

    public bool     IsDelete      { get; set; }
    public int      UploadedBy    { get; set; }
    public DateTime UploadedDate  { get; set; }
}