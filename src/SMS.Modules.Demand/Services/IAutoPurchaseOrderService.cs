namespace SMS.Modules.Demand.Services;

// A29-P5-03 §6.1/§6.2 — turns one sale order line's deficit into a purchase order. Deliberately
// takes supplier, quantity and price as given: choosing them is upstream (ISupplierSelectionService
// for the supplier, §2.4's price waterfall for the price), and sending the "PO created" intimation
// afterwards (§6.1's last step, ISaleOrderEmailService.SendPoCreatedAsync) belongs to whatever
// orchestrates all of it, which has both ids in hand. Whether the org has auto-PO enabled at all
// (SaleOrderConfig.AutoPoEnabled) is likewise the orchestrator's decision, made before it selects a
// supplier — this service does not re-check it. DROP_SHIP is the one exception it does enforce
// itself (A29-P5-05): a DROP_SHIP line is refused while drop ship is disabled, since the source and
// the customer address it stamps on the PO change where the goods go.
public interface IAutoPurchaseOrderService
{
    Task<AutoPurchaseOrderResult> CreateFromSODeficitAsync(
        Guid saleOrderLineUuid, Guid supplierId, decimal qty, decimal price, int userId);
}

/// <param name="Status">What the PO actually ended up as — DRAFT, PENDING_APPROVAL or APPROVED. Can
/// be DRAFT even when the org is configured for REQUIRE_WORKFLOW, if submitting it to the workflow
/// engine failed (see the service's own remarks): the PO still exists and the team can submit it.</param>
/// <param name="Source">BACK_TO_BACK, or DROP_SHIP when the line's fulfillment mode is DROP_SHIP.</param>
/// <param name="AlreadyExisted">True when the line already had a live PO from an earlier call, and
/// that one is returned instead of creating a second.</param>
public sealed record AutoPurchaseOrderResult(
    Guid   PoUuid,
    string PoNumber,
    string Status,
    string Source,
    bool   AlreadyExisted);
