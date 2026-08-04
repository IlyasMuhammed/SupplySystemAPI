using Microsoft.AspNetCore.Http;

namespace SMS.Shared.Common;

internal sealed class TenantContext : ITenantContext
{
    private readonly IHttpContextAccessor _accessor;

    public TenantContext(IHttpContextAccessor accessor) => _accessor = accessor;

    public Guid OrganizationId
    {
        get
        {
            var claim = _accessor.HttpContext?.User?.FindFirst("organizationId")?.Value;
            if (Guid.TryParse(claim, out var id)) return id;

            // No HttpContext (a Hangfire job) but its originating org was captured at enqueue
            // time (see HangfireTenantScope) — use that instead of the hardcoded default so the
            // job's writes land under the correct tenant, not always SCM-DEMO.
            return HangfireTenantScope.OrganizationId ?? TenantDefaults.ScmDemoOrganizationId;
        }
    }

    // No authenticated principal (anonymous endpoint, or no HttpContext at all — a background
    // job) means no tenant can be known in advance, so treat it as a bypass rather than
    // silently scoping to SCM-DEMO. Anonymous endpoints in this app only ever query by a
    // specific unique key (email/token/hash), never a broad listing, so this is safe.
    public bool IsSuperAdmin
    {
        get
        {
            var user = _accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                // MT-007: SuperAdminUsers table membership is the sole authoritative source,
                // stamped into this claim at login/refresh time (see AuthService.BuildTokensAsync
                // + ISuperAdminService) — a plain claim read here, no per-request DB call and no
                // permission-based fallback.
                return user.FindFirst("is_super_admin")?.Value == "true";
            }

            // A Hangfire job whose originating org was captured behaves as a normal org-scoped
            // caller for that org (not a bypass) — only a job/context with no captured org at all
            // (or a genuinely anonymous HTTP endpoint) falls back to the original bypass behavior.
            return HangfireTenantScope.OrganizationId is null;
        }
    }
}
