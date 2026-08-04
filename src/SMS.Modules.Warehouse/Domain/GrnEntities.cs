using SMS.Shared.Common;

namespace SMS.Modules.Warehouse.Domain;

internal class Grn : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public Guid OrganizationId { get; set; }
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
    // Status: DRAFT | PENDING_QC | PENDING_FINANCE | PENDING_APPROVAL | APPROVED | REJECTED
    public string Status { get; set; } = "DRAFT";

    // QC step (performed by QC Officer with GRN_QC_CONFIRM)
    public bool QcPassed { get; set; }
    public string? QcNotes { get; set; }
    public int? QcDoneBy { get; set; }
    public int? QcConfirmedBy { get; set; }
    public DateTime? QcConfirmedAt { get; set; }
    public int? QcRejectedBy { get; set; }
    public DateTime? QcRejectedAt { get; set; }

    // Finance step (optional, triggered when GRN value > 500,000 PKR)
    public bool FinanceApprovalRequired { get; set; }
    public int? FinanceApprovedBy { get; set; }
    public DateTime? FinanceApprovedAt { get; set; }
    public int? FinanceRejectedBy { get; set; }
    public DateTime? FinanceRejectedAt { get; set; }

    // Inventory Manager approval step
    public int ReceivedBy { get; set; }
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? ApprovalDeadline { get; set; }  // SLA: set to UtcNow+24h on PENDING_APPROVAL entry

    // Rejection
    public string? RejectionReason { get; set; }

    public string? InvoiceNo { get; set; }
    public string? Notes { get; set; }
    public bool RequiresInspection { get; set; } = true;
    public DateTime? InspectionCompletedAt { get; set; }
    public bool IsPartialReceipt { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDelete { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public ICollection<GrnLine> Lines { get; set; } = new List<GrnLine>();
    public ICollection<SupplierReturnOrder> ReturnOrders { get; set; } = new List<SupplierReturnOrder>();
}

internal class SupplierReturnOrder : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid OrganizationId { get; set; }

    // SRO number (SRO-YYYY-NNNNN)
    public string ReturnNumber { get; set; } = string.Empty;

    // Type: GRN_REJECTION | POST_RECEIPT_DEFECT | WRONG_ITEM | OVERDELIVERY
    public string SroType { get; set; } = "POST_RECEIPT_DEFECT";

    // Supplier info (denormalised for standalone access)
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;

    // GRN reference (nullable — manual SROs have no GRN)
    public int? GrnId { get; set; }
    public Guid? GrnUuid { get; set; }
    public string? GrnNumber { get; set; }

    // Source warehouse (denormalised at creation; drives inventory deduction at dispatch)
    public Guid? WarehouseUuid { get; set; }

    // PO reference (optional)
    public Guid? OriginalPoUuid { get; set; }
    public string? OriginalPoNumber { get; set; }

    // Return reason header: DAMAGED | DEFECTIVE | WRONG_ITEM | WRONG_QTY | SHORT_EXPIRY | SPEC_MISMATCH | DUPLICATE | QUALITY_FAIL | OTHER
    public string ReturnReason { get; set; } = "DAMAGED";
    public string? ReturnReasonDetail { get; set; }

    // Dispatch info (filled when DISPATCHED)
    public string? RmaNumber { get; set; }
    public DateTime? DispatchDate { get; set; }
    public string? DispatchCarrier { get; set; }
    public string? DispatchTrackingRef { get; set; }

    // Resolution (filled when RESOLVED_*)
    public string? ResolutionType { get; set; }  // CREDIT | REPLACEMENT | DEBIT
    public DateTime? ResolvedAt { get; set; }

    // Status: DRAFT | APPROVED | REJECTED | DISPATCHED | SUPPLIER_RECEIVED | AWAITING_REPLACEMENT | RESOLVED_CREDIT | RESOLVED_REPLACEMENT | RESOLVED_DEBIT | ESCALATED
    public string Status { get; set; } = "DRAFT";

    // Approval
    public int? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    // Rejection of the SRO itself
    public string? RejectionReason { get; set; }

    // SLA tracking — set to DispatchDate + 14 days when dispatched
    public DateTime? SlaDeadline { get; set; }

    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }

    public Grn? Grn { get; set; }
    public ICollection<SupplierReturnOrderLine> Lines { get; set; } = new List<SupplierReturnOrderLine>();
}

internal class SupplierReturnOrderLine : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid OrganizationId { get; set; }
    public int ReturnOrderId { get; set; }
    public SupplierReturnOrder ReturnOrder { get; set; } = null!;

    public int LineNo { get; set; }

    // Optional back-references
    public Guid? GrnLineUuid { get; set; }
    public Guid? PoLineUuid { get; set; }
    public Guid? ProductUuid { get; set; }

    public string ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure { get; set; }
    public decimal QtyToReturn { get; set; }

    // Per-line reason: DAMAGED | DEFECTIVE | WRONG_ITEM | WRONG_QTY | SHORT_EXPIRY | SPEC_MISMATCH | DUPLICATE | QUALITY_FAIL | OTHER
    public string ReturnReason { get; set; } = "DAMAGED";
    public string? ReturnReasonDetail { get; set; }
    public string? Condition { get; set; }
    public decimal? UnitCost { get; set; }
}

internal class GrnLine : ITenantScopedEntity
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid OrganizationId { get; set; }
    public int GrnId { get; set; }
    public Guid PoLineUuid { get; set; }   // UUID ref to purchase_order_lines — no FK (cross-module safe)
    // Inherited from the source PO line at GRN creation — decided at PO time, not editable here.
    public bool RequiresInspection { get; set; } = true;
    public Guid? ProductUuid { get; set; }
    public int LineNo { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? UnitOfMeasure { get; set; }
    public decimal QtyOrdered { get; set; }   // pending qty from PO line at time of GRN creation
    public decimal QtyReceived { get; set; }
    public decimal QtyAccepted { get; set; }
    public decimal QtyRejected { get; set; }
    public string? RejectionReason { get; set; }
    public Guid? BinUuid { get; set; }
    public string? BatchNumber  { get; set; }
    public string? SerialNumber { get; set; }
    public DateTime? ExpiryDate { get; set; }
    public decimal? UnitCost { get; set; }
    public bool HasVariance { get; set; }
    public string? QcResult { get; set; }  // PASS | FAIL | PARTIAL
    // Formal inspection fields (set during PENDING_QC by QC officer)
    public string? InspectionResult { get; set; }  // Pass | Fail | PartialPass
    public string? InspectorRemarks { get; set; }
    public int? InspectedBy { get; set; }
    public DateTime? InspectedAt { get; set; }
    public Grn Grn { get; set; } = null!;
}
