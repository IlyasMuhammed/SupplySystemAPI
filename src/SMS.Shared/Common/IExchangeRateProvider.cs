namespace SMS.Shared.Common;

/// <summary>A rate found for a currency pair on a date.</summary>
/// <param name="FromCurrencyCode">ISO code converted from.</param>
/// <param name="ToCurrencyCode">ISO code converted to.</param>
/// <param name="Rate">1 unit of From = Rate units of To. Exactly 1 when From and To are the same.</param>
/// <param name="EffectiveDate">The date of the stored rate used (the latest on or before the date asked).</param>
/// <param name="Inverted">True when only the opposite pair was on file and this is its reciprocal.</param>
public sealed record ExchangeRateQuote(
    string   FromCurrencyCode,
    string   ToCurrencyCode,
    decimal  Rate,
    DateTime EffectiveDate,
    bool     Inverted);

/// <summary>
/// The current organization's exchange rates (SAP's TCURR in spirit), owned by SMS.Modules.Finance
/// and read through SMS.Shared so Demand, Integration and Logistics need no project reference to it.
/// <para>
/// Lookup: the latest rate for the pair with EffectiveDate on or before <c>asOf</c>; if none, the
/// reciprocal of the latest rate for the opposite pair; same currency → 1. No triangulation — an
/// organization that wants USD→EUR enters it. Codes are compared case-insensitively.
/// </para>
/// <para>
/// Register optional-safe: resolve with <c>GetService</c> (or take it as an optional constructor
/// parameter) so module test harnesses without Finance keep working.
/// </para>
/// </summary>
public interface IExchangeRateProvider
{
    /// <returns>Null when no usable rate is on file.</returns>
    Task<ExchangeRateQuote?> GetRateAsync(string fromCurrencyCode, string toCurrencyCode, DateTime asOf, CancellationToken ct = default);
}

/// <summary>
/// Converts an amount at a quoted rate, rounded the way every Finance amount is (2dp, away from zero).
/// </summary>
public static class ExchangeRateMath
{
    public static decimal Convert(decimal amount, decimal rate) =>
        Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);

    /// <summary>A35 D-13 — the same at the target currency's decimal places (0 for JPY, 3 for BHD).</summary>
    public static decimal Convert(decimal amount, decimal rate, int decimalPlaces) =>
        CurrencyConventions.RoundAmount(amount * rate, decimalPlaces);
}
