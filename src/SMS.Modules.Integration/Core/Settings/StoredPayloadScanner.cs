using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Core.Settings;

/// <summary>What the stored payloads say about the values a mapping screen has to cover.</summary>
/// <param name="TaxRates">
/// Tax percent → how many invoice/bill lines <b>without a tax code</b> carry it — the lines a percent
/// mapping is for (plan D-4). Lines with a code are counted under <paramref name="TaxCodes"/> instead.
/// </param>
/// <param name="TaxCodes">Caller tax code (normalized) → how many invoice/bill lines carry it (plan S-11).</param>
/// <param name="TaxCodeRates">Caller tax code → the rates seen with it → how many lines.</param>
/// <param name="PaymentTermIds">Caller payment-term id → how many customers/vendors reference it.</param>
/// <param name="Currencies">ISO code → how many records are in it.</param>
/// <param name="DocumentCurrencies">ISO code → how many invoices and bills are in it (those need exchange rates).</param>
internal sealed record StoredPayloadFacts(
    IReadOnlyDictionary<decimal, int> TaxRates,
    IReadOnlyDictionary<string, int>  TaxCodes,
    IReadOnlyDictionary<string, IReadOnlyDictionary<decimal, int>> TaxCodeRates,
    IReadOnlyDictionary<string, int>  PaymentTermIds,
    IReadOnlyDictionary<string, int>  Currencies,
    IReadOnlyDictionary<string, int>  DocumentCurrencies);

/// <summary>
/// Reads the payloads the gateway has stored (<c>EntityMap.PayloadJson</c>) to find the tax codes, tax
/// rates, payment terms and currencies callers actually send. This is the plan's "TaxRateSeen" view: the
/// mapping screen lists what is really in use, not what somebody guessed might be.
/// <para>
/// Parsed with System.Text.Json into the Shared payload classes, case-insensitively, so it does not
/// care which naming policy the gateway stored them with. A payload that does not parse is skipped —
/// the gateway blocks those itself.
/// </para>
/// </summary>
internal static class StoredPayloadScanner
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<StoredPayloadFacts> ScanAsync(IntegrationDbContext db, int connectionId, CancellationToken ct)
    {
        var rows = await db.EntityMaps
            .AsNoTracking()
            .Where(m => m.ConnectionId == connectionId && m.PayloadJson != null)
            .Select(m => new { m.Kind, m.PayloadJson })
            .ToListAsync(ct);

        var rates      = new Dictionary<decimal, int>();
        var codes      = new Dictionary<string, int>(StringComparer.Ordinal);
        var codeRates  = new Dictionary<string, Dictionary<decimal, int>>(StringComparer.Ordinal);
        var terms      = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var currencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var documents  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Tax(string? taxCode, decimal? rate)
        {
            if (SyncPayloads.NormalizeTaxCode(taxCode) is { } code)
            {
                Count(codes, code);
                if (rate is { } r)
                {
                    if (!codeRates.TryGetValue(code, out var seen)) codeRates[code] = seen = new Dictionary<decimal, int>();
                    Count(seen, NormalizeRate(r));
                }
            }
            else if (rate is { } r)
            {
                Count(rates, NormalizeRate(r));
            }
        }

        foreach (var row in rows)
        {
            switch (row.Kind)
            {
                case SyncKind.SalesInvoice when Parse<SalesInvoicePayload>(row.PayloadJson) is { } invoice:
                    foreach (var line in invoice.Lines ?? [])
                        Tax(line.TaxCode, line.TaxPercent);
                    Count(currencies, invoice.CurrencyCode);
                    Count(documents, invoice.CurrencyCode);
                    break;

                case SyncKind.Bill when Parse<BillPayload>(row.PayloadJson) is { } bill:
                    // A line with neither a code nor a rate takes the default purchase tax code: nothing to map.
                    foreach (var line in bill.Lines ?? [])
                        Tax(line.TaxCode, line.TaxPercent);
                    Count(currencies, bill.CurrencyCode);
                    Count(documents, bill.CurrencyCode);
                    break;

                case SyncKind.Customer when Parse<CustomerPayload>(row.PayloadJson) is { } customer:
                    Count(terms, customer.PaymentTermExternalId);
                    Count(currencies, customer.CurrencyCode);
                    break;

                case SyncKind.Vendor when Parse<VendorPayload>(row.PayloadJson) is { } vendor:
                    Count(terms, vendor.PaymentTermExternalId);
                    Count(currencies, vendor.CurrencyCode);
                    break;
            }
        }

        return new StoredPayloadFacts(
            rates,
            codes,
            codeRates.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<decimal, int>)kv.Value, StringComparer.Ordinal),
            terms,
            currencies,
            documents);
    }

    /// <summary>Four decimal places — the precision <c>TaxCodeMapping.TaxPercent</c> is stored with.</summary>
    public static decimal NormalizeRate(decimal rate) => decimal.Round(rate, 4, MidpointRounding.AwayFromZero);

    private static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Count(Dictionary<decimal, int> counts, decimal key) =>
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

    private static void Count(Dictionary<string, int> counts, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        var trimmed = key.Trim();
        counts[trimmed] = counts.TryGetValue(trimmed, out var n) ? n + 1 : 1;
    }
}
