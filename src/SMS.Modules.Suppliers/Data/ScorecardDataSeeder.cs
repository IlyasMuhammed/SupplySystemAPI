using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Data;

// Every organization has its own five dimension weights (unique on OrganizationId + DimensionCode). The seeder
// fills in whichever of the five an organization lacks and never touches an existing row, so an admin's edited
// weights survive every restart. It reads with IgnoreQueryFilters and stamps the organization explicitly: at
// startup there is no caller, and the tenant filter's bypass would otherwise see one org's rows as everyone's.
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

    /// <summary>Seeds the current tenant's organization.</summary>
    public Task SeedAsync() => SeedForOrganizationAsync(_db.TenantContext.OrganizationId);

    public async Task SeedForAllAsync(IEnumerable<Guid> organizationIds, CancellationToken ct = default)
    {
        foreach (var organizationId in organizationIds)
            await SeedForOrganizationAsync(organizationId, ct);
    }

    public async Task SeedForOrganizationAsync(Guid organizationId, CancellationToken ct = default)
    {
        var existing = await _db.ScorecardDimensionWeights.IgnoreQueryFilters()
            .Where(d => d.OrganizationId == organizationId)
            .Select(d => d.DimensionCode)
            .ToListAsync(ct);

        var added = false;
        foreach (var (code, name, weight, maxPoints) in DefaultDimensions)
        {
            if (existing.Contains(code)) continue;

            _db.ScorecardDimensionWeights.Add(new ScorecardDimensionWeight
            {
                OrganizationId   = organizationId,
                DimensionCode    = code,
                DimensionName    = name,
                WeightPercentage = weight,
                MaxPoints        = maxPoints,
                IsActive         = true
            });
            added = true;
        }

        if (added) await _db.SaveChangesAsync(ct);
    }
}

/// <summary>A new organization gets its five default scorecard weights as soon as Tenancy creates it.</summary>
internal sealed class ScorecardWeightsProvisioningHandler : IOrganizationProvisionedHandler
{
    private readonly ScorecardDataSeeder _seeder;
    public ScorecardWeightsProvisioningHandler(ScorecardDataSeeder seeder) => _seeder = seeder;

    public Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default) =>
        _seeder.SeedForOrganizationAsync(organizationId, ct);
}
