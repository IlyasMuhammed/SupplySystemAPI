using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Accounting.Onboarding;

/// <summary>
/// A50 D-12 / FSD §5.3 / D-52 — FISCAL: when the organization has no fiscal year, creates the one containing today (day 1 of
/// <c>startMonth</c>, twelve months) through <see cref="IFiscalYearService.CreateAsync"/> (A42 monthly periods + period 13,
/// reporting currency = the sale base). Any existing year → nothing is created and a warning says so. A42 is monthly-only, so
/// <c>periodType</c> "Quarterly" still gets monthly periods, with a warning. Natural key = the start date (yyyy-MM-dd).
/// <c>yearNamePattern</c>: {YYYY} = start year, {YY} = end year's last two digits, {YYYY2} = end year.
/// Reset: Merge / Overwrite = apply (a fiscal year is never changed by a reset; edit it in GL setup). Full = the seeded year
/// removed and re-created, refused when it is referenced (a journal entry dated in it or linked to it, a closed period, or
/// posted opening balances).
/// </summary>
internal sealed class FiscalSetupSeeder : ISetupCategorySeeder
{
    private readonly AccountingDbContext _db;
    private readonly IFiscalYearService _years;
    private readonly ISetupStatusService _setup;
    private readonly TimeProvider _clock;

    public FiscalSetupSeeder(AccountingDbContext db, IFiscalYearService years, ISetupStatusService setup, TimeProvider? clock = null)
    {
        _db = db;
        _years = years;
        _setup = setup;
        _clock = clock ?? TimeProvider.System;
    }

    public string Category => SetupCategories.Fiscal;
    public string? RequiresModule => ModuleCodes.GeneralLedger;

    private Guid Org => _db.TenantContext.OrganizationId is var o && o != Guid.Empty
        ? o : throw new InvalidOperationException("FISCAL seeding needs an ambient organization.");

    internal sealed record Shape(int StartMonth, bool Quarterly, string? NamePattern);

    internal static string Key(DateOnly start) => start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static string Fingerprint(FiscalYear y) => SetupFingerprint.Of(Key(y.StartDate), Key(y.EndDate), y.FiscalYearName);

    public IReadOnlyList<string> ValidatePayload(JsonElement payload) => Parse(payload, out _);

    internal static List<string> Parse(JsonElement payload, out Shape? shape)
    {
        shape = null;
        if (payload.ValueKind != JsonValueKind.Object) return ["The FISCAL payload must be an object."];
        var errors = new List<string>();
        int? month = payload.TryGetProperty("startMonth", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt32(out var mi) ? mi : null;
        if (month is not (>= 1 and <= 12)) errors.Add("startMonth must be 1-12.");
        var type = payload.TryGetProperty("periodType", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()?.Trim() : null;
        if (type is not ("Monthly" or "Quarterly")) errors.Add("periodType must be Monthly (Quarterly is accepted and gets monthly periods).");
        var pattern = payload.TryGetProperty("yearNamePattern", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()?.Trim() : null;
        if (pattern is { Length: > 50 }) errors.Add("yearNamePattern can be at most 50 characters.");
        if (errors.Count == 0) shape = new Shape(month!.Value, type == "Quarterly", string.IsNullOrEmpty(pattern) ? null : pattern);
        return errors;
    }

    internal static (DateOnly Start, DateOnly End) YearContaining(DateOnly today, int startMonth)
    {
        var start = new DateOnly(today.Month >= startMonth ? today.Year : today.Year - 1, startMonth, 1);
        return (start, start.AddYears(1).AddDays(-1));
    }

    internal static string? Name(string? pattern, DateOnly start, DateOnly end) => pattern?
        .Replace("{YYYY2}", end.Year.ToString(CultureInfo.InvariantCulture))
        .Replace("{YYYY}", start.Year.ToString(CultureInfo.InvariantCulture))
        .Replace("{YY}", (end.Year % 100).ToString("00", CultureInfo.InvariantCulture));

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    private Task<List<FiscalYear>> YearsAsync(Guid org, CancellationToken ct) =>
        _db.FiscalYears.IgnoreQueryFilters().AsNoTracking().Where(y => y.OrganizationId == org).OrderBy(y => y.StartDate).ToListAsync(ct);

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var errors = Parse(payload, out var shape);
        var years = await YearsAsync(Org, ct);
        var warnings = new List<string>(errors);
        if (years.Count > 0) warnings.Add($"A fiscal year already exists ({years[0].FiscalYearName}): no fiscal year is created.");
        if (shape?.Quarterly == true) warnings.Add(QuarterlyWarning);
        return new SetupPreview { Category = Category, InPayload = shape is null ? 0 : 1, AlreadyPresent = years.Count > 0 ? 1 : 0, Warnings = warnings };
    }

    internal const string QuarterlyWarning = "The general ledger uses monthly periods: the fiscal year gets 12 monthly periods (report quarterly by grouping them).";

    public async Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var errors = Parse(payload, out var shape);
        if (errors.Count > 0) return new SetupApplyResult { Category = Category, Warnings = errors };
        var org = Org;
        var warnings = new List<string>();
        if (shape!.Quarterly) warnings.Add(QuarterlyWarning);

        var years = await YearsAsync(org, ct);
        if (years.Count > 0)
        {
            var (start, _) = YearContaining(Today, shape.StartMonth);
            var same = years.FirstOrDefault(y => y.StartDate == start);
            var seeded = new List<SeededRecordRef>();
            if (same is not null && context.AlreadySeeded.All(s => s.NaturalKey != Key(same.StartDate)))
                seeded.Add(new SeededRecordRef(Key(same.StartDate), same.Uuid.ToString(), Fingerprint(same), Reused: true));
            if (same is null) warnings.Add($"A fiscal year already exists ({years[0].FiscalYearName}): no fiscal year was created.");
            return new SetupApplyResult { Category = Category, Skipped = 1, Seeded = seeded, Warnings = warnings };
        }
        return await CreateAsync(org, shape, context, warnings, [], 0, ct);
    }

    private async Task<SetupApplyResult> CreateAsync(Guid org, Shape shape, SetupSeedContext context, List<string> warnings,
        List<string> removedKeys, int removed, CancellationToken ct)
    {
        var (start, end) = YearContaining(Today, shape.StartMonth);
        var caller = new AccountingCaller(org, context.UserId ?? 0, AccountingCaller.AllGlCodes, "Setup wizard");
        try
        {
            var model = await _years.CreateAsync(caller, new CreateFiscalYearRequest
            {
                StartDate = start, EndDate = end, FiscalYearName = Name(shape.NamePattern, start, end),
            }, ct);
            return new SetupApplyResult
            {
                Category = Category, Created = 1, Warnings = warnings, Removed = removed, RemovedKeys = removedKeys,
                Seeded = [new SeededRecordRef(Key(model.StartDate), model.Uuid.ToString(),
                    SetupFingerprint.Of(Key(model.StartDate), Key(model.EndDate), model.FiscalYearName))],
            };
        }
        catch (Exception ex) when (ex is BadRequestException or ConflictException)
        {
            warnings.Add($"The fiscal year was not created: {ex.Message}");
            return new SetupApplyResult { Category = Category, Warnings = warnings, Removed = removed, RemovedKeys = removedKeys };
        }
    }

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var years = await YearsAsync(Org, ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            var y = years.FirstOrDefault(x => x.Uuid.ToString() == s.EntityRef);
            if (y is null) missing.Add(s.NaturalKey);
            else if (Fingerprint(y) != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus { Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = years.Count, ModifiedKeys = modified, MissingKeys = missing };
    }

    private async Task<bool> IsReferencedAsync(FiscalYear y, CancellationToken ct)
    {
        var org = y.OrganizationId;
        if (await _db.JournalEntries.IgnoreQueryFilters().AnyAsync(j => j.OrganizationId == org
                && (j.FiscalYearId == y.Id || (j.EntryDate >= y.StartDate && j.EntryDate <= y.EndDate)), ct)) return true;
        if (await _db.FiscalPeriods.IgnoreQueryFilters().AnyAsync(p => p.FiscalYearId == y.Id && p.Status != PeriodStatuses.Open, ct)) return true;
        return await _db.CoaSetupStatuses.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org && s.OpeningBalancesPostedAt != null, ct);
    }

    /// <summary>
    /// REV50-6 — every fiscal year Full would have to remove: a referenced year counts, and so does a year the wizard did not
    /// create (Full never deletes those), so the orchestrator refuses Full with 409 up front.
    /// </summary>
    public async Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var years = await YearsAsync(Org, ct);
        var seededRefs = context.AlreadySeeded.Where(s => !s.Reused).Select(s => s.EntityRef).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keys = new List<string>();
        foreach (var y in years)
            if (!seededRefs.Contains(y.Uuid.ToString()) || await IsReferencedAsync(y, ct))
                keys.Add(Key(y.StartDate));
        return new SetupReferenceCheck { ReferencedCount = keys.Count, ReferencedKeys = keys };
    }

    public async Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default)
    {
        if (mode != SetupResetMode.Full)
        {
            var merged = await ApplyAsync(payload, context, ct);
            if (mode == SetupResetMode.Overwrite) merged.Warnings.Insert(0, "A fiscal year is never changed by a reset: Overwrite ran as Merge. Edit the year in General ledger setup.");
            return merged;
        }

        var errors = Parse(payload, out var shape);
        if (errors.Count > 0) return new SetupApplyResult { Category = Category, Warnings = errors };
        var org = Org;
        var years = await _db.FiscalYears.IgnoreQueryFilters().Include(y => y.Periods).Where(y => y.OrganizationId == org).ToListAsync(ct);
        var seeded = context.AlreadySeeded.Where(s => !s.Reused)
            .Select(s => (Ref: s, Year: years.FirstOrDefault(y => y.Uuid.ToString() == s.EntityRef))).Where(x => x.Year is not null).ToList();
        if (years.Count > seeded.Count)
            throw new ConflictException("Full reset refused: the organization has fiscal years the setup wizard did not create.");
        foreach (var (_, y) in seeded)
            if (await IsReferencedAsync(y!, ct))
                throw new ConflictException($"Full reset refused: fiscal year {y!.FiscalYearName} is referenced (journal entries, a closed period or posted opening balances).");

        foreach (var (_, y) in seeded) { _db.FiscalPeriods.RemoveRange(y!.Periods); _db.FiscalYears.Remove(y); }
        await _db.SaveChangesAsync(ct);
        await _setup.RecomputeAsync(org, context.UserId, "Setup wizard", ct);
        var removedKeys = seeded.Select(x => x.Ref.NaturalKey).ToList();
        return await CreateAsync(org, shape!, SetupFingerprint.Fresh(context), shape!.Quarterly ? [QuarterlyWarning] : [], removedKeys, removedKeys.Count, ct);
    }
}
