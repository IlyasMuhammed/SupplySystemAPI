using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services;

/// <summary>
/// Tells Lookups whether a catalog currency is in use by Inventory's prices, so Lookups refuses to delete
/// it or change its code (SAP alignment, work package A). Two tables keep a currency by its id: supplier
/// rates (<c>VariantSupplier.CurrencyId</c>) and pricing rules (<c>PricingRule.CurrencyId</c>). An id that
/// is not a currency (a lookup value, say — the same checkers answer for those) matches neither.
/// <para>
/// Every organization's rows count — the catalog is shared by all of them — hence IgnoreQueryFilters.
/// Neither table has a soft-delete flag, so every row there counts, inactive and expired ones included:
/// an inactive rate or rule can be switched back on, and an expired one is still the price history.
/// The rate-change audit (<c>SupplierRateHistory</c>) is free text about past values, not a reference,
/// and is not searched.
/// </para>
/// </summary>
internal sealed class InventoryCurrencyReferenceChecker : ILookupReferenceChecker
{
    private readonly InventoryDbContext _db;

    public InventoryCurrencyReferenceChecker(InventoryDbContext db) => _db = db;

    public bool IsValueReferenced(Guid lookupValueId) =>
        lookupValueId != Guid.Empty
        && (_db.VariantSuppliers.IgnoreQueryFilters().Any(r => r.CurrencyId == lookupValueId)
         || _db.PricingRules.IgnoreQueryFilters().Any(r => r.CurrencyId == lookupValueId));
}
