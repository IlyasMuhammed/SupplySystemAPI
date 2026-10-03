using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A32-PC-09 / BR-C2-10 — daily sweep: a SENT quotation whose valid_to has passed (today &gt; valid_to, UTC dates)
/// becomes EXPIRED. Nothing else is touched, so a rerun finds nothing left to do.
/// <para>
/// A recurring job has no user and so no tenant: the SENT-and-past rows are found with the query filter off, then
/// each organization is handled inside its own <see cref="HangfireTenantScope"/> block with an explicit
/// OrganizationId predicate — the <see cref="ReservationExpirySweepJob"/> pattern.
/// </para>
/// <para>
/// Saved one quotation at a time: Status is a concurrency token, so a quotation a user accepted or rejected in the
/// same moment makes only its own save fail (skipped, the user's transition stands) rather than the whole run.
/// </para>
/// <para>Scheduled at 01:07 UTC rather than on the hour, away from the top-of-hour cluster of other jobs.</para>
/// </summary>
internal sealed class QuotationExpiryJob
{
    internal const string RecurringJobId = "sale-quotation-expiry";
    internal const string Cron = "7 1 * * *";

    /// <summary>Raised against the system, not a person.</summary>
    internal const int SystemUserId = 0;

    private static readonly string Sent    = EnumCode<SaleQuotationStatus>.Of(SaleQuotationStatus.Sent);
    private static readonly string Expired = EnumCode<SaleQuotationStatus>.Of(SaleQuotationStatus.Expired);

    private readonly DemandDbContext _db;
    private readonly ILogger<QuotationExpiryJob> _log;

    public QuotationExpiryJob(DemandDbContext db, ILogger<QuotationExpiryJob> log)
    {
        _db  = db;
        _log = log;
    }

    [AutomaticRetry(Attempts = 3)]
    public Task RunAsync() => RunAsync(DateTime.UtcNow.Date);

    /// <summary>Returns how many quotations it expired.</summary>
    internal async Task<int> RunAsync(DateTime today)
    {
        today = today.Date;

        var organizations = await _db.SaleQuotations.IgnoreQueryFilters().AsNoTracking()
            .Where(q => q.Status == Sent && q.ValidTo < today)
            .Select(q => q.OrganizationId)
            .Distinct()
            .ToListAsync();

        var expired = 0;
        foreach (var org in organizations)
        {
            HangfireTenantScope.OrganizationId = org;
            try
            {
                var due = await _db.SaleQuotations.IgnoreQueryFilters()
                    .Where(q => q.OrganizationId == org && q.Status == Sent && q.ValidTo < today)
                    .ToListAsync();

                foreach (var quotation in due)
                {
                    quotation.Status       = Expired;
                    quotation.ModifiedBy   = SystemUserId;
                    quotation.ModifiedDate = DateTime.UtcNow;
                    try
                    {
                        await _db.SaveChangesAsync();
                        expired++;
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        // A user moved it on (accepted/rejected) in the same moment: theirs stands.
                        _db.Entry(quotation).State = EntityState.Detached;
                    }
                }
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }

        if (expired > 0)
            _log.LogInformation("Sale quotation expiry: {Expired} quotation(s) past their valid-to date marked EXPIRED.", expired);
        return expired;
    }
}
