using FluentAssertions;
using SMS.Modules.Finance.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Finance.Tests.MultiCurrency.CurrencyWorld;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>A35 P1-03 / P1-04 — org currencies, BR-C1-01..05 (T-C1-01..07).</summary>
public class OrgCurrencyServiceTests
{
    private readonly CurrencyWorld _w = new();

    private async Task<OrgCurrencyModel> Create(Guid org, SaveOrgCurrencyRequest req)
    {
        await using var db = _w.Db(org);
        return await _w.OrgCurrencies(db).CreateAsync(req, User);
    }

    private async Task<OrgCurrencyModel> Update(Guid org, Guid currencyId, SaveOrgCurrencyRequest req)
    {
        await using var db = _w.Db(org);
        return await _w.OrgCurrencies(db).UpdateAsync(currencyId, req, User);
    }

    [Fact]
    public async Task T_C1_01_a_new_code_is_added_to_the_catalog_and_the_org()
    {
        var org = await _w.NewOrgAsync();
        var mxn = await Create(org, new() { Code = "MXN", Name = "Mexican Peso", Symbol = "MX$", DecimalPlaces = 2 });

        mxn.Code.Should().Be("MXN");
        _w.Catalog.Should().Contain(c => c.Code == "MXN" && c.Id == mxn.CurrencyId);
        await using var db = _w.Db(org);
        (await _w.OrgCurrencies(db).ListAsync(false)).Should().Contain(c => c.Code == "MXN");
    }

    [Theory]
    [InlineData("US", "Currency code must be exactly 3 characters")]
    [InlineData("usd", "Currency code must be uppercase")]
    [InlineData("U1D", "Currency code must be letters A–Z")]
    public async Task T_C1_02_03_bad_codes_are_400(string code, string message)
    {
        var org = await _w.NewOrgAsync();
        var act = () => Create(org, new() { Code = code, Name = "X", Symbol = "X" });
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage(message);
    }

    [Fact]
    public async Task T_C1_04_05_duplicate_in_the_same_org_is_409_but_another_org_may_have_it()
    {
        var acme   = await _w.NewOrgAsync();
        var act = () => Create(acme, new() { Code = "USD" });
        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("Currency USD already exists");

        var globex = Guid.NewGuid();
        _w.Settings.Set(globex, Pkr, Pkr, Pkr, Pkr);
        var usd = await Create(globex, new() { Code = "USD" });
        usd.CurrencyId.Should().Be(Usd, "an existing catalog code is linked, not duplicated");
        usd.Name.Should().Be("US Dollar");
    }

    [Fact]
    public async Task T_C1_06_07_deactivating_an_unused_currency_works_a_base_or_the_rate_currency_does_not()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Pkr, saleBase: Pkr, purchaseBase: Usd);

        (await Update(org, Eur, new() { IsActive = false })).IsActive.Should().BeFalse();

        var sale = () => Update(org, Pkr, new() { IsActive = false });
        (await sale.Should().ThrowAsync<BadRequestException>()).WithMessage("Cannot deactivate — used as sale base currency");
        var purchase = () => Update(org, Usd, new() { IsActive = false });
        (await purchase.Should().ThrowAsync<BadRequestException>()).WithMessage("Cannot deactivate — used as purchase base currency");
    }

    [Fact]
    public async Task Decimal_places_cannot_change_once_documents_use_the_currency()
    {
        var org = await _w.NewOrgAsync();
        _w.Checkers.Add(new FakeUsageChecker { CurrencyUsage = "2 confirmed sale orders" });

        var act = () => Update(org, Aed, new() { DecimalPlaces = 3 });
        (await act.Should().ThrowAsync<ConflictException>()).WithMessage("Cannot change decimal places — 2 confirmed sale orders");
        (await Update(org, Aed, new() { Symbol = "AED" })).Symbol.Should().Be("AED");
    }

    [Theory]
    [InlineData(4, null, null, "Decimal places must be between 0 and 3")]
    [InlineData(2, "0", null, "Rounding must be greater than 0")]
    [InlineData(2, null, "middle", "Symbol position must be 'before' or 'after'")]
    public async Task Bad_formatting_is_400(int decimals, string? rounding, string? position, string message)
    {
        var org = await _w.NewOrgAsync();
        var act = () => Update(org, Usd, new()
        {
            DecimalPlaces = decimals, Rounding = rounding is null ? null : decimal.Parse(rounding), SymbolPosition = position
        });
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage(message);
    }

    [Fact]
    public async Task List_marks_bases_and_rate_currency_and_hides_inactive_unless_asked()
    {
        var org = await _w.NewOrgAsync(rateCurrency: Pkr, saleBase: Pkr, purchaseBase: Usd);
        await Update(org, Jpy, new() { IsActive = false });
        await using var db = _w.Db(org);

        var list = await _w.OrgCurrencies(db).ListAsync(false);
        list.Should().NotContain(c => c.Code == "JPY");
        list.Single(c => c.Code == "PKR").Should().BeEquivalentTo(new { IsRateCurrency = true, BaseFor = new[] { "SALE", "SERVICE" } });
        list.Single(c => c.Code == "USD").BaseFor.Should().Equal("PURCHASE");
        (await _w.OrgCurrencies(db).ListAsync(true)).Should().Contain(c => c.Code == "JPY");
    }

    [Fact]
    public async Task Another_organizations_currency_is_404_even_for_a_super_admin()
    {
        var acme   = await _w.NewOrgAsync();
        var globex = await _w.NewOrgAsync();
        var mxn    = await Create(acme, new() { Code = "MXN", Name = "Mexican Peso", Symbol = "MX$" });

        await using var db = _w.Db(globex, superAdmin: true);
        var act = () => _w.OrgCurrencies(db).GetAsync(mxn.CurrencyId);
        await act.Should().ThrowAsync<NotFoundException>();
    }
}
