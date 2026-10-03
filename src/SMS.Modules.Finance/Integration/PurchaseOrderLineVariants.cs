using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;

namespace SMS.Modules.Finance.Integration;

/// <summary>Which variant each purchase order line ordered — what a supplier invoice line bills, as an item.</summary>
internal interface IPurchaseOrderLineVariants
{
    /// <summary>
    /// PO line uuid → the variant it ordered (null when it named none), for the lines that exist. One query
    /// however many lines are asked about; none at all when none are.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Guid?>> GetAsync(IReadOnlyCollection<Guid> poLineUuids, CancellationToken ct = default);
}

/// <summary>Reads Demand's purchase order lines directly — Finance already references Demand.</summary>
internal sealed class DemandPurchaseOrderLineVariants : IPurchaseOrderLineVariants
{
    private readonly DemandDbContext _demand;

    public DemandPurchaseOrderLineVariants(DemandDbContext demand) => _demand = demand;

    public async Task<IReadOnlyDictionary<Guid, Guid?>> GetAsync(
        IReadOnlyCollection<Guid> poLineUuids, CancellationToken ct = default)
    {
        if (poLineUuids.Count == 0) return new Dictionary<Guid, Guid?>();

        var ids = poLineUuids.Distinct().ToList();
        var rows = await _demand.PurchaseOrderLines.AsNoTracking()
            .Where(l => ids.Contains(l.UUID))
            .Select(l => new { l.UUID, l.VariantUuid })
            .ToListAsync(ct);

        // Not ToDictionary on the query: a duplicated uuid would throw, and one answer is as good as another.
        var result = new Dictionary<Guid, Guid?>();
        foreach (var row in rows) result.TryAdd(row.UUID, row.VariantUuid);
        return result;
    }
}
