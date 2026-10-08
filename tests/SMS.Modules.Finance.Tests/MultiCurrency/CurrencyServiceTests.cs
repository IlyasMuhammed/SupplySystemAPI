using FluentAssertions;
using SMS.Shared.Common;
using Xunit;
using static SMS.Modules.Finance.Tests.MultiCurrency.CurrencyWorld;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>A35 P1-09 / P3-07 — ICurrencyService: lookup by range, conversion through the rate currency (D-2), locking (T-C2-03..05, T-C6-*).</summary>
public class CurrencyServiceTests
{
    private readonly CurrencyWorld _w = new();
    private static DateOnly D(string s) => DateOnly.Parse(s);

    [Fact]
    public async Task T_C2_03_04_lookup_on_the_exact_day_and_inside_a_range()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 277.50m, "2026-10-01", "2026-10-04");
        await _w.SeedRateAsync(org, Usd, 278.05m, "2026-10-07", "2026-10-07");
        await using var db = _w.Db(org);

        (await _w.Currency(db).GetRateAsync(Usd, D("2026-10-07"))).Rate.Should().Be(278.05m);
        (await _w.Currency(db).GetRateAsync(Usd, D("2026-10-03"))).Rate.Should().Be(277.50m);
    }

    [Fact]
    public async Task T_C2_05_no_rate_throws_the_D5_400_message()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 277.50m, "2026-10-01", "2026-10-04");
        await using var db = _w.Db(org);

        var act = () => _w.Currency(db).GetRateAsync(Usd, D("2026-10-05"));
        (await act.Should().ThrowAsync<CurrencyRateNotFoundException>())
            .WithMessage("No exchange rate for USD on 2026-10-05. Add one under Settings → Exchange Rates.");
    }

    [Fact]
    public async Task The_rate_currency_is_always_one()
    {
        var org = await _w.NewOrgAsync();
        await using var db = _w.Db(org);

        var r = await _w.Currency(db).GetRateAsync(Pkr, D("1999-01-01"));
        r.Rate.Should().Be(1m);
        r.Source.Should().Be("SYSTEM");
    }

    [Fact]
    public async Task T_C6_01_direct_conversion_to_base()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Aed, 76.30m, "2026-10-07");
        await using var db = _w.Db(org);

        var r = await _w.Currency(db).ConvertAsync(1000m, Aed, Pkr, D("2026-10-07"));
        r.ConvertedAmount.Should().Be(76_300.00m);
        r.RateUsed.Should().Be(76.30m);
        r.FromCurrencyCode.Should().Be("AED");
        r.ToCurrencyCode.Should().Be("PKR");
    }

    [Fact]
    public async Task T_C6_02_cross_currency_goes_through_the_rate_currency()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Aed, 76.30m, "2026-10-07");
        await _w.SeedRateAsync(org, Eur, 316.48m, "2026-10-07");
        await using var db = _w.Db(org);

        var r = await _w.Currency(db).ConvertAsync(1000m, Aed, Eur, D("2026-10-07"));
        r.ConvertedAmount.Should().Be(241.09m, "1000 × (76.30 / 316.48 rounded to 10dp) = 241.0894… → 241.09");
        r.RateUsed.Should().Be(Math.Round(76.30m / 316.48m, 10, MidpointRounding.AwayFromZero));
    }

    [Fact]
    public async Task T_C6_03_same_currency_needs_no_rate()
    {
        var org = await _w.NewOrgAsync();
        await using var db = _w.Db(org);

        var r = await _w.Currency(db).ConvertAsync(5000m, Mxn, Mxn, D("2026-10-07"));
        r.ConvertedAmount.Should().Be(5000m);
        r.RateUsed.Should().Be(1m);
    }

    [Fact]
    public async Task T_C6_04_historical_rate_is_used()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 277.50m, "2026-10-01", "2026-10-04");
        await _w.SeedRateAsync(org, Usd, 278.05m, "2026-10-05");
        await using var db = _w.Db(org);

        (await _w.Currency(db).ToBaseCurrencyAsync(100m, Usd, D("2026-10-03"), TransactionDomain.Sale)).ConvertedAmount
            .Should().Be(27_750m);
    }

    [Fact]
    public async Task T_C6_05_base_per_domain()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Pkr, saleBase: Pkr, purchaseBase: Usd);
        await using var db = _w.Db(org);

        (await _w.Currency(db).GetBaseCurrencyIdAsync(TransactionDomain.Sale)).Should().Be(Pkr);
        (await _w.Currency(db).GetBaseCurrencyIdAsync(org, TransactionDomain.Purchase)).Should().Be(Usd);
    }

    [Fact]
    public async Task Purchase_base_reached_through_the_rate_currency_appendix_A()
    {
        // Sale base PKR, purchase base USD, rate currency PKR: a EUR PO converts EUR → PKR → USD.
        var org = await _w.NewOrgAsync(rateCurrency: Pkr, saleBase: Pkr, purchaseBase: Usd);
        await _w.SeedRateAsync(org, Usd, 278m, "2026-10-01");
        await _w.SeedRateAsync(org, Eur, 300m, "2026-10-01");
        await using var db = _w.Db(org);

        var lk = await _w.Currency(db).LockRateAsync(Eur, D("2026-10-07"), TransactionDomain.Purchase);
        lk.BaseCurrencyId.Should().Be(Usd);
        lk.Rate.Should().Be(Math.Round(300m / 278m, 10, MidpointRounding.AwayFromZero));
        lk.SameCurrency.Should().BeFalse();
        lk.ToBase(1000m).Should().Be(1079.14m);
    }

    [Fact]
    public async Task Locking_in_the_base_currency_needs_no_rate_and_is_one()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Pkr, saleBase: Pkr, purchaseBase: Usd);
        await using var db = _w.Db(org);

        var lk = await _w.Currency(db).LockRateAsync(Usd, D("2026-10-07"), TransactionDomain.Purchase);
        lk.Rate.Should().Be(1m);
        lk.SameCurrency.Should().BeTrue();
        lk.ToBase(10.005m).Should().Be(10.01m);
    }

    [Fact]
    public async Task Locking_a_foreign_currency_without_a_rate_is_the_400()
    {
        var org = await _w.NewOrgAsync();
        await using var db = _w.Db(org);

        var act = () => _w.Currency(db).LockRateAsync(Usd, D("2026-10-07"), TransactionDomain.Sale);
        await act.Should().ThrowAsync<CurrencyRateNotFoundException>().WithMessage("No exchange rate for USD on 2026-10-07*");
    }

    [Fact]
    public async Task T_C5_07_JPY_and_BHD_rounding_uses_each_currency_decimals()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Jpy, 1.86m, "2026-10-01");
        await _w.SeedRateAsync(org, Bhd, 737.47m, "2026-10-01");
        await using var db = _w.Db(org);

        var jpy = await _w.Currency(db).LockRateAsync(Jpy, D("2026-10-07"), TransactionDomain.Sale);
        jpy.CurrencyDecimalPlaces.Should().Be(0);
        jpy.ToBase(12_340m).Should().Be(22_952.40m);

        var toJpy = await _w.Currency(db).ConvertAsync(1000m, Pkr, Jpy, D("2026-10-07"));
        toJpy.ConvertedAmount.Should().Be(538m, "0dp for JPY");

        var toBhd = await _w.Currency(db).ConvertAsync(1000m, Pkr, Bhd, D("2026-10-07"));
        toBhd.ConvertedAmount.Should().Be(1.356m, "3dp for BHD");
    }

    [Fact]
    public async Task Active_rates_include_the_rate_currency_row_and_only_open_rows()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 277m, "2026-09-01", "2026-09-30");
        await _w.SeedRateAsync(org, Usd, 278m, "2026-10-01");
        await using var db = _w.Db(org);

        var active = await _w.Currency(db).GetActiveRatesAsync();
        active.Select(a => (a.CurrencyCode, a.Rate)).Should().BeEquivalentTo(new[] { ("PKR", 1m), ("USD", 278m) });
    }

    [Fact]
    public async Task Convert_endpoint_shape_defaults_to_the_domain_base_and_reports_the_range_the_cross_rate_holds()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Pkr, saleBase: Pkr, purchaseBase: Usd);
        await _w.SeedRateAsync(org, Aed, 76.30m, "2026-10-07");
        await _w.SeedRateAsync(org, Usd, 278m, "2026-10-01", "2026-10-09");
        await using var db = _w.Db(org);
        var lookup = new SMS.Modules.Finance.Services.OrgCurrencyLookup(db);

        var sale = await SMS.Modules.Finance.Controllers.CurrencyConversionController.ConvertAsync(
            _w.Currency(db), lookup, org, new() { Amount = 5000m, FromCurrencyId = Aed, Date = "2026-10-07" });
        sale.ConvertedAmount.Should().Be(381_500m);
        sale.ToCurrency.Code.Should().Be("PKR");
        (sale.EffectiveFrom, sale.EffectiveTo, sale.Domain).Should().Be(("2026-10-07", "9999-12-31", "SALE"));

        var purchase = await SMS.Modules.Finance.Controllers.CurrencyConversionController.ConvertAsync(
            _w.Currency(db), lookup, org, new() { Amount = 5000m, FromCurrencyId = Aed, Date = "2026-10-07", Domain = "purchase" });
        purchase.ToCurrency.Code.Should().Be("USD");
        (purchase.EffectiveFrom, purchase.EffectiveTo).Should().Be(("2026-10-07", "2026-10-09"));

        var bad = () => SMS.Modules.Finance.Controllers.CurrencyConversionController.ConvertAsync(
            _w.Currency(db), lookup, org, new() { Amount = 1m, FromCurrencyId = Aed, Domain = "SIDEWAYS" });
        await bad.Should().ThrowAsync<SMS.Shared.Exceptions.BadRequestException>();
    }

    [Fact]
    public async Task Another_organizations_rates_are_never_used_even_by_a_super_admin_or_explicit_overload()
    {
        var acme   = await _w.NewOrgAsync();
        var globex = await _w.NewOrgAsync();
        await _w.SeedRateAsync(globex, Usd, 280m, "2026-10-01");

        await using var db = _w.Db(acme, superAdmin: true);
        var act = () => _w.Currency(db).GetRateAsync(Usd, D("2026-10-07"));
        await act.Should().ThrowAsync<CurrencyRateNotFoundException>();

        (await _w.Currency(db).GetRateAsync(globex, Usd, D("2026-10-07"))).Rate.Should().Be(280m, "explicit org overload (Hangfire)");
    }
}
