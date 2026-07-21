namespace SMS.Modules.Warehouse.Models;

// ── Request models ────────────────────────────────────────────────────────────

public class GrnLineReceiveInput
{
    public Guid PoLineUuid { get; set; }
    public decimal QtyReceived { get; set; }
    public decimal QtyAccepted { get; set; }
    public decimal QtyRejected { get; set; }
    // Lookup: Damaged | Wrong item | Short expiry | Over qty
    public string? RejectionReason { get; set; }
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

public class CreateGrnRequest
{
    public Guid PoUuid { get; set; }
    public Guid? WarehouseUuid { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string? DeliveryNoteNo { get; set; }
    public string? VehicleNo { get; set; }
    public string? DriverName { get; set; }
    public string? InvoiceNo { get; set; }
    public string? Notes { get; set; }
    public bool RequiresInspection { get; set; } = true;
    // Optional: pre-fill received quantities at creation time
    public List<GrnLineReceiveInput>? Lines { get; set; }
    public DateTime ReceivedDate { get; internal set; }
}

public class PatchGrnRequest
{
    public Guid? WarehouseUuid { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public string? DeliveryNoteNo { get; set; }
    public string? VehicleNo { get; set; }
    public string? DriverName { get; set; }
    public string? InvoiceNo { get; set; }
    public string? Notes { get; set; }
    public bool? RequiresInspection { get; set; }
}

public class UpdateGrnLineRequest
{
    public Guid? ProductUuid { get; set; }
    public decimal QtyReceived { get; set; }
    public decimal QtyAccepted { get; set; }
    public decimal QtyRejected { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? BinUuid { get; set; }
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public decimal? UnitCost { get; set; }
    public string? QcResult { get; set; }  // PASS | FAIL | PARTIAL
}

public class LinkGrnLineProductRequest
{
    public Guid ProductUuid { get; set; }
}

// Formal inspection recording (PENDING_QC status only)
public class InspectGrnLineRequest
{
    // Pass | Fail | PartialPass
    public string InspectionResult { get; set; } = string.Empty;
    // Required for Pass/Fail (auto-computed); required for PartialPass (manual, must sum to QtyReceived)
    public decimal QtyAccepted { get; set; }
    public decimal QtyRejected { get; set; }
    // Required when QtyRejected > 0
    public string? RejectionReason { get; set; }
    public string? InspectorRemarks { get; set; }
}

// QC Officer confirms that goods passed quality inspection
public class QcConfirmRequest
{
    public string? QcNotes { get; set; }
}

// QC Officer rejects — moves GRN to REJECTED
public class QcRejectRequest
{
    public string Reason { get; set; } = string.Empty;
}

// Finance Officer approves high-value GRN — moves to PENDING_APPROVAL
public class FinanceApproveRequest { }

// Finance Officer rejects high-value GRN — moves to REJECTED
public class FinanceRejectRequest
{
    public string Reason { get; set; } = string.Empty;
}

// Inventory Manager final reject — creates supplier return order
public class InventoryManagerRejectRequest
{
    public string Reason { get; set; } = string.Empty;
}

// Legacy kept for backward-compat deserialization — QC info is now on QcConfirmRequest
public class ApproveGrnRequest
{
    public bool QcPassed { get; set; }
    public string? QcNotes { get; set; }
}

// ── Filter ────────────────────────────────────────────────────────────────────

public class GrnListFilter
{
    public string? Status { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid? PoUuid { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

// ── Response models ───────────────────────────────────────────────────────────

public class GrnListItemModel
{
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public string GrnNumber { get; set; } = string.Empty;
    public Guid PoUuid { get; set; }
    public string PoNumber { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsPartialReceipt { get; set; }
    public bool FinanceApprovalRequired { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime CreatedDate { get; set; }
}

public class GrnDetailModel
{
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public string GrnNumber { get; set; } = string.Empty;
    public Guid PoUuid { get; set; }
    public string PoNumber { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? WarehouseUuid { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string? DeliveryNoteNo { get; set; }
    public string? VehicleNo { get; set; }
    public string? DriverName { get; set; }
    public string Status { get; set; } = string.Empty;

    // QC
    public bool QcPassed { get; set; }
    public string? QcNotes { get; set; }
    public int? QcDoneBy { get; set; }
    public int? QcConfirmedBy { get; set; }
    public DateTime? QcConfirmedAt { get; set; }

    // Finance
    public bool FinanceApprovalRequired { get; set; }
    public int? FinanceApprovedBy { get; set; }
    public DateTime? FinanceApprovedAt { get; set; }

    // IM Approval
    public int ReceivedBy { get; set; }
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ApprovalDeadline { get; set; }

    // Rejection
    public string? RejectionReason { get; set; }

    public string? InvoiceNo { get; set; }
    public string? Notes { get; set; }
    public bool RequiresInspection { get; set; }
    public DateTime? InspectionCompletedAt { get; set; }
    public bool InspectionComplete { get; set; }
    public int InspectedLineCount { get; set; }
    public int TotalLineCount { get; set; }
    public bool IsPartialReceipt { get; set; }
    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
    public List<GrnLineModel> Lines { get; set; } = new();
}

public class GrnLineModel
{
    public Guid UUID { get; set; }
    public Guid PoLineUuid { get; set; }
    public Guid? ProductUuid { get; set; }
    public int LineNo { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure { get; set; }
    public decimal QtyOrdered { get; set; }
    public decimal QtyReceived { get; set; }
    public decimal QtyAccepted { get; set; }
    public decimal QtyRejected { get; set; }
    // Lookup values: Damaged | Wrong item | Short expiry | Over qty
    public string? RejectionReason { get; set; }
    public Guid? BinUuid { get; set; }
    public string? BatchNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public decimal? UnitCost { get; set; }
    public bool HasVariance { get; set; }
    public string? QcResult { get; set; }
    public string? InspectionResult { get; set; }
    public string? InspectorRemarks { get; set; }
    public int? InspectedBy { get; set; }
    public DateTime? InspectedAt { get; set; }
}
