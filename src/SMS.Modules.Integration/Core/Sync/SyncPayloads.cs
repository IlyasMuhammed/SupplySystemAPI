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
        SyncKind.Payment       => JsonSerializer.Deserialize<PaymentPayload>(json, Canonical)!,
        SyncKind.BillPayment   => JsonSerializer.Deserialize<BillPaymentPayload>(json, Canonical)!,
        SyncKind.SalesReceipt  => JsonSerializer.Deserialize<SalesReceiptPayload>(json, Canonical)!,
        SyncKind.CreditMemo    => JsonSerializer.Deserialize<CreditMemoPayload>(json, Canonical)!,
        SyncKind.RefundReceipt => JsonSerializer.Deserialize<RefundReceiptPayload>(json, Canonical)!,
        SyncKind.VendorCredit  => JsonSerializer.Deserialize<VendorCreditPayload>(json, Canonical)!,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    /// <summary>The kind a payload object is sent as (A44: one payload class per kind).</summary>
    public static SyncKind? KindOf(object payload) => payload switch
    {
        CustomerPayload      => SyncKind.Customer,
        VendorPayload        => SyncKind.Vendor,
        ItemPayload          => SyncKind.Item,
        SalesInvoicePayload  => SyncKind.SalesInvoice,
        BillPayload          => SyncKind.Bill,
        PaymentPayload       => SyncKind.Payment,
        BillPaymentPayload   => SyncKind.BillPayment,
        SalesReceiptPayload  => SyncKind.SalesReceipt,
        CreditMemoPayload    => SyncKind.CreditMemo,
        RefundReceiptPayload => SyncKind.RefundReceipt,
        VendorCreditPayload  => SyncKind.VendorCredit,
        _                    => null
    };

    /// <summary>A44: SourceType of a payload (POS documents follow the POSSale / POSReturn sync control, D-14).</summary>
    public static string? SourceTypeOf(object payload) => payload switch
    {
        SalesInvoicePayload s  => s.SourceType,
        SalesDocumentPayload d => d.SourceType,
        PaymentPayload p       => p.SourceType,
        _                      => null
    };

    /// <summary>The document number of a stored document payload (null for master data or unreadable JSON).</summary>
    public static string? DocNumberOf(object payload) => payload switch
    {
        SalesInvoicePayload s  => s.DocNumber?.Trim(),
        BillPayload b          => b.DocNumber?.Trim(),
        PaymentPayload p       => p.DocNumber?.Trim(),
        BillPaymentPayload bp  => bp.DocNumber?.Trim(),
        SalesDocumentPayload d => d.DocNumber?.Trim(),
        VendorCreditPayload v  => v.DocNumber?.Trim(),
        _                      => null
    };

    /// <summary>The transaction date of a document payload; null for master data.</summary>
    public static DateTime? DocumentDateOf(object payload) => payload switch
    {
        SalesInvoicePayload s  => s.TxnDate,
        BillPayload b          => b.TxnDate,
        PaymentPayload p       => p.TxnDate,
        BillPaymentPayload bp  => bp.TxnDate,
        SalesDocumentPayload d => d.TxnDate,
        VendorCreditPayload v  => v.TxnDate,
        _                      => null
    };

    /// <summary>
    /// A44 D-54: the ExternalId of the "POS Walk-in" customer a sales receipt / credit memo / refund receipt without a
    /// customer is sent under. The gateway keeps one map of it per connection and source system; when the connection
    /// has a <c>PosDefaultCustomerRef</c> that map is linked to that QuickBooks customer, otherwise it is created once.
    /// </summary>
    public const string WalkInCustomerExternalId = "POS-WALK-IN";

    public const string WalkInCustomerName = "POS Walk-in";

    /// <summary>The customer a sales document is sent under: its own, else the walk-in customer.</summary>
    public static string CustomerOf(SalesDocumentPayload document) =>
        string.IsNullOrWhiteSpace(document.CustomerExternalId) ? WalkInCustomerExternalId : document.CustomerExternalId.Trim();

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

            // A44 (D-19..D-24): documents can depend on documents — a payment on the invoices it pays, a bill payment
            // on its bills, an applied credit memo on its invoice. The dependency machinery treats them like master
            // data: placeholder maps are asked of the SCM source that owns the kind (Finance for invoices and bills).
            case PaymentPayload payment when kind == SyncKind.Payment:
                if (!string.IsNullOrWhiteSpace(payment.CustomerExternalId))
                    list.Add(new GatewayDependency(SyncKind.Customer, payment.CustomerExternalId.Trim()));
                foreach (var application in payment.Applications ?? [])
                    if (!string.IsNullOrWhiteSpace(application.InvoiceExternalId))
                        list.Add(new GatewayDependency(SyncKind.SalesInvoice, application.InvoiceExternalId.Trim()));
                break;

            case BillPaymentPayload billPayment when kind == SyncKind.BillPayment:
                if (!string.IsNullOrWhiteSpace(billPayment.VendorExternalId))
                    list.Add(new GatewayDependency(SyncKind.Vendor, billPayment.VendorExternalId.Trim()));
                foreach (var application in billPayment.Applications ?? [])
                    if (!string.IsNullOrWhiteSpace(application.BillExternalId))
                        list.Add(new GatewayDependency(SyncKind.Bill, application.BillExternalId.Trim()));
                break;

            case SalesDocumentPayload document when kind is SyncKind.SalesReceipt or SyncKind.CreditMemo or SyncKind.RefundReceipt:
                list.Add(new GatewayDependency(SyncKind.Customer, CustomerOf(document)));
                foreach (var line in document.Lines ?? [])
                    if (!string.IsNullOrWhiteSpace(line.ItemExternalId))
                        list.Add(new GatewayDependency(SyncKind.Item, line.ItemExternalId.Trim()));
                if (document is CreditMemoPayload { ApplyToOriginal: true } memo && !string.IsNullOrWhiteSpace(memo.OriginalInvoiceExternalId))
                    list.Add(new GatewayDependency(SyncKind.SalesInvoice, memo.OriginalInvoiceExternalId.Trim()));
                break;

            case VendorCreditPayload credit when kind == SyncKind.VendorCredit:
                if (!string.IsNullOrWhiteSpace(credit.VendorExternalId))
                    list.Add(new GatewayDependency(SyncKind.Vendor, credit.VendorExternalId.Trim()));
                foreach (var line in credit.Lines ?? [])
                    if (!string.IsNullOrWhiteSpace(line.ItemExternalId))
                        list.Add(new GatewayDependency(SyncKind.Item, line.ItemExternalId!.Trim()));
                break;
        }

        return list.Distinct().ToList();
    }

    public static bool IsParty(SyncKind kind) => kind is SyncKind.Customer or SyncKind.Vendor;
    public static bool IsMasterData(SyncKind kind) => kind is SyncKind.Customer or SyncKind.Vendor or SyncKind.Item;
    public static bool IsDocument(SyncKind kind) => !IsMasterData(kind);

    /// <summary>
    /// A44 D-51: kinds QuickBooks lets the integration undo (void, or delete for BillPayment / RefundReceipt). Credit
    /// memos and vendor credits are flagged for the accountant instead — they may be applied in QuickBooks already.
    /// </summary>
    public static bool IsVoidable(SyncKind kind) =>
        kind is SyncKind.SalesInvoice or SyncKind.Payment or SyncKind.SalesReceipt or SyncKind.BillPayment or SyncKind.RefundReceipt;

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
