using SMS.WorkflowEngine.Models;

namespace SMS.WorkflowEngine.Services;

public interface ITimelineService
{
    /// <summary>
    /// Appends an event to the trace chain's timeline, creating the row on first use.
    /// chainRootType/chainRootRef are only applied when the row is first created; they
    /// are never revisited on later appends.
    /// </summary>
    Task AppendEventAsync(Guid traceId, TimelineEvent newEvent, string? chainRootType = null, string? chainRootRef = null);

    /// <summary>Returns the trace chain's events in ascending chronological order (empty if unknown).</summary>
    Task<IReadOnlyList<TimelineEvent>> GetTimelineAsync(Guid traceId);

    /// <summary>Returns the full timeline detail (chain root + ordered events) for a trace_id, or null if unknown.</summary>
    Task<TimelineDetail?> GetTimelineDetailAsync(Guid traceId);

    /// <summary>Dispatches to the ITraceIdResolver registered for interfaceCode; null if unresolvable.</summary>
    Task<Guid?> ResolveTraceIdAsync(string interfaceCode, Guid documentId);
}
