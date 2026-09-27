using SMS.Modules.Demand.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Repositories;

internal interface IPurchaseOrderRepository
{
    Task<Guid> CreateFromPrAsync(Guid prUuid, ConvertPrToPoRequest req, int createdBy);
    Task<List<Guid>> CreateFromPrSplitAsync(Guid prUuid, ConvertPrSplitRequest req, int createdBy);
    Task<Guid> CreateAsync(CreatePoRequest req, int createdBy);
    // A31-C3/BR-C3-07 — finds an open (DRAFT) PO this same mechanism already raised for this
    // supplier and appends to it (a new line, or a bumped quantity on a matching one) instead of
    // raising a second PO; creates fresh via CreateAsync when none exists yet. req.Lines must have
    // exactly one line — the one shortage being acted on.
    Task<PoConsolidationResult> AddOrIncreaseProductionLineAsync(CreatePoRequest req, int createdBy);
    Task<Guid?> GetLastSupplierForVariantAsync(Guid variantUuid);
    // A29-P5-03 §6.1/§6.3 — a PO generated from one sale order line's deficit. Decides nothing about
    // supplier, price or quantity; the caller already did.
    Task<CreatedPurchaseOrder> CreateFromSaleOrderDeficitAsync(SaleOrderDeficitPo spec, int createdBy);
    // A29-P5-11 §6.2 — returns what changed, field by field, for a PO the system raised for a sale
    // order (the audit log's input); empty for any other PO.
    Task<IReadOnlyList<PoFieldChange>> UpdateAsync(Guid uuid, PatchPoRequest req, int modifiedBy);
    // A29-P5-11 §6.2 — DRAFT POs raised for a sale order line only.
    Task<SplitPoResult> SplitAsync(Guid uuid, SplitPoRequest req, int userId);
    Task SendAsync(Guid uuid, string? contactMobile, int modifiedBy);
    // A29-P4-04 §4.5 — DRAFT only; a PO already SENT/APPROVED/etc. needs a real cancellation
    // workflow, not this. The caller decides whether "not DRAFT" is worth surfacing as an error.
    Task CancelAsync(Guid uuid, string? reason, int modifiedBy);
    Task<PaginatedResponse<PoListItemModel>> GetListAsync(PoListFilter filter);
    Task<PoDetailModel?> GetByIdAsync(Guid uuid);
    Task<List<PoSearchItemModel>> SearchForGrnAsync(string? q, bool receivableOnly);
}

/// <param name="Status">The status to create the PO in — DRAFT or APPROVED. PENDING_APPROVAL is never
/// created directly: it means "submitted to the workflow engine", which only the engine can do.</param>
/// <param name="TraceId">The sale order's own trace id (§6.3.1), so one trace spans SO to PO to GRN.</param>
internal sealed record SaleOrderDeficitPo(
    string  Source,
    string  Status,
    Guid    TraceId,
    int     SaleOrderId,
    int     SaleOrderLineId,
    string  SoNumber,
    Guid    SupplierId,
    string  SupplierName,
    Guid    VariantUuid,
    decimal Quantity,
    decimal UnitPrice,
    Guid?   CustomerShippingAddressId);

/// <param name="Sku">Null when the variant couldn't be resolved (no Inventory context, or it's gone).</param>
internal sealed record CreatedPurchaseOrder(Guid Uuid, int Id, string PoNumber, string? Sku);
