using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

using static SMS.Modules.Finance.Onboarding.SetupJson;

namespace SMS.Modules.Finance.Onboarding;

/// <summary>
/// A50 D-14 / D-50 — CURRENCY: makes every pack currency (base + additional) an active currency of the ambient organization
/// (<c>finance.org_currencies</c>; a missing one is added from the global catalog, an inactive one re-activated) and
/// deactivates nothing. Then sets the sale / purchase / service base to the pack base through Tenancy
/// (<see cref="IOrganizationBaseCurrencySetter"/>), which refuses while documents lock a base (A35 BR-C3-03): the base is then
/// left as it is and a warning says why. The rate currency never changes (A35 REV-07). Currencies are never removed or
/// changed back, so every reset mode runs as Merge and nothing is ever "referenced".
/// </summary>
internal sealed class CurrencySetupSeeder : ISetupCategorySeeder
{
    private readonly FinanceDbContext _db;
    private readonly ILookupsService _lookups;
    private readonly IOrganizationBaseCurrencySetter _bases;
    private readonly IReadOnlyList<ICurrencyUsageChecker> _usage;
    private readonly TimeProvider _clock;

    public CurrencySetupSeeder(FinanceDbContext db, ILookupsService lookups, IOrganizationBaseCurrencySetter? bases = null,
        IEnumerable<ICurrencyUsageChecker>? usage = null, TimeProvider? clock = null)
    {
        _db = db;
        _lookups = lookups;
        _bases = bases ?? new NullOrganizationBaseCurrencySetter();
        _usage = usage?.ToList() ?? [];
        _clock = clock ?? TimeProvider.System;
    }

    public string Category => SetupCategories.Currency;
    public string? RequiresModule => null;

    private Guid Org => _db.TenantContext.OrganizationId is var o && o != Guid.Empty
        ? o : throw new InvalidOperationException("CURRENCY seeding needs an ambient organization.");

    internal sealed record Item(string Code, string Name, string? Symbol, int? Decimals, bool IsBase);

    internal static string Fingerprint(OrgCurrency c) => SetupJson.Fingerprint(c.Code, c.Name, c.Symbol, c.DecimalPlaces);

    public IReadOnlyList<string> ValidatePayload(JsonElement payload) => Parse(payload, out _);

    internal static List<string> Parse(JsonElement payload, out List<Item> items)
    {
        var errors = new List<string>();
        var parsed = new List<Item>();
        items = parsed;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("base", out var b) || b.ValueKind != JsonValueKind.Object)
            return ["The CURRENCY payload needs a \"base\" currency object."];

        void Add(JsonElement e, string at, bool isBase)
        {
            var code = Str(e, "code")?.ToUpperInvariant();
            if (code is null || code.Length != 3 || !code.All(ch => ch is >= 'A' and <= 'Z')) { errors.Add($"{at}: code must be 3 letters A-Z."); return; }
            var name = Str(e, "name");
            if (name is null) { errors.Add($"{at}: name is required."); return; }
            var decimals = Int(e, "decimals");
            if (decimals is < 0 or > 3) { errors.Add($"{at}: decimals must be 0-3."); return; }
            if (parsed.Any(i => i.Code == code)) return; // the base listed again in additional: once is enough
            parsed.Add(new Item(code, name, Str(e, "symbol"), decimals, isBase));
        }

        Add(b, "base", true);
        var i = 0;
        foreach (var e in Array(payload, "additional")) Add(e, $"additional[{i++}]", false);
        return errors;
    }

    private Task<List<OrgCurrency>> OwnAsync(Guid org, CancellationToken ct) =>
        _db.OrgCurrencies.IgnoreQueryFilters().Where(c => c.OrganizationId == org).ToListAsync(ct);

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var errors = Parse(payload, out var items);
        var org = Org;
        var own = await OwnAsync(org, ct);
        var warnings = new List<string>(errors);
        foreach (var domain in new[] { TransactionDomain.Sale, TransactionDomain.Purchase, TransactionDomain.Service })
            foreach (var checker in _usage)
                if (await checker.DescribeDomainBaseUsageAsync(org, domain, ct) is { } used)
                {
                    warnings.Add($"The base currency will not be changed: the {domain.ToString().ToLowerInvariant()} base is in use ({used}).");
                    goto done;
                }
        done:
        return new SetupPreview
        {
            Category = Category, InPayload = items.Count,
            AlreadyPresent = items.Count(i => own.Any(c => c.Code == i.Code && c.IsActive)), Warnings = warnings,
        };
    }

    public Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default) =>
        SeedAsync(payload, context, overwrite: false, ct);

    /// <summary>
    /// REV50-8 — codes this call may add to the GLOBAL catalog (lookups.Currencies): only those of the Super Admin default,
    /// never codes that exist only in the org admin's override.
    /// </summary>
    internal static HashSet<string> GlobalAllowed(JsonElement payload, SetupSeedContext context)
    {
        var source = context.PayloadIsOverride ? context.DefaultPayload : payload;
        if (source is not { } p || Parse(p, out var items).Count > 0) return [];
        return items.Select(i => i.Code).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// REV50-7 — an admin's own choices win on a re-apply / Merge: an inactive currency is re-activated and the base set only on
    /// the first apply of this category (no provenance yet) or in Overwrite; otherwise both are reported and left alone.
    /// </summary>
    private async Task<SetupApplyResult> SeedAsync(JsonElement payload, SetupSeedContext context, bool overwrite, CancellationToken ct)
    {
        var errors = Parse(payload, out var items);
        if (errors.Count > 0) return new SetupApplyResult { Category = Category, Warnings = errors };

        var org = Org;
        var own = await OwnAsync(org, ct);
        var known = context.AlreadySeeded.Select(s => s.NaturalKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalog = _lookups.GetCurrencies();
        var now = _clock.GetUtcNow().UtcDateTime;
        var warnings = new List<string>();
        var seeded = new List<SeededRecordRef>();
        int created = 0, skipped = 0;
        var order = own.Select(c => (int?)c.DisplayOrder).Max() ?? 0;
        var firstOrOverwrite = overwrite || context.AlreadySeeded.Count == 0;
        var allowedGlobal = GlobalAllowed(payload, context);

        foreach (var item in items)
        {
            var row = own.FirstOrDefault(c => c.Code == item.Code);
            if (row is not null)
            {
                if (!row.IsActive && firstOrOverwrite)
                {
                    row.IsActive = true; row.ModifiedBy = context.UserId; row.ModifiedDate = now;
                    warnings.Add($"{row.Code} was inactive and is active again.");
                }
                else if (!row.IsActive)
                    warnings.Add($"{row.Code} is inactive and was left inactive (Overwrite re-activates it).");
                skipped++;
                if (!known.Contains(item.Code)) seeded.Add(new SeededRecordRef(item.Code, row.CurrencyId.ToString(), Fingerprint(row), Reused: true));
                continue;
            }

            var symbol = item.Symbol is { Length: > 0 and <= 5 } s ? s : null;
            var entry = catalog.FirstOrDefault(c => string.Equals(c.Code?.Trim(), item.Code, StringComparison.OrdinalIgnoreCase));
            symbol ??= entry?.Symbol is { Length: > 0 and <= 5 } cs ? cs.Trim() : item.Code;
            var name = item.Name.Length > 60 ? item.Name[..60] : item.Name;
            if (entry is null && !allowedGlobal.Contains(item.Code))
            {
                // REV50-8 — a code only the org admin typed never reaches the shared catalog.
                warnings.Add($"{item.Code} is not in the currency catalog and was not added: ask your platform administrator to add it.");
                continue;
            }
            var currencyId = entry?.Id ?? _lookups.CreateCurrency(new CreateCurrencyRequest { Code = item.Code, Name = name, Symbol = symbol });
            var decimals = item.Decimals ?? CurrencyConventions.DefaultDecimalPlaces;
            row = new OrgCurrency
            {
                OrganizationId = org, CurrencyId = currencyId, Code = item.Code, Name = name, Symbol = symbol,
                DecimalPlaces = decimals, Rounding = OrgCurrencyService.DefaultRounding(decimals),
                SymbolPosition = CurrencyConventions.SymbolBefore, IsActive = true, DisplayOrder = ++order,
                CreatedBy = context.UserId, CreatedDate = now,
            };
            _db.OrgCurrencies.Add(row);
            own.Add(row);
            created++;
            seeded.Add(new SeededRecordRef(item.Code, currencyId.ToString(), Fingerprint(row)));
        }
        await _db.SaveChangesAsync(ct);

        var baseItem = items.First(i => i.IsBase);
        var baseRow = own.FirstOrDefault(c => c.Code == baseItem.Code);
        if (baseRow is null)
            warnings.Add($"The base currency was left as it is: {baseItem.Code} is not a currency of this organization.");
        else if (!firstOrOverwrite)
            warnings.Add($"The base currency was left as it is (Merge never changes it; Overwrite sets it to {baseItem.Code} while no documents exist).");
        else
        {
            if (!baseRow.IsActive) { baseRow.IsActive = true; baseRow.ModifiedBy = context.UserId; baseRow.ModifiedDate = now; await _db.SaveChangesAsync(ct); }
            var set = await _bases.TrySetAllBasesAsync(org, baseRow.CurrencyId, context.UserId, ct);
            if (set.RefusedReason is { } reason)
                warnings.Add($"The base currency was left as it is ({baseItem.Code} not set): {reason}.");
        }

        return new SetupApplyResult { Category = Category, Created = created, Skipped = skipped, Seeded = seeded, Warnings = warnings };
    }

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var own = await _db.OrgCurrencies.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == Org).ToListAsync(ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            var row = own.FirstOrDefault(c => c.CurrencyId.ToString() == s.EntityRef);
            if (row is null || !row.IsActive) missing.Add(s.NaturalKey);
            else if (Fingerprint(row) != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus
        {
            Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = own.Count(c => c.IsActive), ModifiedKeys = modified, MissingKeys = missing,
        };
    }

    public Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default) =>
        Task.FromResult(new SetupReferenceCheck());

    public async Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default)
    {
        var result = await SeedAsync(payload, context, overwrite: mode != SetupResetMode.Merge, ct);
        if (mode == SetupResetMode.Full)
            result.Warnings.Insert(0, "Currencies are never removed: Full ran as Overwrite (missing currencies added, pack currencies activated, base set while no documents exist).");
        return result;
    }
}
