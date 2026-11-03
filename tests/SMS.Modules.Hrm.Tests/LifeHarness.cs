using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Life;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;

namespace SMS.Modules.Hrm.Tests;

internal sealed class LifeGateway : IFinancialPostingGateway
{
    public List<FinancialTransaction> Posted { get; } = [];
    public PostingStatus Next { get; set; } = PostingStatus.Success;
    public bool Throw { get; set; }

    public Task<PostingResult> PostAsync(FinancialTransaction t, CancellationToken ct = default)
    {
        if (Throw) throw new InvalidOperationException("gateway down");
        Posted.Add(t);
        return Task.FromResult(new PostingResult { Status = Next, SourceDocumentId = t.SourceDocumentId, DocumentType = t.DocumentType, Message = Next == PostingStatus.PartialFailure ? "target failed" : null });
    }
    public Task<PostingResult> VoidAsync(VoidRequest r, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PostingResult> IssueCreditAsync(FinancialTransaction t, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<PostingResult> ReverseAsync(VoidRequest r, CancellationToken ct = default) => throw new NotSupportedException();
}

internal sealed class LifeStore : IHrmDocumentStore
{
    public Dictionary<Guid, byte[]> Files { get; } = [];
    public Task<Guid> StorePdfAsync(HrmCaller caller, string interfaceCode, Guid documentUuid, string fileName, byte[] pdf, string requiredPermission, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        Files[id] = pdf;
        return Task.FromResult(id);
    }
    public Task<AttachmentContent?> ReadAsync(Guid attachmentUuid) =>
        Task.FromResult(Files.TryGetValue(attachmentUuid, out var f) ? new AttachmentContent(f, "x.pdf", "application/pdf", null, "HRM_LETTER") : null);
}

internal sealed class LifeApprovals : IHrmApprovalGateway
{
    public bool NoApprover { get; set; }
    public Task<Guid> SubmitAsync(HrmCaller caller, string interfaceCode, Guid documentUuid, string documentNumber, int? employeeId, string fallbackPermission, decimal? conditionValue = null, CancellationToken ct = default)
    {
        if (NoApprover) throw HrmErrors.Conflict("HRM_NO_APPROVER", "nobody");
        return Task.FromResult(Guid.NewGuid());
    }
}

internal sealed class LifeOrgName : IOrganizationNameLookup
{
    public string? Name { get; set; } = "Acme Traders";
    public List<Guid> Asked { get; } = [];
    public Task<string?> GetNameAsync(Guid organizationId, CancellationToken ct = default) { Asked.Add(organizationId); return Task.FromResult(Name); }
}

/// <summary>The workflow engine, reduced to what it tells the handler: approve and reject call back the document's status.</summary>
public class WorkflowProxy : DispatchProxy
{
    internal Func<string, Task> OnStatus { get; set; } = _ => Task.CompletedTask;

    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "ApproveAsync" => Run("APPROVED"),
        "RejectAsync" => Run("REJECTED"),
        _ => throw new NotSupportedException(method.Name)
    };

    private async Task<Guid> Run(string status)
    {
        await OnStatus(status);
        return Guid.NewGuid();
    }
}

/// <summary>LIFE's services over <see cref="HrmHarness"/>, with a capturing gateway.</summary>
internal sealed class LifeHarness
{
    public HrmHarness H { get; }
    public LifeGateway Gateway { get; } = new();
    public LifeStore Store { get; } = new();
    public LifeApprovals Approvals { get; } = new();
    public LifeOrgName OrgName { get; } = new();
    public HrmHarness.Base B { get; private set; } = null!;
    public LoanLedger Ledger { get; }
    public ILoanService Loans { get; }
    public IFinalSettlementService Settlements { get; }
    public IIncrementService Increments { get; }
    public ILetterService Letters { get; }
    public ITaxCertificateService Tax { get; }
    public IExpenseCategoryService Categories { get; }
    public IExpenseClaimService Claims { get; }
    public ExpenseClaimDecisions Decisions { get; }
    public IStatutoryRemittanceService Remittances { get; }
    public IPayrollInputProvider LoanProvider { get; }
    public IPayrollInputProvider ExpenseProvider { get; }

    public LifeHarness(Guid? org = null, string? dbName = null)
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        H = new HrmHarness(org, dbName);
        var posting = new HrmPostingHelper(Gateway);
        Ledger = new LoanLedger(H.Db);
        Loans = new LoanService(H.Db, H.Scope, Ledger, posting);
        Settlements = new FinalSettlementService(H.Db, H.Scope, H.Directory, Ledger, posting);
        Increments = new IncrementService(H.Db, H.Scope);
        Letters = new LetterService(H.Db, H.Scope, H.Directory, Store, OrgName);
        Tax = new TaxCertificateService(H.Db, H.Scope, Store, OrgName);
        Categories = new ExpenseCategoryService(H.Db);
        var workflow = DispatchProxy.Create<IWorkflowActionService, WorkflowProxy>();
        Decisions = new ExpenseClaimDecisions(H.Db, H.Directory, posting);
        Claims = new ExpenseClaimService(H.Db, H.Scope, H.Directory, Approvals, workflow, Decisions, posting);
        ((WorkflowProxy)(object)workflow).OnStatus = status => Decisions.UpdateStatusAsync(_current, status);
        Remittances = new StatutoryRemittanceService(H.Db, posting);
        LoanProvider = new LoanPayrollInputProvider(Ledger);
        ExpenseProvider = new ExpenseReimbursementInputProvider(H.Db);
    }

    private Guid _current;
    /// <summary>Which claim the fake engine's next callback is about.</summary>
    public Guid CurrentClaim { set => _current = value; }

    public async Task<LifeHarness> InitAsync()
    {
        await H.Seeder.EnsureAsync(H.Org);
        B = await H.BaseAsync();
        return this;
    }

    public async Task<int> IdOf(Guid employeeUuid) => await H.Db.Employees.IgnoreQueryFilters().Where(e => e.Uuid == employeeUuid).Select(e => e.Id).FirstAsync();

    /// <summary>Approved salary BASIC + HRENT from 2026-01-01.</summary>
    public async Task<int> SalaryAsync(int employeeId, decimal basic = 60000m, decimal rent = 40000m, DateOnly? from = null)
    {
        var components = await H.Db.SalaryComponents.IgnoreQueryFilters().Where(c => c.OrganizationId == H.Org).ToDictionaryAsync(c => c.Code);
        var structure = await H.Db.SalaryStructures.IgnoreQueryFilters().FirstAsync(s => s.OrganizationId == H.Org);
        var row = new SalaryAssignment
        {
            OrganizationId = H.Org, EmployeeId = employeeId, StructureId = structure.Id, EffectiveFrom = from ?? new DateOnly(2026, 1, 1), ApprovalStatus = SalaryAssignmentStatuses.Approved,
            Lines =
            {
                new SalaryAssignmentLine { OrganizationId = H.Org, ComponentId = components["BASIC"].Id, Amount = basic },
                new SalaryAssignmentLine { OrganizationId = H.Org, ComponentId = components["HRENT"].Id, Amount = rent },
            }
        };
        H.Db.SalaryAssignments.Add(row);
        await H.Db.SaveChangesAsync();
        return row.Id;
    }

    public async Task<PayrollRun> RunAsync(string number, string status = PayrollRunStatuses.Calculated, int year = 2026, int month = 3)
    {
        var start = new DateOnly(year, month, 1);
        var run = new PayrollRun { OrganizationId = H.Org, RunNumber = number, PeriodStart = start, PeriodEnd = start.AddMonths(1).AddDays(-1), PayDate = start.AddMonths(1), Status = status, CurrencyCode = "PKR" };
        H.Db.PayrollRuns.Add(run);
        await H.Db.SaveChangesAsync();
        return run;
    }

    public async Task SetRunStatusAsync(PayrollRun run, string status)
    {
        run.Status = status;
        await H.Db.SaveChangesAsync();
    }

    public async Task<LoanModel> DisbursedLoanAsync(Guid employee, decimal principal = 3000m, int count = 3, string type = LoanTypes.Personal, DateOnly? start = null)
    {
        var admin = H.Admin();
        var startMonth = start ?? new DateOnly(2026, 3, 1);
        var loan = await Loans.CreateAsync(admin, new LoanUpsert { EmployeeUuid = employee, Type = type, Principal = principal, InstallmentCount = count, StartMonth = startMonth });
        await Loans.ApproveAsync(admin, loan.Uuid);
        return await Loans.DisburseAsync(admin, loan.Uuid, new LoanDisburseRequest { DisbursedOn = startMonth, PaidFromAccount = "Bank" });
    }

    public static string Json(FinancialTransaction t) => JsonSerializer.Serialize(t);
}


