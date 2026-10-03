using SMS.Modules.Finance.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

public interface IInvoiceService
{
    Task<Guid>                                  CreateAsync(CreateInvoiceRequest req, int createdBy);
    Task<PaginatedResponse<InvoiceListItemModel>> GetListAsync(InvoiceFilter filter);
    Task<InvoiceDetailModel?>                   GetByUuidAsync(Guid uuid);
    Task<bool>                                  PatchAsync(Guid uuid, PatchInvoiceRequest req, int modifiedBy);
    Task<bool>                                  ApproveAsync(Guid uuid, string? notes, int approvedBy);
    Task<bool>                                  RejectAsync(Guid uuid, string reason, int rejectedBy);
    /// <summary>S-7: reverses an approved, unpaid invoice (see <c>IInvoiceRepository.ReverseAsync</c>). False when not found.</summary>
    Task<bool>                                  ReverseAsync(Guid uuid, string reason, int reversedBy);
    Task<bool>                                  UploadAttachmentAsync(Guid uuid, string url, int modifiedBy);
}

public interface IPaymentService
{
    Task<Guid>                                   CreateAsync(CreatePaymentRequest req, int createdBy);
    Task<PaginatedResponse<PaymentListItemModel>> GetListAsync(PaymentFilter filter);
    Task<PaymentDetailModel?>                    GetByUuidAsync(Guid uuid);
    Task<bool>                                   PatchAsync(Guid uuid, PatchPaymentRequest req, int modifiedBy);
}

public interface ISupplierPaymentService
{
    Task<Guid>                                           CreateAsync(CreateSupplierPaymentRequest req, int createdBy);
    Task<PaginatedResponse<SupplierPaymentListItemModel>> GetListAsync(SupplierPaymentFilter filter);
    Task<SupplierPaymentDetailModel?>                    GetByUuidAsync(Guid uuid);
    Task<bool>                                           ApproveAsync(Guid uuid, int approvedBy);
    Task<bool>                                           CancelAsync(Guid uuid, int cancelledBy);
    Task<bool>                                           PostAsync(Guid uuid, int postedBy);
    Task<bool>                                           BounceAsync(Guid uuid, int bouncedBy);
    Task<List<OutstandingInvoiceModel>>                  GetOutstandingInvoicesAsync(Guid supplierId);
    Task<SupplierAgingModel>                              GetSupplierAgingAsync(Guid supplierId);
    Task<CrossSupplierAgingReport>                        GetCrossSupplierAgingAsync();

    // SFM-007 reports
    Task<PaginatedResponse<PaymentRegisterItem>>          GetPaymentRegisterAsync(PaymentRegisterFilter filter);
    Task<PaginatedResponse<OutstandingPayablesSupplierGroup>> GetOutstandingPayablesAsync(OutstandingPayablesFilter filter);
    Task<PaymentMethodBreakdownReport>                    GetPaymentMethodBreakdownAsync(PaymentMethodBreakdownFilter filter);
}
