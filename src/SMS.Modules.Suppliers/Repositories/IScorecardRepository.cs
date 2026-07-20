using SMS.Modules.Suppliers.Models;

namespace SMS.Modules.Suppliers.Repositories;

public interface IScorecardRepository
{
    /// <summary>All 5 scorecard dimensions with their current weights.</summary>
    Task<List<ScorecardDimensionWeightModel>> GetWeightsAsync();

    /// <summary>Validates the submitted weights sum to 100 and every dimension code is known, then
    /// updates all dimensions atomically in a single SaveChanges. Throws BadRequestException otherwise.</summary>
    Task UpdateWeightsAsync(List<UpdateDimensionWeightItem> weights, int modifiedBy);
}
