using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// Tells Lookups whether a currency in its catalog is in use by Finance, so it can refuse deleting the
/// currency or changing its code (SAP alignment, work package A). Finance stores currencies as code
/// strings, so the id Lookups asks about is turned into its code first; an id that is not a currency (a
/// lookup value, say — the same checkers answer for those) is simply not referenced here.
/// <para>
/// Every organization's rows count — the catalog is shared by all of them — hence IgnoreQueryFilters.
/// Deleted exchange rates do not count: nothing converts with them any more.
/// </para>
/// </summary>
internal sealed class FinanceCurrencyReferenceChecker : ILookupReferenceChecker
{
    private readonly FinanceDbContext _db;
    private readonly ILookupsService  _lookups;

    public FinanceCurrencyReferenceChecker(FinanceDbContext db, ILookupsService lookups)
    {
        _db      = db;
        _lookups = lookups;
    }

    public bool IsValueReferenced(Guid lookupValueId)
    {
        // A35 (D-1): an organization's currency configuration and its rates reference the catalog Guid directly.
        if (_db.OrgCurrencies.IgnoreQueryFilters().Any(c => c.CurrencyId == lookupValueId)
            || _db.CurrencyRates.IgnoreQueryFilters().Any(r => r.CurrencyId == lookupValueId))
            return true;

        var code = _lookups.GetCurrencies().FirstOrDefault(c => c.Id == lookupValueId)?.Code?.Trim();
        if (string.IsNullOrEmpty(code)) return false;

        var upper = code.ToUpperInvariant();

        return _db.Invoices.IgnoreQueryFilters()
                   .Any(i => i.Currency.ToUpper() == upper || (i.BaseCurrencyCode != null && i.BaseCurrencyCode.ToUpper() == upper))
            || _db.SalesInvoices.IgnoreQueryFilters()
                   .Any(i => i.CurrencyCode.ToUpper() == upper || (i.BaseCurrencyCode != null && i.BaseCurrencyCode.ToUpper() == upper))
            || _db.CustomerPayments.IgnoreQueryFilters()
                   .Any(p => p.CurrencyCode.ToUpper() == upper)
            || _db.CustomerLedgerEntries.IgnoreQueryFilters()
                   .Any(e => e.CurrencyCode.ToUpper() == upper)
            || _db.ExchangeRates.IgnoreQueryFilters()
                   .Any(r => !r.IsDelete && (r.FromCurrencyCode.ToUpper() == upper || r.ToCurrencyCode.ToUpper() == upper));
    }
}
