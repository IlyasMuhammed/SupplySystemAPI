using FluentAssertions;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests.FinanceSetup;

/// <summary>ITaxCodeLookup as Demand sees it: a code by id (active or not), the active codes of a side, the side's default.</summary>
public class TaxCodeLookupTests
{
    private readonly SetupWorld _world = new();
    private readonly SetupDesk  _acme;
    private readonly SetupDesk  _globex;

    public TaxCodeLookupTests()
    {
        _acme   = _world.For(Guid.NewGuid());
        _globex = _world.For(Guid.NewGuid());
    }

    [Fact]
    public async Task A_code_is_found_by_id_active_or_not_but_not_another_organizations()
    {
        var active   = (await _acme.CreateCode("GST17", 17m, "SALES", isDefault: true, name: "GST 17%")).TaxCode;
        var inactive = (await _acme.CreateCode("OLD", 16m, "PURCHASE", isActive: false)).TaxCode;
        var theirs   = (await _globex.CreateCode("G1", 5m)).TaxCode;

        (await _acme.Lookup(l => l.GetAsync(active.Uuid)))
            .Should().Be(new TaxCodeInfo(active.Uuid, "GST17", "GST 17%", 17m, "SALES", true, true));
        (await _acme.Lookup(l => l.GetAsync(inactive.Uuid)))!.IsActive.Should().BeFalse();
        (await _acme.Lookup(l => l.GetAsync(theirs.Uuid))).Should().BeNull();
        (await _acme.Lookup(l => l.GetAsync(theirs.Uuid), superAdmin: true)).Should().BeNull();
        (await _acme.Lookup(l => l.GetAsync(Guid.NewGuid()))).Should().BeNull();
        (await _acme.Lookup(l => l.GetAsync(Guid.Empty))).Should().BeNull();
    }

    [Fact]
    public async Task The_active_codes_of_a_side_include_both_codes_default_first_then_by_code()
    {
        await _acme.CreateCode("ZERO", 0m, "SALES");
        await _acme.CreateCode("BOTH16", 16m, "BOTH");
        await _acme.CreateCode("PUR5", 5m, "PURCHASE", isDefault: true);
        await _acme.CreateCode("SAL17", 17m, "SALES", isDefault: true);
        await _acme.CreateCode("OFF", 10m, "SALES", isActive: false);
        await _globex.CreateCode("AAA", 1m, "SALES");

        (await _acme.Lookup(l => l.ListActiveAsync(TaxCodeUsage.Sales))).Select(c => c.Code)
            .Should().Equal("SAL17", "BOTH16", "ZERO");
        (await _acme.Lookup(l => l.ListActiveAsync("purchase"))).Select(c => c.Code)
            .Should().Equal("PUR5", "BOTH16");
    }

    [Fact]
    public async Task The_default_of_a_side_may_be_a_both_code_and_an_inactive_code_is_never_the_default()
    {
        (await _acme.Lookup(l => l.GetDefaultAsync(TaxCodeUsage.Sales))).Should().BeNull();

        await _acme.CreateCode("B1", 15m, "BOTH", isDefault: true);
        (await _acme.Lookup(l => l.GetDefaultAsync(TaxCodeUsage.Sales)))!.Code.Should().Be("B1");
        (await _acme.Lookup(l => l.GetDefaultAsync(TaxCodeUsage.Purchase)))!.Code.Should().Be("B1");

        // A row left default but inactive (not something the service writes) is still not offered.
        await using (var db = _acme.Finance())
        {
            var b1 = db.TaxCodes.Single();
            b1.IsActive = false;
            await db.SaveChangesAsync();
        }
        (await _acme.Lookup(l => l.GetDefaultAsync(TaxCodeUsage.Sales))).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_default_is_not_this_ones()
    {
        await _globex.CreateCode("G1", 5m, "BOTH", isDefault: true);

        (await _acme.Lookup(l => l.GetDefaultAsync(TaxCodeUsage.Sales))).Should().BeNull();
        (await _acme.Lookup(l => l.GetDefaultAsync(TaxCodeUsage.Sales), superAdmin: true)).Should().BeNull();
    }

    [Theory]
    [InlineData("BOTH")]
    [InlineData("")]
    [InlineData("INVOICE")]
    public async Task A_side_must_be_sales_or_purchase(string side)
    {
        var list = () => _acme.Lookup(l => l.ListActiveAsync(side));
        var dflt = () => _acme.Lookup(l => l.GetDefaultAsync(side));
        await list.Should().ThrowAsync<ArgumentException>();
        await dflt.Should().ThrowAsync<ArgumentException>();
    }
}
