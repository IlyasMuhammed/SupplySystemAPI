using SMS.Modules.Material.Models;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A31 C9 §11 — the Consolidated Purchase Required dashboard: material shortages aggregated per
/// variant across every open production order, so procurement raises one purchase order for a
/// product short across several orders instead of one per order (the duplicate-PO problem the FSD's
/// own §11.1 describes, and that PC-06's automatic consolidation already independently narrowed).
/// </summary>
public interface IPurchaseRequiredService
{
    Task<IReadOnlyList<PurchaseRequiredLineModel>> GetListAsync(PurchaseRequiredListFilter filter);
    Task<IReadOnlyList<PurchaseRequiredAffectedOrderModel>> GetAffectedOrdersAsync(Guid variantUuid);

    /// <summary>Marks this variant's current shortage as purchased outside the system. Upserts — a
    /// second call just replaces the notes/timestamp, it does not duplicate the marker.</summary>
    Task AcknowledgeAsync(Guid variantUuid, AcknowledgePurchaseRequiredRequest req, int userId);
    /// <summary>Removes the marker — "this needs a purchase order again." Idempotent.</summary>
    Task ClearAcknowledgementAsync(Guid variantUuid);

    /// <summary>The dashboard's own "Create Purchase Order" button (FSD §11.4) — raises or
    /// consolidates onto an open Draft PO for the chosen supplier and links every live purchase-
    /// method supply requirement for this variant to it, so it stops showing as needing one.</summary>
    Task<Guid> CreatePurchaseOrderAsync(Guid variantUuid, CreatePurchaseRequiredPoRequest req, int userId);
}
