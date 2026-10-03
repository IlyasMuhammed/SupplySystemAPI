using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Core.Connections;

/// <summary>
/// Tells Lookups whether a catalog currency is the home currency of any accounting connection, so Lookups
/// refuses to delete it or change its code (SAP alignment, work package A). A connection keeps the
/// company's home currency as an ISO code (<c>IntegrationConnection.HomeCurrencyCode</c>), so the id Lookups
/// asks about is turned into its code first, through <see cref="ICurrencyCodeLookup"/> — this module
/// references nothing but SMS.Shared. An id that is not a currency (a lookup value, say — the same checkers
/// answer for those) has no code and is simply not referenced. Codes are compared trimmed and
/// case-insensitively.
/// <para>
/// Every organization's connections count — the catalog is shared by all of them — hence
/// IgnoreQueryFilters. Connections have no soft delete; one in any status counts.
/// </para>
/// <para>
/// <b>Stored sync payloads (<c>EntityMap.PayloadJson</c>) are deliberately not searched.</b> A payload is
/// a copy of a record a caller sent: SCM's own customers, vendors, invoices and bills, whose currencies the
/// modules that own those records (Suppliers, Finance) already answer for; or another system's record
/// sent with an API key, whose code is that system's, not a reference into this catalog. And it would not
/// be robust: the code inside is stored exactly as the caller sent it — not trimmed or upper-cased — so a
/// <c>LIKE</c> over the JSON would hang on the database collation for case and miss padded values, while
/// scanning every organization's nvarchar(max) payloads with a leading wildcard.
/// </para>
/// </summary>
internal sealed class IntegrationCurrencyReferenceChecker : ILookupReferenceChecker
{
    private readonly IntegrationDbContext _db;
    private readonly ICurrencyCodeLookup  _currencies;

    public IntegrationCurrencyReferenceChecker(IntegrationDbContext db, ICurrencyCodeLookup currencies)
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

        return _db.Connections.IgnoreQueryFilters()
            .Any(c => c.HomeCurrencyCode != null && c.HomeCurrencyCode.Trim().ToUpper() == upper);
    }
}
