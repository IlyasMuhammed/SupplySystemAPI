using FluentAssertions;
using SMS.Modules.Finance.ArAp.Receivables;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>A48 AR — BR-CR-001..005 and TS-CR-001..006 / 009 on the pure credit rules.</summary>
public class A48CreditRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 25);

    private static CreditStanding Standing(decimal? limit, decimal orders = 0m, decimal invoices = 0m,
        decimal? temp = null, DateOnly? expiry = null, bool hold = false, string? reason = null) =>
        new(limit, temp, expiry, hold, reason, orders, invoices);

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void TS_CR_001_no_credit_limit_means_unlimited(int? limit)
    {
        var d = CreditRules.Decide(CreditControlModes.Block, Standing(limit, orders: 1_000_000m), 5_000_000m, Today, "Acme", "PKR");
        d.Approved.Should().BeTrue();
        d.EffectiveLimit.Should().BeNull();
        d.AvailableCredit.Should().BeNull();
    }

    [Fact]
    public void TS_CR_002_customer_at_80_percent_within_limit_passes()
    {
        var d = CreditRules.Decide(CreditControlModes.Block, Standing(10_000m, orders: 3_000m, invoices: 5_000m), 1_500m, Today, "Acme", "PKR");
        d.Approved.Should().BeTrue();
        d.CurrentExposure.Should().Be(8_000m);
        d.ProjectedExposure.Should().Be(9_500m);
        d.AvailableCredit.Should().Be(2_000m);
    }

    [Fact]
    public void TS_CR_003_exceeding_the_limit_blocks_with_the_contract_text()
    {
        var d = CreditRules.Decide(CreditControlModes.Block, Standing(10_000m, invoices: 10_000m), 5_000m, Today, "Acme", "PKR");
        d.Approved.Should().BeFalse();
        d.OverLimit.Should().BeTrue();
        d.BlockReason.Should().Be("Exceeds the credit limit of 10,000.00 PKR by 5,000.00 PKR (exposure 10,000.00, this order 5,000.00).");
    }

    [Fact]
    public void Exactly_at_the_limit_passes()
    {
        CreditRules.Decide(CreditControlModes.Block, Standing(10_000m, invoices: 6_000m), 4_000m, Today, "Acme", "PKR")
            .Approved.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1_000_000)]
    public void TS_CR_004_hold_blocks_regardless_of_amount(int? limit)
    {
        var d = CreditRules.Decide(CreditControlModes.Block, Standing(limit, hold: true, reason: "Bounced cheques."), 1m, Today, "Acme", "PKR");
        d.Approved.Should().BeFalse();
        d.BlockReason.Should().Be("Acme is on credit hold: Bounced cheques.");
    }

    [Fact]
    public void Hold_blocks_in_warn_mode_too_but_the_limit_only_warns()
    {
        CreditRules.Decide(CreditControlModes.Warn, Standing(100m, hold: true), 1m, Today, "Acme", "PKR").Approved.Should().BeFalse();

        var over = CreditRules.Decide(CreditControlModes.Warn, Standing(100m, invoices: 90m), 50m, Today, "Acme", "PKR");
        over.Approved.Should().BeTrue();
        over.Warning.Should().StartWith("Exceeds the credit limit of 100.00 PKR by 40.00 PKR");
        over.BlockReason.Should().BeNull();
    }

    [Fact]
    public void Off_mode_approves_without_computing()
    {
        var d = CreditRules.Decide(CreditControlModes.Off, Standing(1m, invoices: 1_000m, hold: true), 1_000m, Today, "Acme", "PKR");
        d.Approved.Should().BeTrue();
        d.CurrentExposure.Should().Be(0m);
        d.EffectiveLimit.Should().BeNull();
    }

    [Fact]
    public void TS_CR_005_live_temporary_limit_above_permanent_is_used()
    {
        var s = Standing(10_000m, invoices: 9_000m, temp: 20_000m, expiry: Today.AddDays(10));
        var d = CreditRules.Decide(CreditControlModes.Block, s, 5_000m, Today, "Acme", "PKR");
        d.Approved.Should().BeTrue();
        d.EffectiveLimit.Should().Be(20_000m);
        d.TemporaryActive.Should().BeTrue();
    }

    [Fact]
    public void Temporary_limit_counts_on_its_expiry_day()
    {
        CreditRules.EffectiveLimit(10_000m, 20_000m, Today, Today).Should().Be(20_000m);
    }

    [Fact]
    public void TS_CR_006_expired_temporary_limit_falls_back_to_permanent()
    {
        var s = Standing(10_000m, invoices: 9_000m, temp: 20_000m, expiry: Today.AddDays(-1));
        var d = CreditRules.Decide(CreditControlModes.Block, s, 5_000m, Today, "Acme", "PKR");
        d.Approved.Should().BeFalse();
        d.EffectiveLimit.Should().Be(10_000m);
        d.TemporaryActive.Should().BeFalse();
    }

    [Fact]
    public void BR_CR_002_effective_limit_is_the_max_of_the_two()
    {
        CreditRules.EffectiveLimit(10_000m, 5_000m, Today.AddDays(5), Today).Should().Be(10_000m);
        CreditRules.EffectiveLimit(null, 5_000m, Today.AddDays(5), Today).Should().BeNull();
    }

    [Fact]
    public void TS_CR_009_exposure_is_open_orders_plus_open_invoices()
    {
        CreditRules.Exposure(Standing(1m, orders: 1_250.50m, invoices: 749.50m)).Should().Be(2_000m);
    }
}
