using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Logistics.Constants;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Couriers.Booking;

/// <summary>
/// Every few minutes: expires claims whose workers went quiet, and re-runs any booking that
/// stalled — so a crashed worker, a failed save or a lost enqueue never leaves a consignment in
/// BOOKING waiting for somebody to notice.
/// </summary>
internal sealed class ConsignmentBookingSweepJob
{
    internal const string RecurringJobId = "logistics-consignment-booking-sweep";

    private readonly LogisticsDbContext                  _db;
    private readonly ICarrierCommandLedger               _ledger;
    private readonly ICourierProviderRegistry            _registry;
    private readonly IConsignmentBookingScheduler        _scheduler;
    private readonly ILogger<ConsignmentBookingSweepJob> _logger;

    public ConsignmentBookingSweepJob(
        LogisticsDbContext db, ICarrierCommandLedger ledger, ICourierProviderRegistry registry,
        IConsignmentBookingScheduler scheduler, ILogger<ConsignmentBookingSweepJob> logger)
    {
        _db        = db;
        _ledger    = ledger;
        _registry  = registry;
        _scheduler = scheduler;
        _logger    = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task RunAsync()
    {
        var (expired, requeued) = await SweepAsync();

        if (expired > 0 || requeued > 0)
            _logger.LogInformation(
                "Booking sweep: {Expired} carrier claim(s) expired, {Requeued} booking(s) re-run.", expired, requeued);
    }

    /// <returns>How many claims expired, and how many bookings were handed back to the job.</returns>
    internal async Task<(int expired, int requeued)> SweepAsync(DateTime? utcNow = null, CancellationToken ct = default)
    {
        var now = utcNow ?? DateTime.UtcNow;

        var expired = await _ledger.ExpireStaleLeasesAsync(now, ct);

        var cutoff = now - BookingPolicy.StuckAfter;

        // Every organization: this runs with no tenant.
        var stalled = await _db.Consignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => !c.IsDelete
                     && c.Status == LogisticsStatuses.Shipment.Booking
                     && (c.ModifiedDate ?? c.CreatedDate) <= cutoff)
            .Select(c => new { c.UUID, c.OrganizationId, c.BookingIdempotencyKey })
            .ToListAsync(ct);

        var requeued = 0;

        foreach (var consignment in stalled)
        {
            var command = consignment.BookingIdempotencyKey is null
                ? null
                : await _ledger.FindAsync(CarrierCommandType.Book, consignment.BookingIdempotencyKey, consignment.OrganizationId, ct);

            if (!IsDue(command, now)) continue;

            _scheduler.Enqueue(consignment.UUID, consignment.OrganizationId);
            requeued++;
        }

        return (expired, requeued);
    }

    private bool IsDue(CarrierCommandSnapshot? command, DateTime now) => command switch
    {
        // Never sent — the job was lost or died before claiming. The ledger makes running it safe.
        null => true,

        // Answered, but the consignment never caught up — the job died between the two saves.
        { Status: CarrierCommandStatus.Succeeded or CarrierCommandStatus.Refused or CarrierCommandStatus.NotPerformed } => true,

        // Unknown: only where a retry cannot book twice, within the limit, and once its backoff has
        // passed. Everything else is waiting for a person, and re-running it would change nothing.
        { Status: CarrierCommandStatus.Unknown } c =>
            BookingPolicy.Deduplicates(_registry, c.ProviderKey)
            && c.AttemptCount < BookingPolicy.MaxAutomaticAttempts
            && c.LastAttemptAt + BookingPolicy.RetryDelayAfter(c.AttemptCount) <= now,

        // In flight with a live lease: somebody is on it.
        _ => false
    };
}
