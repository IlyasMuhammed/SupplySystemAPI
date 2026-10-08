using SMS.Modules.Finance.Models;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// The legacy pair-shaped read of exchange rates (SAP alignment S-4), kept for its callers (D-17, A35-E-02) and served from
/// <c>finance.currency_rates</c>: each range appears as X → rate currency. Writes moved to <c>api/currency-rates</c>.
/// </summary>
public interface IExchangeRateService
{
    /// <summary>Ranges as pairs, optionally for one From and/or To code, newest start first.</summary>
    Task<IReadOnlyList<ExchangeRateModel>> ListAsync(string? from, string? to, CancellationToken ct = default);

    /// <summary>
    /// The rate that would be used for from→to on <paramref name="date"/> (yyyy-MM-dd; today when blank),
    /// exactly as documents get it (IExchangeRateProvider). Null when none is on file.
    /// </summary>
    Task<ExchangeRateQuoteModel?> QuoteAsync(string? from, string? to, string? date, CancellationToken ct = default);
}
