using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Couriers.Simulator;

/// <summary>
/// A carrier that does everything except move anything.
/// <para>
/// It exists so the rest of Phase 2 — the booking flow, the retry ledger, labels, the tracking
/// poll and every screen over them — can be built, tested and demonstrated without a single real
/// credential. Sandbox accounts take weeks to arrive; waiting for one before writing the machinery
/// that uses it would leave the machinery untested until the least convenient moment.
/// </para>
/// <para>
/// <b>Entirely stateless.</b> Every answer is derived from the airway bill, and the airway bill is
/// derived from the idempotency key — see <see cref="SimulatorAwb"/>. Nothing is stored, so
/// nothing is lost on a restart and nothing differs between instances.
/// </para>
/// <para>
/// <b>It never pretends to be real.</b> The display name, the label and the tracking URL all say
/// so, because the one genuinely dangerous failure here is somebody shipping a real parcel against
/// a carrier that was quietly a simulation.
/// </para>
/// </summary>
internal sealed class SimulatorCourierProvider : ICourierProvider, ICourierWebhookReceiver
{
    internal const string ProviderKey = "SIMULATOR";

    /// <summary>Verifies and reads simulator webhooks. See <see cref="SimulatorWebhook"/> for the format.</summary>
    public CourierWebhookResult Receive(CourierWebhookRequest request, IReadOnlyDictionary<string, string> credentials) =>
        SimulatorWebhook.Receive(request, credentials);

    public string Key         => ProviderKey;
    public string DisplayName => "Simulator (test carrier — nothing is actually shipped)";

    public CourierCapabilities Capabilities { get; } = new(
        SupportsBooking:       true,
        SupportsCancellation:  true,
        SupportsLabels:        true,
        SupportsTracking:      true,
        // No pickup method on the contract yet; claiming it would promise something unreachable.
        SupportsPickupBooking: false,
        SupportsCod:           true,
        SupportsMultiPiece:    true,
        // The whole point of deriving the airway bill from the key.
        HonoursIdempotencyKey: true,
        SupportsRating:        true);

    // ── Rating ────────────────────────────────────────────────────────────────

    /// <remarks>
    /// Quotes its three services, or the one named. A read: nothing is stored and nothing is
    /// committed, so asking repeatedly is free — which is the contract, not a simulator liberty.
    /// <para>
    /// The two failure scenario codes work here as they do on a booking, so a demo can show a rate
    /// call being refused and a rate call coming apart without either being a real outage.
    /// </para>
    /// </remarks>
    public Task<CourierRateResult> RateAsync(CourierRateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (request.Packages.Count == 0)
            return Task.FromResult(new CourierRateResult(
                CourierOutcome.Refused, Key, [],
                "There is nothing to price: a consignment with no packages has no weight and no volume."));

        if (request.CodAmount is > 0 && string.IsNullOrWhiteSpace(request.CodCurrency))
            return Task.FromResult(new CourierRateResult(
                CourierOutcome.Refused, Key, [], "A cash-on-delivery amount needs a currency."));

        if (SimulatorScenarios.TryParse(request.ServiceCode, out var scenario))
        {
            if (scenario == SimulatorScenario.Refuse)
                return Task.FromResult(new CourierRateResult(
                    CourierOutcome.Refused, Key, [],
                    $"Simulated refusal: {request.ShipTo.PostalCode ?? "this destination"} is not serviceable."));

            if (scenario == SimulatorScenario.Fail)
                return Task.FromResult(new CourierRateResult(
                    CourierOutcome.Failed, Key, [],
                    "Simulated carrier failure. No rate could be obtained."));

            // The journey scenarios say what happens after booking and have nothing to do with
            // price, so they quote the full list like any unspecified request.
            return Task.FromResult(Quote(request, SimulatorTariff.Services));
        }

        if (string.IsNullOrWhiteSpace(request.ServiceCode))
            return Task.FromResult(Quote(request, SimulatorTariff.Services));

        if (!SimulatorTariff.TryResolve(request.ServiceCode, out var service))
            return Task.FromResult(new CourierRateResult(
                CourierOutcome.Refused, Key, [],
                $"'{request.ServiceCode}' is not a service this carrier sells. Available: "
              + $"{string.Join(", ", SimulatorTariff.Services.Select(s => s.Code))}."));

        return Task.FromResult(Quote(request, [service]));
    }

    private CourierRateResult Quote(CourierRateRequest request, IReadOnlyList<SimulatorService> services)
    {
        var shipDate = request.ShipDate ?? DateTime.UtcNow;

        var options = services
            .Select(s => SimulatorTariff.Quote(s, request.Packages, request.CodAmount, shipDate))
            .ToList();

        return new CourierRateResult(
            CourierOutcome.Succeeded, Key, options,
            Message:     "Simulated rates. No carrier was contacted and nothing is committed.",
            RawResponse: $"{{\"simulated\":true,\"options\":{options.Count}}}");
    }

    // ── Booking ───────────────────────────────────────────────────────────────

    public Task<CourierBookingResult> BookAsync(
        CourierBookingRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (request.Packages.Count == 0)
            return Task.FromResult(Refused(
                "A consignment with no packages has nothing to carry.", "NO_PACKAGES"));

        if (request.CodAmount is > 0 && string.IsNullOrWhiteSpace(request.CodCurrency))
            return Task.FromResult(Refused(
                "A cash-on-delivery amount needs a currency.", "COD_CURRENCY_MISSING"));

        var scenario = ScenarioFor(request);

        if (scenario == SimulatorScenario.Refuse)
            return Task.FromResult(Refused(
                $"Simulated refusal: {request.ShipTo.PostalCode ?? "this destination"} is not serviceable.",
                "SIM_UNSERVICEABLE"));

        // Not an exception: the contract's Failed outcome is exactly "the call did not resolve and
        // whether a parcel exists is unknown". Throwing would make it look like nothing happened.
        if (scenario == SimulatorScenario.Fail)
            return Task.FromResult(new CourierBookingResult(
                CourierOutcome.Failed, Key,
                Message: "Simulated carrier failure. Whether a booking was created is unknown.",
                CarrierErrorCode: "SIM_TIMEOUT"));

        var awb = SimulatorAwb.For(request.IdempotencyKey, scenario);

        return Task.FromResult(new CourierBookingResult(
            CourierOutcome.Succeeded, Key,
            AwbNumber:        awb,
            CarrierReference: $"SIMREF-{request.ConsignmentNumber}",
            TrackingUrl:      $"https://simulator.invalid/track/{awb}",
            Cost:             EstimatedCost(request),
            // The tariff's currency, not the COD currency. Collecting cash in one currency says
            // nothing about what the carriage is billed in, and taking the COD currency here would
            // label a rupee figure as dollars whenever the two differed.
            CostCurrency:     SimulatorTariff.Currency,
            Message:          "Simulated booking. Nothing has actually been shipped.",
            RawResponse:      $"{{\"simulated\":true,\"awb\":\"{awb}\",\"scenario\":\"{scenario}\"}}"));
    }

    /// <summary>
    /// The scenario the service code asked for, or one derived from the key so that a demo with
    /// no special setup still shows parcels at a mix of stages rather than seven identical ones.
    /// </summary>
    private static SimulatorScenario ScenarioFor(CourierBookingRequest request) =>
        SimulatorScenarios.TryParse(request.ServiceCode, out var requested)
            ? requested
            : SimulatorScenarios.FromHash(SimulatorAwb.HashOf(request.IdempotencyKey));

    /// <summary>
    /// What the booking cost, taken from the same tariff that quotes it (T-45). One pricing model,
    /// so a consignment quoted and then booked does not come back with two different numbers for
    /// reasons nobody can explain.
    /// </summary>
    private static decimal EstimatedCost(CourierBookingRequest request)
    {
        if (!SimulatorTariff.TryResolve(request.ServiceCode, out var service))
            service = SimulatorTariff.Default;

        return SimulatorTariff
            .Quote(service, request.Packages, request.CodAmount, DateTime.UtcNow)
            .TotalAmount;
    }

    private CourierBookingResult Refused(string message, string code) =>
        new(CourierOutcome.Refused, Key, Message: message, CarrierErrorCode: code);

    // ── Cancellation ──────────────────────────────────────────────────────────

    /// <remarks>
    /// Cancelling twice succeeds twice. That falls out of holding no state, and it is the right
    /// behaviour anyway: cancellation is idempotent, and a second attempt finding "already
    /// cancelled" is not an error anybody needs to handle.
    /// </remarks>
    public Task<CourierCancelResult> CancelAsync(
        CourierCancelRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (!SimulatorAwb.TryParse(request.AwbNumber, out _))
            return Task.FromResult(new CourierCancelResult(
                CourierOutcome.Refused, Key,
                $"'{request.AwbNumber}' was not issued by the simulator."));

        return Task.FromResult(new CourierCancelResult(
            CourierOutcome.Succeeded, Key, "Simulated cancellation."));
    }

    // ── Labels ────────────────────────────────────────────────────────────────

    /// <remarks>
    /// A real PDF rather than a few bytes pretending to be one: the label flow ends with somebody
    /// opening it, and a placeholder that will not open makes that path impossible to demonstrate.
    /// QuestPDF is already a dependency here from T-28.
    /// </remarks>
    public Task<CourierLabelResult> GetLabelAsync(
        string awbNumber, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (!SimulatorAwb.TryParse(awbNumber, out var scenario))
            return Task.FromResult(new CourierLabelResult(
                CourierOutcome.Refused, Key,
                Message: $"'{awbNumber}' was not issued by the simulator."));

        var awb = awbNumber.Trim().ToUpperInvariant();

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            container.Page(page =>
            {
                // 4×6 inches, which is what a label printer expects.
                page.Size(288, 432, Unit.Point);
                page.Margin(12);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Content().Column(column =>
                {
                    column.Item().Background(Colors.Red.Darken2).Padding(6)
                          .Text("SIMULATED LABEL — NOT A REAL SHIPMENT")
                          .FontColor(Colors.White).Bold().FontSize(9).AlignCenter();

                    column.Item().PaddingTop(14).Text("SIMULATOR COURIER").Bold().FontSize(14);
                    column.Item().PaddingTop(2).Text($"Scenario: {scenario}").FontSize(8)
                          .FontColor(Colors.Grey.Darken1);

                    column.Item().PaddingTop(18).Text("AIRWAY BILL").FontSize(8)
                          .FontColor(Colors.Grey.Darken1);
                    column.Item().Text(awb).Bold().FontSize(15);

                    // Not a real barcode symbology — a simulated label that scanned would be a
                    // worse lie than one that plainly does not.
                    column.Item().PaddingTop(14).Height(52)
                          .Background(Colors.Grey.Lighten3)
                          .AlignMiddle().AlignCenter()
                          .Text("[ barcode omitted — simulated ]").FontSize(8)
                          .FontColor(Colors.Grey.Darken1);

                    column.Item().PaddingTop(18).LineHorizontal(1).LineColor(Colors.Black);
                    column.Item().PaddingTop(8).Text("This label is generated by the courier simulator. "
                        + "It carries no carrier contract and no parcel will be collected against it.")
                        .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken2);
                });
            });
        });

        return Task.FromResult(new CourierLabelResult(
            CourierOutcome.Succeeded, Key,
            new CourierLabel(document.GeneratePdf(), "application/pdf", $"{awb}.pdf")));
    }

    // ── Tracking ──────────────────────────────────────────────────────────────

    /// <summary>How far apart the simulated scans are.</summary>
    internal static readonly TimeSpan ScanInterval = TimeSpan.FromHours(6);

    /// <remarks>
    /// The sequence is fixed by the airway bill.
    /// <para>
    /// <b>With a booking time</b> the journey unfolds from it in real time — the first scan an hour
    /// after booking, then one every six hours — and only scans that have already happened are
    /// returned. Every timestamp is the same on every call, which is what lets the tracking poll
    /// (T-40) recognise a scan it has already stored.
    /// </para>
    /// <para>
    /// <b>Without one</b> the whole journey is returned, running backwards from now, so a parcel booked
    /// a moment ago does not read as having travelled last year. Those timestamps move between calls,
    /// so this form is for looking, not for polling.
    /// </para>
    /// </remarks>
    public Task<CourierTrackingResult> TrackAsync(
        CourierTrackingRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);

        if (!SimulatorAwb.TryParse(request.AwbNumber, out var scenario))
            return Task.FromResult(new CourierTrackingResult(
                CourierOutcome.Refused, Key, [],
                $"'{request.AwbNumber}' was not issued by the simulator."));

        var journey = SimulatorScenarios.JourneyOf(scenario);
        var now     = DateTime.UtcNow;

        DateTime WhenOf(int index) => request.BookedAt is { } bookedAt
            // To the second, so the same booking time always produces identical timestamps.
            ? Truncate(bookedAt).AddHours(1) + ScanInterval * index
            : now.AddHours(-1) - ScanInterval * (journey.Count - 1 - index);

        var events = journey
            .Select((step, index) => new CourierTrackingEvent(
                OccurredAt:  WhenOf(index),
                Milestone:   LogisticsCode.Of(step.Milestone),
                CarrierStatus: $"SIM_{LogisticsCode.Of(step.Milestone)}",
                Description: step.Description,
                Location:    step.Location,
                SignedBy:    step.Milestone == TrackingMilestone.Delivered ? "SIMULATED" : null))
            .Where(e => request.BookedAt is null || e.OccurredAt <= now)
            .ToList();

        return Task.FromResult(new CourierTrackingResult(
            CourierOutcome.Succeeded, Key, events));
    }

    private static DateTime Truncate(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }
}
