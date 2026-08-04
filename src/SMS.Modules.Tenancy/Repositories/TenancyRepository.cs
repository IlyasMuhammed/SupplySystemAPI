using System.Data;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Models;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Tenancy.Repositories;

internal sealed class TenancyRepository : ITenancyRepository
{
    private readonly TenancyDbContext _db;
    private readonly IOrgUserProvisioningService _orgUserProvisioning;
    private readonly IWorkflowSeedingService _workflowSeeding;

    public TenancyRepository(
        TenancyDbContext db, IOrgUserProvisioningService orgUserProvisioning, IWorkflowSeedingService workflowSeeding)
    {
        _db = db;
        _orgUserProvisioning = orgUserProvisioning;
        _workflowSeeding = workflowSeeding;
    }

    // ── Organizations ────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<OrganizationListItemModel>> GetOrganizationsAsync(OrganizationFilter filter)
    {
        var query = _db.Organizations.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            query = query.Where(o => o.OrgCode.ToLower().Contains(term) || o.OrgName.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(filter.Plan))
            query = query.Where(o => o.Plan == filter.Plan);
        if (filter.IsActive.HasValue)
            query = query.Where(o => o.IsActive == filter.IsActive.Value);

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderBy(o => o.OrgName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(o => new OrganizationListItemModel
            {
                Id           = o.Id,
                OrgCode      = o.OrgCode,
                OrgName      = o.OrgName,
                Plan         = o.Plan,
                IsActive     = o.IsActive,
                ContactEmail = o.ContactEmail,
                CreatedDate  = o.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<OrganizationListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<OrganizationDetailModel?> GetOrganizationByIdAsync(Guid id)
    {
        return await _db.Organizations.Where(o => o.Id == id)
            .Select(o => new OrganizationDetailModel
            {
                Id            = o.Id,
                OrgCode       = o.OrgCode,
                OrgName       = o.OrgName,
                Plan          = o.Plan,
                IsActive      = o.IsActive,
                ContactEmail  = o.ContactEmail,
                ContactPhone  = o.ContactPhone,
                Address       = o.Address,
                Country       = o.Country,
                TimeZone      = o.TimeZone,
                CreatedBy     = o.CreatedBy,
                CreatedDate   = o.CreatedDate,
                ModifiedBy    = o.ModifiedBy,
                ModifiedDate  = o.ModifiedDate
            })
            .FirstOrDefaultAsync();
    }

    public Task<bool> OrgCodeExistsAsync(string orgCode) =>
        _db.Organizations.AnyAsync(o => o.OrgCode.ToLower() == orgCode.ToLower());

    public async Task<CreateOrganizationResult> CreateOrganizationWithAdminAsync(CreateOrganizationRequest req, int createdBy)
    {
        var result = new CreateOrganizationResult();

        // Cross-DbContext atomic write (org row + admin user row, across the tenant/auth schemas)
        // — open the connection/transaction here on TenancyDbContext, then join
        // IOrgUserProvisioningService's Auth-side write to the same transaction. Mirrors the
        // established pattern in SMS.Modules.Material/Services/MivService.cs. Wrapped in the EF
        // execution strategy so EnableRetryOnFailure doesn't conflict with the user-managed
        // transaction.
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await _db.Database.OpenConnectionAsync();
            var conn = _db.Database.GetDbConnection();
            await using var sqlTx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            _db.Database.UseTransaction(sqlTx);

            var now = DateTime.UtcNow;
            var org = new Organization
            {
                Id           = Guid.NewGuid(),
                OrgCode      = req.OrgCode,
                OrgName      = req.OrgName,
                Plan         = req.Plan,
                IsActive     = true,
                ContactEmail = req.ContactEmail,
                ContactPhone = req.ContactPhone,
                Address      = req.Address,
                Country      = req.Country,
                TimeZone     = req.TimeZone,
                CreatedBy    = createdBy,
                CreatedDate  = now
            };
            _db.Organizations.Add(org);

            // Clone the org's plan template into its own OrganizationFeatures rows — the same
            // routine TenancyDataSeeder runs for every org on every startup, so a newly-created
            // org and the seeder never disagree about what "default for this plan" means.
            var features  = await _db.FeatureDefinitions.ToListAsync();
            var templates = await _db.PlanFeatureTemplates
                .Where(t => t.Plan.ToLower() == req.Plan.ToLower())
                .ToDictionaryAsync(t => t.FeatureDefinitionId, t => t.IsEnabledByDefault);

            foreach (var feature in features)
            {
                templates.TryGetValue(feature.Id, out var isEnabledByDefault);
                _db.OrganizationFeatures.Add(new OrganizationFeature
                {
                    Id                  = Guid.NewGuid(),
                    OrganizationId      = org.Id,
                    FeatureDefinitionId = feature.Id,
                    IsEnabled           = isEnabledByDefault
                });
            }

            await _db.SaveChangesAsync();

            // If this throws (e.g. duplicate admin email), sqlTx is disposed without a commit —
            // the ADO.NET transaction rolls back automatically, taking the org insert with it.
            var adminUserId = await _orgUserProvisioning.CreateOrgAdminUserAsync(
                new CreateOrgAdminUserRequest(
                    req.AdminFirstName, req.AdminLastName, req.AdminEmail,
                    org.Id, req.OrgName, (int)EnumRole.OrgAdmin),
                sqlTx);

            // MT-006 — clone the standard PR/PO/GRN/MIR workflow templates into the new org so it
            // has a working approval chain immediately, atomically with the org + admin user rows.
            await _workflowSeeding.SeedDefaultWorkflowsAsync(org.Id, sqlTx);

            await sqlTx.CommitAsync();

            result.OrganizationId = org.Id;
            result.AdminUserId    = adminUserId;
        });

        return result;
    }

    public async Task<bool> UpdateOrganizationAsync(Guid id, UpdateOrganizationRequest req, int modifiedBy)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == id);
        if (org is null) return false;

        org.OrgName      = req.OrgName;
        org.ContactEmail = req.ContactEmail;
        org.ContactPhone = req.ContactPhone;
        org.Address      = req.Address;
        org.Country      = req.Country;
        org.TimeZone     = req.TimeZone;
        org.ModifiedBy   = modifiedBy;
        org.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> PatchStatusAsync(Guid id, bool isActive, int modifiedBy)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == id);
        if (org is null) return false;

        org.IsActive     = isActive;
        org.ModifiedBy   = modifiedBy;
        org.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> PatchPlanAsync(Guid id, string plan, int modifiedBy)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == id);
        if (org is null) return false;

        // Deliberately does NOT touch OrganizationFeatures — changing an org's billing plan
        // shouldn't silently strip a super-admin's manual feature overrides. Use
        // ApplyPlanTemplateAsync explicitly if a reset is actually wanted.
        org.Plan         = plan;
        org.ModifiedBy   = modifiedBy;
        org.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ApplyPlanTemplateAsync(Guid id, int modifiedBy)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == id);
        if (org is null) return false;

        var templates = await _db.PlanFeatureTemplates
            .Where(t => t.Plan.ToLower() == org.Plan.ToLower())
            .ToDictionaryAsync(t => t.FeatureDefinitionId, t => t.IsEnabledByDefault);

        var orgFeatures = await _db.OrganizationFeatures.Where(f => f.OrganizationId == id).ToListAsync();
        var now = DateTime.UtcNow;

        foreach (var of in orgFeatures)
        {
            if (!templates.TryGetValue(of.FeatureDefinitionId, out var isEnabledByDefault)) continue;
            of.IsEnabled    = isEnabledByDefault;
            of.ModifiedBy   = modifiedBy;
            of.ModifiedDate = now;
        }

        await _db.SaveChangesAsync();
        return true;
    }

    public Task<bool> OrganizationExistsAsync(Guid orgId) => _db.Organizations.AnyAsync(o => o.Id == orgId);

    // ── Feature catalog / toggles ────────────────────────────────────────────

    public async Task<List<FeatureDefinitionModel>> GetFeatureCatalogAsync()
    {
        return await _db.FeatureDefinitions
            .OrderBy(f => f.Category).ThenBy(f => f.DisplayOrder)
            .Select(f => new FeatureDefinitionModel
            {
                Id           = f.Id,
                FeatureCode  = f.FeatureCode,
                FeatureName  = f.FeatureName,
                Category     = f.Category,
                Description  = f.Description,
                IsCore       = f.IsCore,
                DisplayOrder = f.DisplayOrder
            })
            .ToListAsync();
    }

    public async Task<List<PlanFeatureTemplateModel>> GetPlanTemplatesAsync()
    {
        var features = await _db.FeatureDefinitions.ToDictionaryAsync(f => f.Id, f => f.FeatureCode);
        var templates = await _db.PlanFeatureTemplates.ToListAsync();

        return templates
            .GroupBy(t => t.Plan)
            .Select(g => new PlanFeatureTemplateModel
            {
                Plan = g.Key,
                Features = g.Select(t => new PlanFeatureTemplateItem
                {
                    FeatureCode        = features.TryGetValue(t.FeatureDefinitionId, out var code) ? code : "",
                    IsEnabledByDefault = t.IsEnabledByDefault
                }).ToList()
            })
            .ToList();
    }

    public async Task<List<OrganizationFeatureModel>> GetOrganizationFeaturesAsync(Guid orgId)
    {
        var features = await _db.FeatureDefinitions.OrderBy(f => f.Category).ThenBy(f => f.DisplayOrder).ToListAsync();
        var orgFeatures = await _db.OrganizationFeatures
            .Where(f => f.OrganizationId == orgId)
            .ToDictionaryAsync(f => f.FeatureDefinitionId);

        return features.Select(f =>
        {
            orgFeatures.TryGetValue(f.Id, out var of);
            return new OrganizationFeatureModel
            {
                FeatureCode  = f.FeatureCode,
                FeatureName  = f.FeatureName,
                Category     = f.Category,
                Description  = f.Description,
                IsCore       = f.IsCore,
                DisplayOrder = f.DisplayOrder,
                IsEnabled    = of?.IsEnabled ?? false,
                ModifiedDate = of?.ModifiedDate
            };
        }).ToList();
    }

    // Writes only the changed rows (the service layer has already computed the diff) in one
    // SaveChangesAsync — single DbContext, already atomic, no cross-context transaction needed
    // here unlike CreateOrganizationWithAdminAsync.
    public async Task SaveFeatureChangesAsync(Guid orgId, Dictionary<string, bool> changes, int modifiedBy)
    {
        if (changes.Count == 0) return;

        var featureIds = await _db.FeatureDefinitions
            .Where(f => changes.Keys.Contains(f.FeatureCode))
            .ToDictionaryAsync(f => f.FeatureCode, f => f.Id, StringComparer.OrdinalIgnoreCase);

        var existingRows = await _db.OrganizationFeatures
            .Where(f => f.OrganizationId == orgId && featureIds.Values.Contains(f.FeatureDefinitionId))
            .ToDictionaryAsync(f => f.FeatureDefinitionId);

        var now = DateTime.UtcNow;
        foreach (var (featureCode, isEnabled) in changes)
        {
            if (!featureIds.TryGetValue(featureCode, out var featureId)) continue;

            if (existingRows.TryGetValue(featureId, out var row))
            {
                row.IsEnabled    = isEnabled;
                row.ModifiedBy   = modifiedBy;
                row.ModifiedDate = now;
            }
            else
            {
                _db.OrganizationFeatures.Add(new OrganizationFeature
                {
                    Id                  = Guid.NewGuid(),
                    OrganizationId      = orgId,
                    FeatureDefinitionId = featureId,
                    IsEnabled           = isEnabled,
                    ModifiedBy          = modifiedBy,
                    ModifiedDate        = now
                });
            }
        }

        await _db.SaveChangesAsync();
    }
}
