namespace SMS.Modules.Demand.Services;

// A29-P5-02 §3.3 — picks a supplier for a back-to-back/deficit PO line when auto_po_enabled and
// stock is short (§4.3's BACK_TO_BACK/SPLIT scenarios). Mode is read from SaleOrderConfig, not
// passed by the caller, so every consumer picks the same way the org has configured rather than
// each call site holding its own opinion.
public interface ISupplierSelectionService
{
    Task<SupplierSelectionResult> SelectAsync(Guid variantUuid, decimal quantity, int userId);
}

/// <param name="RequiresManualSelection">
/// True when no supplier could be chosen automatically — the org is configured for MANUAL, or
/// DEFAULT_SUPPLIER/BEST_MATCH found nothing to pick from (no default set, or no active vendor
/// supplies the variant at all). The caller creates no PO in this case; <see cref="ISupplierSelectionService.SelectAsync"/>
/// itself raises §3.3's "notification/task for the supply team" before returning.
/// </param>
/// <param name="Mode">Which of the three §3.3 modes actually produced this result — DEFAULT_SUPPLIER
/// and BEST_MATCH both fall through to MANUAL when they find nothing, so this can differ from
/// whatever the org's SaleOrderConfig says.</param>
public sealed record SupplierSelectionResult(
    bool     RequiresManualSelection,
    Guid?    SupplierId,
    string?  SupplierName,
    decimal? UnitPrice,
    string   Mode,
    string   Reason);
