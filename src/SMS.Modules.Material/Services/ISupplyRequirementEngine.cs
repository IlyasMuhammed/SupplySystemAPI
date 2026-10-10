using SMS.Modules.Demand.Models;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;

namespace SMS.Modules.Material.Services;

/// <summary>
/// What happens to a shortage a production order cannot cover from stock or supply already on its
/// way (A30 §13, BR-S01–S07): raise the paperwork that will bring the rest in.
/// </summary>
internal interface ISupplyRequirementEngine
{
    /// <summary>
    /// Raises or refreshes the one live supply requirement for a requirement's shortage, then acts
    /// on it — a purchase order, a child production order, or a note for a transfer nothing here
    /// automates. Idempotent per requirement: an already-ordered requirement's supply requirement is
    /// left alone, and a shortage that has closed cancels an unordered one. Returns null when there
    /// is, and remains, nothing to raise.
    /// </summary>
    Task<SupplyRequirement?> EnsureForShortageAsync(
        ProductionMaterialRequirement pmr, ProductionOrder productionOrder, int userId, int depth, CancellationToken ct = default);

    /// <summary>
    /// A36 D-6 / SVC-SR-01..02 — the same idempotent raise-or-refresh for a service order requirement
    /// (demand source SERVICE_ORDER, one live SR per requirement). A SUBCONTRACT requirement is always bought
    /// (PURCHASE) from the BOM line's own vendor; a STOCK shortage follows the material's supply method, as production.
    /// </summary>
    Task<SupplyRequirement?> EnsureForServiceAsync(
        ServiceMaterialRequirement smr, ServiceOrder serviceOrder, int userId, CancellationToken ct = default);

    /// <summary>A person raising a supply requirement directly, with nothing behind it but their own say-so.</summary>
    Task<Guid> CreateManualAsync(CreateSupplyRequirementRequest req, int userId, CancellationToken ct = default);

    Task CancelAsync(Guid uuid, string reason, int userId, CancellationToken ct = default);

    /// <summary>
    /// A31 C9 — the Purchase Required dashboard's own "Create Purchase Order" action: the same
    /// consolidate-onto-an-open-Draft-PO-for-this-supplier mechanism <see cref="EnsureForShortageAsync"/>
    /// uses automatically (<c>ActPurchaseAsync</c>), but with a person's own chosen supplier, quantity,
    /// price and required-by date instead of auto-resolved ones. Links every live purchase-method
    /// supply requirement for the variant to the resulting PO line (so it stops showing as needing a
    /// purchase, on this dashboard and everywhere else a supply requirement's own status is read) and
    /// registers the line's cumulative expected supply. A variant with no open requirement to link
    /// still gets its PO — nothing here refuses a person's own purchase — it just links nothing.
    /// </summary>
    Task<PoConsolidationResult> CreatePurchaseOrderForShortagesAsync(
        Guid variantUuid, Guid supplierId, string supplierName, decimal quantity, decimal unitPrice,
        DateTime requiredDate, string? notes, int userId, CancellationToken ct = default);
}
