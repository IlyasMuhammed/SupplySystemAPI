using SMS.Shared.Common;

namespace SMS.Modules.Onboarding.Domain;

// A50 API-CONTRACT §1 — every table of schema onboarding (CORE creates them all; SEED/TPL/DEMO build services on them).
// Org-scoped rows implement ITenantScopedEntity (tenant filter + stamp); SetupDefault is platform data (no organization).

/// <summary>D-4 — a Super Admin default: one JSON payload per (country, industry, category).</summary>
public sealed class SetupDefault : IHasModifiedAt
{
    public int    Id { get; set; }
    public Guid   Uuid { get; set; } = Guid.NewGuid();
    /// <summary>ISO alpha-2 or <see cref="SetupCodes.Global"/>.</summary>
    public string CountryCode { get; set; } = SetupCodes.Global;
    public string IndustryType { get; set; } = SetupCodes.Global;
    public string DataCategory { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string DataPayload { get; set; } = "{}";
    /// <summary>Starts at 1; +1 on every payload change (BR-50-013).</summary>
    public int    Version { get; set; } = 1;
    /// <summary>Published: only active rows take part in resolution (unpublish = deactivate).</summary>
    public bool   IsActive { get; set; } = true;
    public string? Notes { get; set; }
    /// <summary>Inserted by the startup pack seeder (vs. created by a Super Admin).</summary>
    public bool   IsSystemPack { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int?   CreatedBy { get; set; }
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public int?   ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>D-11 — one row per category per apply / skip / reset (BR-50-013).</summary>
public sealed class TenantSetupLog : ITenantScopedEntity
{
    public long   Id { get; set; }
    public Guid   OrganizationId { get; set; }
    /// <summary>The org's chosen country / industry (not the level the payload was resolved from — see ResolvedFrom*).</summary>
    public string CountryCode { get; set; } = SetupCodes.Global;
    public string IndustryType { get; set; } = SetupCodes.Global;
    public string DataCategory { get; set; } = string.Empty;
    /// <summary>The SetupDefault applied (null = none resolved). Drives the Super Admin delete guard (D-4 / task 033).</summary>
    public int?   SetupDefaultId { get; set; }
    public string? ResolvedCountryCode { get; set; }
    public string? ResolvedIndustryType { get; set; }
    public int    DefaultsVersion { get; set; }
    public int    RecordsSeeded { get; set; }
    /// <summary><see cref="SetupLogActions"/>.</summary>
    public string Action { get; set; } = SetupLogActions.Seed;
    public string? Message { get; set; }
    public DateTime AppliedAt { get; set; } = DateTime.UtcNow;
    public int?   AppliedBy { get; set; }
}

/// <summary>D-6 — the org's setup state (PK OrganizationId). Tenancy's Organizations table is not altered.</summary>
public sealed class OrganizationSetup : ITenantScopedEntity, IHasModifiedAt
{
    public Guid   OrganizationId { get; set; }
    public string? CountryCode { get; set; }
    public string? IndustryType { get; set; }
    /// <summary><see cref="SetupStatuses"/>.</summary>
    public string SetupStatus { get; set; } = SetupStatuses.NotStarted;
    public int    SetupStep { get; set; }
    /// <summary>Per-step drafts: { "1": {...}, "2": {...} }.</summary>
    public string? StepDataJson { get; set; }
    /// <summary>The payloads the last apply used, per category (admin edits included).</summary>
    public string? DefaultsSnapshotJson { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int?   CompletedBy { get; set; }
    /// <summary>"LEGACY" for organizations that existed before A50 (migration backfill).</summary>
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>D-5 — provenance of a seeded (or reused) record, instead of IsSeeded columns on the owners' tables.</summary>
public sealed class SeededRecord : ITenantScopedEntity
{
    public long   Id { get; set; }
    public Guid   OrganizationId { get; set; }
    public string Category { get; set; } = string.Empty;
    public string NaturalKey { get; set; } = string.Empty;
    /// <summary>The owner's uuid or id, as a string.</summary>
    public string EntityRef { get; set; } = string.Empty;
    public int    Version { get; set; }
    /// <summary>Hash of the seeded values; differs from the seeder's current one = "modified".</summary>
    public string Fingerprint { get; set; } = string.Empty;
    /// <summary>An existing record was reused (e.g. a global role) rather than created.</summary>
    public bool   Reused { get; set; }
    public int    ResetCount { get; set; }
    public DateTime SeededAt { get; set; } = DateTime.UtcNow;
}

/// <summary>D-28 — the org's onboarding checklist (PK OrganizationId).</summary>
public sealed class OnboardingChecklist : ITenantScopedEntity
{
    public Guid   OrganizationId { get; set; }
    public bool   Dismissed { get; set; }
    public DateTime? DismissedAt { get; set; }
    public int?   DismissedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<OnboardingChecklistStep> Steps { get; set; } = new();
}

public sealed class OnboardingChecklistStep : ITenantScopedEntity
{
    public int    Id { get; set; }
    public Guid   OrganizationId { get; set; }
    /// <summary><see cref="SMS.Shared.Onboarding.OnboardingStepCodes"/>.</summary>
    public string StepCode { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Link { get; set; }
    public string? RequiresModule { get; set; }
    public int    SortOrder { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int?   CompletedBy { get; set; }
    public bool   AutoCompleted { get; set; }
}

/// <summary>D-25 — a document (PDF) template / branding per org and type.</summary>
public sealed class DocumentTemplate : ITenantScopedEntity, IHasModifiedAt
{
    public int    Id { get; set; }
    public Guid   Uuid { get; set; } = Guid.NewGuid();
    public Guid   OrganizationId { get; set; }
    /// <summary><see cref="SMS.Shared.Onboarding.DocumentTemplateTypes"/>.</summary>
    public string TemplateType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? HeaderHtml { get; set; }
    public string? BodyHtml { get; set; }
    public string? FooterHtml { get; set; }
    /// <summary>Attachment uploaded with interface code DOC_TEMPLATE_LOGO.</summary>
    public Guid?  LogoAttachmentUuid { get; set; }
    /// <summary>"#RRGGBB".</summary>
    public string? PrimaryColor { get; set; }
    public bool   ShowBankDetails { get; set; }
    public string? BankDetailsText { get; set; }
    public string? TermsAndConditions { get; set; }
    public bool   IsDefault { get; set; }
    public bool   IsSeeded { get; set; }
    public bool   IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int?   CreatedBy { get; set; }
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public int?   ModifiedBy { get; set; }
}

/// <summary>D-26 — an email template per org and type.</summary>
public sealed class EmailTemplate : ITenantScopedEntity, IHasModifiedAt
{
    public int    Id { get; set; }
    public Guid   Uuid { get; set; } = Guid.NewGuid();
    public Guid   OrganizationId { get; set; }
    /// <summary><see cref="SMS.Shared.Onboarding.EmailTemplateTypes"/>.</summary>
    public string TemplateType { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SubjectTemplate { get; set; } = string.Empty;
    public string BodyHtml { get; set; } = string.Empty;
    public bool   IsDefault { get; set; }
    public bool   IsSeeded { get; set; }
    public bool   IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int?   CreatedBy { get; set; }
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public int?   ModifiedBy { get; set; }
}

/// <summary>D-27 — a demo record created by a sample-data contributor (no IsDemo columns).</summary>
public sealed class DemoRecord : ITenantScopedEntity
{
    public long   Id { get; set; }
    public Guid   OrganizationId { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityRef { get; set; } = string.Empty;
    /// <summary>The contributor's type name (cleanup routes each ref back to it).</summary>
    public string? Contributor { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
