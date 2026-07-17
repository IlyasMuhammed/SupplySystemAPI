namespace SMS.Shared.Common;

/// <summary>
/// Implemented by each business module to resolve a document's trace_id without a direct
/// module dependency. Register with DI as ITraceIdResolver; the composite ITimelineService
/// (in SMS.WorkflowEngine) dispatches by InterfaceCode at runtime — mirrors IDocumentStatusHandler.
/// </summary>
public interface ITraceIdResolver
{
    /// <summary>The interface code this resolver owns, e.g. "PO", "PR", "GRN".</summary>
    string InterfaceCode { get; }

    /// <summary>Returns the document's trace_id, or null if the document does not exist.</summary>
    Task<Guid?> ResolveTraceIdAsync(Guid documentId);
}
