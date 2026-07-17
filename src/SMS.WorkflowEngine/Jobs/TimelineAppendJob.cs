using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.WorkflowEngine.Jobs;

internal sealed class TimelineAppendJob : ITimelineAppendJob
{
    private readonly ITimelineService _timeline;
    public TimelineAppendJob(ITimelineService timeline) => _timeline = timeline;

    public Task AppendAsync(Guid traceId, TimelineEvent newEvent, string? chainRootType = null, string? chainRootRef = null) =>
        _timeline.AppendEventAsync(traceId, newEvent, chainRootType, chainRootRef);
}
