using SMS.Modules.Suppliers.Models;

namespace SMS.Modules.Suppliers.Services;

public interface IScorecardService
{
    Task<List<ScorecardDimensionWeightModel>> GetWeightsAsync();
    Task UpdateWeightsAsync(List<UpdateDimensionWeightItem> weights, int modifiedBy);
}
