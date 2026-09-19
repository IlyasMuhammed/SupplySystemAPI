namespace SMS.Modules.Logistics.Couriers.Manual;

/// <summary>
/// The carrier that has no API — which is most of them.
/// <para>
/// A person rings the carrier, or uses its portal, or fills in a paper consignment note, and comes
/// back with an airway bill. This adapter records that. It obtains nothing, because there is
/// nothing to obtain from.
/// </para>
/// <para>
/// <b>Why it is an adapter at all.</b> Manual booking is the path that has to work first: a module
/// that only functions for carriers with APIs is a module most of the business cannot use. Giving
/// it its own code path beside the integrated one is how it rots — the branch nobody exercises
/// drifts, and the manual case is the one that matters most. Behind this contract it goes through
/// the same booking flow, the same idempotency ledger and the same screens as a carrier with a
/// full API, and the only difference is where the number came from.
/// </para>
/// <para>
/// Stateless, like the simulator, and for a simpler reason: it has nothing to remember. The airway
/// bill is an input.
/// </para>
/// </summary>
internal sealed class ManualCourierProvider : ICourierProvider
{
    internal const string ProviderKey = "MANUAL";

    public string Key         => ProviderKey;
    public string DisplayName => "Manual (airway bill keyed in by hand)";

    public CourierCapabilities Capabilities { get; } = new(
        // It books, in the only sense available to it: it records what a human obtained.
        SupportsBooking:       true,

        // There is nobody to tell. A manual booking is cancelled by ringing the carrier, and
        // claiming otherwise would let the UI offer a button that quietly does nothing.
        SupportsCancellation:  false,

        // The carrier prints its own. We never hold the artefact.
        SupportsLabels:        false,

        // The carrier's own website does the tracking — that is what TrackingUrlTemplate on the
        // carrier row is for. Claiming tracking would make the poll (T-40) ask us questions we
        // cannot answer, every few minutes, forever.
        SupportsTracking:      false,

        SupportsPickupBooking: false,

        // A commercial term between the shipper and the carrier, not an API feature. Cash on
        // delivery works fine with a carrier booked by telephone.
        SupportsCod:           true,

        SupportsMultiPiece:    true,

        // Nothing deduplicates here. The ledger (T-36) is the only thing standing between a
        // double submission and two consignments recorded against one real parcel.
        HonoursIdempotencyKey: false,

        // There is nobody to ask. What this carrier charges lives in a rate card (T-46) — a
        // negotiated tariff somebody typed in — which is exactly the arrangement decision G9
        // keeps a card for. Claiming to rate would put a "get a quote" button on the screen that
        // could only ever come back empty.
        SupportsRating:        false);

    /// <remarks>
    /// <see cref="CourierOutcome.Unsupported"/>, and it says where the price does come from. A
    /// carrier booked by telephone still has a price — it is simply one that was agreed in advance
    /// rather than quoted on demand.
    /// </remarks>
    public Task<CourierRateResult> RateAsync(CourierRateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new CourierRateResult(
            CourierOutcome.Unsupported, Key, [],
            "This carrier has no rate API — it is booked by hand. Price it from its rate card, "
          + "which is the agreed tariff for this carrier and service."));
    }

    public Task<CourierBookingResult> BookAsync(
        CourierBookingRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (request.Packages.Count == 0)
            return Task.FromResult(Refused(
                "A consignment with no packages has nothing to hand to a carrier.", "NO_PACKAGES"));

        // The whole point of a manual booking is that the number came from outside the system.
        // Without it the consignment would be BOOKED with nothing to track and nothing to prove
        // it — which is the state T-21 already refused to create, kept refused here.
        if (string.IsNullOrWhiteSpace(request.SuppliedAwbNumber))
            return Task.FromResult(Refused(
                "A manual booking needs the airway bill number the carrier issued — it is the only "
              + "way to track or prove the consignment.",
                "AWB_REQUIRED"));

        var awb = request.SuppliedAwbNumber.Trim();

        return Task.FromResult(new CourierBookingResult(
            CourierOutcome.Succeeded, Key,
            AwbNumber:   awb,
            // Deliberately no TrackingUrl: the carrier row's TrackingUrlTemplate builds it, and
            // an adapter guessing at a carrier's URL shape would be inventing one.
            Message:     "Recorded a manual booking. Nothing was sent to the carrier.",
            RawResponse: null));
    }

    private CourierBookingResult Refused(string message, string code) =>
        new(CourierOutcome.Refused, Key, Message: message, CarrierErrorCode: code);

    /// <remarks>
    /// <see cref="CourierOutcome.Unsupported"/> rather than a quiet success. A caller told the
    /// cancellation succeeded would believe a real parcel had been stopped, when in fact nobody
    /// has told the carrier anything.
    /// </remarks>
    public Task<CourierCancelResult> CancelAsync(
        CourierCancelRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new CourierCancelResult(
            CourierOutcome.Unsupported, Key,
            "This carrier is booked by hand, so it has to be cancelled by hand — contact the "
          + "carrier, then cancel the consignment here."));
    }

    public Task<CourierLabelResult> GetLabelAsync(
        string awbNumber, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new CourierLabelResult(
            CourierOutcome.Unsupported, Key,
            Message: "This carrier issues its own labels. Use the one it gave you."));
    }

    public Task<CourierTrackingResult> TrackAsync(
        CourierTrackingRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(new CourierTrackingResult(
            CourierOutcome.Unsupported, Key, [],
            "This carrier has no tracking feed. Its own website is the tracking — see the "
          + "carrier's tracking URL template."));
    }
}
