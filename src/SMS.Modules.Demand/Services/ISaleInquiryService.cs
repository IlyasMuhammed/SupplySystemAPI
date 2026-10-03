using SMS.Modules.Demand.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A32 C1 — Sale Inquiry. Owner: INQ (implementation, controller api/sale-inquiries, tests).
/// Conventions (same as ISaleOrderService): a method returning false/null means "not found in the caller's
/// organization" (controller → 404); a broken business rule throws BadRequestException (400); a state conflict
/// throws ConflictException (409). Every lookup by uuid filters explicitly on the caller's own organization —
/// another organization's inquiry is "not found", super admin included.
/// </summary>
public interface ISaleInquiryService
{
    /// <summary>Creates a RECEIVED inquiry numbered INQ-YYYY-NNNNN. The partner must be a customer (BR-C1-01).</summary>
    Task<Guid> CreateAsync(CreateSaleInquiryRequest req, int userId);
    /// <summary>Header only. Refused once QUOTED or DECLINED (BR-C1-06).</summary>
    Task<bool> UpdateAsync(Guid uuid, UpdateSaleInquiryRequest req, int userId);
    Task<SaleInquiryModel?> GetByIdAsync(Guid uuid);
    Task<PaginatedResponse<SaleInquiryListItemModel>> GetListAsync(SaleInquiryListFilter filter);

    /// <summary>Returns the new line's uuid; null when the inquiry is not found.</summary>
    Task<Guid?> AddLineAsync(Guid uuid, SaleInquiryLineRequest req, int userId);
    /// <summary>Request fields + evaluation (§3.5 rules). Stamps ReviewedBy/ReviewedAt when the status leaves PENDING.</summary>
    Task<bool> UpdateLineAsync(Guid uuid, Guid lineUuid, UpdateSaleInquiryLineRequest req, int userId);
    Task<bool> DeleteLineAsync(Guid uuid, Guid lineUuid, int userId);

    /// <summary>
    /// User transitions (§3.4): RECEIVED→UNDER_REVIEW (≥1 line), UNDER_REVIEW→REVIEW_COMPLETE (no PENDING line,
    /// BR-C1-03), UNDER_REVIEW|REVIEW_COMPLETE→DECLINED (reason required). Null when not found.
    /// </summary>
    Task<SaleInquiryModel?> ChangeStatusAsync(Guid uuid, ChangeSaleInquiryStatusRequest req, int userId);

    /// <summary>
    /// REVIEW_COMPLETE → QUOTED (BR-C1-07), called by <see cref="ISaleQuotationService.CreateFromInquiryAsync"/>.
    /// Loads the inquiry tracked from the scoped DemandDbContext (caller's organization), refuses any other status
    /// (ConflictException; NotFoundException when absent), sets Status/ModifiedBy/ModifiedDate — and does NOT save:
    /// the caller's single SaveChangesAsync commits the new quotation and this transition together. SaleInquiry.Status
    /// is a concurrency token, so two concurrent create-quotation calls cannot both succeed (the loser gets a 409).
    /// </summary>
    Task MarkQuotedAsync(Guid inquiryUuid, int userId);
}
