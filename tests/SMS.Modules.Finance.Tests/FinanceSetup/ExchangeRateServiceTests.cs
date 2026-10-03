using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Exceptions;
using Xunit;

using static SMS.Modules.Finance.Tests.FinanceSetup.SetupDesk;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>SAP alignment, work package A — exchange rates: validation, one live rate per pair per day, soft delete, listing, quote.</summary>
public class ExchangeRateServiceTests
{
    private readonly SetupWorld _world = new();
    private readonly SetupDesk  _acme;
    private readonly SetupDesk  _globex;

    public ExchangeRateServiceTests()
    {
        _acme   = _world.For(Guid.NewGuid());
        _globex = _world.For(Guid.NewGuid());
    }

    // ── Create and validation ────────────────────────────────────────────────

    [Fact]
    public async Task A_rate_is_stored_with_upper_case_codes_a_date_only_day_and_manual_source()
    {
        var created = await _acme.CreateRate(" usd ", "pkr", 278.5m, "2026-10-01", "  SBP closing  ");

        created.FromCurrencyCode.Should().Be("USD");
        created.ToCurrencyCode.Should().Be("PKR");
        created.Rate.Should().Be(278.5m);
        created.EffectiveDate.Should().Be("2026-10-01");
        created.Source.Should().Be("MANUAL");
        created.Notes.Should().Be("SBP closing");
        created.CreatedDate.Should().Be(TestClock.Start);

        await using var db = _world.Auditor();
        var row = await db.ExchangeRates.SingleAsync();
        row.OrganizationId.Should().Be(_acme.Org);
        row.EffectiveDate.Should().Be(new DateTime(2026, 10, 1));
        row.EffectiveDate.TimeOfDay.Should().Be(TimeSpan.Zero);
        row.CreatedBy.Should().Be(SetupWorld.User);
    }

    [Fact]
    public async Task A_currency_stored_in_lower_case_in_the_catalog_is_matched_and_stored_upper_case()
    {
        (await _acme.CreateRate("AED", "PKR", 75.8m, "2026-10-01")).FromCurrencyCode.Should().Be("AED");
    }

    [Theory]
    [InlineData("2026-10-01T00:00:00Z")]
    [InlineData("2026-10-01T23:30:00-05:00")]
    [InlineData("2026-10-01 18:00")]
    public async Task A_date_time_is_cut_to_the_date_as_written_never_shifted_by_a_time_zone(string date)
    {
        (await _acme.CreateRate("USD", "PKR", 278m, date)).EffectiveDate.Should().Be("2026-10-01");
    }

    [Theory]
    [InlineData("01/10/2026")]
    [InlineData("2026-13-01")]
    [InlineData("2026-02-30")]
    [InlineData("tomorrow")]
    public async Task An_unreadable_date_is_refused(string date)
    {
        var act = () => _acme.CreateRate("USD", "PKR", 278m, date);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*yyyy-MM-dd*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_date_is_required(string? date)
    {
        var act = () => _acme.CreateRate("USD", "PKR", 278m, date!);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*from which date*");
    }

    [Theory]
    [InlineData("XYZ", "PKR", "'XYZ' is not a currency")]
    [InlineData("USD", "ABC", "'ABC' is not a currency")]
    [InlineData("", "PKR", "Choose the currency to convert from")]
    [InlineData("USD", " ", "Choose the currency to convert to")]
    public async Task Both_currencies_must_be_in_the_lookups_catalog(string from, string to, string why)
    {
        var act = () => _acme.CreateRate(from, to, 1m, "2026-10-01");
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage($"*{why}*");
    }

    [Fact]
    public async Task A_currency_is_named_by_its_code_not_its_name()
    {
        var act = () => _acme.CreateRate("US Dollar", "PKR", 1m, "2026-10-01");
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*'US Dollar' is not a currency*");
    }

    [Fact]
    public async Task From_and_to_must_differ_whatever_the_case()
    {
        var act = () => _acme.CreateRate("usd", "USD", 1m, "2026-10-01");
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*two different currencies*");
    }

    [Theory]
    [InlineData("0", "greater than 0")]
    [InlineData("-1", "greater than 0")]
    [InlineData("0.000000001", "at most 8 decimals")]
    [InlineData("278.123456789", "at most 8 decimals")]
    [InlineData("10000000000", "too large")]
    public async Task A_bad_rate_is_refused(string rate, string why)
    {
        var act = () => _acme.CreateRate("USD", "PKR", decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture), "2026-10-01");
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage($"*{why}*");
    }

    [Theory]
    [InlineData("0.00000001")]
    [InlineData("0.00359066")]
    [InlineData("9999999999.99999999")]
    public async Task Rates_with_up_to_eight_decimals_and_ten_whole_digits_are_accepted(string rate)
    {
        var value = decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture);
        (await _acme.CreateRate("PKR", "USD", value, "2026-10-01")).Rate.Should().Be(value);
    }

    [Fact]
    public async Task Notes_longer_than_300_are_refused()
    {
        var act = () => _acme.CreateRate("USD", "PKR", 1m, "2026-10-01", new string('n', 301));
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*300*");
    }

    // ── One live rate per pair per day ───────────────────────────────────────

    [Fact]
    public async Task A_second_rate_for_the_same_pair_and_day_is_a_conflict_that_names_the_existing_rate()
    {
        await _acme.CreateRate("USD", "PKR", 278.5m, "2026-10-01");

        var act = () => _acme.CreateRate("usd", "pkr", 279m, "2026-10-01");

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*already a USD → PKR rate for 2026-10-01 (278.5)*");
    }

    [Fact]
    public async Task The_opposite_pair_another_day_or_another_organization_is_not_a_clash()
    {
        await _acme.CreateRate("USD", "PKR", 278.5m, "2026-10-01");

        await _acme.CreateRate("PKR", "USD", 0.00359066m, "2026-10-01");
        await _acme.CreateRate("USD", "PKR", 279m, "2026-10-02");
        await _globex.CreateRate("USD", "PKR", 280m, "2026-10-01");

        (await _acme.Rates(s => s.ListAsync(null, null))).Should().HaveCount(3);
        (await _globex.Rates(s => s.ListAsync(null, null))).Should().ContainSingle();
    }

    [Fact]
    public async Task Soft_delete_frees_the_day_for_a_corrected_rate_and_keeps_the_deleted_row()
    {
        var wrong = await _acme.CreateRate("USD", "PKR", 2785m, "2026-10-01");

        await _acme.Rates(s => s.DeleteAsync(wrong.Uuid, SetupWorld.User));
        var right = await _acme.CreateRate("USD", "PKR", 278.5m, "2026-10-01");

        (await _acme.Rates(s => s.ListAsync(null, null))).Should().ContainSingle().Which.Uuid.Should().Be(right.Uuid);

        await using var db = _acme.Finance();
        var deleted = await db.ExchangeRates.SingleAsync(r => r.Uuid == wrong.Uuid);
        deleted.IsDelete.Should().BeTrue();
        deleted.ModifiedBy.Should().Be(SetupWorld.User);
        deleted.ModifiedDate.Should().Be(TestClock.Start);
    }

    // ── Update and delete ────────────────────────────────────────────────────

    [Fact]
    public async Task An_update_changes_every_field_and_marks_the_rate_manual()
    {
        var rate = await _acme.CreateRate("USD", "PKR", 278.5m, "2026-10-01");
        await using (var db = _acme.Finance())
        {
            (await db.ExchangeRates.SingleAsync()).Source = "FEED";
            await db.SaveChangesAsync();
        }

        var updated = await _acme.Rates(s => s.UpdateAsync(rate.Uuid, Rate("eur", "usd", 1.0825m, "2026-10-03", "fixed"), SetupWorld.User));

        updated.Uuid.Should().Be(rate.Uuid);
        updated.FromCurrencyCode.Should().Be("EUR");
        updated.ToCurrencyCode.Should().Be("USD");
        updated.Rate.Should().Be(1.0825m);
        updated.EffectiveDate.Should().Be("2026-10-03");
        updated.Notes.Should().Be("fixed");
        updated.Source.Should().Be("MANUAL");
    }

    [Fact]
    public async Task An_update_onto_another_rates_day_is_a_conflict_but_keeping_its_own_day_is_not()
    {
        await _acme.CreateRate("USD", "PKR", 278m, "2026-10-01");
        var second = await _acme.CreateRate("USD", "PKR", 279m, "2026-10-02");

        var clash = () => _acme.Rates(s => s.UpdateAsync(second.Uuid, Rate("USD", "PKR", 279m, "2026-10-01"), SetupWorld.User));
        await clash.Should().ThrowAsync<ConflictException>();

        var same = await _acme.Rates(s => s.UpdateAsync(second.Uuid, Rate("USD", "PKR", 279.25m, "2026-10-02"), SetupWorld.User));
        same.Rate.Should().Be(279.25m);
    }

    [Fact]
    public async Task Updating_or_deleting_a_deleted_unknown_or_other_organizations_rate_is_not_found()
    {
        var theirs  = await _globex.CreateRate("USD", "PKR", 280m, "2026-10-01");
        var deleted = await _acme.CreateRate("USD", "PKR", 278m, "2026-10-01");
        await _acme.Rates(s => s.DeleteAsync(deleted.Uuid, SetupWorld.User));

        foreach (var uuid in new[] { theirs.Uuid, deleted.Uuid, Guid.NewGuid() })
        {
            var update = () => _acme.Rates(s => s.UpdateAsync(uuid, Rate("USD", "PKR", 1m, "2026-10-05"), SetupWorld.User));
            var delete = () => _acme.Rates(s => s.DeleteAsync(uuid, SetupWorld.User));
            await update.Should().ThrowAsync<NotFoundException>();
            await delete.Should().ThrowAsync<NotFoundException>();
        }

        (await _globex.Rates(s => s.ListAsync(null, null))).Single().Rate.Should().Be(280m);
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_is_newest_effective_date_first_and_filters_by_currency_in_any_case()
    {
        await _acme.CreateRate("USD", "PKR", 278m, "2026-09-01");
        await _acme.CreateRate("EUR", "PKR", 301m, "2026-10-01");
        await _acme.CreateRate("USD", "PKR", 279m, "2026-10-01");
        await _acme.CreateRate("PKR", "USD", 0.0036m, "2026-09-15");
        var gone = await _acme.CreateRate("USD", "EUR", 0.92m, "2026-10-05");
        await _acme.Rates(s => s.DeleteAsync(gone.Uuid, SetupWorld.User));

        var all = await _acme.Rates(s => s.ListAsync(null, null));
        all.Select(r => $"{r.EffectiveDate} {r.FromCurrencyCode}{r.ToCurrencyCode}").Should().Equal(
            "2026-10-01 EURPKR", "2026-10-01 USDPKR", "2026-09-15 PKRUSD", "2026-09-01 USDPKR");

        (await _acme.Rates(s => s.ListAsync("usd", null))).Select(r => r.Rate).Should().Equal(279m, 278m);
        (await _acme.Rates(s => s.ListAsync(null, " pkr "))).Should().HaveCount(3);
        (await _acme.Rates(s => s.ListAsync("USD", "PKR"))).Should().HaveCount(2);
        (await _acme.Rates(s => s.ListAsync("GBP", null))).Should().BeEmpty();
    }

    // ── Quote ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_quote_is_what_a_document_would_get_with_its_date_as_text()
    {
        await _acme.CreateRate("USD", "PKR", 278.5m, "2026-10-01");

        var direct   = await _acme.Rates(s => s.QuoteAsync("usd", "pkr", "2026-10-15"));
        var inverted = await _acme.Rates(s => s.QuoteAsync("PKR", "USD", "2026-10-15"));
        var before   = await _acme.Rates(s => s.QuoteAsync("USD", "PKR", "2026-09-30"));
        var same     = await _acme.Rates(s => s.QuoteAsync("EUR", "eur", "2026-09-30"));

        direct!.Rate.Should().Be(278.5m);
        direct.EffectiveDate.Should().Be("2026-10-01");
        direct.Inverted.Should().BeFalse();
        direct.FromCurrencyCode.Should().Be("USD");

        inverted!.Rate.Should().Be(0.00359066m);
        inverted.Inverted.Should().BeTrue();
        inverted.EffectiveDate.Should().Be("2026-10-01");

        before.Should().BeNull("the only rate starts the day after");

        same!.Rate.Should().Be(1m);
        same.EffectiveDate.Should().Be("2026-09-30");
    }

    [Fact]
    public async Task The_quote_defaults_to_today_and_needs_both_currencies()
    {
        await _acme.CreateRate("USD", "PKR", 278.5m, "2026-09-20");

        (await _acme.Rates(s => s.QuoteAsync("USD", "PKR", null)))!.EffectiveDate.Should().Be("2026-09-20", "the clock says 2026-09-20");

        var noFrom = () => _acme.Rates(s => s.QuoteAsync(null, "PKR", "2026-10-01"));
        var noTo   = () => _acme.Rates(s => s.QuoteAsync("USD", "", "2026-10-01"));
        var badDay = () => _acme.Rates(s => s.QuoteAsync("USD", "PKR", "10/01/2026"));
        await noFrom.Should().ThrowAsync<BadRequestException>();
        await noTo.Should().ThrowAsync<BadRequestException>();
        await badDay.Should().ThrowAsync<BadRequestException>();
    }
}
