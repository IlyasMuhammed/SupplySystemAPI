using SMS.Modules.Finance.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A46 C9 / E-4 — <see cref="ICurrencyConverter"/> over <c>finance.currency_rates</c> (A35 storage, E-3: no new table).
/// Same currency → short circuit (no rate lookup). Otherwise from→to = rate(from) / rate(to) on the day, where every stored rate
/// is "units of the organization's base (rate) currency per 1 X". So base→foreign uses the foreign row's inverse,
/// foreign→base its rate, and foreign→foreign triangulates through the base currency. The latest row covering the date is
/// used (so a rate from three days ago serves a later date until superseded). Limited to the caller's organization explicitly.
/// A missing rate or currency is a result (<c>Success=false</c> + message), never an exception.
/// </summary>
internal sealed class CurrencyConverter : ICurrencyConverter
{
    private readonly FinanceDbContext              _db;
    private readonly IOrganizationCurrencyService? _settings;

    public CurrencyConverter(FinanceDbContext db, IOrganizationCurrencyService? settings = null)
    {
        _db       = db;
        _settings = settings;
    }

    public async Task<CurrencyConvertResult> ConvertAsync(CurrencyConvertRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var day = request.EffectiveDate.Date;
        var r   = await ResolveAsync(request.FromCurrencyId, request.FromCurrencyCode, request.ToCurrencyId, request.ToCurrencyCode, day, ct);

        if (r.Error is not null)
            return new CurrencyConvertResult(false, 0m, 0m, day, r.FromCode, r.ToCode, r.Error);

        var converted = r.Same
            ? request.Amount
            : CurrencyConventions.RoundAmount(request.Amount * r.Rate, request.DecimalPlaces);
        return new CurrencyConvertResult(true, converted, r.Rate, r.RateDate, r.FromCode, r.ToCode, null);
    }

    public async Task<CurrencyRateLookup> GetRateAsync(
        Guid? fromCurrencyId, string? fromCurrencyCode, Guid? toCurrencyId, string? toCurrencyCode,
        DateTime effectiveDate, CancellationToken ct = default)
    {
        var day = effectiveDate.Date;
        var r   = await ResolveAsync(fromCurrencyId, fromCurrencyCode, toCurrencyId, toCurrencyCode, day, ct);
        return r.Error is not null
            ? new CurrencyRateLookup(false, 0m, day, r.FromCode, r.ToCode, r.Error)
            : new CurrencyRateLookup(true, r.Rate, r.RateDate, r.FromCode, r.ToCode, null);
    }

    private sealed record Resolved(string FromCode, string ToCode, bool Same, decimal Rate, DateTime RateDate, string? Error);

    private async Task<Resolved> ResolveAsync(
        Guid? fromId, string? fromCode, Guid? toId, string? toCode, DateTime day, CancellationToken ct)
    {
        var org    = _db.TenantContext.OrganizationId;
        var reader = new CurrencyRateReader(_db, _settings, org);

        var from = await IdentifyAsync(reader, fromId, fromCode, "from", ct);
        var to   = await IdentifyAsync(reader, toId, toCode, "to", ct);
        var fc   = from.Code ?? string.Empty;
        var tc   = to.Code ?? string.Empty;
        if (from.Error is not null) return new Resolved(fc, tc, false, 0m, day, from.Error);
        if (to.Error   is not null) return new Resolved(fc, tc, false, 0m, day, to.Error);

        if (from.Id == to.Id)
            return new Resolved(fc, tc, true, 1m, day, null);

        var date = DateOnly.FromDateTime(day);
        var f = await reader.FindAsync(from.Id, date, ct);
        if (f is null)
            return new Resolved(fc, tc, false, 0m, day, CurrencyRateNotFoundException.BuildMessage(fc, from.Id, date));
        var t = await reader.FindAsync(to.Id, date, ct);
        if (t is null)
            return new Resolved(fc, tc, false, 0m, day, CurrencyRateNotFoundException.BuildMessage(tc, to.Id, date));

        var rate = CurrencyConventions.RoundRate(f.Rate / t.Rate);
        if (rate <= 0m)
            return new Resolved(fc, tc, false, 0m, day, $"The exchange rate {fc} to {tc} on {date:yyyy-MM-dd} is too small to use.");

        var eff = f.EffectiveFrom > t.EffectiveFrom ? f.EffectiveFrom : t.EffectiveFrom;
        return new Resolved(fc, tc, false, rate, eff.ToDateTime(TimeOnly.MinValue), null);
    }

    private static async Task<(Guid Id, string? Code, string? Error)> IdentifyAsync(
        CurrencyRateReader reader, Guid? id, string? code, string side, CancellationToken ct)
    {
        if (id is Guid g && g != Guid.Empty)
        {
            var c = await reader.CurrencyAsync(g, ct);
            if (c is null)
            {
                var named = ExchangeRateProvider.Normalize(code);
                return (g, named ?? g.ToString(), $"Currency {named ?? g.ToString()} is not a currency of this organization.");
            }
            return (g, c.Code, null);
        }

        var norm = ExchangeRateProvider.Normalize(code);
        if (norm is null)
            return (Guid.Empty, null, $"Say which currency to convert {side} (a currency id or code).");
        var row = await reader.CurrencyByCodeAsync(norm, ct);
        return row is null
            ? (Guid.Empty, norm, $"Currency {norm} is not a currency of this organization.")
            : (row.CurrencyId, row.Code, null);
    }
}
