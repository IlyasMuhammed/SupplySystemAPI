using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A37 D-18 — what is still in flight when an admin is about to switch Manufacturing / Services off: one line per
/// non-terminal status with a count ("Production orders – In progress", 3). The organization is explicit and the tenant
/// filter is bypassed (Tenancy may ask outside the organization's own request).
/// </summary>
internal abstract class StatusImpactProvider : IModuleImpactProvider
{
    protected readonly MaterialDbContext Db;
    protected StatusImpactProvider(MaterialDbContext db) => Db = db;

    public abstract string ModuleCode { get; }
    protected abstract string Noun { get; }
    protected abstract IReadOnlyList<string> OpenStatuses { get; }

    public async Task<IReadOnlyList<ModuleImpactItem>> GetInProgressAsync(Guid organizationId, CancellationToken ct = default)
    {
        var open   = OpenStatuses;
        var counts = await CountByStatusAsync(organizationId, open, ct);
        return open.Where(counts.ContainsKey)
            .Select(s => new ModuleImpactItem($"{Noun} – {Label(s)}", counts[s])).ToList();
    }

    protected abstract Task<Dictionary<string, int>> CountByStatusAsync(Guid organizationId, IReadOnlyList<string> open, CancellationToken ct);

    /// <summary>MATERIAL_PENDING → "Material pending".</summary>
    internal static string Label(string status)
    {
        var words = status.Replace('_', ' ').ToLowerInvariant();
        return char.ToUpperInvariant(words[0]) + words[1..];
    }
}

internal sealed class ProductionOrderImpactProvider : StatusImpactProvider
{
    public ProductionOrderImpactProvider(MaterialDbContext db) : base(db) { }

    public override string ModuleCode => ModuleCodes.Manufacturing;
    protected override string Noun => "Production orders";
    protected override IReadOnlyList<string> OpenStatuses =>
        ProductionOrderStatus.All.Where(s => !ProductionOrderStatus.IsTerminal(s)).ToList();

    protected override Task<Dictionary<string, int>> CountByStatusAsync(Guid organizationId, IReadOnlyList<string> open, CancellationToken ct) =>
        Db.ProductionOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && open.Contains(o.Status))
            .GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}

internal sealed class ServiceOrderImpactProvider : StatusImpactProvider
{
    public ServiceOrderImpactProvider(MaterialDbContext db) : base(db) { }

    public override string ModuleCode => ModuleCodes.Services;
    protected override string Noun => "Service orders";
    protected override IReadOnlyList<string> OpenStatuses =>
        ServiceOrderStatus.All.Where(s => !ServiceOrderStatus.IsTerminal(s)).ToList();

    protected override Task<Dictionary<string, int>> CountByStatusAsync(Guid organizationId, IReadOnlyList<string> open, CancellationToken ct) =>
        Db.ServiceOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.OrganizationId == organizationId && open.Contains(o.Status))
            .GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
}
