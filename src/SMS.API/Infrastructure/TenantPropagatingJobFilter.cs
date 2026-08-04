using Hangfire.Client;
using Hangfire.Server;
using Microsoft.AspNetCore.Http;
using SMS.Shared.Common;

namespace SMS.API.Infrastructure;

// Bridges tenant identity across the Hangfire enqueue -> execute boundary. Client-side (OnCreating)
// runs on the original request's own thread, where HttpContext (and so the real caller's org) still
// exists — it stashes that org as a job parameter. Server-side (OnPerforming/OnPerformed) runs
// inside the Hangfire worker's own scope, where HttpContext is always null — it restores the
// stashed org into HangfireTenantScope for the duration of the job, so TenantContext (which every
// tenant-scoped DbContext relies on to auto-stamp new rows) resolves the job's true originating
// org instead of silently falling back to a hardcoded default org. See HangfireTenantScope for
// the full story and what this fixed (DocumentTimeline rows written by background jobs were all
// landing under one default org, invisible to every other organization once read back).
public sealed class TenantPropagatingJobFilter : IClientFilter, IServerFilter
{
    private const string JobParameterName = "OriginatingOrganizationId";

    private readonly IHttpContextAccessor _httpContextAccessor;

    public TenantPropagatingJobFilter(IHttpContextAccessor httpContextAccessor) =>
        _httpContextAccessor = httpContextAccessor;

    public void OnCreating(CreatingContext filterContext)
    {
        var claim = _httpContextAccessor.HttpContext?.User?.FindFirst("organizationId")?.Value;
        if (Guid.TryParse(claim, out var organizationId))
            filterContext.SetJobParameter(JobParameterName, organizationId);
    }

    public void OnCreated(CreatedContext filterContext) { }

    public void OnPerforming(PerformingContext filterContext)
    {
        HangfireTenantScope.OrganizationId = filterContext.GetJobParameter<Guid?>(JobParameterName);
    }

    public void OnPerformed(PerformedContext filterContext)
    {
        HangfireTenantScope.OrganizationId = null;
    }
}
