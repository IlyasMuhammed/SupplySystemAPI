using Microsoft.EntityFrameworkCore;

namespace SMS.Shared.Common;

// A37 D-16 — offline/delta sync (docs/module-registry/API-CONTRACT.md §6). Owner: OPSA. No SQL triggers: EF Core 8 writes
// with an OUTPUT clause, which SQL Server refuses on tables with triggers — so the application keeps ModifiedAt.

/// <summary>A37 D-16 — a row with an app-maintained UTC <see cref="ModifiedAt"/>, set on every insert and update.</summary>
public interface IHasModifiedAt
{
    DateTime ModifiedAt { get; set; }
}

public static class ModifiedAtStamping
{
    /// <summary>
    /// Call from SaveChanges / SaveChangesAsync before base: stamps every added or modified <see cref="IHasModifiedAt"/>
    /// entry with one UTC instant. Set-based writes (ExecuteUpdate, raw SQL) bypass this and must set it themselves.
    /// </summary>
    public static void StampModifiedAt(this DbContext db)
    {
        var now = DateTime.UtcNow;
        foreach (var entry in db.ChangeTracker.Entries<IHasModifiedAt>())
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.ModifiedAt = now;
    }
}

public static class SyncPaging
{
    public const int DefaultLimit = 500;
    public const int MaxLimit     = 1000;

    public static int ClampLimit(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    /// <summary>A bound <c>since</c> as UTC: a local value (ISO with an offset) is converted, an unspecified one is UTC.</summary>
    public static DateTime? AsUtc(DateTime? since) => since switch
    {
        null => null,
        { Kind: DateTimeKind.Local } s => s.ToUniversalTime(),
        { } s => DateTime.SpecifyKind(s, DateTimeKind.Utc)
    };

    /// <summary>
    /// A37 §6 — rows with ModifiedAt strictly after <paramref name="since"/>, oldest first, about <paramref name="limit"/>
    /// of them. A page never splits one ModifiedAt value: the rows sharing the last row's timestamp all come along (a
    /// client resuming with "since = last modifiedAt" would otherwise skip the rest of them). <c>HasMore</c> = rows newer
    /// than the page's last timestamp remain.
    /// </summary>
    public static async Task<(List<T> Rows, bool HasMore)> PageAsync<T>(
        IQueryable<T> query, DateTime? since, int limit, CancellationToken ct = default) where T : class, IHasModifiedAt
    {
        if (since is { } s) query = query.Where(x => x.ModifiedAt > s);

        var rows = await query.OrderBy(x => x.ModifiedAt).Take(limit).ToListAsync(ct);
        if (rows.Count < limit) return (rows, false);

        var last = rows[^1].ModifiedAt;
        var page = rows.Where(r => r.ModifiedAt < last).ToList();
        page.AddRange(await query.Where(x => x.ModifiedAt == last).ToListAsync(ct));
        return (page, await query.AnyAsync(x => x.ModifiedAt > last, ct));
    }
}

/// <summary>One lookup row as the sync catalog sends it (tax codes, units of measure).</summary>
public sealed record SyncLookupRow(Guid Id, string Name, string? Notes, bool IsActive, int SortOrder, bool IsGlobal, DateTime ModifiedAt);

/// <summary>A page of rows changed strictly after <c>since</c>, oldest first; <see cref="HasMore"/> = more remain.</summary>
public sealed record SyncLookupPage(IReadOnlyList<SyncLookupRow> Rows, bool HasMore);

/// <summary>
/// A37 §6 — Lookups' master rows for <c>GET /api/sync/catalog</c>, read in the caller's tenant scope (global rows plus the
/// organization's own). Lookups implements; Inventory's sync endpoint returns empty lists when the host lacks it.
/// </summary>
public interface ISyncLookupReader
{
    public const string TaxCodes = "tax-code";
    public const string Uoms     = "uom";

    /// <param name="kind"><see cref="TaxCodes"/> or <see cref="Uoms"/> (the lookup type slug).</param>
    /// <param name="since">Exclusive lower bound on ModifiedAt; null = everything.</param>
    Task<SyncLookupPage> GetChangedAsync(string kind, DateTime? since, int limit, CancellationToken ct = default);
}
