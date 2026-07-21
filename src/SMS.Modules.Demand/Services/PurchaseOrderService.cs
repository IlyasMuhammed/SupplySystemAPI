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

    public PurchaseOrderService(
        IPurchaseOrderRepository repo,
        IWorkflowActionService   workflow,
        IWorkflowInboxService    inbox,
        IBackgroundJobClient     jobs)
    {
        _repo     = repo;
        _workflow = workflow;
        _inbox    = inbox;
        _jobs     = jobs;
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

    public async Task UpdateAsync(Guid uuid, PatchPoRequest req, int modifiedBy)
    {
        await _repo.UpdateAsync(uuid, req, modifiedBy);

        var po = await _repo.GetByIdAsync(uuid);
        if (po is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                po.TraceId,
                new TimelineEvent("PO_AMENDED", "PO", uuid, po.PoNumber, DateTime.UtcNow, modifiedBy, null),
                "PO", po.PoNumber));
    }

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
