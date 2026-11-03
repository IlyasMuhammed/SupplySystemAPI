using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway.JournalEntries;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Gateway.Validation;

// A49 G-1, G-3, G-5, G-14. Runs on store, on release of a held entry and again before the send, so a mapping removed in between is caught,
// and the blocked-record sweep lets an entry through once its mapping is fixed.
internal sealed partial class PayloadValidator
{
    public const string CurrencyMismatchCode = "JE_CURRENCY_MISMATCH";
    public const string AccountUnmappedCode = "JE_ACCOUNT_UNMAPPED";
    public const string CostCenterUnmappedCode = "JE_COST_CENTER_UNMAPPED";

    private async Task ValidateJournalEntryAsync(JournalEntryPayload p, IntegrationConnection connection, PayloadValidationResult r, CancellationToken ct)
    {
        CheckDocNumber(p.DocNumber, "journal entry", r);

        if (p.Lines is not { Count: > 0 })
        {
            r.Error("lines", "JE_EMPTY", "The journal entry has no lines.");
            return;
        }

        var debit  = p.Lines.Where(l => l.PostingType == JournalEntryPostingTypes.Debit).Sum(l => l.Amount);
        var credit = p.Lines.Where(l => l.PostingType == JournalEntryPostingTypes.Credit).Sum(l => l.Amount);
        if (debit != credit)
            r.Error("lines", "JE_UNBALANCED", $"The journal entry does not balance: debits {debit:0.00}, credits {credit:0.00}.");

        await CheckJournalCurrencyAsync(p, connection, r, ct);

        if (_journalRefs is null)
        {
            r.Error("lines", "JE_REFS_UNAVAILABLE", "This host cannot resolve journal accounts and cost centers.");
            return;
        }

        var refs = await _journalRefs.LoadAsync(connection.Id, ct);

        var accounts = p.Lines.Where(l => refs.Account(l.AccountRole, l.AccountCode) is null)
            .Select(l => l.AccountCode ?? l.AccountRole ?? "?").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (accounts.Count > 0)
            r.Error("lines", AccountUnmappedCode,
                $"No QuickBooks account is mapped for {string.Join(", ", accounts)}. Map them under QuickBooks Integration → Accounts; the entry is sent when they are.");

        var costCenters = p.Lines.Where(l => refs.CostCenter(l.CostCenterCode) is null)
            .Select(l => l.CostCenterCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (costCenters.Count > 0)
            r.Error("lines", CostCenterUnmappedCode,
                $"No QuickBooks Class is mapped for cost center {string.Join(", ", costCenters)}. Map them under QuickBooks Integration → Cost centers; the entry is sent when they are.");

        if (refs.Reference?.Preferences?.ClassTrackingEnabled == false)
            r.Error("lines", "JE_CLASSES_OFF", "The QuickBooks company does not track classes, and a journal entry from payroll is never posted unclassified. Turn classes on in QuickBooks, then refresh its reference data.");

        foreach (var problem in JournalEntryAccountRules.PartyAccounts(p.Lines.Select(l => refs.Account(l.AccountRole, l.AccountCode)), refs.Reference))
            r.Error("lines", "JE_ACCOUNT_NEEDS_PARTY", problem);
    }

    private async Task CheckJournalCurrencyAsync(JournalEntryPayload p, IntegrationConnection connection, PayloadValidationResult r, CancellationToken ct)
    {
        var home = (await _currency.FactsAsync(connection, ct)).HomeCurrency;
        var currency = p.CurrencyCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(currency))
            r.Error("currencyCode", "JE_CURRENCY_REQUIRED", "The journal entry has no currency.");
        else if (home is not null && home != currency)
            r.Error("currencyCode", CurrencyMismatchCode,
                $"This entry is in {currency} but the QuickBooks company's home currency is {home}. Payroll cannot post until they match.");
    }
}
