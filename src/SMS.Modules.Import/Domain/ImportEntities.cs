using SMS.Shared.Common;

namespace SMS.Modules.Import.Domain;

// A40 — schema `import` (docs/data-import/ADDENDUM-40-ANALYSIS.md D-3, D-10). Profiles and their fields are a mirror of the
// registered IImportTarget.Describe() definitions, reconciled at every start (global rows, no organization); an
// organization overrides only duplicate strategy and limits (ImportProfileOverride). Sessions, mappings, rows and
// external references are tenant-scoped.

/// <summary>Session status codes (API-CONTRACT "Codes").</summary>
public static class ImportSessionStatus
{
    public const string Uploaded = "UPLOADED", Mapping = "MAPPING", Validating = "VALIDATING", Validated = "VALIDATED",
        Importing = "IMPORTING", Completed = "COMPLETED", CompletedWithErrors = "COMPLETED_WITH_ERRORS", Failed = "FAILED",
        Cancelled = "CANCELLED";

    public static readonly IReadOnlySet<string> Finished = new HashSet<string> { Completed, CompletedWithErrors, Failed, Cancelled };
}

/// <summary>Row status codes.</summary>
public static class ImportRowStatus
{
    public const string Pending = "PENDING", Valid = "VALID", Invalid = "INVALID", Imported = "IMPORTED", Skipped = "SKIPPED",
        Updated = "UPDATED", Error = "ERROR";
}

/// <summary>Duplicate strategies.</summary>
public static class ImportDuplicateStrategy
{
    public const string Skip = "SKIP", Update = "UPDATE", CreateNew = "CREATE_NEW", Error = "ERROR";
    public static readonly IReadOnlySet<string> All = new HashSet<string> { Skip, Update, CreateNew, Error };
}

public static class ImportFileTypes
{
    public const string Xlsx = "XLSX", Csv = "CSV";
}

/// <summary>Generic error codes raised by the Import module itself (targets add LOOKUP_NOT_FOUND etc.).</summary>
public static class ImportErrorCodes
{
    public const string Required = "REQUIRED", InvalidType = "INVALID_TYPE", MaxLength = "MAX_LENGTH", OutOfRange = "OUT_OF_RANGE",
        InvalidPrecision = "INVALID_PRECISION", PatternMismatch = "PATTERN_MISMATCH", InvalidEmail = "INVALID_EMAIL",
        InvalidPhone = "INVALID_PHONE", LookupNotFound = "LOOKUP_NOT_FOUND", DuplicateInFile = "DUPLICATE_IN_FILE",
        DuplicateInDb = "DUPLICATE_IN_DB", MissingDependency = "MISSING_DEPENDENCY", InvalidDate = "INVALID_DATE",
        ImportError = "IMPORT_ERROR";
}

/// <summary>Mirror of one target's <see cref="ImportProfileDefinition"/> (global, reconciled at startup).</summary>
internal sealed class ImportProfile
{
    public int Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ImportOrder { get; set; }
    public bool IsOpeningBalance { get; set; }
    public bool AllowUpdate { get; set; }
    /// <summary>JSON array of field names.</summary>
    public string MatchFieldsJson { get; set; } = "[]";
    /// <summary>False when no registered target describes it any more (kept for old sessions).</summary>
    public bool IsActive { get; set; }
    public DateTime SyncedAt { get; set; }
    public List<ImportProfileField> Fields { get; set; } = new();
}

internal sealed class ImportProfileField
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public string FieldName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string FieldType { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public bool IsKey { get; set; }
    public int SortOrder { get; set; }
    public string? SampleValue { get; set; }
    public int? MaxLength { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public int? DecimalPlaces { get; set; }
    public string? ValidationRegex { get; set; }
    public string? DefaultValue { get; set; }
    public string? LookupList { get; set; }
    public string? AliasesJson { get; set; }
}

/// <summary>An organization's own duplicate strategy / limits for one entity type (D-3 "custom profile row").</summary>
internal sealed class ImportProfileOverride : ITenantScopedEntity, IHasModifiedAt
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string DuplicateStrategy { get; set; } = ImportDuplicateStrategy.Skip;
    public int MaxRowsPerFile { get; set; }
    public int MaxFileSizeMb { get; set; }
    public int ModifiedBy { get; set; }
    public DateTime ModifiedAt { get; set; }
}

internal sealed class ImportSession : ITenantScopedEntity, IHasModifiedAt
{
    public int Id { get; set; }
    public Guid Uuid { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = ImportFileTypes.Xlsx;
    public long FileSizeBytes { get; set; }
    public string? SheetName { get; set; }
    /// <summary>JSON array of the workbook's sheet names (empty for CSV).</summary>
    public string SheetNamesJson { get; set; } = "[]";
    /// <summary>JSON array of the header row (source column names by index).</summary>
    public string HeadersJson { get; set; } = "[]";
    public string Status { get; set; } = ImportSessionStatus.Uploaded;
    public string DuplicateStrategy { get; set; } = ImportDuplicateStrategy.Skip;
    public string? Notes { get; set; }
    public string? SavedMappingName { get; set; }
    public DateTime? SavedMappingAt { get; set; }

    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int InvalidRows { get; set; }
    public int ImportedRows { get; set; }
    public int UpdatedRows { get; set; }
    public int SkippedRows { get; set; }
    public int ErrorRows { get; set; }
    public int ProcessedRows { get; set; }
    /// <summary>Rows the running import will process (the VALID rows when it started).</summary>
    public int RowsToProcess { get; set; }

    public DateTime? ValidationCompletedAt { get; set; }
    public DateTime? ImportStartedAt { get; set; }
    public DateTime? ImportCompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public string? JobId { get; set; }
    public int? ExecutedBy { get; set; }
    /// <summary>D-10 — set when the purge job deleted this session's rows (summary kept).</summary>
    public DateTime? RowsPurgedAt { get; set; }

    public int CreatedBy { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }

    public List<ImportColumnMapping> Mappings { get; set; } = new();
}

internal sealed class ImportColumnMapping : ITenantScopedEntity
{
    public int Id { get; set; }
    public int SessionId { get; set; }
    public Guid OrganizationId { get; set; }
    public int SourceColumnIndex { get; set; }
    public string SourceColumnName { get; set; } = string.Empty;
    public string? TargetFieldName { get; set; }
    public bool IsAutoMapped { get; set; }
    public decimal? ConfidenceScore { get; set; }
}

internal sealed class ImportRow : ITenantScopedEntity
{
    public long Id { get; set; }
    public int SessionId { get; set; }
    public Guid OrganizationId { get; set; }
    /// <summary>The row's number in the file (header = 1).</summary>
    public int RowNumber { get; set; }
    /// <summary>JSON array of the original cell values by source column index.</summary>
    public string RawJson { get; set; } = "[]";
    /// <summary>JSON object fieldName → parsed value (after validation).</summary>
    public string? ValuesJson { get; set; }
    /// <summary>JSON array of ImportFieldError.</summary>
    public string? ErrorsJson { get; set; }
    public string Status { get; set; } = ImportRowStatus.Pending;
    public string? ExternalId { get; set; }
    /// <summary>The existing record matched by external id or the target's match fields.</summary>
    public Guid? ExistingEntityUuid { get; set; }
    /// <summary>The record created or updated by the import.</summary>
    public Guid? EntityUuid { get; set; }
}

/// <summary>M040.7 — a source system's id for a record this organization imported. Unique per (org, entity, externalId)
/// and per (org, entity, entityUuid).</summary>
internal sealed class ImportExternalReference : ITenantScopedEntity, IHasModifiedAt
{
    public int Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public Guid EntityUuid { get; set; }
    public Guid? LastSessionUuid { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ModifiedAt { get; set; }
}
