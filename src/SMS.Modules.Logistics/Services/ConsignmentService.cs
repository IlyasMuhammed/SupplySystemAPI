using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;

namespace SMS.Modules.Logistics.Services;

public interface IConsignmentService
{
    Task<Guid> CreateAsync(CreateConsignmentRequest req, int createdBy);
    Task<ConsignmentDetailModel?> GetByUuidAsync(Guid uuid);
    Task<bool> AttachDeliveryAsync(Guid uuid, Guid deliveryUuid, int userId);
    Task<bool> BookManuallyAsync(Guid uuid, ManualBookingRequest req, int userId);
}

internal sealed class ConsignmentService : IConsignmentService
{
    private readonly IConsignmentRepository _repo;
    public ConsignmentService(IConsignmentRepository repo) => _repo = repo;

    public Task<Guid>                   CreateAsync(CreateConsignmentRequest req, int createdBy)        => _repo.CreateAsync(req, createdBy);
    public Task<ConsignmentDetailModel?> GetByUuidAsync(Guid uuid)                                      => _repo.GetByUuidAsync(uuid);
    public Task<bool>                   AttachDeliveryAsync(Guid uuid, Guid deliveryUuid, int userId)   => _repo.AttachDeliveryAsync(uuid, deliveryUuid, userId);
    public Task<bool>                   BookManuallyAsync(Guid uuid, ManualBookingRequest req, int u)   => _repo.BookManuallyAsync(uuid, req, u);
}
