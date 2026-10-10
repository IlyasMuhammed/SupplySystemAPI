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

    // Initial Admin user, created atomically with the organization — receives an email
    // invitation to set their own password (see IOrgUserProvisioningService).
    public string AdminFirstName { get; set; } = string.Empty;
    public string AdminLastName { get; set; } = string.Empty;
    public string AdminEmail { get; set; } = string.Empty;
}

public class CreateOrganizationResult
{
    public Guid OrganizationId { get; set; }
    public int AdminUserId { get; set; }
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

public class UpdateFeaturesResult
{
    public List<OrganizationFeatureModel> UpdatedFeatures { get; set; } = new();
    public List<string> AutoEnabledDependencies { get; set; } = new();
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
    public bool IsSuperAdmin { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
}
