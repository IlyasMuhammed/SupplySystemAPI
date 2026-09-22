using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Couriers.Dhl;

/// <summary>
/// What goes to DHL Express's MyDHL API and what comes back, shaped by its published OpenAPI schema.
/// <para>
/// DHL validates hard: a missing postal code, a phone-less contact or a 46-character address line is a
/// 422, and a 422 on a booking is a person's time. So every limit the schema states is checked here, in
/// terms this system's users understand, <em>before</em> the call — a refusal that names the field is
/// worth more than DHL's "required key [postalCode] not found".
/// </para>
/// </summary>
internal static class DhlPayloads
{
    // The schema's own limits (supermodelIoLogisticsExpressAddressCreateShipmentRequest / Contact).
    private const int AddressLineMax = 45;
    private const int AddressLines   = 3;
    private const int CityMax        = 45;
    private const int PostalCodeMax  = 12;
    private const int FullNameMax    = 255;
    private const int CompanyMax     = 100;
    private const int PhoneMax       = 70;
    private const int EmailMax       = 70;
    private const int ReferenceMax   = 35;
    private const int DescriptionMax = 70;

    // ── Dates ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// DHL's own format: <c>2006-06-26T17:00:00 GMT+01:00</c>. The time is the origin's local time with
    /// its offset; a UTC instant with a +00:00 offset says the same thing without needing to know the zone.
    /// </summary>
    internal static string Stamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
        + " GMT+00:00";

    // ── Checking before sending ───────────────────────────────────────────────

    /// <summary>The first thing wrong with an address for DHL, in words a warehouse clerk can act on, or null.</summary>
    internal static string? AddressProblem(string role, CourierAddress a, bool forBooking, string? fallbackPostalCode)
    {
        if (string.IsNullOrWhiteSpace(IsoOf(a)) || IsoOf(a)!.Length != 2)
            return $"The {role} address has no two-letter country code (like PK), and DHL books by it.";

        if (string.IsNullOrWhiteSpace(a.City))
            return $"The {role} address has no city.";

        if (string.IsNullOrWhiteSpace(PostalOf(a, fallbackPostalCode)))
            return $"The {role} address has no postal code, which DHL requires. " + (fallbackPostalCode is null && role == "ship-from"
                ? "Add a ShipperPostalCode credential to the DHL account to use one for every collection."
                : "Add it to the address.");

        if (!forBooking) return null;

        if (string.IsNullOrWhiteSpace(a.Line1))
            return $"The {role} address has no street line.";

        if (Lines(a.Line1, a.Line2) is null)
            return $"The {role} address is longer than DHL takes — three lines of {AddressLineMax} characters. Shorten it.";

        if (string.IsNullOrWhiteSpace(a.ContactPhone))
            return $"The {role} contact has no phone number, which DHL requires.";

        return null;
    }

    internal static string? PackageProblem(CourierPackage p) =>
        p.GrossWeightKg is not > 0m
            ? $"Package {p.Barcode} has no weight. DHL prices and books by weight, so weigh it at the pack station."
            : null;

    // ── Addresses ─────────────────────────────────────────────────────────────

    internal static string? IsoOf(CourierAddress a) => a.CountryIsoCode?.Trim().ToUpperInvariant();

    internal static string? PostalOf(CourierAddress a, string? fallback) =>
        Clip(string.IsNullOrWhiteSpace(a.PostalCode) ? fallback : a.PostalCode, PostalCodeMax);

    /// <summary>The address as MyDHL's rating call takes it: flat, no street, no contact.</summary>
    internal static object RatesAddress(CourierAddress a, string? fallbackPostalCode) => new
    {
        postalCode  = PostalOf(a, fallbackPostalCode),
        cityName    = Clip(a.City, CityMax),
        countryCode = IsoOf(a)
    };

    /// <summary>A party as MyDHL's shipment call takes it: a postal address and a contact, both mandatory.</summary>
    internal static object Party(CourierAddress a, string? fallbackPostalCode, string? companyFallback)
    {
        var lines = Lines(a.Line1, a.Line2)!;
        var name  = Clip(a.ContactName, FullNameMax) ?? Clip(companyFallback, FullNameMax) ?? "Receiving";

        return new
        {
            postalAddress = new
            {
                postalCode   = PostalOf(a, fallbackPostalCode),
                cityName     = Clip(a.City, CityMax),
                countryCode  = IsoOf(a),
                addressLine1 = lines[0],
                addressLine2 = lines.Count > 1 ? lines[1] : null,
                addressLine3 = lines.Count > 2 ? lines[2] : null
            },
            contactInformation = new
            {
                email       = Clip(a.ContactEmail, EmailMax),
                phone       = Clip(a.ContactPhone, PhoneMax),
                // DHL insists on both. A person with no company is their own company, which is what
                // every carrier portal does with the same box.
                companyName = Clip(companyFallback, CompanyMax) ?? Clip(name, CompanyMax),
                fullName    = name
            }
        };
    }

    /// <summary>
    /// Street and second line, broken at spaces into at most three lines of 45. Null when it will not
    /// fit — silently cutting an address is how a parcel goes to the wrong door.
    /// </summary>
    internal static List<string>? Lines(string? line1, string? line2)
    {
        var lines = new List<string>();

        foreach (var text in new[] { line1, line2 })
        {
            var remaining = text?.Trim();
            while (!string.IsNullOrEmpty(remaining))
            {
                if (remaining.Length <= AddressLineMax) { lines.Add(remaining); break; }

                var cut = remaining.LastIndexOf(' ', AddressLineMax);
                if (cut <= 0) return null; // one word longer than a line

                lines.Add(remaining[..cut].TrimEnd());
                remaining = remaining[cut..].TrimStart();
            }
        }

        return lines.Count is > 0 and <= AddressLines ? lines : null;
    }

    // ── Packages ──────────────────────────────────────────────────────────────

    /// <summary>A package for rating: weight, and dimensions when all three are known.</summary>
    internal static object RatedPackage(CourierPackage p) => new
    {
        weight     = Kg(p.GrossWeightKg!.Value),
        dimensions = Dimensions(p)
    };

    /// <summary>A package for booking. The barcode rides along as the piece reference, so a scan can be traced back.</summary>
    internal static object BookedPackage(CourierPackage p, string description) => new
    {
        weight             = Kg(p.GrossWeightKg!.Value),
        dimensions         = Dimensions(p),
        customerReferences = new[] { new { typeCode = "CU", value = Clip(p.Barcode, ReferenceMax) } },
        description        = Clip(description, DescriptionMax)
    };

    private static object? Dimensions(CourierPackage p) =>
        p is { LengthCm: > 0m, WidthCm: > 0m, HeightCm: > 0m }
            ? new { length = Cm(p.LengthCm!.Value), width = Cm(p.WidthCm!.Value), height = Cm(p.HeightCm!.Value) }
            : null;

    /// <summary>DHL takes weights to the gram and will not take less than one.</summary>
    private static decimal Kg(decimal kg) => Math.Max(0.001m, Math.Round(kg, 3, MidpointRounding.AwayFromZero));

    private static decimal Cm(decimal cm) => Math.Max(1m, Math.Round(cm, 0, MidpointRounding.AwayFromZero));

    internal static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    // ── Reading DHL's answers ─────────────────────────────────────────────────

    /// <summary>
    /// DHL's error, in one sentence. <c>detail</c> is the useful part; <c>additionalDetails</c> lists each
    /// field that failed, which is what somebody has to fix.
    /// </summary>
    internal static string DescribeError(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var parts = new List<string>();

            foreach (var name in new[] { "detail", "message", "title" })
                if (Text(root, name) is { Length: > 0 } text && !parts.Contains(text, StringComparer.OrdinalIgnoreCase))
                    parts.Add(text);

            if (root.ValueKind == JsonValueKind.Object
             && root.TryGetProperty("additionalDetails", out var extra) && extra.ValueKind == JsonValueKind.Array)
                parts.AddRange(extra.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrWhiteSpace(s))!);

            if (parts.Count > 0)
                return Clip($"DHL said: {string.Join(" — ", parts)}", 600)!;
        }
        catch (JsonException)
        {
            // Not JSON — a gateway's HTML, say. Fall through to the status.
        }

        return $"DHL answered {status} with nothing readable.";
    }

    /// <summary>The response with the label images taken out — they are megabytes of base64 and add nothing to a diagnosis.</summary>
    internal static string Redacted(string body)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["documents"] is JsonArray documents)
            {
                foreach (var document in documents.OfType<JsonObject>())
                    if (document["content"] is JsonValue content && content.TryGetValue<string>(out var text))
                        document["content"] = $"<{text.Length} characters of base64 removed>";

                return root.ToJsonString();
            }
        }
        catch (JsonException) { }

        return Clip(body, 4000) ?? string.Empty;
    }

    /// <summary>DHL's label document, as a file. Null when there is none, or it is in a format we did not ask for.</summary>
    internal static CourierLabel? LabelOf(JsonElement response, string awb)
    {
        if (!response.TryGetProperty("documents", out var documents) || documents.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var document in documents.EnumerateArray())
        {
            if (!string.Equals(Text(document, "typeCode"), "label", StringComparison.OrdinalIgnoreCase)) continue;

            var content = Text(document, "content");
            if (string.IsNullOrEmpty(content)) continue;

            var (contentType, extension) = Text(document, "imageFormat")?.ToUpperInvariant() switch
            {
                "PDF" or null => ("application/pdf", "pdf"),
                "PNG"         => ("image/png", "png"),
                "GIF"         => ("image/gif", "gif"),
                _             => (null, null)
            };

            if (contentType is null) continue;

            try
            {
                return new CourierLabel(Convert.FromBase64String(content), contentType, $"{awb}.{extension}");
            }
            catch (FormatException)
            {
                continue;
            }
        }

        return null;
    }

    internal static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    internal static decimal? Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var number)
            ? number
            : null;
}
