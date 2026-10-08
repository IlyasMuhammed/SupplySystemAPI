using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.MultiCurrency;
using SMS.Shared.Common;
using Xunit;
using static SMS.Modules.Finance.Tests.MultiCurrency.CurrencyWorld;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>
/// IExchangeRateProvider as Demand, Integration and Logistics see it — A35-E-01 (D-4, D-6): same signature, now over
/// finance.currency_rates with triangulation through the rate currency; same currency = 1; nothing = null; the caller's
/// organization only. (Replaces the SAP S-4 pair-table tests: the legacy table is frozen.)
/// </summary>
public class ExchangeRateProviderTests
{
    private readonly CurrencyWorld _w = new();

    private static DateTime Day(int month, int day, int hour = 0) => new(2026, month, day, hour, 0, 0);

    private async Task<ExchangeRateQuote?> Quote(Guid org, string from, string to, DateTime asOf, bool superAdmin = false)
    {
        await using var db = _w.Db(org, superAdmin);
        return await new ExchangeRateProvider(db, _w.Settings).GetRateAsync(from, to, asOf);
    }

    [Fact]
    public async Task Rate_currency_target_uses_the_row_covering_the_day()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 278m, "2026-09-30", "2026-09-30");
        await _w.SeedRateAsync(org, Usd, 278.5m, "2026-10-01");

        (await Quote(org, "USD", "PKR", Day(10, 1, 15))).Should().Be(new ExchangeRateQuote("USD", "PKR", 278.5m, Day(10, 1), false));
        (await Quote(org, "USD", "PKR", Day(9, 30)))!.Rate.Should().Be(278m);
    }

    [Fact]
    public async Task The_reverse_direction_is_the_reciprocal_at_ten_decimals_and_not_marked_inverted()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 278.5m, "2026-10-01");

        (await Quote(org, "PKR", "USD", Day(10, 2))).Should().Be(new ExchangeRateQuote("PKR", "USD", 0.0035906643m, Day(10, 1), false));
    }

    [Fact]
    public async Task Triangulates_through_the_rate_currency_with_the_later_start_as_effective_date()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 278m, "2026-09-01");
        await _w.SeedRateAsync(org, Eur, 300m, "2026-09-15");

        var q = await Quote(org, "USD", "EUR", Day(10, 1));
        q!.Rate.Should().Be(Math.Round(278m / 300m, 10, MidpointRounding.AwayFromZero));
        q.EffectiveDate.Should().Be(Day(9, 15));
        q.Inverted.Should().BeFalse();
    }

    [Fact]
    public async Task Same_currency_is_one_dated_the_day_asked_even_with_nothing_on_file()
    {
        var org = await _w.NewOrgAsync();
        (await Quote(org, "pkr", " PKR ", Day(10, 5, 14))).Should().Be(new ExchangeRateQuote("PKR", "PKR", 1m, Day(10, 5), false));
    }

    [Fact]
    public async Task Nothing_covering_the_day_or_an_unknown_code_is_null()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 278m, "2026-09-01");

        (await Quote(org, "USD", "PKR", Day(8, 31))).Should().BeNull();
        (await Quote(org, "USD", "EUR", Day(10, 1))).Should().BeNull("no EUR rate");
        (await Quote(org, "XXX", "PKR", Day(10, 1))).Should().BeNull();
    }

    [Theory]
    [InlineData(null, "PKR")]
    [InlineData("USD", null)]
    [InlineData("", "PKR")]
    [InlineData("USD", "   ")]
    public async Task A_blank_currency_has_no_rate(string? from, string? to)
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 278m, "2026-09-01");
        (await Quote(org, from!, to!, Day(10, 1))).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_rates_are_never_used_even_by_a_super_admin()
    {
        var acme   = await _w.NewOrgAsync();
        var globex = await _w.NewOrgAsync();
        await _w.SeedRateAsync(globex, Usd, 280m, "2026-09-01");

        (await Quote(acme, "USD", "PKR", Day(10, 1))).Should().BeNull();
        (await Quote(acme, "USD", "PKR", Day(10, 1), superAdmin: true)).Should().BeNull();
        (await Quote(globex, "USD", "PKR", Day(10, 1)))!.Rate.Should().Be(280m);
    }

    [Fact]
    public async Task A_background_job_gets_the_organization_its_tenant_context_carries()
    {
        var acme   = await _w.NewOrgAsync();
        var globex = await _w.NewOrgAsync();
        await _w.SeedRateAsync(acme, Usd, 278m, "2026-09-01");
        await _w.SeedRateAsync(globex, Usd, 280m, "2026-09-01");

        await using var jobDb = new FinanceDbContext(
            new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(_w.DbName).Options,
            new StaticTenantContext { OrganizationId = globex, IsSuperAdmin = false });

        (await new ExchangeRateProvider(jobDb, _w.Settings).GetRateAsync("USD", "PKR", Day(10, 1)))!.Rate.Should().Be(280m);
    }

    [Fact]
    public void Converting_with_a_quote_rounds_to_two_decimals_by_default_and_to_the_given_decimals_otherwise()
    {
        ExchangeRateMath.Convert(100m, 0.00359066m).Should().Be(0.36m);
        ExchangeRateMath.Convert(1.005m, 1m).Should().Be(1.01m);
        ExchangeRateMath.Convert(1234.5m, 1m, 0).Should().Be(1235m);
        ExchangeRateMath.Convert(1.5675m, 1m, 3).Should().Be(1.568m);
    }
}
