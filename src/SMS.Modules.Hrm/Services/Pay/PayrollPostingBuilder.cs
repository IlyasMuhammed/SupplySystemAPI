using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Hrm.Services.Pay;

/// <summary>
/// Tasks 290–296, E-7, E-8. The summarized journal of a run, from its snapshot only: never a name, an employee number or an amount of one person
/// (G-11). Expense lines per role × cost center; balance-sheet lines once, on the payroll cost center the run snapshotted (BR-G5-003). Blocked lines
/// are not in it. Sorted, so the same run always yields the same payload and the gateway's fingerprint holds on a retry.
/// </summary>
internal sealed class PayrollPostingBuilder : IPayrollPostingBuilder
{
    /// <summary>A difference bigger than this is a bug, not rounding: refuse to hide it in DEFAULT_ROUNDING.</summary>
    private const decimal MaxRounding = 1m;

    private readonly HrmDbContext _db;
    public PayrollPostingBuilder(HrmDbContext db) => _db = db;

    private async Task<(List<PayrollRunLineComponent> Rows, Dictionary<int, string?> LineCenters, int Headcount)> LoadAsync(PayrollRun run, CancellationToken ct)
    {
        var lines = await _db.PayrollRunLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.RunId == run.Id && l.OrganizationId == run.OrganizationId && !l.IsBlocked)
            .Select(l => new { l.Id, l.CostCenterCode }).ToListAsync(ct);
        var ids = lines.Select(l => l.Id).ToList();
        var rows = await _db.PayrollRunLineComponents.IgnoreQueryFilters().AsNoTracking().Where(c => ids.Contains(c.LineId)).ToListAsync(ct);
        return (rows, lines.ToDictionary(l => l.Id, l => l.CostCenterCode), lines.Count);
    }

    public async Task<FinancialTransaction> BuildAsync(PayrollRun run, CancellationToken ct = default)
    {
        var (rows, lineCenters, headcount) = await LoadAsync(run, ct);
        var payrollCenter = rows.FirstOrDefault(r => r.Code == PayrollCalculationEngine.NetCode)?.CostCenterCode;

        var debit = new Dictionary<(string Role, string? Center), decimal>();
        var credit = new Dictionary<(string Role, string? Center), decimal>();
        static void Add(Dictionary<(string, string?), decimal> side, string role, string? center, decimal amount) =>
            side[(role, center)] = side.GetValueOrDefault((role, center)) + amount;

        foreach (var row in rows)
        {
            var center = row.CostCenterCode ?? lineCenters.GetValueOrDefault(row.LineId);
            switch (row.Type)
            {
                case SalaryComponentTypes.Earning:
                    var role = row.PostingRole!;
                    Add(debit, role, PayrollRoles.IsExpense(role) ? center : payrollCenter, row.Amount);
                    break;
                case SalaryComponentTypes.Deduction:
                    Add(credit, row.PostingRole!, payrollCenter, row.Amount);
                    break;
                case SalaryComponentTypes.EmployerContribution:
                    Add(debit, row.ExpenseRole!, center, row.Amount);
                    Add(credit, row.PostingRole!, payrollCenter, row.Amount);
                    break;
                case SalaryComponentTypes.Result:
                    Add(credit, row.PostingRole ?? PayrollRoles.SalaryPayable, payrollCenter, row.Amount);
                    break;
            }
        }

        // A signed total (a negative correction) goes on the other side: gateway lines are non-negative and one-sided.
        var entries = new List<(string Role, string? Center, decimal Debit, decimal Credit)>();
        foreach (var ((r, c), amount) in debit) if (amount != 0m) entries.Add((r, c, amount > 0m ? amount : 0m, amount < 0m ? -amount : 0m));
        foreach (var ((r, c), amount) in credit) if (amount != 0m) entries.Add((r, c, amount < 0m ? -amount : 0m, amount > 0m ? amount : 0m));

        var difference = entries.Sum(e => e.Debit) - entries.Sum(e => e.Credit);
        if (Math.Abs(difference) >= MaxRounding)
            throw new InvalidOperationException($"Payroll run {run.RunNumber} does not balance by {difference}.");
        if (difference != 0m)
            entries.Add((PayrollRoles.Rounding, payrollCenter, difference < 0m ? -difference : 0m, difference > 0m ? difference : 0m));

        var number = 0;
        var lines = entries
            .OrderBy(e => e.Debit > 0m ? 0 : 1)
            .ThenBy(e => e.Role, StringComparer.Ordinal).ThenBy(e => e.Center, StringComparer.Ordinal)
            .Select(e => new FinancialTransactionLine
            {
                LineNumber = ++number, AccountRole = e.Role, Debit = e.Debit, Credit = e.Credit, CostCenter = e.Center,
                Label = e.Center is null ? PayrollRoles.Label(e.Role) : $"{PayrollRoles.Label(e.Role)} - {e.Center}"
            }).ToList();

        return new FinancialTransaction
        {
            DocumentType = FinDocType.PayrollRun, SourceDocumentId = run.Uuid, SourceDocumentNumber = run.RunNumber, SourceModule = PostingSourceModules.Hrm,
            TransactionDate = (run.PostingDate ?? run.PayDate).ToDateTime(TimeOnly.MinValue), CurrencyCode = run.CurrencyCode,
            Description = $"Payroll {run.RunNumber}, {run.PeriodStart:yyyy-MM-dd} to {run.PeriodEnd:yyyy-MM-dd}, {headcount} employees",
            Lines = lines,
            Metadata = new FinancialDocumentMetadata { Reference = run.RunNumber }
        };
    }

    public async Task<FinancialTransaction> BuildPaymentAsync(PayrollRun run, DateOnly paidOn, CancellationToken ct = default)
    {
        var (rows, _, headcount) = await LoadAsync(run, ct);
        var net = rows.Where(r => r.Code == PayrollCalculationEngine.NetCode).ToList();
        var total = net.Sum(r => r.Amount);
        var payrollCenter = net.FirstOrDefault()?.CostCenterCode;
        var payableRole = net.FirstOrDefault()?.PostingRole ?? PayrollRoles.SalaryPayable;
        var moneyRole = run.PaidFromAccount == HrmPaymentAccounts.Cash ? PayrollRoles.Cash : PayrollRoles.Bank;
        return new FinancialTransaction
        {
            DocumentType = FinDocType.PayrollPayment, SourceDocumentId = run.Uuid, SourceDocumentNumber = run.RunNumber, SourceModule = PostingSourceModules.Hrm,
            TransactionDate = paidOn.ToDateTime(TimeOnly.MinValue), CurrencyCode = run.CurrencyCode,
            Description = $"Salaries paid {run.RunNumber}, {headcount} employees",
            Lines =
            [
                new FinancialTransactionLine { LineNumber = 1, AccountRole = payableRole, Debit = total, CostCenter = payrollCenter, Label = PayrollRoles.Label(payableRole) },
                new FinancialTransactionLine { LineNumber = 2, AccountRole = moneyRole, Credit = total, CostCenter = payrollCenter, Label = PayrollRoles.Label(moneyRole) },
            ],
            Metadata = new FinancialDocumentMetadata { Reference = run.PaymentReference }
        };
    }
}
