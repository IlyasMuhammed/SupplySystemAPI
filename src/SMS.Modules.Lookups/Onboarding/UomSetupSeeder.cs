using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Domain;
using SMS.Modules.Lookups.Import;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Lookups.Onboarding;

/// <summary>
/// A50 D-16 / D-55 — UOM. Payload (FSD §5.6) <c>{ groups: [{ name, units: [{ code, name, isBase?, conversionFactor? }] }] }</c>.
/// Units are Lookups values of type <c>uom</c> named "{name} ({code})" (the A40 UNIT convention — the code is what
/// Product.UomCode stores). Natural key = code.
/// <list type="bullet">
/// <item>A global (system) unit with the code is <b>reused</b>, never duplicated or renamed.</item>
/// <item>Otherwise an org-owned value is created; the group name goes in <c>Notes</c> (the value's only free field).</item>
/// <item>Conversion factors / isBase stay in the payload only — there is no conversion engine (R-1).</item>
/// <item>Reset never hard-deletes: products keep a unit by its code string, so a removed unit is only deactivated
/// (the Lookups soft delete). Overwrite restores the seeded name and deactivates seeded units the pack dropped; Full also
/// deactivates every seeded unit and then re-seeds (reactivates) the pack's. Custom units are never touched.</item>
/// </list>
/// </summary>
internal sealed partial class UomSetupSeeder : ISetupCategorySeeder
{
    private readonly LookupsDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IEnumerable<ILookupReferenceChecker> _checkers;

    public UomSetupSeeder(LookupsDbContext db, ITenantContext tenant, IEnumerable<ILookupReferenceChecker> checkers)
    {
        _db = db;
        _tenant = tenant;
        _checkers = checkers;
    }

    public string Category => SetupCategories.Uom;
    public string? RequiresModule => null;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-\.]{0,19}$")]
    private static partial Regex ValidCode();

    private sealed record Unit(string Code, string Name, string Group);

    private static List<Unit> Read(JsonElement payload) =>
        SetupJson.Array(payload, "groups").SelectMany(g =>
        {
            var group = SetupJson.Text(g, "name") ?? string.Empty;
            return SetupJson.Array(g, "units").Select(u => new Unit(
                (SetupJson.Text(u, "code") ?? string.Empty).ToUpperInvariant(), SetupJson.Text(u, "name") ?? string.Empty, group));
        }).ToList();

    public IReadOnlyList<string> ValidatePayload(JsonElement payload)
    {
        if (SetupJson.Prop(payload, "groups") is not { ValueKind: JsonValueKind.Array })
            return ["groups: an array is required."];
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in Read(payload))
        {
            if (!ValidCode().IsMatch(u.Code)) errors.Add($"unit '{u.Code}': code must be 1–20 letters, digits, '-', '_' or '.'.");
            else if (!seen.Add(u.Code)) errors.Add($"unit '{u.Code}': appears more than once.");
            if (u.Name.Length is 0 or > 120) errors.Add($"unit '{u.Code}': name is required (at most 120 characters).");
        }
        return errors;
    }

    private Guid Org => _tenant.OrganizationId;

    private async Task<LookupType?> TypeAsync(CancellationToken ct) =>
        await _db.LookupTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == UnitImportTarget.UomSlug, ct);

    private IQueryable<LookupValue> Units(Guid typeId) =>
        _db.LookupValues.IgnoreQueryFilters().Where(v => v.TypeId == typeId && (v.IsGlobal || v.OrganizationId == Org));

    private static string Fingerprint(LookupValue v) => SetupFingerprint.Of(v.DisplayName.Trim(), v.Notes, v.IsActive);

    // ── Preview ─────────────────────────────────────────────────────────────────

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var units = Read(payload);
        var type = await TypeAsync(ct);
        if (type is null)
            return new SetupPreview { Category = Category, InPayload = units.Count, Warnings = ["The 'uom' lookup type does not exist on this server."] };
        var existing = (await Units(type.Id).AsNoTracking().Where(v => v.IsActive).Select(v => v.DisplayName).ToListAsync(ct))
            .Select(UnitImportTarget.CodeOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SetupPreview
        {
            Category = Category, InPayload = units.Count, AlreadyPresent = units.Count(u => existing.Contains(u.Code)),
            Warnings = ["Conversion factors are kept with the defaults only; they are not applied to stock (R-1)."]
        };
    }

    // ── Apply / reset ───────────────────────────────────────────────────────────

    public Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, SetupResetMode.Merge, ct), ct);

    public Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, mode, ct), ct);

    private async Task<SetupApplyResult> SeedAsync(JsonElement payload, SetupSeedContext context, SetupResetMode mode, CancellationToken ct)
    {
        var units = Read(payload);
        var type = await TypeAsync(ct);
        if (type is null)
            return new SetupApplyResult { Category = Category, Warnings = ["The 'uom' lookup type does not exist on this server; nothing was seeded."] };

        var all = await Units(type.Id).ToListAsync(ct);
        var ours = context.AlreadySeeded.Where(s => !s.Reused).ToDictionary(s => s.NaturalKey, StringComparer.OrdinalIgnoreCase);
        var packCodes = units.Select(u => u.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var seeded = new List<SeededRecordRef>();
        int created = 0, skipped = 0, removed = 0;

        // Overwrite drops seeded units the pack no longer has; Full drops every seeded unit (the pack's come back below).
        if (mode != SetupResetMode.Merge)
            foreach (var (key, s) in ours)
            {
                if (mode == SetupResetMode.Overwrite && packCodes.Contains(key)) continue;
                var v = all.FirstOrDefault(x => !x.IsGlobal && x.OrganizationId == Org && x.Id.ToString() == s.EntityRef);
                if (v is null || !v.IsActive) continue;
                if (_checkers.Any(c => c.IsValueReferenced(v.Id)))
                {
                    warnings.Add($"{key}: kept — the unit is in use.");
                    continue;
                }
                v.IsActive = false;
                if (!packCodes.Contains(key)) removed++;
            }

        var maxSort = all.Select(v => (int?)v.SortOrder).Max() ?? 0;
        var removedKeys = new List<string>();

        foreach (var u in units)
        {
            var own = all.Where(v => !v.IsGlobal && v.OrganizationId == Org
                                     && string.Equals(UnitImportTarget.CodeOf(v.DisplayName), u.Code, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(v => v.IsActive).FirstOrDefault();
            var global = all.FirstOrDefault(v => v.IsGlobal
                                     && string.Equals(UnitImportTarget.CodeOf(v.DisplayName), u.Code, StringComparison.OrdinalIgnoreCase));
            var display = UnitImportTarget.DisplayNameOf(u.Name, u.Code);
            var notes = string.IsNullOrWhiteSpace(u.Group) ? null : u.Group;

            if (own is not null)
            {
                if (!ours.TryGetValue(u.Code, out var mine) || mine.EntityRef != own.Id.ToString())
                {
                    skipped++; // the organization's own unit: custom, left alone
                    continue;
                }
                var restore = mode != SetupResetMode.Merge || !own.IsActive;
                if (restore)
                {
                    var changed = own.DisplayName != display || own.Notes != notes || !own.IsActive;
                    own.DisplayName = display;
                    own.Notes = notes;
                    own.IsActive = true;
                    if (changed) created++; else skipped++;
                }
                else skipped++;
                seeded.Add(new SeededRecordRef(u.Code, own.Id.ToString(), restore ? Fingerprint(own) : mine.Fingerprint));
                continue;
            }

            if (global is not null)
            {
                // D-16 — a system unit with the code is reused, never duplicated or renamed.
                seeded.Add(new SeededRecordRef(u.Code, global.Id.ToString(), Fingerprint(global), Reused: true));
                skipped++;
                continue;
            }

            if (all.Any(v => v.IsActive && string.Equals(v.DisplayName.Trim(), display, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"{u.Code}: not created — a unit named '{display}' already exists.");
                skipped++;
                continue;
            }

            var value = new LookupValue
            {
                Id = Guid.NewGuid(), TypeId = type.Id, DisplayName = display, Notes = notes, IsActive = true,
                SortOrder = ++maxSort, IsGlobal = false, OrganizationId = Org
            };
            _db.LookupValues.Add(value);
            all.Add(value);
            seeded.Add(new SeededRecordRef(u.Code, value.Id.ToString(), Fingerprint(value)));
            created++;
        }

        // Seeded units the pack dropped (Overwrite / Full) lose their provenance once they are inactive.
        if (mode != SetupResetMode.Merge)
            foreach (var (key, s) in ours.Where(o => !packCodes.Contains(o.Key)))
                if (all.FirstOrDefault(x => x.Id.ToString() == s.EntityRef) is not { IsActive: true })
                    removedKeys.Add(key);

        await _db.SaveChangesAsync(ct);
        return new SetupApplyResult
        {
            Category = Category, Created = created, Skipped = skipped, Removed = removed,
            Seeded = seeded, RemovedKeys = removedKeys, Warnings = warnings
        };
    }

    // ── Status / references ─────────────────────────────────────────────────────

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var type = await TypeAsync(ct);
        var values = type is null ? new List<LookupValue>() : await Units(type.Id).AsNoTracking().ToListAsync(ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            var v = values.FirstOrDefault(x => x.Id.ToString() == s.EntityRef);
            if (v is null || (!s.Reused && !v.IsActive)) missing.Add(s.NaturalKey);
            else if (Fingerprint(v) != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus
        {
            Category = Category,
            SeededCount = context.AlreadySeeded.Count,
            CurrentCount = values.Count(v => v.IsActive && !v.IsGlobal && v.OrganizationId == Org),
            ModifiedKeys = modified,
            MissingKeys = missing
        };
    }

    /// <summary>
    /// Seeded units another module reports in use (the Lookups ILookupReferenceChecker seam). Products keep a unit by its
    /// code string, which no checker sees — so a reset only ever deactivates, never deletes (D-55).
    /// </summary>
    public Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var keys = context.AlreadySeeded.Where(s => !s.Reused && Guid.TryParse(s.EntityRef, out var id) && _checkers.Any(c => c.IsValueReferenced(id)))
            .Select(s => s.NaturalKey).ToList();
        return Task.FromResult(new SetupReferenceCheck { ReferencedCount = keys.Count, ReferencedKeys = keys });
    }
}
