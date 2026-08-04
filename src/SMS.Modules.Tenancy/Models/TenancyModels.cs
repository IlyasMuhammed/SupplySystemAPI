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

// ── GET /api/tenant/current ──────────────────────────────────────────────────
// Consumed by the React sidebar on every page load to render/hide menu items — feature codes and
// permissions come straight from JWT claims (no extra query), org info from a single lookup.
public class CurrentTenantModel
{
    public Guid Id { get; set; }
    public string OrgCode { get; set; } = string.Empty;
    public string OrgName { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
    public List<string> EnabledFeatureCodes { get; set; } = new();
    public bool IsSuperAdmin { get; set; }
    public string RoleName { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
}
