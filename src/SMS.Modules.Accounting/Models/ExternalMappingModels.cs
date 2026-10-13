namespace SMS.Modules.Accounting.Models;

// A42 API-CONTRACT "External mappings" (BE2).

public sealed class ExternalMappingModel
{
    public Guid      Uuid                { get; set; }
    public string    SystemType          { get; set; } = string.Empty;
    public string    SystemName          { get; set; } = string.Empty;
    public MappingAccountModel Account   { get; set; } = new();
    public string    ExternalAccountCode { get; set; } = string.Empty;
    public string?   ExternalAccountName { get; set; }
    public string    SyncDirection       { get; set; } = string.Empty;
    public bool      IsActive            { get; set; }
    public DateTime? LastSyncedAt        { get; set; }
    public string?   SyncStatus          { get; set; }
    public string?   SyncErrorMessage    { get; set; }
    public string?   ConnectionConfigJson { get; set; }
}

public sealed class MappingAccountModel
{
    public Guid   Uuid        { get; set; }
    public string AccountCode { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
}

public sealed class CreateExternalMappingRequest
{
    public string? SystemType           { get; set; }
    public string? SystemName           { get; set; }
    public Guid    AccountUuid          { get; set; }
    public string? ExternalAccountCode  { get; set; }
    public string? ExternalAccountName  { get; set; }
    public string? SyncDirection        { get; set; }
    public string? ConnectionConfigJson { get; set; }
}

public sealed class UpdateExternalMappingRequest
{
    public string? SystemName           { get; set; }
    public string? ExternalAccountCode  { get; set; }
    public string? ExternalAccountName  { get; set; }
    public string? SyncDirection        { get; set; }
    public bool    IsActive             { get; set; } = true;
    /// <summary>Null = keep; "" = clear.</summary>
    public string? ConnectionConfigJson { get; set; }
}

public sealed class MappingImportResult
{
    public int Created { get; set; }
    public int Updated { get; set; }
    public List<MappingImportError> Errors { get; set; } = [];
}

public sealed class MappingImportError
{
    public int    Line    { get; set; }
    public string Message { get; set; } = string.Empty;
}
