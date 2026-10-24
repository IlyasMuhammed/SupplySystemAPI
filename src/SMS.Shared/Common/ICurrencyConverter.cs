namespace SMS.Shared.Common;

// A46 C9 / E-4 — owner: BE-2 (Finance). Signature is stable; consumers (Demand price resolver) compile against it.
// Names are deliberately NOT CurrencyConversionResult/ExchangeRateResult: A35 already owns CurrencyConversionResult.

/// <summary>
/// A46 — a conversion request. Identify each currency by <c>*CurrencyId</c> (global lookups.Currencies Guid) OR by
/// <c>*CurrencyCode</c> (ISO, any case); the id wins when both are given. The caller's organization (tenant) is used.
/// </summary>
/// <param name="Amount">Amount in the From currency.</param>
/// <param name="EffectiveDate">The rate in force on this date is used (latest on or before it). Time part ignored.</param>
/// <param name="DecimalPlaces">Result rounding, MidpointRounding.AwayFromZero via <see cref="CurrencyConventions.RoundAmount"/> (0..6). Default 4.</param>
public sealed record CurrencyConvertRequest(
    decimal  Amount,
    Guid?    FromCurrencyId,
    string?  FromCurrencyCode,
    Guid?    ToCurrencyId,
    string?  ToCurrencyCode,
    DateTime EffectiveDate,
    int      DecimalPlaces = 4);

/// <summary>
/// Outcome of a conversion. Never throws for a missing rate: <see cref="Success"/> is false and <see cref="ErrorMessage"/>
/// names the currency and date. Same currency: Success, ConvertedAmount = Amount (unrounded), RateUsed = 1.
/// </summary>
/// <param name="ConvertedAmount">Rounded to the request's DecimalPlaces; 0 when not successful.</param>
/// <param name="RateUsed">Units of To per 1 From (10dp); 0 when not successful.</param>
/// <param name="RateDate">Effective date of the rate used (the later start of the legs for a cross rate); the requested date when same-currency.</param>
public sealed record CurrencyConvertResult(
    bool     Success,
    decimal  ConvertedAmount,
    decimal  RateUsed,
    DateTime RateDate,
    string   FromCurrencyCode,
    string   ToCurrencyCode,
    string?  ErrorMessage);

/// <summary>Outcome of <see cref="ICurrencyConverter.GetRateAsync"/>. Rate/RateDate are 0/default when not found.</summary>
public sealed record CurrencyRateLookup(
    bool     Found,
    decimal  Rate,
    DateTime RateDate,
    string   FromCurrencyCode,
    string   ToCurrencyCode,
    string?  ErrorMessage);

/// <summary>
/// A46 C9 — converts money between the current organization's currencies using its stored exchange rates: same-currency
/// short circuit, direct, inverse, and cross rates triangulated through the organization's rate/base currency. Implemented
/// in Finance. Register optional-safe: resolve with <c>GetService</c> / optional constructor parameter.
/// </summary>
public interface ICurrencyConverter
{
    Task<CurrencyConvertResult> ConvertAsync(CurrencyConvertRequest request, CancellationToken ct = default);

    /// <summary>The rate (units of To per 1 From) that <see cref="ConvertAsync"/> would use.</summary>
    Task<CurrencyRateLookup> GetRateAsync(
        Guid? fromCurrencyId, string? fromCurrencyCode, Guid? toCurrencyId, string? toCurrencyCode,
        DateTime effectiveDate, CancellationToken ct = default);
}
