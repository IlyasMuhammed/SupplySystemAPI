namespace SMS.Modules.Tenancy.Domain;

// Plan tiers: BASIC | STANDARD | ENTERPRISE — validated as a string, not an enum, so new tiers
// can be added via seed data alone (matches PurchaseOrder.Status / Grn.Status string-status
// convention used throughout the rest of the codebase, no new enum-mapping precedent needed).
internal class Organization
{
    public Guid Id { get; set; }
    public string OrgCode { get; set; } = string.Empty;
    public string OrgName { get; set; } = string.Empty;
    public string Plan { get; set; } = "BASIC";
    public bool IsActive { get; set; } = true;
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

// Seed-managed catalog of every module/screen/capability that can be toggled per organization.
// No admin CRUD API touches this table — the in-code catalog (see TenancyDataSeeder) is the
// single source of truth, reconciled into this table on every startup.
internal class FeatureDefinition
{
    public Guid Id { get; set; }
    public string FeatureCode { get; set; } = string.Empty;
    public string FeatureName { get; set; } = string.Empty;
    // MODULE | SCREEN | FEATURE — derived from the FeatureCode prefix, stored for fast grouping.
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    // Core features can never be disabled for any organization — enforced in TenancyService,
    // not overridable per-org (see OrganizationFeature — deliberately has no IsCore column).
    public bool IsCore { get; set; }
    public int DisplayOrder { get; set; }
}

// Per-organization toggle state for one FeatureDefinition. Rows are created in bulk when an org
// is created (cloned from its plan's PlanFeatureTemplate) and backfilled on every startup for any
// FeatureDefinition added since — see TenancyDataSeeder.BackfillOrganizationFeaturesAsync.
internal class OrganizationFeature
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid FeatureDefinitionId { get; set; }
    public bool IsEnabled { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

// Seed-managed default feature set per plan tier — drives the initial OrganizationFeatures rows
// cloned at org-creation time, and the explicit "reset to plan defaults" action.
internal class PlanFeatureTemplate
{
    public Guid Id { get; set; }
    public string Plan { get; set; } = string.Empty;
    public Guid FeatureDefinitionId { get; set; }
    public bool IsEnabledByDefault { get; set; }
}

// MT-007 — the authoritative record of platform Super Admins. Deliberately has no OrganizationId:
// Super Admin is outside org scope by definition, not a role within any one tenant. UserId is a
// plain int FK to auth.UserAccounts.UserID with no DB-level cross-schema constraint (same
// reasoning as UserAccount.OrganizationId — Tenancy and Auth don't share a physical FK).
internal class SuperAdminUser
{
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; }
}