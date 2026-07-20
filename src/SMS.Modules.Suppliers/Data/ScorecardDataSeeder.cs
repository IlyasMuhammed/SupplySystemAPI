using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Domain;

namespace SMS.Modules.Suppliers.Data;

internal sealed class ScorecardDataSeeder
{
    private readonly SuppliersDbContext _db;
    public ScorecardDataSeeder(SuppliersDbContext db) => _db = db;

    private static readonly (string Code, string Name, decimal Weight, decimal MaxPoints)[] DefaultDimensions =
    [
        ("DELIVERY",      "Delivery",      25m, 25m),
        ("QUANTITY",      "Quantity",      25m, 25m),
        ("QUALITY",       "Quality",       25m, 25m),
        ("PRICE",         "Price",         15m, 15m),
        ("DOCUMENTATION", "Documentation", 10m, 10m),
    ];

    public async Task SeedAsync()
    {
        foreach (var (code, name, weight, maxPoints) in DefaultDimensions)
        {
            if (!await _db.ScorecardDimensionWeights.AnyAsync(d => d.DimensionCode == code))
            {
                _db.ScorecardDimensionWeights.Add(new ScorecardDimensionWeight
                {
                    DimensionCode    = code,
                    DimensionName    = name,
                    WeightPercentage = weight,
                    MaxPoints        = maxPoints,
                    IsActive         = true
                });
            }
        }

        await _db.SaveChangesAsync();
    }
}
