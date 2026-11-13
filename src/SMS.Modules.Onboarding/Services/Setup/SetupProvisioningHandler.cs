using Microsoft.EntityFrameworkCore;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Onboarding.Services.Setup;

/// <summary>
/// A50 D-6 — a new organization starts the wizard: its OrganizationSetups row is created NotStarted. Idempotent; stamps the
/// given organization (the caller is the super admin). Organizations that existed before A50 were backfilled Completed / LEGACY
/// by the migration, so the startup backfill (<see cref="EnsureMissingAsync"/>) only meets orgs whose handler call failed.
/// </summary>
internal sealed class SetupProvisioningHandler : IOrganizationProvisionedHandler
{
    private readonly OnboardingDbContext _db;

    public SetupProvisioningHandler(OnboardingDbContext db) => _db = db;

    public async Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty) return;
        if (await _db.OrganizationSetups.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == organizationId, ct)) return;
        _db.OrganizationSetups.Add(new OrganizationSetup { OrganizationId = organizationId, SetupStatus = SetupStatuses.NotStarted });
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { _db.ChangeTracker.Clear(); /* created concurrently */ }
    }

    /// <summary>Startup: every organization without a setup row gets one (NotStarted). Returns how many were added.</summary>
    public async Task<int> EnsureMissingAsync(IReadOnlyList<Guid> organizationIds, CancellationToken ct = default)
    {
        var existing = (await _db.OrganizationSetups.IgnoreQueryFilters().Select(s => s.OrganizationId).ToListAsync(ct)).ToHashSet();
        var missing = organizationIds.Where(id => id != Guid.Empty && !existing.Contains(id)).Distinct().ToList();
        foreach (var id in missing)
            _db.OrganizationSetups.Add(new OrganizationSetup { OrganizationId = id, SetupStatus = SetupStatuses.NotStarted });
        if (missing.Count > 0) await _db.SaveChangesAsync(ct);
        return missing.Count;
    }
}
