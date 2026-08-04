namespace SMS.Shared.Common;

// Bridges tenant identity across the one gap ITenantContext's normal HttpContext-based resolution
// can't cover: a Hangfire background job. Jobs execute in their own DI scope with no HttpContext,
// so TenantContext used to fall back to a hardcoded default org for every write a job made — e.g.
// every DocumentTimeline row ever written by a background job landed under that one default org,
// invisible to every other organization's users once read back through the normal tenant filter.
//
// A Hangfire client/server filter (see SMS.API's TenantPropagatingJobFilter) captures the real
// caller's OrganizationId at enqueue time (client-side, where the real HttpContext still exists)
// and restores it into this AsyncLocal for the duration of the job's execution (server-side),
// so TenantContext can fall back to the job's true originating org instead of a hardcoded default
// when there's no HttpContext to read from.
public static class HangfireTenantScope
{
    private static readonly AsyncLocal<Guid?> _organizationId = new();

    public static Guid? OrganizationId
    {
        get => _organizationId.Value;
        set => _organizationId.Value = value;
    }
}
