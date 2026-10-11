using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A38 D-7 — document numbers per store and store-local day: <c>SH-{store}-{yyyyMMdd}-{seq:00}</c> (shifts),
/// <c>POS-…-{seq:0000}</c> (sales), <c>RET-…-{seq:0000}</c> (returns). POS-B uses it for orders.
/// </summary>
internal interface IPosNumberSequence
{
    /// <summary>The next value (1, 2, …) of (store, prefix, businessDate). Concurrency-safe on SQL Server; a failed caller leaves a gap.</summary>
    Task<int> NextValueAsync(PosStore store, string prefix, DateTime businessDate, CancellationToken ct = default);

    /// <summary>The next formatted number for <paramref name="utcNow"/>'s store-local date.</summary>
    Task<string> NextNumberAsync(PosStore store, string prefix, DateTime utcNow, CancellationToken ct = default);
}

internal sealed class PosNumberSequenceService : IPosNumberSequence
{
    private readonly PosDbContext _db;
    public PosNumberSequenceService(PosDbContext db) => _db = db;

    public static string Format(string prefix, string storeCode, DateTime businessDate, int value) =>
        prefix == PosNumberPrefix.Shift
            ? $"{prefix}-{storeCode}-{businessDate:yyyyMMdd}-{value:00}"
            : $"{prefix}-{storeCode}-{businessDate:yyyyMMdd}-{value:0000}";

    public async Task<string> NextNumberAsync(PosStore store, string prefix, DateTime utcNow, CancellationToken ct = default)
    {
        var date = PosClock.BusinessDate(store, utcNow);
        return Format(prefix, store.Code, date, await NextValueAsync(store, prefix, date, ct));
    }

    public async Task<int> NextValueAsync(PosStore store, string prefix, DateTime businessDate, CancellationToken ct = default)
    {
        var date = businessDate.Date;
        if (_db.Database.IsRelational())
        {
            // One atomic statement: HOLDLOCK serialises the first insert of a day, the update takes the row lock.
            var values = await _db.Database.SqlQuery<int>($"""
                MERGE [pos].[number_sequences] WITH (HOLDLOCK) AS t
                USING (SELECT {store.OrganizationId} AS OrganizationId, {store.Id} AS StoreId, {prefix} AS Prefix, CAST({date} AS date) AS BusinessDate) AS s
                   ON t.OrganizationId = s.OrganizationId AND t.StoreId = s.StoreId AND t.Prefix = s.Prefix AND t.BusinessDate = s.BusinessDate
                WHEN MATCHED THEN UPDATE SET LastValue = t.LastValue + 1, ModifiedAt = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (OrganizationId, StoreId, Prefix, BusinessDate, LastValue, ModifiedAt)
                     VALUES (s.OrganizationId, s.StoreId, s.Prefix, s.BusinessDate, 1, SYSUTCDATETIME())
                OUTPUT inserted.LastValue AS [Value];
                """).ToListAsync(ct);
            return values.Single();
        }

        // Non-relational (unit tests): plain read-modify-write.
        var row = await _db.NumberSequences.IgnoreQueryFilters().FirstOrDefaultAsync(
            s => s.OrganizationId == store.OrganizationId && s.StoreId == store.Id && s.Prefix == prefix && s.BusinessDate == date, ct);
        if (row is null)
        {
            row = new PosNumberSequence { OrganizationId = store.OrganizationId, StoreId = store.Id, Prefix = prefix, BusinessDate = date };
            _db.NumberSequences.Add(row);
        }
        row.LastValue++;
        await _db.SaveChangesAsync(ct);
        return row.LastValue;
    }
}
