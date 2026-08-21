using SMS.Modules.Demand.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Services;

public interface IQuotationService
{
    Task<Guid> CreateAsync(CreateQuotationRequest req, int createdBy);
    Task UpdateAsync(Guid uuid, PatchQuotationRequest req, int modifiedBy);
    Task<PaginatedResponse<QuotationListItemModel>> GetListAsync(QuotationListFilter filter);
    Task<QuotationDetailModel?> GetByIdAsync(Guid uuid);
    Task SendAsync(Guid uuid, SendQuotationRequest req, int modifiedBy);
    Task<Guid> RecordResponseAsync(Guid uuid, RecordVendorResponseRequest req, int createdBy);
    Task<List<VendorResponseModel>> GetComparisonAsync(Guid uuid);
    Task OpenBidsAsync(Guid uuid, int openedBy);
    Task AwardAsync(Guid uuid, AwardQuotationRequest req, int awardedBy);
    Task CancelAsync(Guid uuid, string reason, int modifiedBy);
    Task<SendWithLinkResult> SendWithLinkAsync(Guid uuid, SendWithLinkRequest req, int createdBy, string? requestOrigin = null);
    Task<List<RfqAccessLinkModel>> GetAccessLinksAsync(Guid quotationUuid);
    Task ResendLinkAsync(Guid quotationUuid, int linkId);
}
