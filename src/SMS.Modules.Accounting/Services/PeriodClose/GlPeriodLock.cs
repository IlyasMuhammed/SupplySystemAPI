using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 REV-5 / REV-11 — the organization's period-state lock (SQL Server application lock <c>glperiods:{org}</c>, owned by
/// the transaction). Period close / lock / reopen / final close and the year-end close take it <b>Exclusive</b> around their
/// checks and writes; a writer that creates or posts journals should take it <b>Shared</b> before it resolves the period, so
/// postings run in parallel with each other but never in the middle of a close (drafts counted, P&amp;L planned). The
/// in-memory test provider has no locks: a no-op there.
/// </summary>
internal static class GlPeriodLock
{
    public static string Resource(Guid organizationId) => $"glperiods:{organizationId:N}";

    /// <summary>Takes the lock inside the caller's open transaction (30 s timeout, then SQL error 51000).</summary>
    public static async Task AcquireAsync(AccountingDbContext db, Guid organizationId, bool exclusive, CancellationToken ct = default)
    {
        if (!db.Database.IsRelational()) return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("The period lock must be taken inside a transaction.");
        var resource = Resource(organizationId);
        var mode = exclusive ? "Exclusive" : "Shared";
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {resource}, @LockMode = {mode}, @LockOwner = 'Transaction', @LockTimeout = 30000; IF @r < 0 THROW 51000, 'The period lock could not be taken.', 1;", ct);
    }
}
