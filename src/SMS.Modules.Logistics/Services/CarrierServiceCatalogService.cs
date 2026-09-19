using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// The catalogue of products a carrier sells. Public because the controller is; named for the
/// catalogue rather than the entity so it does not read as a service-of-services.
/// </summary>
public interface ICarrierServiceCatalogService
{
    Task<Guid> CreateAsync(CreateCarrierServiceRequest req, int userId);
    Task<IReadOnlyList<CarrierServiceModel>> GetForCarrierAsync(Guid carrierUuid);
    Task<CarrierServiceModel?> GetByUuidAsync(Guid uuid);
    Task<bool> PatchAsync(Guid uuid, PatchCarrierServiceRequest req, int userId);
    Task<bool> DeleteAsync(Guid uuid, int userId);
}

internal sealed class CarrierServiceCatalogService : ICarrierServiceCatalogService
{
    private readonly ICarrierServiceRepository _repo;
    public CarrierServiceCatalogService(ICarrierServiceRepository repo) => _repo = repo;

    public Task<Guid> CreateAsync(CreateCarrierServiceRequest req, int userId) =>
        _repo.CreateAsync(req, userId);

    public Task<IReadOnlyList<CarrierServiceModel>> GetForCarrierAsync(Guid carrierUuid) =>
        _repo.GetForCarrierAsync(carrierUuid);

    public Task<CarrierServiceModel?> GetByUuidAsync(Guid uuid) => _repo.GetByUuidAsync(uuid);

    public Task<bool> PatchAsync(Guid uuid, PatchCarrierServiceRequest req, int userId) =>
        _repo.PatchAsync(uuid, req, userId);

    public Task<bool> DeleteAsync(Guid uuid, int userId) => _repo.DeleteAsync(uuid, userId);
}
