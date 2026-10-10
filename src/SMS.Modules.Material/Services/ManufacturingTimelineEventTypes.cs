namespace SMS.Modules.Material.Services;

/// <summary>
/// A30-P5-07 — the manufacturing document trail's own event types, appended the same way every
/// other document in this codebase does (<see cref="SMS.WorkflowEngine.Jobs.ITimelineAppendJob"/>).
/// Ten codes, matching the FSD's own count (§38): one per state a person or a system decision moves
/// a manufacturing document through, from a recipe going live to a receipt crediting stock — the
/// whole SO → PO → SR → MI → QI → FGR chain shares one trace_id (see the interface codes below and
/// each document's own trace propagation), so all ten show on one timeline for one job.
/// </summary>
internal static class ManufacturingTimelineEventTypes
{
    public const string BomActivated      = "BOM_ACTIVATED";
    public const string ProdCreated       = "PROD_CREATED";
    public const string ProdPlanned       = "PROD_PLANNED";
    public const string ProdMaterialPending = "PROD_MATERIAL_PENDING";
    public const string SrCreated         = "SR_CREATED";
    public const string ProdStarted       = "PROD_STARTED";
    public const string ProdCompleted     = "PROD_COMPLETED";
    public const string MiConfirmed       = "MI_CONFIRMED";
    public const string QiRecorded        = "QI_RECORDED";
    public const string FgrConfirmed      = "FGR_CONFIRMED";
    /// <summary>A34 D-20 — a DRAFT delivery was created from this make-to-order order (ProductionDeliveryHandoff).</summary>
    public const string ProdDeliveryCreated = "PROD_DELIVERY_CREATED";
}

/// <summary>A36 D-14 — service order timeline events, under interface code SERVICE_ORDER (documentId = the order's UUID).</summary>
internal static class ServiceTimelineEventTypes
{
    public const string InterfaceCode  = "SERVICE_ORDER";
    public const string Created        = "SERVICE_ORDER_CREATED";
    public const string Planned        = "SERVICE_ORDER_PLANNED";
    public const string Started        = "SERVICE_ORDER_STARTED";
    public const string MaterialIssued = "SERVICE_ORDER_MATERIAL_ISSUED";
    public const string Waiting        = "SERVICE_ORDER_WAITING";
    public const string Completed      = "SERVICE_ORDER_COMPLETED";
    public const string Closed         = "SERVICE_ORDER_CLOSED";
    public const string Cancelled      = "SERVICE_ORDER_CANCELLED";
}

/// <summary>The interface codes these events (and <see cref="SMS.Shared.Common.ITraceIdResolver"/>) are tagged with.</summary>
internal static class ManufacturingInterfaceCodes
{
    public const string Bom              = "BOM";
    public const string ProductionOrder  = "PROD";
    public const string SupplyRequirement = "SR";
}
