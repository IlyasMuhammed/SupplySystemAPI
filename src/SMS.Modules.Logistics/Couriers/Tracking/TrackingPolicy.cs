using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Couriers.Tracking;

/// <summary>
/// How often to ask a carrier, and how long silence is allowed to last. Pure functions of a
/// consignment's state, so the numbers are testable and live in one place.
/// </summary>
internal static class TrackingPolicy
{
    /// <summary>Statuses in which the parcel is in the carrier's hands and worth asking about.</summary>
    internal static readonly IReadOnlySet<ShipmentStatus> Pollable = new HashSet<ShipmentStatus>
    {
        ShipmentStatus.Booked, ShipmentStatus.LabelReady, ShipmentStatus.PickupRequested,
        ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery,
        ShipmentStatus.DeliveryAttempted, ShipmentStatus.Exception
    };

    /// <summary>Codes of <see cref="Pollable"/>, for queries.</summary>
    internal static readonly string[] PollableCodes = Pollable.Select(LogisticsCode.Of).ToArray();

    /// <summary>A carrier that pushes events is only polled as a backstop, this often at most.</summary>
    internal static readonly TimeSpan WebhookBackstop = TimeSpan.FromHours(6);

    /// <summary>Webhooks this recent mean the carrier is pushing.</summary>
    internal static readonly TimeSpan WebhooksCountAsLiveFor = TimeSpan.FromHours(24);

    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(24);

    /// <summary>When a carrier cannot track at all — ask again tomorrow in case that changes.</summary>
    internal static readonly TimeSpan TrackingUnavailableRecheck = TimeSpan.FromHours(24);

    /// <summary>A manual "refresh now" within this long of the last poll does not call the carrier again.</summary>
    internal static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(1);

    internal const int FailuresBeforeStuck = 5;

    /// <summary>
    /// How often to poll in a status. Closer to delivery means things change faster and people are
    /// waiting on the answer.
    /// </summary>
    internal static TimeSpan? IntervalFor(ShipmentStatus status) => status switch
    {
        ShipmentStatus.OutForDelivery                                                  => TimeSpan.FromMinutes(30),
        ShipmentStatus.DeliveryAttempted or ShipmentStatus.Exception                   => TimeSpan.FromHours(1),
        ShipmentStatus.PickedUp or ShipmentStatus.InTransit                            => TimeSpan.FromHours(2),
        ShipmentStatus.Booked or ShipmentStatus.LabelReady or ShipmentStatus.PickupRequested => TimeSpan.FromHours(4),
        _                                                                              => null
    };

    /// <summary>
    /// When to poll next, or null when the status is not polled at all. Failures back off
    /// exponentially — a carrier API that is down should not be hammered — capped at a day.
    /// </summary>
    internal static DateTime? NextPollAt(ShipmentStatus status, int failures, bool receivesWebhooks, DateTime now)
    {
        if (IntervalFor(status) is not { } interval) return null;

        if (receivesWebhooks && interval < WebhookBackstop) interval = WebhookBackstop;

        if (failures > 0)
        {
            var backedOff = interval.Ticks * Math.Pow(2, Math.Min(failures, 10));
            interval = backedOff >= MaxBackoff.Ticks ? MaxBackoff : TimeSpan.FromTicks((long)backedOff);
        }

        return now + interval;
    }

    /// <summary>
    /// Why the consignment is stuck, or null when it is not.
    /// </summary>
    /// <param name="lastNews">The latest carrier event, or failing that when the consignment last changed.</param>
    internal static string? StuckReason(
        ShipmentStatus status, DateTime lastNews, int pollFailures, string? lastError, DateTime now)
    {
        if (!Pollable.Contains(status)) return null;

        if (pollFailures >= FailuresBeforeStuck)
            return $"Tracking has failed {pollFailures} times in a row: {lastError ?? "no reason given"}";

        var quiet = now - lastNews;

        return status switch
        {
            ShipmentStatus.Booked or ShipmentStatus.LabelReady or ShipmentStatus.PickupRequested when quiet >= TimeSpan.FromDays(3) =>
                $"Not collected by the carrier {Days(quiet)} after booking.",
            ShipmentStatus.PickedUp or ShipmentStatus.InTransit when quiet >= TimeSpan.FromDays(5) =>
                $"No carrier scan for {Days(quiet)} while in transit.",
            ShipmentStatus.OutForDelivery when quiet >= TimeSpan.FromDays(2) =>
                $"Out for delivery for {Days(quiet)} with no outcome.",
            ShipmentStatus.DeliveryAttempted when quiet >= TimeSpan.FromDays(3) =>
                $"Delivery attempted {Days(quiet)} ago and not reattempted.",
            ShipmentStatus.Exception when quiet >= TimeSpan.FromDays(2) =>
                $"In exception for {Days(quiet)}.",
            _ => null
        };
    }

    private static string Days(TimeSpan span) =>
        (int)span.TotalDays == 1 ? "1 day" : $"{(int)span.TotalDays} days";
}
