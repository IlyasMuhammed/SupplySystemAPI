using System.Globalization;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

/// <summary>Small helpers the tax-code and exchange-rate services share: date-only text, rates in words, mapping.</summary>
internal static class FinanceSetupFormat
{
    internal const string DateFormat = "yyyy-MM-dd";

    internal static string Date(DateTime value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// A calendar date as the caller wrote it. "2026-10-01" is the contract; a full ISO date-time is
    /// accepted too, and only its first ten characters are read — the date as written, never shifted
    /// by a time zone. Null when blank.
    /// </summary>
    internal static DateTime? ParseDate(string? text, string what)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();
        var datePart = trimmed.Length > 10 && (trimmed[10] == 'T' || trimmed[10] == ' ') ? trimmed[..10] : trimmed;

        if (DateTime.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);

        throw new BadRequestException($"{what} must be a date written as yyyy-MM-dd, e.g. 2026-10-01 — '{trimmed}' is not.");
    }

    /// <summary>17 → "17", 7.50 → "7.5", 0.25 → "0.25".</summary>
    internal static string Percent(decimal rate) => rate.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>278.50000000 → "278.5" — up to the eight decimals a rate can have.</summary>
    internal static string Rate(decimal rate) => rate.ToString("0.########", CultureInfo.InvariantCulture);

    internal static string SideLabel(string usage) => usage switch
    {
        TaxCodeUsages.Sales    => "sales",
        TaxCodeUsages.Purchase => "purchases",
        _                      => "sales and purchases"
    };

    internal static TaxCodeModel ToModel(TaxCode t) => new()
    {
        Uuid        = t.Uuid,
        Code        = t.Code,
        Name        = t.Name,
        Description = t.Description,
        RatePercent = t.RatePercent,
        Usage       = t.Usage,
        IsDefault   = t.IsDefault,
        IsActive    = t.IsActive
    };

    internal static ExchangeRateModel ToModel(ExchangeRate r) => new()
    {
        Uuid             = r.Uuid,
        FromCurrencyCode = r.FromCurrencyCode,
        ToCurrencyCode   = r.ToCurrencyCode,
        Rate             = r.Rate,
        EffectiveDate    = Date(r.EffectiveDate),
        Source           = r.Source,
        Notes            = r.Notes,
        CreatedDate      = r.CreatedDate
    };

    /// <summary>Trimmed; null when empty.</summary>
    internal static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
