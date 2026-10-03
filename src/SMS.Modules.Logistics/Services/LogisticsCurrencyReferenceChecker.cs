using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// Tells Lookups whether a catalog currency is in use by Logistics, so Lookups refuses to delete it or
/// change its code (SAP alignment, work package A). Logistics stores currencies as ISO code strings, so
/// the id Lookups asks about is turned into its code first, through <see cref="ICurrencyCodeLookup"/>; an
/// id that is not a currency (a lookup value, say — the same checkers answer for those) has no code and
/// is simply not referenced.
/// <para>
/// What counts: carrier bills (<c>CarrierInvoice.Currency</c>), cash on delivery
/// (<c>CodCollection.Currency</c>), consignments (<c>CodCurrency</c>, <c>FreightCurrency</c>), carriers
/// (<c>DefaultCurrency</c>), freight accruals (<c>Currency</c>, <c>BookedCurrency</c>), rate cards
/// (<c>Currency</c>) and the carrier command ledger (<c>CostCurrency</c>). Stored codes are compared
/// trimmed and case-insensitively, as every reader compares them.
/// </para>
/// <para>
/// Every organization's rows count — the catalog is shared by all of them — hence IgnoreQueryFilters.
/// Deleted rows count too, the conservative rule: bills, accruals, COD and consignments are business
/// documents kept on record with their currency, a deleted carrier is still the carrier of the documents
/// that point at it, and a deleted rate card is held to the same rule rather than assumed unread. The
/// command ledger is never deleted.
/// </para>
/// </summary>
internal sealed class LogisticsCurrencyReferenceChecker : ILookupReferenceChecker
{
    private readonly LogisticsDbContext  _db;
    private readonly ICurrencyCodeLookup _currencies;

    public LogisticsCurrencyReferenceChecker(LogisticsDbContext db, ICurrencyCodeLookup currencies)
    {
        _db         = db;
        _currencies = currencies;
    }

    public bool IsValueReferenced(Guid lookupValueId)
    {
        if (lookupValueId == Guid.Empty) return false;

        // The checker contract is synchronous; ASP.NET Core has no SynchronizationContext, so blocking cannot deadlock.
        var code = _currencies.GetCodeAsync(lookupValueId).GetAwaiter().GetResult()?.Trim();
        if (string.IsNullOrEmpty(code)) return false;

        var upper = code.ToUpperInvariant();

        return _db.CarrierInvoices.IgnoreQueryFilters()
                   .Any(i => i.Currency.Trim().ToUpper() == upper)
            || _db.CodCollections.IgnoreQueryFilters()
                   .Any(c => c.Currency.Trim().ToUpper() == upper)
            || _db.Consignments.IgnoreQueryFilters()
                   .Any(c => (c.CodCurrency != null && c.CodCurrency.Trim().ToUpper() == upper)
                          || (c.FreightCurrency != null && c.FreightCurrency.Trim().ToUpper() == upper))
            || _db.Carriers.IgnoreQueryFilters()
                   .Any(c => c.DefaultCurrency != null && c.DefaultCurrency.Trim().ToUpper() == upper)
            || _db.FreightAccruals.IgnoreQueryFilters()
                   .Any(a => a.Currency.Trim().ToUpper() == upper
                          || (a.BookedCurrency != null && a.BookedCurrency.Trim().ToUpper() == upper))
            || _db.RateCards.IgnoreQueryFilters()
                   .Any(r => r.Currency.Trim().ToUpper() == upper)
            || _db.CarrierCommands.IgnoreQueryFilters()
                   .Any(c => c.CostCurrency != null && c.CostCurrency.Trim().ToUpper() == upper);
    }
}
