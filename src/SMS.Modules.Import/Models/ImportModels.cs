using System.Security.Claims;
using SMS.Shared.Authorization;
using SMS.Shared.Common;

namespace SMS.Modules.Import.Models;

/// <summary>A40 — module constants (API-CONTRACT, D-10).</summary>
public static class ImportLimits
{
    public const int DefaultMaxRowsPerFile = 50_000;
    public const int DefaultMaxFileSizeMb  = 10;
    /// <summary>More valid rows than this run as a Hangfire job on queue <see cref="Queue"/>.</summary>
    public const int BackgroundThreshold   = 500;
    public const int BatchSize             = 100;
    public const int ValidationErrorsReturned = 200;
    public const int RetentionDays         = 90;
    /// <summary>D-38 — an IMPORTING session with no batch saved for this long and no live Hangfire job is interrupted.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    /// <summary>D-40 — an UPLOADED session untouched this long is cancelled by the recovery job.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromDays(7);
    public const string Queue              = "import";
    /// <summary>The importer's own field on every profile (external-system id, M040.7).</summary>
    public const string ExternalIdField    = "external_id";
}

/// <summary>Exact refusal texts (API-CONTRACT "Exact refusals").</summary>
public static class ImportMessages
{
    public const string FileType = "Only .xlsx and .csv files are accepted. Please save the file as .xlsx (Excel 2007+).";
    public static string FileSize(int mb) => $"File exceeds the maximum size of {mb} MB.";
    public static string FileRows(int rows) => $"File exceeds the maximum of {rows} rows.";
    public const string NoHeaders = "No column headers detected in the first row.";
    public const string NoDataRows = "The file has no data rows.";
    public static string AlreadyRunning(string entity) => $"Another {entity} import is already running.";
    public static string MapRequired(string display) => $"Map required field: {display}.";
    public static string MapKey(string displays) => $"Map at least one key field: {displays}.";
    public static string ColumnTwice(string source) => $"Column '{source}' is mapped twice.";
    public static string FieldTwice(string display) => $"Field '{display}' is mapped twice.";
    public const string MappingsLocked = "Mappings cannot change once the import has started.";
    public const string OpeningDateMissing = "Set the opening balance date in Finance settings first.";
    // Details fixed by the backend (IMP) — API-CONTRACT appendix.
    public static string SheetNotFound(string sheet) => $"Sheet '{sheet}' was not found in the workbook.";
    public const string Unreadable = "The file could not be read. Please check that it is a valid .xlsx or .csv file.";
    public const string OpeningPermission = "You do not have permission to import opening balances.";
    public const string OpeningFeature = "Opening balance import is not enabled for your organization.";
    public const string ValidateFirst = "Validate the file before importing.";
    public const string NoValidRows = "There are no valid rows to import.";
    public const string Finished = "This import has already finished.";
    public const string Interrupted = "Import interrupted — re-validate and re-run the remaining rows.";
    public const string Abandoned = "Cancelled automatically: not mapped or validated within 7 days.";
    public const string NotRunnable = "This import is already running or finished.";
    public const string UpdateNotAllowed = "This import does not allow updating existing records.";
    public const string OpeningStrategy = "Opening balance imports always reject duplicates (strategy ERROR).";
    public static string UnknownEntity(string entityType) => $"Unknown import type '{entityType}'.";
    public static string UnknownField(string field) => $"Unknown field '{field}'.";
    public static string UnknownColumn(int index) => $"The file has no column {index}.";
}

/// <summary>The signed-in user as the import services need it.</summary>
public sealed record ImportCaller(int UserId, string? Name, IReadOnlySet<string> Permissions)
{
    public bool Has(string permission) => Permissions.Contains(permission);
    public bool CanManageAll => Has(PermissionCodes.IMPORT_MANAGE);

    public static ImportCaller From(ClaimsPrincipal user) =>
        new(user.GetUserId(), user.FindFirst("email")?.Value ?? user.FindFirst(ClaimTypes.Email)?.Value,
            user.GetPermissions().ToHashSet(StringComparer.OrdinalIgnoreCase));
}

public sealed class ImportProfileModel
{
    public string EntityType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ImportOrder { get; set; }
    public bool IsOpeningBalance { get; set; }
    public IReadOnlyList<string> MatchFields { get; set; } = [];
    public string DuplicateStrategy { get; set; } = string.Empty;
    public int MaxRowsPerFile { get; set; }
    public int MaxFileSizeMb { get; set; }
    public bool AllowUpdate { get; set; }
    public bool RequiresOpeningBalancePermission { get; set; }
    public int CompletedSessions { get; set; }
    public DateTime? LastCompletedAt { get; set; }
    /// <summary>The most recent COMPLETED / COMPLETED_WITH_ERRORS session (link to its detail page).</summary>
    public Guid? LastCompletedSessionUuid { get; set; }
    public int? LastImportedRows { get; set; }
}

public sealed class ImportProfileDetailModel
{
    public string EntityType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int ImportOrder { get; set; }
    public bool IsOpeningBalance { get; set; }
    public IReadOnlyList<string> MatchFields { get; set; } = [];
    public string DuplicateStrategy { get; set; } = string.Empty;
    public int MaxRowsPerFile { get; set; }
    public int MaxFileSizeMb { get; set; }
    public bool AllowUpdate { get; set; }
    public bool RequiresOpeningBalancePermission { get; set; }
    public int CompletedSessions { get; set; }
    public DateTime? LastCompletedAt { get; set; }
    public Guid? LastCompletedSessionUuid { get; set; }
    public int? LastImportedRows { get; set; }
    public IReadOnlyList<ImportFieldModel> Fields { get; set; } = [];
}

public sealed class ImportFieldModel
{
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
    public string? LookupList { get; set; }
}

public sealed class ImportProfileSettingsRequest
{
    public string? DuplicateStrategy { get; set; }
    public int? MaxRowsPerFile { get; set; }
    public int? MaxFileSizeMb { get; set; }
}

public sealed class ImportUploadRequest
{
    public string EntityType { get; set; } = string.Empty;
    public string? DuplicateStrategy { get; set; }
    public string? SheetName { get; set; }
    public string? MappingPreset { get; set; }
    public string? Notes { get; set; }
}

public class ImportSessionListItem
{
    public Guid Uuid { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int InvalidRows { get; set; }
    public int ImportedRows { get; set; }
    public int UpdatedRows { get; set; }
    public int SkippedRows { get; set; }
    public int ErrorRows { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByName { get; set; }
}

public sealed class ImportSessionDetail : ImportSessionListItem
{
    public string FileType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string? SheetName { get; set; }
    public IReadOnlyList<string> SheetNames { get; set; } = [];
    public string DuplicateStrategy { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public string? SavedMappingName { get; set; }
    public DateTime? ValidationCompletedAt { get; set; }
    public DateTime? ImportStartedAt { get; set; }
    public DateTime? ImportCompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public IReadOnlyList<ImportColumnMappingModel> Mappings { get; set; } = [];
    public IReadOnlyList<string> UnmappedColumns { get; set; } = [];
    /// <summary>Field NAMES of required fields no column is mapped to.</summary>
    public IReadOnlyList<string> UnmappedRequiredFields { get; set; } = [];
}

public sealed class ImportColumnMappingModel
{
    public int SourceColumnIndex { get; set; }
    public string SourceColumnName { get; set; } = string.Empty;
    public string? TargetFieldName { get; set; }
    public string? TargetDisplayName { get; set; }
    public bool IsAutoMapped { get; set; }
    public decimal? ConfidenceScore { get; set; }
}

public sealed class ImportMappingsRequest
{
    public List<ImportMappingItem> Mappings { get; set; } = new();
}

public sealed class ImportMappingItem
{
    public int SourceColumnIndex { get; set; }
    public string? TargetFieldName { get; set; }
}

public sealed class ImportSavePresetRequest
{
    public string? PresetName { get; set; }
}

public sealed record ImportPresetModel(string Name, DateTime LastUsedAt);

public sealed class ImportPreviewModel
{
    public IReadOnlyList<ImportPreviewColumn> Columns { get; set; } = [];
    public IReadOnlyList<ImportPreviewRow> Rows { get; set; } = [];
}

public sealed record ImportPreviewColumn(string FieldName, string DisplayName);

public sealed class ImportPreviewRow
{
    public int RowNumber { get; set; }
    public IReadOnlyDictionary<string, string?> Values { get; set; } = new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, string>? Issues { get; set; }
}

public sealed class ImportValidationResult
{
    public ImportSessionDetail Session { get; set; } = new();
    public IReadOnlyList<ImportValidationError> Errors { get; set; } = [];
    public IReadOnlyDictionary<string, int> ErrorCountByCode { get; set; } = new Dictionary<string, int>();
}

public sealed record ImportValidationError(int RowNumber, string FieldName, string ErrorCode, string Message, string? SourceValue);

public sealed class ImportExecuteResult
{
    public bool IsBackground { get; set; }
    public string? JobId { get; set; }
    public ImportSessionDetail Session { get; set; } = new();
}

public sealed record ImportProgressModel(Guid SessionUuid, int Processed, int Total, string Status);

public sealed class ImportErrorRowModel
{
    public int RowNumber { get; set; }
    public string Status { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string?> Values { get; set; } = new Dictionary<string, string?>();
    public IReadOnlyList<ImportFieldError> Errors { get; set; } = [];
}

public sealed class ImportSessionFilter
{
    public string? EntityType { get; set; }
    public string? Status { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>A downloadable file.</summary>
public sealed record ImportFile(byte[] Content, string FileName)
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
}
