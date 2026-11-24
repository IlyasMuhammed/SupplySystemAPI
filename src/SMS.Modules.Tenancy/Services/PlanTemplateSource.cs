using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;

namespace SMS.Modules.Tenancy.Services;

/// <summary>
/// A51 P2 — the one place that answers "what does plan X include". tenant.PlanFeatures is the source of truth; the legacy
/// PlanFeatureTemplates table is the fallback only while PlanFeatures holds nothing for the plan, and the in-code template
/// (TenancyFeatureCatalog.GetDefaultEnabledCodes) only while both tables are empty. No caching: a plan edit is visible to the next read.
/// </summary>
internal static class PlanTemplateSource
{
    /// <summary>FeatureDefinition id -> included, for every feature definition (a feature with no row is not included).</summary>
    public static async Task<Dictionary<Guid, bool>> LoadByDefinitionIdAsync(TenancyDbContext db, string plan, CancellationToken ct = default)
    {
        var features = await db.FeatureDefinitions.AsNoTracking().Select(f => new { f.Id, f.FeatureCode }).ToListAsync(ct);
        var included = await LoadCodesAsync(db, plan, ct);
        return features.ToDictionary(f => f.Id, f => included.Contains(f.FeatureCode));
    }

    /// <summary>The feature codes the plan includes (case-insensitive set).</summary>
    public static async Task<HashSet<string>> LoadCodesAsync(TenancyDbContext db, string plan, CancellationToken ct = default)
    {
        var key = plan.ToLower();
        var rows = await db.PlanFeatures.AsNoTracking().Where(p => p.PlanCode.ToLower() == key).ToListAsync(ct);
        if (rows.Count > 0)
            return rows.Where(r => r.IsIncluded).Select(r => r.FeatureCode).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var templates = await db.PlanFeatureTemplates.AsNoTracking().Where(t => t.Plan.ToLower() == key).ToListAsync(ct);
        if (templates.Count > 0)
        {
            var byId = await db.FeatureDefinitions.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.FeatureCode, ct);
            return templates.Where(t => t.IsEnabledByDefault && byId.ContainsKey(t.FeatureDefinitionId))
                .Select(t => byId[t.FeatureDefinitionId]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return TenancyFeatureCatalog.GetDefaultEnabledCodes(plan);
    }
}
