using SMS.Modules.Tenancy.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

/// <summary>
/// Tells Lookups whether a catalog currency is some organization's base currency, so Lookups refuses to
/// delete it or change its code (SAP alignment, work package A). Organizations are not tenant-filtered:
/// every organization counts, as the catalog is shared by all of them.
/// </summary>
internal sealed class TenancyCurrencyReferenceChecker : ILookupReferenceChecker
{
    private readonly TenancyDbContext _db;

    public TenancyCurrencyReferenceChecker(TenancyDbContext db) => _db = db;

    public bool IsValueReferenced(Guid lookupValueId) =>
        lookupValueId != Guid.Empty && _db.Organizations.Any(o => o.BaseCurrency == lookupValueId);
}
