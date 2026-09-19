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
}
