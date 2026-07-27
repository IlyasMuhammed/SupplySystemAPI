namespace SMS.Modules.Demand.Models;

// ── Request models ────────────────────────────────────────────────────────────

public class CreatePoRequest
{
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public List<Guid>? PrIds { get; set; }          // optional: link existing approved PRs
    public List<CreatePoLineRequest>? Lines { get; set; } // used when PrIds is absent
    public DateTime? DeliveryDate { get; set; }
    public Guid? DeliveryWarehouseId { get; set; }
    public string? DeliveryWarehouseName { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public string? InternalNotes { get; set; }
    // Client-generated id so attachments uploaded before save can be linked via the same
    // DocumentId — becomes the PO's own UUID on save.
    public Guid? PoUuid { get; set; }
}

public class CreatePoLineRequest
{
    public Guid? SourcePrLineUuid { get; set; }
    public Guid? ProductUuid { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public DateTime? RequiredDate { get; set; }
    public string? LineNotes { get; set; }
    public string? BudgetCode { get; set; }
    public Guid? WarehouseId { get; set; }
    public string? WarehouseName { get; set; }
    public bool RequiresInspection { get; set; } = true;
}

public class SendPoRequest
{
    public string? SupplierContactMobile { get; set; }
}

public class PatchPoRequest
{
    public string? SupplierName { get; set; }
    public Guid? SupplierId { get; set; }
    public string? Title { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public Guid? DeliveryWarehouseId { get; set; }
    public string? DeliveryWarehouseName { get; set; }
    public string? Notes { get; set; }
    public List<CreatePoLineRequest>? Lines { get; set; }
}

// ── Filter ────────────────────────────────────────────────────────────────────

public class PoListFilter
{
    public string? Status { get; set; }
    public Guid? SupplierId { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

// ── Response models ───────────────────────────────────────────────────────────

public class PoListItemModel
{
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public string PoNumber { get; set; } = string.Empty;
    public string? Title { get; set; }
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public DateTime CreatedDate { get; set; }
}

public class PoDetailModel
{
    public Guid UUID { get; set; }
    public Guid TraceId { get; set; }
    public string PoNumber { get; set; } = string.Empty;
    public string? Title { get; set; }
    public Guid SupplierId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public string? SupplierContactMobile { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public DateTime? DeliveryDate { get; set; }
    public Guid? DeliveryWarehouseId { get; set; }
    public string? DeliveryWarehouseName { get; set; }
    public string? Notes { get; set; }
    public string? InternalNotes { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; }
    public List<PoLineModel> Lines { get; set; } = new();
    public List<Guid> LinkedPrUuids { get; set; } = new();
}

public class PoSearchItemModel
{
    public Guid UUID { get; set; }
    public string PoNumber { get; set; } = string.Empty;
    public string SupplierName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public decimal GrandTotal { get; set; }
    public string? Currency { get; set; }
    public decimal QtyPending { get; set; }
}

public class PoLineModel
{
    public Guid UUID { get; set; }
    public int LineNo { get; set; }
    public Guid? SourcePrLineUuid { get; set; }
    public Guid? ProductUuid { get; set; }
    public string ItemDescription { get; set; } = string.Empty;
    public string? Specification { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public decimal QtyReceived { get; set; }
    public decimal QtyInvoiced { get; set; }
    public decimal QtyPending        => Quantity - QtyReceived;
    public decimal QtyPendingInvoice => QtyReceived - QtyInvoiced;
    public DateTime? RequiredDate { get; set; }
    public string? LineNotes { get; set; }
    public string? BudgetCode { get; set; }
    public Guid? WarehouseId { get; set; }
    public string? WarehouseName { get; set; }
    public Guid? EffectiveWarehouseId { get; set; }
    public string? EffectiveWarehouseName { get; set; }
    public bool RequiresInspection { get; set; } = true;
}
