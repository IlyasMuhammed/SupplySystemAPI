using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Pay;

public interface ISalaryComponentService
{
    Task<IReadOnlyList<SalaryComponentModel>> ListAsync(HrmCaller caller, CancellationToken ct = default);
    Task<SalaryComponentModel> CreateAsync(HrmCaller caller, SalaryComponentUpsert request, CancellationToken ct = default);
    Task<SalaryComponentModel> UpdateAsync(HrmCaller caller, Guid uuid, SalaryComponentUpsert request, CancellationToken ct = default);
    Task<SalaryComponentModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default);
}

public interface ISalaryStructureService
{
    Task<IReadOnlyList<SalaryStructureModel>> ListAsync(HrmCaller caller, CancellationToken ct = default);
    Task<SalaryStructureModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<SalaryStructureModel> CreateAsync(HrmCaller caller, SalaryStructureUpsert request, CancellationToken ct = default);
    Task<SalaryStructureModel> UpdateAsync(HrmCaller caller, Guid uuid, SalaryStructureUpsert request, CancellationToken ct = default);
    Task<SalaryStructureModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default);
    Task<FormulaTestResult> TestFormulaAsync(HrmCaller caller, FormulaTestRequest request, CancellationToken ct = default);
}

public interface IStatutoryService
{
    Task<IReadOnlyList<TaxTableModel>> ListTaxTablesAsync(HrmCaller caller, CancellationToken ct = default);
    Task<TaxTableModel> GetTaxTableAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<TaxTableModel> CreateTaxTableAsync(HrmCaller caller, TaxTableUpsert request, CancellationToken ct = default);
    Task<TaxTableModel> UpdateTaxTableAsync(HrmCaller caller, Guid uuid, TaxTableUpsert request, CancellationToken ct = default);
    Task<TaxTableModel> VerifyTaxTableAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default);
    Task<TaxTableModel> UnverifyTaxTableAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);

    Task<IReadOnlyList<StatutoryRateModel>> ListRatesAsync(HrmCaller caller, CancellationToken ct = default);
    Task<StatutoryRateModel> GetRateAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
    Task<StatutoryRateModel> CreateRateAsync(HrmCaller caller, StatutoryRateUpsert request, CancellationToken ct = default);
    Task<StatutoryRateModel> UpdateRateAsync(HrmCaller caller, Guid uuid, StatutoryRateUpsert request, CancellationToken ct = default);
    Task<StatutoryRateModel> VerifyRateAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default);
    Task<StatutoryRateModel> UnverifyRateAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

/// <summary>The account roles a component may name (API-CONTRACT §14, D-7). HRM cannot read Accounting's DefaultKeys; the gateway re-checks.</summary>
internal static class PayrollRoles
{
    public const string SalaryExpense = "DEFAULT_SALARY_EXPENSE", OvertimeExpense = "DEFAULT_OVERTIME_EXPENSE", BonusExpense = "DEFAULT_BONUS_EXPENSE",
        EmployerContribExpense = "DEFAULT_EMPLOYER_CONTRIB_EXPENSE", GratuityExpense = "DEFAULT_GRATUITY_EXPENSE", LeaveEncashmentExpense = "DEFAULT_LEAVE_ENCASHMENT_EXPENSE",
        SalaryPayable = "DEFAULT_SALARY_PAYABLE", Rounding = "DEFAULT_ROUNDING", Bank = "DEFAULT_BANK", Cash = "DEFAULT_CASH";

    public static readonly IReadOnlySet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        SalaryExpense, OvertimeExpense, BonusExpense, EmployerContribExpense, GratuityExpense, LeaveEncashmentExpense, SalaryPayable,
        "DEFAULT_EMPLOYEE_TAX_PAYABLE", "DEFAULT_EOBI_PAYABLE", "DEFAULT_PF_PAYABLE", "DEFAULT_SOCIAL_SECURITY_PAYABLE", "DEFAULT_OTHER_DEDUCTION_PAYABLE",
        "DEFAULT_EXPENSE_CLAIM_PAYABLE", "DEFAULT_GRATUITY_PROVISION", "DEFAULT_EMPLOYEE_LOAN_RECEIVABLE", "DEFAULT_EMPLOYEE_ADVANCE",
    };

    /// <summary>§11A.2: expense follows the employee's cost center; every other role is a balance-sheet line, posted once on the payroll cost center.</summary>
    public static bool IsExpense(string role) => role.EndsWith("_EXPENSE", StringComparison.Ordinal);

    public static string Label(string role) => role.StartsWith("DEFAULT_", StringComparison.Ordinal) ? role[8..].Replace('_', ' ').ToLowerInvariant() : role;
}

internal sealed class SalaryComponentService : HrmServiceBase, ISalaryComponentService
{
    private readonly ISalaryFormulaEngine _formulas;
    private readonly IHrmSeeder _seeder;

    public SalaryComponentService(HrmDbContext db, ISalaryFormulaEngine formulas, IHrmSeeder seeder) : base(db)
    {
        _formulas = formulas;
        _seeder = seeder;
    }

    private static SalaryComponentModel Map(SalaryComponent c) => new()
    {
        Uuid = c.Uuid, Code = c.Code, Name = c.Name, Type = c.Type, IsTaxable = c.IsTaxable, InGross = c.InGross, CalcMethod = c.CalcMethod,
        DefaultValue = c.DefaultValue, PercentOf = c.PercentOf, Formula = c.Formula, PostingRole = c.PostingRole, ExpenseRole = c.ExpenseRole,
        IsSystem = c.IsSystem, IsActive = c.IsActive, SortOrder = c.SortOrder
    };

    private Task<SalaryComponent?> FindAsync(Guid org, Guid uuid, CancellationToken ct) =>
        Db.SalaryComponents.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.OrganizationId == org && c.Uuid == uuid, ct);

    public async Task<IReadOnlyList<SalaryComponentModel>> ListAsync(HrmCaller caller, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var rows = await Db.SalaryComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == caller.OrganizationId).OrderBy(c => c.SortOrder).ThenBy(c => c.Code).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<SalaryComponentModel> CreateAsync(HrmCaller caller, SalaryComponentUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = new SalaryComponent();
        Stamp(row, caller, isNew: true);
        row.Code = HrmValidate.Code(request.Code);
        if (await Db.SalaryComponents.IgnoreQueryFilters().AnyAsync(c => c.OrganizationId == org && c.Code == row.Code, ct))
            throw HrmErrors.Conflict("COMPONENT_CODE_TAKEN", $"Component code {row.Code} is already in use.");
        if (SalaryFormulaEngine.BuiltInVariables.Contains(row.Code) || !IsPlainName(row.Code))
            throw new BadRequestException($"'{row.Code}' cannot be a component code: use letters, digits and _, and not a reserved formula name.");
        await ApplyAsync(row, request, org, ct);
        row.Type = HrmValidate.OneOf(request.Type, SalaryComponentTypes.All, "type");
        ValidateRoles(row);
        Db.SalaryComponents.Add(row);
        try { await Db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsDuplicate(ex))
        {
            throw HrmErrors.Conflict("COMPONENT_CODE_TAKEN", $"Component code {row.Code} is already in use.");
        }
        return Map(row);
    }

    public async Task<SalaryComponentModel> UpdateAsync(HrmCaller caller, Guid uuid, SalaryComponentUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await FindAsync(org, uuid, ct) ?? throw new NotFoundException("Salary component", uuid);
        var code = HrmValidate.Code(request.Code);
        var type = HrmValidate.OneOf(request.Type, SalaryComponentTypes.All, "type");
        if (row.IsSystem && (code != row.Code || type != row.Type))
            throw HrmErrors.Conflict("COMPONENT_SYSTEM_LOCKED", "A system component's code and type cannot change.");
        if (code != row.Code)
        {
            if (await Db.SalaryComponents.IgnoreQueryFilters().AnyAsync(c => c.OrganizationId == org && c.Code == code && c.Id != row.Id, ct))
                throw HrmErrors.Conflict("COMPONENT_CODE_TAKEN", $"Component code {code} is already in use.");
            if (SalaryFormulaEngine.BuiltInVariables.Contains(code) || !IsPlainName(code))
                throw new BadRequestException($"'{code}' cannot be a component code: use letters, digits and _, and not a reserved formula name.");
            row.Code = code;
        }
        row.Type = type;
        await ApplyAsync(row, request, org, ct);
        ValidateRoles(row);
        Stamp(row, caller);
        await SaveAsync(ct, "This component");
        return Map(row);
    }

    public async Task<SalaryComponentModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var row = await FindAsync(caller.OrganizationId, uuid, ct) ?? throw new NotFoundException("Salary component", uuid);
        row.IsActive = isActive;
        Stamp(row, caller);
        await SaveAsync(ct, "This component");
        return Map(row);
    }

    private static bool IsPlainName(string code) => code.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(code[0]);

    private async Task ApplyAsync(SalaryComponent row, SalaryComponentUpsert r, Guid org, CancellationToken ct)
    {
        row.Name = HrmValidate.Name(r.Name);
        row.IsTaxable = r.IsTaxable;
        row.InGross = r.InGross;
        row.CalcMethod = HrmValidate.OneOf(r.CalcMethod ?? SalaryCalcMethods.Fixed, SalaryCalcMethods.All, "calcMethod");
        row.DefaultValue = r.DefaultValue;
        row.PercentOf = string.IsNullOrWhiteSpace(r.PercentOf) ? null : r.PercentOf.Trim().ToUpperInvariant();
        row.Formula = string.IsNullOrWhiteSpace(r.Formula) ? null : r.Formula.Trim();
        row.PostingRole = string.IsNullOrWhiteSpace(r.PostingRole) ? null : r.PostingRole.Trim().ToUpperInvariant();
        row.ExpenseRole = string.IsNullOrWhiteSpace(r.ExpenseRole) ? null : r.ExpenseRole.Trim().ToUpperInvariant();
        if (r.SortOrder is { } order) row.SortOrder = order;

        if (row.CalcMethod == SalaryCalcMethods.Formula)
        {
            if (row.Formula is null) throw HrmErrors.Bad("COMPONENT_FORMULA_INVALID", "A formula component needs a formula.");
            await CheckFormulaAsync(row.Formula, org, ct);
        }
        else if (row.Formula is not null)
            await CheckFormulaAsync(row.Formula, org, ct);

        if (row.CalcMethod == SalaryCalcMethods.Percent)
        {
            if (row.PercentOf is null || row.DefaultValue is null) throw new BadRequestException("A percent component needs a rate and the component it is a percent of.");
            if (row.DefaultValue is < 0m or > 1000m) throw new BadRequestException("The percent is out of range.");
        }
        if (row.DefaultValue is < 0m) throw new BadRequestException("A default value cannot be negative.");
    }

    /// <summary>Names in a formula must exist at all; whether they come earlier in a structure is checked when the structure is saved.</summary>
    private async Task CheckFormulaAsync(string formula, Guid org, CancellationToken ct)
    {
        IReadOnlySet<string> names;
        try { names = _formulas.Validate(formula); }
        catch (SalaryFormulaException ex) { throw HrmErrors.Bad("COMPONENT_FORMULA_INVALID", ex.Message); }
        var unknown = names.Where(n => !SalaryFormulaEngine.BuiltInVariables.Contains(n)).ToList();
        if (unknown.Count == 0) return;
        var codes = await Db.SalaryComponents.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org && unknown.Contains(c.Code)).Select(c => c.Code).ToListAsync(ct);
        var missing = unknown.Except(codes).ToList();
        if (missing.Count > 0) throw HrmErrors.Bad("COMPONENT_FORMULA_INVALID", $"Unknown name(s) in the formula: {string.Join(", ", missing)}.");
    }

    private static void ValidateRoles(SalaryComponent row)
    {
        foreach (var role in new[] { row.PostingRole, row.ExpenseRole })
            if (role is not null && !PayrollRoles.Known.Contains(role))
                throw HrmErrors.Bad("COMPONENT_ROLE_UNKNOWN", $"Unknown account role '{role}'.");
        if (row.Type != SalaryComponentTypes.Result && row.PostingRole is null)
            throw HrmErrors.Bad("COMPONENT_ROLE_UNKNOWN", "This component needs a posting role, or its amount could not be journaled.");
        if (row.Type == SalaryComponentTypes.EmployerContribution && row.ExpenseRole is null)
            throw HrmErrors.Bad("COMPONENT_ROLE_UNKNOWN", "An employer contribution needs an expense role and a payable role.");
    }
}

internal sealed class SalaryStructureService : HrmServiceBase, ISalaryStructureService
{
    private readonly ISalaryFormulaEngine _formulas;
    private readonly IHrmSeeder _seeder;

    public SalaryStructureService(HrmDbContext db, ISalaryFormulaEngine formulas, IHrmSeeder seeder) : base(db)
    {
        _formulas = formulas;
        _seeder = seeder;
    }

    private static SalaryStructureModel Map(SalaryStructure s) => new()
    {
        Uuid = s.Uuid, Code = s.Code, Name = s.Name, Description = s.Description, IsDefault = s.IsDefault, IsActive = s.IsActive,
        Lines = s.Lines.OrderBy(l => l.Sequence).Select(l => new SalaryStructureLineModel
        {
            Uuid = l.Uuid, ComponentUuid = l.Component!.Uuid, ComponentCode = l.Component.Code, Sequence = l.Sequence, FormulaOverride = l.FormulaOverride, ValueOverride = l.ValueOverride
        }).ToList()
    };

    private IQueryable<SalaryStructure> Query(Guid org) =>
        Db.SalaryStructures.IgnoreQueryFilters().Include(s => s.Lines).ThenInclude(l => l.Component).Where(s => s.OrganizationId == org);

    public async Task<IReadOnlyList<SalaryStructureModel>> ListAsync(HrmCaller caller, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var rows = await Query(caller.OrganizationId).AsNoTracking().OrderBy(s => s.Code).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<SalaryStructureModel> GetAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) =>
        Map(await Query(caller.OrganizationId).AsNoTracking().FirstOrDefaultAsync(s => s.Uuid == uuid, ct) ?? throw new NotFoundException("Salary structure", uuid));

    public async Task<SalaryStructureModel> CreateAsync(HrmCaller caller, SalaryStructureUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = new SalaryStructure { Code = HrmValidate.Code(request.Code) };
        Stamp(row, caller, isNew: true);
        if (await Db.SalaryStructures.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org && s.Code == row.Code, ct))
            throw HrmErrors.Conflict("STRUCTURE_CODE_TAKEN", $"Structure code {row.Code} is already in use.");
        await ApplyAsync(row, request, caller, ct);
        Db.SalaryStructures.Add(row);
        await Db.SaveChangesAsync(ct);
        return Map(await Query(org).AsNoTracking().FirstAsync(s => s.Id == row.Id, ct));
    }

    public async Task<SalaryStructureModel> UpdateAsync(HrmCaller caller, Guid uuid, SalaryStructureUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await Query(org).FirstOrDefaultAsync(s => s.Uuid == uuid, ct) ?? throw new NotFoundException("Salary structure", uuid);
        var code = HrmValidate.Code(request.Code);
        if (code != row.Code && await Db.SalaryStructures.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org && s.Code == code && s.Id != row.Id, ct))
            throw HrmErrors.Conflict("STRUCTURE_CODE_TAKEN", $"Structure code {code} is already in use.");
        row.Code = code;
        Db.SalaryStructureLines.RemoveRange(row.Lines);
        await Db.SaveChangesAsync(ct); // the unique (structure, component) index: old lines go first
        row.Lines.Clear();
        await ApplyAsync(row, request, caller, ct);
        Stamp(row, caller);
        await SaveAsync(ct, "This structure");
        return Map(await Query(org).AsNoTracking().FirstAsync(s => s.Id == row.Id, ct));
    }

    public async Task<SalaryStructureModel> SetActiveAsync(HrmCaller caller, Guid uuid, bool isActive, CancellationToken ct = default)
    {
        var row = await Query(caller.OrganizationId).FirstOrDefaultAsync(s => s.Uuid == uuid, ct) ?? throw new NotFoundException("Salary structure", uuid);
        row.IsActive = isActive;
        Stamp(row, caller);
        await SaveAsync(ct, "This structure");
        return Map(row);
    }

    public async Task<FormulaTestResult> TestFormulaAsync(HrmCaller caller, FormulaTestRequest request, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var today = HrmValidate.Today;
        var org = caller.OrganizationId;
        var table = await Db.TaxSlabTables.IgnoreQueryFilters().AsNoTracking().Include(t => t.Slabs)
            .Where(t => t.OrganizationId == org && t.EffectiveFrom <= today && (t.EffectiveTo == null || t.EffectiveTo >= today))
            .OrderByDescending(t => t.EffectiveFrom).FirstOrDefaultAsync(ct);
        var variables = (request.Variables ?? []).ToDictionary(kv => kv.Key.Trim().ToUpperInvariant(), kv => kv.Value);
        try
        {
            var result = _formulas.Evaluate(request.Formula ?? string.Empty, new FormulaContext
            {
                Variables = variables,
                Slab = table is null ? null : income => TaxSlabCalculator.Annual(table.Slabs.ToList(), income)
            });
            return new FormulaTestResult { Valid = true, Result = result };
        }
        catch (SalaryFormulaException ex)
        {
            return new FormulaTestResult { Valid = false, Error = ex.Message };
        }
    }

    private async Task ApplyAsync(SalaryStructure row, SalaryStructureUpsert r, HrmCaller caller, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        row.Name = HrmValidate.Name(r.Name);
        row.Description = HrmValidate.Optional(r.Description, 500, "Description");
        if (r.IsDefault && !row.IsDefault)
        {
            var others = await Db.SalaryStructures.IgnoreQueryFilters().Where(s => s.OrganizationId == org && s.IsDefault && s.Id != row.Id).ToListAsync(ct);
            others.ForEach(o => o.IsDefault = false);
        }
        row.IsDefault = r.IsDefault;
        if (r.Lines.Count == 0) throw new BadRequestException("A structure needs at least one component.");
        var uuids = r.Lines.Select(l => l.ComponentUuid).ToList();
        if (uuids.Distinct().Count() != uuids.Count) throw new BadRequestException("A component can appear once in a structure.");
        var components = await Db.SalaryComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == org && uuids.Contains(c.Uuid)).ToDictionaryAsync(c => c.Uuid, ct);

        // A formula may name a built-in or a component earlier in the structure (API-CONTRACT §7).
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in r.Lines.OrderBy(l => l.Sequence))
        {
            if (!components.TryGetValue(l.ComponentUuid, out var component)) throw new NotFoundException("Salary component", l.ComponentUuid);
            var formula = string.IsNullOrWhiteSpace(l.FormulaOverride) ? null : l.FormulaOverride.Trim();
            var effective = formula ?? (component.CalcMethod == SalaryCalcMethods.Formula ? component.Formula : null);
            if (effective is not null)
            {
                IReadOnlySet<string> names;
                try { names = _formulas.Validate(effective); }
                catch (SalaryFormulaException ex) { throw HrmErrors.Bad("COMPONENT_FORMULA_INVALID", $"{component.Code}: {ex.Message}"); }
                var bad = names.Where(n => !SalaryFormulaEngine.BuiltInVariables.Contains(n) && !seen.Contains(n)).ToList();
                if (bad.Count > 0) throw HrmErrors.Bad("COMPONENT_FORMULA_INVALID", $"{component.Code} names {string.Join(", ", bad)}, which must come earlier in the structure.");
            }
            if (l.ValueOverride is < 0m) throw new BadRequestException("A value cannot be negative.");
            seen.Add(component.Code);
            var line = new SalaryStructureLine { ComponentId = component.Id, Sequence = l.Sequence, FormulaOverride = formula, ValueOverride = l.ValueOverride };
            Stamp(line, caller, isNew: true);
            row.Lines.Add(line);
        }
    }
}

/// <summary>Tax tables and statutory rates: data, never asserted (G-21). Created unverified; any edit clears verification; only HRM_STATUTORY_VERIFY marks them.</summary>
internal sealed class StatutoryService : HrmServiceBase, IStatutoryService
{
    private readonly IHrmSeeder _seeder;
    public StatutoryService(HrmDbContext db, IHrmSeeder seeder) : base(db) => _seeder = seeder;

    private static TaxTableModel Map(TaxSlabTable t) => new()
    {
        Uuid = t.Uuid, Code = t.Code, Name = t.Name, CountryCode = t.CountryCode, EffectiveFrom = t.EffectiveFrom, EffectiveTo = t.EffectiveTo,
        IsVerified = t.IsVerified, VerifiedByName = t.VerifiedByName, VerifiedAt = t.VerifiedAt, SourceNote = t.SourceNote,
        Slabs = t.Slabs.OrderBy(s => s.FromAmount).Select(s => new TaxSlabModel { FromAmount = s.FromAmount, ToAmount = s.ToAmount, FixedAmount = s.FixedAmount, RatePercent = s.RatePercent }).ToList()
    };

    private static StatutoryRateModel Map(StatutoryRate r) => new()
    {
        Uuid = r.Uuid, RateType = r.RateType, Name = r.Name, CountryCode = r.CountryCode, EmployeePercent = r.EmployeePercent, EmployerPercent = r.EmployerPercent,
        WageCeiling = r.WageCeiling, FixedBasis = r.FixedBasis, EffectiveFrom = r.EffectiveFrom, EffectiveTo = r.EffectiveTo, IsVerified = r.IsVerified,
        VerifiedByName = r.VerifiedByName, VerifiedAt = r.VerifiedAt, SourceNote = r.SourceNote
    };

    private IQueryable<TaxSlabTable> Tables(Guid org) => Db.TaxSlabTables.IgnoreQueryFilters().Include(t => t.Slabs).Where(t => t.OrganizationId == org);
    private IQueryable<StatutoryRate> Rates(Guid org) => Db.StatutoryRates.IgnoreQueryFilters().Where(r => r.OrganizationId == org);

    public async Task<IReadOnlyList<TaxTableModel>> ListTaxTablesAsync(HrmCaller caller, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var rows = await Tables(caller.OrganizationId).AsNoTracking().OrderByDescending(t => t.EffectiveFrom).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<TaxTableModel> GetTaxTableAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) =>
        Map(await Tables(caller.OrganizationId).AsNoTracking().FirstOrDefaultAsync(t => t.Uuid == uuid, ct) ?? throw new NotFoundException("Tax table", uuid));

    public async Task<TaxTableModel> CreateTaxTableAsync(HrmCaller caller, TaxTableUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        await _seeder.EnsureAsync(org, ct);
        var row = new TaxSlabTable { Code = HrmValidate.Code(request.Code, 30) };
        Stamp(row, caller, isNew: true);
        if (await Db.TaxSlabTables.IgnoreQueryFilters().AnyAsync(t => t.OrganizationId == org && t.Code == row.Code, ct))
            throw HrmErrors.Conflict("TAX_TABLE_CODE_TAKEN", $"Tax table code {row.Code} is already in use.");
        await ApplyAsync(row, request, caller, ct);
        Db.TaxSlabTables.Add(row);
        await Db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<TaxTableModel> UpdateTaxTableAsync(HrmCaller caller, Guid uuid, TaxTableUpsert request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var row = await Tables(org).FirstOrDefaultAsync(t => t.Uuid == uuid, ct) ?? throw new NotFoundException("Tax table", uuid);
        var code = HrmValidate.Code(request.Code, 30);
        if (code != row.Code && await Db.TaxSlabTables.IgnoreQueryFilters().AnyAsync(t => t.OrganizationId == org && t.Code == code && t.Id != row.Id, ct))
            throw HrmErrors.Conflict("TAX_TABLE_CODE_TAKEN", $"Tax table code {code} is already in use.");
        row.Code = code;
        Db.TaxSlabs.RemoveRange(row.Slabs);
        await Db.SaveChangesAsync(ct); // the unique (table, from) index: old brackets go first
        row.Slabs.Clear();
        await ApplyAsync(row, request, caller, ct);
        ClearVerification(row);
        Stamp(row, caller);
        await SaveAsync(ct, "This tax table");
        return Map(row);
    }

    public async Task<TaxTableModel> VerifyTaxTableAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default)
    {
        var row = await Tables(caller.OrganizationId).FirstOrDefaultAsync(t => t.Uuid == uuid, ct) ?? throw new NotFoundException("Tax table", uuid);
        row.IsVerified = true;
        row.VerifiedBy = caller.UserIdOrNull;
        row.VerifiedByName = caller.UserName;
        row.VerifiedAt = DateTime.UtcNow;
        row.SourceNote = HrmValidate.Optional(note, 500, "Note") ?? row.SourceNote;
        Stamp(row, caller);
        await SaveAsync(ct, "This tax table");
        return Map(row);
    }

    public async Task<TaxTableModel> UnverifyTaxTableAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await Tables(caller.OrganizationId).FirstOrDefaultAsync(t => t.Uuid == uuid, ct) ?? throw new NotFoundException("Tax table", uuid);
        ClearVerification(row);
        Stamp(row, caller);
        await SaveAsync(ct, "This tax table");
        return Map(row);
    }

    private static void ClearVerification(TaxSlabTable t)
    {
        t.IsVerified = false;
        t.VerifiedBy = null;
        t.VerifiedByName = null;
        t.VerifiedAt = null;
    }

    private async Task ApplyAsync(TaxSlabTable row, TaxTableUpsert r, HrmCaller caller, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        row.Name = HrmValidate.Name(r.Name);
        row.CountryCode = HrmValidate.Code(r.CountryCode ?? "PK", 3);
        row.EffectiveFrom = r.EffectiveFrom;
        row.EffectiveTo = r.EffectiveTo;
        row.SourceNote = HrmValidate.Optional(r.SourceNote, 500, "Source note");
        if (r.EffectiveTo < r.EffectiveFrom) throw new BadRequestException("The end date cannot be before the start date.");
        ValidateSlabs(r.Slabs);

        var others = await Db.TaxSlabTables.IgnoreQueryFilters()
            .Where(t => t.OrganizationId == org && t.CountryCode == row.CountryCode && t.Id != row.Id).ToListAsync(ct);
        CloseOpenPredecessor(others, o => (o.EffectiveFrom, o.EffectiveTo), (o, to) => o.EffectiveTo = to, row.EffectiveFrom);
        if (others.Any(o => Overlaps(o.EffectiveFrom, o.EffectiveTo, row.EffectiveFrom, row.EffectiveTo)))
            throw HrmErrors.Conflict("TAX_TABLE_OVERLAP", "Another tax table covers part of these dates.");

        foreach (var s in r.Slabs.OrderBy(s => s.FromAmount))
        {
            var slab = new TaxSlab { FromAmount = s.FromAmount, ToAmount = s.ToAmount, FixedAmount = s.FixedAmount, RatePercent = s.RatePercent };
            Stamp(slab, caller, isNew: true);
            row.Slabs.Add(slab);
        }
    }

    /// <summary>Brackets from 0, contiguous, only the last open-ended: an income can never fall between two brackets.</summary>
    internal static void ValidateSlabs(IReadOnlyCollection<TaxSlabModel> slabs)
    {
        if (slabs.Count == 0) throw HrmErrors.Bad("TAX_SLABS_INVALID", "A tax table needs at least one bracket.");
        var ordered = slabs.OrderBy(s => s.FromAmount).ToList();
        if (ordered[0].FromAmount != 0m) throw HrmErrors.Bad("TAX_SLABS_INVALID", "The first bracket must start at 0.");
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            if (s.RatePercent is < 0m or > 100m) throw HrmErrors.Bad("TAX_SLABS_INVALID", "A bracket's rate must be between 0 and 100.");
            if (s.FixedAmount < 0m) throw HrmErrors.Bad("TAX_SLABS_INVALID", "A bracket's fixed amount cannot be negative.");
            if (s.ToAmount is { } to && to <= s.FromAmount) throw HrmErrors.Bad("TAX_SLABS_INVALID", "A bracket must end after it starts.");
            if (i < ordered.Count - 1 && s.ToAmount != ordered[i + 1].FromAmount)
                throw HrmErrors.Bad("TAX_SLABS_INVALID", "Each bracket must start where the previous one ends.");
        }
        if (ordered[^1].ToAmount is not null) throw HrmErrors.Bad("TAX_SLABS_INVALID", "The last bracket must have no ceiling.");
    }

    public async Task<IReadOnlyList<StatutoryRateModel>> ListRatesAsync(HrmCaller caller, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var rows = await Rates(caller.OrganizationId).AsNoTracking().OrderBy(r => r.RateType).ThenByDescending(r => r.EffectiveFrom).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<StatutoryRateModel> GetRateAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default) =>
        Map(await Rates(caller.OrganizationId).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Statutory rate", uuid));

    public async Task<StatutoryRateModel> CreateRateAsync(HrmCaller caller, StatutoryRateUpsert request, CancellationToken ct = default)
    {
        await _seeder.EnsureAsync(caller.OrganizationId, ct);
        var row = new StatutoryRate();
        Stamp(row, caller, isNew: true);
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        Db.StatutoryRates.Add(row);
        await Db.SaveChangesAsync(ct);
        return Map(row);
    }

    public async Task<StatutoryRateModel> UpdateRateAsync(HrmCaller caller, Guid uuid, StatutoryRateUpsert request, CancellationToken ct = default)
    {
        var row = await Rates(caller.OrganizationId).FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Statutory rate", uuid);
        await ApplyAsync(row, request, caller.OrganizationId, ct);
        ClearVerification(row);
        Stamp(row, caller);
        await SaveAsync(ct, "This rate");
        return Map(row);
    }

    public async Task<StatutoryRateModel> VerifyRateAsync(HrmCaller caller, Guid uuid, string? note, CancellationToken ct = default)
    {
        var row = await Rates(caller.OrganizationId).FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Statutory rate", uuid);
        row.IsVerified = true;
        row.VerifiedBy = caller.UserIdOrNull;
        row.VerifiedByName = caller.UserName;
        row.VerifiedAt = DateTime.UtcNow;
        row.SourceNote = HrmValidate.Optional(note, 500, "Note") ?? row.SourceNote;
        Stamp(row, caller);
        await SaveAsync(ct, "This rate");
        return Map(row);
    }

    public async Task<StatutoryRateModel> UnverifyRateAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await Rates(caller.OrganizationId).FirstOrDefaultAsync(r => r.Uuid == uuid, ct) ?? throw new NotFoundException("Statutory rate", uuid);
        ClearVerification(row);
        Stamp(row, caller);
        await SaveAsync(ct, "This rate");
        return Map(row);
    }

    private static void ClearVerification(StatutoryRate r)
    {
        r.IsVerified = false;
        r.VerifiedBy = null;
        r.VerifiedByName = null;
        r.VerifiedAt = null;
    }

    private async Task ApplyAsync(StatutoryRate row, StatutoryRateUpsert r, Guid org, CancellationToken ct)
    {
        row.RateType = HrmValidate.OneOf(r.RateType, StatutoryRateTypes.All, "rateType");
        row.Name = HrmValidate.Name(r.Name);
        row.CountryCode = HrmValidate.Code(r.CountryCode ?? "PK", 3);
        if (r.EmployeePercent is < 0m or > 100m || r.EmployerPercent is < 0m or > 100m) throw new BadRequestException("A percent must be between 0 and 100.");
        if (r.WageCeiling is < 0m || r.FixedBasis is < 0m) throw new BadRequestException("A ceiling or basis cannot be negative.");
        if (r.EffectiveTo < r.EffectiveFrom) throw new BadRequestException("The end date cannot be before the start date.");
        row.EmployeePercent = r.EmployeePercent;
        row.EmployerPercent = r.EmployerPercent;
        row.WageCeiling = r.WageCeiling;
        row.FixedBasis = r.FixedBasis;
        row.EffectiveFrom = r.EffectiveFrom;
        row.EffectiveTo = r.EffectiveTo;
        row.SourceNote = HrmValidate.Optional(r.SourceNote, 500, "Source note");

        var others = await Db.StatutoryRates.IgnoreQueryFilters()
            .Where(o => o.OrganizationId == org && o.RateType == row.RateType && o.CountryCode == row.CountryCode && o.Id != row.Id).ToListAsync(ct);
        CloseOpenPredecessor(others, o => (o.EffectiveFrom, o.EffectiveTo), (o, to) => o.EffectiveTo = to, row.EffectiveFrom);
        if (others.Any(o => Overlaps(o.EffectiveFrom, o.EffectiveTo, row.EffectiveFrom, row.EffectiveTo)))
            throw HrmErrors.Conflict("STATUTORY_RATE_OVERLAP", "Another rate of this type covers part of these dates.");
    }

    /// <summary>The seeds are open-ended: a successor closes its open-ended predecessor (only its end date; its verification stands, its values did not change).</summary>
    private static void CloseOpenPredecessor<T>(List<T> others, Func<T, (DateOnly From, DateOnly? To)> dates, Action<T, DateOnly> close, DateOnly newFrom)
    {
        foreach (var o in others)
        {
            var (from, to) = dates(o);
            if (to is null && from < newFrom) close(o, newFrom.AddDays(-1));
        }
    }

    private static bool Overlaps(DateOnly aFrom, DateOnly? aTo, DateOnly bFrom, DateOnly? bTo) =>
        aFrom <= (bTo ?? DateOnly.MaxValue) && bFrom <= (aTo ?? DateOnly.MaxValue);
}
