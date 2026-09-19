namespace SMS.Modules.Logistics.Couriers;

/// <summary>One inbound webhook, exactly as it arrived.</summary>
/// <param name="Headers">Case-insensitive. The adapter reads only what its carrier signs with.</param>
/// <param name="Body">The raw bytes. Signatures are over bytes; a body re-serialised from a model never verifies.</param>
/// <param name="ReceivedAt">For rejecting replayed deliveries whose signed timestamp is too old.</param>
public sealed record CourierWebhookRequest(
    IReadOnlyDictionary<string, string> Headers,
    byte[]                              Body,
    DateTime                            ReceivedAt);

/// <summary>A tracking event a webhook reports, and the airway bill it belongs to.</summary>
public sealed record CourierWebhookEvent(string AwbNumber, CourierTrackingEvent Event);

public enum CourierWebhookVerdict
{
    /// <summary>Signed by the carrier and understood.</summary>
    Accepted,

    /// <summary>Not signed by the carrier — or no secret is configured to check it with. Nothing is kept.</summary>
    InvalidSignature,

    /// <summary>Correctly signed, but too old: a replay. Nothing is kept.</summary>
    Stale,

    /// <summary>Correctly signed, but not something this adapter can read.</summary>
    Malformed
}

/// <param name="DeliveryId">The carrier's own id for this delivery, when it sends one — what makes a resend recognisable.</param>
/// <param name="Reason">Why it was not accepted. Never includes the secret or the expected signature.</param>
public sealed record CourierWebhookResult(
    CourierWebhookVerdict             Verdict,
    IReadOnlyList<CourierWebhookEvent> Events,
    string?                           DeliveryId = null,
    string?                           Reason     = null)
{
    public static CourierWebhookResult Rejected(CourierWebhookVerdict verdict, string reason) => new(verdict, [], null, reason);
}

/// <summary>
/// Implemented by an <see cref="ICourierProvider"/> whose carrier pushes tracking events.
/// <para>
/// Optional, and declared by implementing it rather than by a capability flag: an adapter either
/// knows how to verify and read its carrier's webhooks or it does not, and a flag that could be
/// set without the code behind it is exactly what the contract tests exist to rule out.
/// </para>
/// <para>
/// <b>Verification comes first and is not optional.</b> The endpoint these arrive at is anonymous by
/// necessity — a carrier cannot log in — so the signature is the only thing between the internet and
/// every consignment's status. An adapter must reject a delivery it cannot verify, including when no
/// secret is configured; "no secret, so accept" is how a webhook endpoint becomes a way to mark
/// anyone's parcel delivered.
/// </para>
/// </summary>
public interface ICourierWebhookReceiver
{
    /// <summary>Matches <see cref="ICourierProvider.Key"/>.</summary>
    string Key { get; }

    /// <param name="credentials">The carrier account's decrypted credentials, which hold the webhook secret.</param>
    CourierWebhookResult Receive(CourierWebhookRequest request, IReadOnlyDictionary<string, string> credentials);
}
