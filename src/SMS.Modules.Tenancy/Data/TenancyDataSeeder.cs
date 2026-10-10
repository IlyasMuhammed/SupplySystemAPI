using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Data;

// Runs on every startup (see ITenancyModule.UseTenancyModule). Steps run in FK-dependency order
// and are all idempotent — safe to re-run on every deploy, matching LookupsDataSeeder's
// check-then-insert convention, except FeatureDefinitions/PlanFeatureTemplates are upserted
// (not insert-only) since the in-code catalog in TenancyFeatureCatalog is the source of truth.
internal sealed class TenancyDataSeeder
{
    private readonly TenancyDbContext _db;
    private readonly IUserQueryService _userQuery;
    private const int SystemUserId = 0;
    private const string DemoOrgCode = "SCM-DEMO";

    public TenancyDataSeeder(TenancyDbContext db, IUserQueryService userQuery)
    {
        _db = db;
        _userQuery = userQuery;
    }

    public async Task SeedAsync()
    {
        // A37 D-3 / M037.3 — a cycle or a broken parent stops the start here, before anything is written.
        TenancyFeatureCatalog.Validate();
        await SyncFeatureDefinitionsAsync();
        await SyncFeatureDependenciesAsync();
        await SyncPlanFeatureTemplatesAsync();
        await SeedDemoOrganizationAsync();
        await BackfillOrganizationFeaturesForAllOrgsAsync();
        await SeedSuperAdminAsync();
    }

    // A37 D-1 — the table mirrors the code list exactly (extra rows removed, missing rows added).
    private async Task SyncFeatureDependenciesAsync()
    {
        var wanted = TenancyFeatureCatalog.Dependencies
            .Select(d => (d.Dependent.ToUpperInvariant(), d.RequiredBy.ToUpperInvariant())).ToHashSet();
        var existing = await _db.FeatureDependencies.ToListAsync();

        _db.FeatureDependencies.RemoveRange(existing.Where(e => !wanted.Contains((e.FeatureCode.ToUpperInvariant(), e.DependsOnCode.ToUpperInvariant()))));
        var have = existing.Select(e => (e.FeatureCode.ToUpperInvariant(), e.DependsOnCode.ToUpperInvariant())).ToHashSet();
        foreach (var (code, dependsOn) in wanted.Where(w => !have.Contains(w)))
            _db.FeatureDependencies.Add(new FeatureDependency { FeatureCode = code, DependsOnCode = dependsOn });

        await _db.SaveChangesAsync();
    }

    // MT-007, FSD Section 7.1 Step 2 — one-time migration: designate the first existing System
    // Admin user as Super Admin. Idempotent (checked via AnyAsync, not tied to a specific user id)
    // so it only ever runs once across the table's lifetime — later Super Admins are granted
    // through a dedicated admin action, not by this seeder re-running.
    private async Task SeedSuperAdminAsync()
    {
        if (await _db.SuperAdminUsers.AnyAsync()) return;

        var firstSystemAdminUserId = await _userQuery.GetFirstSystemAdminUserIdAsync();
        if (firstSystemAdminUserId is null) return; // no System Admin exists yet — nothing to migrate

        _db.SuperAdminUsers.Add(new SuperAdminUser
        {
            UserId    = firstSystemAdminUserId.Value,
            CreatedAt = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
    }

    private async Task SyncFeatureDefinitionsAsync()
    {
        var existing = await _db.FeatureDefinitions.ToDictionaryAsync(f => f.FeatureCode, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in TenancyFeatureCatalog.Catalog)
        {
            if (existing.TryGetValue(entry.Code, out var row))
            {
                row.FeatureName  = entry.Name;
                row.Category     = entry.Category;
                row.Description  = entry.Description;
                row.IsCore       = entry.IsCore;
                row.DisplayOrder = entry.DisplayOrder;
                row.ParentModuleCode = entry.Parent;
                row.IsAlwaysOn   = entry.IsAlwaysOn;
                row.IsAvailable  = entry.IsAvailable;
                row.Icon         = entry.Icon;
            }
            else
            {
                _db.FeatureDefinitions.Add(new FeatureDefinition
                {
                    Id           = Guid.NewGuid(),
                    FeatureCode  = entry.Code,
                    FeatureName  = entry.Name,
                    Category     = entry.Category,
                    Description  = entry.Description,
                    IsCore       = entry.IsCore,
                    DisplayOrder = entry.DisplayOrder,
                    ParentModuleCode = entry.Parent,
                    IsAlwaysOn   = entry.IsAlwaysOn,
                    IsAvailable  = entry.IsAvailable,
                    Icon         = entry.Icon
                });
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task SyncPlanFeatureTemplatesAsync()
    {
        var features = await _db.FeatureDefinitions.ToListAsync();
        var existing = await _db.PlanFeatureTemplates.ToListAsync();
        var existingByKey = existing.ToDictionary(t => (t.Plan.ToUpperInvariant(), t.FeatureDefinitionId));

        foreach (var plan in TenancyFeatureCatalog.AllPlans)
        {
            var defaultEnabledCodes = TenancyFeatureCatalog.GetDefaultEnabledCodes(plan);

            foreach (var feature in features)
            {
                var isEnabledByDefault = defaultEnabledCodes.Contains(feature.FeatureCode);
                var key = (plan.ToUpperInvariant(), feature.Id);

                if (existingByKey.TryGetValue(key, out var row))
                {
                    row.IsEnabledByDefault = isEnabledByDefault;
                }
                else
                {
                    _db.PlanFeatureTemplates.Add(new PlanFeatureTemplate
                    {
                        Id                  = Guid.NewGuid(),
                        Plan                = plan,
                        FeatureDefinitionId = feature.Id,
                        IsEnabledByDefault  = isEnabledByDefault
                    });
                }
            }
        }

        await _db.SaveChangesAsync();
    }

    private async Task SeedDemoOrganizationAsync()
    {
        var exists = await _db.Organizations.AnyAsync(o => o.OrgCode == DemoOrgCode);
        if (exists) return;

        _db.Organizations.Add(new Organization
        {
            // A real Organization row must exist at this well-known id, not a random one — it's
            // the hardcoded fallback TenantContext.OrganizationId resolves to for any caller with
            // no HttpContext (startup-time seeding, Hangfire jobs with no captured origin org), so
            // on a genuinely fresh database a random id here left every such fallback pointing at
            // a nonexistent org, which the login-time org-active check then (correctly) treats as
            // deactivated. Confirmed only by seeding a truly empty database from scratch — SMS_Dev
            // was seeded once, long enough ago, that its own SCM-DEMO row already happened to carry
            // this id from whatever version of this method ran at the time.
            Id          = TenantDefaults.ScmDemoOrganizationId,
            OrgCode     = DemoOrgCode,
            OrgName     = "Supply Chain Demo",
            Plan        = TenancyFeatureCatalog.PlanEnterprise,
            IsActive    = true,
            CreatedBy   = SystemUserId,
            CreatedDate = DateTime.UtcNow
        });

        await _db.SaveChangesAsync();
    }

    // Ensures every active organization has an OrganizationFeatures row for every current
    // FeatureDefinition, cloned from that org's plan template. Runs for ALL orgs on every
    // startup (not just at creation) so that when a future ticket adds a new FeatureDefinition,
    // every existing org — including SCM-DEMO — automatically gets a row for it on the next
    // deploy, keeping "SCM-DEMO has every feature enabled" true forever rather than just at
    // the moment this ticket first ran.
    private async Task BackfillOrganizationFeaturesForAllOrgsAsync()
    {
        var orgs     = await _db.Organizations.Where(o => o.IsActive).ToListAsync();
        var features = await _db.FeatureDefinitions.ToListAsync();
        var templates = await _db.PlanFeatureTemplates.ToListAsync();
        var existingKeys = (await _db.OrganizationFeatures
                .Select(f => new { f.OrganizationId, f.FeatureDefinitionId })
                .ToListAsync())
            .Select(x => (x.OrganizationId, x.FeatureDefinitionId))
            .ToHashSet();

        var allRows = await _db.OrganizationFeatures.AsNoTracking().ToListAsync();
        var codeById = features.ToDictionary(f => f.Id, f => f.FeatureCode);
        var now = DateTime.UtcNow;

        foreach (var org in orgs)
        {
            var templateLookup = templates
                .Where(t => string.Equals(t.Plan, org.Plan, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(t => t.FeatureDefinitionId, t => t.IsEnabledByDefault);
            // A37 — what the org has today, by code (licensed AND on), for sub-features added after it was created.
            var orgOn = allRows.Where(r => r.OrganizationId == org.Id && codeById.ContainsKey(r.FeatureDefinitionId))
                .ToDictionary(r => codeById[r.FeatureDefinitionId], r => (r.IsLicensed, On: r.IsLicensed && r.IsEnabled),
                    StringComparer.OrdinalIgnoreCase);

            // Modules first, so a new sub-feature of a new module sees its parent's fresh row.
            foreach (var feature in features.OrderBy(f => f.ParentModuleCode is null ? 0 : 1))
            {
                if (existingKeys.Contains((org.Id, feature.Id))) continue;

                templateLookup.TryGetValue(feature.Id, out var isEnabledByDefault);
                var isLicensed = isEnabledByDefault;
                var systemManaged = false;

                // A37 §1.3 — a sub-feature an existing org gains follows what the org has now, not its plan's default: its
                // module's licence and switch (a Basic org given Logistics by hand gets pick lists); BOM management
                // follows Manufacturing / Service Orders (MOD-08, system-managed). Unavailable entries stay off.
                if (feature.ParentModuleCode is { } parent && orgOn.TryGetValue(parent, out var parentState))
                {
                    var isBom = feature.FeatureCode.Equals(ModuleCodes.BomManagement, StringComparison.OrdinalIgnoreCase);
                    isLicensed = feature.IsAvailable && (parentState.IsLicensed || feature.IsCore);
                    isEnabledByDefault = feature.IsAvailable && (isBom
                        ? TenancyFeatureCatalog.BomManagementDrivers.Any(d => orgOn.TryGetValue(d, out var s) && s.On)
                        : parentState.On || feature.IsCore);
                    isLicensed |= isEnabledByDefault;
                    systemManaged = isBom && isEnabledByDefault;
                }

                _db.OrganizationFeatures.Add(new OrganizationFeature
                {
                    Id                  = Guid.NewGuid(),
                    OrganizationId      = org.Id,
                    FeatureDefinitionId = feature.Id,
                    IsEnabled           = isEnabledByDefault,
                    IsLicensed          = isLicensed,
                    EnabledAt           = isEnabledByDefault ? now : null,
                    IsSystemManaged     = systemManaged
                });
                orgOn[feature.FeatureCode] = (isLicensed, isLicensed && isEnabledByDefault);
            }
        }

        await _db.SaveChangesAsync();
    }
}
