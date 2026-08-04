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
        await SyncFeatureDefinitionsAsync();
        await SyncPlanFeatureTemplatesAsync();
        await SeedDemoOrganizationAsync();
        await BackfillOrganizationFeaturesForAllOrgsAsync();
        await SeedSuperAdminAsync();
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
                    DisplayOrder = entry.DisplayOrder
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
            Id          = Guid.NewGuid(),
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

        foreach (var org in orgs)
        {
            var templateLookup = templates
                .Where(t => string.Equals(t.Plan, org.Plan, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(t => t.FeatureDefinitionId, t => t.IsEnabledByDefault);

            foreach (var feature in features)
            {
                if (existingKeys.Contains((org.Id, feature.Id))) continue;

                templateLookup.TryGetValue(feature.Id, out var isEnabledByDefault);

                _db.OrganizationFeatures.Add(new OrganizationFeature
                {
                    Id                  = Guid.NewGuid(),
                    OrganizationId      = org.Id,
                    FeatureDefinitionId = feature.Id,
                    IsEnabled           = isEnabledByDefault
                });
            }
        }

        await _db.SaveChangesAsync();
    }
}
