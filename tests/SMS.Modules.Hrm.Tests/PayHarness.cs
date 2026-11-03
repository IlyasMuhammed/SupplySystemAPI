using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Att;
using SMS.Modules.Hrm.Services.Pay;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Hrm.Tests;

internal sealed class PayGateway : IFinancialPostingGateway
{
    public PostingStatus Status { get; set; } = PostingStatus.NoTargetConfigured;
    public List<TargetResult> Targets { get; set; } = [];
    public List<FinancialTransaction> Posted { get; } = [];
    public List<VoidRequest> Voids { get; } = [];
    public string? Message { get; set; }

    private PostingResult Result(Guid id, FinDocType type) => new() { Status = Status, SourceDocumentId = id, DocumentType = type, TargetResults = Targets, Message = Message };

    public Task<PostingResult> PostAsync(FinancialTransaction t, CancellationToken ct = default)
    {
        lock (Posted) Posted.Add(t);
        return Task.FromResult(Result(t.SourceDocumentId, t.DocumentType));
    }

    public Task<PostingResult> VoidAsync(VoidRequest r, CancellationToken ct = default)
    {
        lock (Voids) Voids.Add(r);
        return Task.FromResult(Result(r.SourceDocumentId, r.DocumentType));
    }

    public Task<PostingResult> IssueCreditAsync(FinancialTransaction t, CancellationToken ct = default) => PostAsync(t, ct);
    public Task<PostingResult> ReverseAsync(VoidRequest r, CancellationToken ct = default) => VoidAsync(r, ct);

    public void PostsToGl()
    {
        Status = PostingStatus.Success;
        Targets = [new TargetResult { TargetCode = PostingTargetCodes.InternalGl, Status = TargetPostingStatus.Posted, InternalId = "JE-1" }];
    }
}

internal sealed class PayApprovals : IHrmApprovalGateway
{
    public int Submitted { get; private set; }
    public Task<Guid> SubmitAsync(HrmCaller caller, string interfaceCode, Guid documentUuid, string documentNumber, int? employeeId, string fallbackPermission, decimal? conditionValue = null, CancellationToken ct = default)
    {
        Submitted++;
        return Task.FromResult(Guid.NewGuid());
    }
}

internal sealed class PayDocumentStore : IHrmDocumentStore
{
    public Dictionary<Guid, byte[]> Files { get; } = [];
    public Task<Guid> StorePdfAsync(HrmCaller caller, string interfaceCode, Guid documentUuid, string fileName, byte[] pdf, string requiredPermission, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        Files[id] = pdf;
        return Task.FromResult(id);
    }

    public Task<AttachmentContent?> ReadAsync(Guid attachmentUuid) =>
        Task.FromResult<AttachmentContent?>(Files.TryGetValue(attachmentUuid, out var bytes) ? new AttachmentContent(bytes, "x.pdf", "application/pdf", null, "HRM_PAYSLIP") : null);
}

internal sealed class PayReadiness : IPayrollPostingReadinessService
{
    public PayrollPostingReadiness Answer { get; set; } = new();
    public Task<PayrollPostingReadiness> CheckAsync(Guid organizationId, int runId, CancellationToken ct = default) => Task.FromResult(Answer);
}

internal sealed class PayPeriods : IFiscalPeriodGuard
{
    public HashSet<DateOnly> Closed { get; } = [];
    public Task<bool> IsPostingAllowedAsync(Guid organizationId, DateOnly date, CancellationToken ct = default) => Task.FromResult(!Closed.Contains(date));
}

/// <summary>What LIFE's loan provider does, as far as the run can see: reserves, consumes, releases, and reports nothing another run holds.</summary>
internal sealed class PayLoanProvider : IPayrollInputProvider
{
    public decimal Installment { get; set; }
    public int? ReservedBy { get; private set; }
    public int? ConsumedBy { get; private set; }
    public int ConsumeCalls { get; private set; }

    public Task<IReadOnlyList<PayrollInput>> GetInputsAsync(Guid organizationId, int runId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (Installment == 0m || ConsumedBy is not null || (ReservedBy is { } r && r != runId)) return Task.FromResult<IReadOnlyList<PayrollInput>>([]);
        ReservedBy = runId;
        return Task.FromResult<IReadOnlyList<PayrollInput>>([new PayrollInput("LOAN", Installment, "Loan")]);
    }

    public Task MarkConsumedAsync(Guid organizationId, int runId, int employeeId, CancellationToken ct = default)
    {
        ConsumeCalls++;
        if (ReservedBy == runId) ConsumedBy = runId;
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(Guid organizationId, int runId, CancellationToken ct = default)
    {
        if (ReservedBy == runId) ReservedBy = null;
        if (ConsumedBy == runId) ConsumedBy = null;
        return Task.CompletedTask;
    }
}

internal sealed class PayAttendance : IAttendancePayInput
{
    public Func<int, AttendancePayData?> Answer { get; set; } = _ => null;
    public List<int> Stamped { get; } = [];
    public int Released { get; private set; }

    private AttendancePayData Data(int id) => Answer(id) ?? new AttendancePayData(id, 0m, 0m, 0m, 0m, 0, 0, 0m, [], false);

    public Task<AttendancePayData> GetAsync(Guid organizationId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default) => Task.FromResult(Data(employeeId));
    public Task<IReadOnlyDictionary<int, AttendancePayData>> GetManyAsync(Guid organizationId, IReadOnlyCollection<int> employeeIds, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<int, AttendancePayData>>(employeeIds.ToDictionary(i => i, Data));
    public Task<int> MarkOvertimePaidAsync(Guid organizationId, int runId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        Stamped.Add(employeeId);
        return Task.FromResult(1);
    }
    public Task<int> ReleaseOvertimeAsync(Guid organizationId, int runId, CancellationToken ct = default)
    {
        Released++;
        return Task.FromResult(1);
    }
}

/// <summary>PAY's services over the core harness, with the null seams unless a test swaps one.</summary>
internal sealed class PayHarness
{
    public static readonly DateOnly October = new(2026, 10, 1);
    public static readonly DateOnly OctoberEnd = new(2026, 10, 31);

    public HrmHarness Core { get; }
    public HrmHarness.Base Structure { get; private set; } = null!;
    public HrmDbContext Db => Core.Db;
    public Guid Org => Core.Org;
    public PayGateway Gateway { get; } = new();
    public PayApprovals Approvals { get; } = new();
    public PayDocumentStore Documents { get; } = new();
    public PayReadiness Readiness { get; } = new();
    public PayPeriods Periods { get; } = new();
    public PayLoanProvider Loans { get; } = new();
    public List<IPayrollInputProvider> Providers { get; } = [];
    public IAttendancePayInput Attendance { get; set; } = new NullAttendancePayInput();
    public SalaryFormulaEngine Formulas { get; } = new();
    public ISalaryComponentService Components { get; }
    public ISalaryStructureService Structures { get; }
    public IStatutoryService Statutory { get; }
    public SalaryAssignmentService Salaries { get; }
    public IPayrollAdjustmentService Adjustments { get; }
    public IPayslipService Payslips { get; }
    public IPayrollPostingBuilder Builder { get; }

    public PayHarness(HrmHarness? core = null)
    {
        Core = core ?? new HrmHarness();
        Components = new SalaryComponentService(Db, Formulas, Core.Seeder);
        Structures = new SalaryStructureService(Db, Formulas, Core.Seeder);
        Statutory = new StatutoryService(Db, Core.Seeder);
        Salaries = new SalaryAssignmentService(Db, Core.Scope, Approvals, new SalaryDecisions(Db));
        Adjustments = new PayrollAdjustmentService(Db, Core.Scope);
        Payslips = new PayslipService(Db, Core.Scope, Documents);
        Builder = new PayrollPostingBuilder(Db);
        Providers.Add(new PayrollAdjustmentInputProvider(Db));
    }

    public HrmCaller Admin => Core.Admin();

    public IPayrollRunService Runs() => new PayrollRunService(Db, Core.Scope, Core.Directory,
        new PayrollCalculationEngine(Db, Core.Directory, Formulas, Attendance, Providers, new PayrollProgressTracker()),
        Builder, Gateway, Readiness, Providers, Attendance, new PayrollProgressTracker(), Core.Seeder, periods: Periods);

    /// <summary>Org structure + payroll cost center + the current tax table, everything verified so the tests of money are not stopped by G-21.</summary>
    public async Task<PayHarness> ReadyAsync(bool verify = true)
    {
        await Core.Seeder.EnsureAsync(Org);
        Structure = await Core.BaseAsync();
        await Core.Settings.UpdateAsync(Admin, new HrmSettingsUpdate { PayrollCostCenterUuid = Structure.CostCenter });
        await Statutory.CreateTaxTableAsync(Admin, new TaxTableUpsert
        {
            Code = "PK-2027", Name = "Test tax table", EffectiveFrom = new DateOnly(2026, 7, 1), EffectiveTo = new DateOnly(2027, 6, 30),
            Slabs =
            [
                new TaxSlabModel { FromAmount = 0m, ToAmount = 600_000m, FixedAmount = 0m, RatePercent = 0m },
                new TaxSlabModel { FromAmount = 600_000m, ToAmount = 1_200_000m, FixedAmount = 0m, RatePercent = 1m },
                new TaxSlabModel { FromAmount = 1_200_000m, ToAmount = 2_200_000m, FixedAmount = 6_000m, RatePercent = 11m },
                new TaxSlabModel { FromAmount = 2_200_000m, FixedAmount = 116_000m, RatePercent = 23m },
            ]
        });
        if (verify) await VerifyAllAsync();
        return this;
    }

    public async Task VerifyAllAsync()
    {
        foreach (var t in await Statutory.ListTaxTablesAsync(Admin)) await Statutory.VerifyTaxTableAsync(Admin, t.Uuid, "Reviewed in test");
        foreach (var r in await Statutory.ListRatesAsync(Admin)) await Statutory.VerifyRateAsync(Admin, r.Uuid, "Reviewed in test");
    }

    public async Task<SalaryStructureModel> StandardAsync() => (await Structures.ListAsync(Admin)).First(s => s.Code == "STD_MONTHLY");

    /// <summary>An employee with a bank account and an approved salary (BASIC + HRENT).</summary>
    public async Task<EmployeeDetail> StaffAsync(string first, decimal basic, decimal rent = 0m, DateOnly? hired = null, bool bank = true, string? method = null, DateOnly? salaryFrom = null, Guid? department = null)
    {
        var employee = await Core.Employees.CreateAsync(Admin, new EmployeeUpsert
        {
            FirstName = first, LastName = "Test", JoinDate = hired ?? new DateOnly(2026, 1, 1), Employment = Core.Hire(Structure, hired ?? new DateOnly(2026, 1, 1), department: department)
        });
        if (bank)
            await Core.Bank.CreateAsync(Admin, employee.Uuid, new EmployeeBankAccountUpsert { BankName = "HBL", AccountNumber = "1234567890" + first.Length, Iban = "PK00HBL" + first.Length, PaymentMethod = method ?? "Bank" });
        await SalaryAsync(employee.Uuid, salaryFrom ?? hired ?? new DateOnly(2026, 1, 1), basic, rent);
        return employee;
    }

    public async Task<SalaryAssignmentModel> SalaryAsync(Guid employee, DateOnly from, decimal basic, decimal rent = 0m)
    {
        var std = await StandardAsync();
        var lines = new List<SalaryAssignmentLineUpsert> { new() { ComponentUuid = std.Lines.First(l => l.ComponentCode == "BASIC").ComponentUuid, Amount = basic } };
        if (rent > 0m) lines.Add(new() { ComponentUuid = std.Lines.First(l => l.ComponentCode == "HRENT").ComponentUuid, Amount = rent });
        var draft = await Salaries.CreateAsync(Admin, employee, new SalaryAssignmentCreate { StructureUuid = std.Uuid, EffectiveFrom = from, Lines = lines });
        await Salaries.SubmitAsync(Admin, draft.Uuid);
        return await Salaries.ApproveAsync(Admin, draft.Uuid);
    }

    public Task<PayrollRunModel> NewRunAsync(IPayrollRunService runs, DateOnly? payDate = null) =>
        runs.CreateAsync(Admin, new PayrollRunCreate { PeriodStart = October, PeriodEnd = OctoberEnd, PayDate = payDate ?? new DateOnly(2026, 11, 1) });

    /// <summary>Draft → Approved.</summary>
    public async Task<PayrollRunModel> ApprovedRunAsync(IPayrollRunService runs, bool excludeBlocked = false)
    {
        var run = await NewRunAsync(runs);
        await runs.CalculateAsync(Admin, run.Uuid);
        await runs.ReviewAsync(Admin, run.Uuid);
        return await runs.ApproveAsync(Admin, run.Uuid, excludeBlocked);
    }

    public async Task<PayrollRunLineModel> LineAsync(IPayrollRunService runs, Guid run, Guid employee) =>
        (await runs.ListLinesAsync(Admin, run, null, false, 1, 200)).Data.First(l => l.EmployeeUuid == employee);

    public static decimal Amount(PayrollRunLineModel line, string code) => line.Components.Where(c => c.Code == code).Sum(c => c.Amount);
}
