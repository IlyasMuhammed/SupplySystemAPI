using SMS.Shared.Common;

namespace SMS.Modules.Auth.Domain;

internal class UserAccount : ITenantScopedEntity
{
    public int UserID { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? MiddleName { get; set; }
    public string? LastName { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string? ZipCode { get; set; }
    public int RoleID { get; set; }
    public int? PaymentMethod { get; set; }
    public bool IsActive { get; set; }
    public bool IsDelete { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public DateTime? UpdateDate { get; set; }
    public string? AccountActivationToken { get; set; }
    public DateTime? AccountActivationTokenTime { get; set; }
    public string? PasswordResetToken { get; set; }
    public DateTime? PasswordResetTokenTime { get; set; }

    public string? ProfilePictureUrl { get; set; }

    public string? Department { get; set; }
    /// <summary>FK to Departments.DepartmentId; nullable.</summary>
    public int? DepartmentId { get; set; }
    /// <summary>Self-referential FK — direct line manager of this user.</summary>
    public int? SupervisorId { get; set; }
    public DateTime? LastLoginAt { get; set; }

    // Account lockout tracking
    public int FailedLoginAttempts { get; set; }
    public DateTime? LastFailedAt { get; set; }
    public DateTime? LockedUntil { get; set; }

    // Multi-tenancy (MT-002/MT-003). Non-nullable since MT-003's migration backfilled every
    // existing row to SCM-DEMO's org id. No FK constraint across schemas — Tenancy owns
    // Organization, nothing in Auth needs DB-level referential integrity, only the
    // application-level IOrganizationStatusService lookup at login.
    public Guid OrganizationId { get; set; }

    // Invite-to-set-password flow (MT-002) — distinct from AccountActivationToken (self-registration,
    // never lets the user set a password) and PasswordResetToken (6-digit code, assumes an
    // already-active account). Cleared once the invite is accepted.
    public string? InviteToken { get; set; }
    public DateTime? InviteTokenExpiresAt { get; set; }

    // REQ-1.x — distinguishes internal staff accounts from external (supplier-side) accounts.
    // "INTERNAL" | "EXTERNAL", following the same plain-string-with-app-level-validation
    // convention as Supplier.Status rather than a mapped enum type.
    public string SupplierType { get; set; } = "INTERNAL";
}

internal class Department : ITenantScopedEntity
{
    public int DepartmentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    /// <summary>FK to UserAccounts.UserID — the designated head of this department.</summary>
    public int? HeadUserId { get; set; }
    public Guid OrganizationId { get; set; }
}

internal class UserSession : ITenantScopedEntity
{
    public Guid Id { get; set; }
    public int UserID { get; set; }
    /// <summary>SHA-256 hex of the raw refresh token UUID — never stores the raw value.</summary>
    public string TokenHash { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid OrganizationId { get; set; }
}

internal class Permission
{
    public int PermissionID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
}

// Either a global role (IsGlobal=true, OrganizationId=null — the shared seeded catalog: System
// Admin, Procurement Manager, Requester, etc., usable/assignable by every organization) or an
// org-owned custom role (IsGlobal=false, OrganizationId set — created by that org's own Org
// Admin, invisible to and unmodifiable by any other organization). Mirrors Lookups' LookupValue
// pattern (IGloballyExemptTenantScopedEntity) rather than ITenantScopedEntity, since a global row
// has no owning org. RolePermission deliberately still does NOT implement this — it's reached only
// via a Role that's already correctly gated (see RolesController's IsGlobal write-guard), so it
// doesn't need its own filter.
internal class Role : IGloballyExemptTenantScopedEntity
{
    public int RoleID { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RoleCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsGlobal { get; set; } = true;
    public Guid? OrganizationId { get; set; }
}

internal class RolePermission
{
    public int RolePermissionID { get; set; }
    public int RoleID { get; set; }
    public int PermissionID { get; set; }
    public bool IsAllowed { get; set; }
    public Guid OrganizationId { get; set; }
}

internal class UserPermission : ITenantScopedEntity
{
    public int UserPermissionID { get; set; }
    public int UserID { get; set; }
    public int PermissionID { get; set; }
    public bool IsAllowed { get; set; }
    public Guid OrganizationId { get; set; }
}

// REQ-2.x — many-to-many User↔Supplier access mapping. Only meaningful for a
// UserAccount.SupplierType="EXTERNAL" user; restricts which suppliers that user can see once at
// least one mapping exists (see IUserSupplierAccessService for the fail-open default).
internal class UserSupplierAccess : ITenantScopedEntity
{
    public int Id { get; set; }
    public int UserID { get; set; }
    // SMS.Modules.Suppliers.Domain.Supplier.UUID — no cross-schema FK, same pattern as
    // RfqAccessLink.SupplierId in SMS.Modules.Demand.
    public Guid SupplierId { get; set; }
    public int AssignedBy { get; set; }
    public DateTime AssignedAt { get; set; }
    public Guid OrganizationId { get; set; }
}
