using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// SMS.Shared's <see cref="IExchangeRateProvider"/> over Finance's own rate table (S-4): the latest live rate
/// for the pair dated on or before the day asked; else the reciprocal of the opposite pair's latest
/// (<c>Inverted</c>, rounded to the eight decimals a stored rate has); same currency → 1; nothing → null.
/// No triangulation.
/// <para>
/// Limited to the current organization explicitly as well as through the tenant filter, so a super admin
/// (who bypasses the filter) and a Hangfire job (whose organization HangfireTenantScope restores) both get
/// their own organization's rates and never another's.
/// </para>
/// </summary>
internal sealed class ExchangeRateProvider : IExchangeRateProvider
{
    private const int RateDecimals = 8;

    private readonly FinanceDbContext _db;

    public ExchangeRateProvider(FinanceDbContext db) => _db = db;

    public async Task<ExchangeRateQuote?> GetRateAsync(
        string fromCurrencyCode, string toCurrencyCode, DateTime asOf, CancellationToken ct = default)
    {
        var from = Normalize(fromCurrencyCode);
        var to   = Normalize(toCurrencyCode);
        if (from is null || to is null) return null;

        var day = asOf.Date;
        if (from == to) return new ExchangeRateQuote(from, to, 1m, day, Inverted: false);

        var direct = await LatestAsync(from, to, day, ct);
        if (direct is not null)
            return new ExchangeRateQuote(from, to, direct.Rate, direct.EffectiveDate, Inverted: false);

        var opposite = await LatestAsync(to, from, day, ct);
        if (opposite is null || opposite.Rate <= 0m) return null;

        var reciprocal = Math.Round(1m / opposite.Rate, RateDecimals, MidpointRounding.AwayFromZero);
        // A rate so large that its reciprocal rounds to nothing at eight decimals cannot convert anything.
        if (reciprocal <= 0m) return null;

        return new ExchangeRateQuote(from, to, reciprocal, opposite.EffectiveDate, Inverted: true);
    }

    /// <summary>Codes are stored upper-case; callers may send any case and stray spaces.</summary>
    internal static string? Normalize(string? code)
    {
        var trimmed = code?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    private sealed record StoredRate(decimal Rate, DateTime EffectiveDate);

    private Task<StoredRate?> LatestAsync(string from, string to, DateTime day, CancellationToken ct)
    {
        var org = _db.TenantContext.OrganizationId;

        return _db.ExchangeRates.AsNoTracking()
            .Where(r => r.OrganizationId == org
                     && !r.IsDelete
                     && r.FromCurrencyCode == from
                     && r.ToCurrencyCode == to
                     && r.EffectiveDate <= day)
            .OrderByDescending(r => r.EffectiveDate)
            .Select(r => new StoredRate(r.Rate, r.EffectiveDate))
            .FirstOrDefaultAsync(ct);
    }
}
