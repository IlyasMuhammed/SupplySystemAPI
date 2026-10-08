using SMS.Modules.Demand.Models;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Services;

// A29-P3-06 §4.1/§4.2/§4.5.
public interface ISaleOrderService
{
    Task<Guid> CreateAsync(CreateSaleOrderRequest req, int createdBy);
    Task<bool> UpdateAsync(Guid uuid, UpdateSaleOrderRequest req, int modifiedBy);
    Task<SaleOrderModel?> GetByIdAsync(Guid uuid);
    Task<PaginatedResponse<SaleOrderModel>> GetListAsync(SaleOrderListFilter filter);

    // A29-P3-07 §4.5. Confirm is a bare DRAFT -> CONFIRMED transition only — §4.3's availability
    // check, reservation and back-to-back PO creation are a separate, much larger task this one
    // does not attempt (see SaleOrderService's own remarks on ConfirmAsync).
    Task<bool> ConfirmAsync(Guid uuid, int userId);
    Task<bool> CancelAsync(Guid uuid, int userId, string? reason);
    Task<TimelineDetail?> GetTimelineAsync(Guid uuid);
    Task<IReadOnlyList<SaleOrderLineAvailabilityModel>?> GetAvailabilityAsync(Guid uuid);

    /// <summary>What a new order starts as, from the organization's sale order settings (§8.1).</summary>
    Task<SaleOrderDefaultsModel> GetDefaultsAsync();

    // ── A32 C3 (owner FND) ─────────────────────────────────────────────────────

    /// <summary>
    /// PD-04 — a DRAFT order from an accepted quotation, through the same numbering, delivery-mode, retail
    /// availability, min/max quantity, tax-code and totals rules as <see cref="CreateAsync"/>; prices are taken as
    /// quoted. SourceType = FROM_QUOTATION, SourceQuotationId set, SourceInquiryId chained from the quotation
    /// (BR-C3-03); partner and currency come from the quotation. Commits with ONE SaveChangesAsync, so a quotation
    /// status change the caller made on the same scoped DemandDbContext commits with it. A quotation that already
    /// has an order is a 409 (unique index). Throws NotFoundException when the quotation is not in the caller's org.
    /// </summary>
    Task<Guid> CreateFromQuotationAsync(CreateSaleOrderFromQuotationCommand cmd, int createdBy);

    /// <summary>PD-05 — set/replace the customer PO reference, date and linked CUSTOMER_PO attachment. False = not found.</summary>
    Task<bool> UpdateCustomerPoAsync(Guid uuid, UpdateSaleOrderCustomerPoRequest req, int modifiedBy);

    /// <summary>BR-C3-05 — the organization's other orders with this customer PO reference (case-insensitive, trimmed).</summary>
    Task<IReadOnlyList<CustomerPoDuplicateModel>> FindCustomerPoDuplicatesAsync(string reference, Guid? excludeUuid);

    // ── A33 C3/C4 (owner DEM) — fulfillment routes ──────────────────────────────

    /// <summary>
    /// <see cref="ConfirmAsync"/> with its outcome (API-CONTRACT.md §5): under the order's lock, the route gate (400 with
    /// every blocker), reservation and the D-16 route snapshot commit together; then, outside the lock, the deliveries are
    /// created (D-1, best effort). Null = not the caller's order.
    /// </summary>
    Task<SaleOrderConfirmResultModel?> ConfirmWithResultAsync(Guid uuid, int userId);

    /// <summary><see cref="CancelAsync"/> with the deliveries it cancelled and the issued ones it left (D-15). Null = not found.</summary>
    Task<SaleOrderCancelResultModel?> CancelWithResultAsync(Guid uuid, int userId, string? reason);

    /// <summary>BR-C3-05 — how a saved order's lines would be split into deliveries. Persists nothing. Null = not found.</summary>
    Task<SaleOrderDeliveryPreviewModel?> GetDeliveryPreviewAsync(Guid uuid);

    /// <summary>BR-C3-05 — the same for the unsaved form. A <c>SaleOrderUuid</c> that is not the caller's is a NotFoundException.</summary>
    Task<SaleOrderDeliveryPreviewModel> PreviewDeliveriesAsync(SaleOrderDeliveryPreviewRequest req);

    /// <summary>
    /// D-12 / REV-01 — a delivery creator call for the order returned without throwing (e.g. Logistics' recovery
    /// endpoint), so the sweep need not retry it. Own organization; a no-op when nothing is pending. False = not found.
    /// </summary>
    Task<bool> MarkDeliveriesCreatedAsync(Guid uuid);

    /// <summary>
    /// A33 — set (or with null clear) one DRAFT line's route override and nothing else: no re-pricing, no line rebuild,
    /// unlike the full PUT. BR-C3-01 validation; non-DRAFT is a 400 (BR-C3-04). Returns the line with its effective route,
    /// source and blocker recomputed; null = the order or line is not the caller's.
    /// </summary>
    Task<SaleOrderLineModel?> UpdateLineRouteAsync(Guid uuid, Guid lineUuid, Guid? fulfillmentRouteUuid, int userId);

    // ── A34 (owner DEM) — lead time, make-to-order production (API-CONTRACT §5, §6) ──

    /// <summary>
    /// D-16 — calculate one DRAFT line's lead time (ILeadTimeCalculator, the line's effective route, the header's expected
    /// date as the requested date) and store its Calculated* fields; the manual date is untouched. Null = not found.
    /// </summary>
    Task<SaleLineLeadTimeModel<SaleOrderLineModel>?> CalculateLineLeadTimeAsync(Guid uuid, Guid lineUuid, int userId);

    /// <summary>
    /// D-16 — set (null clears) one line's manual delivery date on a DRAFT / CONFIRMED / PARTIALLY_FULFILLED order, under
    /// the order lock, with no re-pricing. An existing production order is not rescheduled (reported). Null = not found.
    /// </summary>
    Task<SaleOrderLineDeliveryDateResultModel?> UpdateLineDeliveryDateAsync(Guid uuid, Guid lineUuid, DateTime? manualDeliveryDate, int userId);

    /// <summary>
    /// D-17 recovery — create (idempotently) and plan the make-to-order production orders of a CONFIRMED /
    /// PARTIALLY_FULFILLED order. Null = not found.
    /// </summary>
    Task<SaleOrderProductionCreationResultModel?> CreateProductionOrdersAsync(Guid uuid, int userId);
}
