namespace SMS.WorkflowEngine.Domain;

internal class DocumentTimeline
{
    public int      Id            { get; set; }
    public Guid     TraceId       { get; set; }

    /// <summary>JSON-serialized array of TimelineEvent, ordered by insertion (append-only).</summary>
    public string   Events        { get; set; } = "[]";

    /// <summary>Interface code of the document that started this trace chain, e.g. "PR".</summary>
    public string?  ChainRootType { get; set; }

    /// <summary>Human-readable reference of the chain root, e.g. the PR number.</summary>
    public string?  ChainRootRef  { get; set; }

    public DateTime FirstEventAt  { get; set; }
    public DateTime LastEventAt   { get; set; }
    public DateTime CreatedAt     { get; set; }

    /// <summary>SQL Server ROWVERSION — optimistic concurrency token guarding the Events read-modify-write.</summary>
    public byte[]   RowVersion    { get; set; } = [];
}
