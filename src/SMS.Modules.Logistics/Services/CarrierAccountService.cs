using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;

namespace SMS.Modules.Logistics.Services;

// Public because the controller is; the implementation stays internal.
public interface ICarrierAccountService
{
    Task<Guid> CreateAsync(CreateCarrierAccountRequest req, int userId);
    Task<IReadOnlyList<CarrierAccountModel>> GetForCarrierAsync(Guid carrierUuid);
    Task<CarrierAccountModel?> GetByUuidAsync(Guid uuid);
    Task<bool> PatchAsync(Guid uuid, PatchCarrierAccountRequest req, int userId);
    Task<bool> DeleteAsync(Guid uuid, int userId);
}

internal sealed class CarrierAccountService : ICarrierAccountService
{
    private readonly ICarrierAccountRepository _repo;
    public CarrierAccountService(ICarrierAccountRepository repo) => _repo = repo;

    public Task<Guid> CreateAsync(CreateCarrierAccountRequest req, int userId) =>
        _repo.CreateAsync(req, userId);

    public Task<IReadOnlyList<CarrierAccountModel>> GetForCarrierAsync(Guid carrierUuid) =>
        _repo.GetForCarrierAsync(carrierUuid);

    public Task<CarrierAccountModel?> GetByUuidAsync(Guid uuid) => _repo.GetByUuidAsync(uuid);

    public Task<bool> PatchAsync(Guid uuid, PatchCarrierAccountRequest req, int userId) =>
        _repo.PatchAsync(uuid, req, userId);

    public Task<bool> DeleteAsync(Guid uuid, int userId) => _repo.DeleteAsync(uuid, userId);
}
