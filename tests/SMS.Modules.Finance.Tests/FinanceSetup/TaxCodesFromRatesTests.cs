using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>"Create codes from rates already used": one SALES code per uncovered tax % on the organization's sale orders.</summary>
public class TaxCodesFromRatesTests
{
    private readonly SetupWorld _world = new();
    private readonly SetupDesk  _acme;
    private readonly SetupDesk  _globex;

    public TaxCodesFromRatesTests()
    {
        _acme   = _world.For(Guid.NewGuid());
        _globex = _world.For(Guid.NewGuid());
    }

    private Task<SMS.Modules.Finance.Services.TaxCodesFromRatesOutcome> Run(SetupDesk desk) =>
        desk.TaxCodes(s => s.CreateFromRatesInUseAsync(SetupWorld.User));

    [Fact]
    public async Task Creates_one_sales_code_per_distinct_rate_named_after_it()
    {
        await _acme.PlaceOrder(false, 17m, 17m, 0m);
        await _acme.PlaceOrder(false, 7.5m, 17.00m, 12.25m);

        var outcome = await Run(_acme);

        outcome.Result.Created.Select(c => (c.Code, c.Name, c.RatePercent)).Should().Equal(
            ("TAX0", "Tax 0%", 0m), ("TAX7_5", "Tax 7.5%", 7.5m), ("TAX12_25", "Tax 12.25%", 12.25m), ("TAX17", "Tax 17%", 17m));
        outcome.Result.Created.Should().OnlyContain(c => c.Usage == "SALES" && c.IsActive && !c.IsDefault);
        outcome.Result.SkippedRates.Should().BeEmpty();
        outcome.Message.Should().Contain("Created 4 sales tax codes").And.Contain("TAX7_5 (7.5%)");

        var listed = await _acme.ListCodes("SALES");
        listed.Select(c => c.Code).Should().BeEquivalentTo(["TAX0", "TAX7_5", "TAX12_25", "TAX17"]);

        await using var db = _acme.Finance();
        (await db.TaxCodes.AllAsync(t => t.CreatedBy == SetupWorld.User && t.OrganizationId == _acme.Org)).Should().BeTrue();
    }

    [Fact]
    public async Task Only_rates_no_active_sales_or_both_code_covers_are_created()
    {
        await _acme.PlaceOrder(false, 17m, 16m, 5m, 8m, 3m);
        await _acme.CreateCode("GST17", 17m, "SALES");
        await _acme.CreateCode("GST16", 16m, "BOTH");
        await _acme.CreateCode("WHT5", 5m, "PURCHASE");               // purchase-only does not cover sales
        await _acme.CreateCode("OLD8", 8m, "SALES", isActive: false); // inactive does not cover

        var outcome = await Run(_acme);

        outcome.Result.Created.Select(c => c.Code).Should().Equal("TAX3", "TAX5", "TAX8");
        outcome.Result.SkippedRates.Should().Equal(16m, 17m);
        outcome.Message.Should().Contain("Created 3 sales tax codes").And.Contain("2 rates already had a code");
    }

    [Fact]
    public async Task Asking_again_creates_nothing_new()
    {
        await _acme.PlaceOrder(false, 17m, 0m);
        (await Run(_acme)).Result.Created.Should().HaveCount(2);

        var again = await Run(_acme);

        again.Result.Created.Should().BeEmpty();
        again.Result.SkippedRates.Should().Equal(0m, 17m);
        again.Message.Should().Contain("All 2 tax rates used on sale orders already have a code");
        (await _acme.ListCodes(includeInactive: true)).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_taken_code_gets_a_numbered_variant()
    {
        await _acme.PlaceOrder(false, 17m, 5m);
        await _acme.CreateCode("TAX17", 16m, "SALES", isActive: false); // the name is taken, the rate is not covered
        await _acme.CreateCode("TAX5", 5m, "PURCHASE");                 // the name is taken, purchase-only

        var outcome = await Run(_acme);

        outcome.Result.Created.Select(c => c.Code).Should().Equal("TAX5-2", "TAX17-2");
    }

    [Fact]
    public async Task Deleted_sale_orders_and_other_organizations_orders_do_not_count()
    {
        await _acme.PlaceOrder(true, 9m);     // deleted
        await _globex.PlaceOrder(false, 4m);  // someone else's
        await _acme.PlaceOrder(false, 17m);

        var outcome = await Run(_acme);

        outcome.Result.Created.Select(c => c.RatePercent).Should().Equal(17m);
        (await _globex.ListCodes(includeInactive: true)).Should().BeEmpty("Globex's codes are Globex's to create");
    }

    [Fact]
    public async Task With_no_sale_orders_nothing_is_created_and_the_message_says_why()
    {
        var outcome = await Run(_acme);

        outcome.Result.Created.Should().BeEmpty();
        outcome.Result.SkippedRates.Should().BeEmpty();
        outcome.Message.Should().Contain("No sale order uses a tax rate yet");
    }

    [Fact]
    public async Task A_super_admin_creates_codes_from_their_own_organizations_orders_only()
    {
        await _globex.PlaceOrder(false, 4m);
        await _acme.PlaceOrder(false, 17m);

        var outcome = await _acme.TaxCodes(s => s.CreateFromRatesInUseAsync(SetupWorld.User), superAdmin: true);

        outcome.Result.Created.Select(c => c.RatePercent).Should().Equal(17m);
    }
}
