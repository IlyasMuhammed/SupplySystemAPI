using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Services;

// Public because the controller is; the implementation stays internal. Same shape as IDeliveryService.
public interface IPickListService
{
    Task<Guid> GenerateAsync(Guid deliveryUuid, GeneratePickListRequest? req, int userId);
    Task<PickListModel?> GetByUuidAsync(Guid uuid);
    Task<PickListModel?> GetForDeliveryAsync(Guid deliveryUuid);
    Task<PaginatedResponse<PickListListItemModel>> GetListAsync(PickListFilter filter);
    Task<bool> AssignAsync(Guid uuid, int assignToUserId, int userId);
    Task<bool> CancelAsync(Guid uuid, DeliveryReasonRequest req, int userId);

    /// <summary>Records what the picker found. Closes the list once every line is answered.</summary>
    Task<ConfirmPickResultModel?> ConfirmAsync(Guid uuid, ConfirmPickRequest req, int userId);
}

internal sealed class PickListService : IPickListService
{
    private readonly IPickListRepository _repo;
    public PickListService(IPickListRepository repo) => _repo = repo;

    public Task<Guid> GenerateAsync(Guid deliveryUuid, GeneratePickListRequest? req, int userId) =>
        _repo.GenerateAsync(deliveryUuid, req, userId);

    public Task<PickListModel?> GetByUuidAsync(Guid uuid)              => _repo.GetByUuidAsync(uuid);
    public Task<PickListModel?> GetForDeliveryAsync(Guid deliveryUuid) => _repo.GetForDeliveryAsync(deliveryUuid);

    public Task<PaginatedResponse<PickListListItemModel>> GetListAsync(PickListFilter filter) =>
        _repo.GetListAsync(filter);

    public Task<bool> AssignAsync(Guid uuid, int assignToUserId, int userId) =>
        _repo.AssignAsync(uuid, assignToUserId, userId);

    public Task<bool> CancelAsync(Guid uuid, DeliveryReasonRequest req, int userId) =>
        _repo.CancelAsync(uuid, req, userId);

    public Task<ConfirmPickResultModel?> ConfirmAsync(Guid uuid, ConfirmPickRequest req, int userId) =>
        _repo.ConfirmAsync(uuid, req, userId);
}
