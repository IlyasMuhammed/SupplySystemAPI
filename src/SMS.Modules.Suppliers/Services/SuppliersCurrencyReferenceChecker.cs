using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// Tells Lookups whether a catalog currency is any business partner's preferred currency, so Lookups
/// refuses to delete it or change its code (SAP alignment, work package A). Partners keep it by id
/// (<c>BusinessPartner.PreferredCurrency</c>); an id that is not a currency (a lookup value, say — the
/// same checkers answer for those) matches no partner.
/// <para>
/// Every organization's partners count — the catalog is shared by all of them — hence IgnoreQueryFilters.
/// Deleted partners do not count: a partner's preferred currency is a default for new documents, not a
/// document's currency, and nothing reads it once the partner is deleted — every partner read in this
/// module and the QuickBooks sync skip deleted partners, and there is no restore. Documents raised for
/// the partner keep their own currency and are answered for by the modules that own them.
/// </para>
/// </summary>
internal sealed class SuppliersCurrencyReferenceChecker : ILookupReferenceChecker
{
    private readonly SuppliersDbContext _db;

    public SuppliersCurrencyReferenceChecker(SuppliersDbContext db) => _db = db;

    public bool IsValueReferenced(Guid lookupValueId) =>
        lookupValueId != Guid.Empty
        && _db.BusinessPartners.IgnoreQueryFilters().Any(p => !p.IsDelete && p.PreferredCurrency == lookupValueId);
}
