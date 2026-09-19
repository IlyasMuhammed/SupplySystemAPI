using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Demand.Services;

// A29-P4-07 §5.3 — "Failed emails retried 3x by Hangfire." The unit that's actually retried: reads
// one already-committed SaleOrderIntimation row by id and attempts the send. Deliberately separate
// from ISaleOrderEmailService's own methods (which render the template and write that row) — if the
// whole render-plus-send were one retried unit, a retry after the row was already written but the
// send failed would re-render and insert a SECOND row for the same event, since the method has no
// memory of "I already logged this" across Hangfire retries. This way the row is written exactly
// once, and only the send — the part that can genuinely fail transiently — retries.
public interface ISaleOrderIntimationDispatchJob
{
    Task DispatchAsync(Guid intimationUuid);
}

internal sealed class SaleOrderIntimationDispatchJob : ISaleOrderIntimationDispatchJob
{
    private readonly DemandDbContext _db;
    private readonly INotificationService _notifications;
    private readonly ILogger<SaleOrderIntimationDispatchJob> _log;

    public SaleOrderIntimationDispatchJob(
        DemandDbContext db, INotificationService notifications, ILogger<SaleOrderIntimationDispatchJob> log)
    {
        _db            = db;
        _notifications = notifications;
        _log           = log;
    }

    [AutomaticRetry(Attempts = 3)]
    public async Task DispatchAsync(Guid intimationUuid)
    {
        var intimation = await _db.SaleOrderIntimations.FirstOrDefaultAsync(x => x.UUID == intimationUuid);
        if (intimation is null)
        {
            // The row this job was enqueued for is gone (or, in a test, was never written against
            // this DbContext instance) — nothing to retry towards, so this is not a transient
            // failure Hangfire should keep re-attempting.
            _log.LogWarning("SaleOrderIntimation {Uuid} not found — dispatch skipped.", intimationUuid);
            return;
        }

        try
        {
            await _notifications.SendEmailAsync(intimation.Recipients, intimation.Subject, intimation.BodyHtml);

            intimation.Status       = EnumCode<SaleOrderIntimationStatus>.Of(SaleOrderIntimationStatus.Sent);
            intimation.SentAt       = DateTime.UtcNow;
            intimation.ErrorMessage = null;
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            intimation.Status       = EnumCode<SaleOrderIntimationStatus>.Of(SaleOrderIntimationStatus.Failed);
            intimation.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            await _db.SaveChangesAsync();
            throw; // let Hangfire retry
        }
    }
}
