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

    /// <summary>
    /// A29-P5-04 §13.7 — the same append, carrying the organization explicitly ("Hangfire jobs get
    /// org id as a parameter"). The implicit route (TenantPropagatingJobFilter) only captures an org
    /// from an HTTP request's claims at enqueue time, so a job that is itself enqueued from inside
    /// another Hangfire job — no request, no claims — hands its timeline job no org at all, and a
    /// timeline row that job has to create is then stamped with the default organization. Callers
    /// that can run inside a background job pass the org they know to be right.
    /// <para>
    /// A required fifth parameter rather than another optional one: existing callers are all
    /// expression trees, which cannot omit optional arguments, and this leaves every one of them
    /// compiling unchanged.
    /// </para>
    /// </summary>
    Task AppendAsync(Guid traceId, TimelineEvent newEvent, string? chainRootType, string? chainRootRef, Guid organizationId);
}
