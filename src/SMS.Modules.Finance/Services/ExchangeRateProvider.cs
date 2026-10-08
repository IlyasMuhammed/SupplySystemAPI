using SMS.Modules.Finance.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// SMS.Shared's <see cref="IExchangeRateProvider"/> — A35-E-01 (D-4, D-6): same signature and same-currency behaviour as
/// SAP S-4, now over <c>finance.currency_rates</c> with triangulation through the organization's rate currency
/// (from→to = rate(from) / rate(to) on the day, 10dp). Codes are the organization's currency codes (any case). Null when a
/// code is not an organization currency or either side has no rate covering the day. <c>EffectiveDate</c> = the later of
/// the two rows' starts; <c>Inverted</c> is always false.
/// <para>
/// Limited to the current organization explicitly (TenantContext.OrganizationId), so a super admin and a Hangfire job (whose
/// organization HangfireTenantScope restores) both get their own organization's rates and never another's.
/// </para>
/// </summary>
internal sealed class ExchangeRateProvider : IExchangeRateProvider
{
    private readonly FinanceDbContext              _db;
    private readonly IOrganizationCurrencyService? _settings;

    public ExchangeRateProvider(FinanceDbContext db, IOrganizationCurrencyService? settings = null)
    {
        _db       = db;
        _settings = settings;
    }

    public async Task<ExchangeRateQuote?> GetRateAsync(
        string fromCurrencyCode, string toCurrencyCode, DateTime asOf, CancellationToken ct = default)
    {
        var from = Normalize(fromCurrencyCode);
        var to   = Normalize(toCurrencyCode);
        if (from is null || to is null) return null;

        var day = asOf.Date;
        if (from == to) return new ExchangeRateQuote(from, to, 1m, day, Inverted: false);

        var reader = new CurrencyRateReader(_db, _settings, _db.TenantContext.OrganizationId);
        var f = await reader.CurrencyByCodeAsync(from, ct);
        var t = await reader.CurrencyByCodeAsync(to, ct);
        if (f is null || t is null) return null;

        var date     = DateOnly.FromDateTime(day);
        var fromRate = await reader.FindAsync(f.CurrencyId, date, ct);
        var toRate   = await reader.FindAsync(t.CurrencyId, date, ct);
        if (fromRate is null || toRate is null) return null;

        var rate = CurrencyConventions.RoundRate(fromRate.Rate / toRate.Rate);
        if (rate <= 0m) return null;

        var effective = fromRate.EffectiveFrom > toRate.EffectiveFrom ? fromRate.EffectiveFrom : toRate.EffectiveFrom;
        return new ExchangeRateQuote(from, to, rate, effective.ToDateTime(TimeOnly.MinValue), Inverted: false);
    }

    /// <summary>Codes are stored upper-case; callers may send any case and stray spaces.</summary>
    internal static string? Normalize(string? code)
    {
        var trimmed = code?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }
}
