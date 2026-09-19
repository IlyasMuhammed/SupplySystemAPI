using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SMS.Modules.Logistics.Couriers;

/// <summary>
/// A stable hash of what a carrier call asks for, so the ledger can tell "the same request again"
/// from "a different request wearing the same key".
/// <para>
/// <b>Excluded:</b> the idempotency key (it is what the fingerprint is compared under) and the
/// credentials. Rotating an API key between a timeout and its retry is still the same booking; and
/// a hash of a secret is not something to store beside it.
/// </para>
/// <para>
/// <b>Normalised:</b> decimals by value, not scale — <c>10</c> and <c>10.00</c> are one weight — and
/// times as UTC round-trip strings. Everything else is compared exactly, in the order given:
/// packages are handling units with barcodes, and a reordered list is a caller that rebuilt the
/// request differently, which is worth refusing loudly rather than guessing about.
/// </para>
/// </summary>
internal static class CourierRequestFingerprint
{
    internal static string For(CourierBookingRequest r) => Hash(new
    {
        op = "BOOK",
        r.ConsignmentNumber,
        r.ServiceCode,
        ShipFrom = Address(r.ShipFrom),
        ShipTo   = Address(r.ShipTo),
        Packages = r.Packages.Select(p => new
        {
            p.Barcode,
            p.PackageType,
            LengthCm      = Number(p.LengthCm),
            WidthCm       = Number(p.WidthCm),
            HeightCm      = Number(p.HeightCm),
            GrossWeightKg = Number(p.GrossWeightKg),
            DeclaredValue = Number(p.DeclaredValue)
        }),
        r.FreightTerms,
        CodAmount = Number(r.CodAmount),
        r.CodCurrency,
        PickupWindowStart = Time(r.PickupWindowStart),
        PickupWindowEnd   = Time(r.PickupWindowEnd),
        r.ReferenceNumbers,
        r.SuppliedAwbNumber
    });

    internal static string For(CourierCancelRequest r) => Hash(new
    {
        op = "CANCEL",
        r.ConsignmentNumber,
        r.AwbNumber
    });

    private static object? Address(CourierAddress? a) => a is null ? null : new
    {
        a.ContactName, a.ContactPhone, a.ContactEmail,
        a.Line1, a.Line2, a.City, a.State, a.PostalCode, a.CountryIsoCode
    };

    // G29 prints the shortest exact representation, so trailing zeros never change the hash.
    private static string? Number(decimal? value) =>
        value?.ToString("G29", CultureInfo.InvariantCulture);

    private static string? Time(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
        { } v => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
    };

    private static string Hash(object canonical)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return Convert.ToHexString(SHA256.HashData(json)).ToLowerInvariant();
    }
}
