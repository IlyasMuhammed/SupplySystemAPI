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
}
