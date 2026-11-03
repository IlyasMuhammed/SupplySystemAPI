using System.Collections.Concurrent;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services.Att;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Hrm.Services.Pay;

/// <summary>Serializes the payroll writes of one organization (calculate, finalize, reverse, create). A transaction-owned applock: released by commit or rollback.</summary>
internal static class PayrollLock
{
    public static async Task AcquireAsync(HrmDbContext db, Guid organizationId, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return;
        var resource = $"hrm-payroll:{organizationId:N}";
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
DECLARE @r int;
EXEC @r = sp_getapplock @Resource = {resource}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 25000;
IF @r < 0 THROW 51049, 'PAYROLL_BUSY', 1;", ct);
        }
        catch (Exception ex) when (ex.Message.Contains("PAYROLL_BUSY", StringComparison.Ordinal))
        {
            throw HrmErrors.Conflict("PAYROLL_BUSY", "Another payroll action is running for this organization; try again in a moment.");
        }
    }
}

/// <summary>In this process only, for the screen. The database lock and RowVersion are what make a concurrent calculation safe.</summary>
internal sealed class PayrollProgressTracker
{
    internal sealed class Entry
    {
        public int Total, Processed;
        public bool Running;
        public string? Error;
    }

    private readonly ConcurrentDictionary<Guid, Entry> _runs = new();

    public Entry? Get(Guid runUuid) => _runs.TryGetValue(runUuid, out var e) ? e : null;

    /// <summary>False when this run is already calculating here.</summary>
    public bool TryStart(Guid runUuid, int total)
    {
        var fresh = new Entry { Total = total, Running = true };
        var current = _runs.GetOrAdd(runUuid, fresh);
        if (ReferenceEquals(current, fresh)) return true;
        lock (current)
        {
            if (current.Running) return false;
            current.Total = total;
            current.Processed = 0;
            current.Error = null;
            current.Running = true;
            return true;
        }
    }

    public void Progress(Guid runUuid, int processed, int total)
    {
        if (!_runs.TryGetValue(runUuid, out var e)) return;
        e.Processed = processed;
        e.Total = total;
    }

    public void Finish(Guid runUuid, string? error = null)
    {
        if (!_runs.TryGetValue(runUuid, out var e)) return;
        lock (e)
        {
            e.Running = false;
            e.Error = error;
        }
    }
}

public interface IPayrollCalculationEngine
{
    /// <summary>Task 246 — rewrites the run's lines in one transaction (Draft or Calculated only). Safe under retry: every attempt starts clean.</summary>
    Task CalculateRunAsync(HrmCaller caller, Guid runUuid, CancellationToken ct = default);
}

/// <summary>The Hangfire side of a large calculation (> 200 employees). Takes the organization explicitly (A-29).</summary>
public sealed class PayrollCalculationJob
{
    private readonly IPayrollCalculationEngine _engine;
    private readonly IServiceProvider _services;

    public PayrollCalculationJob(IPayrollCalculationEngine engine, IServiceProvider services)
    {
        _engine = engine;
        _services = services;
    }

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(Guid organizationId, Guid runUuid, int userId, string? userName)
    {
        HangfireTenantScope.OrganizationId = organizationId;
        var tracker = (PayrollProgressTracker)_services.GetService(typeof(PayrollProgressTracker))!;
        try
        {
            await _engine.CalculateRunAsync(new HrmCaller(organizationId, userId, [], userName), runUuid);
            tracker.Finish(runUuid);
        }
        catch (Exception ex)
        {
            tracker.Finish(runUuid, ex is ConflictException or BadRequestException ? ex.Message : "The calculation failed; see the server log.");
            throw;
        }
        finally
        {
            HangfireTenantScope.OrganizationId = null;
        }
    }
}

/// <summary>
/// API-CONTRACT §9, tasks 245–260. Money is decimal throughout and each component is rounded to 2 dp once, so the lines add up exactly and the
/// journal needs no rounding line in practice. A person's problem blocks their line and never the run. See docs: D-PAY-* in the analysis.
/// </summary>
internal sealed class PayrollCalculationEngine : HrmServiceBase, IPayrollCalculationEngine
{
    /// <summary>Snapshot rows from a provider start here: Finalize compares them with what the providers say then (D-PAY-7).</summary>
    public const int InputSequenceBase = 100_000;
    /// <summary>A zero Result row marking a line paid in full because attendance has no record of the person (D-ATT-3, D-PAY-10). Never journaled, never shown as a component.</summary>
    public const string UntrackedAttendanceCode = "ATT_UNTRACKED";
    public const string NetCode = "NET", TaxCode = "ITAX", BasicCode = "BASIC", OvertimeCode = "OT";
    public const string DefaultTaxFormula = "SLAB(TAXABLE_INCOME)";

    private const decimal HoursPerDay = 8m;

    private readonly IHrmEmployeeDirectory _directory;
    private readonly ISalaryFormulaEngine _formulas;
    private readonly IAttendancePayInput _attendance;
    private readonly IEnumerable<IPayrollInputProvider> _providers;
    private readonly PayrollProgressTracker _progress;
    private readonly IModuleGate? _gate;

    public PayrollCalculationEngine(HrmDbContext db, IHrmEmployeeDirectory directory, ISalaryFormulaEngine formulas, IAttendancePayInput attendance,
        IEnumerable<IPayrollInputProvider> providers, PayrollProgressTracker progress, IModuleGate? gate = null) : base(db)
    {
        _directory = directory;
        _formulas = formulas;
        _attendance = attendance;
        _providers = providers;
        _progress = progress;
        _gate = gate;
    }

    internal static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Statutory components and the rate each one reads. Employee side is a Deduction, employer side an EmployerContribution.</summary>
    internal static readonly IReadOnlyDictionary<string, (string RateType, bool Employee)> StatutoryComponents = new Dictionary<string, (string, bool)>(StringComparer.Ordinal)
    {
        ["EOBI_EE"] = (StatutoryRateTypes.Eobi, true), ["EOBI_ER"] = (StatutoryRateTypes.Eobi, false),
        ["PF_EE"] = (StatutoryRateTypes.ProvidentFund, true), ["PF_ER"] = (StatutoryRateTypes.ProvidentFund, false),
        ["SS_EE"] = (StatutoryRateTypes.SocialSecurity, true), ["SS_ER"] = (StatutoryRateTypes.SocialSecurity, false),
    };

    public async Task CalculateRunAsync(HrmCaller caller, Guid runUuid, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var settings = await _directory.GetSettingsAsync(org, ct);
        var countryCode = settings.CountryCode;
        var divisor = settings.WorkingDaysDivisor;
        var payrollCenterId = settings.PayrollCostCenterId;
        var center = payrollCenterId is { } cid
            ? await Db.CostCenters.IgnoreQueryFilters().AsNoTracking().Where(c => c.Id == cid && c.OrganizationId == org).Select(c => c.Code).FirstOrDefaultAsync(ct)
            : null;

        await Db.InTransactionAsync(async () =>
        {
            Db.ChangeTracker.Clear();
            await PayrollLock.AcquireAsync(Db, org, ct);

            var run = await Db.PayrollRuns.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.OrganizationId == org && r.Uuid == runUuid, ct)
                ?? throw new NotFoundException("Payroll run", runUuid);
            if (run.Status != PayrollRunStatuses.Draft && run.Status != PayrollRunStatuses.Calculated)
                throw HrmErrors.Conflict("PAYROLL_STATUS", $"A run that is {run.Status} cannot be calculated.");

            // A recalculation starts from nothing: whatever the last attempt reserved goes back, and only the current lines reserve again.
            foreach (var provider in _providers) await provider.ReleaseAsync(org, run.Id, ct);
            var old = await Db.PayrollRunLines.IgnoreQueryFilters().Include(l => l.Components).Where(l => l.RunId == run.Id && l.OrganizationId == org).ToListAsync(ct);
            if (old.Count > 0)
            {
                Db.PayrollRunLineComponents.RemoveRange(old.SelectMany(l => l.Components));
                Db.PayrollRunLines.RemoveRange(old);
                await Db.SaveChangesAsync(ct);
            }

            var employees = await _directory.GetEmployedInPeriodAsync(org, run.PeriodStart, run.PeriodEnd, run.PayrollGroup, ct);
            var inputs = await LoadAsync(org, run, employees, countryCode, ct);

            var lines = new List<PayrollRunLine>(employees.Count);
            var processed = 0;
            foreach (var employee in employees)
            {
                var line = await CalculateLineAsync(caller, run, employee, inputs, divisor, center, ct);
                if (line is not null) lines.Add(line);
                _progress.Progress(run.Uuid, ++processed, employees.Count);
            }

            Db.PayrollRunLines.AddRange(lines);
            var payable = lines.Where(l => !l.IsBlocked).ToList();
            run.TotalEmployees = lines.Count;
            run.BlockedEmployees = lines.Count - payable.Count;
            run.TotalGross = payable.Sum(l => l.Gross);
            run.TotalDeductions = payable.Sum(l => l.TotalDeductions);
            run.TotalNet = payable.Sum(l => l.Net);
            run.TotalEmployerCost = payable.Sum(l => l.TotalEmployerCost);
            run.CalculatedAt = DateTime.UtcNow;
            run.CalculatedBy = caller.UserIdOrNull;
            run.Status = PayrollRunStatuses.Calculated;
            Stamp(run, caller);
            await SaveAsync(ct, "This payroll run");
        }, ct);
    }

    private sealed record CalcInputs(
        IReadOnlyDictionary<int, SalaryAssignment[]> Assignments,
        IReadOnlyDictionary<string, SalaryComponent> Components,
        TaxSlabTable? TaxTable,
        IReadOnlyDictionary<string, StatutoryRate> Rates,
        IReadOnlyDictionary<int, (string Method, bool HasAccount)> Bank,
        IReadOnlyDictionary<int, DateOnly> FirstRecord,
        IReadOnlyDictionary<int, AttendancePayData> Attendance,
        bool AttendanceOn);

    private async Task<CalcInputs> LoadAsync(Guid org, PayrollRun run, IReadOnlyList<EmployeeTerms> employees, string countryCode, CancellationToken ct)
    {
        var ids = employees.Select(e => e.EmployeeId).ToList();
        var assignments = await Db.SalaryAssignments.IgnoreQueryFilters().AsNoTracking()
            .Include(a => a.Lines).ThenInclude(l => l.Component)
            .Include(a => a.Structure).ThenInclude(s => s!.Lines).ThenInclude(l => l.Component)
            .Where(a => a.OrganizationId == org && ids.Contains(a.EmployeeId) && PayrollAssignments.PayableStatuses.Contains(a.ApprovalStatus) && a.EffectiveFrom <= run.PeriodEnd)
            .AsSplitQuery().ToListAsync(ct);
        var components = await Db.SalaryComponents.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org).ToDictionaryAsync(c => c.Code, ct);
        var end = run.PeriodEnd;
        var table = await PayrollStatutoryLookup.TaxTableAsync(Db, org, countryCode, end, ct);
        var rates = await PayrollStatutoryLookup.RatesAsync(Db, org, countryCode, end, ct);
        var bank = (await Db.EmployeeBankAccounts.IgnoreQueryFilters().AsNoTracking()
                .Where(a => a.OrganizationId == org && ids.Contains(a.EmployeeId) && a.IsPrimary && a.IsActive)
                .Select(a => new { a.EmployeeId, a.PaymentMethod, HasAccount = a.AccountNumberEncrypted != null || a.IbanEncrypted != null }).ToListAsync(ct))
            .GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => (g.First().PaymentMethod, g.First().HasAccount));
        var first = (await Db.EmploymentRecords.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.OrganizationId == org && ids.Contains(r.EmployeeId))
                .Select(r => new { r.EmployeeId, r.EffectiveFrom }).ToListAsync(ct))
            .GroupBy(r => r.EmployeeId).ToDictionary(g => g.Key, g => g.Min(r => r.EffectiveFrom));
        return new CalcInputs(assignments.GroupBy(a => a.EmployeeId).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.EffectiveFrom).ToArray()),
            components, table, rates, bank, first, await _attendance.GetManyAsync(org, ids, run.PeriodStart, run.PeriodEnd, ct),
            _gate is null || await _gate.IsEnabledAsync(org, HrmFeatureCodes.Attendance, ct));
    }

    private async Task<PayrollRunLine?> CalculateLineAsync(HrmCaller caller, PayrollRun run, EmployeeTerms employee, CalcInputs data, int? settingsDivisor, string? centralCode, CancellationToken ct)
    {
        var org = caller.OrganizationId;
        // The employed part of the period. A leaver's salary is the one in force on their last day, not at period end.
        var joined = employee.JoinDate ?? data.FirstRecord.GetValueOrDefault(employee.EmployeeId, employee.EffectiveFrom);
        var start = joined > run.PeriodStart ? joined : run.PeriodStart;
        var end = employee.LeaveDate is { } left && left < run.PeriodEnd ? left : run.PeriodEnd;
        if (end < start) return null;

        var calendarDays = run.PeriodEnd.DayNumber - run.PeriodStart.DayNumber + 1;
        var employedDays = end.DayNumber - start.DayNumber + 1;
        var divisor = (decimal)(settingsDivisor ?? calendarDays);

        // ATT answers on the payroll's own basis (the divisor; weekly offs and holidays paid; days outside employment already out).
        // The default says nothing (0 working days): pay the employed part of the period.
        var attendance = data.Attendance.TryGetValue(employee.EmployeeId, out var said) && said.WorkingDays > 0m ? said : null;
        var employedPart = Math.Round(divisor * employedDays / calendarDays, 2, MidpointRounding.AwayFromZero);
        var workingDays = attendance?.WorkingDays ?? divisor;
        var paidDays = attendance?.PaidDays ?? employedPart;
        var workedDays = attendance?.WorkedDays ?? employedPart;

        var line = new PayrollRunLine
        {
            RunId = run.Id, EmployeeId = employee.EmployeeId, WorkingDays = workingDays, PaidDays = paidDays, WorkedDays = workedDays,
            DepartmentName = employee.DepartmentName, DesignationName = employee.DesignationName
        };
        Stamp(line, caller, isNew: true);

        var blocks = new List<string>();
        var shares = run.CostCenterMode == PayrollCostCenterModes.Central
            ? (centralCode is null ? [] : [new CostCenterShare(centralCode, 100m)])
            : await _directory.GetCostCenterSharesAsync(org, employee.RecordId, run.CostCenterMode, ct);
        if (shares.Count == 0 || centralCode is null) blocks.Add("No payroll cost center is set.");
        line.CostCenterCode = shares.OrderByDescending(s => s.Percent).ThenBy(s => s.CostCenterCode, StringComparer.Ordinal).FirstOrDefault()?.CostCenterCode;

        // Payment method: from the primary account. None means Bank, and blocks.
        var method = BankPaymentMethods.Bank;
        if (data.Bank.TryGetValue(employee.EmployeeId, out var bank))
        {
            method = bank.Method;
            if (method == BankPaymentMethods.Bank && !bank.HasAccount) blocks.Add("The primary bank account has no account number.");
        }
        else blocks.Add("No bank account.");
        line.PaymentMethod = method;

        var asOf = end;
        var assignment = data.Assignments.TryGetValue(employee.EmployeeId, out var history)
            ? history.FirstOrDefault(a => a.EffectiveFrom <= asOf && (a.EffectiveTo is null || a.EffectiveTo >= asOf))
            : null;
        if (assignment is null)
        {
            blocks.Add("No approved salary assignment in force.");
            Finish(line, blocks);
            return line;
        }

        try
        {
            var inputs = new List<PayrollInput>();
            foreach (var provider in _providers)
                inputs.AddRange(await provider.GetInputsAsync(org, run.Id, employee.EmployeeId, run.PeriodStart, run.PeriodEnd, ct));
            Compose(caller, line, assignment, inputs, data, attendance, shares, centralCode, blocks);
        }
        catch (SalaryFormulaException ex)
        {
            blocks.Add($"Formula: {ex.Message}");
        }
        Finish(line, blocks);
        return line;
    }

    private static void Finish(PayrollRunLine line, List<string> blocks)
    {
        if (blocks.Count == 0) return;
        line.IsBlocked = true;
        var reason = string.Join(" ", blocks);
        line.BlockReason = reason.Length <= 500 ? reason : reason[..500];
    }

    private void Compose(HrmCaller caller, PayrollRunLine line, SalaryAssignment assignment, List<PayrollInput> inputs, CalcInputs data, AttendancePayData? attendance,
        IReadOnlyList<CostCenterShare> shares, string? centralCode, List<string> blocks)
    {
        var amounts = assignment.Lines.ToDictionary(l => l.ComponentId, l => l.Amount);
        var ratio = line.WorkingDays > 0m ? line.PaidDays / line.WorkingDays : 0m;
        var overtimeHours = attendance?.WeightedOvertimeHours ?? 0m; // multipliers applied (ATT)

        decimal gross = 0m, totalEarnings = 0m, taxable = 0m, deductions = 0m, employerCost = 0m, fullBasic = 0m;
        var variables = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["BASIC"] = 0m, ["GROSS"] = 0m, ["TOTAL_EARNINGS"] = 0m, ["TAXABLE_INCOME"] = 0m,
            ["WORKING_DAYS"] = line.WorkingDays, ["PAID_DAYS"] = line.PaidDays, ["OT_HOURS"] = overtimeHours
        };

        void Refresh()
        {
            variables["GROSS"] = gross;
            variables["TOTAL_EARNINGS"] = totalEarnings;
            variables["TAXABLE_INCOME"] = taxable * 12m; // annualized (§7): SLAB takes annual income, ITAX is its twelfth
        }

        void CountEarning(SalaryComponent c, decimal amount)
        {
            totalEarnings += amount;
            if (c.InGross) gross += amount;
            if (c.IsTaxable) taxable += amount;
            Refresh();
        }

        var formulaContext = new FormulaContext
        {
            Variables = variables,
            Slab = data.TaxTable is { } table ? income => TaxSlabCalculator.Annual(table.Slabs.ToList(), income) : null
        };
        decimal Run(string formula) => _formulas.Evaluate(formula, formulaContext);

        decimal Configured(SalaryStructureLine l, SalaryComponent c)
        {
            if (amounts.TryGetValue(c.Id, out var given)) return given;
            if (!string.IsNullOrWhiteSpace(l.FormulaOverride)) return Run(l.FormulaOverride);
            switch (c.CalcMethod)
            {
                case SalaryCalcMethods.Percent:
                    var rate = l.ValueOverride ?? c.DefaultValue ?? 0m;
                    if (c.PercentOf is null || !variables.TryGetValue(c.PercentOf, out var basis))
                        throw new SalaryFormulaException($"{c.Code} is a percent of {c.PercentOf}, which is not earlier in the structure.");
                    return basis * rate / 100m;
                case SalaryCalcMethods.Formula:
                    return string.IsNullOrWhiteSpace(c.Formula) ? 0m : Run(c.Formula);
                default:
                    return l.ValueOverride ?? c.DefaultValue ?? 0m;
            }
        }

        var structureLines = assignment.Structure!.Lines.Where(l => l.Component!.IsActive).OrderBy(l => l.Sequence).ToList();
        var rows = new List<PayrollRunLineComponent>();

        void Row(SalaryComponent c, decimal amount, int sequence, decimal? basis = null)
        {
            if (c.PostingRole is null && c.Type != SalaryComponentTypes.Result) blocks.Add($"{c.Code} has no posting role.");
            if (c.Type == SalaryComponentTypes.EmployerContribution && c.ExpenseRole is null) blocks.Add($"{c.Code} has no expense role.");
            var splits = c.Type == SalaryComponentTypes.Earning && PayrollRoles.IsExpense(c.PostingRole ?? string.Empty)
                         || c.Type == SalaryComponentTypes.EmployerContribution
                ? Allocate(amount, shares)
                : [(null, amount)];
            foreach (var (center, part) in splits)
            {
                var row = new PayrollRunLineComponent
                {
                    Code = c.Code, Name = c.Name, Type = c.Type, Amount = part, Basis = basis, PostingRole = c.PostingRole, ExpenseRole = c.ExpenseRole,
                    IsTaxable = c.IsTaxable, InGross = c.InGross, Sequence = sequence, CostCenterCode = center
                };
                Stamp(row, caller, isNew: true);
                rows.Add(row);
            }
        }

        // Earnings first, in sequence, so a provider's taxable earning and every allowance are in the income the tax sees.
        foreach (var l in structureLines.Where(l => l.Component!.Type == SalaryComponentTypes.Earning))
        {
            var c = l.Component!;
            decimal amount;
            if (c.Code == BasicCode)
            {
                fullBasic = Configured(l, c);
                amount = Money(fullBasic * ratio);
            }
            else if (c.Code == OvertimeCode && !amounts.ContainsKey(c.Id) && string.IsNullOrWhiteSpace(l.FormulaOverride) && c.CalcMethod == SalaryCalcMethods.Fixed && (l.ValueOverride ?? c.DefaultValue) is null)
            {
                // D-PAY-6: hourly rate from the full monthly basic; a formula on OT replaces this.
                amount = line.WorkingDays > 0m ? Money(fullBasic / line.WorkingDays / HoursPerDay * overtimeHours) : 0m;
            }
            else amount = Money(Configured(l, c));
            variables[c.Code] = amount;
            if (c.Code == BasicCode) variables["BASIC"] = amount;
            // The OT row keeps the hours it paid: Finalize stamps overtime paid only if they are still the hours on file (D-PAY-9).
            if (amount != 0m || c.Code == BasicCode) Row(c, amount, l.Sequence, c.Code == OvertimeCode ? Money(overtimeHours) : null);
            CountEarning(c, amount);
        }

        // Then what providers bring as earnings (arrears, bonus, reimbursement).
        var resolved = new List<(SalaryComponent C, decimal Amount)>();
        foreach (var input in inputs)
        {
            var code = input.ComponentCode.Trim().ToUpperInvariant();
            if (!data.Components.TryGetValue(code, out var c) || c.Type == SalaryComponentTypes.Result || c.Type == SalaryComponentTypes.EmployerContribution)
            {
                blocks.Add($"An item names component {code}, which is not an earning or deduction.");
                continue;
            }
            resolved.Add((c, Money(input.Amount)));
        }
        var sequence = PayrollCalculationEngine.InputSequenceBase;
        foreach (var (c, amount) in resolved.Where(r => r.C.Type == SalaryComponentTypes.Earning))
        {
            Row(c, amount, sequence++);
            CountEarning(c, amount);
        }

        // Tax, statutory and configured deductions in sequence, then provider deductions.
        foreach (var l in structureLines.Where(l => l.Component!.Type is SalaryComponentTypes.Deduction or SalaryComponentTypes.EmployerContribution))
        {
            var c = l.Component!;
            decimal amount;
            decimal? basis = null;
            if (c.Code == TaxCode)
            {
                if (data.TaxTable is null)
                {
                    blocks.Add("No tax table is in force for this period.");
                    continue;
                }
                var formula = string.IsNullOrWhiteSpace(l.FormulaOverride) ? c.Formula : l.FormulaOverride;
                var annual = string.IsNullOrWhiteSpace(formula) ? 0m : Run(formula);
                amount = Money(Math.Max(annual, 0m) / 12m);
                // Basis only on the plain formula: Finalize can then re-check the figure against the table (D-PAY-8).
                if (IsDefaultTaxFormula(formula)) basis = variables["TAXABLE_INCOME"];
                Row(c, amount, l.Sequence, basis);
                variables[c.Code] = amount;
                deductions += amount;
                continue;
            }
            if (StatutoryComponents.TryGetValue(c.Code, out var statutory))
            {
                if (!data.Rates.TryGetValue(statutory.RateType, out var rate))
                {
                    blocks.Add($"No {statutory.RateType} rate is in force for this period.");
                    continue;
                }
                basis = StatutoryBasis(rate, variables["BASIC"], gross);
                var percent = statutory.Employee ? rate.EmployeePercent : rate.EmployerPercent;
                amount = line.PaidDays > 0m ? Money(basis.Value * percent / 100m) : 0m;
                Row(c, amount, l.Sequence, basis);
            }
            else
            {
                amount = Money(Configured(l, c));
                if (amount != 0m) Row(c, amount, l.Sequence);
            }
            variables[c.Code] = amount;
            if (c.Type == SalaryComponentTypes.Deduction) deductions += amount;
            else employerCost += amount;
        }
        foreach (var (c, amount) in resolved.Where(r => r.C.Type == SalaryComponentTypes.Deduction))
        {
            Row(c, amount, sequence++);
            deductions += amount;
        }

        var net = totalEarnings - deductions;
        line.Gross = gross;
        line.TotalDeductions = deductions;
        line.Net = net;
        line.TotalEmployerCost = employerCost;
        if (net < 0m) blocks.Add("Deductions exceed earnings: the net pay would be negative.");

        // The NET row keeps the payroll cost center the liabilities post on: a later change of the setting never re-classifies this run.
        var netRow = new PayrollRunLineComponent
        {
            Code = NetCode, Name = data.Components.TryGetValue(NetCode, out var netComponent) ? netComponent.Name : "Net pay", Type = SalaryComponentTypes.Result, Amount = net,
            PostingRole = netComponent?.PostingRole ?? PayrollRoles.SalaryPayable, Sequence = int.MaxValue, CostCenterCode = centralCode
        };
        Stamp(netRow, caller, isNew: true);
        rows.Add(netRow);

        if (data.AttendanceOn && attendance is { AttendanceTracked: false })
        {
            var marker = new PayrollRunLineComponent { Code = UntrackedAttendanceCode, Name = "No attendance recorded", Type = SalaryComponentTypes.Result, Amount = 0m, Sequence = 0 };
            Stamp(marker, caller, isNew: true);
            rows.Add(marker);
        }

        foreach (var row in rows) line.Components.Add(row);
    }

    internal static bool IsDefaultTaxFormula(string? formula) =>
        formula is not null && string.Equals(formula.Replace(" ", string.Empty), DefaultTaxFormula, StringComparison.OrdinalIgnoreCase);

    /// <summary>D-PAY-4: a fixed basis (EOBI's notified minimum wage) is taken as it stands; otherwise PF on earned basic, the rest on gross. A ceiling caps either.</summary>
    internal static decimal StatutoryBasis(StatutoryRate rate, decimal earnedBasic, decimal gross)
    {
        var basis = rate.FixedBasis ?? (rate.RateType == StatutoryRateTypes.ProvidentFund ? earnedBasic : gross);
        return rate.WageCeiling is { } ceiling && basis > ceiling ? ceiling : basis;
    }

    /// <summary>§11A.2 percentage split, exact to the cent: largest remainder, ties by code. One share keeps the line's cost center (null).</summary>
    internal static IReadOnlyList<(string? Center, decimal Amount)> Allocate(decimal amount, IReadOnlyList<CostCenterShare> shares)
    {
        if (shares.Count <= 1) return [(null, amount)];
        var total = shares.Sum(s => s.Percent);
        if (total <= 0m) return [(null, amount)];
        var sign = amount < 0m ? -1m : 1m;
        var cents = decimal.Truncate(Math.Abs(amount) * 100m);
        var parts = shares.OrderBy(s => s.CostCenterCode, StringComparer.Ordinal)
            .Select(s =>
            {
                var exact = cents * s.Percent / total;
                var whole = decimal.Floor(exact);
                return (s.CostCenterCode, Whole: whole, Remainder: exact - whole);
            }).ToList();
        var left = (int)(cents - parts.Sum(p => p.Whole));
        var ordered = parts.OrderByDescending(p => p.Remainder).ThenBy(p => p.CostCenterCode, StringComparer.Ordinal).Select(p => p.CostCenterCode).Take(left).ToHashSet(StringComparer.Ordinal);
        return parts.Select(p => ((string?)p.CostCenterCode, sign * (p.Whole + (ordered.Contains(p.CostCenterCode) ? 1m : 0m)) / 100m)).ToList();
    }
}

/// <summary>Which tax table and rates a run reads: in force at period end, the organization's country.</summary>
internal static class PayrollStatutoryLookup
{
    public static Task<TaxSlabTable?> TaxTableAsync(HrmDbContext db, Guid org, string country, DateOnly on, CancellationToken ct) =>
        db.TaxSlabTables.IgnoreQueryFilters().AsNoTracking().Include(t => t.Slabs)
            .Where(t => t.OrganizationId == org && t.CountryCode == country && t.EffectiveFrom <= on && (t.EffectiveTo == null || t.EffectiveTo >= on))
            .OrderByDescending(t => t.EffectiveFrom).FirstOrDefaultAsync(ct);

    public static async Task<IReadOnlyDictionary<string, StatutoryRate>> RatesAsync(HrmDbContext db, Guid org, string country, DateOnly on, CancellationToken ct)
    {
        var rows = await db.StatutoryRates.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.CountryCode == country && r.EffectiveFrom <= on && (r.EffectiveTo == null || r.EffectiveTo >= on)).ToListAsync(ct);
        return rows.GroupBy(r => r.RateType).ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.EffectiveFrom).First());
    }
}

/// <summary>PAY's own provider: one-off adjustments. Rows from an expense claim belong to LIFE's provider, and are skipped here (see API-CONTRACT Changes).</summary>
internal sealed class PayrollAdjustmentInputProvider : IPayrollInputProvider
{
    private readonly HrmDbContext _db;
    public PayrollAdjustmentInputProvider(HrmDbContext db) => _db = db;

    private IQueryable<PayrollAdjustment> Pending(Guid org, int runId, int employeeId) => _db.PayrollAdjustments.IgnoreQueryFilters()
        .Where(a => a.OrganizationId == org && a.EmployeeId == employeeId && a.ExpenseClaimId == null && a.AppliedInRunId == null && (a.RunId == null || a.RunId == runId));

    public async Task<IReadOnlyList<PayrollInput>> GetInputsAsync(Guid organizationId, int runId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var rows = await Pending(organizationId, runId, employeeId).AsNoTracking().OrderBy(a => a.Id).ToListAsync(ct);
        return rows.Select(a => new PayrollInput(a.ComponentCode, a.Amount, a.Reason)).ToList();
    }

    public async Task MarkConsumedAsync(Guid organizationId, int runId, int employeeId, CancellationToken ct = default)
    {
        var rows = await Pending(organizationId, runId, employeeId).ToListAsync(ct);
        foreach (var row in rows) row.AppliedInRunId = runId;
        await _db.SaveChangesAsync(ct);
    }

    public async Task ReleaseAsync(Guid organizationId, int runId, CancellationToken ct = default)
    {
        var rows = await _db.PayrollAdjustments.IgnoreQueryFilters()
            .Where(a => a.OrganizationId == organizationId && a.AppliedInRunId == runId && a.ExpenseClaimId == null).ToListAsync(ct);
        foreach (var row in rows) row.AppliedInRunId = null;
        if (rows.Count > 0) await _db.SaveChangesAsync(ct);
    }
}
