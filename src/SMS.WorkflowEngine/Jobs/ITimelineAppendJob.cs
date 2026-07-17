using SMS.WorkflowEngine.Models;

namespace SMS.WorkflowEngine.Jobs;

/// <summary>
/// Hangfire job wrapping ITimelineService.AppendEventAsync — business services enqueue
/// through this instead of calling AppendEventAsync inline, so a timeline write can never
/// roll back the business transaction and transient failures get Hangfire's automatic retry.
/// </summary>
public interface ITimelineAppendJob
{
    Task AppendAsync(Guid traceId, TimelineEvent newEvent, string? chainRootType = null, string? chainRootRef = null);
}
