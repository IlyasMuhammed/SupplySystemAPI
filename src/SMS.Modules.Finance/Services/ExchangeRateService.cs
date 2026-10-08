using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

using static SMS.Modules.Finance.Services.FinanceSetupFormat;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A35-E-02 (D-17) — the legacy <c>api/finance/exchange-rates</c> reads, now served from <c>finance.currency_rates</c>.
/// The legacy table <c>finance.exchange_rates</c> is frozen (converted once by <see cref="CurrencyBootstrapper"/>); its
/// writes were removed when the new Exchange Rates page shipped. Limited to the caller's organization explicitly.
/// </summary>
internal sealed class ExchangeRateService : IExchangeRateService
{
    private readonly FinanceDbContext      _db;
    private readonly IExchangeRateProvider _provider;
    private readonly TimeProvider          _clock;

    // The lookups parameter is kept so existing wiring compiles; codes now come from the organization's currencies.
    public ExchangeRateService(
        FinanceDbContext db, ILookupsService lookups, IExchangeRateProvider provider, TimeProvider? clock = null)
    {
        _db       = db;
        _provider = provider;
        _clock    = clock ?? TimeProvider.System;
    }

    private Guid     Org => _db.TenantContext.OrganizationId;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<ExchangeRateModel>> ListAsync(string? from, string? to, CancellationToken ct = default)
    {
        var org        = Org;
        var fromFilter = ExchangeRateProvider.Normalize(from);
        var toFilter   = ExchangeRateProvider.Normalize(to);

        var rc = await new CurrencyRateReader(_db, null, org).RateCurrencyIdAsync(ct);
        if (rc is null) return [];
        var rcCode = await _db.CurrencyRates.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.CurrencyId == rc)
            .Select(r => r.CurrencyCode).FirstOrDefaultAsync(ct) ?? string.Empty;

        if (toFilter is not null && toFilter != rcCode) return [];

        var q = _db.CurrencyRates.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.CurrencyId != rc && r.Source != CurrencyConventions.SourceSystem);
        if (fromFilter is not null) q = q.Where(r => r.CurrencyCode == fromFilter);

        var rows = await q.OrderByDescending(r => r.EffectiveFrom).ThenBy(r => r.CurrencyCode).ToListAsync(ct);
        return rows.Select(r => new ExchangeRateModel
        {
            Uuid             = r.Uuid,
            FromCurrencyCode = r.CurrencyCode,
            ToCurrencyCode   = rcCode,
            Rate             = r.Rate,
            EffectiveDate    = Date(r.EffectiveFrom.ToDateTime(TimeOnly.MinValue)),
            Source           = r.Source,
            Notes            = r.Notes,
            CreatedDate      = r.CreatedDate
        }).ToList();
    }

    public async Task<ExchangeRateQuoteModel?> QuoteAsync(string? from, string? to, string? date, CancellationToken ct = default)
    {
        var fromCode = ExchangeRateProvider.Normalize(from)
                       ?? throw new BadRequestException("Say which currency to convert from (from=USD).");
        var toCode   = ExchangeRateProvider.Normalize(to)
                       ?? throw new BadRequestException("Say which currency to convert to (to=PKR).");
        var asOf     = ParseDate(date, "The date") ?? Now.Date;

        var quote = await _provider.GetRateAsync(fromCode, toCode, asOf, ct);
        return quote is null
            ? null
            : new ExchangeRateQuoteModel
            {
                FromCurrencyCode = quote.FromCurrencyCode,
                ToCurrencyCode   = quote.ToCurrencyCode,
                Rate             = quote.Rate,
                EffectiveDate    = Date(quote.EffectiveDate),
                Inverted         = quote.Inverted
            };
    }
}
