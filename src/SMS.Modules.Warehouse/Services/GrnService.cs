using Hangfire;
using SMS.Modules.Warehouse.Models;
using SMS.Modules.Warehouse.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Warehouse.Services;

internal sealed class GrnService : IGrnService
{
    private readonly IGrnRepository         _repo;
    private readonly IWorkflowActionService _workflow;
    private readonly IDocumentStatusService _docStatus;
    private readonly IBackgroundJobClient   _jobs;

    public GrnService(
        IGrnRepository repo, IWorkflowActionService workflow, IDocumentStatusService docStatus, IBackgroundJobClient jobs)
    {
        _repo      = repo;
        _workflow  = workflow;
        _docStatus = docStatus;
        _jobs      = jobs;
    }

    public async Task<Guid> CreateAsync(CreateGrnRequest req, int createdBy)
    {
        var uuid = await _repo.CreateAsync(req, createdBy);
        var grn  = await _repo.GetByIdAsync(uuid);

        if (grn is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                grn.TraceId,
                new TimelineEvent("GRN_CREATED", "GRN", uuid, grn.GrnNumber, DateTime.UtcNow, createdBy, null),
                "GRN", grn.GrnNumber));

        return uuid;
    }

    public Task UpdateAsync(Guid uuid, PatchGrnRequest req, int modifiedBy) =>
        _repo.UpdateAsync(uuid, req, modifiedBy);

    public Task DeleteAsync(Guid uuid, int deletedBy) =>
        _repo.DeleteAsync(uuid, deletedBy);

    public Task UpdateLineAsync(Guid grnUuid, Guid lineUuid, UpdateGrnLineRequest req, int modifiedBy) =>
        _repo.UpdateLineAsync(grnUuid, lineUuid, req, modifiedBy);

    public Task LinkLineProductAsync(Guid grnUuid, Guid lineUuid, Guid productUuid, int modifiedBy) =>
        _repo.LinkLineProductAsync(grnUuid, lineUuid, productUuid, modifiedBy);

    public Task InspectLineAsync(Guid grnUuid, Guid lineUuid, InspectGrnLineRequest req, int inspectedBy) =>
        _repo.InspectLineAsync(grnUuid, lineUuid, req, inspectedBy);

    public async Task SubmitAsync(Guid grnUuid, int modifiedBy)
    {
        // Validate DRAFT + set IsPartialReceipt (no status change yet)
        await _repo.SubmitAsync(grnUuid, modifiedBy);

        var grn        = await _repo.GetByIdAsync(grnUuid);
        var totalValue = grn!.Lines.Sum(l => l.QtyReceived * (l.UnitCost ?? 0m));

        // If inspection is required, start the QC workflow first.
        // GrnQcStatusHandler will set status → PENDING_QC and, on full QC approval,
        // will automatically submit to the GRN approval workflow.
        // If no inspection is needed, go straight to the GRN approval workflow.
        var interfaceCode = grn.RequiresInspection ? "GRN_QC" : "GRN";

        await _workflow.SubmitAsync(new SubmitDocumentCommand
        {
            InterfaceCode  = interfaceCode,
            DocumentId     = grnUuid,
            DocumentNumber = grn.GrnNumber,
            ConditionValue = totalValue,
            SubmittedBy    = modifiedBy
        });
    }

    // QC confirm: advances the current GRN_QC workflow step.
    // When the final QC step is approved, GrnQcStatusHandler auto-submits to the GRN approval workflow.
    public Task QcConfirmAsync(Guid grnUuid, QcConfirmRequest req, int confirmedBy) =>
        _workflow.ApproveByDocumentAsync("GRN_QC", grnUuid, confirmedBy, req.QcNotes);

    // QC reject: rejects the GRN_QC workflow, which sets GRN status to REJECTED.
    public Task QcRejectAsync(Guid grnUuid, QcRejectRequest req, int rejectedBy) =>
        _workflow.RejectByDocumentAsync("GRN_QC", grnUuid, rejectedBy, req.Reason);

    public async Task ApproveAsync(Guid grnUuid, int approvedBy, string? remarks = null)
    {
        try
        {
            await _workflow.ApproveByDocumentAsync("GRN", grnUuid, approvedBy, remarks);
        }
        catch (NotFoundException)
        {
            // GRN has no workflow record — approve directly.
            // Handles GRNs created before the workflow engine was properly configured.
            await _docStatus.UpdateStatusAsync("GRN", grnUuid, "APPROVED");
        }

        var grn = await _repo.GetByIdAsync(grnUuid);
        if (grn is not null)
        {
            var accepted = grn.Lines.Sum(l => l.QtyAccepted);
            var rejected = grn.Lines.Sum(l => l.QtyRejected);
            var notes    = $"Accepted: {accepted:F4}, Rejected: {rejected:F4}.";

            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                grn.TraceId,
                new TimelineEvent("GRN_APPROVED", "GRN", grnUuid, grn.GrnNumber, DateTime.UtcNow, approvedBy, notes),
                "GRN", grn.GrnNumber));
        }
    }

    public async Task RejectAsync(Guid grnUuid, int rejectedBy, string rejectionReason)
    {
        try
        {
            await _workflow.RejectByDocumentAsync("GRN", grnUuid, rejectedBy, rejectionReason);
        }
        catch (NotFoundException)
        {
            // GRN has no workflow record — reject directly.
            // Handles GRNs created before the workflow engine was properly configured
            // (mirrors the same fallback in ApproveAsync above).
            await _docStatus.UpdateStatusAsync("GRN", grnUuid, "REJECTED");
        }

        var grn = await _repo.GetByIdAsync(grnUuid);
        if (grn is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                grn.TraceId,
                new TimelineEvent("GRN_REJECTED", "GRN", grnUuid, grn.GrnNumber, DateTime.UtcNow, rejectedBy, rejectionReason),
                "GRN", grn.GrnNumber));
    }

    public Task<PaginatedResponse<GrnListItemModel>> GetListAsync(GrnListFilter filter) =>
        _repo.GetListAsync(filter);

    public Task<GrnDetailModel?> GetByIdAsync(Guid uuid) =>
        _repo.GetByIdAsync(uuid);
}
