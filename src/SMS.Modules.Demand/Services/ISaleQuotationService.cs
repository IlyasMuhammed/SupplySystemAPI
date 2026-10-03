using SMS.Modules.Demand.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A32 C2 — Sale Quotation (seller-side; unrelated to the buyer-side RFQ IQuotationService). Owner: QUO
/// (implementation, controller api/sale-quotations, QuotationExpiryJob, tests). Same return/exception
/// conventions as <see cref="ISaleInquiryService"/>.
/// </summary>
public interface ISaleQuotationService
{
    /// <summary>An independent DRAFT quotation numbered SQ-YYYY-NNNNN. The partner must be a customer (BR-C2-01).</summary>
    Task<Guid> CreateAsync(CreateSaleQuotationRequest req, int userId);

    /// <summary>
    /// POST api/sale-inquiries/{uuid}/create-quotation (INQ's controller calls this). The inquiry must be
    /// REVIEW_COMPLETE; lines are pre-populated from its evaluation (API-CONTRACT §2.4) with SourceInquiryLineId
    /// set; <see cref="ISaleInquiryService.MarkQuotedAsync"/> is called before the one SaveChangesAsync so the
    /// quotation and the inquiry's QUOTED status commit together. Throws NotFoundException when the inquiry is
    /// not in the caller's organization. Returns the quotation's uuid.
    /// </summary>
    Task<Guid> CreateFromInquiryAsync(Guid inquiryUuid, CreateSaleQuotationFromInquiryRequest req, int userId);

    /// <summary>Header only, DRAFT only.</summary>
    Task<bool> UpdateAsync(Guid uuid, UpdateSaleQuotationRequest req, int userId);
    Task<SaleQuotationModel?> GetByIdAsync(Guid uuid);
    Task<PaginatedResponse<SaleQuotationListItemModel>> GetListAsync(SaleQuotationListFilter filter);

    /// <summary>DRAFT only. Returns the new line's uuid; null when the quotation is not found.</summary>
    Task<Guid?> AddLineAsync(Guid uuid, SaleQuotationLineRequest req, int userId);
    Task<bool> UpdateLineAsync(Guid uuid, Guid lineUuid, SaleQuotationLineRequest req, int userId);
    /// <summary>DRAFT only. Deleting a REJECTED line that still has ALTERNATIVE lines is refused.</summary>
    Task<bool> DeleteLineAsync(Guid uuid, Guid lineUuid, int userId);

    /// <summary>DRAFT → SENT (BR-C2-04), stamps SentAt/SentByUserId.</summary>
    Task<bool> SendAsync(Guid uuid, int userId);
    /// <summary>SENT only, not on a REJECTED line; COUNTER needs a price (BR-C2-09).</summary>
    Task<bool> RecordCustomerResponseAsync(Guid uuid, Guid lineUuid, RecordCustomerResponseRequest req, int userId);
    /// <summary>SENT → ACCEPTED (BR-C2-07).</summary>
    Task<bool> AcceptAsync(Guid uuid, int userId);
    /// <summary>SENT → REJECTED (BR-C2-08).</summary>
    Task<bool> RejectAsync(Guid uuid, string? reason, int userId);

    /// <summary>
    /// ACCEPTED → CONVERTED (BR-C2-11): sets the tracked quotation CONVERTED, then calls
    /// <see cref="ISaleOrderService.CreateFromQuotationAsync"/> with the ACCEPTED lines only — that call's one
    /// SaveChangesAsync commits the sale order and the quotation's status together. Returns the sale order's uuid;
    /// null when the quotation is not found.
    /// </summary>
    Task<Guid?> ConvertToOrderAsync(Guid uuid, ConvertSaleQuotationToOrderRequest req, int userId);

    /// <summary>
    /// Optional (§4.4 "create a new quotation, optionally copying lines"): a new DRAFT with this one's header and
    /// lines, keeping SourceInquiryId. Returns the new uuid; null when not found.
    /// </summary>
    Task<Guid?> CopyAsync(Guid uuid, int userId);
}
