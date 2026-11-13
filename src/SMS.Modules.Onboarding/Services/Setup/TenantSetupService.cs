using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Modules.Onboarding.Models;
using SMS.Modules.Onboarding.Services.Defaults;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Services.Setup;

/// <summary>Full reset refused (BR-50-008): the controller answers 409 with <see cref="Check"/> as the result.</summary>
internal sealed class SetupResetBlockedException : ConflictException
{
    public SetupReferenceCheck Check { get; }

    public SetupResetBlockedException(string category, SetupReferenceCheck check)
        : base($"{check.ReferencedCount} seeded {category} record(s) are used on documents, so a full reset is not possible. Use Merge or Overwrite instead.") =>
        Check = check;
}

/// <summary>A50 D-10 / D-11 / tasks 040–045, 061–066, 073–078 — the setup wizard and the default data dashboard.</summary>
public interface ITenantSetupService
{
    Task<SetupStatusModel?> GetStatusAsync(bool canRunWizard, CancellationToken ct = default);
    Task<List<SetupOptionModel>> GetCountriesAsync(CancellationToken ct = default);
    Task<List<SetupOptionModel>> GetIndustriesAsync(string? country, CancellationToken ct = default);
    Task<List<SetupPreviewItem>> PreviewAsync(SetupPreviewRequest request, CancellationToken ct = default);
    Task<SetupApplyResponse> ApplyAsync(SetupApplyRequest request, int? userId, CancellationToken ct = default);
    Task<SetupApplyResponse> SkipAsync(int? userId, CancellationToken ct = default);
    Task<SetupStateModel> GetStateAsync(CancellationToken ct = default);
    Task<SetupStateModel> SaveStepAsync(int step, SaveSetupStepRequest request, CancellationToken ct = default);
    Task<SetupCategoriesModel> GetCategoriesAsync(CancellationToken ct = default);
    Task<SetupCategoryResult> ResetAsync(string category, SetupResetRequest request, int? userId, CancellationToken ct = default);
    Task<List<SeededRecordModel>> GetSeededAsync(string category, CancellationToken ct = default);
}

internal sealed class TenantSetupService : ITenantSetupService
{
    public const int MaxStep = 20;

    private static readonly string[] AppliedActions = [SetupLogActions.Seed, SetupLogActions.Merge, SetupLogActions.Overwrite, SetupLogActions.FullReset];

    private readonly OnboardingDbContext _db;
    private readonly ISetupDefaultsService _defaults;
    private readonly SetupPayloadValidator _validator;
    private readonly ITenantContext _tenant;
    private readonly ISetupUserInviter _inviter;
    private readonly IModuleGate? _gate;
    private readonly IOnboardingProgress? _progress;
    private readonly IOrganizationNameLookup? _names;
    private readonly ILogger<TenantSetupService>? _logger;

    public TenantSetupService(OnboardingDbContext db, ISetupDefaultsService defaults, SetupPayloadValidator validator, ITenantContext tenant,
        ISetupUserInviter inviter, IModuleGate? gate = null, IOnboardingProgress? progress = null, IOrganizationNameLookup? names = null,
        ILogger<TenantSetupService>? logger = null)
    {
        _db = db;
        _defaults = defaults;
        _validator = validator;
        _tenant = tenant;
        _inviter = inviter;
        _gate = gate;
        _progress = progress;
        _names = names;
        _logger = logger;
    }

    private Guid OrgId => _tenant.OrganizationId != Guid.Empty
        ? _tenant.OrganizationId
        : throw new BadRequestException("Sign in to an organization to run its setup.");

    // ── status / state (BR-50-001, BR-50-002) ──

    public async Task<SetupStatusModel?> GetStatusAsync(bool canRunWizard, CancellationToken ct = default)
    {
        if (_tenant.OrganizationId == Guid.Empty) return null;
        var setup = await GetOrCreateAsync(ct);
        return new SetupStatusModel
        {
            SetupStatus = setup.SetupStatus, SetupStep = setup.SetupStep, CountryCode = setup.CountryCode, IndustryType = setup.IndustryType,
            CompletedAt = setup.CompletedAt, CanRunWizard = canRunWizard
        };
    }

    public async Task<SetupStateModel> GetStateAsync(CancellationToken ct = default) => await ToStateAsync(await GetOrCreateAsync(ct), ct);

    public async Task<SetupStateModel> SaveStepAsync(int step, SaveSetupStepRequest request, CancellationToken ct = default)
    {
        if (step < 1 || step > MaxStep) throw new BadRequestException($"The step must be between 1 and {MaxStep}.");
        var setup = await GetOrCreateAsync(ct);

        var steps = ReadObject(setup.StepDataJson);
        steps[step.ToString(CultureInfo.InvariantCulture)] = request.Data.ValueKind == JsonValueKind.Undefined ? Empty() : request.Data.Clone();
        setup.StepDataJson = JsonSerializer.Serialize(steps);
        setup.SetupStep = step;
        if (!string.IsNullOrWhiteSpace(request.CountryCode)) setup.CountryCode = SetupKey.Country(request.CountryCode);
        if (!string.IsNullOrWhiteSpace(request.IndustryType)) setup.IndustryType = SetupKey.Industry(request.IndustryType);
        if (setup.SetupStatus == SetupStatuses.NotStarted) setup.SetupStatus = SetupStatuses.InProgress;
        await SaveAsync(ct);
        return await ToStateAsync(setup, ct);
    }

    // ── step 1 lists (tasks 041 / 042) ──

    public async Task<List<SetupOptionModel>> GetCountriesAsync(CancellationToken ct = default)
    {
        var counts = await _db.SetupDefaults.AsNoTracking().Where(d => d.IsActive)
            .GroupBy(d => d.CountryCode).Select(g => new { Code = g.Key, Count = g.Count() }).ToListAsync(ct);
        var list = counts.Where(c => !c.Code.Equals(SetupCodes.Global, StringComparison.OrdinalIgnoreCase))
            .Select(c => new SetupOptionModel(c.Code, CountryName(c.Code), c.Count))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        list.Add(new SetupOptionModel(SetupCodes.Global, "Other / Global",
            counts.FirstOrDefault(c => c.Code.Equals(SetupCodes.Global, StringComparison.OrdinalIgnoreCase))?.Count ?? 0));
        return list;
    }

    public async Task<List<SetupOptionModel>> GetIndustriesAsync(string? country, CancellationToken ct = default)
    {
        var c = SetupKey.Country(country);
        var industries = await _db.SetupDefaults.AsNoTracking()
            .Where(d => d.IsActive && (d.CountryCode == c || d.CountryCode == SetupCodes.Global))
            .Select(d => d.IndustryType).Distinct().ToListAsync(ct);
        var list = industries.Where(i => !i.Equals(SetupCodes.Global, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(i => new SetupOptionModel(i, i.Replace('_', ' ')))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        list.Add(new SetupOptionModel(SetupCodes.Global, "General (any industry)"));
        return list;
    }

    // ── preview (task 043) ──

    public async Task<List<SetupPreviewItem>> PreviewAsync(SetupPreviewRequest request, CancellationToken ct = default)
    {
        var org = OrgId;
        var resolved = await _defaults.ResolveAllAsync(request.CountryCode, request.IndustryType, ct);
        var wanted = request.Categories is { Count: > 0 } ? new HashSet<string>(request.Categories.Select(SetupKey.Category), StringComparer.OrdinalIgnoreCase) : null;
        var overrides = Normalize(request.PayloadOverrides);
        var seeded = await SeededByCategoryAsync(org, ct);
        var items = new List<SetupPreviewItem>();

        foreach (var category in _validator.KnownCategories)
        {
            if (wanted is not null && !wanted.Contains(category)) continue;
            var hit = resolved.GetValueOrDefault(category);
            var seeder = _validator.Seeders.GetValueOrDefault(category);
            var moduleEnabled = await ModuleEnabledAsync(org, seeder, ct);
            JsonElement? payload = overrides.TryGetValue(category, out var o) ? o : hit is null ? null : SetupDefaultsService.Parse(hit.Row.DataPayload);

            var status = SetupCategoryOutcomes.Ready;
            var errors = new List<string>();
            SetupPreview? preview = null;
            if (seeder is null) status = SetupCategoryOutcomes.NoSeeder;
            else if (payload is null) status = SetupCategoryOutcomes.NoDefaults;
            else if (!moduleEnabled) status = SetupCategoryOutcomes.ModuleDisabled;
            else if (overrides.ContainsKey(category) && (errors = _validator.Validate(category, payload.Value)).Count > 0) status = SetupCategoryOutcomes.Invalid;
            else
            {
                try
                {
                    preview = await seeder.PreviewAsync(payload.Value, Context(request.CountryCode, request.IndustryType, hit?.Row.Version ?? 0, null, null,
                        seeded.GetValueOrDefault(category) ?? [], overrides.ContainsKey(category), DefaultOf(hit)), ct);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Setup preview of {Category} failed.", category);
                    errors.Add($"The preview could not be calculated: {ex.Message}");
                }
            }

            items.Add(new SetupPreviewItem
            {
                Category = category, DisplayName = hit?.Row.DisplayName, Status = status, SetupDefaultUuid = hit?.Row.Uuid, Version = hit?.Row.Version,
                ResolvedFrom = hit is null ? null : new ResolvedFromModel { Country = hit.Country, Industry = hit.Industry },
                Payload = payload, Verified = payload is { } p ? JsonCanonical.Verified(p) : null, Preview = preview,
                RequiresModule = seeder?.RequiresModule, ModuleEnabled = moduleEnabled, HasSeeder = seeder is not null, Errors = errors
            });
        }
        return items;
    }

    // ── apply / skip (tasks 045, 061–064, BR-50-003/004) ──

    public Task<SetupApplyResponse> ApplyAsync(SetupApplyRequest request, int? userId, CancellationToken ct = default) =>
        RunAsync(request, userId, skipping: false, ct);

    public async Task<SetupApplyResponse> SkipAsync(int? userId, CancellationToken ct = default)
    {
        var setup = await GetOrCreateAsync(ct);
        if (setup.SetupStatus == SetupStatuses.Completed)
            throw new BadRequestException("Setup is already complete; use the Default Data page to add or reset categories.");
        return await RunAsync(new SetupApplyRequest { CountryCode = SetupCodes.Global, IndustryType = SetupCodes.Global }, userId, skipping: true, ct);
    }

    private async Task<SetupApplyResponse> RunAsync(SetupApplyRequest request, int? userId, bool skipping, CancellationToken ct)
    {
        var org = OrgId;
        var setup = await GetOrCreateAsync(ct);
        var country = SetupKey.Country(request.CountryCode ?? setup.CountryCode);
        var industry = SetupKey.Industry(request.IndustryType ?? setup.IndustryType);

        if (!skipping)
        {
            setup.CountryCode = country;
            setup.IndustryType = industry;
        }
        else
        {
            setup.CountryCode ??= country;
            setup.IndustryType ??= industry;
        }
        if (setup.SetupStatus == SetupStatuses.NotStarted) setup.SetupStatus = SetupStatuses.InProgress;
        await SaveAsync(ct);

        var resolved = await _defaults.ResolveAllAsync(country, industry, ct);
        var overrides = Normalize(request.PayloadOverrides);
        var inputs = Normalize(request.WizardInputs);
        var skip = new HashSet<string>((request.SkipCategories ?? []).Select(SetupKey.Category), StringComparer.OrdinalIgnoreCase);
        var snapshot = ReadObject(setup.DefaultsSnapshotJson);
        var results = new List<SetupCategoryResult>();
        var stop = false;

        foreach (var category in _validator.KnownCategories)
        {
            var result = new SetupCategoryResult { Category = category };
            results.Add(result);
            if (stop) continue; // NotRun

            var hit = resolved.GetValueOrDefault(category);
            result.Version = hit?.Row.Version;
            var seeder = _validator.Seeders.GetValueOrDefault(category);

            if (skip.Contains(category))
            {
                result.Status = SetupCategoryOutcomes.Skipped;
                result.Action = SetupLogActions.Skipped;
                Log(org, country, industry, category, hit, 0, SetupLogActions.Skipped, "Skipped in the wizard.", userId);
                await SaveAsync(ct);
                continue;
            }
            if (seeder is null) { result.Status = SetupCategoryOutcomes.NoSeeder; continue; }
            if (!await ModuleEnabledAsync(org, seeder, ct))
            {
                result.Status = SetupCategoryOutcomes.ModuleDisabled;
                result.Action = SetupLogActions.Skipped;
                result.Message = $"{seeder.RequiresModule} is not enabled for the organization.";
                Log(org, country, industry, category, hit, 0, SetupLogActions.Skipped, result.Message, userId);
                await SaveAsync(ct);
                continue;
            }

            var overridden = overrides.TryGetValue(category, out var o);
            JsonElement? payload = overridden ? o : hit is null ? null : SetupDefaultsService.Parse(hit.Row.DataPayload);
            if (payload is null) { result.Status = SetupCategoryOutcomes.NoDefaults; continue; }

            if (overridden && _validator.Validate(category, payload.Value) is { Count: > 0 } errors)
            {
                result.Status = SetupCategoryOutcomes.Invalid;
                result.Action = SetupLogActions.Failed;
                result.Warnings = errors;
                result.Message = "The edited payload is not valid; nothing was applied for this category.";
                Log(org, country, industry, category, hit, 0, SetupLogActions.Failed, $"{result.Message} {string.Join(" ", errors)}", userId);
                await SaveAsync(ct);
                stop = true;
                continue;
            }

            var already = await SeededAsync(org, category, ct);
            var context = Context(country, industry, hit?.Row.Version ?? 0, userId, inputs.TryGetValue(category, out var input) ? input : null, already,
                overridden, DefaultOf(hit));
            SetupApplyResult applied;
            try
            {
                applied = await seeder.ApplyAsync(payload.Value, context, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Setup apply of {Category} failed for organization {Org}.", category, org);
                _db.ChangeTracker.Clear();
                result.Status = SetupCategoryOutcomes.Failed;
                result.Action = SetupLogActions.Failed;
                result.Message = ex.Message;
                Log(org, country, industry, category, hit, 0, SetupLogActions.Failed, ex.Message, userId);
                await SaveAsync(ct);
                stop = true;
                continue;
            }

            await RecordProvenanceAsync(org, category, hit?.Row.Version ?? 0, applied, replaceFingerprints: false, countReset: false, ct);
            Log(org, country, industry, category, hit, applied.Created, SetupLogActions.Seed, Summary(applied), userId);
            snapshot[category] = Snapshot(hit, payload.Value, overridden);
            result.Status = SetupCategoryOutcomes.Applied;
            result.Action = SetupLogActions.Seed;
            result.Created = applied.Created;
            result.Skipped = applied.Skipped;
            result.Removed = applied.Removed;
            result.Warnings = applied.Warnings;
            await SaveAsync(ct);
        }

        // Reload: a seeder failure clears the change tracker.
        setup = await GetOrCreateAsync(ct);
        setup.DefaultsSnapshotJson = JsonSerializer.Serialize(snapshot);
        var allOk = results.All(r => r.Ok);
        var invites = new List<SetupInviteResult>();

        if (allOk)
        {
            invites = await InviteAsync(org, request.Invites, userId, ct);
            if (skipping) setup.SetupStatus = SetupStatuses.Skipped;
            else if (setup.SetupStatus != SetupStatuses.Completed)
            {
                setup.SetupStatus = SetupStatuses.Completed;
                setup.CompletedAt = DateTime.UtcNow;
                setup.CompletedBy = userId;
            }
        }
        // D-7 / BR-50-003 — a skip never sends the admin back to the wizard, even when a category needs attention.
        else if (skipping) setup.SetupStatus = SetupStatuses.Skipped;
        await SaveAsync(ct);

        if (setup.SetupStatus is SetupStatuses.Completed or SetupStatuses.Skipped && _progress is not null)
        {
            try { await _progress.MarkCompletedAsync(OnboardingStepCodes.CompleteSetup, ct); }
            catch (Exception ex) { _logger?.LogWarning(ex, "The COMPLETE_SETUP checklist step could not be ticked."); }
        }

        return new SetupApplyResponse { SetupStatus = setup.SetupStatus, Completed = allOk, Results = results, Invites = invites };
    }

    private async Task<List<SetupInviteResult>> InviteAsync(Guid org, List<SetupInviteInput>? invites, int? userId, CancellationToken ct)
    {
        var results = new List<SetupInviteResult>();
        if (invites is not { Count: > 0 }) return results;

        var orgName = _names is null ? null : await _names.GetNameAsync(org, ct);
        var roles = (await SeededAsync(org, SetupCategories.Roles, ct)).ToDictionary(r => r.NaturalKey, r => r.EntityRef, StringComparer.OrdinalIgnoreCase);

        var row = 0;
        foreach (var invite in invites)
        {
            row++;
            var email = invite.Email?.Trim() ?? string.Empty;
            if (email.Length == 0)
            {
                results.Add(new SetupInviteResult { Email = email, Outcome = SetupInviteOutcomes.Invalid, Message = "An e-mail address is required." });
                continue;
            }
            var roleRef = invite.RoleRef?.Trim() ?? string.Empty;
            int roleId;
            if (!int.TryParse(roleRef, NumberStyles.None, CultureInfo.InvariantCulture, out roleId)
                && !(roles.TryGetValue(roleRef, out var entityRef) && int.TryParse(entityRef, NumberStyles.None, CultureInfo.InvariantCulture, out roleId)))
            {
                results.Add(new SetupInviteResult { Email = email, Outcome = SetupInviteOutcomes.Failed, Message = $"Role '{roleRef}' was not found." });
                continue;
            }
            try
            {
                results.Add(await _inviter.InviteAsync(org, orgName ?? string.Empty, email, Blank(invite.FirstName), Blank(invite.LastName), roleId, userId ?? 0, ct));
            }
            catch (Exception ex)
            {
                // REV50-13: the address is personal data, so the log names the row and the organization instead.
                _logger?.LogError(ex, "Setup invite row {Row} of organization {Org} failed.", row, org);
                results.Add(new SetupInviteResult { Email = email, Outcome = SetupInviteOutcomes.Failed, Message = ex.Message });
            }
        }
        return results;
    }

    // ── dashboard + reset (tasks 073–078) ──

    public async Task<SetupCategoriesModel> GetCategoriesAsync(CancellationToken ct = default)
    {
        var org = OrgId;
        var setup = await GetOrCreateAsync(ct);
        var resolved = await _defaults.ResolveAllAsync(setup.CountryCode, setup.IndustryType, ct);
        var seeded = await SeededByCategoryAsync(org, ct);
        var logs = await _db.TenantSetupLogs.AsNoTracking().Where(l => l.OrganizationId == org)
            .OrderByDescending(l => l.AppliedAt).ThenByDescending(l => l.Id).ToListAsync(ct);
        var list = new List<SetupCategoryStatusModel>();

        foreach (var category in _validator.KnownCategories)
        {
            var seeder = _validator.Seeders.GetValueOrDefault(category);
            var already = seeded.GetValueOrDefault(category) ?? [];
            var hit = resolved.GetValueOrDefault(category);
            var last = logs.FirstOrDefault(l => l.DataCategory == category);
            var lastApplied = logs.FirstOrDefault(l => l.DataCategory == category && AppliedActions.Contains(l.Action));

            SetupCategoryStatus? status = null;
            if (seeder is not null)
            {
                try { status = await seeder.GetStatusAsync(Context(setup.CountryCode, setup.IndustryType, hit?.Row.Version ?? 0, null, null, already), ct); }
                catch (Exception ex) { _logger?.LogWarning(ex, "Setup status of {Category} could not be read.", category); }
            }

            list.Add(new SetupCategoryStatusModel
            {
                Category = category, DisplayName = hit?.Row.DisplayName, RequiresModule = seeder?.RequiresModule,
                ModuleEnabled = await ModuleEnabledAsync(org, seeder, ct), HasSeeder = seeder is not null,
                SeededCount = status?.SeededCount ?? already.Count, CurrentCount = status?.CurrentCount ?? 0,
                ModifiedCount = status?.ModifiedKeys.Count ?? 0, MissingCount = status?.MissingKeys.Count ?? 0,
                ModifiedKeys = status?.ModifiedKeys ?? [], MissingKeys = status?.MissingKeys ?? [],
                IsModified = status is { ModifiedKeys.Count: > 0 } or { MissingKeys.Count: > 0 },
                LastAppliedAt = last?.AppliedAt, LastAction = last?.Action,
                AppliedVersion = lastApplied?.DefaultsVersion, LatestVersion = hit?.Row.Version,
                UpdateAvailable = lastApplied is not null && hit is not null
                                  && (hit.Row.Id != lastApplied.SetupDefaultId || hit.Row.Version > lastApplied.DefaultsVersion)
            });
        }

        return new SetupCategoriesModel
        {
            CountryCode = setup.CountryCode, IndustryType = setup.IndustryType, SetupStatus = setup.SetupStatus,
            CompletedAt = setup.CompletedAt, Categories = list
        };
    }

    public async Task<SetupCategoryResult> ResetAsync(string category, SetupResetRequest request, int? userId, CancellationToken ct = default)
    {
        var org = OrgId;
        var key = SetupKey.Category(category);
        var seeder = _validator.Seeders.GetValueOrDefault(key) ?? throw new NotFoundException($"Category '{category}' cannot be reset (no seeder).");
        if (request.Mode != SetupResetMode.Merge && !request.Confirm)
            throw new BadRequestException($"{request.Mode} replaces seeded records — confirm it to go ahead.");
        if (!await ModuleEnabledAsync(org, seeder, ct))
            throw new BadRequestException($"{seeder.RequiresModule} is not enabled for the organization.");

        var setup = await GetOrCreateAsync(ct);
        var country = SetupKey.Country(setup.CountryCode);
        var industry = SetupKey.Industry(setup.IndustryType);
        var latest = await _defaults.ResolveAsync(country, industry, key, ct);

        // BR-50-015 — by default reset to what the organization applied (its snapshot), not to a later Super Admin edit.
        var useLatest = string.Equals(request.Source, SetupResetSources.Latest, StringComparison.OrdinalIgnoreCase);
        var snapshot = ReadObject(setup.DefaultsSnapshotJson);
        JsonElement payload;
        int version;
        int? defaultId;
        var isOverride = false;
        if (!useLatest && snapshot.TryGetValue(key, out var snap) && snap.ValueKind == JsonValueKind.Object && snap.TryGetProperty("payload", out var p))
        {
            payload = p.Clone();
            version = snap.TryGetProperty("version", out var v) && v.TryGetInt32(out var vi) ? vi : 0;
            defaultId = snap.TryGetProperty("setupDefaultId", out var d) && d.TryGetInt32(out var di) ? di : null;
            // REV50-8 — an applied admin edit stays an override on reset (no global rows from it).
            isOverride = snap.TryGetProperty("overridden", out var ov) && ov.ValueKind == JsonValueKind.True;
        }
        else if (latest is not null)
        {
            payload = SetupDefaultsService.Parse(latest.Row.DataPayload);
            version = latest.Row.Version;
            defaultId = latest.Row.Id;
        }
        else throw new NotFoundException($"No defaults exist for {key} ({country} / {industry}).");

        var already = await SeededAsync(org, key, ct);
        var context = Context(country, industry, version, userId, null, already, isOverride, DefaultOf(latest));
        var action = request.Mode switch
        {
            SetupResetMode.Overwrite => SetupLogActions.Overwrite,
            SetupResetMode.Full      => SetupLogActions.FullReset,
            _                        => SetupLogActions.Merge
        };
        var result = new SetupCategoryResult { Category = key, Version = version };

        if (request.Mode == SetupResetMode.Full)
        {
            var check = await seeder.CheckReferencesAsync(context, ct);
            if (check.ReferencedCount > 0) throw new SetupResetBlockedException(key, check);
        }

        SetupApplyResult applied;
        try
        {
            applied = await seeder.ResetAsync(payload, request.Mode, context, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Setup reset ({Mode}) of {Category} failed for organization {Org}.", request.Mode, key, org);
            _db.ChangeTracker.Clear();
            LogRaw(org, country, industry, key, defaultId, version, 0, SetupLogActions.Failed, $"{request.Mode} reset failed: {ex.Message}", userId);
            await SaveAsync(ct);
            result.Status = SetupCategoryOutcomes.Failed;
            result.Action = SetupLogActions.Failed;
            result.Message = ex.Message;
            return result;
        }

        await RecordProvenanceAsync(org, key, version, applied, replaceFingerprints: request.Mode != SetupResetMode.Merge, countReset: request.Mode != SetupResetMode.Merge, ct);
        LogRaw(org, country, industry, key, defaultId, version, applied.Created, action, Summary(applied), userId);
        await SaveAsync(ct);

        result.Status = SetupCategoryOutcomes.Applied;
        result.Action = action;
        result.Created = applied.Created;
        result.Skipped = applied.Skipped;
        result.Removed = applied.Removed;
        result.Warnings = applied.Warnings;
        return result;
    }

    public async Task<List<SeededRecordModel>> GetSeededAsync(string category, CancellationToken ct = default)
    {
        var org = OrgId;
        var key = SetupKey.Category(category);
        if (key.Length == 0) throw new BadRequestException("A category is required.");
        return await _db.SeededRecords.AsNoTracking().Where(s => s.OrganizationId == org && s.Category == key)
            .OrderBy(s => s.NaturalKey)
            .Select(s => new SeededRecordModel
            {
                NaturalKey = s.NaturalKey, EntityRef = s.EntityRef, Version = s.Version, Reused = s.Reused, SeededAt = s.SeededAt, ResetCount = s.ResetCount
            }).ToListAsync(ct);
    }

    // ── provenance (D-5) ──

    /// <summary>
    /// Upserts the seeder's refs and drops its removed keys. Seed / Merge keep an existing row's fingerprint (so an admin's
    /// edit still reads as "modified"), unless the record was re-created (its EntityRef changed); Overwrite / Full replace it.
    /// </summary>
    internal async Task RecordProvenanceAsync(Guid org, string category, int version, SetupApplyResult applied, bool replaceFingerprints, bool countReset,
        CancellationToken ct)
    {
        var rows = await _db.SeededRecords.Where(s => s.OrganizationId == org && s.Category == category).ToListAsync(ct);
        var byKey = rows.ToDictionary(r => r.NaturalKey, StringComparer.OrdinalIgnoreCase);

        foreach (var removed in applied.RemovedKeys.Distinct(StringComparer.OrdinalIgnoreCase))
            if (byKey.Remove(removed, out var row))
                _db.SeededRecords.Remove(row);

        foreach (var seeded in applied.Seeded.GroupBy(s => s.NaturalKey, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
        {
            if (byKey.TryGetValue(seeded.NaturalKey, out var row))
            {
                var recreated = !string.Equals(row.EntityRef, seeded.EntityRef, StringComparison.OrdinalIgnoreCase);
                if (!replaceFingerprints && !recreated) continue;
                row.EntityRef = Cap(seeded.EntityRef, 100);
                row.Fingerprint = Cap(seeded.Fingerprint, 100);
                row.Reused = seeded.Reused;
                row.Version = version;
                row.SeededAt = DateTime.UtcNow;
                if (countReset) row.ResetCount++;
                continue;
            }
            var added = new SeededRecord
            {
                OrganizationId = org, Category = category, NaturalKey = Cap(seeded.NaturalKey, 200), EntityRef = Cap(seeded.EntityRef, 100),
                Version = version, Fingerprint = Cap(seeded.Fingerprint, 100), Reused = seeded.Reused
            };
            _db.SeededRecords.Add(added);
            byKey[seeded.NaturalKey] = added;
        }
    }

    // ── helpers ──

    /// <summary>The org's setup row; created NotStarted if the provisioning handler missed it (the migration backfilled older orgs).</summary>
    private async Task<OrganizationSetup> GetOrCreateAsync(CancellationToken ct)
    {
        var org = OrgId;
        var setup = await _db.OrganizationSetups.FirstOrDefaultAsync(s => s.OrganizationId == org, ct);
        if (setup is not null) return setup;
        setup = new OrganizationSetup { OrganizationId = org, SetupStatus = SetupStatuses.NotStarted };
        _db.OrganizationSetups.Add(setup);
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            // A concurrent request created it.
            _db.ChangeTracker.Clear();
            setup = await _db.OrganizationSetups.FirstAsync(s => s.OrganizationId == org, ct);
        }
        return setup;
    }

    private async Task<SetupStateModel> ToStateAsync(OrganizationSetup setup, CancellationToken ct)
    {
        var lastAppliedAt = await _db.TenantSetupLogs.AsNoTracking().Where(l => l.OrganizationId == setup.OrganizationId && AppliedActions.Contains(l.Action))
            .OrderByDescending(l => l.AppliedAt).Select(l => (DateTime?)l.AppliedAt).FirstOrDefaultAsync(ct);
        return new SetupStateModel
        {
            SetupStatus = setup.SetupStatus, SetupStep = setup.SetupStep, CountryCode = setup.CountryCode, IndustryType = setup.IndustryType,
            StepData = JsonSerializer.SerializeToElement(ReadObject(setup.StepDataJson)), CompletedAt = setup.CompletedAt,
            CompletedBy = setup.CompletedBy, Note = setup.Note, LastAppliedAt = lastAppliedAt
        };
    }

    private async Task<bool> ModuleEnabledAsync(Guid org, ISetupCategorySeeder? seeder, CancellationToken ct) =>
        seeder?.RequiresModule is not { Length: > 0 } module || _gate is null || await _gate.IsEnabledAsync(org, module, ct);

    private async Task<IReadOnlyList<SeededRecordRef>> SeededAsync(Guid org, string category, CancellationToken ct) =>
        (await _db.SeededRecords.AsNoTracking().Where(s => s.OrganizationId == org && s.Category == category).ToListAsync(ct))
            .Select(s => new SeededRecordRef(s.NaturalKey, s.EntityRef, s.Fingerprint, s.Reused)).ToList();

    private async Task<Dictionary<string, IReadOnlyList<SeededRecordRef>>> SeededByCategoryAsync(Guid org, CancellationToken ct) =>
        (await _db.SeededRecords.AsNoTracking().Where(s => s.OrganizationId == org).ToListAsync(ct))
            .GroupBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SeededRecordRef>)g.Select(s => new SeededRecordRef(s.NaturalKey, s.EntityRef, s.Fingerprint, s.Reused)).ToList(),
                StringComparer.OrdinalIgnoreCase);

    /// <param name="defaultPayload">The resolved Super Admin default; <paramref name="isOverride"/> = the payload is the admin's edit (REV50-8).</param>
    private static SetupSeedContext Context(string? country, string? industry, int version, int? userId, JsonElement? input, IReadOnlyList<SeededRecordRef> already,
        bool isOverride = false, JsonElement? defaultPayload = null) => new()
    {
        CountryCode = SetupKey.Country(country), IndustryType = SetupKey.Industry(industry), Version = version, UserId = userId,
        WizardInput = input, AlreadySeeded = already, PayloadIsOverride = isOverride, DefaultPayload = defaultPayload
    };

    /// <summary>The resolved Super Admin default payload of a category (REV50-8), whatever the admin overrode.</summary>
    private static JsonElement? DefaultOf(ResolvedDefault? hit) =>
        hit is null ? null : SetupDefaultsService.Parse(hit.Row.DataPayload);

    private void Log(Guid org, string country, string industry, string category, ResolvedDefault? hit, int records, string action, string? message, int? userId) =>
        LogRaw(org, country, industry, category, hit?.Row.Id, hit?.Row.Version ?? 0, records, action, message, userId, hit?.Country, hit?.Industry);

    private void LogRaw(Guid org, string country, string industry, string category, int? defaultId, int version, int records, string action, string? message,
        int? userId, string? resolvedCountry = null, string? resolvedIndustry = null) =>
        _db.TenantSetupLogs.Add(new TenantSetupLog
        {
            OrganizationId = org, CountryCode = Cap(country, 10), IndustryType = Cap(industry, 50), DataCategory = category,
            SetupDefaultId = defaultId, ResolvedCountryCode = resolvedCountry, ResolvedIndustryType = resolvedIndustry,
            DefaultsVersion = version, RecordsSeeded = records, Action = action, Message = message is null ? null : Cap(message, 2000), AppliedBy = userId
        });

    private static JsonElement Snapshot(ResolvedDefault? hit, JsonElement payload, bool overridden) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["setupDefaultId"] = hit?.Row.Id, ["version"] = hit?.Row.Version ?? 0, ["overridden"] = overridden, ["payload"] = payload
        });

    private static string Summary(SetupApplyResult r) =>
        $"{r.Created} created, {r.Skipped} already present, {r.Removed} removed" + (r.Warnings.Count > 0 ? $"; {string.Join(" ", r.Warnings)}" : string.Empty);

    private static Dictionary<string, JsonElement> Normalize(Dictionary<string, JsonElement>? map) =>
        (map ?? new()).Where(kv => kv.Value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            .ToDictionary(kv => SetupKey.Category(kv.Key), kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, JsonElement> ReadObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new Dictionary<string, object>());

    private static string CountryName(string code)
    {
        try { return new RegionInfo(code).EnglishName; }
        catch (ArgumentException) { return code; }
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max];

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw new ConflictException("The setup was changed by someone else — reload and try again."); }
    }
}
