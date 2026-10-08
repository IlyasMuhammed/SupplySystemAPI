using SMS.Shared.Common;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A35 — an <see cref="ICurrencyService"/> for FIN's unit tests: one rate currency, rates per (currency, date) set by the
/// test (latest on or before the date), domain bases set by the test. Lock = rate(X)/rate(base) as CUR's service does
/// (D-2), rounded to 10 places; same currency → 1, no lookup. A missing rate throws the contract's 400.
/// </summary>
internal sealed class FakeCurrencyService : ICurrencyService
{
    public Dictionary<Guid, string> Codes { get; } = [];
    public Dictionary<Guid, int> Decimals { get; } = [];
    public Guid RateCurrency { get; set; }
    public Dictionary<TransactionDomain, Guid> Bases { get; } = [];
    public List<(Guid Currency, DateOnly From, decimal Rate)> Rates { get; } = [];
    public List<(Guid Org, Guid Currency, DateOnly Date, TransactionDomain Domain)> Locks { get; } = [];

    public FakeCurrencyService(Guid rateCurrency, string code)
    {
        RateCurrency = rateCurrency;
        Codes[rateCurrency] = code;
        foreach (var d in Enum.GetValues<TransactionDomain>()) Bases[d] = rateCurrency;
    }

    public FakeCurrencyService Currency(Guid id, string code, int decimals = 2) { Codes[id] = code; Decimals[id] = decimals; return this; }
    public FakeCurrencyService Rate(Guid currency, decimal rate, DateOnly? from = null) { Rates.Add((currency, from ?? new DateOnly(2000, 1, 1), rate)); return this; }

    private decimal? RateOf(Guid currency, DateOnly date) =>
        currency == RateCurrency ? 1m
        : Rates.Where(r => r.Currency == currency && r.From <= date).OrderByDescending(r => r.From).Select(r => (decimal?)r.Rate).FirstOrDefault();

    private decimal Need(Guid currency, DateOnly date) =>
        RateOf(currency, date) ?? throw new CurrencyRateNotFoundException(currency, Codes.GetValueOrDefault(currency), date);

    public Task<DocumentRateLock> LockRateAsync(Guid organizationId, Guid currencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default)
    {
        Locks.Add((organizationId, currencyId, date, domain));
        var baseId = Bases[domain];
        var same   = baseId == currencyId;
        var rate   = same ? 1m : CurrencyConventions.RoundRate(Need(currencyId, date) / Need(baseId, date));
        return Task.FromResult(new DocumentRateLock(currencyId, Codes.GetValueOrDefault(currencyId) ?? "?", baseId, Codes[baseId], domain, rate, date,
            same, Decimals.GetValueOrDefault(currencyId, 2), Decimals.GetValueOrDefault(baseId, 2)));
    }

    public Task<DocumentRateLock> LockRateAsync(Guid currencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        LockRateAsync(Guid.Empty, currencyId, date, domain, ct);

    public Task<Guid> GetBaseCurrencyIdAsync(TransactionDomain domain, CancellationToken ct = default) => Task.FromResult(Bases[domain]);
    public Task<Guid> GetBaseCurrencyIdAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) => Task.FromResult(Bases[domain]);

    public Task<CurrencyRateInfo> GetRateAsync(Guid currencyId, DateOnly date, CancellationToken ct = default) => GetRateAsync(Guid.Empty, currencyId, date, ct);
    public Task<CurrencyRateInfo> GetRateAsync(Guid organizationId, Guid currencyId, DateOnly date, CancellationToken ct = default)
    {
        var r = Need(currencyId, date);
        return Task.FromResult(new CurrencyRateInfo(Guid.NewGuid(), currencyId, Codes.GetValueOrDefault(currencyId) ?? "?", r, 1m / r, date,
            CurrencyConventions.OpenEnd, CurrencyConventions.SourceManual, null));
    }

    public Task<CurrencyConversionResult> ConvertAsync(decimal amount, Guid fromCurrencyId, Guid toCurrencyId, DateOnly date, CancellationToken ct = default) =>
        ConvertAsync(Guid.Empty, amount, fromCurrencyId, toCurrencyId, date, ct);
    public Task<CurrencyConversionResult> ConvertAsync(Guid organizationId, decimal amount, Guid fromCurrencyId, Guid toCurrencyId, DateOnly date, CancellationToken ct = default)
    {
        var rate = fromCurrencyId == toCurrencyId ? 1m : CurrencyConventions.RoundRate(Need(fromCurrencyId, date) / Need(toCurrencyId, date));
        return Task.FromResult(new CurrencyConversionResult(amount, fromCurrencyId, Codes.GetValueOrDefault(fromCurrencyId) ?? "?",
            CurrencyConventions.RoundAmount(amount * rate, Decimals.GetValueOrDefault(toCurrencyId, 2)), toCurrencyId,
            Codes.GetValueOrDefault(toCurrencyId) ?? "?", rate, date));
    }

    public Task<CurrencyConversionResult> ToBaseCurrencyAsync(decimal amount, Guid sourceCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        ConvertAsync(amount, sourceCurrencyId, Bases[domain], date, ct);
    public Task<CurrencyConversionResult> ToBaseCurrencyAsync(Guid organizationId, decimal amount, Guid sourceCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        ConvertAsync(amount, sourceCurrencyId, Bases[domain], date, ct);
    public Task<CurrencyConversionResult> FromBaseCurrencyAsync(decimal amount, Guid targetCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        ConvertAsync(amount, Bases[domain], targetCurrencyId, date, ct);
    public Task<CurrencyConversionResult> FromBaseCurrencyAsync(Guid organizationId, decimal amount, Guid targetCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        ConvertAsync(amount, Bases[domain], targetCurrencyId, date, ct);

    public Task<IReadOnlyList<CurrencyRateInfo>> GetActiveRatesAsync(CancellationToken ct = default) => GetActiveRatesAsync(Guid.Empty, ct);
    public Task<IReadOnlyList<CurrencyRateInfo>> GetActiveRatesAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CurrencyRateInfo>>([]);
}
