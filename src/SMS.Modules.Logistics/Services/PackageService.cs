using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;

namespace SMS.Modules.Logistics.Services;

// Public because the controller is; the implementation stays internal.
public interface IPackageService
{
    Task<Guid> PackAsync(Guid deliveryUuid, PackRequest req, int userId);
    Task<PackageModel?> GetByUuidAsync(Guid uuid);
    Task<DeliveryPackingModel?> GetForDeliveryAsync(Guid deliveryUuid);
    Task<bool> PatchAsync(Guid uuid, PatchPackageRequest req, int userId);
    Task<bool> VoidAsync(Guid uuid, DeliveryReasonRequest req, int userId);
}

internal sealed class PackageService : IPackageService
{
    private readonly IPackageRepository _repo;
    public PackageService(IPackageRepository repo) => _repo = repo;

    public Task<Guid> PackAsync(Guid deliveryUuid, PackRequest req, int userId) =>
        _repo.PackAsync(deliveryUuid, req, userId);

    public Task<PackageModel?> GetByUuidAsync(Guid uuid) => _repo.GetByUuidAsync(uuid);

    public Task<DeliveryPackingModel?> GetForDeliveryAsync(Guid deliveryUuid) =>
        _repo.GetForDeliveryAsync(deliveryUuid);

    public Task<bool> PatchAsync(Guid uuid, PatchPackageRequest req, int userId) =>
        _repo.PatchAsync(uuid, req, userId);

    public Task<bool> VoidAsync(Guid uuid, DeliveryReasonRequest req, int userId) =>
        _repo.VoidAsync(uuid, req, userId);
}
