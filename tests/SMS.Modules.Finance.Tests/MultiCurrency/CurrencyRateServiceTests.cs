using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Models;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Finance.Tests.MultiCurrency.CurrencyWorld;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>A35 P1-08 / P1-10 — rate insertion and correction, BR-C2-01..07 (T-C2-01, 02, 06..09, 11). T-C2-10 is on LocalDB.</summary>
public class CurrencyRateServiceTests
{
    private readonly CurrencyWorld _w = new();

    private static SaveCurrencyRateRequest Req(Guid currency, decimal rate, string from, string? to = null, string? notes = null) =>
        new() { CurrencyId = currency, Rate = rate, EffectiveFrom = from, EffectiveTo = to, Notes = notes };

    private async Task<CurrencyRateInsertResult> Insert(Guid org, SaveCurrencyRateRequest req)
    {
        await using var db = _w.Db(org);
        return await _w.Rates(db).InsertAsync(req, User);
    }

    [Fact]
    public async Task T_C2_01_and_09_first_rate_is_open_ended_with_its_inverse_computed()
    {
        var org = await _w.NewOrgAsync();

        var r = await Insert(org, Req(Usd, 278.05m, "2026-10-07", notes: "SBP closing rate"));

        r.Rate.EffectiveFrom.Should().Be("2026-10-07");
        r.Rate.EffectiveTo.Should().Be("9999-12-31");
        r.Rate.IsCurrent.Should().BeTrue();
        r.Rate.InverseRate.Should().Be(0.0035964755m, "1 / 278.05 = 0.00359647545… rounded away from zero at 10dp");
        r.Rate.Source.Should().Be("MANUAL");
        r.Rate.RateCurrencyCode.Should().Be("PKR");
        r.ClosedPrevious.Should().BeNull();
    }

    [Fact]
    public async Task T_C2_02_and_11_a_new_rate_closes_the_previous_to_the_day_before()
    {
        var org = await _w.NewOrgAsync();
        await Insert(org, Req(Usd, 278.05m, "2026-10-07"));

        var r = await Insert(org, Req(Usd, 278.12m, "2026-10-10"));

        r.ClosedPrevious!.EffectiveTo.Should().Be("2026-10-09");
        r.Rate.EffectiveFrom.Should().Be("2026-10-10");
        await using var db = _w.Db(org);
        (await db.CurrencyRates.CountAsync(x => x.CurrencyCode == "USD")).Should().Be(2);
    }

    [Theory]
    [InlineData("2026-10-07")]   // same day as the current row's start
    [InlineData("2026-10-03")]   // before it
    [InlineData("2026-10-02")]   // inside a closed range
    public async Task T_C2_06_a_date_already_covered_is_409(string from)
    {
        var org = await _w.NewOrgAsync();
        await Insert(org, Req(Usd, 277.50m, "2026-10-01", "2026-10-04"));
        await Insert(org, Req(Usd, 278.05m, "2026-10-07"));

        var act = () => Insert(org, Req(Usd, 279m, from));
        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("Rate already exists for this date*");
    }

    [Fact]
    public async Task A_fixed_range_fills_a_gap_but_not_an_overlap()
    {
        var org = await _w.NewOrgAsync();
        await Insert(org, Req(Usd, 277.50m, "2026-10-01", "2026-10-04"));
        await Insert(org, Req(Usd, 278.05m, "2026-10-07"));

        var gap = await Insert(org, Req(Usd, 277.85m, "2026-10-05", "2026-10-06"));
        gap.Rate.EffectiveTo.Should().Be("2026-10-06");

        var act = () => Insert(org, Req(Usd, 1m, "2026-10-06", "2026-10-08"));
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task T_C2_07_the_rate_currency_cannot_be_given_a_rate()
    {
        var org = await _w.NewOrgAsync();

        var act = () => Insert(org, Req(Pkr, 1.1m, "2026-10-07"));
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("Base currency rate cannot be modified*");
    }

    [Fact]
    public async Task The_system_row_cannot_be_edited()
    {
        var org = await _w.NewOrgAsync();
        await using var db = _w.Db(org);
        var system = await db.CurrencyRates.SingleAsync(x => x.Source == "SYSTEM");

        var act = () => _w.Rates(db).UpdateAsync(system.Uuid, Req(Pkr, 2m, "2000-01-01"), User);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("Base currency rate cannot be modified*");
    }

    [Theory]
    [InlineData(-278.05)]
    [InlineData(0)]
    public async Task T_C2_08_rate_must_be_positive(decimal rate)
    {
        var org = await _w.NewOrgAsync();
        var act = () => Insert(org, Req(Usd, rate, "2026-10-07"));
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("Rate must be positive*");
    }

    [Fact]
    public async Task More_than_ten_decimals_or_too_large_is_400()
    {
        var org = await _w.NewOrgAsync();
        var many = () => Insert(org, Req(Usd, 1.12345678901m, "2026-10-07"));
        await many.Should().ThrowAsync<BadRequestException>();
        var huge = () => Insert(org, Req(Usd, 100_000_000m, "2026-10-07"));
        await huge.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_currency_that_is_not_an_active_org_currency_is_400()
    {
        var org = await _w.NewOrgAsync();
        var act = () => Insert(org, Req(Mxn, 15m, "2026-10-07"));
        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*not an active currency of this organization*");
    }

    [Fact]
    public async Task Effective_to_before_from_is_400()
    {
        var org = await _w.NewOrgAsync();
        var act = () => Insert(org, Req(Usd, 278m, "2026-10-07", "2026-10-06"));
        await act.Should().ThrowAsync<BadRequestException>().WithMessage("Effective to must be on or after effective from*");
    }

    [Fact]
    public async Task Correcting_a_historical_rate_checks_overlap_against_the_others_only()
    {
        var org = await _w.NewOrgAsync();
        var first = await Insert(org, Req(Usd, 277.50m, "2026-10-01"));
        await Insert(org, Req(Usd, 278.05m, "2026-10-07"));

        await using (var db = _w.Db(org))
        {
            var fixedRate = await _w.Rates(db).UpdateAsync(first.Rate.Id, Req(Usd, 277.55m, "2026-10-01", "2026-10-06", "typo"), User);
            fixedRate.Rate.Should().Be(277.55m);
            fixedRate.Notes.Should().Be("typo");
        }

        await using (var db = _w.Db(org))
        {
            var act = () => _w.Rates(db).UpdateAsync(first.Rate.Id, Req(Usd, 277.55m, "2026-10-01", "2026-10-07"), User);
            await act.Should().ThrowAsync<ConflictException>();
        }
    }

    [Fact]
    public async Task Another_organizations_rate_is_404_even_for_a_super_admin()
    {
        var acme   = await _w.NewOrgAsync();
        var globex = await _w.NewOrgAsync();
        var theirs = await Insert(globex, Req(Usd, 280m, "2026-10-01"));

        await using var db = _w.Db(acme, superAdmin: true);
        var act = () => _w.Rates(db).UpdateAsync(theirs.Rate.Id, Req(Usd, 1m, "2026-10-01"), User);
        await act.Should().ThrowAsync<NotFoundException>();
        (await _w.Rates(db).ListAsync(null, null, null)).Should().OnlyContain(r => r.CurrencyCode == "PKR");
    }

    [Fact]
    public async Task List_filters_by_currency_and_overlapping_period_and_history_is_newest_first()
    {
        var org = await _w.NewOrgAsync();
        await Insert(org, Req(Usd, 277m, "2026-09-01"));
        await Insert(org, Req(Usd, 278m, "2026-10-01"));
        await Insert(org, Req(Eur, 300m, "2026-10-01"));
        await using var db = _w.Db(org);
        var svc = _w.Rates(db);

        (await svc.ListAsync(Usd, "2026-10-01", "2026-10-31")).Select(r => r.Rate).Should().Equal(278m);
        (await svc.ListAsync(null, "2026-09-15", "2026-09-20")).Select(r => r.CurrencyCode).Should().BeEquivalentTo("PKR", "USD");
        (await svc.HistoryAsync(Usd)).Select(r => r.Rate).Should().Equal(278m, 277m);
        (await svc.ActiveAsync()).Select(r => r.CurrencyCode).Should().BeEquivalentTo("PKR", "USD", "EUR");
        (await svc.ForDateAsync(Usd, "2026-09-15")).Rate.Should().Be(277m);
    }
}
