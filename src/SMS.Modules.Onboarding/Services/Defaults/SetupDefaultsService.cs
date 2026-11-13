using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Modules.Onboarding.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Services.Defaults;

/// <summary>A row picked by the fallback chain, and the level it was found at (D-4).</summary>
public sealed record ResolvedDefault(SetupDefault Row, string Country, string Industry);

/// <summary>A50 D-4 / tasks 028–036 — the Super Admin store of default payloads.</summary>
public interface ISetupDefaultsService
{
    Task<List<SetupDefaultModel>> ListAsync(string? country, string? industry, string? category, bool? active, CancellationToken ct = default);
    Task<SetupDefaultModel> GetAsync(Guid uuid, CancellationToken ct = default);
    Task<SetupDefaultModel> CreateAsync(CreateSetupDefaultRequest request, int? userId, CancellationToken ct = default);
    Task<SetupDefaultModel> UpdateAsync(Guid uuid, UpdateSetupDefaultRequest request, int? userId, CancellationToken ct = default);
    Task DeleteAsync(Guid uuid, CancellationToken ct = default);
    Task<SetupDefaultModel> SetActiveAsync(Guid uuid, bool active, int? userId, CancellationToken ct = default);
    Task<SetupDefaultsImportResult> ImportAsync(SetupDefaultsPackFile pack, bool dryRun, string mode, int? userId, CancellationToken ct = default);
    Task<SetupDefaultsPackFile> ExportAsync(string? country, string? industry, string? category, CancellationToken ct = default);
    ValidateSetupPayloadResult Validate(string? category, JsonElement payload);
    Task<SetupDefaultsMetaModel> MetaAsync(CancellationToken ct = default);
    /// <summary>Fallback chain: exact → country+GLOBAL → GLOBAL+industry → GLOBAL+GLOBAL; active rows only.</summary>
    Task<ResolvedDefault?> ResolveAsync(string? country, string? industry, string category, CancellationToken ct = default);
    /// <summary>Every category of <see cref="SetupPayloadValidator.KnownCategories"/> resolved at once (one query).</summary>
    Task<IReadOnlyDictionary<string, ResolvedDefault?>> ResolveAllAsync(string? country, string? industry, CancellationToken ct = default);
}

internal sealed class SetupDefaultsService : ISetupDefaultsService
{
    private static readonly Regex CountryPattern  = new("^([A-Z]{2}|GLOBAL)$", RegexOptions.Compiled);
    private static readonly Regex IndustryPattern = new("^[A-Za-z0-9_]{1,50}$", RegexOptions.Compiled);
    private static readonly string[] AppliedActions = [SetupLogActions.Seed, SetupLogActions.Merge, SetupLogActions.Overwrite, SetupLogActions.FullReset];

    private readonly OnboardingDbContext _db;
    private readonly SetupPayloadValidator _validator;

    public SetupDefaultsService(OnboardingDbContext db, SetupPayloadValidator validator)
    {
        _db = db;
        _validator = validator;
    }

    // ── fallback (task 009 / 036) ──

    /// <summary>The chain's levels for a (country, industry), most specific first, without repeats.</summary>
    internal static IReadOnlyList<(string Country, string Industry)> Chain(string? country, string? industry)
    {
        var c = SetupKey.Country(country);
        var i = SetupKey.Industry(industry);
        return new[] { (c, i), (c, SetupCodes.Global), (SetupCodes.Global, i), (SetupCodes.Global, SetupCodes.Global) }
            .Distinct().ToList();
    }

    internal static ResolvedDefault? Pick(IEnumerable<SetupDefault> rows, string? country, string? industry, string category)
    {
        var candidates = rows.Where(r => r.IsActive && r.DataCategory.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var (c, i) in Chain(country, industry))
            if (candidates.FirstOrDefault(r => r.CountryCode.Equals(c, StringComparison.OrdinalIgnoreCase)
                                            && r.IndustryType.Equals(i, StringComparison.OrdinalIgnoreCase)) is { } hit)
                return new ResolvedDefault(hit, hit.CountryCode, hit.IndustryType);
        return null;
    }

    private Task<List<SetupDefault>> ChainRowsAsync(string? country, string? industry, CancellationToken ct)
    {
        var countries = new[] { SetupKey.Country(country), SetupCodes.Global }.Distinct().ToList();
        var industries = new[] { SetupKey.Industry(industry), SetupCodes.Global }.Distinct().ToList();
        return _db.SetupDefaults.AsNoTracking()
            .Where(r => r.IsActive && countries.Contains(r.CountryCode) && industries.Contains(r.IndustryType))
            .ToListAsync(ct);
    }

    public async Task<ResolvedDefault?> ResolveAsync(string? country, string? industry, string category, CancellationToken ct = default) =>
        Pick(await ChainRowsAsync(country, industry, ct), country, industry, SetupKey.Category(category));

    public async Task<IReadOnlyDictionary<string, ResolvedDefault?>> ResolveAllAsync(string? country, string? industry, CancellationToken ct = default)
    {
        var rows = await ChainRowsAsync(country, industry, ct);
        return _validator.KnownCategories.ToDictionary(c => c, c => Pick(rows, country, industry, c), StringComparer.OrdinalIgnoreCase);
    }

    // ── reads ──

    public async Task<List<SetupDefaultModel>> ListAsync(string? country, string? industry, string? category, bool? active, CancellationToken ct = default)
    {
        var q = _db.SetupDefaults.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(country))  { var c = SetupKey.Country(country);   q = q.Where(r => r.CountryCode == c); }
        if (!string.IsNullOrWhiteSpace(industry)) { var i = SetupKey.Industry(industry); q = q.Where(r => r.IndustryType == i); }
        if (!string.IsNullOrWhiteSpace(category)) { var k = SetupKey.Category(category); q = q.Where(r => r.DataCategory == k); }
        if (active is { } a) q = q.Where(r => r.IsActive == a);

        var rows = await q.ToListAsync(ct);
        var applied = await AppliedCountsAsync(rows.Select(r => r.Id).ToList(), ct);
        var order = _validator.KnownCategories;
        return rows
            .OrderBy(r => r.CountryCode == SetupCodes.Global ? 1 : 0).ThenBy(r => r.CountryCode)
            .ThenBy(r => r.IndustryType == SetupCodes.Global ? 1 : 0).ThenBy(r => r.IndustryType)
            .ThenBy(r => Index(order, r.DataCategory))
            .Select(r => ToModel(r, applied.GetValueOrDefault(r.Id), withPayload: false))
            .ToList();
    }

    public async Task<SetupDefaultModel> GetAsync(Guid uuid, CancellationToken ct = default)
    {
        var row = await FindAsync(uuid, ct);
        return ToModel(row, (await AppliedCountsAsync([row.Id], ct)).GetValueOrDefault(row.Id), withPayload: true);
    }

    public async Task<SetupDefaultsMetaModel> MetaAsync(CancellationToken ct = default)
    {
        var keys = await _db.SetupDefaults.AsNoTracking().Select(r => new { r.CountryCode, r.IndustryType }).ToListAsync(ct);
        return new SetupDefaultsMetaModel
        {
            Categories = _validator.KnownCategories.Select(c => new SetupCategoryMetaModel
            {
                Code = c,
                HasSeeder = _validator.Seeders.ContainsKey(c),
                HasSchema = _validator.HasSchema(c),
                RequiresModule = _validator.Seeders.TryGetValue(c, out var s) ? s.RequiresModule : null
            }).ToList(),
            Countries = keys.Select(k => k.CountryCode).Append(SetupCodes.Global).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList(),
            Industries = keys.Select(k => k.IndustryType).Append(SetupCodes.Global).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c).ToList()
        };
    }

    public ValidateSetupPayloadResult Validate(string? category, JsonElement payload)
    {
        var errors = new List<string>();
        var key = SetupKey.Category(category);
        if (!_validator.IsKnownCategory(key)) errors.Add($"Unknown data category '{category}'.");
        else errors.AddRange(_validator.Validate(key, payload));
        return new ValidateSetupPayloadResult { Valid = errors.Count == 0, Errors = errors };
    }

    // ── writes ──

    public async Task<SetupDefaultModel> CreateAsync(CreateSetupDefaultRequest request, int? userId, CancellationToken ct = default)
    {
        var (country, industry, category, errors) = ValidateKey(request.CountryCode, request.IndustryType, request.DataCategory);
        if (errors.Count == 0) errors.AddRange(PayloadErrors(category, request.DataPayload));
        if (errors.Count > 0) throw new BadRequestException(string.Join(" ", errors));

        if (await FindByKeyAsync(country, industry, category, ct) is not null)
            throw new ConflictException($"A default for {SetupKey.Format(country, industry, category)} already exists — edit it instead.");

        var row = new SetupDefault
        {
            CountryCode = country, IndustryType = industry, DataCategory = category,
            DisplayName = DisplayNameOr(request.DisplayName, country, industry, category),
            DataPayload = request.DataPayload.GetRawText(), Version = 1, IsActive = request.IsActive,
            Notes = Trim(request.Notes, 1000), CreatedBy = userId, ModifiedBy = userId
        };
        _db.SetupDefaults.Add(row);
        await SaveAsync(ct);
        return ToModel(row, 0, withPayload: true);
    }

    public async Task<SetupDefaultModel> UpdateAsync(Guid uuid, UpdateSetupDefaultRequest request, int? userId, CancellationToken ct = default)
    {
        var row = await FindAsync(uuid, ct, tracked: true);
        if (!string.IsNullOrWhiteSpace(request.RowVersion))
        {
            byte[] expected;
            try { expected = Convert.FromBase64String(request.RowVersion); }
            catch (FormatException) { throw new BadRequestException("rowVersion is not valid base64."); }
            _db.Entry(row).Property(r => r.RowVersion).OriginalValue = expected;
        }

        if (request.DataPayload.ValueKind != JsonValueKind.Undefined)
        {
            var errors = PayloadErrors(row.DataCategory, request.DataPayload);
            if (errors.Count > 0) throw new BadRequestException(string.Join(" ", errors));
            if (ApplyPayload(row, request.DataPayload)) row.ModifiedBy = userId;
        }
        if (request.DisplayName is not null) row.DisplayName = DisplayNameOr(request.DisplayName, row.CountryCode, row.IndustryType, row.DataCategory);
        if (request.Notes is not null) row.Notes = Trim(request.Notes, 1000);
        if (request.IsActive is { } active) row.IsActive = active;
        row.ModifiedBy = userId;

        await SaveAsync(ct);
        return ToModel(row, (await AppliedCountsAsync([row.Id], ct)).GetValueOrDefault(row.Id), withPayload: true);
    }

    public async Task DeleteAsync(Guid uuid, CancellationToken ct = default)
    {
        var row = await FindAsync(uuid, ct, tracked: true);
        // The startup pack seeder re-inserts a missing system pack key, so deleting one would silently come back (REV50-2).
        if (row.IsSystemPack)
            throw new ConflictException("This default comes from a built-in pack and would be restored at the next start. Unpublish (deactivate) it instead.");
        // Task 033 — a default an organization has applied (any version of it) stays: deactivate instead.
        var used = await _db.TenantSetupLogs.IgnoreQueryFilters().Where(l => l.SetupDefaultId == row.Id)
            .Select(l => l.OrganizationId).Distinct().CountAsync(ct);
        if (used > 0)
            throw new ConflictException($"{used} organization(s) have applied this default, so it cannot be deleted. Unpublish (deactivate) it instead.");
        _db.SetupDefaults.Remove(row);
        await SaveAsync(ct);
    }

    public async Task<SetupDefaultModel> SetActiveAsync(Guid uuid, bool active, int? userId, CancellationToken ct = default)
    {
        var row = await FindAsync(uuid, ct, tracked: true);
        if (row.IsActive != active)
        {
            row.IsActive = active;
            row.ModifiedBy = userId;
            await SaveAsync(ct);
        }
        return ToModel(row, (await AppliedCountsAsync([row.Id], ct)).GetValueOrDefault(row.Id), withPayload: true);
    }

    // ── import / export (tasks 034 / 035) ──

    public async Task<SetupDefaultsImportResult> ImportAsync(SetupDefaultsPackFile pack, bool dryRun, string mode, int? userId, CancellationToken ct = default)
    {
        if (pack.Format is { Length: > 0 } format && !format.Equals(SetupDefaultsPackFormat.Name, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException($"Unknown file format '{format}' (expected '{SetupDefaultsPackFormat.Name}').");
        if (pack.Defaults.Count == 0) throw new BadRequestException("The file has no defaults.");
        var insertOnly = string.Equals(mode, SetupImportModes.InsertOnly, StringComparison.OrdinalIgnoreCase);
        if (!insertOnly && !string.Equals(mode, SetupImportModes.Upsert, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException($"Unknown import mode '{mode}' (upsert or insertOnly).");

        var result = new SetupDefaultsImportResult { DryRun = dryRun };
        var existing = await _db.SetupDefaults.ToListAsync(ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < pack.Defaults.Count; index++)
        {
            var item = pack.Defaults[index];
            var (country, industry, category, errors) = ValidateKey(item.CountryCode, item.IndustryType, item.DataCategory);
            var key = SetupKey.Format(country, industry, category);
            if (errors.Count == 0 && !seen.Add(key)) errors.Add("The file has this key more than once.");
            if (errors.Count == 0) errors.AddRange(PayloadErrors(category, item.Payload));
            if (errors.Count > 0)
            {
                result.Errors.Add(new SetupDefaultsImportError(index, key, errors));
                result.Items.Add(new SetupDefaultsImportItem(key, "Invalid"));
                continue;
            }

            var row = existing.FirstOrDefault(r => r.CountryCode.Equals(country, StringComparison.OrdinalIgnoreCase)
                && r.IndustryType.Equals(industry, StringComparison.OrdinalIgnoreCase) && r.DataCategory.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                _db.SetupDefaults.Add(new SetupDefault
                {
                    CountryCode = country, IndustryType = industry, DataCategory = category,
                    DisplayName = DisplayNameOr(item.DisplayName ?? JsonCanonical.TopLevelString(item.Payload, "displayName"), country, industry, category),
                    DataPayload = item.Payload.GetRawText(), Version = 1, IsActive = item.IsActive ?? true,
                    Notes = Trim(item.Notes, 1000), CreatedBy = userId, ModifiedBy = userId
                });
                result.Created++;
                result.Items.Add(new SetupDefaultsImportItem(key, "Created"));
                continue;
            }

            var changed = false;
            if (!insertOnly)
            {
                changed |= ApplyPayload(row, item.Payload);
                if (item.DisplayName is { Length: > 0 } dn && dn != row.DisplayName) { row.DisplayName = DisplayNameOr(dn, country, industry, category); changed = true; }
                if (item.Notes is not null && item.Notes != row.Notes) { row.Notes = Trim(item.Notes, 1000); changed = true; }
                if (item.IsActive is { } active && active != row.IsActive) { row.IsActive = active; changed = true; }
                if (changed) row.ModifiedBy = userId;
            }
            if (changed) result.Updated++; else result.Unchanged++;
            result.Items.Add(new SetupDefaultsImportItem(key, changed ? "Updated" : "Unchanged"));
        }

        // Task 039 — all or nothing: one bad item and nothing is written.
        if (result.Errors.Count > 0 || dryRun)
        {
            _db.ChangeTracker.Clear();
            return result;
        }
        await SaveAsync(ct);
        return result;
    }

    public async Task<SetupDefaultsPackFile> ExportAsync(string? country, string? industry, string? category, CancellationToken ct = default)
    {
        var q = _db.SetupDefaults.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(country))  { var c = SetupKey.Country(country);   q = q.Where(r => r.CountryCode == c); }
        if (!string.IsNullOrWhiteSpace(industry)) { var i = SetupKey.Industry(industry); q = q.Where(r => r.IndustryType == i); }
        if (!string.IsNullOrWhiteSpace(category)) { var k = SetupKey.Category(category); q = q.Where(r => r.DataCategory == k); }
        var order = _validator.KnownCategories;
        var rows = (await q.ToListAsync(ct)).OrderBy(r => r.CountryCode).ThenBy(r => r.IndustryType).ThenBy(r => Index(order, r.DataCategory));
        return new SetupDefaultsPackFile
        {
            ExportedAt = DateTime.UtcNow,
            Defaults = rows.Select(r => new SetupDefaultsPackItem
            {
                CountryCode = r.CountryCode, IndustryType = r.IndustryType, DataCategory = r.DataCategory, DisplayName = r.DisplayName,
                Notes = r.Notes, IsActive = r.IsActive, Version = r.Version, Payload = Parse(r.DataPayload)
            }).ToList()
        };
    }

    // ── helpers ──

    /// <summary>Sets the payload; Version +1 only when it really changed (canonical compare, BR-50-013).</summary>
    internal static bool ApplyPayload(SetupDefault row, JsonElement payload)
    {
        string current;
        try { current = JsonCanonical.Write(row.DataPayload); }
        catch (JsonException) { current = string.Empty; }
        if (current == JsonCanonical.Write(payload)) return false;
        row.DataPayload = payload.GetRawText();
        row.Version++;
        return true;
    }

    private (string Country, string Industry, string Category, List<string> Errors) ValidateKey(string? countryCode, string? industryType, string? dataCategory)
    {
        var errors = new List<string>();
        var country = SetupKey.Country(countryCode);
        var industry = SetupKey.Industry(industryType);
        var category = SetupKey.Category(dataCategory);
        if (!CountryPattern.IsMatch(country)) errors.Add($"Country '{countryCode}' must be an ISO alpha-2 code or GLOBAL.");
        if (!IndustryPattern.IsMatch(industry)) errors.Add($"Industry '{industryType}' must be 1–50 letters, digits or underscores.");
        if (string.IsNullOrEmpty(category)) errors.Add("A data category is required.");
        else if (!_validator.IsKnownCategory(category)) errors.Add($"Unknown data category '{dataCategory}'.");
        return (country, industry, category, errors);
    }

    private List<string> PayloadErrors(string category, JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Undefined ? ["A payload is required."] : _validator.Validate(category, payload);

    private async Task<SetupDefault?> FindByKeyAsync(string country, string industry, string category, CancellationToken ct) =>
        (await _db.SetupDefaults.AsNoTracking().Where(r => r.DataCategory == category).ToListAsync(ct))
            .FirstOrDefault(r => r.CountryCode.Equals(country, StringComparison.OrdinalIgnoreCase) && r.IndustryType.Equals(industry, StringComparison.OrdinalIgnoreCase));

    private async Task<SetupDefault> FindAsync(Guid uuid, CancellationToken ct, bool tracked = false)
    {
        var q = tracked ? _db.SetupDefaults : _db.SetupDefaults.AsNoTracking();
        return await q.FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Setup default", uuid);
    }

    private async Task<Dictionary<int, int>> AppliedCountsAsync(List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        var rows = await _db.TenantSetupLogs.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.SetupDefaultId != null && ids.Contains(l.SetupDefaultId.Value) && AppliedActions.Contains(l.Action))
            .Select(l => new { Id = l.SetupDefaultId!.Value, l.OrganizationId }).Distinct().ToListAsync(ct);
        return rows.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.Count());
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("Someone else changed this default — reload it and try again."); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UQ_SD_Key", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new ConflictException("A default for that country, industry and category already exists.");
        }
    }

    private static SetupDefaultModel ToModel(SetupDefault r, int appliedCount, bool withPayload)
    {
        var payload = Parse(r.DataPayload);
        return new SetupDefaultModel
        {
            Uuid = r.Uuid, CountryCode = r.CountryCode, IndustryType = r.IndustryType, DataCategory = r.DataCategory, DisplayName = r.DisplayName,
            Version = r.Version, IsActive = r.IsActive, Notes = r.Notes, IsSystemPack = r.IsSystemPack,
            RecordCount = JsonCanonical.CountRecords(payload), AppliedCount = appliedCount,
            CreatedAt = r.CreatedAt, CreatedBy = r.CreatedBy, ModifiedAt = r.ModifiedAt, ModifiedBy = r.ModifiedBy,
            RowVersion = r.RowVersion is { Length: > 0 } rv ? Convert.ToBase64String(rv) : null,
            DataPayload = withPayload ? payload : null
        };
    }

    internal static SetupDefaultModel ToResolvedModel(ResolvedDefault resolved) => new()
    {
        Uuid = resolved.Row.Uuid, CountryCode = resolved.Row.CountryCode, IndustryType = resolved.Row.IndustryType,
        DataCategory = resolved.Row.DataCategory, DisplayName = resolved.Row.DisplayName, Version = resolved.Row.Version,
        IsActive = resolved.Row.IsActive, Notes = resolved.Row.Notes, IsSystemPack = resolved.Row.IsSystemPack,
        RecordCount = JsonCanonical.CountRecords(Parse(resolved.Row.DataPayload)),
        CreatedAt = resolved.Row.CreatedAt, ModifiedAt = resolved.Row.ModifiedAt,
        DataPayload = Parse(resolved.Row.DataPayload),
        ResolvedFrom = new ResolvedFromModel { Country = resolved.Country, Industry = resolved.Industry }
    };

    internal static JsonElement Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }
    }

    private static int Index(IReadOnlyList<string> order, string category)
    {
        var i = order.ToList().FindIndex(c => c.Equals(category, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? int.MaxValue : i;
    }

    private static string DisplayNameOr(string? name, string country, string industry, string category) =>
        string.IsNullOrWhiteSpace(name) ? $"{country} {industry} — {category}" : Trim(name, 200)!;

    private static string? Trim(string? s, int max) => s is null ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];
}
