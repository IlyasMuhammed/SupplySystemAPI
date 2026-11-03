using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Security;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Hrm.Services.Life;

/// <summary>§13 — D-11 in one place.</summary>
internal sealed class HrmPostingHelper : IHrmPostingHelper
{
    private readonly IFinancialPostingGateway _gateway;
    private readonly ILogger<HrmPostingHelper>? _log;

    public HrmPostingHelper(IFinancialPostingGateway gateway, ILogger<HrmPostingHelper>? log = null)
    {
        _gateway = gateway;
        _log = log;
    }

    public async Task<HrmPostingOutcome> PostAsync(FinancialTransaction transaction, CancellationToken ct = default)
    {
        try
        {
            return Map(await _gateway.PostAsync(transaction, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The document is committed: a failure here is a state to retry, never a 500 that suggests the document was not saved.
            _log?.LogError(ex, "Posting {Type} {Id} threw.", transaction.DocumentType, transaction.SourceDocumentId);
            return new HrmPostingOutcome(HrmPostingStates.Failed, Trim("The posting could not be sent: " + ex.Message));
        }
    }

    internal static HrmPostingOutcome Map(PostingResult result) => result.Status switch
    {
        PostingStatus.Success or PostingStatus.DuplicateSkipped => new(HrmPostingStates.Posted, null),
        PostingStatus.NoTargetConfigured or PostingStatus.ModuleNotLicensed => new(HrmPostingStates.NoTarget, "No posting required."),
        _ => new(HrmPostingStates.Failed, Trim(result.Message ?? "The posting failed."))
    };

    private static string Trim(string note) => note.Length <= 1000 ? note : note[..1000];
}

internal static class LifeMoney
{
    public static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    public static DateTime Date(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);

    /// <summary>Splits an amount by percentage shares so the parts sum to it exactly: the last share takes the rounding.</summary>
    public static IReadOnlyList<(string? CostCenter, decimal Amount)> Split(decimal amount, IReadOnlyList<CostCenterShare> shares)
    {
        if (shares.Count == 0) return [(null, amount)];
        var parts = new List<(string?, decimal)>();
        var left = amount;
        for (var i = 0; i < shares.Count; i++)
        {
            var part = i == shares.Count - 1 ? left : Round(amount * shares[i].Percent / 100m);
            left -= part;
            if (part != 0) parts.Add((shares[i].CostCenterCode, part));
        }
        return parts;
    }

    /// <summary>Numbers the lines and refuses an unbalanced transaction here, with a clear message, rather than at a target.</summary>
    public static IReadOnlyList<FinancialTransactionLine> Number(IEnumerable<FinancialTransactionLine> lines)
    {
        var numbered = lines.Where(l => l.Debit != 0 || l.Credit != 0).Select((l, i) => new FinancialTransactionLine
        {
            LineNumber = i + 1, AccountRole = l.AccountRole, AccountCode = l.AccountCode, Debit = l.Debit, Credit = l.Credit, TaxCode = l.TaxCode,
            TaxAmount = l.TaxAmount, Label = l.Label, CostCenter = l.CostCenter
        }).ToList();
        if (numbered.Sum(l => l.Debit) != numbered.Sum(l => l.Credit))
            throw new InvalidOperationException("An HRM posting does not balance.");
        return numbered;
    }

    public static string CashRole(string paidFrom) => paidFrom == HrmPaymentAccounts.Cash ? "DEFAULT_CASH" : "DEFAULT_BANK";
}

/// <summary>
/// The name snapshot for a "...ByName" column: the known name, else <see cref="HrmUserNames"/> (a failing directory never fails the action).
/// HrmCaller.UserName is now resolved centrally for requests; this still serves the engine's callback, which has no HrmCaller.
/// </summary>
internal sealed class LifeUserNames
{
    private readonly HrmUserNames _names;
    public LifeUserNames(IOrgUserDirectory? users = null) => _names = new HrmUserNames(users);

    public async Task<string?> NameAsync(Guid org, int? userId, string? known, CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace(known) ? known : await _names.NameAsync(org, userId, known, ct);
}

/// <summary>Numbers, locks and the base currency: what every LIFE service needs.</summary>
internal abstract class LifeServiceBase : HrmServiceBase
{
    protected readonly IHrmPostingHelper Posting;
    private readonly ICurrencyService? _currency;
    private readonly IOrgCurrencyLookup? _currencies;

    protected LifeServiceBase(HrmDbContext db, IHrmPostingHelper posting, ICurrencyService? currency, IOrgCurrencyLookup? currencies) : base(db)
    {
        Posting = posting;
        _currency = currency;
        _currencies = currencies;
    }

    /// <summary>Payroll and HR documents are in the organization's base currency (OD-12). PKR is the platform's own fallback (D-7).</summary>
    protected async Task<string> BaseCurrencyAsync(Guid org, CancellationToken ct)
    {
        if (_currency is null || _currencies is null) return "PKR";
        var id = await _currency.GetBaseCurrencyIdAsync(org, TransactionDomain.Service, ct);
        return (await _currencies.GetAsync(org, id, ct))?.Code ?? "PKR";
    }

    /// <summary>A lock for the current transaction, for a rule no index enforces. No-op on the in-memory provider.</summary>
    protected async Task LockAsync(string resource, CancellationToken ct)
    {
        if (!Db.Database.IsRelational()) return;
        await Db.Database.ExecuteSqlRawAsync(
            "EXEC sp_getapplock @Resource = {0}, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 15000", [resource], ct);
    }

    protected static string NextNumber(string prefix, string? latest)
    {
        var next = latest is not null && int.TryParse(latest[(prefix.Length + 1)..], out var n) ? n + 1 : 1;
        return $"{prefix}-{next:D5}";
    }

    /// <summary>Saves a new numbered row. Two creators can read the same latest number: the unique index refuses one, which then takes the next.</summary>
    protected async Task SaveNumberedAsync(string prefix, IQueryable<string> numbers, Action<string> assign, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var latest = await numbers.Where(n => n.StartsWith(prefix + "-")).OrderByDescending(n => n).FirstOrDefaultAsync(ct);
            assign(NextNumber(prefix, latest));
            try
            {
                await Db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException ex) when (IsDuplicate(ex) && attempt < 6) { }
        }
    }

    protected async Task<HrmPostingOutcome> PostSafelyAsync(Func<Task<FinancialTransaction>> build, CancellationToken ct)
    {
        FinancialTransaction tx;
        try
        {
            tx = await build();
        }
        catch (ConflictException ex)
        {
            return new HrmPostingOutcome(HrmPostingStates.Failed, ex.Message);
        }
        return await Posting.PostAsync(tx, ct);
    }
}
