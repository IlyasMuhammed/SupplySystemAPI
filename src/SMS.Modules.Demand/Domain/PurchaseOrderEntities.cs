namespace SMS.Modules.Demand.Domain;

internal class PurchaseOrder
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public string PoNumber { get; set; } = string.Empty;
    public string? Title { get; set; }
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string Status { get; set; } = "DRAFT"; // DRAFT | APPROVED | SENT | PARTIALLY_RECEIVED | RECEIVED | PARTIALLY_INVOICED | CLOSED | CANCELLED
    public decimal TotalAmount { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public string? Notes { get; set; }
    public string? InternalNotes { get; set; }
    public Guid? DeliveryWarehouseId { get; set; }
    public string? DeliveryWarehouseName { get; set; }
    public string? SupplierContactMobile { get; set; }  // verified E.164 mobile captured at Send time
    public bool IsActive { get; set; } = true;
    public bool IsDelete { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public int? ModifiedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public ICollection<PurchaseOrderLine> Lines { get; set; } = new List<PurchaseOrderLine>();
    public ICollection<PurchaseOrderPrLink> PrLinks { get; set; } = new List<PurchaseOrderPrLink>();
}

internal class PurchaseOrderLine
{
    public int Id { get; set; }
    public Guid UUID { get; set; }
    public int PurchaseOrderId { get; set; }
    public int LineNo { get; set; }
    public Guid? SourcePrLineUuid { get; set; } // UUID reference to pr_lines — no FK (cross-module safe)
    public Guid? ProductUuid { get; set; }       // UUID reference to inventory products — no FK (cross-module safe)
    public string ItemDescription { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public decimal QtyReceived { get; set; }   // cumulative qty received across all GRNs
    public decimal QtyInvoiced { get; set; }   // cumulative qty invoiced across all approved invoices
    public DateTime? RequiredDate { get; set; }
    public string? LineNotes { get; set; }
    public string? BudgetCode { get; set; }
    public Guid? WarehouseId { get; set; }     // per-line override; null means use PO header DeliveryWarehouseId
    public string? WarehouseName { get; set; }
    // Decided at PO creation; inherited by the GRN line when goods are received — QC is
    // skipped entirely for lines where this is false.
    public bool RequiresInspection { get; set; } = true;
    public PurchaseOrder PurchaseOrder { get; set; } = null!;

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public Guid? EffectiveWarehouseId => WarehouseId ?? PurchaseOrder?.DeliveryWarehouseId;

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? EffectiveWarehouseName => WarehouseName ?? PurchaseOrder?.DeliveryWarehouseName;
}

internal class PurchaseOrderPrLink
{
    public int Id { get; set; }
    public int PurchaseOrderId { get; set; }
    public Guid PrUuid { get; set; } // UUID reference to purchase_requisitions — no FK
    public PurchaseOrder PurchaseOrder { get; set; } = null!;
}
