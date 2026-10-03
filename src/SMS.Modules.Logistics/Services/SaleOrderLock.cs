using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// A33 REV-02 — runs work on the Logistics connection under the sale order's own application lock
/// (<see cref="SaleOrderLocks.HoldsResource"/>, the one Demand's confirm / cancel / reserve take), in a transaction, so
/// the delivery creator can never insert deliveries for an order a concurrent cancel is about to commit. App locks are
/// database-wide, so Demand's connection and this one exclude each other.
/// <para>
/// The <c>SaleOrderHolds.OneChangeAtATimeAsync</c> pattern: inside the context's execution strategy (the Logistics
/// context retries on transient failures, which forbids a bare user transaction), the change tracker cleared on a
/// retry. The in-memory provider has no locks and runs the work as it is.
/// </para>
/// <para>
/// <b>Never</b> call this from the canceller: Demand calls the canceller while holding this very lock on its own
/// connection, and taking it again from here would wait out the timeout and fail.
/// </para>
/// </summary>
internal static class SaleOrderLock
{
    public static async Task<T> RunAsync<T>(LogisticsDbContext db, Guid saleOrderUuid, Func<Task<T>> work)
    {
        if (!db.Database.IsRelational())
            return await work();

        if (db.Database.CurrentTransaction is not null)
        {
            await LockAsync(db, saleOrderUuid);
            return await work();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var attempt  = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync();
            await LockAsync(db, saleOrderUuid);
            var result = await work();
            await transaction.CommitAsync();
            return result;
        });
    }

    private static async Task LockAsync(LogisticsDbContext db, Guid saleOrderUuid)
    {
        var outcome = new SqlParameter("@outcome", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await db.Database.ExecuteSqlRawAsync(
            "DECLARE @result int; "
          + "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout; "
          + "SET @outcome = @result;",
            [
                new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = SaleOrderLocks.HoldsResource(saleOrderUuid) },
                new SqlParameter("@timeout", SqlDbType.Int) { Value = SaleOrderLocks.TimeoutMilliseconds },
                outcome
            ]);

        if (outcome.Value is not int granted || granted < 0)
            throw new ConflictException(
                "Someone else is changing this sale order right now, so its deliveries were not created. Try again in a moment.");
    }
}
