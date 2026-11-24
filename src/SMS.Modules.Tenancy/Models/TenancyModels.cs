namespace SMS.Modules.Tenancy.Models;

// ── Organization request models ─────────────────────────────────────────────

public class CreateOrganizationRequest
{
    public string OrgCode { get; set; } = string.Empty;
    public string OrgName { get; set; } = string.Empty;
    public string Plan { get; set; } = "BASIC";
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Address { get; set; }
    public string? Country { get; set; }
    public string? TimeZone { get; set; }
    public Guid? BaseCurrency { get; set; }

    // A51 P3 — chosen at creation. All optional: without a country/industry the organization behaves exactly as before (setup
    // NotStarted, the wizard asks on the admin's first login). CountryCode is an alpha-2 code of the Lookups countries master and
    // sets Country (the NAME) and, when TimeZone is empty, the country's default timezone. IndustryType needs a CountryCode and
    // must be offered in that country (Industries / CountryIndustries masters).
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    /// <summary>Optional alias of <see cref="Plan"/> (the A51 spec's name for it); wins over Plan when both are sent.</summary>
    public string? PlanCode { get; set; }

    // Initial Admin user, created atomically with the organization — receives an email
    // invitation to set their own password (see IOrgUserProvisioningService).
    public string AdminFirstName { get; set; } = string.Empty;
    public string AdminLastName { get; set; } = string.Empty;
    public string AdminEmail { get; set; } = string.Empty;

    /// <summary>
    /// The modules to license at creation. Null = the plan's defaults (the old behaviour); a list (even an empty one)
    /// licenses exactly those modules, what they depend on, and the always-on core — so an empty list leaves an
    /// organization whose admin can only manage users.
    /// </summary>
    public List<string>? ModuleCodes { get; set; }
}

public class CreateOrganizationResult
{
    public Guid OrganizationId { get; set; }
    public int AdminUserId { get; set; }

    // A51 P3 — what was stored, so the form can show it.
    public string? Plan { get; set; }
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    public string? TimeZone { get; set; }
    /// <summary>True when the timezone came from the country's default rather than from the request.</summary>
    public bool TimeZoneFromCountry { get; set; }

    /// <summary>
    /// Null when no country/industry was chosen (nothing to apply). Otherwise what applying the A50 default data did: best effort,
    /// the organization exists whatever the outcome.
    /// </summary>
    public SMS.Shared.Onboarding.OrganizationDefaultsOutcome? Defaults { get; set; }

    /// <summary>A51 P7 — plan features NOT licensed to the new organization because they are Coming Soon or not visible (reason ComingSoon / NotVisible).</summary>
    public List<PlanApplyFeatureItem> SkippedFeatures { get; set; } = new();

    /// <summary>A51 P7 (A51-D12) — did the initial admin's invitation e-mail go out? The invite link is present ONLY when it did not.</summary>
    public OrganizationInvitationOutcome? Invitation { get; set; }
}

public class OrganizationInvitationOutcome
{
    public string? AdminEmail { get; set; }
    public bool EmailSent { get; set; }
    public bool EmailConfigured { get; set; }
    public string? Error { get; set; }
    /// <summary>Set only when the e-mail was not sent (failed or not configured): the Super Admin hands it to the admin. Never logged.</summary>
    public string? InviteLink { get; set; }
    public DateTime? InviteExpiresAt { get; set; }
}

public class ResendInvitationRequest
{
    /// <summary>Optional corrected address (only for an account that is not set up yet).</summary>
    public string? Email { get; set; }
}

public class ResendInvitationResult
{
    public SMS.Shared.Common.OrgUserSummary? User { get; set; }
    public bool EmailSent { get; set; }
    public bool EmailConfigured { get; set; }
    public string? Error { get; set; }
    /// <summary>Set only when the e-mail was not sent. Never logged.</summary>
    public string? InviteLink { get; set; }
}

// Profile-only edit — deliberately excludes Plan/IsActive, which are separate, deliberate
// actions (PatchPlanRequest / PatchStatusRequest) so a routine profile edit can never
// accidentally trigger a feature-template reset or reactivate a suspended tenant.
public class UpdateOrganizationRequest
{
    public string OrgName { get; set; } = string.Empty;
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Address { get; set; }
    public string? Country { get; set; }
    public string? TimeZone { get; set; }

    /// <summary>
    /// A lookups.Currencies id to make the organization's base currency. Null (or absent) leaves the base
    /// currency as it is — a client that does not deal with it (the profile form before it had a picker)
    /// must not wipe it. To remove it, send <see cref="ClearBaseCurrency"/> instead.
    /// </summary>
    public Guid? BaseCurrency { get; set; }

    /// <summary>True removes the base currency. Cannot be combined with a <see cref="BaseCurrency"/>.</summary>
    public bool ClearBaseCurrency { get; set; }
}

// ── Organization settings (REQ-4.x) ─────────────────────────────────────────

public class OrganizationSettingsModel
{
    public int AckLinkExpiryDays { get; set; }
}

public class UpdateOrganizationSettingsRequest
{
    [System.ComponentModel.DataAnnotations.Range(
        SMS.Shared.Common.OrganizationSettingsDefaults.AckLinkExpiryMinDays,
        SMS.Shared.Common.OrganizationSettingsDefaults.AckLinkExpiryMaxDays,
        ErrorMessage = "Acknowledgment link expiry must be between {1} and {2} days.")]
    public int AckLinkExpiryDays { get; set; }
}

// ── A35 D-7 — organization currency settings (API-CONTRACT.md §4) ───────────

public class OrgCurrencySettingsModel
{
    public Guid SaleBaseCurrencyId { get; set; }
    public string? SaleBaseCurrencyCode { get; set; }
    public Guid PurchaseBaseCurrencyId { get; set; }
    public string? PurchaseBaseCurrencyCode { get; set; }
    public Guid ServiceBaseCurrencyId { get; set; }
    public string? ServiceBaseCurrencyCode { get; set; }
    public Guid RateCurrencyId { get; set; }
    public string? RateCurrencyCode { get; set; }
    public string? ExchangeGainAccountCode { get; set; }
    public string? ExchangeLossAccountCode { get; set; }
    public string? UnrealizedGainAccountCode { get; set; }
    public string? UnrealizedLossAccountCode { get; set; }
    /// <summary>False when the organization has no settings row yet (values are the D-7 fallback).</summary>
    public bool IsStored { get; set; }
    public OrgCurrencySettingsLocks Locks { get; set; } = new();
}

public class OrgCurrencySettingsLocks
{
    public OrgCurrencySettingsLock Sale { get; set; } = new();
    public OrgCurrencySettingsLock Purchase { get; set; } = new();
    public OrgCurrencySettingsLock Service { get; set; } = new();
    public OrgCurrencySettingsLock RateCurrency { get; set; } = new();
}

public class OrgCurrencySettingsLock
{
    public bool Locked { get; set; }
    /// <summary>What uses it ("12 confirmed sale orders"); null when not locked.</summary>
    public string? Reason { get; set; }
}

public class UpdateOrgCurrencySettingsRequest
{
    public Guid SaleBaseCurrencyId { get; set; }
    public Guid PurchaseBaseCurrencyId { get; set; }
    public Guid ServiceBaseCurrencyId { get; set; }
    /// <summary>Null = unchanged.</summary>
    public Guid? RateCurrencyId { get; set; }
    public string? ExchangeGainAccountCode { get; set; }
    public string? ExchangeLossAccountCode { get; set; }
    public string? UnrealizedGainAccountCode { get; set; }
    public string? UnrealizedLossAccountCode { get; set; }
}

public class PatchOrganizationStatusRequest
{
    public bool IsActive { get; set; }
}

public class PatchOrganizationPlanRequest
{
    public string Plan { get; set; } = string.Empty;
}

// "The org admin" has no dedicated column on Organization — it's whichever user holds the
// OrgAdmin role within the org (see IOrgUserProvisioningService.ReassignOrgAdminAsync).
public class UpdateOrgAdminRequest
{
    public int NewAdminUserId { get; set; }
}

/// <summary>Resend the invite to a user who has not set up their account; Email, when given, corrects the address first.</summary>
public class ReinviteOrgUserRequest
{
    public string? Email { get; set; }
}

// ── Organization response models ────────────────────────────────────────────

public class OrganizationListItemModel
{
    public Guid Id { get; set; }
    public string OrgCode { get; set; } = string.Empty;
    public string OrgName { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? ContactEmail { get; set; }
    public DateTime CreatedDate { get; set; }
    /// <summary>A51 P7 — from the organization's setup row (Onboarding); null when it has none.</summary>
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    /// <summary>NotStarted / InProgress / Completed / Skipped; null when the organization has no setup row.</summary>
    public string? SetupStatus { get; set; }
}

public class OrganizationDetailModel
{
    public Guid Id { get; set; }
    public string OrgCode { get; set; } = string.Empty;
    public string OrgName { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? Address { get; set; }
    public string? Country { get; set; }
    public string? TimeZone { get; set; }
    public Guid? BaseCurrency { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    /// <summary>A51 P7 — see <see cref="OrganizationListItemModel"/>. Read-only: changing country or industry later does not re-run the default data.</summary>
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    public string? SetupStatus { get; set; }
}

public class OrganizationFilter
{
    public string? Search { get; set; }
    public string? Plan { get; set; }
    public bool? IsActive { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

// ── Feature catalog / toggle models ─────────────────────────────────────────

public class FeatureDefinitionModel
{
    public Guid Id { get; set; }
    public string FeatureCode { get; set; } = string.Empty;
    public string FeatureName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsCore { get; set; }
    public int DisplayOrder { get; set; }
    /// <summary>False for catalog entries that are listed but not built ("Coming soon").</summary>
    public bool IsAvailable { get; set; } = true;
    /// <summary>A51 P2 — Live | Beta | ComingSoon | Deprecated.</summary>
    public string Status { get; set; } = "Live";
    public bool IsVisible { get; set; } = true;
    public string? Icon { get; set; }
}

// Merged catalog + one org's toggle state — backs the feature-management screen.
public class OrganizationFeatureModel
{
    public string FeatureCode { get; set; } = string.Empty;
    public string FeatureName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsCore { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsEnabled { get; set; }
    public DateTime? ModifiedDate { get; set; }
    // A37 §1.2
    public bool IsLicensed { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? GraceEndsAt { get; set; }
    public string? ParentModuleCode { get; set; }
    public bool IsAlwaysOn { get; set; }
    public bool IsAvailable { get; set; }
    /// <summary>Usable now: licensed, on, and its module usable too (what the menu and the API gates go by).</summary>
    public bool IsUsable { get; set; }
    /// <summary>A51 P7 — the platform status of the feature (Live / Beta / ComingSoon / Deprecated), so the page can say "not licensed because Coming Soon".</summary>
    public string FeatureStatus { get; set; } = "Live";
    public bool IsVisible { get; set; } = true;
    /// <summary>The organization keeps access whatever the status / visibility say.</summary>
    public bool IsGrandfathered { get; set; }
}

public class PlanFeatureTemplateModel
{
    public string Plan { get; set; } = string.Empty;
    public List<PlanFeatureTemplateItem> Features { get; set; } = new();
}

public class PlanFeatureTemplateItem
{
    public string FeatureCode { get; set; } = string.Empty;
    public bool IsEnabledByDefault { get; set; }
}

// Single atomic bulk-toggle endpoint (PUT .../features) — replaces the single-item PATCH +
// independent-per-item bulk-toggle POST from MT-001. All-or-nothing: the whole batch is validated
// (core-protection + dependency graph) before anything is written.
public class UpdateFeaturesRequest
{
    public List<FeatureToggleItem> Features { get; set; } = new();
}

public class FeatureToggleItem
{
    public string FeatureCode { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

public class LicensedNotEnabledModel
{
    public string FeatureCode { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class UpdateFeaturesResult
{
    public List<OrganizationFeatureModel> UpdatedFeatures { get; set; } = new();
    public List<string> AutoEnabledDependencies { get; set; } = new();
    /// <summary>A49 D-POST-1: licensed, but a module guard kept the switch off (with why, "CODE: text").</summary>
    public List<LicensedNotEnabledModel> LicensedNotEnabled { get; set; } = new();
}

// ── A37 module registry (API-CONTRACT §1.1) ─────────────────────────────────

public class EnabledModulesModel
{
    public List<string> Modules { get; set; } = new();
    public List<string> Features { get; set; } = new();
    public List<ModuleGraceModel> Grace { get; set; } = new();
}

public class ModuleGraceModel
{
    public string Code { get; set; } = string.Empty;
    public DateTime GraceEndsAt { get; set; }
}

public class ModuleCardModel
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public bool IsAlwaysOn { get; set; }
    public bool IsAvailable { get; set; }
    public bool IsLicensed { get; set; }
    public bool IsEnabled { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? GraceEndsAt { get; set; }
    public DateTime? DisabledAt { get; set; }
    public string? DisabledByName { get; set; }
    public DateTime? EnabledAt { get; set; }
    public List<ModuleRefModel> DependsOn { get; set; } = new();
    public List<ModuleRefModel> Dependents { get; set; } = new();
    public List<ModuleFeatureModel> Features { get; set; } = new();
    public int FeatureCount { get; set; }
    public int EnabledFeatureCount { get; set; }
    public string RowVersion { get; set; } = string.Empty;
    internal int DisplayOrder { get; set; }
}

public class ModuleRefModel
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
}

public class ModuleFeatureModel
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsCore { get; set; }
    public bool IsAvailable { get; set; }
    public bool IsLicensed { get; set; }
    public bool IsEnabled { get; set; }
    public string? RequiresCode { get; set; }
    public List<string> RequiredBy { get; set; } = new();
    public bool AutoManaged { get; set; }
}

public class ModuleHistoryEntryModel
{
    public DateTime PerformedAt { get; set; }
    public string Action { get; set; } = string.Empty;
    public string? FeatureCode { get; set; }
    public string PerformedByName { get; set; } = string.Empty;
    public int? GraceDays { get; set; }
    public string? Notes { get; set; }
}

public class ModuleImpactModel
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<ModuleRefModel> Dependents { get; set; } = new();
    public List<ModuleImpactLineModel> InProgress { get; set; } = new();
    public List<string> WillBlock { get; set; } = new();
    public List<string> NotAffected { get; set; } = new();
    public int DefaultGraceDays { get; set; } = 30;
}

public class ModuleImpactLineModel
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class EnableModuleRequest
{
    public string? RowVersion { get; set; }
}

public class DisableModuleRequest
{
    public int? GraceDays { get; set; }
    public string? Notes { get; set; }
    public string? RowVersion { get; set; }
}

public class ToggleModuleFeatureRequest
{
    public bool Enabled { get; set; }
    public string? RowVersion { get; set; }
}

public class CurrentTenantFeatureState
{
    public string Code { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public bool Usable { get; set; }
    public bool IncludedInPlan { get; set; }
}

// ── GET /api/tenant/current ──────────────────────────────────────────────────
// Consumed by the React sidebar on every page load to render/hide menu items — feature codes and
// permissions come straight from JWT claims (no extra query), org info from a single lookup.
public class CurrentTenantModel
{
    public Guid Id { get; set; }
    public string OrgCode { get; set; } = string.Empty;
    public string OrgName { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    /// <summary>The organization's base currency (a lookups.Currencies id); null when none is configured.</summary>
    public Guid? BaseCurrency { get; set; }
    public List<string> EnabledFeatureCodes { get; set; } = new();
    /// <summary>A51 P4 — per feature: status, visibility, usable, includedInPlan (the menu's greyed Coming Soon item).</summary>
    public List<CurrentTenantFeatureState> FeatureStates { get; set; } = new();
    public bool IsSuperAdmin { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
}
