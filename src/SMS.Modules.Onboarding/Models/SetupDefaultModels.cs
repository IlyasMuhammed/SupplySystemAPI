using System.Text.Json;

namespace SMS.Modules.Onboarding.Models;

// A50 API-CONTRACT Details "Super admin" — api/system/setup-defaults.

public sealed class SetupDefaultModel
{
    public Guid    Uuid { get; init; }
    public string  CountryCode { get; init; } = string.Empty;
    public string  IndustryType { get; init; } = string.Empty;
    public string  DataCategory { get; init; } = string.Empty;
    public string  DisplayName { get; init; } = string.Empty;
    public int     Version { get; init; }
    public bool    IsActive { get; init; }
    public string? Notes { get; init; }
    public bool    IsSystemPack { get; init; }
    public int     RecordCount { get; init; }
    public int     AppliedCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public int?    CreatedBy { get; init; }
    public DateTime ModifiedAt { get; init; }
    public int?    ModifiedBy { get; init; }
    public string? RowVersion { get; init; }
    /// <summary>Only on the detail / save responses (not in the list).</summary>
    public JsonElement? DataPayload { get; init; }
    /// <summary>Only on <c>resolve</c>.</summary>
    public ResolvedFromModel? ResolvedFrom { get; init; }
}

public sealed class ResolvedFromModel
{
    public string Country { get; init; } = string.Empty;
    public string Industry { get; init; } = string.Empty;
}

public sealed class CreateSetupDefaultRequest
{
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    public string? DataCategory { get; set; }
    public string? DisplayName { get; set; }
    public string? Notes { get; set; }
    public bool    IsActive { get; set; } = true;
    public JsonElement DataPayload { get; set; }
}

public sealed class UpdateSetupDefaultRequest
{
    public string? DisplayName { get; set; }
    public string? Notes { get; set; }
    public bool?   IsActive { get; set; }
    /// <summary>Undefined (absent) = keep the payload.</summary>
    public JsonElement DataPayload { get; set; }
    /// <summary>Base64 rowversion from the read; optional (absent = last write wins).</summary>
    public string? RowVersion { get; set; }
}

public sealed class ValidateSetupPayloadRequest
{
    public string? DataCategory { get; set; }
    public JsonElement DataPayload { get; set; }
}

public sealed class ValidateSetupPayloadResult
{
    public bool Valid { get; init; }
    public List<string> Errors { get; init; } = new();
}

public sealed class SetupDefaultsMetaModel
{
    public List<SetupCategoryMetaModel> Categories { get; init; } = new();
    public List<string> Countries { get; init; } = new();
    public List<string> Industries { get; init; } = new();
}

public sealed class SetupCategoryMetaModel
{
    public string  Code { get; init; } = string.Empty;
    public bool    HasSeeder { get; init; }
    public bool    HasSchema { get; init; }
    public string? RequiresModule { get; init; }
}

/// <summary>The pack file (import body / export file).</summary>
public sealed class SetupDefaultsPackFile
{
    public string Format { get; set; } = SetupDefaultsPackFormat.Name;
    public int    Version { get; set; } = 1;
    public DateTime? ExportedAt { get; set; }
    public List<SetupDefaultsPackItem> Defaults { get; set; } = new();
}

public static class SetupDefaultsPackFormat
{
    public const string Name = "sms-setup-defaults";
}

public sealed class SetupDefaultsPackItem
{
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    public string? DataCategory { get; set; }
    public string? DisplayName { get; set; }
    public string? Notes { get; set; }
    public bool?   IsActive { get; set; }
    /// <summary>Written on export; ignored on import.</summary>
    public int?    Version { get; set; }
    public JsonElement Payload { get; set; }
}

public sealed class SetupDefaultsImportResult
{
    public bool DryRun { get; init; }
    public int  Created { get; set; }
    public int  Updated { get; set; }
    public int  Unchanged { get; set; }
    public List<SetupDefaultsImportError> Errors { get; init; } = new();
    public List<SetupDefaultsImportItem> Items { get; init; } = new();
}

public sealed record SetupDefaultsImportError(int Index, string Key, List<string> Errors);

public sealed record SetupDefaultsImportItem(string Key, string Action);

public static class SetupImportModes
{
    public const string Upsert = "upsert";
    public const string InsertOnly = "insertOnly";
}
