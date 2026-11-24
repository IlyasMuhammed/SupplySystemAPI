namespace SMS.Shared.Onboarding;

// A51 P3 — what the super admin's Create Organization flow needs from other modules, without a project reference between them
// (the same arrangement as ICurrencyCodeLookup). Country lookup lives in SMS.Shared.Common (ICountryLookup).

/// <summary>Whether an industry may be chosen for a country (Onboarding's Industries + CountryIndustries masters).</summary>
public enum IndustryAvailability
{
    Available = 0,
    /// <summary>No such industry code.</summary>
    Unknown = 1,
    /// <summary>The industry exists but is switched off.</summary>
    Inactive = 2,
    /// <summary>The industry is active but has no active link to the country.</summary>
    NotOfferedInCountry = 3
}

public interface IIndustryCatalog
{
    Task<IndustryAvailability> CheckAsync(string countryCode, string industryCode, CancellationToken ct = default);
}

/// <summary>Pack levels of the A50 fallback chain, most specific first.</summary>
public static class OrganizationPackLevels
{
    public const string Exact    = "Exact";     // country + industry
    public const string Country  = "Country";   // country + GLOBAL industry
    public const string Industry = "Industry";  // GLOBAL country + industry
    public const string Global   = "Global";    // GLOBAL + GLOBAL
    public const string None     = "None";      // nothing resolved for the category
}

public sealed class OrganizationDefaultsCategoryResult
{
    public string Category { get; set; } = string.Empty;
    /// <summary>Applied / Skipped / ModuleDisabled / NoDefaults / NoSeeder / Invalid / Failed / NotRun (A50 outcomes).</summary>
    public string Status { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public int Created { get; set; }
    public int Skipped { get; set; }
    public string? Message { get; set; }
    /// <summary>The level of the fallback chain the default came from (<see cref="OrganizationPackLevels"/>).</summary>
    public string PackLevel { get; set; } = OrganizationPackLevels.None;
    public string? ResolvedCountry { get; set; }
    public string? ResolvedIndustry { get; set; }
}

/// <summary>What applying the A50 defaults to a new organization did. Never thrown: a failure is reported here.</summary>
public sealed class OrganizationDefaultsOutcome
{
    /// <summary>False when nothing was applied (no industry chosen, or the engine could not start): see <see cref="Message"/>.</summary>
    public bool Attempted { get; set; }
    /// <summary>NotStarted / InProgress / Completed / Skipped — the organization's OrganizationSetups status afterwards.</summary>
    public string? SetupStatus { get; set; }
    /// <summary>True when every category was ok and the setup is Completed.</summary>
    public bool Completed { get; set; }
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    /// <summary>The most specific pack level any category resolved at.</summary>
    public string PackLevel { get; set; } = OrganizationPackLevels.None;
    /// <summary>True when at least one category with defaults came from a more general level than the exact country + industry.</summary>
    public bool FallbackUsed { get; set; }
    public string? Message { get; set; }
    /// <summary>A51 P7 — <see cref="OrganizationDefaultsStatuses"/>: Running right after a background start, else the finished state.</summary>
    public string Status { get; set; } = OrganizationDefaultsStatuses.NotStarted;
    public List<OrganizationDefaultsCategoryResult> Categories { get; set; } = new();
}

/// <summary>
/// Applies the A50 default data to an organization other than the caller's own (the super admin creating it): it pre-fills the
/// organization's setup row with the chosen country / industry and, when asked, runs the A50 apply engine under that organization.
/// Implemented by SMS.Modules.Onboarding.
/// </summary>
public interface IOrganizationDefaultsApplier
{
    /// <summary>Only records the choice on the organization's setup row (wizard pre-fill); the status is unchanged.</summary>
    Task<OrganizationDefaultsOutcome> PrefillAsync(Guid organizationId, string countryCode, string? industryType, CancellationToken ct = default);

    /// <summary>Records the choice and runs the A50 apply (defaults only, no overrides, no invites) for the organization.</summary>
    Task<OrganizationDefaultsOutcome> ApplyAsync(Guid organizationId, string countryCode, string industryType, int? userId, CancellationToken ct = default);

    /// <summary>
    /// A51 P7 — records the choice (wizard pre-fill) and queues the apply for a background worker; returns at once with
    /// <see cref="OrganizationDefaultsOutcome.Status"/> = Running. Progress: <see cref="IOrganizationDefaultsStatusReader"/>.
    /// A host without a worker runs the apply inline instead.
    /// </summary>
    Task<OrganizationDefaultsOutcome> StartApplyAsync(Guid organizationId, string countryCode, string industryType, int? userId, CancellationToken ct = default);
}

// ── A51 P7 — the apply runs in the background after the organization is created ──

/// <summary>Overall state of an organization's default-data apply (<see cref="OrganizationDefaultsStatus.Status"/>).</summary>
public static class OrganizationDefaultsStatuses
{
    /// <summary>Queued or running in the background.</summary>
    public const string Running    = "Running";
    /// <summary>Every category ok; the setup is Completed.</summary>
    public const string Completed  = "Completed";
    /// <summary>Finished or interrupted with some categories not done (partial); the organization admin can finish in the wizard.</summary>
    public const string InProgress = "InProgress";
    /// <summary>The apply could not run at all.</summary>
    public const string Failed     = "Failed";
    /// <summary>Nothing was ever applied (no industry chosen, or the job was lost before it ran).</summary>
    public const string NotStarted = "NotStarted";
}

/// <summary>What the super admin reads while (and after) the default data is being applied: GET api/system/organizations/{id}/defaults-status.</summary>
public sealed class OrganizationDefaultsStatus
{
    public Guid OrganizationId { get; set; }
    /// <summary><see cref="OrganizationDefaultsStatuses"/>.</summary>
    public string Status { get; set; } = OrganizationDefaultsStatuses.NotStarted;
    /// <summary>The organization's OrganizationSetups status: NotStarted / InProgress / Completed / Skipped.</summary>
    public string? SetupStatus { get; set; }
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    public string PackLevel { get; set; } = OrganizationPackLevels.None;
    public bool FallbackUsed { get; set; }
    public string? Message { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    /// <summary>One row per category in apply order. Status: Pending until its turn, then the A50 outcome (Applied / Skipped / ModuleDisabled / Failed ...).</summary>
    public List<OrganizationDefaultsCategoryResult> Categories { get; set; } = new();
}

/// <summary>Reads <see cref="OrganizationDefaultsStatus"/>. Implemented by SMS.Modules.Onboarding; null when the organization is unknown.</summary>
public interface IOrganizationDefaultsStatusReader
{
    Task<OrganizationDefaultsStatus?> GetAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>A51 P7 — the country / industry / setup status the organization list and detail show (OrganizationSetups lives in Onboarding).</summary>
public sealed record OrganizationSetupSummary(string? CountryCode, string? IndustryType, string? SetupStatus);

public interface IOrganizationSetupSummaries
{
    /// <summary>One query for all the given organizations; organizations without a setup row are absent from the result.</summary>
    Task<Dictionary<Guid, OrganizationSetupSummary>> GetAsync(IReadOnlyCollection<Guid> organizationIds, CancellationToken ct = default);
}
