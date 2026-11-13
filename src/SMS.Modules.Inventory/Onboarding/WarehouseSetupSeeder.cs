using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Inventory.Onboarding;

/// <summary>
/// A50 D-21 / D-56 — WAREHOUSE (MODULE_INVENTORY).
/// <list type="bullet">
/// <item>Pack: <c>{ zoneTypes: [{ code, name, description?, default? }], defaultWarehouse?: { code, name } }</c>.</item>
/// <item>Wizard input (Step 6, <see cref="SetupSeedContext.WizardInput"/>): <c>{ warehouses: [{ code, name, address?, city?,
/// country?, zones?: [zoneTypeCode] }] }</c>. A warehouse without <c>zones</c> gets the pack's default zone types (every type
/// when none is marked default). With no input, the pack's <c>defaultWarehouse</c> is seeded.</item>
/// <item>Natural keys: the warehouse code (<c>WH-MAIN</c>) and <c>WH-MAIN/RECEIVING</c> for a zone. A zone's Code is the zone
/// type code, its Name the type's name (the Zone entity has no type column).</item>
/// <item>An existing warehouse is matched by code. One this seeder did not create is not changed, except that zones the
/// wizard explicitly asks for are added to it when missing.</item>
/// <item>Reset never deletes a warehouse: other modules keep warehouses by uuid (deliveries, POs, transfers) with no
/// reference visible here, so a dropped seeded warehouse is <b>deactivated</b>. A dropped seeded zone is deleted when no
/// rack, bin or stock row uses it, else deactivated.</item>
/// </list>
/// </summary>
internal sealed class WarehouseSetupSeeder : ISetupCategorySeeder
{
    private const int MaxCode = 20, MaxName = 200;

    private readonly InventoryDbContext _db;
    private readonly ITenantContext _tenant;

    public WarehouseSetupSeeder(InventoryDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public string Category => SetupCategories.Warehouse;
    public string? RequiresModule => ModuleCodes.Inventory;

    private Guid Org => _tenant.OrganizationId;

    private sealed record ZoneType(string Code, string Name, string? Description, bool IsDefault);
    private sealed record PlannedWarehouse(string Code, string Name, string? Address, string? City, string? Country, List<string> Zones, bool ExplicitZones);

    private static List<ZoneType> ZoneTypes(JsonElement payload) =>
        SetupJson.Array(payload, "zoneTypes").Select(z => new ZoneType(
            (SetupJson.Text(z, "code") ?? string.Empty).ToUpperInvariant(), SetupJson.Text(z, "name") ?? string.Empty,
            SetupJson.Text(z, "description"), SetupJson.Bool(z, "default") ?? false)).ToList();

    private static List<PlannedWarehouse> Plan(JsonElement payload, JsonElement? input, List<string> warnings)
    {
        var types = ZoneTypes(payload);
        var defaults = types.Where(t => t.IsDefault).Select(t => t.Code).ToList();
        if (defaults.Count == 0) defaults = types.Select(t => t.Code).ToList();

        var rows = input is { ValueKind: JsonValueKind.Object } i && SetupJson.Prop(i, "warehouses") is { ValueKind: JsonValueKind.Array }
            ? SetupJson.Array(i, "warehouses").ToList()
            : SetupJson.Prop(payload, "defaultWarehouse") is { ValueKind: JsonValueKind.Object } d ? [d] : [];

        var plan = new List<PlannedWarehouse>();
        foreach (var w in rows)
        {
            var code = (SetupJson.Text(w, "code") ?? string.Empty).ToUpperInvariant();
            var explicitZones = SetupJson.Prop(w, "zones") is { ValueKind: JsonValueKind.Array };
            var zones = explicitZones ? SetupJson.Strings(w, "zones").Select(z => z.ToUpperInvariant()).Distinct().ToList() : defaults.ToList();
            foreach (var unknown in zones.Where(z => types.All(t => t.Code != z)).ToList())
            {
                warnings.Add($"{code}: zone type '{unknown}' is not in the defaults and was ignored.");
                zones.Remove(unknown);
            }
            plan.Add(new PlannedWarehouse(code, SetupJson.Text(w, "name") ?? string.Empty, SetupJson.Text(w, "address"),
                SetupJson.Text(w, "city"), SetupJson.Text(w, "country"), zones, explicitZones));
        }
        return plan;
    }

    public IReadOnlyList<string> ValidatePayload(JsonElement payload)
    {
        if (SetupJson.Prop(payload, "zoneTypes") is not { ValueKind: JsonValueKind.Array })
            return ["zoneTypes: an array is required."];
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var z in ZoneTypes(payload))
        {
            if (z.Code.Length is 0 or > MaxCode) errors.Add($"zone type '{z.Code}': code is required (at most {MaxCode} characters).");
            else if (!seen.Add(z.Code)) errors.Add($"zone type '{z.Code}': appears more than once.");
            if (z.Name.Length is 0 or > MaxName) errors.Add($"zone type '{z.Code}': name is required.");
        }
        if (SetupJson.Prop(payload, "defaultWarehouse") is { ValueKind: JsonValueKind.Object } d)
        {
            var code = SetupJson.Text(d, "code") ?? string.Empty;
            if (code.Length is 0 or > MaxCode) errors.Add($"defaultWarehouse.code: required, at most {MaxCode} characters.");
            if ((SetupJson.Text(d, "name") ?? string.Empty).Length is 0 or > MaxName) errors.Add("defaultWarehouse.name: required.");
        }
        return errors;
    }

    private static string WhKey(string code) => code;
    private static string ZoneKey(string wh, string zone) => $"{wh}/{zone}";
    private static string ZoneRef(int id) => "Z:" + id.ToString(CultureInfo.InvariantCulture);
    private static bool TryZoneRef(string r, out int id)
    {
        id = 0;
        return r.StartsWith("Z:", StringComparison.Ordinal) && int.TryParse(r[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private static string Fp(Warehouse w) => SetupFingerprint.Of(w.Name.Trim(), w.Address, w.City, w.Country, w.IsActive);
    private static string Fp(Zone z) => SetupFingerprint.Of(z.Name.Trim(), z.Description, z.IsActive);

    // ── Preview ─────────────────────────────────────────────────────────────────

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var plan = Plan(payload, context.WizardInput, warnings);
        var codes = (await _db.Warehouses.IgnoreQueryFilters().AsNoTracking().Where(w => w.OrganizationId == Org).Select(w => w.Code).ToListAsync(ct))
            .Select(c => c.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (plan.Count == 0) warnings.Add("No warehouse to create: add one in the wizard or set defaultWarehouse.");
        return new SetupPreview
        {
            Category = Category, InPayload = plan.Count, AlreadyPresent = plan.Count(p => codes.Contains(p.Code)), Warnings = warnings
        };
    }

    // ── Apply / reset ───────────────────────────────────────────────────────────

    public Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, SetupResetMode.Merge, ct), ct);

    public Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, mode, ct), ct);

    private async Task<SetupApplyResult> SeedAsync(JsonElement payload, SetupSeedContext context, SetupResetMode mode, CancellationToken ct)
    {
        var warnings = new List<string>();
        var plan = Plan(payload, context.WizardInput, warnings);
        var types = ZoneTypes(payload).ToDictionary(t => t.Code, StringComparer.OrdinalIgnoreCase);
        var ours = context.AlreadySeeded.ToDictionary(s => s.NaturalKey, StringComparer.OrdinalIgnoreCase);
        var planned = plan.SelectMany(p => p.Zones.Select(z => ZoneKey(p.Code, z)).Append(WhKey(p.Code))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restore = mode != SetupResetMode.Merge;

        var warehouses = await _db.Warehouses.IgnoreQueryFilters().Where(w => w.OrganizationId == Org).ToListAsync(ct);
        var zones = await _db.Zones.IgnoreQueryFilters().Where(z => z.OrganizationId == Org).ToListAsync(ct);
        int created = 0, skipped = 0, removed = 0;
        var removedKeys = new List<string>();
        var seeded = new List<SeededRecordRef>();

        // Overwrite drops seeded records the plan no longer has; Full drops every seeded record (re-seeded below).
        if (restore)
        {
            foreach (var s in context.AlreadySeeded.OrderByDescending(s => s.NaturalKey.Contains('/')))
            {
                if (mode == SetupResetMode.Overwrite && planned.Contains(s.NaturalKey)) continue;
                if (TryZoneRef(s.EntityRef, out var zid))
                {
                    var z = zones.FirstOrDefault(x => x.Id == zid);
                    if (z is null) { removedKeys.Add(s.NaturalKey); continue; }
                    if (await ZoneUsedAsync(z.Id, ct)) z.IsActive = false;
                    else { _db.Zones.Remove(z); zones.Remove(z); removedKeys.Add(s.NaturalKey); }
                }
                else if (Guid.TryParse(s.EntityRef, out var uuid) && warehouses.FirstOrDefault(x => x.Uuid == uuid) is { } w)
                    w.IsActive = false; // never deleted (see summary)
                else { removedKeys.Add(s.NaturalKey); continue; }
                if (!planned.Contains(s.NaturalKey)) removed++;
            }
            await _db.SaveChangesAsync(ct);
            foreach (var k in removedKeys) ours.Remove(k);
        }

        var newZones = new List<(string Key, Zone Zone)>();
        foreach (var p in plan)
        {
            var key = WhKey(p.Code);
            var wh = warehouses.FirstOrDefault(w => string.Equals(w.Code.Trim(), p.Code, StringComparison.OrdinalIgnoreCase));
            var mine = wh is not null && ours.TryGetValue(key, out var r) && r.EntityRef == wh.Uuid.ToString();

            if (wh is null)
            {
                wh = new Warehouse
                {
                    Uuid = Guid.NewGuid(), OrganizationId = Org, Code = p.Code, Name = p.Name, Address = p.Address, City = p.City,
                    Country = p.Country, IsActive = true, CreatedDate = DateTime.UtcNow, CreatedBy = context.UserId ?? 0
                };
                _db.Warehouses.Add(wh);
                warehouses.Add(wh);
                seeded.Add(new SeededRecordRef(key, wh.Uuid.ToString(), Fp(wh)));
                mine = true;
                created++;
            }
            else if (mine && (restore || !wh.IsActive))
            {
                wh.Name = p.Name; wh.Address = p.Address; wh.City = p.City; wh.Country = p.Country; wh.IsActive = true;
                seeded.Add(new SeededRecordRef(key, wh.Uuid.ToString(), Fp(wh)));
                created++;
            }
            else
            {
                if (mine) seeded.Add(ours[key]);
                skipped++;
                // A warehouse the organization already had: only zones the wizard explicitly lists are added.
                if (!mine && !p.ExplicitZones) continue;
            }

            foreach (var zc in p.Zones)
            {
                var zkey = ZoneKey(p.Code, zc);
                var type = types[zc];
                var zone = wh.Id == 0 ? null : zones.FirstOrDefault(z => z.WarehouseId == wh.Id && string.Equals(z.Code?.Trim(), zc, StringComparison.OrdinalIgnoreCase));
                if (zone is null)
                {
                    zone = new Zone { Warehouse = wh, OrganizationId = Org, Code = zc, Name = type.Name, Description = type.Description, IsActive = true };
                    _db.Zones.Add(zone);
                    zones.Add(zone);
                    newZones.Add((zkey, zone));
                    created++;
                }
                else if (ours.TryGetValue(zkey, out var zr) && zr.EntityRef == ZoneRef(zone.Id))
                {
                    if (restore || !zone.IsActive)
                    {
                        zone.Name = type.Name; zone.Description = type.Description; zone.IsActive = true;
                        seeded.Add(new SeededRecordRef(zkey, ZoneRef(zone.Id), Fp(zone)));
                        created++;
                    }
                    else { seeded.Add(zr); skipped++; }
                }
                else skipped++; // the organization's own zone
            }
        }

        await _db.SaveChangesAsync(ct);
        seeded.AddRange(newZones.Select(z => new SeededRecordRef(z.Key, ZoneRef(z.Zone.Id), Fp(z.Zone))));
        return new SetupApplyResult
        {
            Category = Category, Created = created, Skipped = skipped, Removed = removed,
            Seeded = seeded, RemovedKeys = removedKeys, Warnings = warnings
        };
    }

    private async Task<bool> ZoneUsedAsync(int zoneId, CancellationToken ct) =>
        await _db.Racks.IgnoreQueryFilters().AnyAsync(r => r.ZoneId == zoneId, ct)
        || await _db.Bins.IgnoreQueryFilters().AnyAsync(b => b.ZoneId == zoneId, ct)
        || await _db.InventoryItems.IgnoreQueryFilters().AnyAsync(i => i.ZoneId == zoneId, ct);

    private async Task<bool> WarehouseUsedAsync(int warehouseId, CancellationToken ct) =>
        await _db.InventoryItems.IgnoreQueryFilters().AnyAsync(i => i.WarehouseId == warehouseId, ct)
        || await _db.InventoryLedgerEntries.IgnoreQueryFilters().AnyAsync(l => l.WarehouseId == warehouseId, ct);

    // ── Status / references ─────────────────────────────────────────────────────

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var warehouses = await _db.Warehouses.IgnoreQueryFilters().AsNoTracking().Where(w => w.OrganizationId == Org).ToListAsync(ct);
        var zones = await _db.Zones.IgnoreQueryFilters().AsNoTracking().Where(z => z.OrganizationId == Org).ToListAsync(ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            string? fp = TryZoneRef(s.EntityRef, out var zid)
                ? zones.FirstOrDefault(z => z.Id == zid) is { } z ? Fp(z) : null
                : Guid.TryParse(s.EntityRef, out var uuid) && warehouses.FirstOrDefault(w => w.Uuid == uuid) is { } w ? Fp(w) : null;
            if (fp is null) missing.Add(s.NaturalKey);
            else if (fp != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus
        {
            Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = warehouses.Count(w => w.IsActive),
            ModifiedKeys = modified, MissingKeys = missing
        };
    }

    /// <summary>Seeded warehouses with stock or ledger rows, and seeded zones with racks, bins or stock (BR-50-008).</summary>
    public async Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var keys = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            if (TryZoneRef(s.EntityRef, out var zid))
            {
                if (await ZoneUsedAsync(zid, ct)) keys.Add(s.NaturalKey);
            }
            else if (Guid.TryParse(s.EntityRef, out var uuid)
                     && await _db.Warehouses.IgnoreQueryFilters().Where(w => w.OrganizationId == Org && w.Uuid == uuid)
                         .Select(w => (int?)w.Id).FirstOrDefaultAsync(ct) is { } id
                     && await WarehouseUsedAsync(id, ct))
                keys.Add(s.NaturalKey);
        }
        return new SetupReferenceCheck { ReferencedCount = keys.Count, ReferencedKeys = keys };
    }
}
