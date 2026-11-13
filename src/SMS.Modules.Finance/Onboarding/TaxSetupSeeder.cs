using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

using static SMS.Modules.Finance.Onboarding.SetupJson;

namespace SMS.Modules.Finance.Onboarding;

/// <summary>
/// A50 D-13 / D-52 — TAX: the pack's tax codes into <c>finance.tax_codes</c> of the ambient organization, by Code (natural
/// key, upper case). Insert-only on apply; an existing code (seeded or the admin's own) is never changed. A pack default
/// becomes the default only where no active code of an overlapping usage is the default already (one default per usage, as
/// TaxCodeService rules). References (BR-50-008) = the code's uuid on sale orders, quotations, sales invoice lines and supplier
/// invoices. Reset: Merge = apply; Overwrite = seeded codes back to the pack's values (unreferenced seeded codes no longer in
/// the pack are removed, referenced ones deactivated), custom codes kept; Full = every code of the organization removed and
/// the pack re-seeded, refused when any code is referenced.
/// </summary>
internal sealed class TaxSetupSeeder : ISetupCategorySeeder
{
    private static readonly Regex CodePattern = new("^[A-Z0-9_-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly FinanceDbContext _db;
    private readonly DemandDbContext? _demand;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<ITaxCodeReferenceChecker> _checkers;

    /// <param name="checkers">Modules that keep a code by its text (POS stores/orders, QuickBooks mappings) — REV50-1.</param>
    public TaxSetupSeeder(FinanceDbContext db, DemandDbContext? demand = null, TimeProvider? clock = null,
        IEnumerable<ITaxCodeReferenceChecker>? checkers = null)
    {
        _db = db;
        _demand = demand;
        _clock = clock ?? TimeProvider.System;
        _checkers = checkers?.ToList() ?? [];
    }

    public string Category => SetupCategories.Tax;
    public string? RequiresModule => ModuleCodes.Finance;

    private Guid Org => _db.TenantContext.OrganizationId is var o && o != Guid.Empty
        ? o : throw new InvalidOperationException("TAX seeding needs an ambient organization.");

    internal sealed record Item(string Code, string Name, string? Description, decimal Rate, string Usage, bool IsDefault)
    {
        public string Fingerprint => SetupJson.Fingerprint(Code, Name, Description, Rate, Usage);
    }

    internal static string Fingerprint(TaxCode t) => SetupJson.Fingerprint(t.Code, t.Name, t.Description, t.RatePercent, t.Usage);

    // ── payload ──────────────────────────────────────────────────────────────

    public IReadOnlyList<string> ValidatePayload(JsonElement payload) => Parse(payload, out _);

    internal static List<string> Parse(JsonElement payload, out List<Item> items)
    {
        var errors = new List<string>();
        items = [];
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("taxCodes", out var list) || list.ValueKind != JsonValueKind.Array)
            return ["The TAX payload needs a \"taxCodes\" array."];

        var i = 0;
        foreach (var e in list.EnumerateArray())
        {
            var at = $"taxCodes[{i++}]";
            var code = Str(e, "code")?.ToUpperInvariant();
            var name = Str(e, "name");
            var rate = Dec(e, "rate");
            var usage = Str(e, "usage")?.ToUpperInvariant();
            if (code is null || code.Length > 20 || !CodePattern.IsMatch(code))
                errors.Add($"{at}: code must be 1-20 characters of A-Z, 0-9, '_' or '-'.");
            if (name is null || name.Length > 100) errors.Add($"{at}: name is required (at most 100 characters).");
            if (rate is not { } r || r < 0 || r > 100 || decimal.Round(r, 2) != r) errors.Add($"{at}: rate must be 0-100 with at most two decimals.");
            if (usage is null || !TaxCodeUsages.All.Contains(usage)) errors.Add($"{at}: usage must be SALES, PURCHASE or BOTH.");
            var description = Str(e, "description");
            if (description is { Length: > 300 }) errors.Add($"{at}: description can be at most 300 characters.");
            if (code is not null && items.Any(x => x.Code == code)) errors.Add($"{at}: code {code} is listed twice.");
            if (errors.Count == 0)
                items.Add(new Item(code!, name!, description, rate!.Value, usage!, Bool(e, "isDefault")));
        }
        var defaults = items.Where(x => x.IsDefault).ToList();
        for (var a = 0; a < defaults.Count; a++)
            for (var b = a + 1; b < defaults.Count; b++)
                if (Overlaps(defaults[a].Usage, defaults[b].Usage))
                    errors.Add($"Only one default per usage: {defaults[a].Code} and {defaults[b].Code} overlap.");
        if (items.Count == 0 && errors.Count == 0) errors.Add("The TAX payload has no tax codes.");
        return errors;
    }

    private static bool Overlaps(string a, string b) => a == TaxCodeUsages.Both || b == TaxCodeUsages.Both || a == b;

    private Task<List<TaxCode>> AllAsync(Guid org, CancellationToken ct) =>
        _db.TaxCodes.IgnoreQueryFilters().Where(t => t.OrganizationId == org).ToListAsync(ct);

    // ── preview / apply ──────────────────────────────────────────────────────

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var errors = Parse(payload, out var items);
        var codes = (await AllAsync(Org, ct)).Select(t => t.Code.ToUpperInvariant()).ToHashSet();
        var warnings = new List<string>(errors);
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.False)
            warnings.Add(Str(payload, "verificationNote") ?? "These tax rates are not verified: check them with your tax adviser.");
        return new SetupPreview { Category = Category, InPayload = items.Count, AlreadyPresent = items.Count(x => codes.Contains(x.Code)), Warnings = warnings };
    }

    public async Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var errors = Parse(payload, out var items);
        if (errors.Count > 0) return new SetupApplyResult { Category = Category, Warnings = errors };
        var org = Org;
        var all = await AllAsync(org, ct);
        var result = Seed(org, all, items, context, new List<string>());
        await _db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Adds the missing codes to <paramref name="all"/> (tracked); returns created + reused refs.</summary>
    private SetupApplyResult Seed(Guid org, List<TaxCode> all, List<Item> items, SetupSeedContext context, List<string> warnings,
        List<string>? removed = null, int removedCount = 0)
    {
        var known = context.AlreadySeeded.Select(s => s.NaturalKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = _clock.GetUtcNow().UtcDateTime;
        var seeded = new List<SeededRecordRef>();
        int created = 0, skipped = 0;
        foreach (var item in items)
        {
            var existing = all.FirstOrDefault(t => string.Equals(t.Code, item.Code, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                skipped++;
                if (!known.Contains(item.Code)) seeded.Add(new SeededRecordRef(item.Code, existing.Uuid.ToString(), Fingerprint(existing), Reused: true));
                continue;
            }
            var makeDefault = item.IsDefault && !all.Any(t => t.IsDefault && t.IsActive && Overlaps(t.Usage, item.Usage));
            if (item.IsDefault && !makeDefault) warnings.Add($"{item.Code} was not made the default: another code is the default for {item.Usage}.");
            var entity = new TaxCode
            {
                Uuid = Guid.NewGuid(), OrganizationId = org, Code = item.Code, Name = item.Name, Description = item.Description,
                RatePercent = item.Rate, Usage = item.Usage, IsDefault = makeDefault, IsActive = true,
                CreatedBy = context.UserId ?? 0, CreatedDate = now,
            };
            _db.TaxCodes.Add(entity);
            all.Add(entity);
            created++;
            seeded.Add(new SeededRecordRef(item.Code, entity.Uuid.ToString(), item.Fingerprint));
        }
        return new SetupApplyResult
        {
            Category = Category, Created = created, Skipped = skipped, Seeded = seeded, Warnings = warnings,
            Removed = removedCount, RemovedKeys = removed ?? [],
        };
    }

    // ── status / references / reset ─────────────────────────────────────────

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var all = await _db.TaxCodes.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == Org).ToListAsync(ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            var row = all.FirstOrDefault(t => t.Uuid.ToString() == s.EntityRef);
            if (row is null) missing.Add(s.NaturalKey);
            else if (Fingerprint(row) != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus
        {
            Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = all.Count, ModifiedKeys = modified, MissingKeys = missing,
        };
    }

    public async Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        // REV50-6 — exactly what Full would delete: EVERY tax code of the organization (custom and reused ones included).
        var rows = await _db.TaxCodes.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == Org).ToListAsync(ct);
        var used = await ReferencedAsync(rows, ct);
        var keys = rows.Where(t => used.Contains(t.Uuid)).Select(t => t.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();
        return new SetupReferenceCheck { ReferencedCount = keys.Count, ReferencedKeys = keys };
    }

    /// <summary>
    /// The uuids of <paramref name="codes"/> that something keeps: a document by uuid (sale orders, quotations, sales invoice
    /// lines, supplier invoices) or another module by the code's text (<see cref="ITaxCodeReferenceChecker"/>: POS, QuickBooks).
    /// </summary>
    internal async Task<HashSet<Guid>> ReferencedAsync(IReadOnlyCollection<TaxCode> codes, CancellationToken ct)
    {
        var used = new HashSet<Guid>();
        if (codes.Count == 0) return used;
        foreach (var code in codes)
            foreach (var checker in _checkers)
                if (await checker.FindCodeReferenceAsync(code.OrganizationId, code.Code, ct) is { Length: > 0 })
                {
                    used.Add(code.Uuid);
                    break;
                }
        var ids = codes.Select(c => (Guid?)c.Uuid).ToList();
        used.UnionWith(await _db.SalesInvoiceLines.IgnoreQueryFilters().Where(l => ids.Contains(l.TaxCodeUuid)).Select(l => l.TaxCodeUuid!.Value).Distinct().ToListAsync(ct));
        used.UnionWith(await _db.Invoices.IgnoreQueryFilters().Where(i => ids.Contains(i.TaxCodeUuid)).Select(i => i.TaxCodeUuid!.Value).Distinct().ToListAsync(ct));
        if (_demand is not null)
        {
            used.UnionWith(await _demand.SaleOrderLines.IgnoreQueryFilters().Where(l => ids.Contains(l.TaxCodeUuid)).Select(l => l.TaxCodeUuid!.Value).Distinct().ToListAsync(ct));
            used.UnionWith(await _demand.SaleQuotationLines.IgnoreQueryFilters().Where(l => ids.Contains(l.TaxCodeUuid)).Select(l => l.TaxCodeUuid!.Value).Distinct().ToListAsync(ct));
        }
        return used;
    }

    public async Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default)
    {
        if (mode == SetupResetMode.Merge) return await ApplyAsync(payload, context, ct);
        var errors = Parse(payload, out var items);
        if (errors.Count > 0) return new SetupApplyResult { Category = Category, Warnings = errors };

        var org = Org;
        var all = await AllAsync(org, ct);
        var warnings = new List<string>();
        var removedKeys = new List<string>();
        var removed = 0;
        var now = _clock.GetUtcNow().UtcDateTime;

        if (mode == SetupResetMode.Full)
        {
            var used = await ReferencedAsync(all, ct);
            if (used.Count > 0)
                throw new SMS.Shared.Exceptions.ConflictException(
                    $"Full reset refused: {used.Count} tax code(s) are referenced ({string.Join(", ", all.Where(t => used.Contains(t.Uuid)).Select(t => t.Code))}). Use Overwrite instead.");
            _db.TaxCodes.RemoveRange(all);
            removedKeys.AddRange(context.AlreadySeeded.Select(s => s.NaturalKey));
            removed = all.Count;
            all.Clear();
            var fresh = new SetupSeedContext
            {
                CountryCode = context.CountryCode, IndustryType = context.IndustryType, Version = context.Version, UserId = context.UserId,
                WizardInput = context.WizardInput, AlreadySeeded = [],
            };
            var full = Seed(org, all, items, fresh, warnings, removedKeys, removed);
            await _db.SaveChangesAsync(ct);
            return full;
        }

        // Overwrite — seeded (non-reused) codes back to the pack; custom codes untouched.
        var seededRefs = context.AlreadySeeded.Where(s => !s.Reused).ToList();
        var seededRows = seededRefs.Select(s => (Ref: s, Row: all.FirstOrDefault(t => t.Uuid.ToString() == s.EntityRef))).Where(x => x.Row is not null).ToList();
        var referenced = await ReferencedAsync(seededRows.Select(x => x.Row!).ToList(), ct);
        var reverted = new List<SeededRecordRef>();
        foreach (var (s, row) in seededRows)
        {
            var item = items.FirstOrDefault(i => string.Equals(i.Code, row!.Code, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                if (referenced.Contains(row!.Uuid))
                {
                    row.IsActive = false; row.IsDefault = false; row.ModifiedBy = context.UserId; row.ModifiedDate = now;
                    warnings.Add($"{row.Code} is no longer in the defaults but is used on documents: deactivated, not removed.");
                }
                else
                {
                    _db.TaxCodes.Remove(row);
                    all.Remove(row);
                    removedKeys.Add(s.NaturalKey);
                    removed++;
                }
                continue;
            }
            if (Fingerprint(row!) == item.Fingerprint && row!.IsActive) continue;
            row!.Name = item.Name; row.Description = item.Description; row.RatePercent = item.Rate; row.Usage = item.Usage; row.IsActive = true;
            row.ModifiedBy = context.UserId; row.ModifiedDate = now;
            if (referenced.Contains(row.Uuid)) warnings.Add($"{row.Code} is used on documents: they keep their rate; new lines use {item.Rate.ToString(CultureInfo.InvariantCulture)}%.");
            reverted.Add(new SeededRecordRef(s.NaturalKey, row.Uuid.ToString(), item.Fingerprint));
        }
        var result = Seed(org, all, items, context, warnings, removedKeys, removed);
        result.Seeded.AddRange(reverted);
        await _db.SaveChangesAsync(ct);
        return result;
    }
}
