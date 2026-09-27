namespace SMS.Modules.Material.Models;

// ── A31 C9 — Consolidated Purchase Required dashboard ──────────────────────────
// Aggregates ProductionMaterialRequirement.ShortageQuantity (what nothing — held or planned —
// covers) across every open production order, per material variant (the real transactable unit
// in this codebase, not the bare product). See the FSD §11 for the feature's own shape.

public class PurchaseRequiredListFilter
{
    public Guid?   SupplierId    { get; set; }
    public decimal? MinShortageQty { get; set; }
    /// <summary>"shortage" (default), "urgency" (earliest required first), or "product" (name).</summary>
    public string? SortBy        { get; set; }
}

public class PurchaseRequiredLineModel
{
    public Guid     VariantUuid          { get; set; }
    public Guid     ProductUuid          { get; set; }
    public string   ProductName          { get; set; } = string.Empty;
    public string   VariantName          { get; set; } = string.Empty;
    public string   Sku                  { get; set; } = string.Empty;
    public string   Uom                  { get; set; } = string.Empty;
    public decimal  TotalShortageQty     { get; set; }
    public int      AffectedPoCount      { get; set; }
    public DateTime EarliestRequiredDate { get; set; }
    public Guid?    DefaultSupplierId    { get; set; }
    public string?  DefaultSupplierName  { get; set; }
    public decimal  CurrentStockOnHand   { get; set; }
    /// <summary>An open PRODUCTION-sourced purchase order already raised for this variant, if PC-06's
    /// automatic consolidation (or a prior manual "Create Purchase Order" here) already created one.</summary>
    public Guid?    PendingPoUuid        { get; set; }
    public string?  PendingPoNumber      { get; set; }
    public decimal? PendingPoQuantity    { get; set; }
    public string?  PendingPoStatus      { get; set; }
    /// <summary>True once someone has marked this shortage as purchased outside the system (BR from the
    /// user's own explicit ask, not in the FSD text) — suppresses the "Create Purchase Order" action.</summary>
    public bool     IsAcknowledgedManually { get; set; }
    public string?  AcknowledgedNotes    { get; set; }
    public DateTime? AcknowledgedAt      { get; set; }
}

public class PurchaseRequiredAffectedOrderModel
{
    public Guid     ProductionOrderUuid { get; set; }
    public string   ProductionNumber    { get; set; } = string.Empty;
    public string   Status              { get; set; } = string.Empty;
    public decimal  PlannedQuantity     { get; set; }
    public decimal  ShortageQuantity    { get; set; }
    public DateTime? PlannedStartDate   { get; set; }
    public DateTime RequiredDate        { get; set; }
}

public class AcknowledgePurchaseRequiredRequest
{
    public string? Notes { get; set; }
}

public class CreatePurchaseRequiredPoRequest
{
    public Guid    SupplierId   { get; set; }
    public string  SupplierName { get; set; } = string.Empty;
    public decimal Quantity     { get; set; }
    public decimal UnitPrice    { get; set; }
    public DateTime RequiredDate { get; set; }
    public string?  Notes       { get; set; }
}
