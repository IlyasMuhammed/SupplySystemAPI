using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;

namespace SMS.Modules.Suppliers.Services;

internal sealed class ScorecardService : IScorecardService
{
    private readonly IScorecardRepository _repo;
    public ScorecardService(IScorecardRepository repo) => _repo = repo;

    public Task<List<ScorecardDimensionWeightModel>> GetWeightsAsync() => _repo.GetWeightsAsync();

    public Task UpdateWeightsAsync(List<UpdateDimensionWeightItem> weights, int modifiedBy) =>
        _repo.UpdateWeightsAsync(weights, modifiedBy);
}
