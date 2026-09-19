using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Tests.Couriers;

/// <summary>
/// The smallest thing that satisfies <see cref="ICourierProvider"/>.
/// <para>
/// It exists to run <see cref="CourierProviderContractTests"/> against something, so the base
/// suite is verified to be runnable and self-consistent rather than being an untested description
/// of a contract. T-32's simulator is the first adapter anybody would actually use; this is not
/// that, and is deliberately not registered in DI.
/// </para>
/// </summary>
internal sealed class StubCourierProvider : ICourierProvider
{
    internal const string ProviderKey = "STUB";

    /// <summary>A postcode this stub refuses, so the refusal path has a subject.</summary>
    internal const string UnserviceablePostcode = "00000";

    private readonly Dictionary<string, string> _bookedByKey = [];
    private readonly HashSet<string> _awbs = [];

    public string Key         => ProviderKey;
    public string DisplayName => "Stub carrier (tests only)";

    public CourierCapabilities Capabilities { get; init; } = new(
        SupportsBooking:       true,
        SupportsCancellation:  true,
        SupportsLabels:        true,
        SupportsTracking:      true,
        SupportsPickupBooking: false,
        SupportsCod:           false,
        SupportsMultiPiece:    true,
        HonoursIdempotencyKey: true,
        SupportsRating:        true);

    /// <summary>Two services, so "quote everything" and "quote one" are distinguishable.</summary>
    private static readonly (string Code, string Name, decimal PerKg, int Days)[] Services =
    [
        ("STUB-STANDARD", "Stub Standard", 100m, 3),
        ("STUB-EXPRESS",  "Stub Express",  180m, 1)
    ];

    private const decimal FuelRate = 0.1m;

    public Task<CourierRateResult> RateAsync(CourierRateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (request.Packages.Count == 0)
            return Task.FromResult(new CourierRateResult(
                CourierOutcome.Refused, Key, [], "There is nothing to price."));

        if (request.ShipTo.PostalCode == UnserviceablePostcode)
            return Task.FromResult(new CourierRateResult(
                CourierOutcome.Refused, Key, [], $"Postcode {UnserviceablePostcode} is not serviceable."));

        var wanted = request.ServiceCode?.Trim();

        var services = string.IsNullOrWhiteSpace(wanted)
            ? Services
            : [.. Services.Where(s => string.Equals(s.Code, wanted, StringComparison.OrdinalIgnoreCase))];

        if (services.Length == 0)
            return Task.FromResult(new CourierRateResult(
                CourierOutcome.Refused, Key, [], $"'{wanted}' is not a service this carrier sells."));

        var weight = Math.Max(0.5m, request.Packages.Sum(p => p.GrossWeightKg ?? 0m));

        var options = services.Select(s =>
        {
            var basic = Math.Round(weight * s.PerKg, 2);
            var fuel  = Math.Round(basic * FuelRate, 2);

            return new CourierRateOption(
                ServiceCode:        s.Code,
                ServiceName:        s.Name,
                TotalAmount:        basic + fuel,
                Currency:           "PKR",
                BaseAmount:         basic,
                Surcharges:         [new CourierSurcharge("FUEL", "Fuel surcharge", fuel)],
                ChargeableWeightKg: weight,
                EstimatedDelivery:  (request.ShipDate ?? DateTime.UtcNow).Date.AddDays(s.Days),
                TransitDays:        s.Days);
        }).ToList();

        return Task.FromResult(new CourierRateResult(CourierOutcome.Succeeded, Key, options));
    }

    public Task<CourierBookingResult> BookAsync(
        CourierBookingRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (request.Packages.Count == 0)
            return Task.FromResult(new CourierBookingResult(
                CourierOutcome.Refused, Key,
                Message: "A consignment with no packages has nothing to carry."));

        if (request.ShipTo.PostalCode == UnserviceablePostcode)
            return Task.FromResult(new CourierBookingResult(
                CourierOutcome.Refused, Key,
                Message: $"Postcode {UnserviceablePostcode} is not serviceable.",
                CarrierErrorCode: "UNSERVICEABLE"));

        // The idempotency guarantee this stub claims: the same key returns the same booking.
        if (_bookedByKey.TryGetValue(request.IdempotencyKey, out var existing))
            return Task.FromResult(Booked(existing));

        var awb = $"STUB{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        _bookedByKey[request.IdempotencyKey] = awb;
        _awbs.Add(awb);

        return Task.FromResult(Booked(awb));
    }

    private CourierBookingResult Booked(string awb) => new(
        CourierOutcome.Succeeded, Key,
        AwbNumber:   awb,
        TrackingUrl: $"https://stub.example.com/track/{awb}",
        RawResponse: $"{{\"awb\":\"{awb}\"}}");

    public Task<CourierCancelResult> CancelAsync(
        CourierCancelRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!Capabilities.SupportsCancellation)
            return Task.FromResult(new CourierCancelResult(
                CourierOutcome.Unsupported, Key, "This carrier does not cancel bookings."));

        if (!_awbs.Remove(request.AwbNumber))
            return Task.FromResult(new CourierCancelResult(
                CourierOutcome.Refused, Key, $"No booking found for {request.AwbNumber}."));

        return Task.FromResult(new CourierCancelResult(CourierOutcome.Succeeded, Key));
    }

    public Task<CourierLabelResult> GetLabelAsync(
        string awbNumber, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!Capabilities.SupportsLabels)
            return Task.FromResult(new CourierLabelResult(
                CourierOutcome.Unsupported, Key, Message: "This carrier issues no labels."));

        if (!_awbs.Contains(awbNumber))
            return Task.FromResult(new CourierLabelResult(
                CourierOutcome.Refused, Key, Message: $"No booking found for {awbNumber}."));

        // "%PDF-" is enough to be a plausible artefact; the contract only requires bytes and a
        // media type somebody's browser could act on.
        return Task.FromResult(new CourierLabelResult(
            CourierOutcome.Succeeded, Key,
            new CourierLabel(
                System.Text.Encoding.ASCII.GetBytes($"%PDF-1.4 stub label {awbNumber}"),
                "application/pdf",
                $"{awbNumber}.pdf")));
    }

    public Task<CourierTrackingResult> TrackAsync(
        CourierTrackingRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!Capabilities.SupportsTracking)
            return Task.FromResult(new CourierTrackingResult(
                CourierOutcome.Unsupported, Key, [], "This carrier offers no tracking."));

        if (!_awbs.Contains(request.AwbNumber))
            return Task.FromResult(new CourierTrackingResult(
                CourierOutcome.Refused, Key, [], $"No booking found for {request.AwbNumber}."));

        var start = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        return Task.FromResult(new CourierTrackingResult(
            CourierOutcome.Succeeded, Key,
            [
                new CourierTrackingEvent(start,
                    LogisticsCode.Of(TrackingMilestone.PickedUp),   "PU",  "Collected", "Karachi"),
                new CourierTrackingEvent(start.AddHours(6),
                    LogisticsCode.Of(TrackingMilestone.InTransit),  "IT",  "In transit", "Multan")
            ]));
    }
}
