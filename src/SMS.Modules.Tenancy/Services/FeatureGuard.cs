using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

/// <summary>A51 P4 — see <see cref="IFeatureGuard"/>. Reads the same ModuleState the tenant snapshot, IModuleGate and
/// GET api/tenant/current read, so the filter (403), the menu and this guard cannot disagree.</summary>
internal sealed class FeatureGuard : IFeatureGuard
{
    private readonly TenancyDbContext _db;
    private readonly TimeProvider _clock;

    public FeatureGuard(TenancyDbContext db, TimeProvider? clock = null)
    {
        _db = db;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<bool> IsAccessibleAsync(Guid organizationId, string featureCode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(featureCode)) return false;
        var states = await GetStatesAsync(organizationId, ct);
        return states.Any(s => s.Usable && s.Code.Equals(featureCode, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<FeatureState>> GetStatesAsync(Guid organizationId, CancellationToken ct = default)
    {
        var states = await ModuleState.LoadAsync(_db, organizationId, track: false, ct);
        var access = ModuleState.Evaluate(states, _clock.GetUtcNow().UtcDateTime);
        var plan = await _db.Organizations.AsNoTracking().Where(o => o.Id == organizationId).Select(o => o.Plan).FirstOrDefaultAsync(ct);
        var planCodes = plan is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : await PlanTemplateSource.LoadCodesAsync(_db, plan, ct);

        return states.Select(s => new FeatureState(
            s.Code,
            s.Definition.Status,
            s.Definition.IsVisible || s.IsGrandfathered,
            access[s.Code].Level == FeatureAccessLevel.Full,
            planCodes.Contains(s.Code) || s.IsLicensed)).ToList();
    }
}
