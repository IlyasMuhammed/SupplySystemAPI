using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Tenancy.Services;

/// <summary>A37 — the org admin's Settings › Modules (API-CONTRACT §1.1): MOD-01..11 over the existing feature registry.</summary>
public interface IModuleRegistryService
{
    Task<List<ModuleCardModel>> GetModulesAsync(Guid orgId, CancellationToken ct = default);
    Task<EnabledModulesModel> GetEnabledAsync(Guid orgId, CancellationToken ct = default);
    Task<ModuleCardModel> EnableAsync(Guid orgId, string code, EnableModuleRequest req, int userId, CancellationToken ct = default);
    Task<ModuleCardModel> DisableAsync(Guid orgId, string code, DisableModuleRequest req, int userId, CancellationToken ct = default);
    Task<ModuleCardModel> SetFeatureAsync(Guid orgId, string code, string featureCode, ToggleModuleFeatureRequest req, int userId, CancellationToken ct = default);
    /// <summary>Newest first. <paramref name="moduleCode"/> null = every code (the super admin's view).</summary>
    Task<List<ModuleHistoryEntryModel>> GetHistoryAsync(Guid orgId, string? moduleCode, CancellationToken ct = default);
    Task<ModuleImpactModel> GetImpactAsync(Guid orgId, string code, CancellationToken ct = default);
}

internal sealed class ModuleRegistryService : IModuleRegistryService
{
    internal const int DefaultGraceDays = 30;
    internal const int MaxGraceDays = 365;

    private readonly TenancyDbContext _db;
    private readonly ITenantSnapshotProvider _snapshots;
    private readonly IUserQueryService? _users;
    private readonly IReadOnlyList<IModuleImpactProvider> _impactProviders;
    private readonly TimeProvider _clock;

    public ModuleRegistryService(
        TenancyDbContext db, ITenantSnapshotProvider snapshots, IUserQueryService? users = null,
        IEnumerable<IModuleImpactProvider>? impactProviders = null, TimeProvider? clock = null)
    {
        _db = db;
        _snapshots = snapshots;
        _users = users;
        _impactProviders = impactProviders?.ToList() ?? [];
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── Reads ────────────────────────────────────────────────────────────────

    public async Task<List<ModuleCardModel>> GetModulesAsync(Guid orgId, CancellationToken ct = default)
    {
        var states = await ModuleState.LoadAsync(_db, orgId, track: false, ct);
        var names = await UserNamesAsync(states.Select(s => s.Row?.DisabledBy));
        var now = Now;
        return states.Where(s => s.IsModule && !IsHidden(s.Code))
            .Select(s => BuildCard(states, s, names, now))
            .OrderBy(c => ModuleState.SortRank(c.Status)).ThenBy(c => c.DisplayOrder)
            .ToList();
    }

    public async Task<EnabledModulesModel> GetEnabledAsync(Guid orgId, CancellationToken ct = default)
    {
        var states = await ModuleState.LoadAsync(_db, orgId, track: false, ct);
        var modules = states.Where(s => s.IsModule).Select(s => s.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var access = ModuleState.Evaluate(states, Now);
        return new EnabledModulesModel
        {
            Modules  = access.Values.Where(a => a.Level == FeatureAccessLevel.Full && modules.Contains(a.Code)).Select(a => a.Code).ToList(),
            Features = access.Values.Where(a => a.Level == FeatureAccessLevel.Full && !modules.Contains(a.Code)).Select(a => a.Code).ToList(),
            Grace    = access.Values.Where(a => a.Level == FeatureAccessLevel.Grace && modules.Contains(a.Code) && a.GraceEndsAt is not null)
                .Select(a => new ModuleGraceModel { Code = a.Code, GraceEndsAt = a.GraceEndsAt!.Value }).ToList()
        };
    }

    public async Task<List<ModuleHistoryEntryModel>> GetHistoryAsync(Guid orgId, string? moduleCode, CancellationToken ct = default)
    {
        var query = _db.OrganizationFeatureHistory.AsNoTracking().Where(h => h.OrganizationId == orgId);
        if (moduleCode is not null)
        {
            var module = await _db.FeatureDefinitions.AsNoTracking().FirstOrDefaultAsync(f => f.FeatureCode == moduleCode, ct);
            if (module is null || module.ParentModuleCode is not null) throw new NotFoundException("Module", moduleCode);
            var codes = await _db.FeatureDefinitions.Where(f => f.ParentModuleCode == module.FeatureCode)
                .Select(f => f.FeatureCode).ToListAsync(ct);
            codes.Add(module.FeatureCode);
            query = query.Where(h => codes.Contains(h.FeatureCode));
        }

        var rows = await query.OrderByDescending(h => h.PerformedAt).ToListAsync(ct);
        var names = await UserNamesAsync(rows.Select(r => r.PerformedBy));
        return rows.Select(r => new ModuleHistoryEntryModel
        {
            PerformedAt     = r.PerformedAt,
            Action          = r.Action,
            FeatureCode     = moduleCode is not null && r.FeatureCode.Equals(moduleCode, StringComparison.OrdinalIgnoreCase) ? null : r.FeatureCode,
            PerformedByName = r.PerformedBy is { } by ? names.GetValueOrDefault(by) ?? $"User {by}" : "System",
            GraceDays       = r.GraceDays,
            Notes           = r.Notes
        }).ToList();
    }

    public async Task<ModuleImpactModel> GetImpactAsync(Guid orgId, string code, CancellationToken ct = default)
    {
        var states = await ModuleState.LoadAsync(_db, orgId, track: false, ct);
        var byCode = states.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        var module = byCode.GetValueOrDefault(code);
        if (module is null || !module.IsModule) throw new NotFoundException("Module", code);

        var inProgress = new List<ModuleImpactLineModel>();
        foreach (var provider in _impactProviders.Where(p => p.ModuleCode.Equals(module.Code, StringComparison.OrdinalIgnoreCase)))
            inProgress.AddRange((await provider.GetInProgressAsync(orgId, ct)).Select(i => new ModuleImpactLineModel { Label = i.Label, Count = i.Count }));

        var onFeatures = states.Where(s => s.Definition.ParentModuleCode == module.Code && s.IsOn).Select(s => s.Name).ToList();
        var willBlock = new List<string>
        {
            $"New {module.Name} documents cannot be created.",
            $"{module.Name} menu items are hidden and its background jobs stop.",
            "When the grace period ends, its existing records become read-only."
        };
        if (onFeatures.Count > 0) willBlock.Add($"These features switch off with it: {string.Join(", ", onFeatures)}.");

        return new ModuleImpactModel
        {
            Code = module.Code,
            Name = module.Name,
            Dependents = EnabledDependents(byCode, module.Code).Select(d => new ModuleRefModel { Code = d.Code, Name = d.Name, IsEnabled = true }).ToList(),
            InProgress = inProgress,
            WillBlock = willBlock,
            NotAffected =
            [
                "No data is deleted — switching the module back on restores everything.",
                "Existing records stay readable.",
                "During the grace period, records already in progress can still be updated and completed.",
                "Other modules keep working."
            ],
            DefaultGraceDays = DefaultGraceDays
        };
    }

    // ── Writes (MOD-01..11) ──────────────────────────────────────────────────

    public async Task<ModuleCardModel> EnableAsync(Guid orgId, string code, EnableModuleRequest req, int userId, CancellationToken ct = default)
    {
        var writer = await OrgFeatureWriter.LoadAsync(_db, orgId, Now, ct);
        var module = GetModule(writer, code);
        CheckRowVersion(module, req.RowVersion);

        if (!module.Definition.IsAvailable) throw new BadRequestException($"{module.Name} is not yet available.");
        if (!module.IsLicensed) throw new BadRequestException($"{module.Name} is not included in your plan — contact your administrator.");
        if (module.IsOn) return await CardAsync(writer, module.Code);

        var missing = TenancyFeatureCatalog.Dependencies
            .Where(d => d.Dependent.Equals(module.Code, StringComparison.OrdinalIgnoreCase) && writer.Find(d.RequiredBy)?.IsOn != true)
            .Select(d => writer.Find(d.RequiredBy)?.Name ?? d.RequiredBy).ToList();
        if (missing.Count > 0) throw new BadRequestException($"Enable {string.Join(", ", missing)} first.");

        writer.SwitchOn(module.Code, userId, FeatureHistoryActions.Enabled);
        if (IsBomDriver(module.Code)) writer.ApplyBomRule(module.Name);
        return await SaveAndCardAsync(writer, module.Code, ct);
    }

    public async Task<ModuleCardModel> DisableAsync(Guid orgId, string code, DisableModuleRequest req, int userId, CancellationToken ct = default)
    {
        var writer = await OrgFeatureWriter.LoadAsync(_db, orgId, Now, ct);
        var module = GetModule(writer, code);
        CheckRowVersion(module, req.RowVersion);

        if (module.Definition.IsAlwaysOn || module.Definition.IsCore) throw new BadRequestException($"{module.Name} is always on.");
        var graceDays = req.GraceDays ?? DefaultGraceDays;
        if (graceDays is < 0 or > MaxGraceDays) throw new BadRequestException("Grace period must be between 0 and 365 days.");
        if (!module.IsOn) return await CardAsync(writer, module.Code);

        var dependents = EnabledDependents(writer.States.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase), module.Code)
            .Select(d => d.Name).ToList();
        if (dependents.Count > 0) throw new BadRequestException($"Disable {string.Join(", ", dependents)} first.");

        DateTime? graceEndsAt = graceDays > 0 ? writer.Now.AddDays(graceDays) : null;
        writer.SwitchOff(module.Code, userId, FeatureHistoryActions.Disabled, graceEndsAt, graceDays, Trim(req.Notes));
        if (IsBomDriver(module.Code)) writer.ApplyBomRule(module.Name, graceEndsAt);
        return await SaveAndCardAsync(writer, module.Code, ct);
    }

    public async Task<ModuleCardModel> SetFeatureAsync(
        Guid orgId, string code, string featureCode, ToggleModuleFeatureRequest req, int userId, CancellationToken ct = default)
    {
        var writer = await OrgFeatureWriter.LoadAsync(_db, orgId, Now, ct);
        var module = GetModule(writer, code);
        var feature = writer.Find(featureCode);
        if (feature is null || !string.Equals(feature.Definition.ParentModuleCode, module.Code, StringComparison.OrdinalIgnoreCase))
            throw new NotFoundException("Feature", featureCode);
        CheckRowVersion(module, req.RowVersion);

        if (!feature.Definition.IsAvailable) throw new BadRequestException($"{feature.Name} is not yet available.");

        if (req.Enabled)
        {
            if (feature.IsOn) return await CardAsync(writer, module.Code);
            if (!feature.IsLicensed) throw new BadRequestException($"{feature.Name} is not included in your plan — contact your administrator.");
            if (!module.IsOn) throw new BadRequestException($"Switch on {module.Name} first.");
            var missing = TenancyFeatureCatalog.Dependencies
                .Where(d => d.Dependent.Equals(feature.Code, StringComparison.OrdinalIgnoreCase) && writer.Find(d.RequiredBy)?.IsOn != true)
                .Select(d => writer.Find(d.RequiredBy)?.Name ?? d.RequiredBy).ToList();
            if (missing.Count > 0) throw new BadRequestException($"Enable {string.Join(", ", missing)} first.");

            writer.SwitchOn(feature.Code, userId, FeatureHistoryActions.FeatureEnabled);
        }
        else
        {
            if (feature.Definition.IsCore) throw new BadRequestException("Core features cannot be switched off.");
            if (feature.Row is not { IsEnabled: true }) return await CardAsync(writer, module.Code);
            var dependents = EnabledDependents(writer.States.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase), feature.Code)
                .Select(d => d.Name).ToList();
            if (dependents.Count > 0) throw new BadRequestException($"Disable {string.Join(", ", dependents)} first.");

            writer.SwitchOff(feature.Code, userId, FeatureHistoryActions.FeatureDisabled, null, null);
        }

        // MOD-08 — set by hand: the system leaves it alone from now on.
        writer.Row(feature.Code).IsSystemManaged = false;
        writer.TouchModule(module.Code, userId);
        return await SaveAndCardAsync(writer, module.Code, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsHidden(string code) => TenancyFeatureCatalog.ByCode.TryGetValue(code, out var e) && e.IsHidden;

    private static bool IsBomDriver(string code) =>
        TenancyFeatureCatalog.BomManagementDrivers.Contains(code, StringComparer.OrdinalIgnoreCase);

    private static string? Trim(string? notes) =>
        string.IsNullOrWhiteSpace(notes) ? null : notes.Trim() is { Length: > 500 } t ? t[..500] : notes.Trim();

    private static OrgFeatureState GetModule(OrgFeatureWriter writer, string code)
    {
        var module = writer.Find(code);
        if (module is null || !module.IsModule || IsHidden(module.Code)) throw new NotFoundException("Module", code);
        return module;
    }

    private static IEnumerable<OrgFeatureState> EnabledDependents(IReadOnlyDictionary<string, OrgFeatureState> byCode, string code) =>
        TenancyFeatureCatalog.Dependencies
            .Where(d => d.RequiredBy.Equals(code, StringComparison.OrdinalIgnoreCase))
            .Select(d => byCode.GetValueOrDefault(d.Dependent))
            .Where(s => s?.IsOn == true)
            .Select(s => s!);

    /// <summary>TS-30 — a stale rowVersion is a 409 before anything changes; the EF concurrency token catches a race after.</summary>
    private void CheckRowVersion(OrgFeatureState module, string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion) || module.Row is null) return;
        byte[] expected;
        try { expected = Convert.FromBase64String(rowVersion); }
        catch (FormatException) { throw new BadRequestException("rowVersion is not valid."); }

        if (!expected.AsSpan().SequenceEqual(module.Row.RowVersion ?? []))
            throw new ConflictException($"{module.Name} was changed by someone else. Reload and try again.");
        _db.Entry(module.Row).Property(r => r.RowVersion).OriginalValue = expected;
    }

    private async Task<ModuleCardModel> SaveAndCardAsync(OrgFeatureWriter writer, string moduleCode, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException($"{writer.Get(moduleCode).Name} was changed by someone else. Reload and try again.");
        }
        _snapshots.Invalidate(writer.OrganizationId);
        return await CardAsync(writer, moduleCode);
    }

    private async Task<ModuleCardModel> CardAsync(OrgFeatureWriter writer, string moduleCode)
    {
        var states = writer.States.ToList();
        var names = await UserNamesAsync(states.Select(s => s.Row?.DisabledBy));
        return BuildCard(states, writer.Get(moduleCode), names, writer.Now);
    }

    private async Task<Dictionary<int, string>> UserNamesAsync(IEnumerable<int?> ids)
    {
        var distinct = ids.Where(i => i is > 0).Select(i => i!.Value).Distinct().ToList();
        if (distinct.Count == 0 || _users is null) return [];
        return (await _users.GetUsersAsync(distinct)).ToDictionary(u => u.UserId, u => u.DisplayName);
    }

    private static ModuleCardModel BuildCard(
        IReadOnlyList<OrgFeatureState> states, OrgFeatureState module, IReadOnlyDictionary<int, string> names, DateTime now)
    {
        var byCode = states.ToDictionary(s => s.Code, StringComparer.OrdinalIgnoreCase);
        ModuleRefModel Ref(string c) => new()
        {
            Code = c, Name = byCode.GetValueOrDefault(c)?.Name ?? c, IsEnabled = byCode.GetValueOrDefault(c)?.IsOn == true
        };

        var features = states
            .Where(s => string.Equals(s.Definition.ParentModuleCode, module.Code, StringComparison.OrdinalIgnoreCase) && !IsHidden(s.Code))
            .Select(f => new ModuleFeatureModel
            {
                Code        = f.Code,
                Name        = f.Name,
                Description = f.Definition.Description,
                IsCore      = f.Definition.IsCore,
                IsAvailable = f.Definition.IsAvailable,
                IsLicensed  = f.IsLicensed,
                IsEnabled   = f.IsOn,
                RequiresCode = TenancyFeatureCatalog.Dependencies
                    .FirstOrDefault(d => d.Dependent.Equals(f.Code, StringComparison.OrdinalIgnoreCase)).RequiredBy,
                RequiredBy  = TenancyFeatureCatalog.Dependencies
                    .Where(d => d.RequiredBy.Equals(f.Code, StringComparison.OrdinalIgnoreCase)).Select(d => d.Dependent).ToList(),
                AutoManaged = f.Row?.IsSystemManaged == true
            }).ToList();

        var row = module.Row;
        var status = ModuleState.Status(module, now);
        return new ModuleCardModel
        {
            Code           = module.Code,
            Name           = module.Name,
            Description    = module.Definition.Description,
            Icon           = module.Definition.Icon,
            IsAlwaysOn     = module.Definition.IsAlwaysOn || module.Definition.IsCore,
            IsAvailable    = module.Definition.IsAvailable,
            IsLicensed     = module.IsLicensed,
            IsEnabled      = module.IsOn,
            Status         = status,
            GraceEndsAt    = status == ModuleState.StatusGrace ? row!.GracePeriodEndsAt : null,
            DisabledAt     = row is { IsEnabled: false } ? row.DisabledAt : null,
            DisabledByName = row is { IsEnabled: false, DisabledBy: { } by } ? names.GetValueOrDefault(by) ?? $"User {by}" : null,
            EnabledAt      = row is { IsEnabled: true } ? row.EnabledAt : null,
            DependsOn      = TenancyFeatureCatalog.Dependencies
                .Where(d => d.Dependent.Equals(module.Code, StringComparison.OrdinalIgnoreCase)).Select(d => Ref(d.RequiredBy)).ToList(),
            Dependents     = TenancyFeatureCatalog.Dependencies
                .Where(d => d.RequiredBy.Equals(module.Code, StringComparison.OrdinalIgnoreCase)).Select(d => Ref(d.Dependent)).ToList(),
            Features       = features,
            FeatureCount   = features.Count,
            EnabledFeatureCount = features.Count(f => f.IsEnabled),
            RowVersion     = Convert.ToBase64String(row?.RowVersion ?? []),
            DisplayOrder   = module.Definition.DisplayOrder
        };
    }
}
