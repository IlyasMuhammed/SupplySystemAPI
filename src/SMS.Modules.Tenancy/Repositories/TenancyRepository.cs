using System.Data;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Services;
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
                BaseCurrency  = o.BaseCurrency,
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
            try
            {

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
                BaseCurrency = req.BaseCurrency,
                CreatedBy    = createdBy,
                CreatedDate  = now
            };
            _db.Organizations.Add(org);

            // Clone the org's plan template into its own OrganizationFeatures rows — the same
            // routine TenancyDataSeeder runs for every org on every startup, so a newly-created
            // org and the seeder never disagree about what "default for this plan" means.
            var features  = await _db.FeatureDefinitions.ToListAsync();
            // A51 P2 — the plan's content comes from tenant.PlanFeatures (PlanTemplateSource), not the legacy template rows.
            var templates = await PlanTemplateSource.LoadByDefinitionIdAsync(_db, req.Plan);

            // The modules the super admin ticked at creation (plus what they depend on); null keeps the plan's defaults.
            var chosen = req.ModuleCodes is null ? null : ExpandWithDependencies(req.ModuleCodes);
            var licensedCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedModules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var skippedComing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Modules before their sub-features, so a sub-feature sees whether its module was licensed.
            foreach (var feature in features.OrderBy(f => f.ParentModuleCode is null ? 0 : 1))
            {
                templates.TryGetValue(feature.Id, out var isEnabledByDefault);
                if (chosen is not null)
                {
                    // Core entries (master data, user management, workflow) are never switched off. Everything else is
                    // licensed only inside a chosen module, and a sub-feature still follows its plan default.
                    var inChosenModule = feature.ParentModuleCode is null
                        ? chosen.Contains(feature.FeatureCode)
                        : licensedCodes.Contains(feature.ParentModuleCode);
                    isEnabledByDefault = feature.IsAvailable
                        && (feature.IsCore || (inChosenModule && (feature.ParentModuleCode is null || isEnabledByDefault)));
                    if (isEnabledByDefault) licensedCodes.Add(feature.FeatureCode);
                }
                // A51 P7 (A51-D10) — a NEW organization is not licensed for a feature that is ComingSoon or hidden (nobody is grandfathered
                // yet), nor for the sub-features of such a module. Core features stay. The result lists what was left out.
                // A51 P8: this applies to explicitly ticked modules too (the form pre-ticks the plan's modules).
                if (isEnabledByDefault && !feature.IsCore)
                {
                    var comingSoon = string.Equals(feature.Status, FeatureStatuses.ComingSoon, StringComparison.OrdinalIgnoreCase);
                    var moduleLeftOut = feature.ParentModuleCode is not null && skippedModules.Contains(feature.ParentModuleCode);
                    if (comingSoon || !feature.IsVisible || moduleLeftOut)
                    {
                        isEnabledByDefault = false;
                        licensedCodes.Remove(feature.FeatureCode);
                        if (feature.ParentModuleCode is null) skippedModules.Add(feature.FeatureCode);
                        result.SkippedFeatures.Add(new PlanApplyFeatureItem
                        {
                            FeatureCode = feature.FeatureCode, FeatureName = feature.FeatureName,
                            Reason = comingSoon || (moduleLeftOut && skippedComing.Contains(feature.ParentModuleCode!)) ? "ComingSoon" : "NotVisible",
                            Detail = comingSoon || (moduleLeftOut && skippedComing.Contains(feature.ParentModuleCode!))
                                ? "Not licensed because it is Coming Soon." : "Not licensed because it is not visible yet."
                        });
                        if (comingSoon) skippedComing.Add(feature.FeatureCode);
                    }
                }
                // A37 D-4 — the plan is the licence; MOD-08 — BOM management from the plan is the system's.
                _db.OrganizationFeatures.Add(new OrganizationFeature
                {
                    Id                  = Guid.NewGuid(),
                    OrganizationId      = org.Id,
                    FeatureDefinitionId = feature.Id,
                    IsEnabled           = isEnabledByDefault,
                    IsLicensed          = isEnabledByDefault,
                    EnabledAt           = isEnabledByDefault ? now : null,
                    EnabledBy           = isEnabledByDefault ? createdBy : null,
                    IsSystemManaged     = isEnabledByDefault && feature.FeatureCode == ModuleCodes.BomManagement
                });
            }

            await _db.SaveChangesAsync();

            // If this throws (e.g. duplicate admin email), sqlTx is disposed without a commit —
            // the ADO.NET transaction rolls back automatically, taking the org insert with it.
            var adminUserId = await _orgUserProvisioning.CreateOrgAdminUserAsync(
                new CreateOrgAdminUserRequest(
                    req.AdminFirstName, req.AdminLastName, req.AdminEmail,
                    org.Id, req.OrgName, (int)EnumRole.OrgAdmin,
                    SendInviteEmail: false), // A51 P7: TenancyService sends it after the commit and reports whether it went out
                sqlTx);

            // MT-006 — clone the standard PR/PO/GRN/MIR workflow templates into the new org so it
            // has a working approval chain immediately, atomically with the org + admin user rows.
            await _workflowSeeding.SeedDefaultWorkflowsAsync(org.Id, sqlTx);

            await sqlTx.CommitAsync();

            result.OrganizationId = org.Id;
            result.AdminUserId    = adminUserId;
            }
            finally
            {
                // A35 — detach the transaction from the context, committed or not: otherwise TenancyDbContext keeps
                // reporting it as CurrentTransaction for the rest of the request, and every later query in the same
                // scope (the provisioning handlers' GetSettingsAsync, for one) fails with "the retrying execution
                // strategy does not support user-initiated transactions".
                _db.Database.UseTransaction(null);
            }
        });

        return result;
    }

    /// <summary>The ticked modules plus everything they depend on (e.g. Manufacturing needs Inventory), repeated until stable.</summary>
    private static HashSet<string> ExpandWithDependencies(IEnumerable<string> codes)
    {
        var set = new HashSet<string>(codes, StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (var (dependent, requiredBy) in TenancyFeatureCatalog.Dependencies)
                if (set.Contains(dependent) && set.Add(requiredBy)) changed = true;
        } while (changed);
        return set;
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
        // Only when the request says so: a null BaseCurrency means "not part of this edit", not "remove it"
        // (it used to wipe the base currency on every profile edit from a form that never sent one).
        if (req.ClearBaseCurrency)
            org.BaseCurrency = null;
        else if (req.BaseCurrency is { } baseCurrency)
            org.BaseCurrency = baseCurrency;
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

    public async Task<bool> ApplyPlanTemplateAsync(Guid id, int modifiedBy) =>
        await ApplyPlanTemplateDetailedAsync(id, modifiedBy) is not null;

    // A37 D-4 — resets the licence (and the switch with it); only rows that change are written and logged.
    // A51 P7 — the decision comes from PlanApplier (mode full): ComingSoon / hidden features are not licensed, core, grandfathered and
    // hand-licensed rows are not removed, and what was added / removed / skipped (with the reason) is returned.
    public async Task<PlanApplyOrgResult?> ApplyPlanTemplateDetailedAsync(Guid id, int modifiedBy)
    {
        var org = await _db.Organizations.FirstOrDefaultAsync(o => o.Id == id);
        if (org is null) return null;

        var included = await PlanTemplateSource.LoadCodesAsync(_db, org.Plan);
        var writer = await OrgFeatureWriter.LoadAsync(_db, id, DateTime.UtcNow);
        var decision = PlanApplier.Plan(writer.States.ToList(), included, full: true, org.Plan);
        PlanApplier.Apply(writer, decision, modifiedBy, $"Plan template {org.Plan} applied.");
        decision.Result.OrganizationId = org.Id;
        decision.Result.OrgCode = org.OrgCode;
        decision.Result.OrgName = org.OrgName;

        await _db.SaveChangesAsync();
        return decision.Result;
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
                DisplayOrder = f.DisplayOrder,
                IsAvailable  = f.IsAvailable,
                Status       = f.Status,
                IsVisible    = f.IsVisible,
                Icon         = f.Icon
            })
            .ToListAsync();
    }

    public async Task<List<PlanFeatureTemplateModel>> GetPlanTemplatesAsync()
    {
        // A51 P2 — read from tenant.PlanFeatures; the legacy template table only while PlanFeatures is empty.
        var planRows = await _db.PlanFeatures.AsNoTracking().ToListAsync();
        if (planRows.Count > 0)
        {
            return planRows
                .GroupBy(p => p.PlanCode, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PlanFeatureTemplateModel
                {
                    Plan = g.Key,
                    Features = g.Select(p => new PlanFeatureTemplateItem { FeatureCode = p.FeatureCode, IsEnabledByDefault = p.IsIncluded }).ToList()
                })
                .ToList();
        }

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
        var now = DateTime.UtcNow;
        var states = await ModuleState.LoadAsync(_db, orgId, track: false);
        var access = ModuleState.Evaluate(states, now);

        return states.OrderBy(s => s.Definition.Category).ThenBy(s => s.Definition.DisplayOrder).Select(s =>
        {
            var f = s.Definition;
            var of = s.Row;
            var status = ModuleState.Status(s, now);
            return new OrganizationFeatureModel
            {
                FeatureCode      = f.FeatureCode,
                FeatureName      = f.FeatureName,
                Category         = f.Category,
                Description      = f.Description,
                IsCore           = f.IsCore,
                DisplayOrder     = f.DisplayOrder,
                IsEnabled        = of?.IsEnabled ?? false,
                ModifiedDate     = of?.ModifiedDate,
                IsLicensed       = of?.IsLicensed ?? false,
                Status           = status,
                GraceEndsAt      = status == ModuleState.StatusGrace ? of!.GracePeriodEndsAt : null,
                ParentModuleCode = f.ParentModuleCode,
                IsAlwaysOn       = f.IsAlwaysOn,
                IsAvailable      = f.IsAvailable,
                IsUsable         = access[f.FeatureCode].Level == FeatureAccessLevel.Full,
                FeatureStatus    = f.Status,
                IsVisible        = f.IsVisible,
                IsGrandfathered  = of?.IsGrandfathered ?? false
            };
        }).ToList();
    }

    // Writes only the changed rows (the service layer has already computed the diff) in one
    // SaveChangesAsync — single DbContext, already atomic, no cross-context transaction needed
    // here unlike CreateOrganizationWithAdminAsync.
    // A37 D-4/D-8 — the super admin's toggle is the licence (licensed + switched on together), logged LICENSED /
    // UNLICENSED, and MOD-08 follows Manufacturing / Service Orders.
    public async Task GrantLicenceOnlyAsync(Guid orgId, IReadOnlyCollection<string> codes, int modifiedBy, string notes)
    {
        var writer = await OrgFeatureWriter.LoadAsync(_db, orgId, DateTime.UtcNow);
        foreach (var code in codes)
            if (writer.Find(code) is not null)
                writer.GrantLicenceOnly(code, modifiedBy, notes);
        await _db.SaveChangesAsync();
    }

    public async Task SaveFeatureChangesAsync(Guid orgId, Dictionary<string, bool> changes, int modifiedBy)
    {
        if (changes.Count == 0) return;

        var writer = await OrgFeatureWriter.LoadAsync(_db, orgId, DateTime.UtcNow);
        foreach (var (featureCode, licensed) in changes)
            if (writer.Find(featureCode) is not null)
                writer.SetLicence(featureCode, licensed, modifiedBy);

        if (changes.Keys.Any(c => TenancyFeatureCatalog.BomManagementDrivers.Contains(c, StringComparer.OrdinalIgnoreCase)))
            writer.ApplyBomRule("the licence change");

        await _db.SaveChangesAsync();
    }
}
