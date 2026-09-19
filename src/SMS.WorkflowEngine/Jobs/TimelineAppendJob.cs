using SMS.Shared.Common;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.WorkflowEngine.Jobs;

internal sealed class TimelineAppendJob : ITimelineAppendJob
{
    private readonly ITimelineService _timeline;
    public TimelineAppendJob(ITimelineService timeline) => _timeline = timeline;

    public Task AppendAsync(Guid traceId, TimelineEvent newEvent, string? chainRootType = null, string? chainRootRef = null) =>
        _timeline.AppendEventAsync(traceId, newEvent, chainRootType, chainRootRef);

    // Sets the same ambient scope TenantPropagatingJobFilter would have restored from an HTTP-origin
    // job parameter, for just this call, and puts back whatever was there — the filter's own
    // OnPerformed resets it to null afterwards regardless, but nothing here should depend on that.
    public async Task AppendAsync(
        Guid traceId, TimelineEvent newEvent, string? chainRootType, string? chainRootRef, Guid organizationId)
    {
        var previous = HangfireTenantScope.OrganizationId;
        HangfireTenantScope.OrganizationId = organizationId;
        try
        {
            await _timeline.AppendEventAsync(traceId, newEvent, chainRootType, chainRootRef);
        }
        finally
        {
            HangfireTenantScope.OrganizationId = previous;
        }
    }
}
