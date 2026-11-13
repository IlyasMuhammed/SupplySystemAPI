using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SMS.Modules.Onboarding.Services.Defaults;

/// <summary>A50 D-4 — canonical JSON (sorted keys, no whitespace, normalized numbers): "did the payload change" for the
/// version bump ignores formatting and key order.</summary>
internal static class JsonCanonical
{
    public static string Write(JsonElement element)
    {
        var sb = new StringBuilder();
        Append(element, sb);
        return sb.ToString();
    }

    public static string Write(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Write(doc.RootElement);
    }

    private static void Append(JsonElement e, StringBuilder sb)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                sb.Append('{');
                var first = true;
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonSerializer.Serialize(p.Name)).Append(':');
                    Append(p.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var i = 0;
                foreach (var item in e.EnumerateArray())
                {
                    if (i++ > 0) sb.Append(',');
                    Append(item, sb);
                }
                sb.Append(']');
                break;
            case JsonValueKind.Number:
                sb.Append(e.TryGetDecimal(out var d) ? d.ToString("G29", CultureInfo.InvariantCulture) : e.GetRawText());
                break;
            case JsonValueKind.String:
                sb.Append(JsonSerializer.Serialize(e.GetString()));
                break;
            default:
                sb.Append(e.GetRawText());
                break;
        }
    }

    /// <summary>Records in a payload: the items of its top-level arrays, nested <c>children</c> arrays included (COA tree).</summary>
    public static int CountRecords(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return 0;
        var total = 0;
        foreach (var p in payload.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.Array)
                total += CountTree(p.Value);
        return total;
    }

    private static int CountTree(JsonElement array)
    {
        var n = 0;
        foreach (var item in array.EnumerateArray())
        {
            n++;
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                n += CountTree(children);
        }
        return n;
    }

    /// <summary>The payload's top-level <c>verified</c> boolean (tax packs are Unverified, D-13), else null.</summary>
    public static bool? Verified(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("verified", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    public static string? TopLevelString(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
