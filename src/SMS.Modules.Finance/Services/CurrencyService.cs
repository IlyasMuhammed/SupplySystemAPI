using SMS.Modules.Finance.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A35 C6 — SMS.Shared's <see cref="ICurrencyService"/> over <c>finance.currency_rates</c> (D-2, D-4, D-6, D-12, D-13).
/// The parameterless-organization overloads use the caller's tenant; the explicit ones never read another organization's
/// rows. Domain bases come from Tenancy (<see cref="IOrganizationCurrencyService"/>).
/// </summary>
internal sealed class CurrencyService : ICurrencyService
{
    private readonly FinanceDbContext              _db;
    private readonly IOrganizationCurrencyService? _settings;

    public CurrencyService(FinanceDbContext db, IOrganizationCurrencyService? settings = null)
    {
        _db       = db;
        _settings = settings;
    }

    private Guid Tenant => _db.TenantContext.OrganizationId;
    private CurrencyRateReader Reader(Guid org) => new(_db, _settings, org);

    // ── Rates ────────────────────────────────────────────────────────────────

    public Task<CurrencyRateInfo> GetRateAsync(Guid currencyId, DateOnly date, CancellationToken ct = default) =>
        GetRateAsync(Tenant, currencyId, date, ct);

    public Task<CurrencyRateInfo> GetRateAsync(Guid organizationId, Guid currencyId, DateOnly date, CancellationToken ct = default) =>
        Reader(organizationId).RequireAsync(currencyId, date, ct);

    public Task<IReadOnlyList<CurrencyRateInfo>> GetActiveRatesAsync(CancellationToken ct = default) =>
        GetActiveRatesAsync(Tenant, ct);

    public Task<IReadOnlyList<CurrencyRateInfo>> GetActiveRatesAsync(Guid organizationId, CancellationToken ct = default) =>
        Reader(organizationId).ActiveAsync(ct);

    // ── Conversion ───────────────────────────────────────────────────────────

    public Task<CurrencyConversionResult> ConvertAsync(decimal amount, Guid fromCurrencyId, Guid toCurrencyId, DateOnly date, CancellationToken ct = default) =>
        ConvertAsync(Tenant, amount, fromCurrencyId, toCurrencyId, date, ct);

    public async Task<CurrencyConversionResult> ConvertAsync(
        Guid organizationId, decimal amount, Guid fromCurrencyId, Guid toCurrencyId, DateOnly date, CancellationToken ct = default)
    {
        var reader = Reader(organizationId);
        var (rate, _, _) = await reader.CrossAsync(fromCurrencyId, toCurrencyId, date, ct);
        var decimals = await reader.DecimalsAsync(toCurrencyId, ct);
        var fromCode = await reader.CodeAsync(fromCurrencyId, ct);
        var toCode   = fromCurrencyId == toCurrencyId ? fromCode : await reader.CodeAsync(toCurrencyId, ct);

        return new CurrencyConversionResult(
            amount, fromCurrencyId, fromCode,
            CurrencyConventions.RoundAmount(amount * rate, decimals),
            toCurrencyId, toCode, rate, date);
    }

    public Task<CurrencyConversionResult> ToBaseCurrencyAsync(decimal amount, Guid sourceCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        ToBaseCurrencyAsync(Tenant, amount, sourceCurrencyId, date, domain, ct);

    public async Task<CurrencyConversionResult> ToBaseCurrencyAsync(
        Guid organizationId, decimal amount, Guid sourceCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        await ConvertAsync(organizationId, amount, sourceCurrencyId, await GetBaseCurrencyIdAsync(organizationId, domain, ct), date, ct);

    public Task<CurrencyConversionResult> FromBaseCurrencyAsync(decimal amount, Guid targetCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        FromBaseCurrencyAsync(Tenant, amount, targetCurrencyId, date, domain, ct);

    public async Task<CurrencyConversionResult> FromBaseCurrencyAsync(
        Guid organizationId, decimal amount, Guid targetCurrencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        await ConvertAsync(organizationId, amount, await GetBaseCurrencyIdAsync(organizationId, domain, ct), targetCurrencyId, date, ct);

    // ── Locking ──────────────────────────────────────────────────────────────

    public Task<DocumentRateLock> LockRateAsync(Guid currencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default) =>
        LockRateAsync(Tenant, currencyId, date, domain, ct);

    public async Task<DocumentRateLock> LockRateAsync(
        Guid organizationId, Guid currencyId, DateOnly date, TransactionDomain domain, CancellationToken ct = default)
    {
        var reader   = Reader(organizationId);
        var baseId   = await GetBaseCurrencyIdAsync(organizationId, domain, ct);
        var same     = baseId == currencyId;
        var (rate, _, _) = await reader.CrossAsync(currencyId, baseId, date, ct);

        return new DocumentRateLock(
            currencyId, await reader.CodeAsync(currencyId, ct),
            baseId, await reader.CodeAsync(baseId, ct),
            domain, same ? 1m : rate, date, same,
            await reader.DecimalsAsync(currencyId, ct),
            await reader.DecimalsAsync(baseId, ct));
    }

    // ── Bases ────────────────────────────────────────────────────────────────

    public Task<Guid> GetBaseCurrencyIdAsync(TransactionDomain domain, CancellationToken ct = default) =>
        GetBaseCurrencyIdAsync(Tenant, domain, ct);

    public async Task<Guid> GetBaseCurrencyIdAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default)
    {
        Guid? id = null;
        if (_settings is not null)
            id = await _settings.GetBaseCurrencyIdAsync(organizationId, domain, ct);
        id ??= await Reader(organizationId).RateCurrencyIdAsync(ct);

        return id is Guid g && g != Guid.Empty
            ? g
            : throw new BadRequestException(
                $"No {CurrencyConventions.DomainCode(domain).ToLowerInvariant()} base currency is configured for this organization. "
              + "Set it under Settings → Currency Configuration.");
    }
}
