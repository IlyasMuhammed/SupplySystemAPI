using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SMS.Modules.Logistics.Constants;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Couriers.Tracking;

internal enum PollOutcome
{
    /// <summary>The carrier answered; new events, if any, are recorded.</summary>
    Polled,

    /// <summary>The carrier or account cannot track. Asked again much later.</summary>
    TrackingUnavailable,

    /// <summary>The carrier could not be asked, or refused. Counted, and backed off.</summary>
    Failed,

    /// <summary>Not in a status worth asking about, not booked, or not an API carrier.</summary>
    NotPollable
}

internal sealed record PollResult(PollOutcome Outcome, int Recorded, string? StatusChangedTo, string? Error);

internal sealed record TrackingSweepResult(int Polled, int Failed, int NowStuck, int NoLongerStuck);

/// <summary>
/// Asks carriers what happened, for every consignment whose poll is due, then flags the ones that
/// have gone quiet for too long.
/// <para>
/// <b>Polling and webhooks are not alternatives.</b> For a carrier with no webhooks the poll is the
/// only source of tracking. For one with webhooks it is the backstop: webhooks get lost, and a
/// carrier that pushed a thousand events and dropped the "delivered" one leaves a parcel in transit
/// forever. Both write through <see cref="ITrackingEventRecorder"/>, so an event seen both ways is
/// stored once, and a carrier that is pushing is simply polled far less often.
/// </para>
/// <para>
/// <b>Runs across every organization with no tenant</b>, like every recurring job here: each query
/// ignores the tenant filter and scopes itself.
/// </para>
/// </summary>
internal sealed class ConsignmentTrackingPoller
{
    internal const string RecurringJobId = "logistics-consignment-tracking-poll";

    internal const int DefaultBatchSize = 200;

    internal static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromSeconds(30);

    private readonly LogisticsDbContext                 _db;
    private readonly ICarrierAccountResolver            _resolver;
    private readonly ITrackingEventRecorder             _recorder;
    private readonly ILogger<ConsignmentTrackingPoller> _logger;
    private readonly TimeSpan                           _callTimeout;
    private readonly int                                _batchSize;

    public ConsignmentTrackingPoller(
        LogisticsDbContext db, ICarrierAccountResolver resolver, ITrackingEventRecorder recorder,
        IConfiguration configuration, ILogger<ConsignmentTrackingPoller> logger)
    {
        _db       = db;
        _resolver = resolver;
        _recorder = recorder;
        _logger   = logger;

        var seconds  = configuration.GetValue<int?>("Logistics:Tracking:CarrierCallTimeoutSeconds");
        _callTimeout = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : DefaultCallTimeout;

        var batch  = configuration.GetValue<int?>("Logistics:Tracking:PollBatchSize");
        _batchSize = batch is > 0 ? batch.Value : DefaultBatchSize;
    }

    /// <summary>The recurring job: poll what is due, then re-evaluate what is stuck.</summary>
    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync()
    {
        var result = await SweepAsync(DateTime.UtcNow);

        if (result.Polled + result.Failed + result.NowStuck + result.NoLongerStuck > 0)
            _logger.LogInformation(
                "Tracking sweep: {Polled} polled, {Failed} failed, {NowStuck} newly stuck, {NoLongerStuck} no longer stuck.",
                result.Polled, result.Failed, result.NowStuck, result.NoLongerStuck);
    }

    internal async Task<TrackingSweepResult> SweepAsync(DateTime now, CancellationToken ct = default)
    {
        var api = LogisticsCode.Of(CarrierIntegrationMode.Api);

        // Longest overdue first, never-polled before all of them, so a backlog drains fairly.
        var due = await _db.Consignments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => !c.IsDelete
                     && TrackingPolicy.PollableCodes.Contains(c.Status)
                     && c.MasterAwb != null
                     && c.Carrier != null && c.Carrier.IntegrationMode == api
                     && (c.TrackingNextPollAt == null || c.TrackingNextPollAt <= now))
            .OrderBy(c => c.TrackingNextPollAt == null ? 0 : 1)
            .ThenBy(c => c.TrackingNextPollAt)
            .Select(c => c.Id)
            .Take(_batchSize)
            .ToListAsync(ct);

        int polled = 0, failed = 0;

        foreach (var id in due)
        {
            // One consignment's trouble — a carrier outage, a bad account — must not stop the rest.
            try
            {
                var result = await PollAsync(id, now, ct);
                if (result.Outcome == PollOutcome.Failed) failed++; else polled++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failed++;
                _logger.LogError("Tracking poll for consignment {ConsignmentId} failed unexpectedly: {Reason}", id, ex.Message);
            }
            finally
            {
                _db.ChangeTracker.Clear();
            }
        }

        var (nowStuck, noLongerStuck) = await EvaluateStuckAsync(now, ct);

        return new TrackingSweepResult(polled, failed, nowStuck, noLongerStuck);
    }

    /// <summary>
    /// Polls one consignment now, whatever its schedule. Scoped by id, so callers acting for a user
    /// must have found the consignment through the tenant filter first.
    /// </summary>
    internal async Task<PollResult> PollAsync(int consignmentId, DateTime now, CancellationToken ct = default)
    {
        var consignment = await _db.Consignments
            .IgnoreQueryFilters()
            .Include(c => c.Carrier)
            .Include(c => c.CarrierAccount)
            .FirstOrDefaultAsync(c => c.Id == consignmentId && !c.IsDelete, ct);

        if (consignment?.Carrier is not { } carrier
            || string.IsNullOrWhiteSpace(consignment.MasterAwb)
            || !LogisticsCode.TryParse<ShipmentStatus>(consignment.Status, out var status)
            || !TrackingPolicy.Pollable.Contains(status)
            || carrier.IntegrationMode != LogisticsCode.Of(CarrierIntegrationMode.Api))
            return new PollResult(PollOutcome.NotPollable, 0, null, null);

        ResolvedCarrierAccount account;
        try
        {
            account = await _resolver.ResolveAsync(carrier.UUID, consignment.CarrierAccount?.UUID, ct);
        }
        catch (Exception ex) when (ex is ConflictException or NotFoundException or BadRequestException)
        {
            return await FailAsync(consignment, status, $"Carrier configuration: {ex.Message}", now, ct);
        }

        if (!account.Capabilities.SupportsTracking)
        {
            consignment.TrackingLastPolledAt = now;
            consignment.TrackingLastError    = $"{account.Provider.DisplayName} does not track on account '{account.AccountName}'.";
            consignment.TrackingNextPollAt   = now + TrackingPolicy.TrackingUnavailableRecheck;
            await _db.SaveChangesAsync(ct);
            return new PollResult(PollOutcome.TrackingUnavailable, 0, null, consignment.TrackingLastError);
        }

        CourierTrackingResult result;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_callTimeout);

            try
            {
                result = await account.Provider.TrackAsync(
                    new CourierTrackingRequest(consignment.MasterAwb.Trim(), consignment.ConsignmentNumber,
                                               account.Credentials, await BookedAtAsync(consignment, ct)),
                    timeout.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                return await FailAsync(consignment, status, $"The carrier did not answer ({ex.GetType().Name}).", now, ct);
            }
        }

        switch (result.Outcome)
        {
            case CourierOutcome.Succeeded:
                var recorded = await _recorder.RecordAsync(consignment, result.Events, TrackingEventSource.Poll, now, ct);

                var newStatus = LogisticsCode.Parse<ShipmentStatus>(consignment.Status);
                consignment.TrackingLastPolledAt = now;
                consignment.TrackingPollFailures = 0;
                consignment.TrackingLastError    = null;
                consignment.TrackingNextPollAt   = TrackingPolicy.NextPollAt(newStatus, 0, await ReceivesWebhooksAsync(consignment, now, ct), now);
                await _db.SaveChangesAsync(ct);

                return new PollResult(PollOutcome.Polled, recorded.Recorded, recorded.StatusChangedTo, null);

            case CourierOutcome.Unsupported:
                consignment.TrackingLastPolledAt = now;
                consignment.TrackingLastError    = result.Message ?? "The carrier does not track this consignment.";
                consignment.TrackingNextPollAt   = now + TrackingPolicy.TrackingUnavailableRecheck;
                await _db.SaveChangesAsync(ct);
                return new PollResult(PollOutcome.TrackingUnavailable, 0, null, consignment.TrackingLastError);

            default:
                return await FailAsync(consignment, status,
                    result.Message ?? (result.Outcome == CourierOutcome.Refused ? "The carrier refused." : "The carrier could not answer."),
                    now, ct);
        }
    }

    private async Task<PollResult> FailAsync(
        Consignment consignment, ShipmentStatus status, string error, DateTime now, CancellationToken ct)
    {
        consignment.TrackingPollFailures += 1;
        consignment.TrackingLastPolledAt  = now;
        consignment.TrackingLastError     = error.Length <= 500 ? error : error[..500];
        consignment.TrackingNextPollAt    = TrackingPolicy.NextPollAt(
            status, consignment.TrackingPollFailures, await ReceivesWebhooksAsync(consignment, now, ct), now);

        await _db.SaveChangesAsync(ct);
        return new PollResult(PollOutcome.Failed, 0, null, consignment.TrackingLastError);
    }

    /// <summary>
    /// Re-evaluates every live consignment on an API carrier: flags the newly quiet, clears the
    /// ones that have moved. Stuck is a state, not an event — it goes away by itself.
    /// </summary>
    internal async Task<(int nowStuck, int noLongerStuck)> EvaluateStuckAsync(DateTime now, CancellationToken ct = default)
    {
        var api = LogisticsCode.Of(CarrierIntegrationMode.Api);

        // Manual carriers are left out: with no tracking feed, every one of them would read as
        // never collected, and a list where everything is stuck is a list nobody reads.
        var candidates = await _db.Consignments
            .IgnoreQueryFilters()
            .Where(c => !c.IsDelete
                     && c.Carrier != null && c.Carrier.IntegrationMode == api
                     && (TrackingPolicy.PollableCodes.Contains(c.Status) || c.StuckSince != null))
            .ToListAsync(ct);

        int nowStuck = 0, noLongerStuck = 0;

        foreach (var c in candidates)
        {
            var reason = LogisticsCode.TryParse<ShipmentStatus>(c.Status, out var status)
                ? TrackingPolicy.StuckReason(
                    status, c.LastTrackingEventAt ?? c.ModifiedDate ?? c.CreatedDate,
                    c.TrackingPollFailures, c.TrackingLastError, now)
                : null;

            if (reason is null)
            {
                if (c.StuckSince is null) continue;
                c.StuckSince  = null;
                c.StuckReason = null;
                noLongerStuck++;
                continue;
            }

            if (c.StuckSince is null)
            {
                c.StuckSince = now;
                nowStuck++;
            }

            c.StuckReason = reason.Length <= 500 ? reason : reason[..500];
        }

        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        return (nowStuck, noLongerStuck);
    }

    /// <summary>
    /// When the booking went through — what an adapter with no memory dates its scans from. The
    /// ledger's completed booking command is the truth; the consignment's creation is the fallback.
    /// </summary>
    private async Task<DateTime> BookedAtAsync(Consignment consignment, CancellationToken ct)
    {
        var book      = LogisticsCode.Of(CarrierCommandType.Book);
        var succeeded = LogisticsStatuses.CarrierCommand.Succeeded;

        var completed = await _db.CarrierCommands
            .IgnoreQueryFilters()
            .Where(c => c.ConsignmentId == consignment.Id && c.CommandType == book && c.Status == succeeded)
            .OrderByDescending(c => c.CompletedAt)
            .Select(c => c.CompletedAt)
            .FirstOrDefaultAsync(ct);

        return completed ?? consignment.CreatedDate;
    }

    private Task<bool> ReceivesWebhooksAsync(Consignment consignment, DateTime now, CancellationToken ct)
    {
        var webhook = LogisticsCode.Of(TrackingEventSource.Webhook);
        var since   = now - TrackingPolicy.WebhooksCountAsLiveFor;

        return _db.ConsignmentTrackingEvents
            .IgnoreQueryFilters()
            .AnyAsync(e => e.ConsignmentId == consignment.Id && e.Source == webhook && e.ReceivedAt >= since, ct);
    }
}
