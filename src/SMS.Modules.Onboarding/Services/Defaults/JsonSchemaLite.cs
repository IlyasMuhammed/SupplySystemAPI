using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SMS.Modules.Onboarding.Services.Defaults;

/// <summary>
/// A50 D-4 — the JSON Schema subset the pack schemas (Packs/schemas/*.json, DATA) use. The repo carries no schema library,
/// so this covers: type (one or a list), properties, required, additionalProperties (bool or schema), items, enum, const,
/// minimum / maximum / exclusiveMinimum / exclusiveMaximum, minLength / maxLength, pattern, minItems / maxItems and local
/// <c>$ref</c> ("#/$defs/x", "#/definitions/x"). Anything else (format, oneOf…) is ignored, never failed.
/// </summary>
internal static class JsonSchemaLite
{
    private const int MaxErrors = 50;

    public static List<string> Validate(JsonElement schema, JsonElement instance)
    {
        var errors = new List<string>();
        Check(schema, schema, instance, "$", errors, 0);
        return errors;
    }

    private static void Check(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> errors, int depth)
    {
        if (errors.Count >= MaxErrors || depth > 64 || schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("$ref", out var reference) && reference.ValueKind == JsonValueKind.String)
        {
            if (Resolve(root, reference.GetString()!) is { } target)
                Check(root, target, value, path, errors, depth + 1);
            // 2020-12 allows siblings of $ref; carry on with them below.
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, value))
        {
            errors.Add($"{path}: expected {Describe(type)}, got {Kind(value)}.");
            return;
        }

        if (schema.TryGetProperty("enum", out var enumValues) && enumValues.ValueKind == JsonValueKind.Array
            && !enumValues.EnumerateArray().Any(e => JsonEquals(e, value)))
            errors.Add($"{path}: must be one of {string.Join(", ", enumValues.EnumerateArray().Select(e => e.GetRawText()))}.");

        if (schema.TryGetProperty("const", out var constant) && !JsonEquals(constant, value))
            errors.Add($"{path}: must be {constant.GetRawText()}.");

        switch (value.ValueKind)
        {
            case JsonValueKind.Number: CheckNumber(schema, value, path, errors); break;
            case JsonValueKind.String: CheckString(schema, value, path, errors); break;
            case JsonValueKind.Array:  CheckArray(root, schema, value, path, errors, depth); break;
            case JsonValueKind.Object: CheckObject(root, schema, value, path, errors, depth); break;
        }
    }

    private static void CheckNumber(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var n = value.GetDouble();
        if (Num(schema, "minimum") is { } min && n < min) errors.Add($"{path}: must be >= {Fmt(min)}.");
        if (Num(schema, "maximum") is { } max && n > max) errors.Add($"{path}: must be <= {Fmt(max)}.");
        if (Num(schema, "exclusiveMinimum") is { } xmin && n <= xmin) errors.Add($"{path}: must be > {Fmt(xmin)}.");
        if (Num(schema, "exclusiveMaximum") is { } xmax && n >= xmax) errors.Add($"{path}: must be < {Fmt(xmax)}.");
    }

    private static void CheckString(JsonElement schema, JsonElement value, string path, List<string> errors)
    {
        var s = value.GetString() ?? string.Empty;
        if (Num(schema, "minLength") is { } minLen && s.Length < minLen) errors.Add($"{path}: must be at least {Fmt(minLen)} characters.");
        if (Num(schema, "maxLength") is { } maxLen && s.Length > maxLen) errors.Add($"{path}: must be at most {Fmt(maxLen)} characters.");
        if (schema.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String)
        {
            try
            {
                if (!Regex.IsMatch(s, pattern.GetString()!, RegexOptions.None, TimeSpan.FromMilliseconds(200)))
                    errors.Add($"{path}: '{s}' does not match {pattern.GetString()}.");
            }
            catch (ArgumentException) { /* a pattern .NET cannot read is ignored */ }
            catch (RegexMatchTimeoutException) { errors.Add($"{path}: pattern check timed out."); }
        }
    }

    private static void CheckArray(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> errors, int depth)
    {
        var count = value.GetArrayLength();
        if (Num(schema, "minItems") is { } minItems && count < minItems) errors.Add($"{path}: needs at least {Fmt(minItems)} item(s).");
        if (Num(schema, "maxItems") is { } maxItems && count > maxItems) errors.Add($"{path}: allows at most {Fmt(maxItems)} item(s).");
        if (schema.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
        {
            var i = 0;
            foreach (var item in value.EnumerateArray())
                Check(root, items, item, $"{path}[{i++}]", errors, depth + 1);
        }
    }

    private static void CheckObject(JsonElement root, JsonElement schema, JsonElement value, string path, List<string> errors, int depth)
    {
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            foreach (var name in required.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.String).Select(r => r.GetString()!))
                if (!value.TryGetProperty(name, out _))
                    errors.Add($"{path}: '{name}' is required.");

        var hasProperties = schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object;
        schema.TryGetProperty("additionalProperties", out var additional);

        foreach (var property in value.EnumerateObject())
        {
            var childPath = $"{path}.{property.Name}";
            if (hasProperties && properties.TryGetProperty(property.Name, out var propertySchema))
                Check(root, propertySchema, property.Value, childPath, errors, depth + 1);
            else if (additional.ValueKind == JsonValueKind.False)
                errors.Add($"{path}: property '{property.Name}' is not allowed.");
            else if (additional.ValueKind == JsonValueKind.Object)
                Check(root, additional, property.Value, childPath, errors, depth + 1);
        }
    }

    private static JsonElement? Resolve(JsonElement root, string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return null;
        var current = root;
        foreach (var segment in reference[2..].Split('/'))
        {
            var name = segment.Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current)) return null;
        }
        return current;
    }

    private static bool MatchesType(JsonElement type, JsonElement value) => type.ValueKind switch
    {
        JsonValueKind.String => Is(type.GetString()!, value),
        JsonValueKind.Array  => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && Is(t.GetString()!, value)),
        _ => true
    };

    private static bool Is(string type, JsonElement value) => type switch
    {
        "object"  => value.ValueKind == JsonValueKind.Object,
        "array"   => value.ValueKind == JsonValueKind.Array,
        "string"  => value.ValueKind == JsonValueKind.String,
        "number"  => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && Math.Abs(value.GetDouble() % 1) < double.Epsilon,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null"    => value.ValueKind == JsonValueKind.Null,
        _ => true
    };

    private static string Describe(JsonElement type) =>
        type.ValueKind == JsonValueKind.Array ? string.Join(" or ", type.EnumerateArray().Select(t => t.GetString())) : type.GetString() ?? "?";

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        var k => k.ToString().ToLowerInvariant()
    };

    private static double? Num(JsonElement schema, string name) =>
        schema.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Fmt(double d) => d.ToString(CultureInfo.InvariantCulture);

    internal static bool JsonEquals(JsonElement a, JsonElement b) => JsonCanonical.Write(a) == JsonCanonical.Write(b);
}
