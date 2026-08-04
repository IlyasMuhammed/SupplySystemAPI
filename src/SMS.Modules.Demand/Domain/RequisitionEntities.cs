using SMS.Shared.Common;

namespace SMS.Modules.Demand.Domain;

internal class PurchaseRequisition : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public Guid OrganizationId { get; set; }

    public string PrNumber { get; set; } = string.Empty;
    public string PrTitle { get; set; } = string.Empty;
    public string? Department { get; set; }
    public int RequesterId { get; set; }
    public DateTime RequestedDate { get; set; }
    public string? Priority { get; set; }
    public string? PrType { get; set; }
    public bool RequiresQuotation { get; set; }
    public string? Justification { get; set; }
    public decimal EstimatedTotal { get; set; }
    public Guid? WarehouseUuid { get; set; }
    public string Status { get; set; } = "DRAFT";
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectionReason { get; set; }
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;
    public bool IsDelete { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public ICollection<PrLine> Lines { get; set; } = new List<PrLine>();
}

internal class PrLine : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid OrganizationId { get; set; }
    public int PurchaseRequisitionId { get; set; }
    public int LineNo { get; set; }
    public Guid? ProductId { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal Quantity { get; set; }
    public decimal EstimatedUnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public Guid? PreferredSupplierId { get; set; }
    public bool RequiresQuotation { get; set; }
    public string? QuotationStatus { get; set; }
    public string? LineStatus { get; set; }
    public DateTime? RequiredDate { get; set; }
    public string? LineNotes { get; set; }
    public string? BudgetCode { get; set; }

    // Disbursement tracking (written only by the MIR-approval handler). NEVER a status/state field —
    // PrLine.LineStatus above remains the sole source of truth for the line's procurement status.
    public decimal DisbursedQty     { get; set; }
    public string  DisbursedMirIds  { get; set; } = "[]";

    public PurchaseRequisition PurchaseRequisition { get; set; } = null!;
}
