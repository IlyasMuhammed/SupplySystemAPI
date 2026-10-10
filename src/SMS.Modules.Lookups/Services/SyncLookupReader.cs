using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Lookups.Services;

/// <summary>
/// A37 §6 — tax-code and unit-of-measure lookup values changed after a point, for Inventory's <c>/api/sync/catalog</c>.
/// Tenant scope is the query filter's: the global catalog plus the caller organization's own rows.
/// </summary>
internal sealed class SyncLookupReader : ISyncLookupReader
{
    private readonly LookupsDbContext _db;

    public SyncLookupReader(LookupsDbContext db) => _db = db;

    public async Task<SyncLookupPage> GetChangedAsync(string kind, DateTime? since, int limit, CancellationToken ct = default)
    {
        var values = _db.LookupValues.AsNoTracking().Where(v => v.Type.Slug == kind);
        var (rows, hasMore) = await SyncPaging.PageAsync(values, since, limit, ct);
        return new SyncLookupPage(
            rows.Select(v => new SyncLookupRow(v.Id, v.DisplayName, v.Notes, v.IsActive, v.SortOrder, v.IsGlobal, v.ModifiedAt)).ToList(),
            hasMore);
    }
}
