namespace SMS.Modules.Tenancy.Models;

// ── A51 P2 — plans ──────────────────────────────────────────────────────────

public class PlanModel
{
    public int Id { get; set; }
    /// <summary>What Organization.Plan stores (BASIC is displayed as "Default" — see Name).</summary>
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Display only: nothing bills from it.</summary>
    public decimal? MonthlyPrice { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public int IncludedFeatureCount { get; set; }
    public int TotalFeatureCount { get; set; }
}

public class PlanFeatureItemModel
{
    public string FeatureCode { get; set; } = string.Empty;
    public string FeatureName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? ParentModuleCode { get; set; }
    public bool IsCore { get; set; }
    public bool IsAvailable { get; set; }
    /// <summary>Never part of a plan (HRM, QuickBooks own app): only the super admin licenses it, by hand. The page disables it.</summary>
    public bool LicensedByHandOnly { get; set; }
    public bool IsIncluded { get; set; }
    public int DisplayOrder { get; set; }
}

public class PlanWithFeaturesModel : PlanModel
{
    public List<PlanFeatureItemModel> Features { get; set; } = new();
}

public class UpdatePlanRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    /// <summary>Null clears the price. Must be &gt;= 0.</summary>
    public decimal? MonthlyPrice { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public class PlanFeatureChange
{
    public string FeatureCode { get; set; } = string.Empty;
    public bool IsIncluded { get; set; }
}

public class UpdatePlanFeaturesRequest
{
    /// <summary>The features to change; those not listed keep their state.</summary>
    public List<PlanFeatureChange> Features { get; set; } = new();
}

// ── A51 P2 — feature status / visibility ────────────────────────────────────

public class SetFeatureStatusRequest
{
    /// <summary>Live | Beta | ComingSoon | Deprecated.</summary>
    public string Status { get; set; } = string.Empty;
}

public class SetFeatureVisibilityRequest
{
    public bool IsVisible { get; set; }
}

/// <summary>What one organization sees of a feature: the platform Status/IsVisible, unless the organization is grandfathered.</summary>
public class EffectiveFeatureStatusModel
{
    public string FeatureCode { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsVisible { get; set; }
    public bool IsGrandfathered { get; set; }
    public bool EffectiveVisible { get; set; }
    public bool EffectiveUsable { get; set; }
}

// ── A51 P7 — applying a plan to organizations ───────────────────────────────

/// <summary>Why a feature was added, removed or left alone when a plan was applied to an organization.</summary>
public class PlanApplyFeatureItem
{
    public string FeatureCode { get; set; } = string.Empty;
    public string FeatureName { get; set; } = string.Empty;
    /// <summary>Added / Removed: null. Skipped: ComingSoon, NotVisible, NotAvailable, Core, Grandfathered, LicensedByHandOnly,
    /// SwitchedOffByOrganization, NeedsModule, InUse.</summary>
    public string? Reason { get; set; }
    public string? Detail { get; set; }
}

public class PlanApplyOrgResult
{
    public Guid OrganizationId { get; set; }
    public string? OrgCode { get; set; }
    public string? OrgName { get; set; }
    public string Plan { get; set; } = string.Empty;
    public List<PlanApplyFeatureItem> Added { get; set; } = new();
    public List<PlanApplyFeatureItem> Removed { get; set; } = new();
    public List<PlanApplyFeatureItem> Skipped { get; set; } = new();
}

public class ApplyPlanToOrganizationsRequest
{
    /// <summary>addOnly (default): license newly included features, never remove. full: the same result as the organization's "Reset to Plan Defaults".</summary>
    public string? Mode { get; set; } = "addOnly";
    public bool DryRun { get; set; }
}

public class ApplyPlanToOrganizationsResult
{
    public string PlanCode { get; set; } = string.Empty;
    public string Mode { get; set; } = "addOnly";
    public bool DryRun { get; set; }
    public int OrganizationCount { get; set; }
    public int TotalAdded { get; set; }
    public int TotalRemoved { get; set; }
    public int TotalSkipped { get; set; }
    public List<PlanApplyOrgResult> Organizations { get; set; } = new();
}
