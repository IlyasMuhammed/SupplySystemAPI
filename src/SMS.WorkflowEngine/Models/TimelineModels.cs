namespace SMS.WorkflowEngine.Models;

/// <summary>A single lifecycle event appended to a document trace chain's timeline.</summary>
public record TimelineEvent(
    string   EventType,
    string   InterfaceCode,
    Guid     DocumentId,
    string?  DocumentNumber,
    DateTime OccurredAt,
    int?     PerformedBy,
    string?  Notes
);

/// <summary>Full timeline for a trace chain — the shape returned by both GET endpoints.</summary>
public class TimelineDetail
{
    public Guid     TraceId       { get; set; }
    public string?  ChainRootType { get; set; }
    public string?  ChainRootRef  { get; set; }
    public DateTime FirstEventAt  { get; set; }
    public DateTime LastEventAt   { get; set; }
    public List<TimelineEvent> Events { get; set; } = [];
    public int TotalEventCount => Events.Count;
}
