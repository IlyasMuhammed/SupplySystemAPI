using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Couriers.Tracking;

public sealed record ConsignmentTrackingEventModel(
    string    Milestone,
    string?   CarrierStatus,
    string?   Description,
    string?   Location,
    string?   SignedBy,
    DateTime  OccurredAt,
    DateTime  ReceivedAt,
    string    Source,
    string?   AppliedStatus);

/// <summary>A consignment that has gone quiet for longer than its status allows.</summary>
public sealed record StuckConsignmentModel(
    Guid      ConsignmentUuid,
    string    ConsignmentNumber,
    string?   CarrierName,
    string    Status,
    string?   MasterAwb,
    DateTime  StuckSince,
    string    StuckReason,
    DateTime? LastTrackingEventAt,
    int       TrackingPollFailures,
    string?   TrackingLastError);

/// <param name="Polled">False when the carrier was asked moments ago and was not asked again.</param>
public sealed record TrackingRefreshModel(
    bool      Polled,
    string    Status,
    int       NewEvents,
    string?   Error,
    DateTime? LastPolledAt,
    DateTime? NextPollAt);

public interface IConsignmentTrackingService
{
    /// <summary>The consignment's timeline, latest first. Null when the consignment does not exist.</summary>
    Task<IReadOnlyList<ConsignmentTrackingEventModel>?> GetTimelineAsync(Guid consignmentUuid);

    /// <summary>This organization's stuck consignments, longest stuck first.</summary>
    Task<IReadOnlyList<StuckConsignmentModel>> GetStuckAsync();

    /// <summary>Asks the carrier now instead of waiting for the schedule. Null when the consignment does not exist.</summary>
    Task<TrackingRefreshModel?> RefreshAsync(Guid consignmentUuid, DateTime? utcNow = null, CancellationToken ct = default);
}

internal sealed class ConsignmentTrackingService : IConsignmentTrackingService
{
    /// <summary>More than this many stuck is a carrier outage, not a list to work through one by one.</summary>
    internal const int MaxStuckListed = 500;

    private readonly LogisticsDbContext        _db;
    private readonly ConsignmentTrackingPoller _poller;

    public ConsignmentTrackingService(LogisticsDbContext db, ConsignmentTrackingPoller poller)
    {
        _db     = db;
        _poller = poller;
    }

    public async Task<IReadOnlyList<ConsignmentTrackingEventModel>?> GetTimelineAsync(Guid consignmentUuid)
    {
        var consignmentId = await _db.Consignments
            .Where(c => c.UUID == consignmentUuid && !c.IsDelete)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync();

        if (consignmentId is null) return null;

        return await _db.ConsignmentTrackingEvents
            .AsNoTracking()
            .Where(e => e.ConsignmentId == consignmentId)
            .OrderByDescending(e => e.OccurredAt)
            .ThenByDescending(e => e.Id)
            .Select(e => new ConsignmentTrackingEventModel(
                e.Milestone, e.CarrierStatus, e.Description, e.Location, e.SignedBy,
                e.OccurredAt, e.ReceivedAt, e.Source, e.AppliedStatus))
            .ToListAsync();
    }

    public async Task<IReadOnlyList<StuckConsignmentModel>> GetStuckAsync() =>
        await _db.Consignments
            .AsNoTracking()
            .Where(c => !c.IsDelete && c.StuckSince != null)
            .OrderBy(c => c.StuckSince)
            .Take(MaxStuckListed)
            .Select(c => new StuckConsignmentModel(
                c.UUID, c.ConsignmentNumber, c.CarrierName, c.Status, c.MasterAwb, c.StuckSince!.Value,
                c.StuckReason ?? string.Empty, c.LastTrackingEventAt, c.TrackingPollFailures, c.TrackingLastError))
            .ToListAsync();

    public async Task<TrackingRefreshModel?> RefreshAsync(Guid consignmentUuid, DateTime? utcNow = null, CancellationToken ct = default)
    {
        var now = utcNow ?? DateTime.UtcNow;

        // Found through the tenant filter: a user can only refresh their own organization's consignment.
        var consignment = await _db.Consignments
            .AsNoTracking()
            .Include(c => c.Carrier)
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment is null) return null;

        if (string.IsNullOrWhiteSpace(consignment.MasterAwb))
            throw new ConflictException($"Consignment {consignment.ConsignmentNumber} is not booked, so there is nothing to track.");

        if (consignment.Carrier?.IntegrationMode != LogisticsCode.Of(CarrierIntegrationMode.Api))
            throw new ConflictException(
                $"{consignment.CarrierName ?? "This carrier"} has no API integration. Its own website is the tracking.");

        if (!LogisticsCode.TryParse<ShipmentStatus>(consignment.Status, out var status) || !TrackingPolicy.Pollable.Contains(status))
            throw new ConflictException(
                $"Consignment {consignment.ConsignmentNumber} is {consignment.Status}; there is nothing more to track.");

        // A button pressed twice, or by two people, asks the carrier once.
        if (consignment.TrackingLastPolledAt is { } last && now - last < TrackingPolicy.RefreshCooldown)
            return new TrackingRefreshModel(false, consignment.Status, 0, consignment.TrackingLastError,
                                            consignment.TrackingLastPolledAt, consignment.TrackingNextPollAt);

        var result = await _poller.PollAsync(consignment.Id, now, ct);
        _db.ChangeTracker.Clear();

        var after = await _db.Consignments.AsNoTracking().SingleAsync(c => c.Id == consignment.Id, ct);

        return new TrackingRefreshModel(
            result.Outcome != PollOutcome.NotPollable, after.Status, result.Recorded, result.Error,
            after.TrackingLastPolledAt, after.TrackingNextPollAt);
    }
}
