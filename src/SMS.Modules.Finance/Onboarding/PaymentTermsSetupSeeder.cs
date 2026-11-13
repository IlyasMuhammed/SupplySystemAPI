using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

using static SMS.Modules.Finance.Onboarding.SetupJson;

namespace SMS.Modules.Finance.Onboarding;

/// <summary>
/// A50 D-15 / D-53 — PAYMENT_TERMS. Two targets:
/// <list type="bullet">
/// <item>every pack term is ensured in the GLOBAL <c>lookups.PaymentTerms</c> by Name (insert if missing, never updated, not
/// in provenance, never removed by a reset) — partners reference that table;</item>
/// <item>terms with an early-payment discount (discountPercent &gt; 0) become the organization's A48 <c>EarlyPaymentTerm</c>
/// rows by Code. A term without a discount (COD, NET30…) cannot be one: the table's CHECKs require 0 &lt; DiscountPercent
/// &lt; 100 and DiscountDays &lt; NetDays.</item>
/// </list>
/// References = vendor finance profiles and payment schedules that use the term. Reset: Merge = apply; Overwrite = seeded
/// terms back to the pack (unreferenced seeded terms no longer in the pack removed, referenced ones deactivated); Full = every
/// early payment term of the organization removed and re-seeded, refused when any is referenced. <c>isDefault</c> has no
/// target column and is ignored.
/// </summary>
internal sealed class PaymentTermsSetupSeeder : ISetupCategorySeeder
{
    private readonly FinanceDbContext _db;
    private readonly ILookupsService? _lookups;
    private readonly TimeProvider _clock;

    public PaymentTermsSetupSeeder(FinanceDbContext db, ILookupsService? lookups = null, TimeProvider? clock = null)
    {
        _db = db;
        _lookups = lookups;
        _clock = clock ?? TimeProvider.System;
    }

    public string Category => SetupCategories.PaymentTerms;
    public string? RequiresModule => null;

    private Guid Org => _db.TenantContext.OrganizationId is var o && o != Guid.Empty
        ? o : throw new InvalidOperationException("PAYMENT_TERMS seeding needs an ambient organization.");

    internal sealed record Item(string Code, string Name, int NetDays, decimal DiscountPercent, int DiscountDays)
    {
        public bool IsEarly => DiscountPercent > 0;
        public string Fingerprint => SetupJson.Fingerprint(Code, Name, NetDays, DiscountPercent, DiscountDays);
    }

    internal static string Fingerprint(EarlyPaymentTerm t) => SetupJson.Fingerprint(t.Code, t.Name, t.NetDays, t.DiscountPercent, t.DiscountDays);

    public IReadOnlyList<string> ValidatePayload(JsonElement payload) => Parse(payload, out _);

    internal static List<string> Parse(JsonElement payload, out List<Item> items)
    {
        var errors = new List<string>();
        items = [];
        var list = Array(payload, "terms").ToList();
        if (list.Count == 0) return ["The PAYMENT_TERMS payload needs a non-empty \"terms\" array."];
        var i = 0;
        foreach (var e in list)
        {
            var at = $"terms[{i++}]";
            var code = Str(e, "code")?.ToUpperInvariant();
            var name = Str(e, "name");
            var net = Int(e, "netDays") ?? Int(e, "days");
            var pct = Dec(e, "discountPercent") ?? 0m;
            var dd = Int(e, "discountDays") ?? 0;
            var before = errors.Count;
            if (code is null || code.Length > 20) errors.Add($"{at}: code is required (at most 20 characters).");
            if (name is null || name.Length > 100) errors.Add($"{at}: name is required (at most 100 characters).");
            if (net is not (>= 0 and <= 365)) errors.Add($"{at}: netDays must be 0-365.");
            if (pct < 0 || pct >= 100) errors.Add($"{at}: discountPercent must be 0 or more and below 100.");
            if (pct > 0 && (dd < 0 || dd >= net)) errors.Add($"{at}: discountDays must be below netDays when there is a discount.");
            if (code is not null && items.Any(x => x.Code == code)) errors.Add($"{at}: code {code} is listed twice.");
            if (errors.Count == before) items.Add(new Item(code!, name!, net!.Value, pct, pct > 0 ? dd : 0));
        }
        return errors;
    }

    private Task<List<EarlyPaymentTerm>> AllAsync(Guid org, CancellationToken ct) =>
        _db.EarlyPaymentTerms.IgnoreQueryFilters().Where(t => t.OrganizationId == org).ToListAsync(ct);

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var errors = Parse(payload, out var items);
        var codes = (await AllAsync(Org, ct)).Select(t => t.Code.ToUpperInvariant()).ToHashSet();
        var early = items.Where(i => i.IsEarly).ToList();
        var warnings = new List<string>(errors);
        if (items.Count > early.Count)
            warnings.Add($"{items.Count - early.Count} term(s) without an early-payment discount are added to the shared payment terms list only.");
        return new SetupPreview { Category = Category, InPayload = early.Count, AlreadyPresent = early.Count(i => codes.Contains(i.Code)), Warnings = warnings };
    }

    public async Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var errors = Parse(payload, out var items);
        if (errors.Count > 0) return new SetupApplyResult { Category = Category, Warnings = errors };
        var org = Org;
        var all = await AllAsync(org, ct);
        var warnings = new List<string>();
        var result = Seed(org, all, items, context, warnings, [], 0);
        await _db.SaveChangesAsync(ct);
        EnsureGlobalTerms(items, context, warnings);
        return result;
    }

    /// <summary>REV50-8 — names this call may insert into the GLOBAL list: the payload's, or only the Super Admin default's when the payload is an override.</summary>
    internal static HashSet<string> GlobalAllowed(List<Item> items, SetupSeedContext context)
    {
        if (!context.PayloadIsOverride) return items.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (context.DefaultPayload is not { } d || Parse(d, out var defaults).Count > 0) return new(StringComparer.OrdinalIgnoreCase);
        return defaults.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>D-15 — the global list by Name, insert-only. Its own SaveChanges (Lookups' context), after Finance's.</summary>
    private void EnsureGlobalTerms(List<Item> items, SetupSeedContext context, List<string> warnings)
    {
        if (_lookups is null) return;
        var names = _lookups.GetPaymentTerms().Select(p => p.Name.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowed = GlobalAllowed(items, context);
        foreach (var item in items.Where(i => !names.Contains(i.Name)))
        {
            if (!allowed.Contains(item.Name))
            {
                // REV50-8 — a name only the org admin typed never reaches the shared (all-tenant) list.
                warnings.Add($"Payment term \"{item.Name}\" is not in the shared payment terms list and was not added to it: ask your platform administrator.");
                continue;
            }
            try
            {
                _lookups.CreatePaymentTerms(new CreatePaymentTermRequest { Name = item.Name, Days = item.NetDays });
                names.Add(item.Name);
            }
            catch (ConflictException)
            {
                // created by someone else meanwhile — that is the goal
            }
            catch (BadRequestException ex)
            {
                warnings.Add($"Payment term \"{item.Name}\" was not added to the shared list: {ex.Message}");
            }
        }
    }

    private SetupApplyResult Seed(Guid org, List<EarlyPaymentTerm> all, List<Item> items, SetupSeedContext context, List<string> warnings,
        List<string> removedKeys, int removed)
    {
        var known = context.AlreadySeeded.Select(s => s.NaturalKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = _clock.GetUtcNow().UtcDateTime;
        var seeded = new List<SeededRecordRef>();
        int created = 0, skipped = 0;
        foreach (var item in items.Where(i => i.IsEarly))
        {
            var existing = all.FirstOrDefault(t => string.Equals(t.Code, item.Code, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                skipped++;
                if (!known.Contains(item.Code)) seeded.Add(new SeededRecordRef(item.Code, existing.Uuid.ToString(), Fingerprint(existing), Reused: true));
                continue;
            }
            var term = new EarlyPaymentTerm
            {
                OrganizationId = org, Code = item.Code, Name = item.Name, NetDays = item.NetDays, DiscountPercent = item.DiscountPercent,
                DiscountDays = item.DiscountDays, IsActive = true, CreatedAt = now, CreatedBy = context.UserId, CreatedByName = "Setup wizard",
            };
            _db.EarlyPaymentTerms.Add(term);
            all.Add(term);
            created++;
            seeded.Add(new SeededRecordRef(item.Code, term.Uuid.ToString(), item.Fingerprint));
        }
        return new SetupApplyResult
        {
            Category = Category, Created = created, Skipped = skipped, Seeded = seeded, Warnings = warnings, Removed = removed, RemovedKeys = removedKeys,
        };
    }

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var all = await _db.EarlyPaymentTerms.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == Org).ToListAsync(ct);
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

    internal async Task<HashSet<int>> ReferencedAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        var used = new HashSet<int>();
        if (ids.Count == 0) return used;
        var list = ids.Select(i => (int?)i).ToList();
        used.UnionWith(await _db.PartnerFinancialProfiles.IgnoreQueryFilters().Where(p => list.Contains(p.EarlyPaymentTermId))
            .Select(p => p.EarlyPaymentTermId!.Value).Distinct().ToListAsync(ct));
        used.UnionWith(await _db.PaymentSchedules.IgnoreQueryFilters().Where(p => list.Contains(p.EarlyPaymentTermId))
            .Select(p => p.EarlyPaymentTermId!.Value).Distinct().ToListAsync(ct));
        return used;
    }

    /// <summary>REV50-6 — exactly what Full would delete: EVERY early payment term of the organization (custom ones included).</summary>
    public async Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var all = await _db.EarlyPaymentTerms.IgnoreQueryFilters().AsNoTracking().Where(t => t.OrganizationId == Org).ToListAsync(ct);
        var used = await ReferencedAsync(all.Select(t => t.Id).ToList(), ct);
        var keys = all.Where(t => used.Contains(t.Id)).Select(t => t.Code).OrderBy(c => c, StringComparer.Ordinal).ToList();
        return new SetupReferenceCheck { ReferencedCount = keys.Count, ReferencedKeys = keys };
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
        SetupApplyResult result;

        if (mode == SetupResetMode.Full)
        {
            var used = await ReferencedAsync(all.Select(t => t.Id).ToList(), ct);
            if (used.Count > 0)
                throw new ConflictException(
                    $"Full reset refused: {used.Count} payment term(s) are used by vendors or payment schedules ({string.Join(", ", all.Where(t => used.Contains(t.Id)).Select(t => t.Code))}). Use Overwrite instead.");
            _db.EarlyPaymentTerms.RemoveRange(all);
            removed = all.Count;
            removedKeys.AddRange(context.AlreadySeeded.Select(s => s.NaturalKey));
            all.Clear();
            result = Seed(org, all, items, new SetupSeedContext { UserId = context.UserId, Version = context.Version }, warnings, removedKeys, removed);
        }
        else
        {
            var seededRows = context.AlreadySeeded.Where(s => !s.Reused)
                .Select(s => (Ref: s, Row: all.FirstOrDefault(t => t.Uuid.ToString() == s.EntityRef))).Where(x => x.Row is not null).ToList();
            var referenced = await ReferencedAsync(seededRows.Select(x => x.Row!.Id).ToList(), ct);
            var reverted = new List<SeededRecordRef>();
            foreach (var (s, row) in seededRows)
            {
                var item = items.FirstOrDefault(i => i.IsEarly && string.Equals(i.Code, row!.Code, StringComparison.OrdinalIgnoreCase));
                if (item is null)
                {
                    if (referenced.Contains(row!.Id))
                    {
                        row.IsActive = false; row.ModifiedAt = now; row.ModifiedBy = context.UserId;
                        warnings.Add($"{row.Code} is no longer in the defaults but is in use: deactivated, not removed.");
                    }
                    else { _db.EarlyPaymentTerms.Remove(row); all.Remove(row); removedKeys.Add(s.NaturalKey); removed++; }
                    continue;
                }
                if (Fingerprint(row!) == item.Fingerprint && row!.IsActive) continue;
                row!.Name = item.Name; row.NetDays = item.NetDays; row.DiscountPercent = item.DiscountPercent; row.DiscountDays = item.DiscountDays;
                row.IsActive = true; row.ModifiedAt = now; row.ModifiedBy = context.UserId; row.ModifiedByName = "Setup wizard";
                reverted.Add(new SeededRecordRef(s.NaturalKey, row.Uuid.ToString(), item.Fingerprint));
            }
            result = Seed(org, all, items, context, warnings, removedKeys, removed);
            result.Seeded.AddRange(reverted);
        }
        await _db.SaveChangesAsync(ct);
        EnsureGlobalTerms(items, context, warnings);
        return result;
    }
}
