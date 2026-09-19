using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Data.Maps;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;

namespace SMS.Modules.Logistics.Couriers.Tracking;

/// <param name="Invalid">Events dropped because they were unusable — an unknown milestone, or dated in the future.</param>
/// <param name="StatusChangedTo">The consignment's new status, when the events moved it.</param>
internal sealed record TrackingRecordResult(int Recorded, int Duplicates, int Invalid, string? StatusChangedTo);

internal interface ITrackingEventRecorder
{
    /// <summary>
    /// Stores the events that are new for this consignment and moves its status forward if they
    /// warrant it. Saves. The consignment must be tracked by the same context.
    /// </summary>
    Task<TrackingRecordResult> RecordAsync(
        Consignment consignment, IEnumerable<CourierTrackingEvent> events, TrackingEventSource source,
        DateTime now, CancellationToken ct = default);
}

/// <summary>
/// Turns what a carrier reports into a consignment's timeline and status. Shared by webhooks
/// (T-39) and the tracking poll (T-40), so the two can never disagree about what an event means.
/// <para>
/// <b>Carriers report events late, twice, and out of order.</b> Three rules follow:
/// </para>
/// <list type="bullet">
/// <item>An event is stored once, keyed on what it says — not on whether it came by webhook or poll.</item>
/// <item>Only the latest event decides the status. An event older than the one that last set the
/// status is kept on the timeline and changes nothing, so a delayed "in transit" never undoes a
/// "delivered" that arrived first.</item>
/// <item>Status moves forward along the state machine. A carrier skipping steps — booked straight to
/// delivered, because a webhook was missed — walks the shortest legal path, so every status in
/// between is one the consignment could really have been in. Where no path exists (the consignment
/// is cancelled, or already delivered), the event is recorded and the status left alone.</item>
/// </list>
/// </summary>
internal sealed class TrackingEventRecorder : ITrackingEventRecorder
{
    /// <summary>
    /// Clock skew is real; an event days ahead is a carrier bug. Accepting one would pin
    /// <see cref="Consignment.LastStatusEventAt"/> in the future, and every genuine later event
    /// would be ignored as older.
    /// </summary>
    internal static readonly TimeSpan FutureTolerance = TimeSpan.FromHours(1);

    private static readonly ShipmentStateMachine Machine = ShipmentStateMachine.Instance;

    /// <summary>The statuses a carrier's events may move a consignment through.</summary>
    private static readonly HashSet<ShipmentStatus> CarrierDriven =
    [
        ShipmentStatus.Booked, ShipmentStatus.LabelReady, ShipmentStatus.PickupRequested,
        ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery,
        ShipmentStatus.DeliveryAttempted, ShipmentStatus.Exception, ShipmentStatus.Delivered,
        ShipmentStatus.ReturnedToOrigin, ShipmentStatus.Lost
    ];

    private readonly LogisticsDbContext _db;

    public TrackingEventRecorder(LogisticsDbContext db) => _db = db;

    /// <summary>
    /// The status a milestone implies, or null for milestones that describe progress without
    /// changing where the consignment stands.
    /// </summary>
    internal static ShipmentStatus? StatusFor(TrackingMilestone milestone) => milestone switch
    {
        TrackingMilestone.PickedUp          => ShipmentStatus.PickedUp,
        TrackingMilestone.InTransit         => ShipmentStatus.InTransit,
        TrackingMilestone.ArrivedAtHub      => ShipmentStatus.InTransit,
        TrackingMilestone.DepartedHub       => ShipmentStatus.InTransit,
        TrackingMilestone.CustomsHold       => ShipmentStatus.Exception,
        TrackingMilestone.Exception         => ShipmentStatus.Exception,
        TrackingMilestone.OutForDelivery    => ShipmentStatus.OutForDelivery,
        TrackingMilestone.DeliveryAttempted => ShipmentStatus.DeliveryAttempted,
        TrackingMilestone.Delivered         => ShipmentStatus.Delivered,
        TrackingMilestone.Returned          => ShipmentStatus.ReturnedToOrigin,
        // Information received, and a return that has only begun, say nothing about where the
        // parcel physically is that its current status does not already say.
        _                                   => null
    };

    public async Task<TrackingRecordResult> RecordAsync(
        Consignment consignment, IEnumerable<CourierTrackingEvent> events, TrackingEventSource source,
        DateTime now, CancellationToken ct = default)
    {
        var candidates = new List<(ConsignmentTrackingEvent entity, TrackingMilestone milestone)>();
        var invalid = 0;

        foreach (var e in events)
        {
            if (!LogisticsCode.TryParse<TrackingMilestone>(e.Milestone, out var milestone))
            {
                invalid++;
                continue;
            }

            var occurredAt = Normalise(e.OccurredAt);
            if (occurredAt > now + FutureTolerance)
            {
                invalid++;
                continue;
            }

            var entity = new ConsignmentTrackingEvent
            {
                UUID           = Guid.NewGuid(),
                OrganizationId = consignment.OrganizationId,
                ConsignmentId  = consignment.Id,
                Milestone      = LogisticsCode.Of(milestone),
                CarrierStatus  = Clip(e.CarrierStatus, ConsignmentTrackingEventMap.CarrierStatusMax),
                Description    = Clip(e.Description, ConsignmentTrackingEventMap.DescriptionMax),
                Location       = Clip(e.Location, ConsignmentTrackingEventMap.LocationMax),
                SignedBy       = Clip(e.SignedBy, ConsignmentTrackingEventMap.SignedByMax),
                OccurredAt     = occurredAt,
                ReceivedAt     = now,
                Source         = LogisticsCode.Of(source),
                CreatedDate    = now
            };
            entity.EventKey = KeyOf(entity);
            candidates.Add((entity, milestone));
        }

        // Duplicates within this batch, then against what is already stored.
        var unique = candidates.GroupBy(c => c.entity.EventKey).Select(g => g.First()).ToList();
        var keys   = unique.Select(c => c.entity.EventKey).ToList();

        var known = await _db.ConsignmentTrackingEvents
            .IgnoreQueryFilters()
            .Where(t => t.ConsignmentId == consignment.Id && keys.Contains(t.EventKey))
            .Select(t => t.EventKey)
            .ToListAsync(ct);

        var fresh = unique.Where(c => !known.Contains(c.entity.EventKey)).ToList();
        var duplicates = candidates.Count - fresh.Count;

        foreach (var (entity, _) in fresh)
            _db.ConsignmentTrackingEvents.Add(entity);

        var statusChangedTo = ApplyStatus(consignment, fresh, now);

        // Any event is news, whether or not it moves the status — "no news" is what stuck detection measures.
        if (fresh.Count > 0)
        {
            var latest = fresh.Max(f => f.entity.OccurredAt);
            if (consignment.LastTrackingEventAt is null || latest > consignment.LastTrackingEventAt)
                consignment.LastTrackingEventAt = latest;
        }

        foreach (var (entity, milestone) in fresh)
        {
            if (milestone == TrackingMilestone.PickedUp && consignment.ActualDispatchAt is null)
                consignment.ActualDispatchAt = entity.OccurredAt;
        }

        if (fresh.Count > 0 || statusChangedTo is not null)
            await _db.SaveChangesAsync(ct);

        return new TrackingRecordResult(fresh.Count, duplicates, invalid, statusChangedTo);
    }

    private static string? ApplyStatus(
        Consignment consignment, List<(ConsignmentTrackingEvent entity, TrackingMilestone milestone)> fresh, DateTime now)
    {
        // The latest event that implies a status. Ties break toward the milestone further along —
        // carriers stamp several events with one minute.
        var deciding = fresh
            .Select(f => (f.entity, f.milestone, target: StatusFor(f.milestone)))
            .Where(f => f.target is not null)
            .OrderBy(f => f.entity.OccurredAt)
            .ThenBy(f => (int)f.milestone)
            .LastOrDefault();

        if (deciding.entity is null) return null;

        if (consignment.LastStatusEventAt is { } last && deciding.entity.OccurredAt < last)
            return null;

        consignment.LastStatusEventAt = deciding.entity.OccurredAt;

        if (!LogisticsCode.TryParse<ShipmentStatus>(consignment.Status, out var current)) return null;

        var target = deciding.target!.Value;
        if (current == target) return null;

        var path = PathBetween(current, target);
        if (path is null) return null;

        foreach (var step in path)
        {
            Machine.EnsureCanTransition(current, step);
            current = step;
        }

        consignment.Status       = LogisticsCode.Of(target);
        consignment.ModifiedDate = now;
        deciding.entity.AppliedStatus = consignment.Status;

        if (target == ShipmentStatus.Delivered)
            consignment.ActualArrivalAt ??= deciding.entity.OccurredAt;

        return consignment.Status;
    }

    /// <summary>
    /// The shortest sequence of legal transitions from one carrier-driven status to another, or
    /// null when there is none. Excludes the start; includes the target.
    /// </summary>
    internal static IReadOnlyList<ShipmentStatus>? PathBetween(ShipmentStatus from, ShipmentStatus to)
    {
        if (!CarrierDriven.Contains(from) || !CarrierDriven.Contains(to)) return null;

        var previous = new Dictionary<ShipmentStatus, ShipmentStatus> { [from] = from };
        var queue    = new Queue<ShipmentStatus>([from]);

        while (queue.TryDequeue(out var status))
        {
            foreach (var next in Machine.From(status).Where(CarrierDriven.Contains))
            {
                if (previous.ContainsKey(next)) continue;
                previous[next] = status;

                if (next == to)
                {
                    var path = new List<ShipmentStatus> { to };
                    for (var s = status; s != from; s = previous[s]) path.Add(s);
                    path.Reverse();
                    return path;
                }

                queue.Enqueue(next);
            }
        }

        return null;
    }

    private static DateTime Normalise(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local       => value.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _                        => value
        };

        // To the second: the same event resent is often re-serialised with different precision.
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private static string KeyOf(ConsignmentTrackingEvent e)
    {
        // Description is left out on purpose: carriers reword it between a webhook and the tracking
        // API for the very same scan.
        var identity = string.Join('|',
            e.Milestone,
            e.OccurredAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            (e.CarrierStatus ?? string.Empty).ToUpperInvariant(),
            (e.Location ?? string.Empty).ToUpperInvariant());

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
