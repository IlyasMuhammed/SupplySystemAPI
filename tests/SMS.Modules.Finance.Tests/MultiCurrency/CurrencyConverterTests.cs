using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using Xunit;
using static SMS.Modules.Finance.Tests.MultiCurrency.CurrencyWorld;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>
/// A46 BE-2 â€” ICurrencyConverter (TS-52..56, A46-0112..0134), BR-34 manual-over-feed (A46-0100/0109), FEED rows read-only
/// (A46-0096/0104) and POST api/finance/convert-currency. Base (rate) currency is USD: EUR 1.087 USD, AED 0.2723 USD.
/// </summary>
public class CurrencyConverterTests
{
    private readonly CurrencyWorld _w = new();
    private static readonly DateTime D = new(2026, 10, 15);

    private async Task<Guid> OrgAsync()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Usd);
        await _w.SeedRateAsync(org, Eur, 1.087m, "2026-10-01");
        await _w.SeedRateAsync(org, Aed, 0.2723m, "2026-10-01");
        return org;
    }

    private async Task<CurrencyConvertResult> Convert(Guid org, decimal amount, Guid from, Guid to, DateTime? on = null, int dp = 4)
    {
        await using var db = _w.Db(org);
        return await new CurrencyConverter(db, _w.Settings)
            .ConvertAsync(new CurrencyConvertRequest(amount, from, null, to, null, on ?? D, dp));
    }

    [Fact]
    public async Task Same_currency_returns_the_amount_unchanged_with_rate_one_and_needs_no_rate()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Usd);   // no EUR rate on file at all
        var r = await Convert(org, 123.456789m, Eur, Eur);
        r.Success.Should().BeTrue();
        r.ConvertedAmount.Should().Be(123.456789m);
        r.RateUsed.Should().Be(1m);
        r.FromCurrencyCode.Should().Be("EUR");
        r.ToCurrencyCode.Should().Be("EUR");
    }

    [Fact]
    public async Task Same_currency_by_code_any_case()
    {
        var org = await OrgAsync();
        await using var db = _w.Db(org);
        var r = await new CurrencyConverter(db, _w.Settings)
            .ConvertAsync(new CurrencyConvertRequest(10m, null, " eur ", null, "EUR", D));
        r.Success.Should().BeTrue();
        r.ConvertedAmount.Should().Be(10m);
    }

    [Fact]
    public async Task Base_to_foreign_divides_by_the_foreign_rate()
    {
        var org = await OrgAsync();
        var r = await Convert(org, 100m, Usd, Eur);
        r.Success.Should().BeTrue();
        r.RateUsed.Should().Be(0.9199632015m);
        r.ConvertedAmount.Should().Be(91.9963m);
        r.RateDate.Should().Be(new DateTime(2026, 10, 1));
    }

    [Fact]
    public async Task Foreign_to_base_multiplies_by_the_foreign_rate()
    {
        var org = await OrgAsync();
        var r = await Convert(org, 50m, Eur, Usd);
        r.Success.Should().BeTrue();
        r.RateUsed.Should().Be(1.087m);
        r.ConvertedAmount.Should().Be(54.35m);
    }

    [Fact]
    public async Task Cross_rate_triangulates_through_the_base_currency()
    {
        var org = await OrgAsync();
        var r = await Convert(org, 100m, Eur, Aed);
        // 1.087 / 0.2723 = 3.99192067...
        r.Success.Should().BeTrue();
        r.RateUsed.Should().Be(CurrencyConventions.RoundRate(1.087m / 0.2723m));
        r.ConvertedAmount.Should().Be(Math.Round(100m * r.RateUsed, 4, MidpointRounding.AwayFromZero));

        var back = await Convert(org, 100m, Aed, Eur);
        back.RateUsed.Should().Be(CurrencyConventions.RoundRate(0.2723m / 1.087m));
    }

    [Fact]
    public async Task No_rate_for_the_from_side_is_a_result_not_an_exception()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Usd);
        await _w.SeedRateAsync(org, Aed, 0.2723m, "2026-10-01");
        var r = await Convert(org, 10m, Eur, Aed);
        r.Success.Should().BeFalse();
        r.ConvertedAmount.Should().Be(0m);
        r.ErrorMessage.Should().Contain("EUR").And.Contain("2026-10-15");
    }

    [Fact]
    public async Task No_rate_for_the_to_side_names_that_currency()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Usd);
        await _w.SeedRateAsync(org, Eur, 1.087m, "2026-10-01");
        var r = await Convert(org, 10m, Eur, Aed);
        r.Success.Should().BeFalse();
        r.ErrorMessage.Should().Contain("AED").And.Contain("2026-10-15");
    }

    [Fact]
    public async Task A_rate_before_the_date_is_used_and_a_date_before_every_rate_is_not_found()
    {
        var org = await OrgAsync();          // EUR open-ended from 2026-10-01
        var late = await Convert(org, 10m, Eur, Usd, new DateTime(2026, 10, 18));
        late.Success.Should().BeTrue();
        late.RateDate.Should().Be(new DateTime(2026, 10, 1));

        var early = await Convert(org, 10m, Eur, Usd, new DateTime(2026, 9, 30));
        early.Success.Should().BeFalse();
        early.ErrorMessage.Should().Contain("2026-09-30");
    }

    [Fact]
    public async Task The_latest_rate_covering_the_date_wins_when_the_rate_changed()
    {
        var org = await OrgAsync();
        await using (var db = _w.Db(org))
            await _w.Rates(db).InsertAsync(new SaveCurrencyRateRequest { CurrencyId = Eur, Rate = 1.10m, EffectiveFrom = "2026-10-10" }, User);

        (await Convert(org, 100m, Eur, Usd, new DateTime(2026, 10, 9))).ConvertedAmount.Should().Be(108.7m);
        (await Convert(org, 100m, Eur, Usd, new DateTime(2026, 10, 12))).ConvertedAmount.Should().Be(110m);
    }

    [Theory]
    [InlineData(2, 92.00)]
    [InlineData(4, 91.9963)]
    [InlineData(6, 91.99632)]
    public async Task Decimal_places_are_respected(int dp, double expected)
    {
        var org = await OrgAsync();
        var r = await Convert(org, 100m, Usd, Eur, dp: dp);
        r.ConvertedAmount.Should().Be((decimal)expected);
    }

    [Fact]
    public async Task Rounding_is_away_from_zero_at_the_midpoint()
    {
        var org = await OrgAsync();
        await _w.SeedRateAsync(org, Jpy, 0.5m, "2026-10-01");
        (await Convert(org, 3m, Jpy, Usd, dp: 0)).ConvertedAmount.Should().Be(2m);     // 1.5 -> 2
        (await Convert(org, -3m, Jpy, Usd, dp: 0)).ConvertedAmount.Should().Be(-2m);
    }

    [Fact]
    public async Task Tiny_and_huge_amounts_keep_their_precision()
    {
        var org = await OrgAsync();
        (await Convert(org, 0.0001m, Eur, Usd, dp: 6)).ConvertedAmount.Should().Be(0.000109m);
        (await Convert(org, 0.0001m, Eur, Usd)).ConvertedAmount.Should().Be(0.0001m);
        (await Convert(org, 999_999_999.9999m, Eur, Usd)).ConvertedAmount.Should().Be(1_086_999_999.9999m);
        (await Convert(org, 0m, Eur, Usd)).ConvertedAmount.Should().Be(0m);
    }

    [Fact]
    public async Task Another_organizations_rates_are_never_used()
    {
        var a = await OrgAsync();
        var b = await _w.NewOrgAsync(rateCurrency: Usd);
        await _w.SeedRateAsync(b, Eur, 2m, "2026-10-01");
        var c = await _w.NewOrgAsync(rateCurrency: Usd);   // no rates

        (await Convert(a, 10m, Eur, Usd)).ConvertedAmount.Should().Be(10.87m);
        (await Convert(b, 10m, Eur, Usd)).ConvertedAmount.Should().Be(20m);
        (await Convert(c, 10m, Eur, Usd)).Success.Should().BeFalse();

        // A super admin's context still only sees the org it is acting as.
        await using var db = _w.Db(a, superAdmin: true);
        var r = await new CurrencyConverter(db, _w.Settings).ConvertAsync(new CurrencyConvertRequest(10m, Eur, null, Usd, null, D));
        r.ConvertedAmount.Should().Be(10.87m);
    }

    [Fact]
    public async Task Unknown_or_missing_currency_is_a_failed_result()
    {
        var org = await OrgAsync();
        await using var db = _w.Db(org);
        var sut = new CurrencyConverter(db, _w.Settings);

        var unknown = await sut.ConvertAsync(new CurrencyConvertRequest(1m, null, "XXX", null, "USD", D));
        unknown.Success.Should().BeFalse();
        unknown.ErrorMessage.Should().Contain("XXX");

        var blank = await sut.ConvertAsync(new CurrencyConvertRequest(1m, null, null, null, "USD", D));
        blank.Success.Should().BeFalse();
    }

    [Fact]
    public async Task GetRate_matches_convert_and_reports_not_found()
    {
        var org = await OrgAsync();
        await using var db = _w.Db(org);
        var sut = new CurrencyConverter(db, _w.Settings);

        var ok = await sut.GetRateAsync(Eur, null, null, "usd", D);
        ok.Found.Should().BeTrue();
        ok.Rate.Should().Be(1.087m);
        ok.FromCurrencyCode.Should().Be("EUR");
        ok.ToCurrencyCode.Should().Be("USD");

        var none = await sut.GetRateAsync(Eur, null, Usd, null, new DateTime(2020, 1, 1));
        none.Found.Should().BeFalse();
        none.ErrorMessage.Should().Contain("EUR");
    }

    // â”€â”€ BR-34 / FEED read-only â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task<Guid> SeedFeedAsync(Guid org, decimal rate, string from)
    {
        await using var db = _w.Db(org);
        var row = new CurrencyRate
        {
            OrganizationId = org, CurrencyId = Eur, CurrencyCode = "EUR", Rate = rate, InverseRate = CurrencyConventions.RoundRate(1m / rate),
            EffectiveFrom = DateOnly.Parse(from), EffectiveTo = CurrencyConventions.OpenEnd, Source = "API_ECB"
        };
        db.CurrencyRates.Add(row);
        await db.SaveChangesAsync();
        return row.Uuid;
    }

    [Fact]
    public async Task BR34_a_manual_rate_for_the_same_date_takes_over_the_feed_row()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Usd);
        await SeedFeedAsync(org, 1.05m, "2026-10-10");

        await using (var db = _w.Db(org))
        {
            var res = await _w.Rates(db).InsertAsync(new SaveCurrencyRateRequest { CurrencyId = Eur, Rate = 1.09m, EffectiveFrom = "2026-10-10" }, User);
            res.Rate.Source.Should().Be("MANUAL");
            res.Rate.Rate.Should().Be(1.09m);
        }

        await using var check = _w.Db(org);
        var rows = await check.CurrencyRates.Where(r => r.CurrencyCode == "EUR").ToListAsync();
        rows.Should().ContainSingle().Which.Source.Should().Be("MANUAL");
        (await Convert(org, 100m, Eur, Usd)).ConvertedAmount.Should().Be(109m);
    }

    [Fact]
    public async Task A_manual_manual_clash_on_the_same_date_is_still_409()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Usd);
        await using var db = _w.Db(org);
        await _w.Rates(db).InsertAsync(new SaveCurrencyRateRequest { CurrencyId = Eur, Rate = 1.05m, EffectiveFrom = "2026-10-10" }, User);
        var act = () => _w.Rates(db).InsertAsync(new SaveCurrencyRateRequest { CurrencyId = Eur, Rate = 1.09m, EffectiveFrom = "2026-10-10" }, User);
        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task A_later_manual_rate_closes_a_feed_row_as_usual()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Usd);
        await SeedFeedAsync(org, 1.05m, "2026-10-01");
        await using var db = _w.Db(org);
        var res = await _w.Rates(db).InsertAsync(new SaveCurrencyRateRequest { CurrencyId = Eur, Rate = 1.09m, EffectiveFrom = "2026-10-10" }, User);
        res.ClosedPrevious!.EffectiveTo.Should().Be("2026-10-09");
        res.ClosedPrevious.Source.Should().Be("API_ECB");
    }

    [Fact]
    public async Task Feed_rows_cannot_be_updated()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Usd);
        var id = await SeedFeedAsync(org, 1.05m, "2026-10-10");
        await using var db = _w.Db(org);
        var act = () => _w.Rates(db).UpdateAsync(id, new SaveCurrencyRateRequest { Rate = 2m, EffectiveFrom = "2026-10-10" }, User);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*read-only*");

        await using var check = _w.Db(org);
        (await check.CurrencyRates.SingleAsync(r => r.Uuid == id)).Rate.Should().Be(1.05m);
    }

    [Fact]
    public async Task Manual_rows_can_still_be_updated()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Usd);
        await using var db = _w.Db(org);
        var ins = await _w.Rates(db).InsertAsync(new SaveCurrencyRateRequest { CurrencyId = Eur, Rate = 1.05m, EffectiveFrom = "2026-10-10" }, User);
        var upd = await _w.Rates(db).UpdateAsync(ins.Rate.Id, new SaveCurrencyRateRequest { Rate = 1.06m, EffectiveFrom = "2026-10-10" }, User);
        upd.Rate.Should().Be(1.06m);
    }

    [Fact]
    public void There_is_no_rate_delete_so_feed_rows_cannot_be_deleted()
    {
        typeof(ICurrencyRateService).GetMethods().Select(m => m.Name).Should().NotContain(n => n.StartsWith("Delete"));
        typeof(CurrencyRatesController).GetMethods().Select(m => m.Name).Should().NotContain(n => n.StartsWith("Delete"));
    }

    // â”€â”€ POST api/finance/convert-currency â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task<ConvertCurrencyA46Response> Post(Guid org, ConvertCurrencyA46Request req)
    {
        await using var db = _w.Db(org);
        var ctl = new FinanceConvertCurrencyController(new CurrencyConverter(db, _w.Settings));
        var ok  = (OkObjectResult)await ctl.Convert(req, default);
        ok.StatusCode.Should().Be(200);
        return ((ApiResponse<ConvertCurrencyA46Response>)ok.Value!).Result!;
    }

    [Fact]
    public async Task Endpoint_converts_by_code_and_returns_the_spec_shape()
    {
        var org = await OrgAsync();
        var r = await Post(org, new() { Amount = 50m, FromCurrencyCode = "EUR", ToCurrencyCode = "USD", EffectiveDate = "2026-10-15" });
        r.Success.Should().BeTrue();
        r.ConvertedAmount.Should().Be(54.35m);
        r.RateUsed.Should().Be(1.087m);
        r.RateDate.Should().Be("2026-10-01");
        r.FromCurrencyCode.Should().Be("EUR");
        r.ToCurrencyCode.Should().Be("USD");
        r.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Endpoint_converts_by_id_with_decimal_places()
    {
        var org = await OrgAsync();
        var r = await Post(org, new() { Amount = 100m, FromCurrencyId = Usd, ToCurrencyId = Eur, EffectiveDate = "2026-10-15", DecimalPlaces = 2 });
        r.ConvertedAmount.Should().Be(92.00m);
    }

    [Fact]
    public async Task Endpoint_answers_200_with_success_false_when_no_rate()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Usd);
        var r = await Post(org, new() { Amount = 50m, FromCurrencyCode = "EUR", ToCurrencyCode = "USD", EffectiveDate = "2026-10-15" });
        r.Success.Should().BeFalse();
        r.ErrorMessage.Should().Contain("EUR").And.Contain("2026-10-15");
    }

    [Fact]
    public async Task Endpoint_rejects_malformed_input_with_400()
    {
        var org = await OrgAsync();
        await using var db = _w.Db(org);
        var ctl = new FinanceConvertCurrencyController(new CurrencyConverter(db, _w.Settings));

        var nullBody = () => ctl.Convert(null, default);
        await nullBody.Should().ThrowAsync<BadRequestException>();
        var badDate = () => ctl.Convert(new() { Amount = 1, FromCurrencyCode = "EUR", ToCurrencyCode = "USD", EffectiveDate = "15/10/2026" }, default);
        await badDate.Should().ThrowAsync<BadRequestException>();
        var badDp = () => ctl.Convert(new() { Amount = 1, FromCurrencyCode = "EUR", ToCurrencyCode = "USD", DecimalPlaces = 9 }, default);
        await badDp.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public void Endpoint_is_permission_gated_and_the_A35_endpoint_is_unchanged()
    {
        var m = typeof(FinanceConvertCurrencyController).GetMethod(nameof(FinanceConvertCurrencyController.Convert))!;
        m.GetCustomAttributes(typeof(SMS.Shared.Authorization.RequirePermissionAttribute), false).Should().NotBeEmpty();
        typeof(CurrencyConversionController).GetMethod(nameof(CurrencyConversionController.Convert)).Should().NotBeNull();
    }
}

