using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Tenancy.Services;

/// <summary>A51 P2 (D-3) — plans and what they include. tenant.PlanFeatures is the single source of truth; nothing is cached, so
/// an edit is visible to the very next read.</summary>
public interface IPlanService
{
    Task<List<PlanModel>> ListPlansAsync(CancellationToken ct = default);
    /// <summary>The plan with every feature and whether it is included; null when the code is unknown.</summary>
    Task<PlanWithFeaturesModel?> GetPlanWithFeaturesAsync(string code, CancellationToken ct = default);
    Task<PlanModel> UpdatePlanAsync(string code, UpdatePlanRequest request, int? userId, CancellationToken ct = default);
    /// <summary>Changes the listed features (others keep their state); all-or-nothing. 400 unknown code, 422 breaks a rule.</summary>
    Task<PlanWithFeaturesModel> UpdatePlanFeaturesAsync(string code, IReadOnlyList<PlanFeatureChange> features, int? userId, CancellationToken ct = default);
    /// <summary>The feature codes the organization's plan includes (its plan, not its per-organization overrides).</summary>
    Task<List<string>> GetTenantFeaturesAsync(Guid orgId, CancellationToken ct = default);
    Task<bool> HasFeatureAsync(Guid orgId, string featureCode, CancellationToken ct = default);
    /// <summary>Status / visibility of every feature as this organization sees it (grandfathered organizations ignore both).</summary>
    Task<List<EffectiveFeatureStatusModel>> GetEffectiveFeatureStatusAsync(Guid orgId, CancellationToken ct = default);
    /// <summary>
    /// A51 P7 — pushes the plan's current feature set to the organizations on it. addOnly (default) licenses newly included features and
    /// never removes; full = the same as each organization's "Reset to Plan Defaults". With <c>dryRun</c> nothing is written.
    /// 404 unknown plan, 400 unknown mode.
    /// </summary>
    Task<ApplyPlanToOrganizationsResult> ApplyToOrganizationsAsync(string code, ApplyPlanToOrganizationsRequest request, int? userId, CancellationToken ct = default);
}

internal sealed class PlanService : IPlanService
{
    private readonly TenancyDbContext _db;
    private readonly ILogger<PlanService> _log;

    public PlanService(TenancyDbContext db, ILogger<PlanService>? log = null)
    {
        _db = db;
        _log = log ?? NullLogger<PlanService>.Instance;
    }

    public async Task<List<PlanModel>> ListPlansAsync(CancellationToken ct = default)
    {
        var plans = await _db.Plans.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Code).ToListAsync(ct);
        var total = await _db.FeatureDefinitions.CountAsync(ct);
        var result = new List<PlanModel>();
        foreach (var p in plans)
            result.Add(ToModel(p, (await PlanTemplateSource.LoadCodesAsync(_db, p.Code, ct)).Count, total));
        return result;
    }

    public async Task<PlanWithFeaturesModel?> GetPlanWithFeaturesAsync(string code, CancellationToken ct = default)
    {
        var plan = await FindPlanAsync(code, track: false, ct);
        if (plan is null) return null;

        var included = await PlanTemplateSource.LoadCodesAsync(_db, plan.Code, ct);
        var defs = await _db.FeatureDefinitions.AsNoTracking().ToListAsync(ct);
        var model = new PlanWithFeaturesModel();
        Fill(model, plan, included.Count, defs.Count);
        model.Features = defs
            .OrderBy(f => f.Category == "MODULE" ? 0 : f.Category == "SCREEN" ? 1 : 2).ThenBy(f => f.DisplayOrder)
            .Select(f => new PlanFeatureItemModel
            {
                FeatureCode = f.FeatureCode, FeatureName = f.FeatureName, Category = f.Category, ParentModuleCode = f.ParentModuleCode,
                IsCore = f.IsCore, IsAvailable = f.IsAvailable, DisplayOrder = f.DisplayOrder,
                LicensedByHandOnly = TenancyFeatureCatalog.LicensedByHandOnly.Contains(f.FeatureCode),
                IsIncluded = included.Contains(f.FeatureCode)
            }).ToList();
        return model;
    }

    public async Task<PlanModel> UpdatePlanAsync(string code, UpdatePlanRequest request, int? userId, CancellationToken ct = default)
    {
        var plan = await FindPlanAsync(code, track: true, ct) ?? throw new NotFoundException("Plan", code);
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name)) throw new BadRequestException("A plan name is required.");
        if (name.Length > 100) throw new BadRequestException("The plan name can be at most 100 characters.");
        if (request.Description is { Length: > 500 }) throw new BadRequestException("The description can be at most 500 characters.");
        if (request.MonthlyPrice is < 0 or > 9_999_999_999_999m) throw new BadRequestException("The monthly price must be zero or more.");

        plan.Name = name;
        plan.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        plan.MonthlyPrice = request.MonthlyPrice is { } price ? Math.Round(price, 2) : null;
        plan.IsActive = request.IsActive;
        plan.SortOrder = request.SortOrder;
        plan.ModifiedAt = DateTime.UtcNow;
        plan.ModifiedBy = userId;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Plan {Plan} edited by user {UserId} (name {Name}, price {Price}, active {Active}).", plan.Code, userId, plan.Name, plan.MonthlyPrice, plan.IsActive);
        var total = await _db.FeatureDefinitions.CountAsync(ct);
        return ToModel(plan, (await PlanTemplateSource.LoadCodesAsync(_db, plan.Code, ct)).Count, total);
    }

    public async Task<PlanWithFeaturesModel> UpdatePlanFeaturesAsync(
        string code, IReadOnlyList<PlanFeatureChange> features, int? userId, CancellationToken ct = default)
    {
        var plan = await FindPlanAsync(code, track: true, ct) ?? throw new NotFoundException("Plan", code);
        var defs = await _db.FeatureDefinitions.ToDictionaryAsync(f => f.FeatureCode, StringComparer.OrdinalIgnoreCase, ct);

        // The current state, materialised as rows when the plan was still served from the legacy template / code fallback.
        var key = plan.Code.ToLower();
        var rows = await _db.PlanFeatures.Where(p => p.PlanCode.ToLower() == key).ToListAsync(ct);
        var rowByCode = rows.ToDictionary(r => r.FeatureCode, StringComparer.OrdinalIgnoreCase);
        var current = await PlanTemplateSource.LoadCodesAsync(_db, plan.Code, ct);
        foreach (var def in defs.Values)
        {
            if (rowByCode.ContainsKey(def.FeatureCode)) continue;
            var row = new PlanFeature { PlanCode = plan.Code, FeatureCode = def.FeatureCode, IsIncluded = current.Contains(def.FeatureCode) };
            _db.PlanFeatures.Add(row);
            rowByCode[def.FeatureCode] = row;
        }

        var requested = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        foreach (var change in features ?? Array.Empty<PlanFeatureChange>())
        {
            if (!defs.ContainsKey(change.FeatureCode ?? ""))
                throw new BadRequestException($"Unknown feature code '{change.FeatureCode}'.");
            requested[change.FeatureCode!] = change.IsIncluded;
        }

        bool Final(string c) => requested.TryGetValue(c, out var v) ? v : rowByCode.TryGetValue(c, out var r) && r.IsIncluded;

        foreach (var (featureCode, included) in requested)
        {
            var def = defs[featureCode];
            if (included)
            {
                if (!def.IsAvailable) errors.Add($"{def.FeatureName} is not available yet, so it cannot be part of a plan.");
                else if (TenancyFeatureCatalog.LicensedByHandOnly.Contains(def.FeatureCode))
                    errors.Add($"{def.FeatureName} is licensed by hand only and cannot be part of a plan.");
                if (def.ParentModuleCode is { } parent && !Final(parent))
                    errors.Add($"{def.FeatureName} needs its module ({defs.GetValueOrDefault(parent)?.FeatureName ?? parent}) to be included too.");
                foreach (var (dependent, requiredBy) in TenancyFeatureCatalog.Dependencies)
                    if (dependent.Equals(def.FeatureCode, StringComparison.OrdinalIgnoreCase) && !Final(requiredBy))
                        errors.Add($"{def.FeatureName} needs {defs.GetValueOrDefault(requiredBy)?.FeatureName ?? requiredBy} to be included too.");
            }
            else
            {
                if (def.IsCore) errors.Add($"{def.FeatureName} is a core feature and cannot be removed from a plan.");
                foreach (var child in defs.Values.Where(d => d.ParentModuleCode != null
                             && d.ParentModuleCode.Equals(def.FeatureCode, StringComparison.OrdinalIgnoreCase) && Final(d.FeatureCode)))
                    errors.Add($"{def.FeatureName} cannot be removed while {child.FeatureName} is still included.");
                foreach (var (dependent, requiredBy) in TenancyFeatureCatalog.Dependencies)
                    if (requiredBy.Equals(def.FeatureCode, StringComparison.OrdinalIgnoreCase) && Final(dependent))
                        errors.Add($"{def.FeatureName} cannot be removed while {defs.GetValueOrDefault(dependent)?.FeatureName ?? dependent} (which needs it) is still included.");
            }
        }
        if (errors.Count > 0) throw new UnprocessableEntityException(string.Join(" ", errors.Distinct()));

        foreach (var (featureCode, included) in requested) rowByCode[featureCode].IsIncluded = included;

        // Keep the legacy template mirror equal to PlanFeatures (the startup backfill still reads it).
        var templates = await _db.PlanFeatureTemplates.Where(t => t.Plan.ToLower() == key).ToListAsync(ct);
        var templateByDef = templates.ToDictionary(t => t.FeatureDefinitionId);
        foreach (var (featureCode, row) in rowByCode)
        {
            var def = defs[featureCode];
            if (templateByDef.TryGetValue(def.Id, out var t)) t.IsEnabledByDefault = row.IsIncluded;
            else _db.PlanFeatureTemplates.Add(new PlanFeatureTemplate
            {
                Id = Guid.NewGuid(), Plan = plan.Code, FeatureDefinitionId = def.Id, IsEnabledByDefault = row.IsIncluded
            });
        }

        plan.ModifiedAt = DateTime.UtcNow;
        plan.ModifiedBy = userId;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Plan {Plan} features changed by user {UserId}: {Changes}.", plan.Code, userId,
            string.Join(", ", requested.Select(r => $"{r.Key}={(r.Value ? "in" : "out")}")));
        return (await GetPlanWithFeaturesAsync(plan.Code, ct))!;
    }

    public async Task<List<string>> GetTenantFeaturesAsync(Guid orgId, CancellationToken ct = default)
    {
        var plan = await _db.Organizations.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Plan).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Organization", orgId);
        return (await PlanTemplateSource.LoadCodesAsync(_db, plan, ct)).OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    public async Task<bool> HasFeatureAsync(Guid orgId, string featureCode, CancellationToken ct = default)
    {
        var plan = await _db.Organizations.AsNoTracking().Where(o => o.Id == orgId).Select(o => o.Plan).FirstOrDefaultAsync(ct);
        if (plan is null || string.IsNullOrWhiteSpace(featureCode)) return false;
        return (await PlanTemplateSource.LoadCodesAsync(_db, plan, ct)).Contains(featureCode);
    }

    public async Task<List<EffectiveFeatureStatusModel>> GetEffectiveFeatureStatusAsync(Guid orgId, CancellationToken ct = default)
    {
        var defs = await _db.FeatureDefinitions.AsNoTracking().ToListAsync(ct);
        var grandfathered = (await _db.OrganizationFeatures.AsNoTracking()
                .Where(r => r.OrganizationId == orgId && r.IsGrandfathered).Select(r => r.FeatureDefinitionId).ToListAsync(ct))
            .ToHashSet();
        return defs.OrderBy(f => f.DisplayOrder).Select(f =>
        {
            var g = grandfathered.Contains(f.Id);
            return new EffectiveFeatureStatusModel
            {
                FeatureCode = f.FeatureCode, Status = f.Status, IsVisible = f.IsVisible, IsGrandfathered = g,
                EffectiveVisible = FeatureStatusRules.IsVisibleTo(f.IsVisible, g),
                EffectiveUsable = FeatureStatusRules.IsUsableBy(f.Status, g)
            };
        }).ToList();
    }

    public async Task<ApplyPlanToOrganizationsResult> ApplyToOrganizationsAsync(
        string code, ApplyPlanToOrganizationsRequest request, int? userId, CancellationToken ct = default)
    {
        var plan = await FindPlanAsync(code, track: false, ct) ?? throw new NotFoundException("Plan", code);
        var mode = PlanApplier.NormalizeMode(request?.Mode) ?? throw new BadRequestException("Mode must be addOnly or full.");
        var dryRun = request?.DryRun == true;
        var full = mode == PlanApplier.ModeFull;

        var included = await PlanTemplateSource.LoadCodesAsync(_db, plan.Code, ct);
        var key = plan.Code.ToLower();
        var orgs = await _db.Organizations.AsNoTracking().Where(o => o.Plan.ToLower() == key).OrderBy(o => o.OrgName).ToListAsync(ct);

        var result = new ApplyPlanToOrganizationsResult { PlanCode = plan.Code, Mode = mode, DryRun = dryRun, OrganizationCount = orgs.Count };
        var by = userId ?? 0;
        var anyWritten = false;
        foreach (var org in orgs)
        {
            // Each organization is its own unit: a failure of one is reported and the others still go through.
            try
            {
                var writer = await OrgFeatureWriter.LoadAsync(_db, org.Id, DateTime.UtcNow, ct);
                var decision = PlanApplier.Plan(writer.States.ToList(), included, full, plan.Code);
                decision.Result.OrganizationId = org.Id;
                decision.Result.OrgCode = org.OrgCode;
                decision.Result.OrgName = org.OrgName;
                if (!dryRun && (decision.AddCodes.Count > 0 || decision.RemoveCodes.Count > 0))
                {
                    PlanApplier.Apply(writer, decision, by, $"Plan {plan.Code} applied to organizations ({mode}).");
                    await _db.SaveChangesAsync(ct);
                    anyWritten = true;
                }
                result.Organizations.Add(decision.Result);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _db.ChangeTracker.Clear();
                _log.LogError(ex, "Applying plan {Plan} to organization {Organization} failed.", plan.Code, org.Id);
                result.Organizations.Add(new PlanApplyOrgResult
                {
                    OrganizationId = org.Id, OrgCode = org.OrgCode, OrgName = org.OrgName, Plan = plan.Code,
                    Skipped = [new PlanApplyFeatureItem { FeatureCode = "*", FeatureName = "All features", Reason = "Failed", Detail = ex.Message }]
                });
            }
        }

        if (anyWritten) TenantSnapshotProvider.InvalidateAll();
        result.TotalAdded = result.Organizations.Sum(o => o.Added.Count);
        result.TotalRemoved = result.Organizations.Sum(o => o.Removed.Count);
        result.TotalSkipped = result.Organizations.Sum(o => o.Skipped.Count);
        if (!dryRun)
            _log.LogInformation("Plan {Plan} applied ({Mode}) to {Count} organization(s) by user {UserId}: {Added} added, {Removed} removed, {Skipped} skipped.",
                plan.Code, mode, orgs.Count, userId, result.TotalAdded, result.TotalRemoved, result.TotalSkipped);
        return result;
    }

    private async Task<Plan?> FindPlanAsync(string code, bool track, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var key = code.Trim().ToLower();
        var q = track ? _db.Plans.AsQueryable() : _db.Plans.AsNoTracking();
        return await q.FirstOrDefaultAsync(p => p.Code.ToLower() == key, ct);
    }

    private static PlanModel ToModel(Plan p, int included, int total)
    {
        var m = new PlanModel();
        Fill(m, p, included, total);
        return m;
    }

    private static void Fill(PlanModel m, Plan p, int included, int total)
    {
        m.Id = p.Id; m.Code = p.Code; m.Name = p.Name; m.Description = p.Description; m.MonthlyPrice = p.MonthlyPrice;
        m.IsActive = p.IsActive; m.SortOrder = p.SortOrder; m.IncludedFeatureCount = included; m.TotalFeatureCount = total;
    }
}

/// <summary>A51 P2 — the super admin's instant status / visibility switches on a feature definition. There is no per-definition
/// history table (organization_feature_history is per organization), so each change is written to the application log.</summary>
public interface IFeatureAdminService
{
    Task<FeatureDefinitionModel> SetStatusAsync(string featureCode, string status, int? userId, CancellationToken ct = default);
    Task<FeatureDefinitionModel> SetVisibilityAsync(string featureCode, bool isVisible, int? userId, CancellationToken ct = default);
}

internal sealed class FeatureAdminService : IFeatureAdminService
{
    private readonly TenancyDbContext _db;
    private readonly ILogger<FeatureAdminService> _log;

    public FeatureAdminService(TenancyDbContext db, ILogger<FeatureAdminService>? log = null)
    {
        _db = db;
        _log = log ?? NullLogger<FeatureAdminService>.Instance;
    }

    public async Task<FeatureDefinitionModel> SetStatusAsync(string featureCode, string status, int? userId, CancellationToken ct = default)
    {
        var normalized = FeatureStatuses.Normalize(status)
            ?? throw new BadRequestException($"Status must be one of: {string.Join(", ", FeatureStatuses.All)}.");
        var def = await FindAsync(featureCode, ct);
        var old = def.Status;
        def.Status = normalized;
        await _db.SaveChangesAsync(ct);
        TenantSnapshotProvider.InvalidateAll();
        _log.LogInformation("Feature {Feature} status {Old} -> {New} by user {UserId}.", def.FeatureCode, old, normalized, userId);
        return ToModel(def);
    }

    public async Task<FeatureDefinitionModel> SetVisibilityAsync(string featureCode, bool isVisible, int? userId, CancellationToken ct = default)
    {
        var def = await FindAsync(featureCode, ct);
        var old = def.IsVisible;
        def.IsVisible = isVisible;
        await _db.SaveChangesAsync(ct);
        TenantSnapshotProvider.InvalidateAll();
        _log.LogInformation("Feature {Feature} visibility {Old} -> {New} by user {UserId}.", def.FeatureCode, old, isVisible, userId);
        return ToModel(def);
    }

    private async Task<FeatureDefinition> FindAsync(string code, CancellationToken ct)
    {
        var key = (code ?? "").Trim().ToLower();
        return await _db.FeatureDefinitions.FirstOrDefaultAsync(f => f.FeatureCode.ToLower() == key, ct)
            ?? throw new NotFoundException("Feature", code ?? "");
    }

    private static FeatureDefinitionModel ToModel(FeatureDefinition f) => new()
    {
        Id = f.Id, FeatureCode = f.FeatureCode, FeatureName = f.FeatureName, Category = f.Category, Description = f.Description,
        IsCore = f.IsCore, DisplayOrder = f.DisplayOrder, IsAvailable = f.IsAvailable, Status = f.Status, IsVisible = f.IsVisible, Icon = f.Icon
    };
}
