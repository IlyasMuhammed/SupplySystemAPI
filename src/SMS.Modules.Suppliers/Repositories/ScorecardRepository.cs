using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Suppliers.Repositories;

internal sealed class ScorecardRepository : IScorecardRepository
{
    private readonly SuppliersDbContext _db;
    public ScorecardRepository(SuppliersDbContext db) => _db = db;

    public async Task<List<ScorecardDimensionWeightModel>> GetWeightsAsync() =>
        await _db.ScorecardDimensionWeights
            .OrderBy(w => w.Id)
            .Select(w => new ScorecardDimensionWeightModel
            {
                DimensionCode    = w.DimensionCode,
                DimensionName    = w.DimensionName,
                WeightPercentage = w.WeightPercentage,
                MaxPoints        = w.MaxPoints,
                IsActive         = w.IsActive,
                ModifiedDate     = w.ModifiedDate
            })
            .ToListAsync();

    public async Task UpdateWeightsAsync(List<UpdateDimensionWeightItem> weights, int modifiedBy)
    {
        var sum = weights.Sum(w => w.WeightPercentage);
        if (sum != 100m)
            throw new BadRequestException($"Dimension weights must sum to 100. Current sum: {sum}.");

        var existing = await _db.ScorecardDimensionWeights.ToListAsync();
        var now = DateTime.UtcNow;

        foreach (var item in weights)
        {
            var dim = existing.FirstOrDefault(d => d.DimensionCode == item.DimensionCode)
                ?? throw new BadRequestException($"Unknown scorecard dimension code '{item.DimensionCode}'.");

            dim.WeightPercentage = item.WeightPercentage;
            dim.MaxPoints        = item.MaxPoints;
            dim.ModifiedDate     = now;
        }

        await _db.SaveChangesAsync();
    }
}
