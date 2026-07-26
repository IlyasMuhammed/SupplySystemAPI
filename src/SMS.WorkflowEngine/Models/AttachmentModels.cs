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