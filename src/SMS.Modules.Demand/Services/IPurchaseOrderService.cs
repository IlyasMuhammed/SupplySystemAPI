using SMS.Modules.Demand.Models;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Services;

public interface IPurchaseOrderService
{
    Task<Guid> CreateFromPrAsync(Guid prUuid, ConvertPrToPoRequest req, int createdBy);
    Task<List<Guid>> CreateFromPrSplitAsync(Guid prUuid, ConvertPrSplitRequest req, int createdBy);
    Task<Guid> CreateAsync(CreatePoRequest req, int createdBy);
    // A31-C3/BR-C3-07 — appends to an existing open PRODUCTION-sourced Draft PO for this supplier
    // when one exists, rather than raising a duplicate; creates fresh otherwise.
    Task<PoConsolidationResult> AddOrIncreaseProductionLineAsync(CreatePoRequest req, int createdBy);
    // A31-C3 §5.4 tier 2 — the supplier of the most recent non-Draft, non-dead PO line for this
    // variant, or null when this variant has never actually been bought before.
    Task<Guid?> GetLastSupplierForVariantAsync(Guid variantUuid);
    // A29-P5-11 §6.2 — returns what changed field by field for a PO the system raised for a sale
    // order (empty for any other), which is what the controller writes to the audit log.
    Task<IReadOnlyList<PoFieldChange>> UpdateAsync(Guid uuid, PatchPoRequest req, int modifiedBy);
    // A29-P5-11 §6.2 — move part of a line of a DRAFT sale-order PO to a new PO for another supplier.
    Task<SplitPoResult> SplitAsync(Guid uuid, SplitPoRequest req, int userId);
    Task SubmitForApprovalAsync(Guid uuid, int userId);
    Task SendAsync(Guid uuid, string? contactMobile, int modifiedBy);
    Task<PaginatedResponse<PoListItemModel>> GetListAsync(PoListFilter filter);
    Task<PoDetailModel?> GetByIdAsync(Guid uuid);
    Task<List<PoSearchItemModel>> SearchForGrnAsync(string? q, bool receivableOnly);
    Task ApproveAsync(Guid poUuid, int approvedBy, string? remarks = null);
    Task RejectAsync(Guid poUuid, int rejectedBy, string rejectionReason);
    // A29-P4-04 §4.5.
    Task CancelAsync(Guid uuid, int userId, string? reason);

    // A29-P5-08 §13.5 — the PO's whole trace. A back-to-back PO carries its sale order's trace_id
    // (§6.3.1), so this shows the sale order's own events too, and vice versa. Null when the PO
    // doesn't exist in the caller's organization or its trace has no events yet.
    Task<TimelineDetail?> GetTimelineAsync(Guid uuid);
}
