using SMS.Modules.Finance.Models;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// The organization's exchange rates as the Settings screen manages them (SAP alignment, S-4). Other
/// modules read rates through SMS.Shared's <c>IExchangeRateProvider</c>, not through this.
/// </summary>
public interface IExchangeRateService
{
    /// <summary>Live rates, optionally for one From and/or To currency, newest effective date first.</summary>
    Task<IReadOnlyList<ExchangeRateModel>> ListAsync(string? from, string? to, CancellationToken ct = default);

    Task<ExchangeRateModel> CreateAsync(SaveExchangeRateRequest req, int userId, CancellationToken ct = default);

    Task<ExchangeRateModel> UpdateAsync(Guid uuid, SaveExchangeRateRequest req, int userId, CancellationToken ct = default);

    /// <summary>Soft delete: the rate stops being used and its pair's day is free for a corrected one.</summary>
    Task DeleteAsync(Guid uuid, int userId, CancellationToken ct = default);

    /// <summary>
    /// The rate that would be used for from→to on <paramref name="date"/> (yyyy-MM-dd; today when blank),
    /// exactly as documents get it. Null when none is on file.
    /// </summary>
    Task<ExchangeRateQuoteModel?> QuoteAsync(string? from, string? to, string? date, CancellationToken ct = default);
}
