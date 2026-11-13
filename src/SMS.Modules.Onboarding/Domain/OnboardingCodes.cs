namespace SMS.Modules.Onboarding.Domain;

public static class SetupCodes
{
    /// <summary>The wildcard country / industry of the fallback chain (D-4).</summary>
    public const string Global = "GLOBAL";
    /// <summary>OrganizationSetups.Note of organizations that existed before A50 (D-6).</summary>
    public const string LegacyNote = "LEGACY";
}

/// <summary>D-6 — OrganizationSetups.SetupStatus (CHECK).</summary>
public static class SetupStatuses
{
    public const string NotStarted = "NotStarted";
    public const string InProgress = "InProgress";
    public const string Completed  = "Completed";
    public const string Skipped    = "Skipped";

    public static readonly IReadOnlyList<string> All = [NotStarted, InProgress, Completed, Skipped];
}

/// <summary>D-11 — TenantSetupLog.Action (CHECK).</summary>
public static class SetupLogActions
{
    public const string Seed      = "Seed";
    public const string Merge     = "Merge";
    public const string Overwrite = "Overwrite";
    public const string FullReset = "FullReset";
    public const string Skipped   = "Skipped";
    public const string Failed    = "Failed";

    public static readonly IReadOnlyList<string> All = [Seed, Merge, Overwrite, FullReset, Skipped, Failed];
}
