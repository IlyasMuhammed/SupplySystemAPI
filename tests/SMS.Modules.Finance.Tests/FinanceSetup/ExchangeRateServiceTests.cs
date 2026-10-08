using FluentAssertions;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.MultiCurrency;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Finance.Tests.MultiCurrency.CurrencyWorld;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>
/// A35-E-02 (D-17) — the legacy exchange-rate reads, served from finance.currency_rates: every range appears as
/// "X → rate currency" dated by its start; the quote is IExchangeRateProvider's answer. Writes are gone (api/currency-rates).
/// </summary>
public class ExchangeRateServiceTests
{
    private readonly CurrencyWorld _w = new();

    private async Task<T> Svc<T>(Guid org, Func<ExchangeRateService, Task<T>> act, bool superAdmin = false)
    {
        await using var db = _w.Db(org, superAdmin);
        return await act(new ExchangeRateService(db, _w.Lookups().Object, new ExchangeRateProvider(db, _w.Settings), _w.Clock));
    }

    [Fact]
    public async Task Ranges_are_listed_as_pairs_against_the_rate_currency_newest_first_and_filtered()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 277m, "2026-09-01", "2026-09-30");
        await _w.SeedRateAsync(org, Usd, 278m, "2026-10-01");
        await _w.SeedRateAsync(org, Eur, 300m, "2026-09-15");

        var all = await Svc(org, s => s.ListAsync(null, null));
        all.Select(r => (r.FromCurrencyCode, r.ToCurrencyCode, r.Rate, r.EffectiveDate)).Should().Equal(
            ("USD", "PKR", 278m, "2026-10-01"), ("EUR", "PKR", 300m, "2026-09-15"), ("USD", "PKR", 277m, "2026-09-01"));

        (await Svc(org, s => s.ListAsync("usd", null))).Should().HaveCount(2);
        (await Svc(org, s => s.ListAsync(null, "pkr"))).Should().HaveCount(3);
        (await Svc(org, s => s.ListAsync(null, "USD"))).Should().BeEmpty("rates are only stored against the rate currency");
    }

    [Fact]
    public async Task The_quote_is_the_providers_answer_and_blank_codes_are_400()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 278m, "2026-10-01");
        await _w.SeedRateAsync(org, Eur, 300m, "2026-10-01");

        var q = await Svc(org, s => s.QuoteAsync("eur", "usd", "2026-10-02"));
        q!.Rate.Should().Be(Math.Round(300m / 278m, 10, MidpointRounding.AwayFromZero));
        q.EffectiveDate.Should().Be("2026-10-01");
        (await Svc(org, s => s.QuoteAsync("USD", "PKR", "2026-09-30"))).Should().BeNull();

        var act = () => Svc(org, s => s.QuoteAsync(null, "PKR", null));
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Another_organizations_ranges_are_never_listed_even_for_a_super_admin()
    {
        var acme   = await _w.NewOrgAsync();
        var globex = await _w.NewOrgAsync();
        await _w.SeedRateAsync(globex, Usd, 280m, "2026-10-01");

        (await Svc(acme, s => s.ListAsync(null, null), superAdmin: true)).Should().BeEmpty();
    }
}
