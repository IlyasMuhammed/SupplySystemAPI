using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// Tells Lookups whether a catalog currency is the currency of any sale order, so Lookups refuses to
/// delete it or change its code (SAP alignment, work package A). Sale orders keep their currency by id
/// (<c>SaleOrder.CurrencyId</c>); an id that is not a currency (a lookup value, say — the same checkers
/// answer for those) matches no sale order. Purchase orders carry no currency of their own.
/// <para>
/// Every organization's sale orders count — the catalog is shared by all of them — hence
/// IgnoreQueryFilters. Deleted sale orders count too: a deleted business document stays on record, and
/// is still shown and audited with its currency.
/// </para>
/// </summary>
internal sealed class DemandCurrencyReferenceChecker : ILookupReferenceChecker
{
    private readonly DemandDbContext _db;

    public DemandCurrencyReferenceChecker(DemandDbContext db) => _db = db;

    public bool IsValueReferenced(Guid lookupValueId) =>
        lookupValueId != Guid.Empty
        && _db.SaleOrders.IgnoreQueryFilters().Any(o => o.CurrencyId == lookupValueId);
}
