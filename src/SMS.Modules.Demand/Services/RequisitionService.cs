using Hangfire;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Demand.Services;

internal sealed class RequisitionService : IRequisitionService
{
    private readonly IRequisitionRepository _repo;
    private readonly IWorkflowActionService _workflow;
    private readonly IBackgroundJobClient   _jobs;

    public RequisitionService(IRequisitionRepository repo, IWorkflowActionService workflow, IBackgroundJobClient jobs)
    {
        _repo     = repo;
        _workflow = workflow;
        _jobs     = jobs;
    }

    public async Task<Guid> CreateAsync(CreatePrRequest req, int createdBy)
    {
        var uuid = await _repo.CreateAsync(req, createdBy);
        var pr   = await _repo.GetByIdAsync(uuid);

        if (pr is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                pr.TraceId,
                new TimelineEvent("PR_CREATED", "PR", uuid, pr.PrNumber, DateTime.UtcNow, createdBy, null),
                "PR", pr.PrNumber));

        return uuid;
    }

    public Task<PaginatedResponse<PrListItemModel>> GetListAsync(PrListFilter filter) =>
        _repo.GetListAsync(filter);

    public Task<PrDetailModel?> GetByIdAsync(Guid uuid) =>
        _repo.GetByIdAsync(uuid);

    public Task UpdateAsync(Guid uuid, PatchPrRequest req, int modifiedBy) =>
        _repo.UpdateAsync(uuid, req, modifiedBy);

    public async Task SubmitAsync(Guid uuid, int modifiedBy)
    {
        var pr = await _repo.GetByIdAsync(uuid)
            ?? throw new NotFoundException("PurchaseRequisition", uuid);

        if (pr.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT requisitions can be submitted. Current status: {pr.Status}.");

        await _workflow.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode  = "PR",
            DocumentId     = uuid,
            DocumentNumber = pr.PrNumber,
            SubmittedBy    = modifiedBy
        });

        _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            pr.TraceId,
            new TimelineEvent("PR_SUBMITTED", "PR", uuid, pr.PrNumber, DateTime.UtcNow, modifiedBy, null),
            "PR", pr.PrNumber));
    }

    public async Task ApproveAsync(Guid prUuid, int approvedBy, string? remarks = null)
    {
        await _workflow.ApproveByDocumentAsync("PR", prUuid, approvedBy, remarks);

        var pr = await _repo.GetByIdAsync(prUuid);

        // Only log once the workflow is genuinely fully approved — under ALL/MAJORITY
        // approval modes a single vote is just one of several required, and logging
        // here on every partial vote produces one duplicate timeline entry per approver.
        if (pr is not null && pr.Status == "APPROVED")
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                pr.TraceId,
                new TimelineEvent("PR_APPROVED", "PR", prUuid, pr.PrNumber, DateTime.UtcNow, approvedBy, remarks),
                "PR", pr.PrNumber));
    }

    public async Task RejectAsync(Guid prUuid, int rejectedBy, string rejectionReason)
    {
        await _workflow.RejectByDocumentAsync("PR", prUuid, rejectedBy, rejectionReason);

        var pr = await _repo.GetByIdAsync(prUuid);
        if (pr is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                pr.TraceId,
                new TimelineEvent("PR_REJECTED", "PR", prUuid, pr.PrNumber, DateTime.UtcNow, rejectedBy, rejectionReason),
                "PR", pr.PrNumber));
    }
}
