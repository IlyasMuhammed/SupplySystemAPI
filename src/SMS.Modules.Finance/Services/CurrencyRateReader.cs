using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A35 D-2 — the read side of <c>finance.currency_rates</c> for ONE organization, given explicitly (never the tenant filter:
/// it is off for super admins and in Hangfire). Every rate is "units of the rate currency per 1 X"; the rate currency is the
/// currency of the organization's SYSTEM row, else Tenancy's <see cref="OrgCurrencySettingsSnapshot.RateCurrencyId"/>.
/// </summary>
internal sealed class CurrencyRateReader
{
    private readonly FinanceDbContext              _db;
    private readonly IOrganizationCurrencyService? _settings;
    private readonly Guid                          _org;
    private Guid?                                  _rateCurrency;
    private bool                                   _rateCurrencyResolved;
    private readonly Dictionary<Guid, OrgCurrency?> _currencies = new();

    public CurrencyRateReader(FinanceDbContext db, IOrganizationCurrencyService? settings, Guid org)
    {
        _db       = db;
        _settings = settings;
        _org      = org;
    }

    public Guid Organization => _org;

    private IQueryable<CurrencyRate> Rates =>
        _db.CurrencyRates.IgnoreQueryFilters().AsNoTracking().Where(r => r.OrganizationId == _org);

    /// <summary>The organization's rate currency, or null when none can be resolved.</summary>
    public async Task<Guid?> RateCurrencyIdAsync(CancellationToken ct = default)
    {
        if (_rateCurrencyResolved) return _rateCurrency;

        var system = await Rates.Where(r => r.Source == CurrencyConventions.SourceSystem)
            .OrderBy(r => r.Id).Select(r => (Guid?)r.CurrencyId).FirstOrDefaultAsync(ct);

        if (system is null && _settings is not null)
        {
            var s = await _settings.GetSettingsAsync(_org, ct);
            if (s.RateCurrencyId != Guid.Empty) system = s.RateCurrencyId;
        }

        _rateCurrency         = system;
        _rateCurrencyResolved = true;
        return system;
    }

    public async Task<OrgCurrency?> CurrencyAsync(Guid currencyId, CancellationToken ct = default)
    {
        if (_currencies.TryGetValue(currencyId, out var known)) return known;
        var row = await _db.OrgCurrencies.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrganizationId == _org && c.CurrencyId == currencyId, ct);
        _currencies[currencyId] = row;
        return row;
    }

    public async Task<OrgCurrency?> CurrencyByCodeAsync(string code, CancellationToken ct = default)
    {
        var upper = code.Trim().ToUpperInvariant();
        var row = await _db.OrgCurrencies.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrganizationId == _org && c.Code == upper, ct);
        if (row is not null) _currencies[row.CurrencyId] = row;
        return row;
    }

    public async Task<string> CodeAsync(Guid currencyId, CancellationToken ct = default)
    {
        var c = await CurrencyAsync(currencyId, ct);
        if (c is not null) return c.Code;
        var fromRate = await Rates.Where(r => r.CurrencyId == currencyId).Select(r => r.CurrencyCode).FirstOrDefaultAsync(ct);
        return fromRate ?? currencyId.ToString();
    }

    public async Task<int> DecimalsAsync(Guid currencyId, CancellationToken ct = default) =>
        (await CurrencyAsync(currencyId, ct))?.DecimalPlaces ?? CurrencyConventions.DefaultDecimalPlaces;

    /// <summary>The row covering <paramref name="date"/>, or null. The rate currency always answers (stored or synthesised 1.0).</summary>
    public async Task<CurrencyRateInfo?> FindAsync(Guid currencyId, DateOnly date, CancellationToken ct = default)
    {
        var rc = await RateCurrencyIdAsync(ct);
        if (rc == currencyId)
        {
            var sys = await Rates.Where(r => r.CurrencyId == currencyId && r.Source == CurrencyConventions.SourceSystem)
                .OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
            return sys is not null
                ? ToInfo(sys)
                : new CurrencyRateInfo(Guid.Empty, currencyId, await CodeAsync(currencyId, ct), 1m, 1m,
                    CurrencyConventions.SystemStart, CurrencyConventions.OpenEnd, CurrencyConventions.SourceSystem, null);
        }
        if (rc is null) return null;

        var row = await Rates
            .Where(r => r.CurrencyId == currencyId && r.EffectiveFrom <= date && r.EffectiveTo >= date)
            .OrderByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
        return row is null ? null : ToInfo(row);
    }

    public async Task<CurrencyRateInfo> RequireAsync(Guid currencyId, DateOnly date, CancellationToken ct = default) =>
        await FindAsync(currencyId, date, ct)
        ?? throw new CurrencyRateNotFoundException(currencyId, await CodeAsync(currencyId, ct), date);

    /// <summary>Units of <paramref name="to"/> per 1 <paramref name="from"/> on <paramref name="date"/> (10dp), with both rows; 1 for the same currency.</summary>
    public async Task<(decimal Rate, CurrencyRateInfo? From, CurrencyRateInfo? To)> CrossAsync(
        Guid from, Guid to, DateOnly date, CancellationToken ct = default)
    {
        if (from == to) return (1m, null, null);
        var f = await RequireAsync(from, date, ct);
        var t = await RequireAsync(to, date, ct);
        return (CurrencyConventions.RoundRate(f.Rate / t.Rate), f, t);
    }

    public async Task<IReadOnlyList<CurrencyRateInfo>> ActiveAsync(CancellationToken ct = default)
    {
        var open = CurrencyConventions.OpenEnd;
        var rows = await Rates.Where(r => r.EffectiveTo == open).OrderBy(r => r.CurrencyCode).ToListAsync(ct);
        var list = rows.Select(ToInfo).ToList();

        var rc = await RateCurrencyIdAsync(ct);
        if (rc is Guid id && list.All(r => r.CurrencyId != id))
            list.Insert(0, (await FindAsync(id, DateOnly.FromDateTime(DateTime.UtcNow), ct))!);
        return list;
    }

    internal static CurrencyRateInfo ToInfo(CurrencyRate r) =>
        new(r.Uuid, r.CurrencyId, r.CurrencyCode, r.Rate, r.InverseRate, r.EffectiveFrom, r.EffectiveTo, r.Source, r.Notes);
}
