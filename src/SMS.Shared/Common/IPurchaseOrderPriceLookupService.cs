namespace SMS.Shared.Common;

// RC-002/RC-003 (FSD Addendum 28) — shared interface so SMS.Modules.Inventory can resolve "last PO
// price/date" for a batch of (variant, supplier) pairs without a project reference to
// SMS.Modules.Demand. The implementation lives in SMS.Modules.Demand and is resolved via the DI
// container — batch shape mirrors IStockAvailabilityService.GetAvailabilityForVariantsAsync,
// isolation mechanism mirrors ISupplierContactLookupService.
public interface IPurchaseOrderPriceLookupService
{
    /// <summary>
    /// For each (VariantUuid, SupplierId) pair, the price and PO date of that pair's most recent
    /// PurchaseOrderLine — considering only purchase orders that were actually approved/sent (not
    /// DRAFT/PENDING_APPROVAL/REJECTED/CANCELLED). Pairs with no qualifying PO line are omitted
    /// from the result (not present in the dictionary), not returned as null.
    /// </summary>
    Task<IReadOnlyDictionary<(Guid VariantUuid, Guid SupplierId), LastPoInfo>> GetLastPricesAsync(
        IReadOnlyList<(Guid VariantUuid, Guid SupplierId)> pairs);

    /// <summary>
    /// RC-004 — richer single-pair summary for the rate-edit panel's PO Reference section: the
    /// most recent qualifying PO's identity (for a deep link), date, price, and how many qualifying
    /// POs exist for this pair in the trailing 12 months. Null when none exist yet.
    /// </summary>
    Task<PoReferenceSummary?> GetPoReferenceSummaryAsync(Guid variantUuid, Guid supplierId);
}

public sealed record LastPoInfo(decimal UnitPrice, DateTime PoDate);

public sealed record PoReferenceSummary(
    Guid LastPoUuid, string LastPoNumber, DateTime LastPoDate, decimal LastPoPrice, int PoCountLast12Months);
