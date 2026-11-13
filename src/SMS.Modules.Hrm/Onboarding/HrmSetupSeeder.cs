using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Hrm.Onboarding;

/// <summary>
/// A50 D-30 / D-90 — the HRM category (task 058). Payload <c>{ verified:false, leaveTypes:[…], salaryComponents:[…], taxSlabTables:[…] }</c>
/// (PAYLOAD-SCHEMAS.md). The HRM module owns the tables, so the module that already seeds them on first use (<see cref="Services.HrmSeeder"/>,
/// values in HrmSeedData) also answers the wizard; a test keeps the PK pack and HrmSeedData in step.
/// <list type="bullet">
/// <item>Natural keys: <c>LEAVE/CODE</c>, <c>COMPONENT/CODE</c>, <c>TAX/CODE</c>; EntityRef = the row's Uuid.</item>
/// <item><b>Nothing here verifies anything (A49 G-21).</b> A payload that says <c>verified:true</c> is invalid, a tax table is always created
/// IsVerified = false, and no path ever writes IsVerified, VerifiedBy or VerifiedAt. A table a person has verified is never rewritten or deleted.</item>
/// <item>A row with the pack's code is adopted (provenance, not rewritten) when its values already equal the pack's — that is what HRM's own first-use seed
/// leaves behind — and is the organization's own row otherwise.</item>
/// <item>Reset: Overwrite restores seeded rows to the pack and removes the seeded rows the pack dropped; Full removes every seeded row and re-creates the
/// pack's. A leave type with requests or ledger entries, a component used by an assignment, a structure other than the default one or a payroll line, and
/// a verified tax table are kept and reported (BR-50-008).</item>
/// </list>
/// </summary>
internal sealed class HrmSetupSeeder : ISetupCategorySeeder
{
    internal const string DefaultStructureCode = "STD_MONTHLY";

    private readonly HrmDbContext _db;
    private readonly ITenantContext _tenant;

    public HrmSetupSeeder(HrmDbContext db, ITenantContext tenant)
    {
        _db = db;
        _tenant = tenant;
    }

    public string Category => SetupCategories.Hrm;
    public string? RequiresModule => ModuleCodes.Hrm;

    private Guid Org => _tenant.OrganizationId;

    // ── Payload ─────────────────────────────────────────────────────────────────

    internal sealed record LeaveSpec(string Code, string Name, decimal Days, bool IsPaid, decimal? Cap, int? AttachAfter, string? Gender, string Accrual, DateOnly From)
    {
        public string Key => "LEAVE/" + Code;
    }

    internal sealed record ComponentSpec(string Code, string Name, string Type, bool Taxable, bool InGross, string Calc, string? Posting, string? Expense, string? Formula, int Sort)
    {
        public string Key => "COMPONENT/" + Code;
    }

    internal sealed record SlabSpec(decimal From, decimal? To, decimal Fixed, decimal Rate);

    internal sealed record TaxSpec(string Code, string Name, string Country, DateOnly From, DateOnly? To, string? Note, IReadOnlyList<SlabSpec> Slabs)
    {
        public string Key => "TAX/" + Code;
    }

    private static DateOnly? Date(JsonElement e, string name) =>
        SetupJson.Text(e, name) is { } t && DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Up(string? s) => (s ?? string.Empty).Trim().ToUpperInvariant();

    internal static List<LeaveSpec> Leaves(JsonElement p) =>
        SetupJson.Array(p, "leaveTypes").Select(l => new LeaveSpec(
            Up(SetupJson.Text(l, "code")), SetupJson.Text(l, "name") ?? string.Empty, SetupJson.Decimal(l, "entitlementDays") ?? 0m,
            SetupJson.Bool(l, "isPaid") ?? true, SetupJson.Decimal(l, "carryForwardCap"), SetupJson.Int(l, "attachmentAfterDays"),
            SetupJson.Text(l, "genderRestriction"), SetupJson.Text(l, "accrualMethod") ?? LeaveAccrualMethods.Annual,
            Date(l, "effectiveFrom") ?? new DateOnly(DateTime.UtcNow.Year, 1, 1))).ToList();

    internal static List<ComponentSpec> Components(JsonElement p) =>
        SetupJson.Array(p, "salaryComponents").Select((c, i) => new ComponentSpec(
            Up(SetupJson.Text(c, "code")), SetupJson.Text(c, "name") ?? string.Empty, SetupJson.Text(c, "type") ?? string.Empty,
            SetupJson.Bool(c, "isTaxable") ?? false, SetupJson.Bool(c, "inGross") ?? false, SetupJson.Text(c, "calcMethod") ?? SalaryCalcMethods.Fixed,
            SetupJson.Text(c, "postingRole"), SetupJson.Text(c, "expenseRole"), SetupJson.Text(c, "formula"), SetupJson.Int(c, "sortOrder") ?? (i + 1) * 10)).ToList();

    internal static List<TaxSpec> Taxes(JsonElement p) =>
        SetupJson.Array(p, "taxSlabTables").Select(t => new TaxSpec(
            (SetupJson.Text(t, "code") ?? string.Empty).Trim(), SetupJson.Text(t, "name") ?? string.Empty,
            Up(SetupJson.Text(t, "countryCode") ?? "PK"), Date(t, "effectiveFrom") ?? default, Date(t, "effectiveTo"), SetupJson.Text(t, "sourceNote"),
            SetupJson.Array(t, "slabs").Select(s => new SlabSpec(
                SetupJson.Decimal(s, "from") ?? 0m, SetupJson.Decimal(s, "to"), SetupJson.Decimal(s, "fixedAmount") ?? 0m, SetupJson.Decimal(s, "ratePercent") ?? 0m))
                .OrderBy(s => s.From).ToList())).ToList();

    public IReadOnlyList<string> ValidatePayload(JsonElement payload)
    {
        var errors = new List<string>();
        if (payload.ValueKind != JsonValueKind.Object) return ["The payload must be an object."];
        // G-21: seeding never verifies. A payload claiming otherwise is refused, not quietly downgraded.
        if (SetupJson.Bool(payload, "verified") != false)
            errors.Add("verified: must be false. HR defaults are never verified by seeding; a person with the statutory-verify permission does that in HR.");

        var leaves = Leaves(payload);
        var components = Components(payload);
        var taxes = Taxes(payload);
        if (leaves.Count + components.Count + taxes.Count == 0) errors.Add("The payload has no leave types, salary components or tax tables.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in leaves)
        {
            if (l.Code.Length is 0 or > 20) errors.Add($"leaveTypes: code '{l.Code}' must be 1-20 characters.");
            else if (!seen.Add(l.Key)) errors.Add($"leaveTypes: '{l.Code}' appears more than once.");
            if (l.Name.Length is 0 or > 100) errors.Add($"leaveTypes {l.Code}: name is required (at most 100 characters).");
            if (l.Days < 0) errors.Add($"leaveTypes {l.Code}: entitlementDays cannot be negative.");
            if (l.Cap < 0) errors.Add($"leaveTypes {l.Code}: carryForwardCap cannot be negative.");
            if (!LeaveAccrualMethods.All.Contains(l.Accrual)) errors.Add($"leaveTypes {l.Code}: accrualMethod must be one of {string.Join(", ", LeaveAccrualMethods.All)}.");
            if (l.Gender is not null && !Genders.All.Contains(l.Gender)) errors.Add($"leaveTypes {l.Code}: genderRestriction must be one of {string.Join(", ", Genders.All)}.");
        }
        foreach (var c in components)
        {
            if (c.Code.Length is 0 or > 20) errors.Add($"salaryComponents: code '{c.Code}' must be 1-20 characters.");
            else if (!seen.Add(c.Key)) errors.Add($"salaryComponents: '{c.Code}' appears more than once.");
            if (c.Name.Length is 0 or > 100) errors.Add($"salaryComponents {c.Code}: name is required (at most 100 characters).");
            if (!SalaryComponentTypes.All.Contains(c.Type)) errors.Add($"salaryComponents {c.Code}: type must be one of {string.Join(", ", SalaryComponentTypes.All)}.");
            if (!SalaryCalcMethods.All.Contains(c.Calc)) errors.Add($"salaryComponents {c.Code}: calcMethod must be one of {string.Join(", ", SalaryCalcMethods.All)}.");
            else if (c.Calc == SalaryCalcMethods.Percent) errors.Add($"salaryComponents {c.Code}: a Percent component needs a base component; set that up in HR, not in the defaults.");
            else if (c.Calc == SalaryCalcMethods.Formula && string.IsNullOrWhiteSpace(c.Formula)) errors.Add($"salaryComponents {c.Code}: a Formula component needs a formula.");
            if (c.Posting?.Length > 50 || c.Expense?.Length > 50) errors.Add($"salaryComponents {c.Code}: posting roles are at most 50 characters.");
            if (c.Formula?.Length > 1000) errors.Add($"salaryComponents {c.Code}: formula is at most 1000 characters.");
        }
        foreach (var t in taxes)
        {
            if (t.Code.Length is 0 or > 30) errors.Add($"taxSlabTables: code '{t.Code}' must be 1-30 characters.");
            else if (!seen.Add(t.Key)) errors.Add($"taxSlabTables: '{t.Code}' appears more than once.");
            if (t.Name.Length is 0 or > 100) errors.Add($"taxSlabTables {t.Code}: name is required (at most 100 characters).");
            if (t.Country.Length is < 2 or > 3) errors.Add($"taxSlabTables {t.Code}: countryCode must be 2-3 letters.");
            if (t.From == default) errors.Add($"taxSlabTables {t.Code}: effectiveFrom (yyyy-MM-dd) is required.");
            if (t.To is { } to && to < t.From) errors.Add($"taxSlabTables {t.Code}: effectiveTo is before effectiveFrom.");
            if (t.Note?.Length > 500) errors.Add($"taxSlabTables {t.Code}: sourceNote is at most 500 characters.");
            if (t.Slabs.Count == 0) { errors.Add($"taxSlabTables {t.Code}: at least one slab is required."); continue; }
            if (t.Slabs[0].From != 0m) errors.Add($"taxSlabTables {t.Code}: the first slab must start at 0.");
            for (var i = 0; i < t.Slabs.Count; i++)
            {
                var s = t.Slabs[i];
                if (s.Rate is < 0 or > 100) errors.Add($"taxSlabTables {t.Code}: slab {i + 1} rate must be 0-100.");
                if (s.To is null && i < t.Slabs.Count - 1) errors.Add($"taxSlabTables {t.Code}: only the last slab may have no upper limit.");
                if (s.To is { } upper && upper <= s.From) errors.Add($"taxSlabTables {t.Code}: slab {i + 1} must end above where it starts.");
                if (i > 0 && t.Slabs[i - 1].To != s.From) errors.Add($"taxSlabTables {t.Code}: slab {i + 1} must start where the previous one ends.");
            }
        }
        return errors;
    }

    // ── Fingerprints (what "unchanged" means; IsVerified is deliberately not part of them) ──

    private static string Fp(LeaveSpec l) =>
        SetupFingerprint.Of(l.Name.Trim(), l.IsPaid, l.Cap, l.AttachAfter, l.Gender, true, l.Days, l.Accrual);

    private static string Fp(ComponentSpec c) =>
        SetupFingerprint.Of(c.Name.Trim(), c.Type, c.Taxable, c.InGross, c.Calc, c.Posting, c.Expense, c.Formula, c.Sort, true);

    private static string SlabText(IEnumerable<SlabSpec> slabs) =>
        string.Join(";", slabs.OrderBy(s => s.From).Select(s => string.Join("|",
            s.From.ToString("0.##", CultureInfo.InvariantCulture), s.To?.ToString("0.##", CultureInfo.InvariantCulture) ?? "-",
            s.Fixed.ToString("0.##", CultureInfo.InvariantCulture), s.Rate.ToString("0.####", CultureInfo.InvariantCulture))));

    private static string Fp(TaxSpec t) =>
        SetupFingerprint.Of(t.Name.Trim(), t.Country, t.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            t.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), SlabText(t.Slabs));

    private static LeaveSpec Spec(LeaveType l) =>
        new(l.Code, l.Name, l.Policies.OrderBy(p => p.EffectiveFrom).LastOrDefault(p => p.EffectiveTo == null)?.Entitlement ?? 0m, l.IsPaid, l.CarryForwardCap,
            l.AttachmentAfterDays, l.GenderRestriction, l.Policies.OrderBy(p => p.EffectiveFrom).LastOrDefault(p => p.EffectiveTo == null)?.AccrualMethod ?? LeaveAccrualMethods.Annual,
            default);

    private static string CurrentFp(LeaveType l) => l.IsActive ? Fp(Spec(l)) : SetupFingerprint.Of("inactive", l.Id);

    private static string CurrentFp(SalaryComponent c) =>
        c.IsActive ? Fp(new ComponentSpec(c.Code, c.Name, c.Type, c.IsTaxable, c.InGross, c.CalcMethod, c.PostingRole, c.ExpenseRole, c.Formula, c.SortOrder))
                   : SetupFingerprint.Of("inactive", c.Id);

    private static string CurrentFp(TaxSlabTable t) =>
        Fp(new TaxSpec(t.Code, t.Name, t.CountryCode, t.EffectiveFrom, t.EffectiveTo, t.SourceNote,
            t.Slabs.Select(s => new SlabSpec(s.FromAmount, s.ToAmount, s.FixedAmount, s.RatePercent)).ToList()));

    // ── Reads ───────────────────────────────────────────────────────────────────

    private Task<List<LeaveType>> LeaveRowsAsync(bool track, CancellationToken ct)
    {
        var q = _db.LeaveTypes.IgnoreQueryFilters().Include(l => l.Policies).Where(l => l.OrganizationId == Org);
        return (track ? q : q.AsNoTracking()).ToListAsync(ct);
    }

    private Task<List<SalaryComponent>> ComponentRowsAsync(bool track, CancellationToken ct)
    {
        var q = _db.SalaryComponents.IgnoreQueryFilters().Where(c => c.OrganizationId == Org);
        return (track ? q : q.AsNoTracking()).ToListAsync(ct);
    }

    private Task<List<TaxSlabTable>> TaxRowsAsync(bool track, CancellationToken ct)
    {
        var q = _db.TaxSlabTables.IgnoreQueryFilters().Include(t => t.Slabs).Where(t => t.OrganizationId == Org);
        return (track ? q : q.AsNoTracking()).ToListAsync(ct);
    }

    // ── Preview ─────────────────────────────────────────────────────────────────

    public async Task<SetupPreview> PreviewAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default)
    {
        var leaves = Leaves(payload);
        var components = Components(payload);
        var taxes = Taxes(payload);
        var leaveRows = (await LeaveRowsAsync(false, ct)).Select(l => l.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compRows = (await ComponentRowsAsync(false, ct)).Select(c => c.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taxRows = (await TaxRowsAsync(false, ct)).Select(t => t.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new SetupPreview
        {
            Category = Category,
            InPayload = leaves.Count + components.Count + taxes.Count,
            AlreadyPresent = leaves.Count(l => leaveRows.Contains(l.Code)) + components.Count(c => compRows.Contains(c.Code)) + taxes.Count(t => taxRows.Contains(t.Code)),
            Warnings = Warnings(taxes)
        };
    }

    private static List<string> Warnings(IReadOnlyList<TaxSpec> taxes)
    {
        var warnings = new List<string>();
        if (taxes.Count > 0)
            warnings.Add("Tax slabs are UNVERIFIED seed data. Payroll cannot be finalized until someone with the statutory-verify permission has checked them against the current notification in HR.");
        return warnings;
    }

    // ── Apply / reset ───────────────────────────────────────────────────────────

    public Task<SetupApplyResult> ApplyAsync(JsonElement payload, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, SetupResetMode.Merge, ct), ct);

    public Task<SetupApplyResult> ResetAsync(JsonElement payload, SetupResetMode mode, SetupSeedContext context, CancellationToken ct = default) =>
        SetupUnitOfWork.RunAsync(_db, () => SeedAsync(payload, context, mode, ct), ct);

    private async Task<SetupApplyResult> SeedAsync(JsonElement payload, SetupSeedContext context, SetupResetMode mode, CancellationToken ct)
    {
        var leaves = Leaves(payload);
        var components = Components(payload);
        var taxes = Taxes(payload);
        var packKeys = leaves.Select(l => l.Key).Concat(components.Select(c => c.Key)).Concat(taxes.Select(t => t.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ours = context.AlreadySeeded.ToDictionary(s => s.NaturalKey, StringComparer.OrdinalIgnoreCase);
        var warnings = Warnings(taxes);
        var removedKeys = new List<string>();
        var removed = 0;
        var user = context.UserId;

        var leaveRows = await LeaveRowsAsync(true, ct);
        var compRows = await ComponentRowsAsync(true, ct);
        var taxRows = await TaxRowsAsync(true, ct);

        if (mode != SetupResetMode.Merge)
        {
            var blocked = await ReferencedAsync(context.AlreadySeeded, leaveRows, compRows, taxRows, ct);
            foreach (var s in context.AlreadySeeded)
            {
                if (mode == SetupResetMode.Overwrite && packKeys.Contains(s.NaturalKey)) continue;
                if (blocked.TryGetValue(s.NaturalKey, out var why)) { warnings.Add($"{s.NaturalKey}: kept — {why}."); continue; }
                var uuid = Guid.TryParse(s.EntityRef, out var g) ? g : Guid.Empty;
                if (s.NaturalKey.StartsWith("LEAVE/", StringComparison.Ordinal))
                {
                    if (leaveRows.FirstOrDefault(l => l.Uuid == uuid) is { } row) { _db.LeaveTypes.Remove(row); leaveRows.Remove(row); }
                }
                else if (s.NaturalKey.StartsWith("COMPONENT/", StringComparison.Ordinal))
                {
                    if (compRows.FirstOrDefault(c => c.Uuid == uuid) is { } row)
                    {
                        // The default structure lists the component; with no assignment on it, the line goes with it.
                        _db.SalaryStructureLines.RemoveRange(await _db.SalaryStructureLines.IgnoreQueryFilters()
                            .Where(l => l.OrganizationId == Org && l.ComponentId == row.Id).ToListAsync(ct));
                        _db.SalaryComponents.Remove(row);
                        compRows.Remove(row);
                    }
                }
                else if (taxRows.FirstOrDefault(t => t.Uuid == uuid) is { } row) { _db.TaxSlabTables.Remove(row); taxRows.Remove(row); }
                removedKeys.Add(s.NaturalKey);
                if (!packKeys.Contains(s.NaturalKey)) removed++;
            }
            await _db.SaveChangesAsync(ct);
            foreach (var k in removedKeys) ours.Remove(k);
        }

        var seeded = new List<SeededRecordRef>();
        var pending = new List<(string Key, Func<string> Ref, Func<string> Fp)>();
        int created = 0, skipped = 0;
        var restore = mode != SetupResetMode.Merge;

        foreach (var spec in leaves)
        {
            var row = leaveRows.FirstOrDefault(l => string.Equals(l.Code, spec.Code, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new LeaveType { OrganizationId = Org, Code = spec.Code, IsSeeded = true, CreatedBy = user, ModifiedBy = user };
                Apply(row, spec, user);
                _db.LeaveTypes.Add(row);
                leaveRows.Add(row);
                created++;
                pending.Add((spec.Key, () => row.Uuid.ToString(), () => Fp(spec)));
            }
            else if (ours.TryGetValue(spec.Key, out var mine) && mine.EntityRef == row.Uuid.ToString())
            {
                if (restore) { Apply(row, spec, user); created++; seeded.Add(new SeededRecordRef(spec.Key, row.Uuid.ToString(), Fp(spec))); }
                else { skipped++; seeded.Add(mine); }
            }
            else if (CurrentFp(row) == Fp(spec)) { skipped++; seeded.Add(new SeededRecordRef(spec.Key, row.Uuid.ToString(), Fp(spec))); }   // HRM's own first-use seed: adopted
            else skipped++;   // the organization's own leave type with that code
        }

        foreach (var spec in components)
        {
            var row = compRows.FirstOrDefault(c => string.Equals(c.Code, spec.Code, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new SalaryComponent { OrganizationId = Org, Code = spec.Code, IsSystem = true, CreatedBy = user, ModifiedBy = user };
                Apply(row, spec, user);
                _db.SalaryComponents.Add(row);
                compRows.Add(row);
                created++;
                pending.Add((spec.Key, () => row.Uuid.ToString(), () => Fp(spec)));
            }
            else if (ours.TryGetValue(spec.Key, out var mine) && mine.EntityRef == row.Uuid.ToString())
            {
                if (restore) { Apply(row, spec, user); created++; seeded.Add(new SeededRecordRef(spec.Key, row.Uuid.ToString(), Fp(spec))); }
                else { skipped++; seeded.Add(mine); }
            }
            else if (CurrentFp(row) == Fp(spec)) { skipped++; seeded.Add(new SeededRecordRef(spec.Key, row.Uuid.ToString(), Fp(spec))); }
            else skipped++;
        }

        foreach (var spec in taxes)
        {
            var row = taxRows.FirstOrDefault(t => string.Equals(t.Code, spec.Code, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new TaxSlabTable { OrganizationId = Org, Code = spec.Code, IsVerified = false, CreatedBy = user, ModifiedBy = user };
                ApplyTax(row, spec, user);
                _db.TaxSlabTables.Add(row);
                taxRows.Add(row);
                created++;
                pending.Add((spec.Key, () => row.Uuid.ToString(), () => Fp(spec)));
            }
            else if (ours.TryGetValue(spec.Key, out var mine) && mine.EntityRef == row.Uuid.ToString())
            {
                if (restore && row.IsVerified)
                {
                    warnings.Add($"{spec.Key}: left as it is — a person has verified this table, so it is not rewritten.");
                    skipped++;
                    seeded.Add(mine);
                }
                else if (restore)
                {
                    _db.TaxSlabs.RemoveRange(row.Slabs.ToList());
                    row.Slabs.Clear();
                    ApplyTax(row, spec, user);
                    created++;
                    seeded.Add(new SeededRecordRef(spec.Key, row.Uuid.ToString(), Fp(spec)));
                }
                else { skipped++; seeded.Add(mine); }
            }
            else if (CurrentFp(row) == Fp(spec)) { skipped++; seeded.Add(new SeededRecordRef(spec.Key, row.Uuid.ToString(), Fp(spec))); }
            else skipped++;
        }

        await _db.SaveChangesAsync(ct);
        seeded.AddRange(pending.Select(p => new SeededRecordRef(p.Key, p.Ref(), p.Fp())));

        return new SetupApplyResult
        {
            Category = Category, Created = created, Skipped = skipped, Removed = removed, Seeded = seeded,
            RemovedKeys = removedKeys.Where(k => !seeded.Any(s => string.Equals(s.NaturalKey, k, StringComparison.OrdinalIgnoreCase))).ToList(),
            Warnings = warnings
        };
    }

    private static void Apply(LeaveType row, LeaveSpec spec, int? user)
    {
        row.Name = spec.Name.Trim();
        row.IsPaid = spec.IsPaid;
        row.CarryForwardCap = spec.Cap;
        row.AttachmentAfterDays = spec.AttachAfter;
        row.GenderRestriction = spec.Gender;
        row.IsActive = true;
        row.ModifiedAt = DateTime.UtcNow;
        row.ModifiedBy = user;
        var policy = row.Policies.OrderBy(p => p.EffectiveFrom).LastOrDefault(p => p.EffectiveTo == null);
        if (policy is null)
            row.Policies.Add(new LeavePolicy { OrganizationId = row.OrganizationId, Entitlement = spec.Days, AccrualMethod = spec.Accrual, EffectiveFrom = spec.From, CreatedBy = user, ModifiedBy = user });
        else { policy.Entitlement = spec.Days; policy.AccrualMethod = spec.Accrual; policy.ModifiedAt = DateTime.UtcNow; policy.ModifiedBy = user; }
    }

    private static void Apply(SalaryComponent row, ComponentSpec spec, int? user)
    {
        row.Name = spec.Name.Trim();
        row.Type = spec.Type;
        row.IsTaxable = spec.Taxable;
        row.InGross = spec.InGross;
        row.CalcMethod = spec.Calc;
        row.PostingRole = spec.Posting;
        row.ExpenseRole = spec.Expense;
        row.Formula = spec.Formula;
        row.SortOrder = spec.Sort;
        row.IsActive = true;
        row.ModifiedAt = DateTime.UtcNow;
        row.ModifiedBy = user;
    }

    /// <summary>Never touches IsVerified / VerifiedBy / VerifiedAt (G-21).</summary>
    private static void ApplyTax(TaxSlabTable row, TaxSpec spec, int? user)
    {
        row.Name = spec.Name.Trim();
        row.CountryCode = spec.Country;
        row.EffectiveFrom = spec.From;
        row.EffectiveTo = spec.To;
        row.SourceNote = spec.Note;
        row.ModifiedAt = DateTime.UtcNow;
        row.ModifiedBy = user;
        foreach (var s in spec.Slabs)
            row.Slabs.Add(new TaxSlab { OrganizationId = row.OrganizationId, FromAmount = s.From, ToAmount = s.To, FixedAmount = s.Fixed, RatePercent = s.Rate, CreatedBy = user, ModifiedBy = user });
    }

    // ── Status / references ─────────────────────────────────────────────────────

    public async Task<SetupCategoryStatus> GetStatusAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var leaves = await LeaveRowsAsync(false, ct);
        var comps = await ComponentRowsAsync(false, ct);
        var taxes = await TaxRowsAsync(false, ct);
        var modified = new List<string>();
        var missing = new List<string>();
        foreach (var s in context.AlreadySeeded)
        {
            var uuid = Guid.TryParse(s.EntityRef, out var g) ? g : Guid.Empty;
            string? current = s.NaturalKey.StartsWith("LEAVE/", StringComparison.Ordinal) ? leaves.FirstOrDefault(l => l.Uuid == uuid) is { } l ? CurrentFp(l) : null
                : s.NaturalKey.StartsWith("COMPONENT/", StringComparison.Ordinal) ? comps.FirstOrDefault(c => c.Uuid == uuid) is { } c ? CurrentFp(c) : null
                : taxes.FirstOrDefault(t => t.Uuid == uuid) is { } t ? CurrentFp(t) : null;
            if (current is null) missing.Add(s.NaturalKey);
            else if (current != s.Fingerprint) modified.Add(s.NaturalKey);
        }
        return new SetupCategoryStatus
        {
            Category = Category, SeededCount = context.AlreadySeeded.Count, CurrentCount = leaves.Count + comps.Count + taxes.Count,
            ModifiedKeys = modified, MissingKeys = missing
        };
    }

    public async Task<SetupReferenceCheck> CheckReferencesAsync(SetupSeedContext context, CancellationToken ct = default)
    {
        var blocked = await ReferencedAsync(context.AlreadySeeded, await LeaveRowsAsync(false, ct), await ComponentRowsAsync(false, ct), await TaxRowsAsync(false, ct), ct);
        return new SetupReferenceCheck { ReferencedCount = blocked.Count, ReferencedKeys = blocked.Keys.ToList() };
    }

    private Task<SalaryStructure?> DefaultStructureAsync(CancellationToken ct) =>
        _db.SalaryStructures.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == Org && s.Code == DefaultStructureCode, ct);

    /// <summary>Seeded keys that Full / Overwrite must keep, with the reason.</summary>
    private async Task<Dictionary<string, string>> ReferencedAsync(
        IReadOnlyList<SeededRecordRef> seeded, List<LeaveType> leaves, List<SalaryComponent> comps, List<TaxSlabTable> taxes, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var usedLeave = (await _db.LeaveRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == Org).Select(r => r.LeaveTypeId).Distinct().ToListAsync(ct))
            .Concat(await _db.LeaveLedgerEntries.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == Org).Select(r => r.LeaveTypeId).Distinct().ToListAsync(ct))
            .ToHashSet();

        var assigned = await _db.SalaryAssignmentLines.IgnoreQueryFilters().AsNoTracking().Where(l => l.OrganizationId == Org).Select(l => l.ComponentId).Distinct().ToListAsync(ct);
        var defaultStructure = await DefaultStructureAsync(ct);
        var defaultInUse = defaultStructure is not null
            && await _db.SalaryAssignments.IgnoreQueryFilters().AnyAsync(a => a.OrganizationId == Org && a.StructureId == defaultStructure.Id, ct);
        var otherStructureLines = await _db.SalaryStructureLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == Org && (defaultInUse || defaultStructure == null || l.StructureId != defaultStructure.Id)).Select(l => l.ComponentId).Distinct().ToListAsync(ct);
        var payrollCodes = (await _db.PayrollRunLineComponents.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == Org).Select(c => c.Code).Distinct().ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var s in seeded)
        {
            var uuid = Guid.TryParse(s.EntityRef, out var g) ? g : Guid.Empty;
            if (s.NaturalKey.StartsWith("LEAVE/", StringComparison.Ordinal))
            {
                if (leaves.FirstOrDefault(l => l.Uuid == uuid) is { } l && usedLeave.Contains(l.Id)) result[s.NaturalKey] = "leave requests or balances use it";
            }
            else if (s.NaturalKey.StartsWith("COMPONENT/", StringComparison.Ordinal))
            {
                if (comps.FirstOrDefault(c => c.Uuid == uuid) is { } c)
                {
                    if (assigned.Contains(c.Id)) result[s.NaturalKey] = "salary assignments use it";
                    else if (otherStructureLines.Contains(c.Id)) result[s.NaturalKey] = "a salary structure in use lists it";
                    else if (payrollCodes.Contains(c.Code)) result[s.NaturalKey] = "payroll runs used it";
                }
            }
            else if (taxes.FirstOrDefault(t => t.Uuid == uuid) is { IsVerified: true }) result[s.NaturalKey] = "a person has verified this table";
        }
        return result;
    }
}
