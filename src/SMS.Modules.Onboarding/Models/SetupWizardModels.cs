using System.Text.Json;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Models;

// A50 API-CONTRACT Details "Org admin" — api/onboarding/setup/*.

public sealed class SetupStatusModel
{
    public string    SetupStatus { get; init; } = string.Empty;
    public int       SetupStep { get; init; }
    public string?   CountryCode { get; init; }
    public string?   IndustryType { get; init; }
    public DateTime? CompletedAt { get; init; }
    public bool      CanRunWizard { get; init; }
}

public sealed class SetupStateModel
{
    public string    SetupStatus { get; init; } = string.Empty;
    public int       SetupStep { get; init; }
    public string?   CountryCode { get; init; }
    public string?   IndustryType { get; init; }
    /// <summary>{ "1": {...}, "3": {...} }.</summary>
    public JsonElement StepData { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int?      CompletedBy { get; init; }
    public string?   Note { get; init; }
    public DateTime? LastAppliedAt { get; init; }
}

public sealed class SaveSetupStepRequest
{
    public JsonElement Data { get; set; }
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
}

public sealed record SetupOptionModel(string Code, string Name, int? DefaultsCount = null);

public sealed class SetupPreviewRequest
{
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    public List<string>? Categories { get; set; }
    public Dictionary<string, JsonElement>? PayloadOverrides { get; set; }
}

public static class SetupCategoryOutcomes
{
    public const string Ready          = "Ready";
    public const string Applied        = "Applied";
    public const string Skipped        = "Skipped";
    public const string ModuleDisabled = "ModuleDisabled";
    public const string NoDefaults     = "NoDefaults";
    public const string NoSeeder       = "NoSeeder";
    public const string Invalid        = "Invalid";
    public const string Failed         = "Failed";
    public const string NotRun         = "NotRun";

    /// <summary>Outcomes that do not hold back Completed (D-11).</summary>
    public static bool IsOk(string status) => status is Applied or Skipped or ModuleDisabled or NoDefaults or NoSeeder;
}

public sealed class SetupPreviewItem
{
    public string  Category { get; init; } = string.Empty;
    public string? DisplayName { get; init; }
    public string  Status { get; init; } = SetupCategoryOutcomes.Ready;
    public Guid?   SetupDefaultUuid { get; init; }
    public int?    Version { get; init; }
    public ResolvedFromModel? ResolvedFrom { get; init; }
    public JsonElement? Payload { get; init; }
    public bool?   Verified { get; init; }
    public SetupPreview? Preview { get; init; }
    public string? RequiresModule { get; init; }
    public bool    ModuleEnabled { get; init; }
    public bool    HasSeeder { get; init; }
    public List<string> Errors { get; init; } = new();
}

public sealed class SetupInviteInput
{
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    /// <summary>A RoleID (digits) or a ROLES pack role code seeded / reused by the apply.</summary>
    public string? RoleRef { get; set; }
}

public sealed class SetupApplyRequest
{
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    public Dictionary<string, JsonElement>? PayloadOverrides { get; set; }
    public Dictionary<string, JsonElement>? WizardInputs { get; set; }
    public List<string>? SkipCategories { get; set; }
    public List<SetupInviteInput>? Invites { get; set; }
}

public sealed class SetupCategoryResult
{
    public string  Category { get; init; } = string.Empty;
    public string  Status { get; set; } = SetupCategoryOutcomes.NotRun;
    /// <summary>The TenantSetupLog action written (null = none).</summary>
    public string? Action { get; set; }
    public bool    Ok => SetupCategoryOutcomes.IsOk(Status);
    public int     Created { get; set; }
    public int     Skipped { get; set; }
    public int     Removed { get; set; }
    public int?    Version { get; set; }
    public List<string> Warnings { get; set; } = new();
    public string? Message { get; set; }
}

public static class SetupInviteOutcomes
{
    public const string Invited = "INVITED";
    public const string Exists  = "EXISTS";
    public const string Invalid = "INVALID";
    public const string Failed  = "FAILED";
}

public sealed class SetupInviteResult
{
    public string  Email { get; init; } = string.Empty;
    public string  Outcome { get; init; } = SetupInviteOutcomes.Failed;
    public int?    UserId { get; init; }
    public string? Message { get; init; }
}

public sealed class SetupApplyResponse
{
    public string SetupStatus { get; init; } = string.Empty;
    public bool   Completed { get; init; }
    public List<SetupCategoryResult> Results { get; init; } = new();
    public List<SetupInviteResult> Invites { get; init; } = new();
}

public sealed class SetupCategoriesModel
{
    public string?   CountryCode { get; init; }
    public string?   IndustryType { get; init; }
    public string    SetupStatus { get; init; } = string.Empty;
    public DateTime? CompletedAt { get; init; }
    public List<SetupCategoryStatusModel> Categories { get; init; } = new();
}

public sealed class SetupCategoryStatusModel
{
    public string    Category { get; init; } = string.Empty;
    public string?   DisplayName { get; init; }
    public string?   RequiresModule { get; init; }
    public bool      ModuleEnabled { get; init; }
    public bool      HasSeeder { get; init; }
    public int       SeededCount { get; init; }
    public int       CurrentCount { get; init; }
    public int       ModifiedCount { get; init; }
    public int       MissingCount { get; init; }
    public List<string> ModifiedKeys { get; init; } = new();
    public List<string> MissingKeys { get; init; } = new();
    public bool      IsModified { get; init; }
    public DateTime? LastAppliedAt { get; init; }
    public string?   LastAction { get; init; }
    public int?      AppliedVersion { get; init; }
    public int?      LatestVersion { get; init; }
    public bool      UpdateAvailable { get; init; }
}

public static class SetupResetSources
{
    public const string Applied = "applied";
    public const string Latest  = "latest";
}

public sealed class SetupResetRequest
{
    public SetupResetMode Mode { get; set; } = SetupResetMode.Merge;
    public bool Confirm { get; set; }
    public string? Source { get; set; }
}

public sealed class SeededRecordModel
{
    public string   NaturalKey { get; init; } = string.Empty;
    public string   EntityRef { get; init; } = string.Empty;
    public int      Version { get; init; }
    public bool     Reused { get; init; }
    public DateTime SeededAt { get; init; }
    public int      ResetCount { get; init; }
}
