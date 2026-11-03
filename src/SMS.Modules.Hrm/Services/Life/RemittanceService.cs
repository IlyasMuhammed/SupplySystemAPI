using Microsoft.EntityFrameworkCore;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Models;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Hrm.Services.Life;

public interface IStatutoryRemittanceService
{
    Task<IReadOnlyList<RemittanceTrackerRow>> TrackerAsync(HrmCaller caller, int? year, string? type, CancellationToken ct = default);
    Task<IReadOnlyList<RemittanceModel>> ListAsync(HrmCaller caller, string? type, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<RemittanceModel> CreateAsync(HrmCaller caller, RemittanceCreate request, CancellationToken ct = default);
    Task<RemittanceModel> RetryPostingAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default);
}

internal sealed class StatutoryRemittanceService : LifeServiceBase, IStatutoryRemittanceService
{
    private static readonly string[] Finalized = [PayrollRunStatuses.Finalized, PayrollRunStatuses.Posted, PayrollRunStatuses.Paid];

    internal static string PayableRole(string type) => type switch
    {
        RemittanceTypes.IncomeTax => "DEFAULT_EMPLOYEE_TAX_PAYABLE",
        RemittanceTypes.Eobi => "DEFAULT_EOBI_PAYABLE",
        RemittanceTypes.ProvidentFund => "DEFAULT_PF_PAYABLE",
        RemittanceTypes.SocialSecurity => "DEFAULT_SOCIAL_SECURITY_PAYABLE",
        _ => throw new BadRequestException($"Unknown remittance type {type}.")
    };

    /// <summary>The payroll components whose total is owed to the authority: employee and employer sides.</summary>
    private static string[] Components(string type) => type switch
    {
        RemittanceTypes.IncomeTax => ["ITAX"],
        RemittanceTypes.Eobi => ["EOBI_EE", "EOBI_ER"],
        RemittanceTypes.ProvidentFund => ["PF_EE", "PF_ER"],
        _ => ["SS_EE", "SS_ER"]
    };

    public StatutoryRemittanceService(HrmDbContext db, IHrmPostingHelper posting, ICurrencyService? currency = null, IOrgCurrencyLookup? currencies = null) : base(db, posting, currency, currencies) { }

    private static RemittanceModel Map(StatutoryRemittance r) => new()
    {
        Uuid = r.Uuid, RemittanceNumber = r.RemittanceNumber, RemittanceType = r.RemittanceType, PeriodStart = r.PeriodStart, PeriodEnd = r.PeriodEnd, Amount = r.Amount, PaidOn = r.PaidOn, PaidFromAccount = r.PaidFromAccount,
        Reference = r.Reference, AuthorityName = r.AuthorityName, Notes = r.Notes, PostingState = r.PostingState, PostingNote = r.PostingNote
    };

    private async Task<List<(DateOnly Start, DateOnly End, decimal Amount)>> CalculatedAsync(Guid org, string type, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var codes = Components(type);
        var rows = await Db.PayrollRunLineComponents.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == org && codes.Contains(c.Code) && !c.Line!.IsBlocked && Finalized.Contains(c.Line.Run!.Status) && c.Line.Run.PeriodEnd >= from && c.Line.Run.PeriodStart <= to)
            .Select(c => new { c.Line!.Run!.PeriodStart, c.Line.Run.PeriodEnd, c.Amount }).ToListAsync(ct);
        return rows.GroupBy(r => (r.PeriodStart, r.PeriodEnd)).Select(g => (g.Key.PeriodStart, g.Key.PeriodEnd, g.Sum(r => r.Amount))).ToList();
    }

    public async Task<IReadOnlyList<RemittanceTrackerRow>> TrackerAsync(HrmCaller caller, int? year, string? type, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var y = year ?? HrmValidate.Today.Year;
        var (from, to) = (new DateOnly(y, 1, 1), new DateOnly(y, 12, 31));
        var types = string.IsNullOrWhiteSpace(type) ? RemittanceTypes.All : [HrmValidate.OneOf(type, RemittanceTypes.All, "type")];
        var result = new List<RemittanceTrackerRow>();
        foreach (var t in types)
        {
            var calculated = await CalculatedAsync(org, t, from, to, ct);
            var paid = await Db.StatutoryRemittances.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == org && r.RemittanceType == t && r.PeriodEnd >= from && r.PeriodStart <= to)
                .Select(r => new { r.PeriodStart, r.PeriodEnd, r.Amount }).ToListAsync(ct);
            var periods = calculated.Select(c => (c.Start, c.End)).Concat(paid.Select(p => (p.PeriodStart, p.PeriodEnd))).Distinct().OrderBy(p => p.Item1);
            foreach (var (start, end) in periods)
            {
                var c = calculated.Where(x => x.Start == start && x.End == end).Sum(x => x.Amount);
                var p = paid.Where(x => x.PeriodStart == start && x.PeriodEnd == end).Sum(x => x.Amount);
                result.Add(new RemittanceTrackerRow { Type = t, PeriodStart = start, PeriodEnd = end, Calculated = c, Paid = p, Balance = c - p });
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<RemittanceModel>> ListAsync(HrmCaller caller, string? type, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var query = Db.StatutoryRemittances.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == caller.OrganizationId);
        if (!string.IsNullOrWhiteSpace(type)) { var t = HrmValidate.OneOf(type, RemittanceTypes.All, "type"); query = query.Where(r => r.RemittanceType == t); }
        if (from is { } f) query = query.Where(r => r.PeriodEnd >= f);
        if (to is { } e) query = query.Where(r => r.PeriodStart <= e);
        return (await query.OrderByDescending(r => r.PeriodStart).ThenByDescending(r => r.Id).ToListAsync(ct)).Select(Map).ToList();
    }

    /// <summary>TS-G13-001. Vendor-less: the authority's details stay in HRM.</summary>
    internal static FinancialTransaction Build(StatutoryRemittance r, string currency)
    {
        var label = $"Statutory remittance {r.RemittanceNumber} {r.RemittanceType}";
        return new FinancialTransaction
        {
            DocumentType = FinDocType.StatutoryRemittance, SourceModule = PostingSourceModules.Hrm, SourceDocumentId = r.Uuid, SourceDocumentNumber = r.RemittanceNumber,
            TransactionDate = LifeMoney.Date(r.PaidOn), CurrencyCode = currency, Description = label, Metadata = new FinancialDocumentMetadata { Reference = r.Reference },
            Lines = LifeMoney.Number(
            [
                new() { AccountRole = PayableRole(r.RemittanceType), Debit = r.Amount, Label = label },
                new() { AccountRole = LifeMoney.CashRole(r.PaidFromAccount), Credit = r.Amount, Label = label }
            ])
        };
    }

    private async Task PostAsync(HrmCaller caller, StatutoryRemittance row, CancellationToken ct)
    {
        var outcome = await Posting.PostAsync(Build(row, await BaseCurrencyAsync(caller.OrganizationId, ct)), ct);
        row.PostingState = outcome.State;
        row.PostingNote = outcome.Note;
        Stamp(row, caller);
        await SaveAsync(ct, "This remittance");
    }

    public async Task<RemittanceModel> CreateAsync(HrmCaller caller, RemittanceCreate request, CancellationToken ct = default)
    {
        var org = caller.OrganizationId;
        var type = HrmValidate.OneOf(request.RemittanceType, RemittanceTypes.All, "remittanceType");
        if (request.PeriodStart == default || request.PeriodEnd < request.PeriodStart) throw new BadRequestException("The period is invalid.");
        if (request.PaidOn == default) throw new BadRequestException("The payment date is required.");
        if (request.Amount <= 0 || request.Amount != LifeMoney.Round(request.Amount)) throw new BadRequestException("The amount must be positive, with at most 2 decimals.");
        var row = new StatutoryRemittance
        {
            RemittanceType = type, PeriodStart = request.PeriodStart, PeriodEnd = request.PeriodEnd, Amount = request.Amount, PaidOn = request.PaidOn,
            PaidFromAccount = HrmValidate.OneOf(request.PaidFromAccount ?? HrmPaymentAccounts.Bank, HrmPaymentAccounts.All, "paidFromAccount"), Reference = HrmValidate.Optional(request.Reference, 100, "Reference"),
            AuthorityName = HrmValidate.Optional(request.AuthorityName, 200, "Authority"), Notes = HrmValidate.Optional(request.Notes, 1000, "Notes"), PostingState = HrmPostingStates.Pending
        };
        Stamp(row, caller, isNew: true);

        await Db.InTransactionAsync(async () =>
        {
            await LockAsync($"hrm-remittance-{org:N}-{type}", ct);
            var existing = await Db.StatutoryRemittances.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.OrganizationId == org && r.RemittanceType == type && r.Id != row.Id && r.PeriodEnd >= row.PeriodStart && r.PeriodStart <= row.PeriodEnd).Select(r => new { r.Amount, r.Reference, r.PeriodStart, r.PeriodEnd }).ToListAsync(ct);
            if (row.Reference is not null && existing.Any(r => r.Reference == row.Reference && r.PeriodStart == row.PeriodStart && r.PeriodEnd == row.PeriodEnd))
                throw HrmErrors.Conflict("REMITTANCE_DUPLICATE", $"A {type} remittance with reference {row.Reference} is already recorded for this period.");
            var calculated = (await CalculatedAsync(org, type, row.PeriodStart, row.PeriodEnd, ct)).Sum(c => c.Amount);
            var paid = existing.Sum(r => r.Amount);
            if (paid + row.Amount > calculated && !request.Force)
                throw HrmErrors.Bad("REMITTANCE_OVERPAID", $"Payroll calculated {calculated:N2} for this period and {paid:N2} is already paid; {row.Amount:N2} more exceeds it. Send force to record it anyway.");
            if (Db.Entry(row).State == EntityState.Detached) Db.StatutoryRemittances.Add(row);
            await SaveNumberedAsync($"RM-{HrmValidate.Today.Year}", Db.StatutoryRemittances.IgnoreQueryFilters().Where(r => r.OrganizationId == org).Select(r => r.RemittanceNumber), n => row.RemittanceNumber = n, ct);
        }, ct);

        await PostAsync(caller, row, ct);
        return Map(row);
    }

    public async Task<RemittanceModel> RetryPostingAsync(HrmCaller caller, Guid uuid, CancellationToken ct = default)
    {
        var row = await Db.StatutoryRemittances.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.OrganizationId == caller.OrganizationId && r.Uuid == uuid, ct) ?? throw new NotFoundException("Remittance", uuid);
        await PostAsync(caller, row, ct);
        return Map(row);
    }
}
