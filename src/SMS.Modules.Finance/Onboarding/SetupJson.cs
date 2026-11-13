using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SMS.Modules.Finance.Onboarding;

/// <summary>A50 — small readers over a SetupDefaults payload (camelCase JSON, docs/onboarding/PAYLOAD-SCHEMAS.md) and the D-5 fingerprint.</summary>
internal static class SetupJson
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? (string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim())
            : null;

    public static decimal? Dec(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetDecimal(out var d) => d,
                JsonValueKind.String when decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var s) => s,
                _ => null
            }
            : null;

    public static int? Int(JsonElement e, string name) =>
        Dec(e, name) is { } d && d == decimal.Truncate(d) && d >= int.MinValue && d <= int.MaxValue ? (int)d : null;

    public static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().ToList()
            : [];

    /// <summary>D-5 — a stable hash of the seeded values (invariant formatting, '|'-joined), 32 hex chars.</summary>
    public static string Fingerprint(params object?[] values)
    {
        var text = string.Join("|", values.Select(v => v switch
        {
            null => "",
            decimal d => d.ToString("0.####", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString()
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
    }
}
