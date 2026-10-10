using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

/// <summary>One catalog entry joined with one organization's row (Row null = the org has no row for it yet).</summary>
internal sealed record OrgFeatureState(FeatureDefinition Definition, OrganizationFeature? Row)
{
    public string Code => Definition.FeatureCode;
    public string Name => Definition.FeatureName;
    public bool IsModule => Definition.ParentModuleCode is null && Definition.Category == TenancyFeatureCatalog.CategoryModule;
    public bool IsLicensed => Row?.IsLicensed == true;
    /// <summary>Licensed and switched on (the row itself — not its parent).</summary>
    public bool IsOn => Definition.IsAvailable && Row is { IsLicensed: true, IsEnabled: true };
}

/// <summary>
/// A37 D-6 — the one place that turns an organization's rows into access levels: the tenant snapshot, IModuleGate,
/// GET api/tenant/current and the module cards all read this, so they cannot disagree.
/// </summary>
internal static class ModuleState
{
    internal static async Task<List<OrgFeatureState>> LoadAsync(TenancyDbContext db, Guid orgId, bool track, CancellationToken ct = default)
    {
        var definitions = await db.FeatureDefinitions.AsNoTracking().OrderBy(f => f.DisplayOrder).ToListAsync(ct);
        var rowsQuery = db.OrganizationFeatures.Where(f => f.OrganizationId == orgId);
        var rows = await (track ? rowsQuery : rowsQuery.AsNoTracking()).ToDictionaryAsync(f => f.FeatureDefinitionId, ct);
        return definitions.Select(d => new OrgFeatureState(d, rows.GetValueOrDefault(d.Id))).ToList();
    }

    /// <summary>The row's own level, ignoring its parent.</summary>
    internal static FeatureAccessLevel SelfLevel(OrgFeatureState s, DateTime utcNow)
    {
        if (s.IsOn) return FeatureAccessLevel.Full;
        if (s.Row is null || !s.Definition.IsAvailable) return FeatureAccessLevel.None;
        if (!s.Row.IsEnabled && s.Row.GracePeriodEndsAt is { } ends && ends > utcNow && s.Row.IsLicensed) return FeatureAccessLevel.Grace;
        return s.Row.DisabledAt is not null ? FeatureAccessLevel.ReadOnly : FeatureAccessLevel.None;
    }

    /// <summary>Per code: the effective access (the lower of itself and its parent module).</summary>
    internal static Dictionary<string, FeatureAccess> Evaluate(IReadOnlyList<OrgFeatureState> states, DateTime utcNow)
    {
        var byCode = states.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, FeatureAccess>(StringComparer.OrdinalIgnoreCase);

        FeatureAccess Own(OrgFeatureState s)
        {
            var level = SelfLevel(s, utcNow);
            return new FeatureAccess(s.Code, s.Code, s.Name, level, level == FeatureAccessLevel.Grace ? s.Row!.GracePeriodEndsAt : null);
        }

        foreach (var s in states)
        {
            var own = Own(s);
            if (s.Definition.ParentModuleCode is { } parentCode)
            {
                // A parent the org has no row for blocks its features like a never-licensed module.
                var parent = byCode.TryGetValue(parentCode, out var p)
                    ? Own(p)
                    : new FeatureAccess(parentCode, parentCode, parentCode, FeatureAccessLevel.None, null);
                if (parent.Level < own.Level)
                    own = parent with { Code = s.Code };
                else if (parent.Level == FeatureAccessLevel.Grace && own.Level == FeatureAccessLevel.Grace && parent.GraceEndsAt < own.GraceEndsAt)
                    own = own with { GraceEndsAt = parent.GraceEndsAt };
            }
            result[s.Code] = own;
        }
        return result;
    }

    internal static TenantSnapshot ToSnapshot(bool isActive, IReadOnlyList<OrgFeatureState> states, DateTime utcNow)
    {
        var access = Evaluate(states, utcNow);
        var enabled = access.Values.Where(a => a.Level == FeatureAccessLevel.Full).Select(a => a.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new TenantSnapshot(isActive, enabled) { Access = access };
    }

    // ── ModuleCard status (API-CONTRACT §1.1) ────────────────────────────────

    internal const string StatusAlwaysOn    = "ALWAYS_ON";
    internal const string StatusActive      = "ACTIVE";
    internal const string StatusGrace       = "GRACE";
    internal const string StatusDisabled    = "DISABLED";
    internal const string StatusNotLicensed = "NOT_LICENSED";
    internal const string StatusComingSoon  = "COMING_SOON";

    internal static string Status(OrgFeatureState s, DateTime utcNow)
    {
        if (!s.Definition.IsAvailable) return StatusComingSoon;
        if (!s.IsLicensed) return StatusNotLicensed;
        if (s.Row!.IsEnabled) return s.Definition.IsAlwaysOn || s.Definition.IsCore ? StatusAlwaysOn : StatusActive;
        return s.Row.GracePeriodEndsAt > utcNow ? StatusGrace : StatusDisabled;
    }

    internal static int SortRank(string status) => status switch
    {
        StatusAlwaysOn => 0, StatusActive => 1, StatusGrace => 2, StatusDisabled => 3, StatusNotLicensed => 4, _ => 5
    };
}
