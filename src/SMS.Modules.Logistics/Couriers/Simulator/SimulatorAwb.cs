using System.Security.Cryptography;
using System.Text;

namespace SMS.Modules.Logistics.Couriers.Simulator;

/// <summary>
/// The simulator's airway bill: <c>SIM-{scenario}{10 hex}</c>.
/// <para>
/// <b>It is a pure function of the idempotency key</b>, which is what lets the whole adapter hold
/// no state at all. Booking the same key twice returns the same airway bill — across restarts,
/// across instances, forever — so the idempotence the contract asks for costs nothing and cannot
/// drift. A simulator that remembered its bookings in a dictionary would lose them on the first
/// app recycle, which is precisely when a demo is being watched.
/// </para>
/// <para>
/// The scenario letter travels inside the number because tracking is only ever given the airway
/// bill. Carrying it there is what lets <c>TrackAsync</c> answer without a lookup.
/// </para>
/// </summary>
internal static class SimulatorAwb
{
    private const string Prefix    = "SIM-";
    private const int    HashChars = 10;

    /// <summary>Total length: prefix + one scenario letter + the hash.</summary>
    private static readonly int Length = Prefix.Length + 1 + HashChars;

    internal static string For(string idempotencyKey, SimulatorScenario scenario) =>
        $"{Prefix}{SimulatorScenarios.CodeOf(scenario)}{Hex(idempotencyKey)}";

    /// <summary>
    /// Reads an airway bill back. Returns false for anything this simulator did not issue, which
    /// is how a stateless adapter still refuses to label or track a number it has never seen.
    /// </summary>
    internal static bool TryParse(string? awb, out SimulatorScenario scenario)
    {
        scenario = default;

        if (string.IsNullOrWhiteSpace(awb)) return false;

        var trimmed = awb.Trim();

        if (trimmed.Length != Length) return false;
        if (!trimmed.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) return false;

        if (!SimulatorScenarios.TryFromCode(char.ToUpperInvariant(trimmed[Prefix.Length]), out scenario))
            return false;

        // The hash has to look like a hash, or "SIM-Axxxxxxxxxx" would track as a real booking.
        return trimmed[(Prefix.Length + 1)..].All(Uri.IsHexDigit);
    }

    /// <summary>
    /// A stable number derived from the key. SHA-256 for spread, not for secrecy — nothing here
    /// is a secret, and the only property that matters is that one key always gives one number.
    /// </summary>
    internal static uint HashOf(string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey ?? string.Empty));
        return BitConverter.ToUInt32(bytes, 0);
    }

    private static string Hex(string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey ?? string.Empty));
        return Convert.ToHexString(bytes)[..HashChars];
    }
}
