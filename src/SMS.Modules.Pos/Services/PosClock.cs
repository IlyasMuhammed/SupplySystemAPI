using SMS.Modules.Pos.Domain;

namespace SMS.Modules.Pos.Services;

/// <summary>A38 D-3 — store-local time. Unknown time zone ids read as UTC (they are validated on store save).</summary>
internal static class PosClock
{
    /// <summary>A terminal counts as online when it was seen within this window (API-CONTRACT §2 Terminal.isOnline).</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(15);

    public static bool TryFindZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    public static TimeZoneInfo Zone(PosStore store) => TryFindZone(store.Timezone, out var zone) ? zone : TimeZoneInfo.Utc;

    public static DateTime ToLocal(PosStore store, DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone(store));

    /// <summary>The store-local calendar date of <paramref name="utc"/> (Kind Unspecified, midnight).</summary>
    public static DateTime BusinessDate(PosStore store, DateTime utc) => ToLocal(store, utc).Date;

    public static bool IsOnline(DateTime? lastSeenAt, DateTime utcNow) => lastSeenAt is { } seen && utcNow - seen < OnlineWindow;
}
