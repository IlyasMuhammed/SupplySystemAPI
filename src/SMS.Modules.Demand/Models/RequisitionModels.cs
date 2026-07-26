using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Models;

// ── Requests ─────────────────────────────────────────────────────────────────

public class CreatePrRequest
{
    public string PrTitle { get; set; } = string.Empty;
    public string? Department { get; set; }
    public DateTime RequestedDate { get; set; }
    public string? Priority { get; set; }
    public string? PrType { get; set; }
    public bool RequiresQuotation { get; set; }
    public string? Justification { get; set; }
    public Guid? WarehouseUuid { get; set; }
    public string? Notes { get; set; }
    public List<CreatePrLineRequest> Lines { get; set; } = [];
    // Client-generated id so attachments uploaded before save can be linked via the same
    // DocumentId — becomes the PR's own UUID on save.
    public Guid? PrUuid { get; set; }
}

public class CreatePrLineRequest
{
    public Guid? ProductId { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal Quantity { get; set; }
    public decimal EstimatedUnitPrice { get; set; }
    public Guid? PreferredSupplierId { get; set; }
    public bool RequiresQuotation { get; set; }
    public DateTime? RequiredDate { get; set; }
    public string? LineNotes { get; set; }
    public string? BudgetCode { get; set; }
}

public class PatchPrRequest
{
    public string? PrTitle { get; set; }
    public string? Department { get; set; }
    public DateTime? RequestedDate { get; set; }
    public string? Priority { get; set; }
    public string? PrType { get; set; }
    public bool? RequiresQuotation { get; set; }
    public string? Justification { get; set; }
    public Guid? WarehouseUuid { get; set; }
    public bool ClearWarehouse { get; set; }
    public string? Notes { get; set; }
    public List<CreatePrLineRequest>? Lines { get; set; }
}

public class RejectPrRequest
{
    public string RejectionReason { get; set; } = string.Empty;
}

// ── Conversion requests ───────────────────────────────────────────────────────

public class ConvertPrToPoRequest
{
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public DateTime? DeliveryDate { get; set; }
    public string? Notes { get; set; }
}

public class ConvertPrSplitRequest
{
    public List<PrLineVendorAssignment> Lines { get; set; } = [];
    public DateTime? DeliveryDate { get; set; }
    public string? Notes { get; set; }
}

public class PrLineVendorAssignment
{
    public Guid PrLineUuid { get; set; }
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
}

// ── Filters ──────────────────────────────────────────────────────────────────

public class PrListFilter
{
    public string? Status { get; set; }
    public string? Department { get; set; }
    public int? RequesterId { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

// ── Response models ───────────────────────────────────────────────────────────

public class PrListItemModel
{
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public string PrNumber { get; set; } = string.Empty;
    public string PrTitle { get; set; } = string.Empty;
    public string? Department { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime RequestedDate { get; set; }
    public decimal EstimatedTotal { get; set; }
    public int RequesterId { get; set; }
    public DateTime CreatedDate { get; set; }
}

public class PrDetailModel
{
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
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
    public string Status { get; set; } = string.Empty;
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? RejectionReason { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; }
    public int CreatedBy { get; set; }
    public LinkedAwardedQuotationInfo? LinkedAwardedQuotation { get; set; }
    public List<PrLineModel> Lines { get; set; } = [];
}

public class LinkedAwardedQuotationInfo
{
    public string QuotationNumber { get; set; } = string.Empty;
    public Guid AwardedSupplierId { get; set; }
    public string AwardedSupplierName { get; set; } = string.Empty;
}

public class PrLineModel
{
    public Guid UUID { get; set; }
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
    public decimal DisbursedQty { get; set; }
}
