using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Hrm.Services.Post;

/// <summary>
/// G-3 / D-10. What the run will post, from PAY's own builder (so this checks exactly the lines the gateway will get), asked of each active
/// target. QuickBooks counts only while the document type's sync control sends it: off + held, the journal waits and is validated
/// when released, and the accountant's enable refuses until this same check passes (D-POST-2). Mappings only; G-21 is PAY's.
/// </summary>
internal sealed class PayrollPostingReadinessService : IPayrollPostingReadinessService
{
    private readonly HrmDbContext _db;
    private readonly IPayrollPostingBuilder _builder;
    private readonly IPostingRoleReadiness _glRoles;
    private readonly IQuickBooksPostingReadiness _quickBooks;
    private readonly IReadOnlyList<IFinancialPostingTarget> _targets;
    private readonly IHrmCostCenterSource _costCenters;

    public PayrollPostingReadinessService(
        HrmDbContext db, IPayrollPostingBuilder builder, IPostingRoleReadiness glRoles, IQuickBooksPostingReadiness quickBooks,
        IEnumerable<IFinancialPostingTarget> targets, IHrmCostCenterSource costCenters)
    {
        _db = db;
        _builder = builder;
        _glRoles = glRoles;
        _quickBooks = quickBooks;
        _targets = targets.ToList();
        _costCenters = costCenters;
    }

    public async Task<PayrollPostingReadiness> CheckAsync(Guid organizationId, int runId, CancellationToken ct = default)
    {
        var run = await _db.PayrollRuns.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(r => r.OrganizationId == organizationId && r.Id == runId, ct)
            ?? throw new NotFoundException("Payroll run", runId);

        // Asked before Finalize too; the date plays no part in what is checked, and this copy is not tracked.
        run.PostingDate ??= run.PayDate;
        return await CheckTransactionAsync(organizationId, await _builder.BuildAsync(run, ct), ct);
    }

    public async Task<PayrollPostingReadiness> CheckTransactionAsync(Guid organizationId, FinancialTransaction tx, CancellationToken ct = default)
    {
        var glActive = await IsActiveAsync(PostingTargetCodes.InternalGl, ct);
        var qbActive = await IsActiveAsync(PostingTargetCodes.QuickBooks, ct);

        var lines = tx.Lines.Where(l => l.Debit != 0 || l.Credit != 0).ToList();
        var roles = lines.Where(l => string.IsNullOrWhiteSpace(l.AccountCode) && !string.IsNullOrWhiteSpace(l.AccountRole))
            .Select(l => l.AccountRole!.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var missingRoles = glActive ? await _glRoles.MissingRolesAsync(roles, ct) : [];

        var result = new PayrollPostingReadiness
        {
            NoTarget = !glActive && !qbActive,
            InternalGlActive = glActive,
            QuickBooksActive = qbActive,
            InternalGlReady = missingRoles.Count == 0,
            MissingGlRoles = missingRoles
        };
        if (!qbActive) return result with { IsReady = result.InternalGlReady };

        // A line with no cost center of its own posts on the payroll cost center (BR-G5-003), so that one needs its Class too.
        var codes = lines.Where(l => !string.IsNullOrWhiteSpace(l.CostCenter)).Select(l => l.CostCenter!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (lines.Any(l => string.IsNullOrWhiteSpace(l.CostCenter)))
        {
            var payroll = (await _costCenters.ListAsync(organizationId, ct)).FirstOrDefault(c => c.IsPayrollCostCenter);
            if (payroll is not null && !codes.Contains(payroll.Code, StringComparer.OrdinalIgnoreCase)) codes.Add(payroll.Code);
        }

        var quickBooks = await _quickBooks.CheckAsync(new QuickBooksPostingNeeds(
            tx.DocumentType.ToString(),
            lines.Select(l => string.IsNullOrWhiteSpace(l.AccountCode)
                    ? new QuickBooksAccountNeed(l.AccountRole?.Trim(), null)
                    : new QuickBooksAccountNeed(null, l.AccountCode.Trim())).Distinct().ToList(),
            codes, tx.CurrencyCode), ct);

        var quickBooksReady = !quickBooks.SendsNow || quickBooks.IsReady;
        return result with
        {
            IsReady = result.InternalGlReady && quickBooksReady,
            QuickBooksReady = quickBooksReady,
            MissingQboAccounts = quickBooks.MissingAccounts.Select(a => new MissingQboAccount(a.Role ?? a.AccountCode ?? "?", a.AccountCode)).ToList(),
            MissingCostCenters = quickBooks.MissingCostCenters.Select(c => new MissingCostCenter(c.Code, c.Name)).ToList(),
            CurrencyMismatch = quickBooks.CurrencyMismatch is { } m ? new CurrencyMismatch(m.OrgCurrency, m.QboCurrency) : null
        };
    }

    private async Task<bool> IsActiveAsync(string code, CancellationToken ct)
    {
        var target = _targets.FirstOrDefault(t => t.TargetCode == code);
        return target is not null && await target.IsActiveAsync(ct);
    }
}
