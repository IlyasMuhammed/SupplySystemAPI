using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>What a connection says about currencies, as the QuickBooks rules need it (plans D-1, S-10).</summary>
/// <param name="HomeCurrency">QuickBooks' home currency, upper-case; null when not known yet.</param>
/// <param name="MultiCurrencyEnabled">QuickBooks' multicurrency preference. Unknown counts as off.</param>
internal sealed record CurrencyFacts(string? HomeCurrency, bool MultiCurrencyEnabled);

/// <summary>
/// Currency facts and exchange rates for the validator and the object builder, so both apply exactly the
/// same rules: a record in the home currency is always fine; a foreign one only when QuickBooks'
/// multicurrency is on and the currency is active there; a foreign <i>document</i> also needs the
/// caller's rate for its date, which is sent as the transaction's ExchangeRate (plan S-10).
/// <para>
/// Rates come from <see cref="IExchangeRateProvider"/> (SMS.Shared, implemented by Finance), resolved
/// optional-safe: a host without Finance simply has no rates, so foreign documents are refused.
/// </para>
/// </summary>
internal interface ICurrencyRules
{
    Task<CurrencyFacts> FactsAsync(IntegrationConnection connection, CancellationToken ct = default);

    /// <summary>The currencies QuickBooks has active (from the reference snapshot), upper-case. Null when never loaded.</summary>
    Task<IReadOnlyCollection<string>?> ActiveCurrenciesAsync(IntegrationConnection connection, CancellationToken ct = default);

    /// <summary>
    /// Home-currency units per one unit of <paramref name="currency"/> on <paramref name="asOf"/> — QuickBooks'
    /// meaning of ExchangeRate. Null when no exchange-rate source is registered, no rate is on file, or the
    /// rate on file is not usable (zero or negative).
    /// </summary>
    Task<ExchangeRateQuote?> RateToHomeAsync(string currency, string homeCurrency, DateTime asOf, CancellationToken ct = default);
}

internal sealed class CurrencyRules : ICurrencyRules
{
    /// <summary>
    /// Places an exchange rate is sent with: the precision Finance stores rates in (decimal(18,8)). Only a
    /// reciprocal (inverted) rate ever has more.
    /// </summary>
    public const int RateDecimals = 8;

    private readonly IReferenceDataReader _reference;
    private readonly IServiceProvider     _services;
    private readonly Dictionary<int, RemoteReferenceData?> _cache = new();
    private readonly Dictionary<(string From, string To, DateTime Day), ExchangeRateQuote?> _rates = new();

    public CurrencyRules(IReferenceDataReader reference, IServiceProvider services)
    {
        _reference = reference;
        _services  = services;
    }

    /// <summary>Trimmed and upper-cased; null when blank.</summary>
    public static string? Normalize(string? currency) =>
        string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();

    /// <summary>A rate as QuickBooks is sent it.</summary>
    public static decimal ForQuickBooks(decimal rate) => decimal.Round(rate, RateDecimals, MidpointRounding.AwayFromZero);

    public static string Format(decimal rate) => rate.ToString("0.########", CultureInfo.InvariantCulture);

    /// <summary>
    /// The document's own exchange rate (the caller's snapshot, plan S-5) as QuickBooks is sent it — when it
    /// converts into QuickBooks' home currency and is still a rate at eight places. Null otherwise; the rate table
    /// then decides. Preferred over the table so QuickBooks books the document at the rate SMS booked it, even if
    /// SMS's rates are corrected afterwards.
    /// </summary>
    public static decimal? DocumentRateToHome(decimal? rate, string? rateCurrency, string homeCurrency) =>
        rate is { } r && Normalize(rateCurrency) == Normalize(homeCurrency) && ForQuickBooks(r) > 0 ? ForQuickBooks(r) : null;

    public async Task<CurrencyFacts> FactsAsync(IntegrationConnection connection, CancellationToken ct = default)
    {
        var home = Normalize(connection.HomeCurrencyCode);
        var multi = connection.MultiCurrencyEnabled;

        // Not captured on the connection yet: fall back to the cached preferences, if any.
        if (home is null || multi is null)
        {
            var preferences = (await ReferenceAsync(connection, ct))?.Preferences;
            home  ??= Normalize(preferences?.HomeCurrencyCode);
            multi ??= preferences?.MultiCurrencyEnabled;
        }

        return new CurrencyFacts(home, multi == true);
    }

    public async Task<IReadOnlyCollection<string>?> ActiveCurrenciesAsync(IntegrationConnection connection, CancellationToken ct = default)
    {
        var data = await ReferenceAsync(connection, ct);
        if (data is null) return null;

        // The provider stores only active CompanyCurrency rows (and none at all while multicurrency is off).
        return data.Currencies
            .Select(c => Normalize(c.Code))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<ExchangeRateQuote?> RateToHomeAsync(string currency, string homeCurrency, DateTime asOf, CancellationToken ct = default)
    {
        var provider = _services.GetService<IExchangeRateProvider>();
        if (provider is null) return null;

        // Asked once per scope: validation and the build of one record ask the same question, and a pass that
        // re-validates a batch of Blocked documents asks it for many documents of one currency and date.
        var key = (currency.Trim().ToUpperInvariant(), homeCurrency.Trim().ToUpperInvariant(), asOf.Date);
        if (_rates.TryGetValue(key, out var known)) return known;

        var quote  = await provider.GetRateAsync(key.Item1, key.Item2, key.Item3, ct);
        var usable = quote is { Rate: > 0 } && ForQuickBooks(quote.Rate) > 0 ? quote : null;
        _rates[key] = usable;
        return usable;
    }

    private async Task<RemoteReferenceData?> ReferenceAsync(IntegrationConnection connection, CancellationToken ct)
    {
        if (_cache.TryGetValue(connection.Id, out var cached)) return cached;
        var data = await _reference.GetCachedAsync(connection.Id, ct);
        _cache[connection.Id] = data;
        return data;
    }
}
