using SMS.Shared.Common;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A35 — Finance's currency service as Demand sees it: rates per (currency, from-date) in units of the base per 1 unit,
/// bases per domain, decimals per currency. Records every lock asked for. Only what Demand calls is implemented.
/// </summary>
internal sealed class FakeCurrencyService : ICurrencyService
{
    public Dictionary<TransactionDomain, Guid> Bases { get; } = [];
    public Dictionary<Guid, string> Codes { get; } = [];
    public Dictionary<Guid, int> Decimals { get; } = [];
    /// <summary>(currency, effective from) → rate vs the base. The latest from-date on or before the asked date wins.</summary>
    public List<(Guid Currency, DateOnly From, decimal Rate)> Rates { get; } = [];
    public List<(Guid Org, Guid Currency, DateOnly Date, TransactionDomain Domain)> Locks { get; } = [];

    public void Rate(Guid currency, decimal rate, DateOnly? from = null) => Rates.Add((currency, from ?? new DateOnly(2000, 1, 1), rate));

    public Task<DocumentRateLock> LockRateAsync(Guid organizationId, Guid currencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default)
    {
        Locks.Add((organizationId, currencyId, date, domain));
        var baseId = Bases[domain];
        var dec = Decimals.GetValueOrDefault(currencyId, 2);
        var baseDec = Decimals.GetValueOrDefault(baseId, 2);
        if (currencyId == baseId)
            return Task.FromResult(new DocumentRateLock(currencyId, Code(currencyId), baseId, Code(baseId), domain, 1m, date, true, dec, baseDec));

        var hit = Rates.Where(r => r.Currency == currencyId && r.From <= date).OrderByDescending(r => r.From).Select(r => (decimal?)r.Rate).FirstOrDefault();
        if (hit is not { } rate)
            throw new CurrencyRateNotFoundException(currencyId, Code(currencyId), date);
        return Task.FromResult(new DocumentRateLock(currencyId, Code(currencyId), baseId, Code(baseId), domain, rate, date, false, dec, baseDec));
    }

    public Task<DocumentRateLock> LockRateAsync(Guid currencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        throw new InvalidOperationException("Demand must use the explicit-organization overload.");

    public Task<Guid> GetBaseCurrencyIdAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
        Task.FromResult(Bases[domain]);

    public Task<Guid> GetBaseCurrencyIdAsync(TransactionDomain domain, CancellationToken ct = default) => Task.FromResult(Bases[domain]);

    private string Code(Guid id) => Codes.GetValueOrDefault(id, id.ToString());

    public Task<CurrencyRateInfo> GetRateAsync(Guid currencyId, DateOnly date, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CurrencyRateInfo> GetRateAsync(Guid organizationId, Guid currencyId, DateOnly date, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CurrencyConversionResult> ConvertAsync(decimal amount, Guid fromCurrencyId, Guid toCurrencyId, DateOnly date, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CurrencyConversionResult> ConvertAsync(Guid organizationId, decimal amount, Guid fromCurrencyId, Guid toCurrencyId, DateOnly date, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CurrencyConversionResult> ToBaseCurrencyAsync(decimal amount, Guid sourceCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CurrencyConversionResult> ToBaseCurrencyAsync(Guid organizationId, decimal amount, Guid sourceCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CurrencyConversionResult> FromBaseCurrencyAsync(decimal amount, Guid targetCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<CurrencyConversionResult> FromBaseCurrencyAsync(Guid organizationId, decimal amount, Guid targetCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<CurrencyRateInfo>> GetActiveRatesAsync(CancellationToken ct = default) => throw new NotImplementedException();
    public Task<IReadOnlyList<CurrencyRateInfo>> GetActiveRatesAsync(Guid organizationId, CancellationToken ct = default) => throw new NotImplementedException();
}

/// <summary>A35 — Suppliers' partner currency defaults: partner → (sale, purchase) default, else the given bases.</summary>
internal sealed class FakePartnerCurrencyDefaults(Dictionary<TransactionDomain, Guid> bases) : IPartnerCurrencyDefaults
{
    public Dictionary<Guid, (Guid? Sale, Guid? Purchase)> Partners { get; } = [];

    public Task<PartnerCurrencyDefaults?> GetAsync(Guid organizationId, Guid partnerId, CancellationToken ct = default) =>
        Task.FromResult(Partners.TryGetValue(partnerId, out var p) ? new PartnerCurrencyDefaults(partnerId, p.Sale, p.Purchase) : null);

    public Task<Guid> ResolveDefaultCurrencyAsync(Guid organizationId, Guid partnerId, TransactionDomain domain, CancellationToken ct = default)
    {
        Partners.TryGetValue(partnerId, out var p);
        var own = domain switch { TransactionDomain.Sale => p.Sale, TransactionDomain.Purchase => p.Purchase, _ => null };
        return Task.FromResult(own ?? bases[domain]);
    }
}

/// <summary>A35 — Finance's org currency lookup: the listed currencies are configured, with their active flag.</summary>
internal sealed class FakeOrgCurrencies : IOrgCurrencyLookup
{
    public Dictionary<Guid, OrgCurrencyInfo> Currencies { get; } = [];

    public void Add(Guid id, string code, int decimals = 2, bool active = true) =>
        Currencies[id] = new OrgCurrencyInfo(id, code, code, code, decimals, 0m, CurrencyConventions.SymbolBefore, active, 0);

    public Task<OrgCurrencyInfo?> GetAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
        Task.FromResult(Currencies.GetValueOrDefault(currencyId));
    public Task<OrgCurrencyInfo?> GetByCodeAsync(Guid organizationId, string code, CancellationToken ct = default) =>
        Task.FromResult(Currencies.Values.FirstOrDefault(c => c.Code == code));
    public Task<IReadOnlyList<OrgCurrencyInfo>> ListAsync(Guid organizationId, bool activeOnly, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OrgCurrencyInfo>>([.. Currencies.Values.Where(c => !activeOnly || c.IsActive)]);
    public Task<int> GetDecimalPlacesAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
        Task.FromResult(Currencies.TryGetValue(currencyId, out var c) ? c.DecimalPlaces : 2);
}
