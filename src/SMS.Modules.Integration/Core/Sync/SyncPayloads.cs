using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>
/// How a caller's payload is stored on its <see cref="EntityMap"/>: one canonical JSON form, so the
/// same data always yields the same fingerprint, and "sent the same thing twice" is detectable
/// without calling QuickBooks.
/// </summary>
internal static class SyncPayloads
{
    /// <summary>
    /// Fixed options — never the host's. Decimals are written without trailing zeros (10.50 and 10.5
    /// are the same price), enums by name, properties in declaration order.
    /// </summary>
    private static readonly JsonSerializerOptions Canonical = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented          = false,
        Converters             = { new NormalizedDecimalConverter(), new JsonStringEnumConverter() }
    };

    /// <summary>For the sync log and dry-run output: readable, polymorphic by runtime type.</summary>
    private static readonly JsonSerializerOptions Log = new()
    {
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented          = false,
        Converters             = { new JsonStringEnumConverter() }
    };

    public static string Serialize<T>(T payload) where T : class =>
        JsonSerializer.Serialize(payload, payload.GetType(), Canonical);

    public static object Deserialize(SyncKind kind, string json) => kind switch
    {
        SyncKind.Customer     => JsonSerializer.Deserialize<CustomerPayload>(json, Canonical)!,
        SyncKind.Vendor       => JsonSerializer.Deserialize<VendorPayload>(json, Canonical)!,
        SyncKind.Item         => JsonSerializer.Deserialize<ItemPayload>(json, Canonical)!,
        SyncKind.SalesInvoice => JsonSerializer.Deserialize<SalesInvoicePayload>(json, Canonical)!,
        SyncKind.Bill         => JsonSerializer.Deserialize<BillPayload>(json, Canonical)!,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>A built QuickBooks object (or anything else) as log JSON.</summary>
    public static string ToLogJson(object value) => JsonSerializer.Serialize(value, value.GetType(), Log);

    /// <summary>SHA-256 of the canonical JSON, lower-case hex (64 chars).</summary>
    public static string Fingerprint(string canonicalJson) => Sha256Hex(canonicalJson);

    /// <summary>
    /// The ledger key for one intended call. The source system is part of it so two callers that
    /// happen to share an ExternalId (and a payload) never share a ledger row.
    /// </summary>
    public static string CommandKey(
        int connectionId, string sourceSystem, SyncKind kind, string externalId, OutboxOperation operation, string fingerprint) =>
        Sha256Hex($"{connectionId}|{sourceSystem}|{kind}|{externalId}|{operation}|{fingerprint}");

    public static string Sha256Hex(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    // ── Dependencies ──────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions DependencyJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters           = { new JsonStringEnumConverter() }
    };

    public static string SerializeDependencies(IReadOnlyCollection<GatewayDependency> deps) =>
        JsonSerializer.Serialize(deps, DependencyJson);

    public static IReadOnlyList<GatewayDependency> DeserializeDependencies(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<GatewayDependency>>(json, DependencyJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The text an ExternalId becomes inside DependsOnJson — for a cheap LIKE prefilter.</summary>
    public static string EncodedForContains(string externalId) => JsonEncodedText.Encode(externalId).ToString();

    /// <summary>What a document needs in QuickBooks before it can be sent: its party, and every item its lines use.</summary>
    public static IReadOnlyList<GatewayDependency> DependenciesOf(SyncKind kind, object payload)
    {
        var list = new List<GatewayDependency>();

        switch (payload)
        {
            case SalesInvoicePayload invoice when kind == SyncKind.SalesInvoice:
                if (!string.IsNullOrWhiteSpace(invoice.CustomerExternalId))
                    list.Add(new GatewayDependency(SyncKind.Customer, invoice.CustomerExternalId.Trim()));
                foreach (var line in invoice.Lines ?? [])
                    if (!string.IsNullOrWhiteSpace(line.ItemExternalId))
                        list.Add(new GatewayDependency(SyncKind.Item, line.ItemExternalId.Trim()));
                break;

            case BillPayload bill when kind == SyncKind.Bill:
                if (!string.IsNullOrWhiteSpace(bill.VendorExternalId))
                    list.Add(new GatewayDependency(SyncKind.Vendor, bill.VendorExternalId.Trim()));
                foreach (var line in bill.Lines ?? [])
                    if (!string.IsNullOrWhiteSpace(line.ItemExternalId))
                        list.Add(new GatewayDependency(SyncKind.Item, line.ItemExternalId!.Trim()));
                break;
        }

        return list.Distinct().ToList();
    }

    public static bool IsParty(SyncKind kind) => kind is SyncKind.Customer or SyncKind.Vendor;
    public static bool IsMasterData(SyncKind kind) => kind is SyncKind.Customer or SyncKind.Vendor or SyncKind.Item;
    public static bool IsDocument(SyncKind kind) => kind is SyncKind.SalesInvoice or SyncKind.Bill;

    /// <summary>Money as QuickBooks keeps it: two places, half away from zero.</summary>
    public static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static string Rate(decimal percent) => percent.ToString("0.####", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// A tax rate as TaxCodeMapping stores it (4 places) — the mapping screen lists rates this way, so a
    /// payload's rate must be compared this way too.
    /// </summary>
    public static decimal NormalizeRate(decimal percent) => decimal.Round(percent, 4, MidpointRounding.AwayFromZero);

    /// <summary>The longest caller tax code a mapping can hold (<c>TaxCodeMapping.SourceTaxCode</c>).</summary>
    public const int MaxTaxCodeLength = 20;

    /// <summary>
    /// A caller's tax code as TaxCodeMapping stores and compares it (plan S-11): trimmed, upper-case — SCM's
    /// codes are upper-case already, and "gst17" from another system is the same code. Null when blank.
    /// </summary>
    public static string? NormalizeTaxCode(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();

    public static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private sealed class NormalizedDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
                ? decimal.Parse(reader.GetString()!, NumberStyles.Number, CultureInfo.InvariantCulture)
                : reader.GetDecimal();

        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.ToString("0.############################", CultureInfo.InvariantCulture), skipInputValidation: true);
    }
}

/// <summary>Retry spacing for throttled / unknown-outcome calls: 1m, 5m, 15m, 1h, 3h, 6h, 12h, 24h.</summary>
internal static class SyncBackoff
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
        TimeSpan.FromHours(3), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24)
    ];

    /// <param name="attemptCount">Attempts made so far (1 after the first failure).</param>
    public static TimeSpan After(int attemptCount) =>
        Schedule[Math.Clamp(attemptCount - 1, 0, Schedule.Length - 1)];
}

internal static class SyncText
{
    public static string? Clip(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
