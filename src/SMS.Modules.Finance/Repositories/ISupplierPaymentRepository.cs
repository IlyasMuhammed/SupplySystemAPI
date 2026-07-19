using SMS.Modules.Finance.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Repositories;

public interface ISupplierPaymentRepository
{
    Task<Guid> CreateAsync(CreateSupplierPaymentRequest req, int createdBy);
    Task<PaginatedResponse<SupplierPaymentListItemModel>> GetListAsync(SupplierPaymentFilter filter);
    Task<SupplierPaymentDetailModel?> GetByUuidAsync(Guid uuid);
    Task<bool> ApproveAsync(Guid uuid, int approvedBy);
    Task<bool> CancelAsync(Guid uuid, int cancelledBy);
    Task<bool> PostAsync(Guid uuid, int postedBy);
    Task<bool> BounceAsync(Guid uuid, int bouncedBy);
    Task<List<OutstandingInvoiceModel>> GetOutstandingInvoicesAsync(Guid supplierId);
    Task<SupplierAgingModel> GetSupplierAgingAsync(Guid supplierId);
    Task<CrossSupplierAgingReport> GetCrossSupplierAgingAsync();

    // SFM-007 reports
    Task<PaginatedResponse<PaymentRegisterItem>> GetPaymentRegisterAsync(PaymentRegisterFilter filter);
    Task<PaginatedResponse<OutstandingPayablesSupplierGroup>> GetOutstandingPayablesAsync(OutstandingPayablesFilter filter);
    Task<PaymentMethodBreakdownReport> GetPaymentMethodBreakdownAsync(PaymentMethodBreakdownFilter filter);
}
