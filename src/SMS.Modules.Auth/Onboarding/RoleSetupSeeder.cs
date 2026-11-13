using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Repositories;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Auth.Onboarding;

/// <summary>
/// A50 D-20 / D-55 — ROLES. Payload <c>{ roles: [{ code, name, description?, permissions: [code | "ALL"], requiredModules: [ModuleCode] }] }</c>.
/// <list type="bullet">
/// <item>A code that is a <b>global</b> role is reused as is (provenance Reused, EntityRef = RoleID). Global roles are never edited.</item>
/// <item>Otherwise an <b>org-owned custom role</b> is created with the pack's permissions, filtered by the modules enabled for
/// the organization (task 071) — a permission whose module (ModuleCodeMap) is off is dropped; a role whose required module
/// is off is not created. "ALL" = every permission except the platform-only ones. Super-admin-only codes are never granted.</item>
/// <item>An org role that already has the code is left alone (custom when this seeder did not create it).</item>
/// <item>Reset: Overwrite re-grants this seeder's roles from the pack; Full deletes this seeder's roles (only those no user
/// holds) and re-creates them. Custom roles are never touched.</item>
/// </list>
/// </summary>
internal sealed partial class RoleSetupSeeder : ISetupCategorySeeder
{
    private const string All = "ALL";

    private readonly AuthDbContext _db;
    private readonly ITenantContext _tenant;
    private readonly IModuleGate? _modules;

    public RoleSetupSeeder(AuthDbContext db, ITenantContext tenant, IModuleGate? modules = null)
    {
        _db = db;
        _tenant = tenant;
        _modules = modules;
    }

    public string Category => SetupCategories.Roles;
    public string? RequiresModule => null;

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9_]{0,49}$")]
    private static partial Regex ValidCode();

    private sealed record PackRole(string Code, string Name, string? Description, IReadOnlyList<string> Permissions, IReadOnlyList<string> RequiredModules);

    private static List<PackRole> Read(JsonElement payload) =>
        SetupJson.Array(payload, "roles")
            .Select(r => new PackRole(
                (SetupJson.Text(r, "code") ?? string.Empty).ToUpperInvariant(),
                SetupJson.Text(r, "name") ?? string.Empty,
                SetupJson.Text(r, "description"),
                SetupJson.Strings(r, "permissions").Select(p => p.ToUpperInvariant()).Distinct().ToList(),
                SetupJson.Strings(r, "requiredModules").Select(m => m.ToUpperInvariant()).Distinct().ToList()))
            .ToList();

    public IReadOnlyList<string> ValidatePayload(JsonElement payload)
    {
        var errors = new List<string>();
        if (SetupJson.Prop(payload, "roles") is not { ValueKind: JsonValueKind.Array })
            return ["roles: an array is required."];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        foreach (var r in Read(payload))
        {
            i++;
            if (!ValidCode().IsMatch(r.Code)) errors.Add($"roles[{i}].code: 1–50 capital letters, digits or '_'.");
            else if (!seen.Add(r.Code)) errors.Add($"roles[{i}].code: '{r.Code}' appears more than once.");
            if (r.Name.Length is 0 or > 100) errors.Add($"roles[{i}].name: required, at most 100 characters.");
        }
        return errors;
    }

    private Guid Org => _tenant.OrganizationId;

    private IQueryable<Role> VisibleRoles() =>
        _db.Roles.IgnoreQueryFilters().Where(r => r.IsGlobal || r.OrganizationId == Org);

    // ── Preview ─────────────────────────────────────────────────────────────────

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var pack = Read(payload);
        var roles = await VisibleRoles().AsNoTracking().Select(r => r.RoleCode).ToListAsync(ct);
        var codes = roles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var enabled = await EnabledModulesAsync(pack.SelectMany(p => p.RequiredModules), ct);
        foreach (var r in pack.Where(r => !codes.Contains(r.Code)))
            if (r.RequiredModules.FirstOrDefault(m => !enabled.Contains(m)) is { } off)
                warnings.Add($"{r.Code}: not created — module {off} is not enabled.");
        return new SetupPreview
        {
            Category = Category, InPayload = pack.Count, AlreadyPresent = pack.Count(r => codes.Contains(r.Code)), Warnings = warnings
        };
    }

    // ── Apply / reset ───────────────────────────────────────────────────────────

    public Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, overwrite: false, removed: [], removedCount: 0, extraWarnings: [], ct), ct);

    public Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, async () =>
        {
            if (mode == SetupResetMode.Merge)
                return await SeedAsync(payload, context, overwrite: false, removed: [], removedCount: 0, extraWarnings: [], ct);

            var pack = Read(payload).Select(r => r.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var removedKeys = new List<string>();
            var warnings = new List<string>();
            foreach (var seeded in context.AlreadySeeded.Where(s => !s.Reused))
            {
                // Full deletes every role this seeder created; Overwrite only those the pack no longer has.
                if (mode == SetupResetMode.Overwrite && pack.Contains(seeded.NaturalKey)) continue;
                var role = await OwnSeededRoleAsync(seeded, ct);
                if (role is null) { removedKeys.Add(seeded.NaturalKey); continue; }
                if (await HeldAsync(role.RoleID, ct))
                {
                    warnings.Add($"{role.RoleCode}: kept — users hold this role.");
                    continue;
                }
                _db.RolePermissions.RemoveRange(await _db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.RoleID == role.RoleID).ToListAsync(ct));
                _db.Roles.Remove(role);
                removedKeys.Add(seeded.NaturalKey);
            }
            await _db.SaveChangesAsync(ct);

            var remaining = context.AlreadySeeded.Where(s => !removedKeys.Contains(s.NaturalKey, StringComparer.OrdinalIgnoreCase)).ToList();
            var next = new SetupSeedContext
            {
                CountryCode = context.CountryCode, IndustryType = context.IndustryType, Version = context.Version,
                UserId = context.UserId, WizardInput = context.WizardInput, AlreadySeeded = remaining
            };
            return await SeedAsync(payload, next, overwrite: true, removedKeys, removedKeys.Count, warnings, ct);
        }, ct);

    private async Task<SetupApplyResult> SeedAsync(
        JsonElement payload, SetupSeedContext context, bool overwrite, List<string> removed, int removedCount, List<string> extraWarnings,
        CancellationToken ct)
    {
        var pack = Read(payload);
        var warnings = new List<string>(extraWarnings);
        var seededRefs = new List<SeededRecordRef>();
        var created = 0;
        var skipped = 0;

        var visible = await VisibleRoles().ToListAsync(ct);
        var permissions = await _db.Permissions.AsNoTracking().Select(p => new { p.PermissionID, p.Code }).ToListAsync(ct);
        var permissionByCode = permissions.ToDictionary(p => p.Code, p => p.PermissionID, StringComparer.OrdinalIgnoreCase);
        var permissionModules = permissions.Select(p => ModuleCodeMap.ForPermission(p.Code)).OfType<string>();
        var enabled = await EnabledModulesAsync(pack.SelectMany(p => p.RequiredModules).Concat(permissionModules), ct);
        var ours = context.AlreadySeeded.Where(s => !s.Reused).ToDictionary(s => s.NaturalKey, StringComparer.OrdinalIgnoreCase);

        foreach (var r in pack)
        {
            var global = visible.FirstOrDefault(x => x.IsGlobal && string.Equals(x.RoleCode, r.Code, StringComparison.OrdinalIgnoreCase));
            if (global is not null)
            {
                // D-20 — a global role is reused, never copied or edited.
                seededRefs.Add(new SeededRecordRef(r.Code, Id(global.RoleID), RoleFingerprint(global.Name, null), Reused: true));
                skipped++;
                continue;
            }

            var own = visible.FirstOrDefault(x => !x.IsGlobal && string.Equals(x.RoleCode, r.Code, StringComparison.OrdinalIgnoreCase));
            var wanted = WantedPermissions(r, permissionByCode, enabled, warnings);

            if (own is not null)
            {
                if (!ours.ContainsKey(r.Code))
                {
                    // The organization's own role with that code: custom, left alone.
                    skipped++;
                    continue;
                }
                if (overwrite)
                {
                    own.Name = r.Name;
                    own.Description = r.Description;
                    own.IsActive = true;
                    await ReplaceGrantsAsync(own.RoleID, wanted.Select(c => permissionByCode[c]).ToList(), ct);
                    seededRefs.Add(new SeededRecordRef(r.Code, Id(own.RoleID), RoleFingerprint(r.Name, wanted)));
                    created++;
                }
                else
                {
                    seededRefs.Add(new SeededRecordRef(r.Code, Id(own.RoleID), ours[r.Code].Fingerprint));
                    skipped++;
                }
                continue;
            }

            if (r.RequiredModules.FirstOrDefault(m => !enabled.Contains(m)) is { } off)
            {
                warnings.Add($"{r.Code}: not created — module {off} is not enabled.");
                skipped++;
                continue;
            }
            if (visible.Any(x => string.Equals(x.Name.Trim(), r.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"{r.Code}: not created — a role named '{r.Name}' already exists.");
                skipped++;
                continue;
            }

            var roleId = await NextRoleIdAsync(ct);
            // A recycled id must not inherit grants left behind by a deleted role (AuthRepository.CreateRoleAsync precedent).
            _db.RolePermissions.RemoveRange(await _db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.RoleID == roleId).ToListAsync(ct));
            var role = new Role
            {
                RoleID = roleId, Name = r.Name, RoleCode = r.Code, Description = r.Description,
                IsActive = true, IsGlobal = false, OrganizationId = Org
            };
            _db.Roles.Add(role);
            visible.Add(role);
            await _db.SaveChangesAsync(ct);
            await ReplaceGrantsAsync(roleId, wanted.Select(c => permissionByCode[c]).ToList(), ct);
            seededRefs.Add(new SeededRecordRef(r.Code, Id(roleId), RoleFingerprint(r.Name, wanted)));
            created++;
        }

        await _db.SaveChangesAsync(ct);
        return new SetupApplyResult
        {
            Category = Category, Created = created, Skipped = skipped, Removed = removedCount,
            Seeded = seededRefs, RemovedKeys = removed, Warnings = warnings
        };
    }

    /// <summary>The pack's codes that exist, whose module is on, minus the platform-only ones. "ALL" = every such code.</summary>
    private static List<string> WantedPermissions(
        PackRole r, IReadOnlyDictionary<string, int> permissionByCode, IReadOnlySet<string> enabled, List<string> warnings)
    {
        var requested = r.Permissions.Contains(All) ? permissionByCode.Keys.ToList() : r.Permissions.ToList();
        var unknown = requested.Where(c => !permissionByCode.ContainsKey(c)).ToList();
        if (unknown.Count > 0) warnings.Add($"{r.Code}: unknown permission codes dropped: {string.Join(", ", unknown)}.");

        var everyRole = AuthDataSeeder.EveryRoleCodes;
        return requested.Concat(everyRole)
            .Where(permissionByCode.ContainsKey)
            .Where(c => !Repositories.AuthRepository.SuperAdminOnlyPermissionCodes.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Where(c => ModuleCodeMap.ForPermission(c) is not { } m || enabled.Contains(m))
            .Select(c => c.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
    }

    private async Task ReplaceGrantsAsync(int roleId, IReadOnlyCollection<int> allowed, CancellationToken ct)
    {
        var existing = await _db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.RoleID == roleId).ToListAsync(ct);
        foreach (var rp in existing) rp.IsAllowed = allowed.Contains(rp.PermissionID);
        foreach (var id in allowed.Where(id => existing.All(rp => rp.PermissionID != id)))
            _db.RolePermissions.Add(new RolePermission { RoleID = roleId, PermissionID = id, IsAllowed = true, OrganizationId = Org });
    }

    /// <summary>
    /// RoleID has no identity (RoleMap.ValueGeneratedNever). Next free id after every role, grant and user — a grant or a
    /// user left on a deleted role's id must not be inherited (AuthDataSeeder "taken" rule).
    /// </summary>
    private async Task<int> NextRoleIdAsync(CancellationToken ct)
    {
        var maxRole  = await _db.Roles.IgnoreQueryFilters().MaxAsync(r => (int?)r.RoleID, ct) ?? 0;
        var maxGrant = await _db.RolePermissions.IgnoreQueryFilters().MaxAsync(rp => (int?)rp.RoleID, ct) ?? 0;
        var maxUser  = await _db.UserAccounts.IgnoreQueryFilters().MaxAsync(u => (int?)u.RoleID, ct) ?? 0;
        var tracked  = _db.ChangeTracker.Entries<Role>().Select(e => e.Entity.RoleID).DefaultIfEmpty(0).Max();
        return Math.Max(Math.Max(maxRole, maxGrant), Math.Max(maxUser, tracked)) + 1;
    }

    private async Task<IReadOnlySet<string>> EnabledModulesAsync(IEnumerable<string> modules, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in modules.Distinct(StringComparer.OrdinalIgnoreCase))
            if (_modules is null || await _modules.IsEnabledAsync(Org, m, ct)) set.Add(m);
        return set;
    }

    private Task<bool> HeldAsync(int roleId, CancellationToken ct) =>
        _db.UserAccounts.IgnoreQueryFilters().AnyAsync(u => u.RoleID == roleId && !u.IsDelete, ct);

    private async Task<Role?> OwnSeededRoleAsync(SeededRecordRef seeded, CancellationToken ct) =>
        int.TryParse(seeded.EntityRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? await _db.Roles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.RoleID == id && !r.IsGlobal && r.OrganizationId == Org, ct)
            : null;

    private static string Id(int roleId) => roleId.ToString(CultureInfo.InvariantCulture);

    private static string RoleFingerprint(string name, IReadOnlyCollection<string>? grants) =>
        SetupFingerprint.Of(name.Trim(), grants is null ? null : string.Join(",", grants.OrderBy(c => c, StringComparer.Ordinal)));

    // ── Status / references ─────────────────────────────────────────────────────

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            if (!int.TryParse(s.EntityRef, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) { missing.Add(s.NaturalKey); continue; }
            var role = await VisibleRoles().AsNoTracking().FirstOrDefaultAsync(r => r.RoleID == id, ct);
            if (role is null) { missing.Add(s.NaturalKey); continue; }
            var current = s.Reused ? RoleFingerprint(role.Name, null) : RoleFingerprint(role.Name, await GrantedCodesAsync(id, ct));
            if (current != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus
        {
            Category = Category,
            SeededCount = context.AlreadySeeded.Count,
            CurrentCount = await _db.Roles.IgnoreQueryFilters().CountAsync(r => !r.IsGlobal && r.OrganizationId == Org, ct),
            ModifiedKeys = modified,
            MissingKeys = missing
        };
    }

    private async Task<List<string>> GrantedCodesAsync(int roleId, CancellationToken ct) =>
        await (from rp in _db.RolePermissions.IgnoreQueryFilters()
               join p in _db.Permissions on rp.PermissionID equals p.PermissionID
               where rp.RoleID == roleId && rp.IsAllowed
               select p.Code).ToListAsync(ct);

    /// <summary>Seeded org roles that a user holds — Full reset keeps those (D-20). Reused global roles are never deleted.</summary>
    public async Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var keys = new List<string>();
        foreach (var s in context.AlreadySeeded.Where(s => !s.Reused))
            if (await OwnSeededRoleAsync(s, ct) is { } role && await HeldAsync(role.RoleID, ct))
                keys.Add(s.NaturalKey);
        return new SetupReferenceCheck { ReferencedCount = keys.Count, ReferencedKeys = keys };
    }
}
