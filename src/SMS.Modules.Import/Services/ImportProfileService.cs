using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Import.Data;
using SMS.Modules.Import.Domain;
using SMS.Modules.Import.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Import.Services;

/// <summary>The duplicate strategy and limits in force for one organization and entity type.</summary>
internal sealed record ImportEffectiveSettings(string DuplicateStrategy, int MaxRowsPerFile, int MaxFileSizeMb);

public interface IImportProfileService
{
    Task<IReadOnlyList<ImportProfileModel>> ListAsync(CancellationToken ct = default);
    Task<ImportProfileDetailModel> GetAsync(string entityType, CancellationToken ct = default);
    Task<ImportProfileDetailModel> UpdateSettingsAsync(string entityType, ImportProfileSettingsRequest req, ImportCaller caller, CancellationToken ct = default);
    /// <summary>D-3 — mirrors the registered targets into import.profiles / profile_fields (startup).</summary>
    Task ReconcileAsync(CancellationToken ct = default);
}

internal sealed class ImportProfileService : IImportProfileService
{
    private readonly ImportDbContext _db;
    private readonly ImportTargetRegistry _registry;
    private readonly ITenantContext _tenant;

    public ImportProfileService(ImportDbContext db, ImportTargetRegistry registry, ITenantContext tenant)
    {
        _db = db; _registry = registry; _tenant = tenant;
    }

    /// <summary>Profile defaults: SKIP (opening balances: ERROR, IMP-OB-06), 50 000 rows, 10 MB.</summary>
    internal static ImportEffectiveSettings Defaults(ImportProfileDefinition def) => new(
        def.IsOpeningBalance ? ImportDuplicateStrategy.Error : ImportDuplicateStrategy.Skip,
        ImportLimits.DefaultMaxRowsPerFile, ImportLimits.DefaultMaxFileSizeMb);

    internal static async Task<ImportEffectiveSettings> EffectiveAsync(ImportDbContext db, Guid org, ImportProfileDefinition def, CancellationToken ct)
    {
        var defaults = Defaults(def);
        var row = await db.ProfileOverrides.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrganizationId == org && o.EntityType == def.EntityType, ct);
        if (row is null) return defaults;
        var strategy = def.IsOpeningBalance ? ImportDuplicateStrategy.Error : row.DuplicateStrategy;
        return new(strategy, row.MaxRowsPerFile, row.MaxFileSizeMb);
    }

    public async Task<IReadOnlyList<ImportProfileModel>> ListAsync(CancellationToken ct = default)
    {
        var org = _tenant.OrganizationId;
        var stats = await StatsAsync(org, ct);
        var result = new List<ImportProfileModel>();
        foreach (var entry in _registry.All)
        {
            var s = await EffectiveAsync(_db, org, entry.Definition, ct);
            var st = stats.GetValueOrDefault(entry.Definition.EntityType);
            var d = entry.Definition;
            result.Add(new ImportProfileModel
            {
                EntityType = d.EntityType, Name = d.Name, Description = d.Description, ImportOrder = d.ImportOrder,
                IsOpeningBalance = d.IsOpeningBalance, MatchFields = d.MatchFields, DuplicateStrategy = s.DuplicateStrategy,
                MaxRowsPerFile = s.MaxRowsPerFile, MaxFileSizeMb = s.MaxFileSizeMb, AllowUpdate = d.AllowUpdate && !d.IsOpeningBalance,
                RequiresOpeningBalancePermission = d.IsOpeningBalance,
                CompletedSessions = st?.Count ?? 0, LastCompletedAt = st?.LastAt, LastCompletedSessionUuid = st?.LastUuid,
                LastImportedRows = st?.LastRows
            });
        }
        return result;
    }

    public async Task<ImportProfileDetailModel> GetAsync(string entityType, CancellationToken ct = default)
    {
        var entry = _registry.Get(entityType);
        var org = _tenant.OrganizationId;
        var s = await EffectiveAsync(_db, org, entry.Definition, ct);
        var st = (await StatsAsync(org, ct)).GetValueOrDefault(entry.Definition.EntityType);
        var d = entry.Definition;
        return new ImportProfileDetailModel
        {
            EntityType = d.EntityType, Name = d.Name, Description = d.Description, ImportOrder = d.ImportOrder,
            IsOpeningBalance = d.IsOpeningBalance, MatchFields = d.MatchFields, DuplicateStrategy = s.DuplicateStrategy,
            MaxRowsPerFile = s.MaxRowsPerFile, MaxFileSizeMb = s.MaxFileSizeMb, AllowUpdate = d.AllowUpdate && !d.IsOpeningBalance,
            RequiresOpeningBalancePermission = d.IsOpeningBalance,
            CompletedSessions = st?.Count ?? 0, LastCompletedAt = st?.LastAt, LastCompletedSessionUuid = st?.LastUuid,
            LastImportedRows = st?.LastRows,
            Fields = entry.Fields.Select(ToModel).ToList()
        };
    }

    internal static ImportFieldModel ToModel(ImportFieldDefinition f) => new()
    {
        FieldName = f.FieldName, DisplayName = f.DisplayName, Description = f.Description, FieldType = f.FieldType,
        IsRequired = f.IsRequired, IsKey = f.IsKey, SortOrder = f.SortOrder, SampleValue = f.SampleValue, MaxLength = f.MaxLength,
        MinValue = f.MinValue, MaxValue = f.MaxValue, DecimalPlaces = f.DecimalPlaces, LookupList = f.LookupList
    };

    public async Task<ImportProfileDetailModel> UpdateSettingsAsync(string entityType, ImportProfileSettingsRequest req, ImportCaller caller, CancellationToken ct = default)
    {
        var entry = _registry.Get(entityType);
        var def = entry.Definition;
        var org = _tenant.OrganizationId;
        var current = await EffectiveAsync(_db, org, def, ct);

        var strategy = req.DuplicateStrategy?.Trim().ToUpperInvariant() ?? current.DuplicateStrategy;
        if (!ImportDuplicateStrategy.All.Contains(strategy))
            throw new BadRequestException("Duplicate strategy must be SKIP, UPDATE, CREATE_NEW or ERROR.");
        if (def.IsOpeningBalance && strategy != ImportDuplicateStrategy.Error) throw new BadRequestException(ImportMessages.OpeningStrategy);
        if (strategy == ImportDuplicateStrategy.Update && !def.AllowUpdate) throw new BadRequestException(ImportMessages.UpdateNotAllowed);

        var maxRows = req.MaxRowsPerFile ?? current.MaxRowsPerFile;
        if (maxRows < 1 || maxRows > ImportLimits.DefaultMaxRowsPerFile)
            throw new BadRequestException($"Maximum rows per file must be between 1 and {ImportLimits.DefaultMaxRowsPerFile}.");
        var maxMb = req.MaxFileSizeMb ?? current.MaxFileSizeMb;
        if (maxMb < 1 || maxMb > ImportLimits.DefaultMaxFileSizeMb)
            throw new BadRequestException($"Maximum file size must be between 1 and {ImportLimits.DefaultMaxFileSizeMb} MB.");

        var row = await _db.ProfileOverrides.IgnoreQueryFilters()
            .FirstOrDefaultAsync(o => o.OrganizationId == org && o.EntityType == def.EntityType, ct);
        if (row is null)
        {
            row = new ImportProfileOverride { OrganizationId = org, EntityType = def.EntityType };
            _db.ProfileOverrides.Add(row);
        }
        row.DuplicateStrategy = strategy;
        row.MaxRowsPerFile = maxRows;
        row.MaxFileSizeMb = maxMb;
        row.ModifiedBy = caller.UserId;
        await _db.SaveChangesAsync(ct);
        return await GetAsync(def.EntityType, ct);
    }

    private sealed record Stat(int Count, DateTime? LastAt, Guid LastUuid, int? LastRows);

    private async Task<Dictionary<string, Stat>> StatsAsync(Guid org, CancellationToken ct)
    {
        var done = await _db.Sessions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == org &&
                        (s.Status == ImportSessionStatus.Completed || s.Status == ImportSessionStatus.CompletedWithErrors))
            .Select(s => new { s.EntityType, s.ImportCompletedAt, s.Uuid, Rows = s.ImportedRows + s.UpdatedRows })
            .ToListAsync(ct);
        return done.GroupBy(d => d.EntityType, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g =>
        {
            var last = g.OrderByDescending(x => x.ImportCompletedAt).First();
            return new Stat(g.Count(), last.ImportCompletedAt, last.Uuid, last.Rows);
        }, StringComparer.OrdinalIgnoreCase);
    }

    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var existing = await _db.Profiles.Include(p => p.Fields).ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _registry.All)
        {
            var def = entry.Definition;
            seen.Add(def.EntityType);
            var row = existing.FirstOrDefault(p => p.EntityType.Equals(def.EntityType, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new ImportProfile { EntityType = def.EntityType };
                _db.Profiles.Add(row);
            }
            row.Name = def.Name;
            row.Description = def.Description;
            row.ImportOrder = def.ImportOrder;
            row.IsOpeningBalance = def.IsOpeningBalance;
            row.AllowUpdate = def.AllowUpdate && !def.IsOpeningBalance;
            row.MatchFieldsJson = JsonSerializer.Serialize(def.MatchFields);
            row.IsActive = true;
            row.SyncedAt = now;

            var fieldNames = entry.Fields.Select(f => f.FieldName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in row.Fields.Where(f => !fieldNames.Contains(f.FieldName)).ToList())
            {
                row.Fields.Remove(stale);
                _db.ProfileFields.Remove(stale);
            }
            foreach (var f in entry.Fields)
            {
                var field = row.Fields.FirstOrDefault(x => x.FieldName.Equals(f.FieldName, StringComparison.OrdinalIgnoreCase));
                if (field is null)
                {
                    field = new ImportProfileField { FieldName = f.FieldName };
                    row.Fields.Add(field);
                }
                field.DisplayName = f.DisplayName;
                field.Description = f.Description;
                field.FieldType = f.FieldType;
                field.IsRequired = f.IsRequired;
                field.IsKey = f.IsKey;
                field.SortOrder = f.SortOrder;
                field.SampleValue = f.SampleValue;
                field.MaxLength = f.MaxLength;
                field.MinValue = f.MinValue;
                field.MaxValue = f.MaxValue;
                field.DecimalPlaces = f.DecimalPlaces;
                field.ValidationRegex = f.ValidationRegex;
                field.DefaultValue = f.DefaultValue;
                field.LookupList = f.LookupList;
                field.AliasesJson = f.Aliases is { Count: > 0 } a ? JsonSerializer.Serialize(a) : null;
            }
        }
        foreach (var gone in existing.Where(p => !seen.Contains(p.EntityType))) gone.IsActive = false;
        await _db.SaveChangesAsync(ct);
    }
}
