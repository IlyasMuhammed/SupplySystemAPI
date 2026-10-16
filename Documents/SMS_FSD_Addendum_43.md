# SMS — Functional Specification Document — Addendum 43

## Multi-Tenant Administration & Tenant Isolation

| Field              | Value                                                  |
|--------------------|--------------------------------------------------------|
| **Document ID**    | SMS-FSD-ADD-043                                        |
| **Version**        | 1.0                                                   |
| **Date**           | 2026-10-13                                             |
| **Status**         | Draft — Pending Approval                               |
| **Author**         | Muhammad Ilyas / Claude (AI-Assisted)                  |
| **Depends On**     | Core Identity (ASP.NET Core Identity), SMS Base Schema |
| **Deferred Items** | Tenant Billing Integration, Usage Metering, Custom Domain Mapping, Tenant Data Purge, Super Admin Tenant Impersonation — see §13 |

---

## 1. Objective

Define the multi-tenant administration layer for SMS, covering:

1. **Super Admin Role** — a platform-level administrator who manages tenants but has **zero visibility** into tenant business data (inventory, sales, finance, HR, CRM, or any module).
2. **Tenant Data Isolation** — every tenant sees only its own data; cross-tenant access is prevented at the database, application, and API layers.
3. **Tenant Lifecycle Management** — super admin can activate and deactivate tenants.
4. **Read-Only Mode** — when a tenant is inactive, its users can still log in and view their data, but **no transaction of any kind** can be performed.

---

## 2. Background & Context

SMS operates as a multi-tenant SaaS application using a **shared database with `OrgId` discriminator** pattern. Every business entity implements the `IMultiTenant` interface carrying an `OrgId` foreign key to the `Organizations` table. This addendum formalizes the administration, enforcement, and lifecycle rules around that tenancy model.

### Current State (Pre-Addendum 43)

| Aspect | Current Status |
|--------|----------------|
| Multi-tenant entities | `IMultiTenant` interface exists; entities carry `OrgId` |
| Tenant resolution | OrgId extracted from JWT claims |
| Query filtering | Partial — some queries filter manually |
| Super admin | Not implemented |
| Tenant status management | Not implemented |
| Read-only mode | Not implemented |
| Global query filters | Not applied systematically |
| Cross-tenant write prevention | Not enforced at SaveChanges level |

### Target State (Post-Addendum 43)

| Aspect | Target |
|--------|--------|
| Global query filters | Every `IMultiTenant` entity has EF Core `HasQueryFilter` |
| SaveChanges interceptor | Auto-sets OrgId on insert; blocks cross-tenant writes |
| Tenant context middleware | Resolves OrgId from JWT on every request |
| Super admin portal | Separate UI with tenant management only |
| Tenant status | Active / Inactive / PendingSetup lifecycle |
| Read-only middleware | Blocks writes for inactive tenants; allows reads |
| Audit trail | Every status change logged with reason and actor |

---

## 3. Definitions

| Term | Meaning |
|------|---------|
| **Super Admin** | Platform-level administrator. Manages tenants. Has **no access** to tenant business modules or data. |
| **Tenant Admin** | Organization-level administrator. Manages users, settings, and operations **within** their own tenant. |
| **Tenant** | An `Organization` record. The isolation boundary for all business data. Identified by `OrgId`. |
| **Tenant Context** | The resolved `OrgId` for the current HTTP request, derived from JWT claims. |
| **Global Query Filter** | EF Core mechanism that automatically appends `WHERE OrgId = @currentOrgId` to every LINQ query on multi-tenant entities. |
| **Read-Only Mode** | Tenant state where authenticated users can view data (GET) but all write operations (POST/PUT/PATCH/DELETE) are blocked with HTTP 403. |
| **Platform Schema** | New database schema `platform` housing super-admin-level objects that exist above the tenant boundary. |

---

## 4. Change Items Overview

| ID | Change Item | Schema | New Entities |
|----|-------------|--------|-------------|
| C1 | Platform Schema & Super Admin Role | `platform` | `SuperAdmins` |
| C2 | Organization (Tenant) Entity Enhancements | `dbo` | — (alter existing) |
| C3 | Tenant Status Management & Audit Log | `platform` | `TenantStatusLogs` |
| C4 | Tenant Data Isolation Enforcement | Cross-cutting | — (middleware + interceptor) |
| C5 | Read-Only Mode for Inactive Tenants | Cross-cutting | — (middleware) |
| C6 | Super Admin Tenant Management API | — | — (API layer) |
| C7 | Super Admin Portal UI | — | — (React SPA) |
| C8 | Inactive Tenant UX (Tenant-Side) | — | — (React components) |

---

## 5. Change Item Details

### C1 — Platform Schema & Super Admin Role Infrastructure

#### 5.1.1 Database Schema

Create a new `platform` schema to house objects that exist above the tenant level. Nothing in this schema carries an `OrgId` column.

```sql
CREATE SCHEMA [platform] AUTHORIZATION [dbo];
```

#### 5.1.2 Entity: `platform.SuperAdmins`

| Column | Type | Constraints | Description |
|--------|------|-------------|-------------|
| `Id` | `int` | PK, Identity | Auto-increment primary key |
| `UserId` | `nvarchar(450)` | FK → `AspNetUsers.Id`, Unique, Not Null | Link to ASP.NET Identity user record |
| `DisplayName` | `nvarchar(200)` | Not Null | Display name in platform portal |
| `Email` | `nvarchar(256)` | Not Null | Contact email address |
| `Phone` | `nvarchar(50)` | Nullable | Contact phone number |
| `IsActive` | `bit` | Not Null, Default `1` | Whether this super admin account is active |
| `LastLoginAt` | `datetimeoffset` | Nullable | Timestamp of last successful login |
| `CreatedAt` | `datetimeoffset` | Not Null, Default `SYSDATETIMEOFFSET()` | Record creation timestamp |
| `CreatedBy` | `nvarchar(450)` | Not Null | UserId of creator |
| `UpdatedAt` | `datetimeoffset` | Nullable | Last update timestamp |
| `UpdatedBy` | `nvarchar(450)` | Nullable | UserId of last updater |

**Indexes:**

| Index Name | Columns | Type |
|------------|---------|------|
| `IX_SuperAdmins_UserId` | `UserId` | Unique |
| `IX_SuperAdmins_Email` | `Email` | Non-unique |
| `IX_SuperAdmins_IsActive` | `IsActive` | Filtered (`WHERE IsActive = 1`) |

#### 5.1.3 Super Admin Identity Design

```
┌─────────────────────────────────────────────────────────┐
│                  AspNetUsers                            │
│  ┌──────────────────┐  ┌──────────────────────────────┐│
│  │ Super Admin User  │  │ Tenant User                  ││
│  │ OrgId = NULL      │  │ OrgId = 42                   ││
│  │ Role: SuperAdmin  │  │ Role: TenantAdmin / User     ││
│  │ JWT: no org_id    │  │ JWT: org_id = 42             ││
│  │ claim             │  │ claim                        ││
│  │ account_type:     │  │ account_type:                ││
│  │   "platform"      │  │   "tenant"                   ││
│  └──────────────────┘  └──────────────────────────────┘│
└─────────────────────────────────────────────────────────┘
```

**Key Design Decisions:**

1. Super admin users exist in `AspNetUsers` but with `OrgId = NULL` — they are platform-level, not attached to any tenant.
2. A dedicated `SuperAdmin` role in `AspNetRoles` distinguishes them from tenant users.
3. The JWT for a super admin contains `role: "SuperAdmin"` and `account_type: "platform"` but **no** `org_id` claim.
4. Super admin users CANNOT be assigned to any organization.
5. Tenant users CANNOT be elevated to super admin — separate provisioning pathway.
6. Super admin accounts are created by other super admins or via database seed.

#### 5.1.4 Authorization Policies

```csharp
// Registration in Program.cs / Startup
services.AddAuthorization(options =>
{
    // Platform-level operations — super admin only
    options.AddPolicy("PlatformAdmin", policy =>
        policy.RequireRole("SuperAdmin")
              .RequireClaim("account_type", "platform"));

    // Tenant-level operations — any authenticated tenant user
    options.AddPolicy("TenantUser", policy =>
        policy.RequireClaim("account_type", "tenant")
              .RequireClaim("org_id"));

    // Tenant admin operations — tenant admin role within a tenant
    options.AddPolicy("TenantAdmin", policy =>
        policy.RequireRole("TenantAdmin", "Admin")
              .RequireClaim("account_type", "tenant")
              .RequireClaim("org_id"));
});
```

#### 5.1.5 Route Separation

| Route Prefix | Access | Purpose |
|-------------|--------|---------|
| `/api/platform/*` | `PlatformAdmin` policy only | Super admin operations |
| `/api/*` (all other) | `TenantUser` policy | Tenant business operations |
| `/api/auth/*` | Anonymous / Authenticated | Login, logout, token refresh |

The authorization middleware enforces:
- Super admin requests to `/api/` (non-platform) → **403 Forbidden** with `PLATFORM_USER_RESTRICTED`
- Tenant user requests to `/api/platform/*` → **403 Forbidden** with `TENANT_USER_RESTRICTED`

---

### C2 — Organization (Tenant) Entity Enhancements

#### 5.2.1 New Columns on `dbo.Organizations`

| Column | Type | Constraints | Description |
|--------|------|-------------|-------------|
| `Status` | `nvarchar(20)` | Not Null, Default `'Active'` | Tenant status: `Active`, `Inactive`, `PendingSetup` |
| `Subdomain` | `nvarchar(63)` | Unique (filtered), Nullable | URL subdomain (e.g., `acme` for `acme.sms-app.com`) |
| `ContactName` | `nvarchar(200)` | Nullable | Primary contact person |
| `ContactEmail` | `nvarchar(256)` | Nullable | Primary contact email |
| `ContactPhone` | `nvarchar(50)` | Nullable | Primary contact phone |
| `LogoUrl` | `nvarchar(500)` | Nullable | Organization logo URL |
| `Industry` | `nvarchar(100)` | Nullable | Industry / vertical classification |
| `Country` | `nvarchar(100)` | Nullable | Country of operation |
| `Timezone` | `nvarchar(100)` | Nullable | Default timezone (IANA format, e.g., `Asia/Dubai`) |
| `MaxUsers` | `int` | Not Null, Default `0` | Maximum allowed users (0 = unlimited) |
| `CurrentUserCount` | `int` | Not Null, Default `0` | Denormalized count of active users |
| `SubscriptionPlan` | `nvarchar(50)` | Nullable | Plan tier: `Free`, `Starter`, `Professional`, `Enterprise` |
| `SubscriptionStartDate` | `datetimeoffset` | Nullable | When subscription began |
| `SubscriptionEndDate` | `datetimeoffset` | Nullable | When subscription expires |
| `ActivatedAt` | `datetimeoffset` | Nullable | When tenant was first activated |
| `DeactivatedAt` | `datetimeoffset` | Nullable | When tenant was last deactivated |
| `DeactivationReason` | `nvarchar(500)` | Nullable | Reason for deactivation (required when deactivating) |

#### 5.2.2 TenantStatus Values

| Status | Description | Allowed Operations |
|--------|-------------|-------------------|
| `Active` | Fully operational | All read + write operations |
| `Inactive` | Read-only mode | Login + read-only; all writes blocked |
| `PendingSetup` | Newly provisioned, setup not complete | Setup wizard + initial configuration only |

#### 5.2.3 New Indexes

| Index Name | Columns | Notes |
|------------|---------|-------|
| `IX_Organizations_Status` | `Status` | Filter tenants by status |
| `IX_Organizations_Subdomain` | `Subdomain` | Unique, filtered `WHERE Subdomain IS NOT NULL` |
| `IX_Organizations_SubscriptionPlan` | `SubscriptionPlan` | Filter by plan tier |
| `IX_Organizations_Status_SubscriptionPlan` | `Status, SubscriptionPlan` | Dashboard queries |

#### 5.2.4 C# Entity Update

```csharp
public class Organization : BaseAuditableEntity
{
    public int Id { get; set; }
    public string Name { get; set; }

    // --- New Tenant Management Fields ---
    public string Status { get; set; } = TenantStatus.Active;
    public string? Subdomain { get; set; }
    public string? ContactName { get; set; }
    public string? ContactEmail { get; set; }
    public string? ContactPhone { get; set; }
    public string? LogoUrl { get; set; }
    public string? Industry { get; set; }
    public string? Country { get; set; }
    public string? Timezone { get; set; }
    public int MaxUsers { get; set; }
    public int CurrentUserCount { get; set; }
    public string? SubscriptionPlan { get; set; }
    public DateTimeOffset? SubscriptionStartDate { get; set; }
    public DateTimeOffset? SubscriptionEndDate { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
    public string? DeactivationReason { get; set; }

    // Navigation
    public ICollection<TenantStatusLog> StatusLogs { get; set; } = new List<TenantStatusLog>();
}

public static class TenantStatus
{
    public const string Active = "Active";
    public const string Inactive = "Inactive";
    public const string PendingSetup = "PendingSetup";

    public static readonly string[] All = { Active, Inactive, PendingSetup };
}
```

---

### C3 — Tenant Status Management & Audit Log

#### 5.3.1 Entity: `platform.TenantStatusLogs`

Every status change is recorded with the actor, timestamp, reason, and previous/new state.

| Column | Type | Constraints | Description |
|--------|------|-------------|-------------|
| `Id` | `bigint` | PK, Identity | Auto-increment primary key |
| `OrganizationId` | `int` | FK → `Organizations.Id`, Not Null | Tenant affected |
| `PreviousStatus` | `nvarchar(20)` | Not Null | Status before the change |
| `NewStatus` | `nvarchar(20)` | Not Null | Status after the change |
| `Reason` | `nvarchar(500)` | Nullable | Reason for the status change |
| `Notes` | `nvarchar(2000)` | Nullable | Additional notes from super admin |
| `ChangedByUserId` | `nvarchar(450)` | FK → `AspNetUsers.Id`, Not Null | Super admin who made the change |
| `ChangedAt` | `datetimeoffset` | Not Null, Default `SYSDATETIMEOFFSET()` | When the change occurred |
| `IpAddress` | `nvarchar(45)` | Nullable | IP address of the request |

**Indexes:**

| Index Name | Columns |
|------------|---------|
| `IX_TenantStatusLogs_OrgId_ChangedAt` | `OrganizationId, ChangedAt DESC` |
| `IX_TenantStatusLogs_ChangedBy` | `ChangedByUserId` |

#### 5.3.2 Status Transition Rules

```
              ┌──────────────┐
              │ PendingSetup │
              └──────┬───────┘
                     │ Setup Complete
                     ▼
              ┌──────────────┐  Deactivate   ┌──────────────┐
              │    Active    │ ─────────────► │   Inactive   │
              │              │ ◄───────────── │              │
              └──────────────┘  Reactivate    └──────────────┘
```

| From | To | Allowed | Who |
|------|----|---------|-----|
| `PendingSetup` | `Active` | Yes | System (on setup completion) or Super Admin |
| `PendingSetup` | `Inactive` | Yes | Super Admin |
| `Active` | `Inactive` | Yes | Super Admin (requires reason) |
| `Inactive` | `Active` | Yes | Super Admin |
| `Active` | `PendingSetup` | **No** | Cannot revert to setup |
| `Inactive` | `PendingSetup` | **No** | Cannot revert to setup |

#### 5.3.3 Side Effects of Status Changes

**Deactivation (Active → Inactive):**

| # | Side Effect | Mechanism |
|---|-------------|-----------|
| 1 | Set `DeactivatedAt` and `DeactivationReason` on Organization | Direct update |
| 2 | All active user sessions flagged for status check on next request | Response header `X-Tenant-Status: Inactive` |
| 3 | Background jobs (Hangfire) for this tenant are paused | `TenantDeactivatedEvent` handler calls Hangfire recurring job manager |
| 4 | Scheduled tasks are suspended | Same event handler |
| 5 | API keys for the tenant return `403 TENANT_INACTIVE` | Read-only middleware intercepts |
| 6 | Webhook deliveries are paused | Same event handler |
| 7 | Tenant admin receives email notification | `TenantDeactivatedEvent` handler |
| 8 | Status log entry created | Audit service |
| 9 | Platform audit log entry created | Audit service |

**Reactivation (Inactive → Active):**

| # | Side Effect | Mechanism |
|---|-------------|-----------|
| 1 | Clear `DeactivatedAt`; update `ActivatedAt` | Direct update |
| 2 | Read-only restrictions lifted immediately | Middleware reads live status |
| 3 | Background jobs resume | `TenantActivatedEvent` handler |
| 4 | Scheduled tasks resume | Same event handler |
| 5 | API keys become functional | Middleware reads live status |
| 6 | Webhook deliveries resume | Same event handler |
| 7 | Tenant admin receives email notification | `TenantActivatedEvent` handler |
| 8 | Status log entry created | Audit service |

#### 5.3.4 Entity: `platform.PlatformAuditLogs`

General-purpose audit log for all super admin actions.

| Column | Type | Constraints | Description |
|--------|------|-------------|-------------|
| `Id` | `bigint` | PK, Identity | Auto-increment primary key |
| `Action` | `nvarchar(50)` | Not Null | Action slug (see table below) |
| `EntityType` | `nvarchar(100)` | Not Null | Entity type affected |
| `EntityId` | `nvarchar(50)` | Not Null | Entity ID affected |
| `Details` | `nvarchar(max)` | Nullable | JSON payload with change details |
| `PerformedByUserId` | `nvarchar(450)` | FK → `AspNetUsers.Id`, Not Null | Who performed the action |
| `PerformedAt` | `datetimeoffset` | Not Null, Default `SYSDATETIMEOFFSET()` | When the action was performed |
| `IpAddress` | `nvarchar(45)` | Nullable | IP address of the request |

**Audit Action Slugs:**

| Action | EntityType | When |
|--------|-----------|------|
| `TenantCreated` | `Organization` | New tenant provisioned |
| `TenantUpdated` | `Organization` | Tenant profile edited |
| `TenantActivated` | `Organization` | Status → Active |
| `TenantDeactivated` | `Organization` | Status → Inactive |
| `TenantPlanChanged` | `Organization` | Subscription plan modified |
| `SuperAdminCreated` | `SuperAdmin` | New super admin provisioned |
| `SuperAdminUpdated` | `SuperAdmin` | Super admin profile edited |
| `SuperAdminDeactivated` | `SuperAdmin` | Super admin account disabled |

**Indexes:**

| Index Name | Columns |
|------------|---------|
| `IX_PlatformAuditLogs_Action_PerformedAt` | `Action, PerformedAt DESC` |
| `IX_PlatformAuditLogs_EntityType_EntityId` | `EntityType, EntityId` |
| `IX_PlatformAuditLogs_PerformedBy` | `PerformedByUserId` |

---

### C4 — Tenant Data Isolation Enforcement

This change item establishes systematic, defence-in-depth tenant isolation across four layers.

#### 5.4.1 Layer 1 — Tenant Context Service (Scoped)

```csharp
public interface ITenantContext
{
    int? CurrentOrgId { get; }
    string? CurrentTenantStatus { get; }
    bool IsSuperAdmin { get; }
    bool IsTenantActive { get; }
    bool IsReadOnlyMode { get; }
}

public class TenantContext : ITenantContext
{
    public int? CurrentOrgId { get; private set; }
    public string? CurrentTenantStatus { get; private set; }
    public bool IsSuperAdmin { get; private set; }
    public bool IsTenantActive => CurrentTenantStatus == TenantStatus.Active;
    public bool IsReadOnlyMode => CurrentTenantStatus == TenantStatus.Inactive;

    public void SetFromClaims(ClaimsPrincipal user)
    {
        IsSuperAdmin = user.IsInRole("SuperAdmin");

        if (!IsSuperAdmin)
        {
            var orgClaim = user.FindFirst("org_id")?.Value;
            CurrentOrgId = orgClaim != null ? int.Parse(orgClaim) : null;
        }
    }

    public void SetTenantStatus(string status)
    {
        CurrentTenantStatus = status;
    }
}
```

**Registration:** `services.AddScoped<ITenantContext, TenantContext>();`

**Critical:** The tenant context MUST be scoped (per-request). A singleton would capture the first request's tenant and leak data across all subsequent requests.

#### 5.4.2 Layer 2 — Tenant Context Middleware

Runs early in the pipeline, after authentication but before authorization and business logic.

```csharp
public class TenantContextMiddleware
{
    private readonly RequestDelegate _next;

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantCtx,
        ApplicationDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var tc = (TenantContext)tenantCtx;
            tc.SetFromClaims(context.User);

            if (!tenantCtx.IsSuperAdmin && tenantCtx.CurrentOrgId.HasValue)
            {
                // Load tenant status (cached per request)
                var org = await db.Organizations
                    .AsNoTracking()
                    .Where(o => o.Id == tenantCtx.CurrentOrgId.Value)
                    .Select(o => new { o.Status })
                    .FirstOrDefaultAsync();

                if (org == null)
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "TENANT_NOT_FOUND",
                        message = "Your organization was not found."
                    });
                    return;
                }

                tc.SetTenantStatus(org.Status);

                // Set response headers for frontend
                context.Response.Headers["X-Tenant-Status"] = org.Status;
                if (org.Status == TenantStatus.Inactive)
                    context.Response.Headers["X-Tenant-ReadOnly"] = "true";
            }
        }

        await _next(context);
    }
}
```

**Pipeline Order:**

```
UseAuthentication()
  → UseTenantContext()        // Resolves OrgId + status
    → UseTenantStatusCheck()  // Enforces read-only mode (C5)
      → UseAuthorization()
        → UseEndpoints()
```

#### 5.4.3 Layer 3 — EF Core Global Query Filters

Applied in `DbContext.OnModelCreating` to every entity implementing `IMultiTenant`.

```csharp
public interface IMultiTenant
{
    int OrgId { get; set; }
}

protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    foreach (var entityType in modelBuilder.Model.GetEntityTypes())
    {
        if (typeof(IMultiTenant).IsAssignableFrom(entityType.ClrType))
        {
            var method = typeof(ApplicationDbContext)
                .GetMethod(nameof(ApplyTenantFilter),
                    BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(entityType.ClrType);

            method.Invoke(null, new object[] { modelBuilder, this });
        }
    }
}

private static void ApplyTenantFilter<TEntity>(
    ModelBuilder builder, ApplicationDbContext context)
    where TEntity : class, IMultiTenant
{
    builder.Entity<TEntity>().HasQueryFilter(
        e => e.OrgId == context._tenantContext.CurrentOrgId);
}
```

**Important:** The filter references the `DbContext` instance's `_tenantContext` property (resolved per-scope), not a captured constant. EF Core caches the compiled model, so a captured constant would freeze the filter to one tenant.

**Super Admin Bypass:**

Super admin services use a separate `PlatformDbContext` with **no** `HasQueryFilter` calls. This context is registered only for platform-scoped services and is never injected into tenant-facing controllers.

```csharp
// Only for super admin services — NO global query filters
public class PlatformDbContext : DbContext
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<SuperAdmin> SuperAdmins => Set<SuperAdmin>();
    public DbSet<TenantStatusLog> TenantStatusLogs => Set<TenantStatusLog>();
    public DbSet<PlatformAuditLog> PlatformAuditLogs => Set<PlatformAuditLog>();
    // No IMultiTenant entities are exposed here
}
```

#### 5.4.4 Layer 4 — SaveChanges Interceptor

Enforces tenant context on every write operation. Prevents accidental or malicious cross-tenant data insertion.

```csharp
public override async Task<int> SaveChangesAsync(CancellationToken ct = default)
{
    var tenantCtx = _tenantContext;

    foreach (var entry in ChangeTracker.Entries<IMultiTenant>())
    {
        switch (entry.State)
        {
            case EntityState.Added:
                if (!tenantCtx.CurrentOrgId.HasValue)
                    throw new TenantContextException(
                        "Cannot insert multi-tenant entity without a tenant context.");
                entry.Entity.OrgId = tenantCtx.CurrentOrgId.Value;
                break;

            case EntityState.Modified:
                if (entry.Entity.OrgId != tenantCtx.CurrentOrgId)
                    throw new CrossTenantAccessException(
                        $"Cross-tenant write blocked. Entity OrgId={entry.Entity.OrgId}, " +
                        $"Context OrgId={tenantCtx.CurrentOrgId}.");
                // Prevent OrgId from being changed
                entry.Property(nameof(IMultiTenant.OrgId)).IsModified = false;
                break;

            case EntityState.Deleted:
                if (entry.Entity.OrgId != tenantCtx.CurrentOrgId)
                    throw new CrossTenantAccessException(
                        "Cross-tenant delete blocked.");
                break;
        }
    }

    return await base.SaveChangesAsync(ct);
}
```

#### 5.4.5 Layer Summary — Defence in Depth

| Layer | What It Protects | Bypass Risk |
|-------|-----------------|-------------|
| **Tenant Context Middleware** | Sets OrgId from JWT; no OrgId → no tenant operations | None — runs before all business logic |
| **EF Core Global Query Filter** | All LINQ queries automatically scoped to current tenant | Raw SQL (`FromSqlRaw`) bypasses it — see guard below |
| **SaveChanges Interceptor** | Insert/Update/Delete verified against current tenant | None — runs on every `SaveChanges` call |
| **API Route Guard** | Path params with entity IDs verified against tenant | Application-level checks in service layer |

**Raw SQL Guard:**

Every stored procedure and `FromSqlRaw` / `ExecuteSqlRaw` call MUST include `OrgId = @orgId` as a parameter. Code review checklist item: "Does this raw SQL include tenant filtering?"

#### 5.4.6 Index Strategy

Every table with an `OrgId` column must have it as the **leading column** in its primary query indexes for optimal performance:

```
Pattern: IX_TableName_OrgId_<BusinessKey>

Examples:
  IX_ChartOfAccounts_OrgId_AccountCode  ON (OrgId, AccountCode)
  IX_SalesOrders_OrgId_OrderDate        ON (OrgId, OrderDate DESC)
  IX_Products_OrgId_Sku                 ON (OrgId, Sku)
```

---

### C5 — Read-Only Mode for Inactive Tenants

#### 5.5.1 Tenant Status Middleware

Runs after `TenantContextMiddleware` and before authorization. Intercepts write requests for inactive tenants.

```csharp
public class TenantStatusMiddleware
{
    private readonly RequestDelegate _next;
    private static readonly HashSet<string> ReadOnlyMethods =
        new() { "GET", "HEAD", "OPTIONS" };

    public async Task InvokeAsync(HttpContext context, ITenantContext tenantCtx)
    {
        // Super admin bypasses — they don't belong to a tenant
        if (tenantCtx.IsSuperAdmin)
        {
            await _next(context);
            return;
        }

        // Tenant is active — proceed normally
        if (tenantCtx.IsTenantActive || tenantCtx.CurrentOrgId == null)
        {
            await _next(context);
            return;
        }

        // Tenant is inactive — enforce read-only
        var method = context.Request.Method.ToUpperInvariant();
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? "";

        // Allow exempt endpoints regardless of method
        if (IsExemptEndpoint(path))
        {
            await _next(context);
            return;
        }

        // Block all non-read methods
        if (!ReadOnlyMethods.Contains(method))
        {
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "TENANT_INACTIVE",
                message = "Your organization is currently inactive. " +
                          "You can view your data but cannot make changes. " +
                          "Contact your administrator for assistance.",
                readOnly = true,
                tenantStatus = "Inactive"
            });
            return;
        }

        // Read methods proceed
        await _next(context);
    }

    private static bool IsExemptEndpoint(string path)
    {
        return path.StartsWith("/api/auth/") ||
               path.StartsWith("/api/users/me/password") ||
               path == "/api/users/me/profile" ||
               path.StartsWith("/api/export/") ||
               path == "/api/tenant/status" ||
               path.StartsWith("/api/notifications");
    }
}
```

#### 5.5.2 Exempt Endpoints

Certain operations must remain functional even when the tenant is inactive.

| Endpoint Pattern | Allowed Methods | Reason |
|-----------------|----------------|--------|
| `/api/auth/*` | All | Login, logout, token refresh must work |
| `/api/users/me/password` | PUT | Users should be able to change their password |
| `/api/users/me/profile` | GET | Users can view their own profile |
| `/api/export/*` | GET | **Critical:** Users must be able to export/download their own data |
| `/api/tenant/status` | GET | Check current tenant status |
| `/api/notifications/*` | GET | Read existing notifications |
| `/api/platform/*` | All | Super admin endpoints (handled separately) |

#### 5.5.3 Error Response Format

All blocked write attempts return a consistent JSON error:

```json
{
  "error": "TENANT_INACTIVE",
  "message": "Your organization is currently inactive. You can view your data but cannot make changes. Contact your administrator for assistance.",
  "readOnly": true,
  "tenantStatus": "Inactive"
}
```

HTTP Status: **403 Forbidden**

#### 5.5.4 Response Headers

Every response for an inactive tenant includes:

```
X-Tenant-Status: Inactive
X-Tenant-ReadOnly: true
```

The frontend React app reads these headers (via Axios response interceptor) to enforce client-side UI restrictions.

#### 5.5.5 Background Job Handling

When a tenant is deactivated, the `TenantDeactivatedEvent` handler:

1. Queries all Hangfire recurring jobs associated with the tenant's `OrgId`
2. Sets them to a **paused** state (not deleted)
3. Records the paused job IDs for restoration

When reactivated, the `TenantActivatedEvent` handler:

1. Restores all paused recurring jobs
2. Triggers any jobs that were scheduled to run during the inactive period (if applicable)

#### 5.5.6 PendingSetup Behavior

Tenants in `PendingSetup` status follow a separate restriction:

- **Allowed:** Setup wizard endpoints, initial configuration, CoA template selection
- **Blocked:** All other business endpoints
- The setup wizard guide (see Addendum 42, C9) drives the configuration process
- Once all mandatory setup steps are complete, status transitions to `Active`

---

### C6 — Super Admin Tenant Management API

All endpoints require the `PlatformAdmin` authorization policy.

#### 5.6.1 Tenant Management Endpoints

| # | Method | Endpoint | Description | Request Body | Response |
|---|--------|----------|-------------|-------------|----------|
| 1 | `GET` | `/api/platform/tenants` | List tenants (paginated) | — | `PagedResult<TenantListDto>` |
| 2 | `POST` | `/api/platform/tenants` | Create new tenant | `CreateTenantRequest` | `TenantDetailDto` (201) |
| 3 | `GET` | `/api/platform/tenants/{id}` | Get tenant details | — | `TenantDetailDto` |
| 4 | `PUT` | `/api/platform/tenants/{id}` | Update tenant profile | `UpdateTenantRequest` | `TenantDetailDto` |
| 5 | `PATCH` | `/api/platform/tenants/{id}/activate` | Activate tenant | `ActivateTenantRequest` | `TenantDetailDto` |
| 6 | `PATCH` | `/api/platform/tenants/{id}/deactivate` | Deactivate tenant | `DeactivateTenantRequest` | `TenantDetailDto` |
| 7 | `GET` | `/api/platform/tenants/{id}/status-history` | Status change history | — | `List<TenantStatusLogDto>` |
| 8 | `GET` | `/api/platform/tenants/{id}/stats` | Tenant statistics | — | `TenantStatsDto` |

#### 5.6.2 Dashboard Endpoints

| # | Method | Endpoint | Description | Response |
|---|--------|----------|-------------|----------|
| 9 | `GET` | `/api/platform/dashboard/summary` | Dashboard KPIs | `DashboardSummaryDto` |
| 10 | `GET` | `/api/platform/dashboard/recent-changes` | Recent status changes | `List<TenantStatusLogDto>` |

#### 5.6.3 Audit Log Endpoints

| # | Method | Endpoint | Description | Response |
|---|--------|----------|-------------|----------|
| 11 | `GET` | `/api/platform/audit-log` | Platform audit log (paginated) | `PagedResult<AuditLogDto>` |

#### 5.6.4 Super Admin Management Endpoints

| # | Method | Endpoint | Description | Request Body | Response |
|---|--------|----------|-------------|-------------|----------|
| 12 | `GET` | `/api/platform/admins` | List super admins | — | `List<SuperAdminDto>` |
| 13 | `POST` | `/api/platform/admins` | Create super admin | `CreateSuperAdminRequest` | `SuperAdminDto` (201) |
| 14 | `PUT` | `/api/platform/admins/{id}` | Update super admin | `UpdateSuperAdminRequest` | `SuperAdminDto` |
| 15 | `PATCH` | `/api/platform/admins/{id}/deactivate` | Deactivate super admin | — | `SuperAdminDto` |

#### 5.6.5 Tenant Status Check (Tenant User Access)

| # | Method | Endpoint | Description | Policy | Response |
|---|--------|----------|-------------|--------|----------|
| 16 | `GET` | `/api/tenant/status` | Current tenant status | `TenantUser` | `TenantStatusDto` |

#### 5.6.6 Key DTOs

```csharp
// --- Request DTOs ---

public record CreateTenantRequest(
    string Name,
    string? Subdomain,
    string? ContactName,
    string? ContactEmail,
    string? ContactPhone,
    string? Industry,
    string? Country,
    string? Timezone,
    string? SubscriptionPlan,
    int MaxUsers = 0
);

public record UpdateTenantRequest(
    string? Name,
    string? Subdomain,
    string? ContactName,
    string? ContactEmail,
    string? ContactPhone,
    string? Industry,
    string? Country,
    string? Timezone,
    string? LogoUrl,
    string? SubscriptionPlan,
    int? MaxUsers
);

public record DeactivateTenantRequest(
    string Reason,       // Required — cannot deactivate without a reason
    string? Notes
);

public record ActivateTenantRequest(
    string? Notes
);

public record CreateSuperAdminRequest(
    string Email,
    string DisplayName,
    string Password,
    string? Phone
);

// --- Response DTOs ---

public record TenantListDto(
    int Id,
    string Name,
    string Status,
    string? Subdomain,
    string? ContactName,
    string? ContactEmail,
    string? SubscriptionPlan,
    int CurrentUserCount,
    int MaxUsers,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? DeactivatedAt,
    DateTimeOffset CreatedAt
);

public record TenantDetailDto(
    int Id,
    string Name,
    string Status,
    string? Subdomain,
    string? ContactName,
    string? ContactEmail,
    string? ContactPhone,
    string? LogoUrl,
    string? Industry,
    string? Country,
    string? Timezone,
    int MaxUsers,
    int CurrentUserCount,
    string? SubscriptionPlan,
    DateTimeOffset? SubscriptionStartDate,
    DateTimeOffset? SubscriptionEndDate,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? DeactivatedAt,
    string? DeactivationReason,
    DateTimeOffset CreatedAt
);

public record TenantStatsDto(
    int TotalUsers,
    int ActiveUsers,
    int InactiveUsers,
    int ModulesEnabled,
    DateTimeOffset? LastActivityAt
);

public record DashboardSummaryDto(
    int TotalTenants,
    int ActiveTenants,
    int InactiveTenants,
    int PendingSetupTenants,
    int TotalPlatformUsers,
    Dictionary<string, int> TenantsByPlan,
    List<TenantStatusLogDto> RecentChanges
);

public record TenantStatusDto(
    string Status,
    bool IsReadOnly,
    string? DeactivationReason,
    DateTimeOffset? DeactivatedAt
);
```

#### 5.6.7 Query Parameters for Tenant List

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `page` | int | 1 | Page number |
| `pageSize` | int | 20 | Items per page (max 100) |
| `search` | string | null | Search by name, subdomain, contact email |
| `status` | string | null | Filter by status: `Active`, `Inactive`, `PendingSetup` |
| `plan` | string | null | Filter by subscription plan |
| `country` | string | null | Filter by country |
| `sortBy` | string | `Name` | Sort field: `Name`, `Status`, `CreatedAt`, `CurrentUserCount` |
| `sortDir` | string | `asc` | Sort direction: `asc`, `desc` |

---

### C7 — Super Admin Portal UI

The super admin portal is a **separate React application** (or isolated route group) with its own layout. It contains **no tenant business modules** — no inventory, sales, finance, HR, CRM, POS, or any other module screens.

#### 5.7.1 Portal Layout

```
┌──────────────────────────────────────────────────────────────┐
│  ┌──┐  SMS Platform Admin                    [Admin ▼] [🔔]  │
│  └──┘                                                        │
├───────────┬──────────────────────────────────────────────────┤
│           │                                                  │
│ Dashboard │                                                  │
│ Tenants   │               Main Content Area                  │
│ Admins    │                                                  │
│ Audit Log │                                                  │
│           │                                                  │
│           │                                                  │
│           │                                                  │
│           │                                                  │
└───────────┴──────────────────────────────────────────────────┘
```

**Navigation Items — Only 4:**

| # | Item | Route | Description |
|---|------|-------|-------------|
| 1 | Dashboard | `/platform` | KPI tiles + recent activity |
| 2 | Tenants | `/platform/tenants` | Tenant list + detail + management |
| 3 | Admins | `/platform/admins` | Super admin user management |
| 4 | Audit Log | `/platform/audit-log` | Platform action history |

There are deliberately no other menu items. The super admin's scope is tenant management only.

#### 5.7.2 Dashboard View

```
┌──────────────────────────────────────────────────────────────┐
│  Platform Dashboard                                          │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌─────────────┐ ┌─────────────┐ ┌─────────────┐ ┌────────┐│
│  │ Total       │ │ Active      │ │ Inactive    │ │Pending ││
│  │ Tenants     │ │             │ │             │ │ Setup  ││
│  │    45       │ │    38       │ │     5       │ │   2    ││
│  └─────────────┘ └─────────────┘ └─────────────┘ └────────┘│
│                                                              │
│  ┌─────────────────────────┐  ┌────────────────────────────┐│
│  │ Tenants by Plan         │  │ Recent Status Changes      ││
│  │                         │  │                            ││
│  │ Enterprise  ████  12    │  │ Beta Industries → Inactive ││
│  │ Professional████  18    │  │   Oct 12, 2026  by admin   ││
│  │ Starter     ███   10    │  │                            ││
│  │ Free        ██     5    │  │ Delta Corp → Active        ││
│  │                         │  │   Oct 10, 2026  by admin   ││
│  └─────────────────────────┘  │                            ││
│                               │ Gamma Solutions → Pending  ││
│  ┌─────────────────────────┐  │   Oct 09, 2026  by admin   ││
│  │ Total Users: 342        │  └────────────────────────────┘│
│  │ Active Today: 127       │                                │
│  └─────────────────────────┘                                │
└──────────────────────────────────────────────────────────────┘
```

**The dashboard does NOT show:**
- Revenue or financial data of any tenant
- Business metrics (sales volume, order counts, inventory levels)
- Individual user details or activity
- Module-specific data from any tenant

#### 5.7.3 Tenant List View

```
┌──────────────────────────────────────────────────────────────┐
│  Tenants                                      [+ New Tenant] │
├──────────────────────────────────────────────────────────────┤
│  [Search tenants...         ] [Status ▼] [Plan ▼] [Country▼]│
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  ● Acme Trading Co.              Active   Professional  25👤│
│    acme.sms-app.com              Since Jan 2025              │
│                                                              │
│  ○ Beta Industries               Inactive  Starter     10👤 │
│    beta.sms-app.com              Deactivated Oct 12, 2026    │
│                                                              │
│  ◐ Gamma Solutions               Pending   Enterprise   0👤 │
│    gamma.sms-app.com             Created Oct 09, 2026        │
│                                                              │
│  ● Delta Corp                    Active   Professional  18👤│
│    delta.sms-app.com             Since Mar 2025              │
│                                                              │
├──────────────────────────────────────────────────────────────┤
│  Showing 1–10 of 45                      [◄ 1  2  3  4  5 ►]│
└──────────────────────────────────────────────────────────────┘
```

**Status indicators:** ● Active (green) | ○ Inactive (grey) | ◐ Pending (amber)

#### 5.7.4 Tenant Detail View

```
┌──────────────────────────────────────────────────────────────┐
│  ← Back to Tenants                                           │
│                                                              │
│  ┌───────┐  Acme Trading Co.            Status: ● Active    │
│  │ LOGO  │  acme.sms-app.com            [Deactivate]        │
│  └───────┘  Professional Plan                                │
│                                                              │
│  ┌─ Profile ─┬─ Users ─┬─ Status History ─┬─ Settings ────┐│
│  │                                                         ││
│  │  Organization Name:   Acme Trading Co.                  ││
│  │  Contact Name:        Ahmad Raza                        ││
│  │  Contact Email:       admin@acmetrading.com             ││
│  │  Contact Phone:       +971-50-xxx-xxxx                  ││
│  │  Industry:            Trading & Distribution            ││
│  │  Country:             UAE                               ││
│  │  Timezone:            Asia/Dubai                        ││
│  │                                                         ││
│  │  Subscription Plan:   Professional (25 users max)       ││
│  │  Plan Start:          Jan 15, 2025                      ││
│  │  Plan End:            Jan 14, 2026                      ││
│  │                                                         ││
│  │  Active Users:        18 / 25                           ││
│  │  Tenant Created:      Jan 10, 2025                      ││
│  │  Last Activity:       Oct 12, 2026                      ││
│  │                                                         ││
│  │                                       [Edit Profile]    ││
│  └─────────────────────────────────────────────────────────┘│
└──────────────────────────────────────────────────────────────┘
```

**Users Tab** — Shows ONLY aggregate information:

```
┌─────────────────────────────────────────────────────────────┐
│  ┌─ Profile ─┬─ Users ─┬─ Status History ─┬─ Settings ───┐ │
│  │                                                        │ │
│  │  Total Users:      22                                  │ │
│  │  Active Users:     18                                  │ │
│  │  Inactive Users:    4                                  │ │
│  │  Max Allowed:      25                                  │ │
│  │                                                        │ │
│  │  Tenant Admin:     Ahmad Raza (admin@acmetrading.com)  │ │
│  │                                                        │ │
│  │  ⓘ Individual user details are managed by the         │ │
│  │    tenant administrator and are not visible here.      │ │
│  │                                                        │ │
│  └────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

The super admin can see user counts and the tenant admin contact but **cannot** view individual user records, roles, emails, or activity within the tenant.

**Status History Tab:**

```
┌─────────────────────────────────────────────────────────────┐
│  ┌─ Profile ─┬─ Users ─┬─ Status History ─┬─ Settings ───┐ │
│  │                                                        │ │
│  │  Date              From        To          By          │ │
│  │  ──────────────────────────────────────────────────     │ │
│  │  Oct 12, 2026      Inactive    Active      admin       │ │
│  │                    Reason: Payment received            │ │
│  │                                                        │ │
│  │  Sep 15, 2026      Active      Inactive    admin       │ │
│  │                    Reason: Payment overdue (30 days)    │ │
│  │                                                        │ │
│  │  Jan 10, 2025      PendingSetup Active     system      │ │
│  │                    Reason: Initial setup completed      │ │
│  │                                                        │ │
│  └────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

#### 5.7.5 Deactivation Confirmation Dialog

```
┌──────────────────────────────────────────────────┐
│  ⚠ Deactivate Tenant                             │
│                                                  │
│  Are you sure you want to deactivate             │
│  "Acme Trading Co."?                             │
│                                                  │
│  This will:                                      │
│  • Put the tenant in read-only mode              │
│  • Users can still log in and view their data    │
│  • All transactions will be blocked              │
│  • Background jobs will be paused                │
│                                                  │
│  Reason *                                        │
│  ┌──────────────────────────────────────────────┐│
│  │ Payment overdue                              ││
│  └──────────────────────────────────────────────┘│
│                                                  │
│  Notes (optional)                                │
│  ┌──────────────────────────────────────────────┐│
│  │ Invoice #2026-089 unpaid for 30 days.        ││
│  │ Customer notified via email on Oct 1.        ││
│  └──────────────────────────────────────────────┘│
│                                                  │
│               [Cancel]   [Deactivate]            │
└──────────────────────────────────────────────────┘
```

#### 5.7.6 New Tenant Creation Form

```
┌──────────────────────────────────────────────────┐
│  Create New Tenant                               │
│                                                  │
│  Organization Name *                             │
│  ┌──────────────────────────────────────────────┐│
│  │                                              ││
│  └──────────────────────────────────────────────┘│
│                                                  │
│  Subdomain                                       │
│  ┌────────────────────────┐ .sms-app.com         │
│  │ acme                   │                      │
│  └────────────────────────┘                      │
│                                                  │
│  Contact Name *           Contact Email *        │
│  ┌──────────────────────┐ ┌────────────────────┐ │
│  │                      │ │                    │ │
│  └──────────────────────┘ └────────────────────┘ │
│                                                  │
│  Contact Phone            Industry               │
│  ┌──────────────────────┐ ┌────────────────────┐ │
│  │                      │ │ Select...        ▼ │ │
│  └──────────────────────┘ └────────────────────┘ │
│                                                  │
│  Country                  Timezone               │
│  ┌──────────────────────┐ ┌────────────────────┐ │
│  │ Select...          ▼ │ │ Select...        ▼ │ │
│  └──────────────────────┘ └────────────────────┘ │
│                                                  │
│  Subscription Plan        Max Users              │
│  ┌──────────────────────┐ ┌────────────────────┐ │
│  │ Select...          ▼ │ │ 0 (unlimited)      │ │
│  └──────────────────────┘ └────────────────────┘ │
│                                                  │
│  Admin Account Setup                             │
│  ┌──────────────────────────────────────────────┐│
│  │ Admin Email *                                ││
│  │ ┌──────────────────────────────────────────┐ ││
│  │ │                                          │ ││
│  │ └──────────────────────────────────────────┘ ││
│  │ Temporary Password *                         ││
│  │ ┌──────────────────────────────────────────┐ ││
│  │ │ ●●●●●●●●●●                              │ ││
│  │ └──────────────────────────────────────────┘ ││
│  └──────────────────────────────────────────────┘│
│                                                  │
│               [Cancel]   [Create Tenant]         │
└──────────────────────────────────────────────────┘
```

When a tenant is created:
1. Organization record inserted with `Status = 'PendingSetup'`
2. A new ASP.NET Identity user is created with the provided admin email and password
3. The user is assigned the `TenantAdmin` role and the new `OrgId`
4. A welcome email is sent to the admin with login instructions
5. Platform audit log entry is created

---

### C8 — Inactive Tenant UX (Tenant-Side)

When a tenant is inactive, the tenant application's UI adapts to communicate the read-only state clearly.

#### 5.8.1 Global Inactive Banner

A persistent, non-dismissible banner appears at the top of every page:

```
┌──────────────────────────────────────────────────────────────┐
│ ⚠ Your organization is currently inactive. You can view      │
│   your data but cannot make changes. Contact your            │
│   administrator for assistance.                              │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  [Normal tenant application content below]                   │
│                                                              │
```

**Banner Specification:**
- Background: amber/warning color
- Position: Fixed at top, below main navigation
- Cannot be dismissed
- Visible on every route within the tenant app
- Does NOT appear for super admin users

#### 5.8.2 UI Control Behavior in Inactive State

| Control Type | Active State | Inactive State |
|-------------|-------------|----------------|
| Create/Add buttons | Enabled, visible | **Disabled** with tooltip: "Organization is inactive" |
| Edit buttons | Enabled, visible | **Disabled** with tooltip |
| Delete buttons | Enabled, visible | **Hidden** |
| Save/Submit buttons | Enabled | **Disabled** with tooltip |
| Search/filter | Functional | Functional (read-only still works) |
| Export/Download | Functional | **Functional** (data export always allowed) |
| Navigation | All links active | All links active (viewing is allowed) |
| Forms | Editable | **Read-only display mode** — all inputs disabled |
| Bulk actions | Available | **Hidden** |
| Context menus (write ops) | Visible | **Hidden** |

#### 5.8.3 Frontend Implementation

The frontend detects inactive state via two mechanisms:

**1. Auth Response:** Login response includes `tenantStatus` field:

```json
{
  "token": "eyJ...",
  "user": { ... },
  "tenantStatus": "Inactive",
  "isReadOnly": true
}
```

**2. Response Header Interceptor (Axios):**

```typescript
// axios interceptor
api.interceptors.response.use((response) => {
  const tenantStatus = response.headers['x-tenant-status'];
  if (tenantStatus === 'Inactive') {
    store.dispatch(setReadOnlyMode(true));
  }
  return response;
});

// Error interceptor for blocked writes
api.interceptors.response.use(null, (error) => {
  if (error.response?.status === 403 &&
      error.response?.data?.error === 'TENANT_INACTIVE') {
    // Show toast: "Operation not allowed — organization is inactive"
    toast.warning(error.response.data.message);
  }
  return Promise.reject(error);
});
```

**3. React Context:**

```typescript
interface TenantState {
  status: 'Active' | 'Inactive' | 'PendingSetup';
  isReadOnly: boolean;
}

const TenantContext = React.createContext<TenantState>({
  status: 'Active',
  isReadOnly: false,
});

// Usage in components:
const { isReadOnly } = useTenantContext();

<Button disabled={isReadOnly} title={isReadOnly ? "Organization is inactive" : ""}>
  Create Order
</Button>
```

---

## 6. Database Migrations

| # | Migration | Depends On | Description |
|---|-----------|-----------|-------------|
| M1 | `CreatePlatformSchema` | — | `CREATE SCHEMA [platform]` |
| M2 | `CreateSuperAdminsTable` | M1 | Create `platform.SuperAdmins` table with indexes |
| M3 | `AlterOrganizationsAddTenantFields` | — | Add `Status`, `Subdomain`, contact, subscription columns to `dbo.Organizations` |
| M4 | `CreateTenantStatusLogsTable` | M1, M3 | Create `platform.TenantStatusLogs` table with indexes |
| M5 | `CreatePlatformAuditLogsTable` | M1 | Create `platform.PlatformAuditLogs` table with indexes |
| M6 | `SeedSuperAdminRole` | — | Insert `SuperAdmin` role into `AspNetRoles` |
| M7 | `SeedDefaultSuperAdmin` | M2, M6 | Create default super admin user (email/password from config) |
| M8 | `AddOrgIdLeadingIndexes` | — | Verify/add `OrgId` as leading column in key business table indexes |
| M9 | `SetExistingTenantsActive` | M3 | Set `Status = 'Active'` for all existing Organization records |

**Execution Order:** M1 → M2, M3 (parallel) → M4, M5 (parallel) → M6 → M7 → M8, M9 (parallel)

---

## 7. Business Rules

### BR-SA: Super Admin Rules

| # | Rule | Enforcement |
|---|------|-------------|
| BR-SA-1 | Super admin users MUST have role `SuperAdmin` and no `OrgId` in JWT | Identity service + JWT generation |
| BR-SA-2 | Super admin CANNOT access any endpoint outside `/api/platform/*` | Authorization middleware |
| BR-SA-3 | Super admin CANNOT impersonate a tenant user or view tenant business data | No mechanism exists; PlatformDbContext has no business entities |
| BR-SA-4 | At least one super admin must remain active at all times | Deactivation endpoint checks count before proceeding |
| BR-SA-5 | Super admin accounts are provisioned by other super admins or via seed; self-registration is NOT available | No public registration endpoint exists |
| BR-SA-6 | Super admin login and platform actions are logged in `PlatformAuditLogs` | Audit service |

### BR-TI: Tenant Isolation Rules

| # | Rule | Enforcement |
|---|------|-------------|
| BR-TI-1 | Every entity implementing `IMultiTenant` MUST have a global query filter on `OrgId` | `OnModelCreating` scan + unit test verification |
| BR-TI-2 | `SaveChanges` MUST auto-set `OrgId` on insert and block cross-tenant writes | SaveChanges override |
| BR-TI-3 | The `OrgId` column MUST NOT be modifiable once set on an entity | `entry.Property("OrgId").IsModified = false` |
| BR-TI-4 | Raw SQL (`FromSqlRaw`, stored procedures) MUST include `OrgId` in WHERE clause | Code review checklist; integration test coverage |
| BR-TI-5 | Tenant users CANNOT access `/api/platform/*` endpoints | Authorization policy |
| BR-TI-6 | No API response shall include data from a different tenant | Global query filter + service layer validation |
| BR-TI-7 | Tenant context (`ITenantContext`) MUST be registered as Scoped, never Singleton | DI registration; unit test verification |

### BR-TS: Tenant Status Rules

| # | Rule | Enforcement |
|---|------|-------------|
| BR-TS-1 | Only super admin can change tenant status | `PlatformAdmin` policy on status endpoints |
| BR-TS-2 | Deactivation REQUIRES a non-empty reason string | Request validation; 400 if empty |
| BR-TS-3 | Every status change MUST create a `TenantStatusLog` entry | Service layer; transactional with status update |
| BR-TS-4 | Status transitions must follow the defined state machine (§5.3.2) | Service layer validation |
| BR-TS-5 | Status changes are effective immediately — no grace period | Middleware reads live status |
| BR-TS-6 | When deactivating: set `DeactivatedAt` + `DeactivationReason`; when activating: clear `DeactivatedAt`, update `ActivatedAt` | Service layer |
| BR-TS-7 | Tenant admin receives email notification on status change | Domain event handler |

### BR-RO: Read-Only Mode Rules

| # | Rule | Enforcement |
|---|------|-------------|
| BR-RO-1 | Inactive tenant users CAN authenticate (login/logout/refresh) | Exempt endpoint list |
| BR-RO-2 | Inactive tenant users CAN perform GET/HEAD/OPTIONS requests | TenantStatusMiddleware allows read methods |
| BR-RO-3 | Inactive tenant users CANNOT perform POST/PUT/PATCH/DELETE on business endpoints | TenantStatusMiddleware blocks write methods |
| BR-RO-4 | Inactive tenant users CAN change their own password | Exempt endpoint |
| BR-RO-5 | Inactive tenant users CAN export/download their data | Exempt endpoint |
| BR-RO-6 | Blocked write responses MUST return 403 with error code `TENANT_INACTIVE` | Middleware response format |
| BR-RO-7 | Response headers MUST include `X-Tenant-Status` and `X-Tenant-ReadOnly: true` | TenantContextMiddleware |
| BR-RO-8 | Background jobs (Hangfire) for inactive tenants MUST be paused, not deleted | `TenantDeactivatedEvent` handler |
| BR-RO-9 | When reactivated, paused background jobs MUST resume automatically | `TenantActivatedEvent` handler |
| BR-RO-10 | The frontend MUST display a persistent, non-dismissible inactive banner | React TenantContext + Banner component |
| BR-RO-11 | All create/edit/delete UI controls MUST be disabled or hidden when inactive | React `isReadOnly` flag |

---

## 8. Test Scenarios

### TS-SA: Super Admin Tests (9 tests)

| # | Scenario | Expected Result | Priority |
|---|----------|----------------|----------|
| TS-SA-1 | Super admin logs in with valid credentials | JWT issued with `role: SuperAdmin`, `account_type: platform`, no `org_id` | P0 |
| TS-SA-2 | Super admin accesses `/api/platform/tenants` | 200 OK — list of all tenants returned | P0 |
| TS-SA-3 | Super admin accesses `/api/products` (tenant endpoint) | 403 Forbidden — `PLATFORM_USER_RESTRICTED` | P0 |
| TS-SA-4 | Super admin creates a new tenant | 201 Created — Organization + admin user created | P0 |
| TS-SA-5 | Super admin updates tenant profile | 200 OK — fields updated, audit log created | P1 |
| TS-SA-6 | Super admin views tenant detail | Response contains profile, user count, contact — no business data | P0 |
| TS-SA-7 | Deactivate last remaining super admin | 400 Bad Request — at least one must remain active | P1 |
| TS-SA-8 | Super admin views tenant stats | User counts returned; no financial/business metrics | P1 |
| TS-SA-9 | All super admin actions appear in platform audit log | Audit log entries created with correct action, entity, actor | P1 |

### TS-TI: Tenant Isolation Tests (10 tests)

| # | Scenario | Expected Result | Priority |
|---|----------|----------------|----------|
| TS-TI-1 | Tenant A user queries products | Returns only Tenant A's products; Tenant B's products not visible | P0 |
| TS-TI-2 | Tenant A user queries sales orders | Returns only Tenant A's orders | P0 |
| TS-TI-3 | Tenant A user creates a product | Product's `OrgId` automatically set to Tenant A's ID | P0 |
| TS-TI-4 | Tenant A user attempts to modify entity with Tenant B's `OrgId` | `CrossTenantAccessException` thrown; operation blocked | P0 |
| TS-TI-5 | Tenant user accesses `/api/platform/tenants` (admin endpoint) | 403 Forbidden — `TENANT_USER_RESTRICTED` | P0 |
| TS-TI-6 | JWT with no `org_id` claim accesses tenant endpoint | 403 Forbidden — no tenant context | P0 |
| TS-TI-7 | EF Core `Include()` navigation across tenant boundary | Only related entities within same tenant loaded | P1 |
| TS-TI-8 | Every `IMultiTenant` entity has a registered global query filter | Unit test scans model — all entities covered | P0 |
| TS-TI-9 | `OrgId` change attempt on existing entity | `OrgId` property marked as not-modified; change silently prevented | P1 |
| TS-TI-10 | Two concurrent requests from different tenants | Each request scoped independently; no cross-tenant leakage | P0 |

### TS-TS: Tenant Status Tests (8 tests)

| # | Scenario | Expected Result | Priority |
|---|----------|----------------|----------|
| TS-TS-1 | Super admin deactivates an active tenant | Status → `Inactive`; `DeactivatedAt` set; status log created | P0 |
| TS-TS-2 | Deactivation without reason | 400 Bad Request — reason is required | P0 |
| TS-TS-3 | Super admin reactivates an inactive tenant | Status → `Active`; `DeactivatedAt` cleared; `ActivatedAt` updated | P0 |
| TS-TS-4 | Invalid transition: Active → PendingSetup | 400 Bad Request — transition not allowed | P1 |
| TS-TS-5 | Status change creates TenantStatusLog entry | Log entry has correct previous/new status, actor, timestamp, reason | P0 |
| TS-TS-6 | Status change creates PlatformAuditLog entry | Audit log entry with `TenantActivated` or `TenantDeactivated` action | P1 |
| TS-TS-7 | Newly created tenant has `PendingSetup` status | Organization.Status = `PendingSetup` after creation | P1 |
| TS-TS-8 | Completing setup transitions to `Active` | Status changes to `Active`; `ActivatedAt` set | P1 |

### TS-RO: Read-Only Mode Tests (13 tests)

| # | Scenario | Expected Result | Priority |
|---|----------|----------------|----------|
| TS-RO-1 | Inactive tenant user logs in | 200 OK — authentication succeeds; response includes `tenantStatus: Inactive` | P0 |
| TS-RO-2 | Inactive tenant user performs GET `/api/products` | 200 OK — data returned normally | P0 |
| TS-RO-3 | Inactive tenant user performs POST `/api/products` | 403 Forbidden — `TENANT_INACTIVE` error | P0 |
| TS-RO-4 | Inactive tenant user performs PUT `/api/products/1` | 403 Forbidden — `TENANT_INACTIVE` error | P0 |
| TS-RO-5 | Inactive tenant user performs PATCH `/api/products/1` | 403 Forbidden — `TENANT_INACTIVE` error | P0 |
| TS-RO-6 | Inactive tenant user performs DELETE `/api/products/1` | 403 Forbidden — `TENANT_INACTIVE` error | P0 |
| TS-RO-7 | Inactive tenant user changes password (PUT `/api/users/me/password`) | 200 OK — exempt endpoint | P0 |
| TS-RO-8 | Inactive tenant user exports data (GET `/api/export/products`) | 200 OK — data export works | P1 |
| TS-RO-9 | Response headers include `X-Tenant-Status: Inactive` | Header present on all responses | P1 |
| TS-RO-10 | Tenant reactivated — previously blocked POST now succeeds | 200/201 — write operations restored immediately | P0 |
| TS-RO-11 | Hangfire jobs paused on deactivation | No background jobs run for this tenant | P1 |
| TS-RO-12 | Hangfire jobs resumed on reactivation | Paused jobs resume execution | P1 |
| TS-RO-13 | Error response for blocked writes matches documented format | JSON body has `error`, `message`, `readOnly`, `tenantStatus` fields | P1 |

---

## 9. Domain Events

| # | Event | Raised When | Subscribers |
|---|-------|-------------|-------------|
| DE-1 | `TenantCreatedEvent` | New organization provisioned | Email service (welcome email), Audit logger |
| DE-2 | `TenantActivatedEvent` | Status → Active | Job scheduler (resume jobs), Email service (notification), Audit logger |
| DE-3 | `TenantDeactivatedEvent` | Status → Inactive | Job scheduler (pause jobs), Email service (notification), Audit logger |
| DE-4 | `TenantProfileUpdatedEvent` | Organization profile fields changed | Audit logger |
| DE-5 | `TenantPlanChangedEvent` | Subscription plan changed | Audit logger, Entitlement service (update limits) |
| DE-6 | `SuperAdminCreatedEvent` | New super admin provisioned | Audit logger |
| DE-7 | `SuperAdminDeactivatedEvent` | Super admin account disabled | Audit logger |

---

## 10. Development Phases

### Phase 1 — Foundation & Schema (5 dev days)

| Focus | Tasks |
|-------|-------|
| Database | Create `platform` schema; create `SuperAdmins`, `TenantStatusLogs`, `PlatformAuditLogs` tables |
| Organization | Add new columns to `Organizations` table; create indexes |
| Identity | Add `SuperAdmin` role to `AspNetRoles`; seed default super admin |
| Migrations | M1–M7, M9 |

### Phase 2 — Tenant Isolation Layer (6 dev days)

| Focus | Tasks |
|-------|-------|
| Tenant Context | Implement `ITenantContext` / `TenantContext` service |
| Middleware | `TenantContextMiddleware` — resolve OrgId from JWT, load tenant status |
| EF Core | Apply `HasQueryFilter` on all `IMultiTenant` entities |
| SaveChanges | Override with OrgId auto-set on insert, cross-tenant write prevention |
| Indexes | Verify/add OrgId-leading indexes on all business tables (M8) |
| Authorization | Implement `PlatformAdmin`, `TenantUser`, `TenantAdmin` policies |
| Route Guard | Block super admin ↔ tenant cross-access |

### Phase 3 — Tenant Lifecycle & Read-Only Mode (5 dev days)

| Focus | Tasks |
|-------|-------|
| Status Service | `TenantLifecycleService` — activate, deactivate, transition validation |
| Status Middleware | `TenantStatusMiddleware` — enforce read-only for inactive tenants |
| Exempt Endpoints | Configure exempt list (auth, password, export, status check) |
| Side Effects | Domain event handlers for job pausing, email notifications |
| Audit | `PlatformAuditService` — log all super admin actions |
| Status Logging | Create `TenantStatusLog` on every transition |

### Phase 4 — Super Admin API (4 dev days)

| Focus | Tasks |
|-------|-------|
| Tenant CRUD | Endpoints 1–8 (list, create, detail, update, activate, deactivate, history, stats) |
| Dashboard | Endpoints 9–10 (summary, recent changes) |
| Audit | Endpoint 11 (audit log) |
| Admin CRUD | Endpoints 12–15 (list, create, update, deactivate super admins) |
| Tenant Status | Endpoint 16 (tenant-side status check) |
| DTOs | All request/response DTOs |
| Validation | FluentValidation rules for all requests |

### Phase 5 — Super Admin Portal UI (6 dev days)

| Focus | Tasks |
|-------|-------|
| Layout | Platform admin shell with 4-item sidebar |
| Dashboard | KPI tiles, tenants-by-plan chart, recent changes list |
| Tenant List | Table with search, filters, pagination, status indicators |
| Tenant Detail | Tabbed view (Profile, Users, Status History, Settings) |
| Tenant Forms | Create tenant form, edit profile form |
| Dialogs | Deactivation confirmation (with reason), reactivation confirmation |
| Admin List | Super admin management table |
| Audit Log | Filterable audit log table with action details |

### Phase 6 — Tenant-Side UX & Testing (5 dev days)

| Focus | Tasks |
|-------|-------|
| Inactive Banner | Global banner component, tenant context provider |
| Control Disabling | Disabled buttons, hidden actions, read-only forms |
| Axios Interceptors | Response header reader, blocked-write error handler |
| Unit Tests | All TS-SA, TS-TI, TS-TS, TS-RO scenarios |
| Integration Tests | Cross-tenant isolation, middleware pipeline, status transitions |
| E2E Tests | Super admin portal flow, deactivation → read-only → reactivation cycle |
| Performance | Verify no N+1 queries in tenant status check; cache tenant status per request |

**Total Estimated Effort: ~31 dev days**

---

## 11. Middleware Pipeline Summary

The complete middleware pipeline order for SMS:

```
1. UseExceptionHandler()
2. UseHttpsRedirection()
3. UseStaticFiles()
4. UseRouting()
5. UseAuthentication()
6. UseTenantContext()          ← NEW (C4) — Resolve OrgId + load tenant status
7. UseTenantStatusCheck()      ← NEW (C5) — Enforce read-only for inactive
8. UseAuthorization()
9. UsePlatformRouteGuard()     ← NEW (C1) — Block cross-access
10. UseEndpoints()
```

---

## 12. Security Considerations

| Concern | Mitigation |
|---------|-----------|
| JWT tampering (changing org_id claim) | JWT signed with server key; claims validated on every request |
| Direct database access bypassing filters | Connection strings restricted; no direct DB access from client |
| Raw SQL bypassing global filters | Code review checklist; integration tests for all raw SQL |
| Super admin privilege escalation | Separate provisioning; no self-registration; no elevation from tenant user |
| Tenant status caching stale | Status loaded fresh per request; no cross-request caching |
| Inactive tenant data exfiltration | Export endpoints are deliberately allowed — tenants own their data |
| Cross-tenant navigation via URL manipulation | All entity lookups go through EF Core global filter; service layer validates ownership |

---

## 13. Deferred to Future Addendums

The following capabilities are **out of scope** for Addendum 43 and will be addressed in future addendums:

| Item | Description | Reason for Deferral |
|------|-------------|-------------------|
| Tenant Billing Integration | Payment processing, invoice generation for subscriptions | Requires payment gateway integration |
| Usage Metering | Track API calls, storage, bandwidth per tenant | Requires metrics infrastructure |
| Custom Domain Mapping | CNAME support for tenant-specific domains | Requires DNS and SSL certificate management |
| Tenant Data Migration | Tools for importing/exporting complete tenant data | Complex scope; separate utility |
| Super Admin Tenant Impersonation | Ability for super admin to "view as" a tenant (with audit trail) | Privacy and security implications require separate design |
| Tenant Suspension State | Automatic suspension on payment failure (distinct from manual deactivation) | Requires billing integration first |
| Tenant Data Purge / Deletion | Complete removal of a tenant and all its data (GDPR compliance) | Requires careful cascade deletion design |
| Multi-Region Tenant Placement | Ability to assign tenants to specific data regions | Requires infrastructure for multi-region deployment |
| Tenant Feature Flags | Per-tenant feature toggles for gradual rollout | Requires feature flag infrastructure |
| Tenant API Key Management | Self-service API key generation and management by tenants | Requires API gateway configuration |

---

## Appendix A: Entity Relationship Diagram (Text)

```
┌──────────────────┐         ┌─────────────────────────┐
│   AspNetUsers    │         │    AspNetRoles           │
│                  │         │                          │
│  Id (PK)         │────┐    │  Id (PK)                │
│  Email           │    │    │  Name (SuperAdmin,       │
│  OrgId (nullable)│    │    │        TenantAdmin, etc.)│
│  ...             │    │    └─────────────────────────┘
└─────────┬────────┘    │
          │             │
   ┌──────┘   ┌─────────┘
   │          │
   ▼          ▼
┌──────────────────────┐     ┌─────────────────────────┐
│ platform.SuperAdmins │     │   dbo.Organizations     │
│                      │     │                          │
│  Id (PK)             │     │  Id (PK)                │
│  UserId (FK, Unique) │     │  Name                   │
│  DisplayName         │     │  Status ◄───────────────┤
│  Email               │     │  Subdomain              │
│  IsActive            │     │  ContactName/Email/Phone│
│  ...                 │     │  MaxUsers               │
└──────────────────────┘     │  SubscriptionPlan       │
                             │  ActivatedAt            │
                             │  DeactivatedAt          │
                             │  DeactivationReason     │
                             │  ...                    │
                             └───────────┬─────────────┘
                                         │
                    ┌────────────────────┘
                    │
                    ▼
┌──────────────────────────────┐    ┌──────────────────────────┐
│ platform.TenantStatusLogs    │    │ platform.PlatformAuditLogs│
│                              │    │                          │
│  Id (PK)                     │    │  Id (PK)                │
│  OrganizationId (FK)         │    │  Action                 │
│  PreviousStatus              │    │  EntityType             │
│  NewStatus                   │    │  EntityId               │
│  Reason                      │    │  Details (JSON)         │
│  ChangedByUserId (FK)        │    │  PerformedByUserId (FK) │
│  ChangedAt                   │    │  PerformedAt            │
│  IpAddress                   │    │  IpAddress              │
└──────────────────────────────┘    └──────────────────────────┘
```

---

## Appendix B: Multi-Tenant Entity Compliance Checklist

For every entity in SMS that stores tenant-specific data:

- [ ] Implements `IMultiTenant` interface
- [ ] Has `OrgId` column with FK to `Organizations.Id`
- [ ] Registered in `OnModelCreating` global query filter scan
- [ ] Has composite index with `OrgId` as leading column
- [ ] Service layer does NOT manually set `OrgId` (SaveChanges interceptor handles it)
- [ ] Raw SQL (if any) includes `WHERE OrgId = @orgId`
- [ ] Integration test verifies cross-tenant isolation for this entity
- [ ] No navigation property crosses the tenant boundary without filter

**Existing Schemas Requiring Verification:**

| Schema | Key Entities | Status |
|--------|-------------|--------|
| `inventory` | Products, Warehouses, StockMovements | Verify |
| `sales` | SalesOrders, SalesOrderItems, Customers | Verify |
| `purchasing` | PurchaseOrders, Suppliers | Verify |
| `manufacturing` | ProductionOrders, BOM | Verify |
| `finance` | ChartOfAccounts, FiscalYears, AccountingDefaults | Verify |
| `hr` | Employees, Departments | Verify |
| `crm` | Leads, Pipelines, Activities | Verify |
| `pos` | POSSessions, POSTransactions | Verify |
| `logistics` | DeliveryOrders, Shipments | Verify |
| `service` | ServiceOrders, ServiceItems | Verify |

---

## Appendix C: Configuration Reference

### appsettings.json — Platform Section

```json
{
  "Platform": {
    "DefaultSuperAdmin": {
      "Email": "superadmin@sms-platform.com",
      "DisplayName": "Platform Administrator",
      "Password": "{{FROM_ENVIRONMENT_VARIABLE}}"
    },
    "TenantDefaults": {
      "MaxUsers": 0,
      "DefaultPlan": "Free",
      "DefaultTimezone": "UTC"
    },
    "Security": {
      "RequireReasonForDeactivation": true,
      "MinSuperAdminCount": 1,
      "AuditLogRetentionDays": 365
    }
  }
}
```

**Note:** The default super admin password MUST come from an environment variable or secret manager, never from the config file directly.

---

*End of Addendum 43*
