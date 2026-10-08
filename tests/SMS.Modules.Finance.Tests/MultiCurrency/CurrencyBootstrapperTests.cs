using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Shared.Common;
using Xunit;
using static SMS.Modules.Finance.Tests.MultiCurrency.CurrencyWorld;

namespace SMS.Modules.Finance.Tests.MultiCurrency;

/// <summary>A35 P1-13 / P1-14 / E-01 — the per-organization bootstrap: seed currencies, rate-currency row, legacy conversion, participants.</summary>
public class CurrencyBootstrapperTests
{
    private readonly CurrencyWorld _w = new();

    private sealed class Recorder : ICurrencyRatesReadyParticipant
    {
        public List<(Guid Org, int Rates)> Calls { get; } = [];
        public Func<Guid, Task<int>>? CountRates { get; init; }
        public async Task OnCurrencyRatesReadyAsync(Guid organizationId, CancellationToken ct = default) =>
            Calls.Add((organizationId, CountRates is null ? 0 : await CountRates(organizationId)));
    }

    private CurrencyBootstrapper Boot(SMS.Modules.Finance.Data.FinanceDbContext db, params ICurrencyRatesReadyParticipant[] participants) =>
        new(db, _w.Lookups().Object, participants, _w.Settings);

    private static ExchangeRate Legacy(Guid org, string from, string to, decimal rate, string day, bool deleted = false) => new()
    {
        OrganizationId = org, FromCurrencyCode = from, ToCurrencyCode = to, Rate = rate,
        EffectiveDate = DateTime.Parse(day), IsDelete = deleted, CreatedBy = 1
    };

    [Fact]
    public async Task A_new_organization_gets_the_catalogs_seed_currencies_and_its_rate_currency_row_and_a_rerun_adds_nothing()
    {
        var org = Guid.NewGuid();
        _w.Settings.Set(org, Pkr, Usd, Pkr, Pkr);

        await using (var db = _w.Db(org)) await Boot(db).OnOrganizationProvisionedAsync(org);
        await using (var db = _w.Db(org)) await Boot(db).EnsureAsync(org);

        await using var check = _w.Db(org);
        var currencies = await check.OrgCurrencies.Where(c => c.OrganizationId == org).OrderBy(c => c.DisplayOrder).ToListAsync();
        currencies.Select(c => c.Code).Should().Equal("PKR", "USD", "EUR", "AED", "JPY", "BHD");
        currencies.Single(c => c.Code == "JPY").DecimalPlaces.Should().Be(0);
        currencies.Single(c => c.Code == "BHD").Rounding.Should().Be(0.001m);
        currencies.Single(c => c.Code == "USD").CurrencyId.Should().Be(Usd, "identity is the global catalog Guid (D-1)");

        var rates = await check.CurrencyRates.Where(r => r.OrganizationId == org).ToListAsync();
        rates.Should().ContainSingle();
        rates[0].Should().BeEquivalentTo(new { CurrencyId = Pkr, Rate = 1m, InverseRate = 1m, Source = "SYSTEM",
            EffectiveFrom = CurrencyConventions.SystemStart, EffectiveTo = CurrencyConventions.OpenEnd });
    }

    [Fact]
    public async Task Without_settings_the_rate_currency_falls_back_to_PKR()
    {
        var org = Guid.NewGuid();   // no settings row at all
        await using (var db = _w.Db(org)) await Boot(db).EnsureAsync(org);

        await using var check = _w.Db(org);
        (await check.CurrencyRates.SingleAsync(r => r.OrganizationId == org)).CurrencyCode.Should().Be("PKR");
    }

    [Fact]
    public async Task Legacy_rates_against_the_rate_currency_become_ranges_others_are_left_and_participants_run_after()
    {
        var org = Guid.NewGuid();
        _w.Settings.Set(org, Pkr, Pkr, Pkr, Pkr);
        await using (var seed = _w.Db(org))
        {
            seed.ExchangeRates.AddRange(
                Legacy(org, "USD", "PKR", 277m, "2026-09-01"),
                Legacy(org, "USD", "PKR", 278m, "2026-10-01"),
                Legacy(org, "PKR", "USD", 0.0036m, "2026-10-01"),          // same day: direct wins
                Legacy(org, "PKR", "EUR", 0.004m, "2026-09-15"),           // reciprocal → 250
                Legacy(org, "USD", "EUR", 0.9m, "2026-09-15"),             // neither side PKR: not converted
                Legacy(org, "usd", "pkr", 999m, "2026-08-01", deleted: true));
            await seed.SaveChangesAsync();
        }

        var recorder = new Recorder
        {
            CountRates = async o => { await using var d = _w.Db(o); return await d.CurrencyRates.CountAsync(r => r.OrganizationId == o); }
        };
        await using (var db = _w.Db(org)) await Boot(db, recorder).EnsureAsync(org);
        await using (var db = _w.Db(org)) await Boot(db, recorder).EnsureAsync(org);

        await using var check = _w.Db(org);
        var usd = await check.CurrencyRates.Where(r => r.OrganizationId == org && r.CurrencyId == Usd).OrderBy(r => r.EffectiveFrom).ToListAsync();
        usd.Select(r => (r.Rate, r.EffectiveFrom, r.EffectiveTo)).Should().Equal(
            (277m, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
            (278m, new DateOnly(2026, 10, 1), CurrencyConventions.OpenEnd));
        var eur = await check.CurrencyRates.SingleAsync(r => r.OrganizationId == org && r.CurrencyId == Eur);
        eur.Rate.Should().Be(250m);
        (await check.CurrencyRates.CountAsync(r => r.OrganizationId == org)).Should().Be(4, "SYSTEM + 2 USD + 1 EUR, twice run");

        recorder.Calls.Should().Equal((org, 4), (org, 4));   // participants see the converted rates
    }

    [Fact]
    public async Task A_currency_already_entered_by_hand_is_not_touched_by_the_conversion()
    {
        var org = await _w.NewOrgAsync();
        await _w.SeedRateAsync(org, Usd, 280m, "2026-10-01");
        await using (var seed = _w.Db(org))
        {
            seed.ExchangeRates.Add(Legacy(org, "USD", "PKR", 277m, "2026-09-01"));
            await seed.SaveChangesAsync();
        }

        await using (var db = _w.Db(org)) await Boot(db).EnsureAsync(org);

        await using var check = _w.Db(org);
        (await check.CurrencyRates.Where(r => r.OrganizationId == org && r.CurrencyId == Usd).Select(r => r.Rate).ToListAsync())
            .Should().Equal(280m);
    }

    [Fact]
    public async Task A_failing_participant_or_organization_does_not_stop_the_others()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var recorder = new Recorder();
        var throwing = new ThrowingParticipant();

        await using (var db = _w.Db(Guid.Empty, superAdmin: true))
            await Boot(db, throwing, recorder).EnsureForAllAsync([a, b]);

        recorder.Calls.Select(c => c.Org).Should().Equal(a, b);
    }

    private sealed class ThrowingParticipant : ICurrencyRatesReadyParticipant
    {
        public Task OnCurrencyRatesReadyAsync(Guid organizationId, CancellationToken ct = default) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public async Task A_catalog_code_the_seed_list_lacks_is_added_when_it_is_the_organizations_base()
    {
        var mxn = Guid.NewGuid();
        _w.Catalog.Add(new CurrencyModel { Id = mxn, Code = "MXN", Name = "Mexican Peso", Symbol = "MX$" });
        var org = Guid.NewGuid();
        _w.Settings.Set(org, mxn, mxn, mxn, mxn);

        await using (var db = _w.Db(org)) await Boot(db).EnsureAsync(org);

        await using var check = _w.Db(org);
        (await check.OrgCurrencies.SingleAsync(c => c.OrganizationId == org && c.Code == "MXN")).DisplayOrder.Should().BeGreaterThan(18);
        (await check.CurrencyRates.SingleAsync(r => r.OrganizationId == org)).CurrencyId.Should().Be(mxn);
    }
}
