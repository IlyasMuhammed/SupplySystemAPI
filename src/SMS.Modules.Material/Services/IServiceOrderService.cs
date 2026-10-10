using SMS.Modules.Material.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Services;

/// <summary>A36 — service orders (API-CONTRACT §3). Every action returns the full detail.</summary>
public interface IServiceOrderService
{
    Task<Guid> CreateAsync(CreateServiceOrderRequest req, int userId, CancellationToken ct = default);
    Task<PaginatedResponse<ServiceOrderListItemModel>> GetListAsync(ServiceOrderListFilter filter, CancellationToken ct = default);
    Task<ServiceOrderDetailModel?> GetAsync(Guid uuid, CancellationToken ct = default);
    Task<ServiceOrderDetailModel> UpdateAsync(Guid uuid, UpdateServiceOrderRequest req, int userId, CancellationToken ct = default);
    Task<ServiceOrderDetailModel> PlanAsync(Guid uuid, int userId, CancellationToken ct = default);
    Task<ServiceOrderDetailModel> StartAsync(Guid uuid, int userId, CancellationToken ct = default);
    Task<ServiceOrderDetailModel> CompleteAsync(Guid uuid, CompleteServiceOrderRequest req, int userId, CancellationToken ct = default);
    Task<ServiceOrderDetailModel> CancelAsync(Guid uuid, string? reason, int userId, CancellationToken ct = default);
    Task<ServiceOrderDetailModel> CloseAsync(Guid uuid, int userId, CancellationToken ct = default);
    Task<IReadOnlyList<ServiceMaterialModel>> GetMaterialsAsync(Guid uuid, CancellationToken ct = default);
    Task<ServiceOrderDetailModel> AddMaterialAsync(Guid uuid, AddAdhocMaterialRequest req, int userId, CancellationToken ct = default);
    Task<ServiceOrderDetailModel> RemoveMaterialAsync(Guid uuid, Guid smrUuid, int userId, CancellationToken ct = default);
    Task<ServiceOrderDetailModel> AllocateAsync(Guid uuid, int userId, CancellationToken ct = default);
    Task<ServiceLedgerModel> GetLedgerAsync(Guid uuid, CancellationToken ct = default);
    Task<ServiceDashboardModel> GetDashboardAsync(int userId, CancellationToken ct = default);
}
