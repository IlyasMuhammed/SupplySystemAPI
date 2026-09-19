using SMS.Modules.Logistics.Couriers;

namespace SMS.Modules.Logistics.Tests.Couriers;

/// <summary>
/// A carrier whose every answer the test decides — success, refusal, an unresolved call, an
/// exception, or a call that hangs — and which records exactly what it was sent.
/// </summary>
internal sealed class ScriptedCourierProvider : ICourierProvider
{
    private readonly Queue<Func<CourierBookingRequest, CancellationToken, Task<CourierBookingResult>>> _script = new();
    private readonly Queue<Func<string, CancellationToken, Task<CourierLabelResult>>> _labelScript = new();

    private readonly Queue<Func<CourierTrackingRequest, CancellationToken, Task<CourierTrackingResult>>> _trackScript = new();

    private readonly Queue<Func<CourierRateRequest, CancellationToken, Task<CourierRateResult>>> _rateScript = new();

    public ScriptedCourierProvider(
        string key, bool deduplicates = false, bool cod = true, bool multiPiece = true, bool labels = true,
        bool tracking = false, bool rating = false)
    {
        Key          = key;
        Capabilities = new CourierCapabilities(
            SupportsBooking: true, SupportsLabels: labels, SupportsTracking: tracking, SupportsCod: cod,
            SupportsMultiPiece: multiPiece, HonoursIdempotencyKey: deduplicates, SupportsRating: rating);
    }

    /// <summary>Every rate request received, in order.</summary>
    public List<CourierRateRequest> RateCalls { get; } = [];

    /// <summary>Scripts the next rate answer. Unscripted requests quote one flat option.</summary>
    public ScriptedCourierProvider ThenRates(Func<CourierRateRequest, CancellationToken, Task<CourierRateResult>> step)
    {
        _rateScript.Enqueue(step);
        return this;
    }

    public ScriptedCourierProvider ThenRates(params CourierRateOption[] options) =>
        ThenRates((_, _) => Task.FromResult(new CourierRateResult(CourierOutcome.Succeeded, Key, options)));

    public Task<CourierRateResult> RateAsync(CourierRateRequest request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RateCalls.Add(request);

        if (_rateScript.Count > 0) return _rateScript.Dequeue()(request, ct);

        if (!Capabilities.SupportsRating)
            return Task.FromResult(new CourierRateResult(
                CourierOutcome.Unsupported, Key, [], "This scripted carrier does not rate."));

        return Task.FromResult(new CourierRateResult(
            CourierOutcome.Succeeded, Key,
            [new CourierRateOption(request.ServiceCode ?? "SCRIPTED", "Scripted service", 1000m, "PKR")]));
    }

    /// <summary>Every tracking request received, in order.</summary>
    public List<CourierTrackingRequest> TrackCalls { get; } = [];

    /// <summary>Scripts the next tracking answer. Unscripted requests succeed with no events.</summary>
    public ScriptedCourierProvider ThenTracks(Func<CourierTrackingRequest, CancellationToken, Task<CourierTrackingResult>> step)
    {
        _trackScript.Enqueue(step);
        return this;
    }

    public ScriptedCourierProvider ThenTracks(params CourierTrackingEvent[] events) =>
        ThenTracks((_, _) => Task.FromResult(new CourierTrackingResult(CourierOutcome.Succeeded, Key, events)));

    /// <summary>A minimal but genuine PDF header — enough to pass signature checks.</summary>
    internal static byte[] Pdf(string marker = "label") =>
        System.Text.Encoding.ASCII.GetBytes($"%PDF-1.7\n% {marker}\n%%EOF");

    public string Key { get; }
    public string DisplayName => $"Scripted carrier {Key}";
    public CourierCapabilities Capabilities { get; }

    /// <summary>Every booking request received, in order.</summary>
    public List<CourierBookingRequest> Calls { get; } = [];

    /// <summary>Every airway bill a label was requested for, in order.</summary>
    public List<string> LabelCalls { get; } = [];

    public ScriptedCourierProvider Then(Func<CourierBookingRequest, CancellationToken, Task<CourierBookingResult>> step)
    {
        _script.Enqueue(step);
        return this;
    }

    public ScriptedCourierProvider ThenBooks(string awb, CourierLabel? label = null) =>
        Then((_, _) => Task.FromResult(Booked(awb) with { Label = label }));

    public ScriptedCourierProvider ThenRefuses(string message) =>
        Then((_, _) => Task.FromResult(new CourierBookingResult(CourierOutcome.Refused, Key, Message: message, CarrierErrorCode: "REFUSED")));

    public ScriptedCourierProvider ThenTimesOut() =>
        Then((_, _) => Task.FromResult(new CourierBookingResult(CourierOutcome.Failed, Key, Message: "Gateway timeout")));

    public ScriptedCourierProvider ThenThrows(Exception ex) =>
        Then((_, _) => Task.FromException<CourierBookingResult>(ex));

    /// <summary>Scripts the next label answer. Unscripted requests return a valid PDF.</summary>
    public ScriptedCourierProvider ThenLabel(Func<string, CancellationToken, Task<CourierLabelResult>> step)
    {
        _labelScript.Enqueue(step);
        return this;
    }

    public ScriptedCourierProvider ThenLabel(CourierOutcome outcome, CourierLabel? label = null, string? message = null) =>
        ThenLabel((_, _) => Task.FromResult(new CourierLabelResult(outcome, Key, label, message)));

    public CourierBookingResult Booked(string awb) =>
        new(CourierOutcome.Succeeded, Key, AwbNumber: awb, CarrierReference: "REF-" + awb,
            TrackingUrl: $"https://scripted.invalid/track/{awb}");

    public Task<CourierBookingResult> BookAsync(CourierBookingRequest request, CancellationToken ct = default)
    {
        Calls.Add(request);
        return _script.Count > 0
            ? _script.Dequeue()(request, ct)
            : Task.FromResult(Booked($"AWB-{Calls.Count}"));
    }

    public Task<CourierCancelResult> CancelAsync(CourierCancelRequest request, CancellationToken ct = default) =>
        Task.FromResult(new CourierCancelResult(CourierOutcome.Unsupported, Key));

    public Task<CourierLabelResult> GetLabelAsync(
        string awbNumber, IReadOnlyDictionary<string, string> credentials, CancellationToken ct = default)
    {
        LabelCalls.Add(awbNumber);
        return _labelScript.Count > 0
            ? _labelScript.Dequeue()(awbNumber, ct)
            : Task.FromResult(new CourierLabelResult(
                CourierOutcome.Succeeded, Key, new CourierLabel(Pdf(awbNumber), "application/pdf", "carrier-name.pdf")));
    }

    public Task<CourierTrackingResult> TrackAsync(CourierTrackingRequest request, CancellationToken ct = default)
    {
        TrackCalls.Add(request);
        return _trackScript.Count > 0
            ? _trackScript.Dequeue()(request, ct)
            : Task.FromResult(new CourierTrackingResult(
                Capabilities.SupportsTracking ? CourierOutcome.Succeeded : CourierOutcome.Unsupported, Key, []));
    }
}
