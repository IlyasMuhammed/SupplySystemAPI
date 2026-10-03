using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>
/// IExchangeRateProvider as Demand, Integration and Logistics see it (S-4): latest rate on or before the day,
/// else the reciprocal of the opposite pair, same currency = 1, nothing = null — for the caller's organization only.
/// </summary>
public class ExchangeRateProviderTests
{
    private readonly SetupWorld _world = new();
    private readonly SetupDesk  _acme;
    private readonly SetupDesk  _globex;

    public ExchangeRateProviderTests()
    {
        _acme   = _world.For(Guid.NewGuid());
        _globex = _world.For(Guid.NewGuid());
    }

    private static DateTime Day(int month, int day, int hour = 0) => new(2026, month, day, hour, 0, 0);

    private ExchangeRate Row(string from, string to, decimal rate, DateTime day, bool deleted = false) => new()
    {
        Uuid = Guid.NewGuid(), FromCurrencyCode = from, ToCurrencyCode = to, Rate = rate, EffectiveDate = day,
        IsDelete = deleted, CreatedBy = 1
    };

    [Fact]
    public async Task The_rate_dated_exactly_on_the_day_is_used()
    {
        await _acme.Seed(Row("USD", "PKR", 278m, Day(9, 30)), Row("USD", "PKR", 278.5m, Day(10, 1)));

        var quote = await _acme.Quote("USD", "PKR", Day(10, 1));

        quote.Should().Be(new ExchangeRateQuote("USD", "PKR", 278.5m, Day(10, 1), Inverted: false));
    }

    [Fact]
    public async Task Otherwise_the_latest_rate_before_the_day_is_used_and_the_time_of_day_asked_does_not_matter()
    {
        await _acme.Seed(Row("USD", "PKR", 277m, Day(9, 1)), Row("USD", "PKR", 278m, Day(9, 15)));

        var quote = await _acme.Quote("USD", "PKR", Day(9, 20, hour: 23));

        quote!.Rate.Should().Be(278m);
        quote.EffectiveDate.Should().Be(Day(9, 15));
    }

    [Fact]
    public async Task A_future_dated_rate_is_not_used_until_its_day()
    {
        await _acme.Seed(Row("USD", "PKR", 278m, Day(9, 15)), Row("USD", "PKR", 290m, Day(10, 15)));

        (await _acme.Quote("USD", "PKR", Day(10, 14, hour: 23)))!.Rate.Should().Be(278m);
        (await _acme.Quote("USD", "PKR", Day(10, 15)))!.Rate.Should().Be(290m);
    }

    [Fact]
    public async Task With_only_the_opposite_pair_on_file_its_reciprocal_is_used_rounded_to_eight_decimals_and_marked_inverted()
    {
        await _acme.Seed(Row("USD", "PKR", 278.5m, Day(10, 1)));

        var quote = await _acme.Quote("PKR", "USD", Day(10, 2));

        quote.Should().Be(new ExchangeRateQuote("PKR", "USD", 0.00359066m, Day(10, 1), Inverted: true));
    }

    [Fact]
    public async Task The_direct_pair_wins_over_a_newer_opposite_pair()
    {
        await _acme.Seed(Row("USD", "PKR", 278m, Day(9, 1)), Row("PKR", "USD", 0.0035m, Day(10, 1)));

        var quote = await _acme.Quote("USD", "PKR", Day(10, 2));

        quote!.Rate.Should().Be(278m);
        quote.Inverted.Should().BeFalse();
    }

    [Fact]
    public async Task The_opposite_pair_is_also_only_used_up_to_the_day()
    {
        await _acme.Seed(Row("USD", "PKR", 250m, Day(9, 1)), Row("USD", "PKR", 300m, Day(10, 1)));

        (await _acme.Quote("PKR", "USD", Day(9, 30)))!.Rate.Should().Be(0.004m);
    }

    [Fact]
    public async Task The_same_currency_is_one_dated_the_day_asked_even_with_nothing_on_file()
    {
        var quote = await _acme.Quote("pkr", " PKR ", Day(10, 5, hour: 14));

        quote.Should().Be(new ExchangeRateQuote("PKR", "PKR", 1m, Day(10, 5), Inverted: false));
    }

    [Fact]
    public async Task Nothing_on_file_either_way_is_null_and_there_is_no_triangulation()
    {
        await _acme.Seed(Row("USD", "PKR", 278m, Day(9, 1)), Row("EUR", "PKR", 301m, Day(9, 1)));

        (await _acme.Quote("USD", "EUR", Day(10, 1))).Should().BeNull("USD→EUR is not derived through PKR");
        (await _acme.Quote("USD", "PKR", Day(8, 31))).Should().BeNull("nothing on or before the day");
    }

    [Fact]
    public async Task Deleted_rates_are_ignored_both_ways()
    {
        await _acme.Seed(
            Row("USD", "PKR", 278m, Day(9, 1)),
            Row("USD", "PKR", 2780m, Day(9, 15), deleted: true));

        (await _acme.Quote("USD", "PKR", Day(10, 1)))!.Rate.Should().Be(278m);

        await _acme.Seed(Row("EUR", "PKR", 301m, Day(9, 1), deleted: true));
        (await _acme.Quote("PKR", "EUR", Day(10, 1))).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_rates_are_never_used_even_by_a_super_admin()
    {
        await _globex.Seed(Row("USD", "PKR", 280m, Day(9, 1)));

        (await _acme.Quote("USD", "PKR", Day(10, 1))).Should().BeNull();
        (await _acme.Quote("USD", "PKR", Day(10, 1), superAdmin: true)).Should().BeNull();
        (await _acme.Quote("PKR", "USD", Day(10, 1), superAdmin: true)).Should().BeNull();
        (await _globex.Quote("USD", "PKR", Day(10, 1)))!.Rate.Should().Be(280m);
    }

    [Fact]
    public async Task A_background_job_gets_the_organization_its_tenant_context_carries()
    {
        // In a Hangfire job TenantContext reports the job's organization (HangfireTenantScope) and no bypass;
        // a context built that way is exactly what the job's FinanceDbContext gets.
        await _acme.Seed(Row("USD", "PKR", 278m, Day(9, 1)));
        await _globex.Seed(Row("USD", "PKR", 280m, Day(9, 1)));

        await using var jobDb = new SMS.Modules.Finance.Data.FinanceDbContext(
            new DbContextOptionsBuilder<SMS.Modules.Finance.Data.FinanceDbContext>().UseInMemoryDatabase(_world.DbName).Options,
            new StaticTenantContext { OrganizationId = _globex.Org, IsSuperAdmin = false });

        (await new SMS.Modules.Finance.Services.ExchangeRateProvider(jobDb).GetRateAsync("USD", "PKR", Day(10, 1)))!
            .Rate.Should().Be(280m);
    }

    [Theory]
    [InlineData(null, "PKR")]
    [InlineData("USD", null)]
    [InlineData("", "PKR")]
    [InlineData("USD", "   ")]
    public async Task A_blank_currency_has_no_rate(string? from, string? to)
    {
        await _acme.Seed(Row("USD", "PKR", 278m, Day(9, 1)));

        (await _acme.Quote(from!, to!, Day(10, 1))).Should().BeNull();
    }

    [Fact]
    public async Task Codes_are_matched_in_any_case_and_returned_upper_case()
    {
        await _acme.Seed(Row("USD", "PKR", 278m, Day(9, 1)));

        var quote = await _acme.Quote(" usd", "Pkr ", Day(10, 1));

        quote!.FromCurrencyCode.Should().Be("USD");
        quote.ToCurrencyCode.Should().Be("PKR");
        quote.Rate.Should().Be(278m);
    }

    [Fact]
    public async Task A_reciprocal_too_small_for_eight_decimals_is_no_rate()
    {
        await _acme.Seed(Row("XAU", "VND", 9_000_000_000m, Day(9, 1)));

        (await _acme.Quote("VND", "XAU", Day(10, 1))).Should().BeNull();
    }

    [Fact]
    public void Converting_with_a_quote_rounds_to_two_decimals_away_from_zero()
    {
        ExchangeRateMath.Convert(100m, 0.00359066m).Should().Be(0.36m);
        ExchangeRateMath.Convert(1.005m, 1m).Should().Be(1.01m);
    }
}
