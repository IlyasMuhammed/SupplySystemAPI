using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Core.Sync;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Gateway.JournalEntries;

/// <summary>The roles an HRM document type always names, for the accountant's enable check (G-15). A document's real lines are checked when it posts.</summary>
internal static class HrmSyncRoles
{
    public static IReadOnlyList<string> For(string documentType) => documentType switch
    {
        "PayrollRun"          => ["DEFAULT_SALARY_EXPENSE", "DEFAULT_SALARY_PAYABLE"],
        "PayrollPayment"      => ["DEFAULT_SALARY_PAYABLE", "DEFAULT_BANK"],
        "LoanDisbursement"    => ["DEFAULT_EMPLOYEE_LOAN_RECEIVABLE", "DEFAULT_BANK"],
        "Expense"             => ["DEFAULT_EXPENSE_CLAIM_PAYABLE"],
        "ExpensePayment"      => ["DEFAULT_EXPENSE_CLAIM_PAYABLE", "DEFAULT_BANK"],
        "StatutoryRemittance" => ["DEFAULT_EMPLOYEE_TAX_PAYABLE", "DEFAULT_BANK"],
        "FinalSettlement"     => ["DEFAULT_SALARY_PAYABLE"],
        "PayrollProvision"    => ["DEFAULT_GRATUITY_EXPENSE", "DEFAULT_GRATUITY_PROVISION"],
        _                     => []
    };
}

/// <summary>QuickBooks account rules a journal line must meet beyond being mapped.</summary>
internal static class JournalEntryAccountRules
{
    /// <summary>
    /// QuickBooks asks for a customer or vendor on a journal line that hits Accounts Receivable or Accounts Payable, and these entries
    /// carry no party on purpose (G-11, G-13). From memory of Intuit's behaviour, not checked against a real company: no sandbox here.
    /// </summary>
    public static IEnumerable<string> PartyAccounts(IEnumerable<string?> accountIds, RemoteReferenceData? reference)
    {
        if (reference is null) yield break;
        var ids = accountIds.OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var account in reference.Accounts.Where(a => ids.Contains(a.Id)))
            if (account.AccountType is "Accounts Payable" or "Accounts Receivable")
                yield return $"QuickBooks account '{account.Name}' is {account.AccountType}, which needs a customer or vendor on a journal line, and HR entries carry none. Map a liability or other current asset account instead.";
    }
}

/// <summary>A49 G-3, G-5, G-14 for <see cref="IQuickBooksPostingReadiness"/>. Same resolver as the validator, so what it says is what the send will do.</summary>
internal sealed class QuickBooksPostingReadinessService : IQuickBooksPostingReadiness
{
    private readonly IConnectionAccessor _connections;
    private readonly IJournalEntryRefSource _refs;
    private readonly ISyncControlReader _control;
    private readonly ICurrencyRules _currency;
    private readonly IServiceProvider _services;

    public QuickBooksPostingReadinessService(
        IConnectionAccessor connections, IJournalEntryRefSource refs, ISyncControlReader control, ICurrencyRules currency, IServiceProvider services)
    {
        _connections = connections;
        _refs = refs;
        _control = control;
        _currency = currency;
        _services = services;
    }

    public async Task<QuickBooksReadinessResult> CheckAsync(QuickBooksPostingNeeds needs, CancellationToken ct = default)
    {
        if (!await QuickBooksTargetActivity.IsActiveAsync(_services, _connections, ct)) return new QuickBooksReadinessResult();

        var connection = (await _connections.GetCurrentAsync(ct))!;
        var refs = await _refs.LoadAsync(connection.Id, ct);

        var missingAccounts = needs.Accounts.Distinct()
            .Where(a => refs.Account(a.Role, a.AccountCode) is null).ToList();

        var mappings = _services.GetRequiredService<ICostCenterQboMappingService>();
        var missingCenters = new List<QuickBooksCostCenterGap>();
        foreach (var code in needs.CostCenterCodes.Distinct(StringComparer.OrdinalIgnoreCase))
            if (refs.CostCenter(code) is null)
                missingCenters.Add(new QuickBooksCostCenterGap(code, await mappings.NameOfAsync(code, ct)));

        QuickBooksCurrencyMismatch? mismatch = null;
        var home = (await _currency.FactsAsync(connection, ct)).HomeCurrency;
        var org = CurrencyRules.Normalize(needs.CurrencyCode);
        if (home is not null && org is not null && home != org) mismatch = new QuickBooksCurrencyMismatch(org, home);

        var problems = new List<string>();
        if (refs.Reference?.Preferences?.ClassTrackingEnabled == false)
            problems.Add("The QuickBooks company does not track classes; turn them on and refresh its reference data.");
        problems.AddRange(JournalEntryAccountRules.PartyAccounts(needs.Accounts.Select(a => refs.Account(a.Role, a.AccountCode)), refs.Reference));

        var decision = await _control.CheckAsync(connection.Id, needs.DocumentType, ct);
        return new QuickBooksReadinessResult
        {
            Connected          = true,
            SendsNow           = decision.Action == SyncControlAction.Process,
            MissingAccounts    = missingAccounts,
            MissingCostCenters = missingCenters,
            CurrencyMismatch   = mismatch,
            Problems           = problems
        };
    }
}
