using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Couriers.Simulator;

/// <summary>
/// The simulator's webhook format, modelled on how real carriers sign deliveries — so the receiving
/// side is built and proven against the same shape of problem before a real carrier arrives.
/// <para>
/// <b>Signing:</b> <c>X-Sim-Timestamp</c> holds Unix seconds; <c>X-Sim-Signature</c> holds
/// <c>v1=</c> and the lowercase hex HMAC-SHA256, keyed with the account's <c>WebhookSecret</c>
/// credential, over <c>{timestamp}.{raw body}</c>. The timestamp is inside the signature, so a
/// captured delivery cannot be replayed with a fresh one.
/// </para>
/// <para>
/// <b>Body:</b> <c>{ "deliveryId": "...", "events": [ { "awb", "milestone", "occurredAt",
/// "carrierStatus", "description", "location", "signedBy" } ] }</c>.
/// </para>
/// </summary>
internal static class SimulatorWebhook
{
    internal const string TimestampHeader   = "X-Sim-Timestamp";
    internal const string SignatureHeader   = "X-Sim-Signature";
    internal const string SecretCredential  = "WebhookSecret";
    internal const string SignaturePrefix   = "v1=";

    /// <summary>How old a signed timestamp may be — and how far ahead, for clock skew.</summary>
    internal static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>The signature header value for a body. Used by tests and by anyone demonstrating the flow.</summary>
    internal static string Sign(string secret, long timestamp, byte[] body) =>
        SignaturePrefix + Convert.ToHexString(Hmac(secret, timestamp, body)).ToLowerInvariant();

    internal static CourierWebhookResult Receive(
        CourierWebhookRequest request, IReadOnlyDictionary<string, string> credentials)
    {
        // No secret means nothing can be verified — and so nothing is accepted.
        if (!credentials.TryGetValue(SecretCredential, out var secret) || string.IsNullOrWhiteSpace(secret))
            return CourierWebhookResult.Rejected(CourierWebhookVerdict.InvalidSignature,
                $"No {SecretCredential} credential is configured for this account, so the delivery cannot be verified.");

        if (!request.Headers.TryGetValue(TimestampHeader, out var timestampText)
            || !long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp))
            return CourierWebhookResult.Rejected(CourierWebhookVerdict.InvalidSignature, $"Missing or unreadable {TimestampHeader}.");

        if (!request.Headers.TryGetValue(SignatureHeader, out var signatureText)
            || !signatureText.StartsWith(SignaturePrefix, StringComparison.Ordinal)
            || !TryHex(signatureText[SignaturePrefix.Length..], out var presented))
            return CourierWebhookResult.Rejected(CourierWebhookVerdict.InvalidSignature, $"Missing or unreadable {SignatureHeader}.");

        // Constant time: a comparison that stops at the first differing byte tells an attacker,
        // byte by byte, how much of a forged signature is right.
        if (!CryptographicOperations.FixedTimeEquals(presented, Hmac(secret, timestamp, request.Body)))
            return CourierWebhookResult.Rejected(CourierWebhookVerdict.InvalidSignature, "The signature does not match.");

        // Checked only once the timestamp is known to be genuine.
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
        if ((request.ReceivedAt - signedAt).Duration() > Tolerance)
            return CourierWebhookResult.Rejected(CourierWebhookVerdict.Stale,
                $"Signed at {signedAt:u}, more than {Tolerance.TotalMinutes:0} minutes from now. Treated as a replay.");

        return Parse(request.Body);
    }

    private static CourierWebhookResult Parse(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var deliveryId = root.TryGetProperty("deliveryId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;

            if (!root.TryGetProperty("events", out var list) || list.ValueKind != JsonValueKind.Array)
                return CourierWebhookResult.Rejected(CourierWebhookVerdict.Malformed, "The body has no 'events' array.");

            var events = new List<CourierWebhookEvent>();
            var index = 0;

            foreach (var item in list.EnumerateArray())
            {
                var awb       = Text(item, "awb");
                var milestone = Text(item, "milestone");
                var occurred  = Text(item, "occurredAt");

                if (string.IsNullOrWhiteSpace(awb))
                    return CourierWebhookResult.Rejected(CourierWebhookVerdict.Malformed, $"Event {index} has no 'awb'.");

                if (!LogisticsCode.TryParse<TrackingMilestone>(milestone, out _))
                    return CourierWebhookResult.Rejected(CourierWebhookVerdict.Malformed,
                        $"Event {index} has milestone '{milestone}', which is not one this system knows.");

                if (!DateTimeOffset.TryParse(occurred, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var occurredAt))
                    return CourierWebhookResult.Rejected(CourierWebhookVerdict.Malformed, $"Event {index} has no readable 'occurredAt'.");

                events.Add(new CourierWebhookEvent(awb.Trim(), new CourierTrackingEvent(
                    occurredAt.UtcDateTime, milestone!,
                    Text(item, "carrierStatus"), Text(item, "description"), Text(item, "location"), Text(item, "signedBy"))));
                index++;
            }

            return new CourierWebhookResult(CourierWebhookVerdict.Accepted, events, deliveryId);
        }
        catch (JsonException)
        {
            return CourierWebhookResult.Rejected(CourierWebhookVerdict.Malformed, "The body is not valid JSON.");
        }
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static byte[] Hmac(string secret, long timestamp, byte[] body)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var signed = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signed, 0);
        body.CopyTo(signed, prefix.Length);

        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
    }

    private static bool TryHex(string hex, out byte[] bytes)
    {
        try
        {
            bytes = hex.Length == 64 ? Convert.FromHexString(hex) : [];
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
