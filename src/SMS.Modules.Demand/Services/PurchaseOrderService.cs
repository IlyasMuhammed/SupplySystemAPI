using Hangfire;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Demand.Services;

internal sealed class PurchaseOrderService : IPurchaseOrderService
{
    private readonly IPurchaseOrderRepository _repo;
    private readonly IWorkflowActionService   _workflow;
    private readonly IWorkflowInboxService    _inbox;
    private readonly IBackgroundJobClient     _jobs;
    private readonly ITimelineService         _timeline;

    public PurchaseOrderService(
        IPurchaseOrderRepository repo,
        IWorkflowActionService   workflow,
        IWorkflowInboxService    inbox,
        IBackgroundJobClient     jobs,
        ITimelineService         timeline)
    {
        _repo     = repo;
        _workflow = workflow;
        _inbox    = inbox;
        _jobs     = jobs;
        _timeline = timeline;
    }

    public async Task<Guid> CreateFromPrAsync(Guid prUuid, ConvertPrToPoRequest req, int createdBy)
    {
        var poUuid = await _repo.CreateFromPrAsync(prUuid, req, createdBy);
        await EnqueuePoCreatedAsync(poUuid, createdBy);
        return poUuid;
    }

    public async Task<List<Guid>> CreateFromPrSplitAsync(Guid prUuid, ConvertPrSplitRequest req, int createdBy)
    {
        var poUuids = await _repo.CreateFromPrSplitAsync(prUuid, req, createdBy);
        foreach (var poUuid in poUuids)
            await EnqueuePoCreatedAsync(poUuid, createdBy);
        return poUuids;
    }

    public async Task<Guid> CreateAsync(CreatePoRequest req, int createdBy)
    {
        var poUuid = await _repo.CreateAsync(req, createdBy);
        await EnqueuePoCreatedAsync(poUuid, createdBy);
        return poUuid;
    }

    public async Task<IReadOnlyList<PoFieldChange>> UpdateAsync(Guid uuid, PatchPoRequest req, int modifiedBy)
    {
        var changes = await _repo.UpdateAsync(uuid, req, modifiedBy);

        var po = await _repo.GetByIdAsync(uuid);
        if (po is not null)
        {
            // The timeline says what changed too, for the POs that are audited field by field —
            // "PO amended" with no detail is what a hand-made PO's edit has always shown.
            var notes = changes.Count == 0 ? null : Truncate(string.Join("; ", changes.Select(DescribeChange)), 500);
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                po.TraceId,
                new TimelineEvent("PO_AMENDED", "PO", uuid, po.PoNumber, DateTime.UtcNow, modifiedBy, notes),
                "PO", po.PoNumber));
        }

        return changes;
    }

    public async Task<SplitPoResult> SplitAsync(Guid uuid, SplitPoRequest req, int userId)
    {
        var result = await _repo.SplitAsync(uuid, req, userId);

        var amended = string.Join("; ", result.Changes.Select(DescribeChange));
        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            result.TraceId,
            new TimelineEvent("PO_AMENDED", "PO", result.SourcePoUuid, result.SourcePoNumber, DateTime.UtcNow, userId, amended),
            "PO", result.SourcePoNumber));
        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            result.TraceId,
            new TimelineEvent("PO_CREATED", "PO", result.NewPoUuid, result.NewPoNumber, DateTime.UtcNow, userId,
                $"Split from {result.SourcePoNumber}"),
            "PO", result.NewPoNumber));

        return result;
    }

    private static string DescribeChange(PoFieldChange c) =>
        c.OldValue is null ? $"{c.Field}: {c.NewValue}"
        : c.NewValue is null ? $"{c.Field}: {c.OldValue} removed"
        : $"{c.Field}: {c.OldValue} → {c.NewValue}";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public async Task SubmitForApprovalAsync(Guid uuid, int userId)
    {
        var po = await _repo.GetByIdAsync(uuid)
            ?? throw new NotFoundException("PurchaseOrder", uuid);

        if (po.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT purchase orders can be submitted for approval. Current status: {po.Status}.");

        await _workflow.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode  = "PO",
            DocumentId     = uuid,
            DocumentNumber = po.PoNumber,
            ConditionValue = po.TotalAmount,
            SubmittedBy    = userId
        });

        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            po.TraceId,
            new TimelineEvent("PO_SUBMITTED", "PO", uuid, po.PoNumber, DateTime.UtcNow, userId, null),
            "PO", po.PoNumber));
    }

    public async Task SendAsync(Guid uuid, string? contactMobile, int modifiedBy)
    {
        await _repo.SendAsync(uuid, contactMobile, modifiedBy);
        if (!string.IsNullOrWhiteSpace(contactMobile))
            _jobs.Enqueue<PoWhatsAppDispatchJob>(j => j.SendPoWhatsAppAsync(uuid, contactMobile));

        var po = await _repo.GetByIdAsync(uuid);
        if (po is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                po.TraceId,
                new TimelineEvent("PO_SENT", "PO", uuid, po.PoNumber, DateTime.UtcNow, modifiedBy, null),
                "PO", po.PoNumber));
    }

    // A29-P4-04 §4.5.
    public async Task CancelAsync(Guid uuid, int userId, string? reason)
    {
        await _repo.CancelAsync(uuid, reason, userId);

        var po = await _repo.GetByIdAsync(uuid);
        if (po is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                po.TraceId,
                new TimelineEvent("PO_CANCELLED", "PO", uuid, po.PoNumber, DateTime.UtcNow, userId, reason),
                "PO", po.PoNumber));
    }

    // Through the PO trace-id resolver rather than loading the whole PO just to read one column —
    // the same route GET /api/timeline/by-document takes, so the two can never disagree on which
    // trace a PO belongs to.
    public async Task<TimelineDetail?> GetTimelineAsync(Guid uuid)
    {
        var traceId = await _timeline.ResolveTraceIdAsync("PO", uuid);
        return traceId is null ? null : await _timeline.GetTimelineDetailAsync(traceId.Value);
    }

    public Task<PaginatedResponse<PoListItemModel>> GetListAsync(PoListFilter filter) =>
        _repo.GetListAsync(filter);

    public Task<List<PoSearchItemModel>> SearchForGrnAsync(string? q, bool receivableOnly) =>
        _repo.SearchForGrnAsync(q, receivableOnly);

    public Task<PoDetailModel?> GetByIdAsync(Guid uuid) =>
        _repo.GetByIdAsync(uuid);

    public async Task ApproveAsync(Guid poUuid, int approvedBy, string? remarks = null)
    {
        // Capture the tier being actioned before approving — CurrentStepNumber advances once approved.
        var activeApproval = await _inbox.GetActiveApprovalByDocumentAsync(poUuid);
        var tierStep        = activeApproval?.CurrentStepNumber;
        var tierName         = activeApproval?.Steps.FirstOrDefault(s => s.StepNumber == tierStep)?.StepName;

        await _workflow.ApproveByDocumentAsync("PO", poUuid, approvedBy, remarks);

        var po = await _repo.GetByIdAsync(poUuid);
        if (po is not null)
        {
            // Who approved it is carried on the event's PerformedBy/PerformedByName (resolved
            // generically for every timeline event) — notes only need the tier-specific detail.
            var notes = tierStep.HasValue
                ? $"At tier {tierStep} ({tierName})."
                : null;

            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                po.TraceId,
                new TimelineEvent("PO_APPROVED", "PO", poUuid, po.PoNumber, DateTime.UtcNow, approvedBy, notes),
                "PO", po.PoNumber));
        }
    }

    public Task RejectAsync(Guid poUuid, int rejectedBy, string rejectionReason) =>
        _workflow.RejectByDocumentAsync("PO", poUuid, rejectedBy, rejectionReason);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task EnqueuePoCreatedAsync(Guid poUuid, int createdBy)
    {
        var po = await _repo.GetByIdAsync(poUuid);
        if (po is null) return;

        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            po.TraceId,
            new TimelineEvent("PO_CREATED", "PO", poUuid, po.PoNumber, DateTime.UtcNow, createdBy, null),
            "PO", po.PoNumber));
    }
}
