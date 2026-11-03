using FluentAssertions;
using SMS.Modules.Finance.ArAp.Receivables;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>A48 AR — BR-DN-001..004 / TS-DN-001..005 on the pure dunning rules, and the template engine (task 242).</summary>
public class A48DunningRulesTests
{
    private static readonly DateTime AsOf = new(2026, 10, 25);

    private static DunningProcedureRule Procedure(int grace = 5, decimal minimum = 0m) =>
        new(grace, minimum, [new(1, 7), new(2, 14), new(3, 30), new(4, 14)]);

    private static DunningOpenItem Item(int daysOverdue, decimal open = 100m, string number = "SINV-1") =>
        new(Guid.NewGuid(), number, AsOf.AddDays(-daysOverdue - 30), AsOf.AddDays(-daysOverdue), "PKR", open, open);

    [Fact]
    public void TS_DN_001_overdue_12_days_grace_5_level1_at_day_7_is_eligible()
    {
        var e = DunningRules.Evaluate(Procedure(), 0, null, [Item(12)], AsOf);
        e.Eligible.Should().BeTrue();
        e.ProposedLevel.Should().Be(1);
        e.DaysOverdue.Should().Be(12);
    }

    [Fact]
    public void TS_DN_002_overdue_3_days_grace_5_is_not_eligible()
    {
        var e = DunningRules.Evaluate(Procedure(), 0, null, [Item(3)], AsOf);
        e.Eligible.Should().BeFalse();
        e.SkipReason.Should().Contain("grace");
    }

    [Fact]
    public void Level1_waits_for_its_own_days_even_past_grace()
    {
        var e = DunningRules.Evaluate(Procedure(grace: 2), 0, null, [Item(6)], AsOf);
        e.Eligible.Should().BeFalse();
        e.SkipReason.Should().Contain("level 1 starts at 7");
    }

    [Fact]
    public void TS_DN_003_level2_20_days_since_last_level3_at_30_not_yet()
    {
        var e = DunningRules.Evaluate(Procedure(), 2, AsOf.AddDays(-20), [Item(60)], AsOf);
        e.Eligible.Should().BeFalse();
        e.ProposedLevel.Should().Be(3);
    }

    [Fact]
    public void TS_DN_004_level2_35_days_since_last_is_eligible_for_level3()
    {
        var e = DunningRules.Evaluate(Procedure(), 2, AsOf.AddDays(-35), [Item(60)], AsOf);
        e.Eligible.Should().BeTrue();
        e.ProposedLevel.Should().Be(3);
    }

    [Fact]
    public void TS_DN_005_total_overdue_below_minimum_is_skipped()
    {
        var e = DunningRules.Evaluate(Procedure(minimum: 50m), 0, null, [Item(20, 30m)], AsOf);
        e.Eligible.Should().BeFalse();
        e.SkipReason.Should().Contain("below the procedure's minimum");
    }

    [Fact]
    public void BR_DN_004_never_skips_a_level_however_long_ago()
    {
        var e = DunningRules.Evaluate(Procedure(), 1, AsOf.AddDays(-400), [Item(500)], AsOf);
        e.ProposedLevel.Should().Be(2);
    }

    [Fact]
    public void At_the_last_level_nothing_further_is_proposed()
    {
        var e = DunningRules.Evaluate(Procedure(), 4, AsOf.AddDays(-100), [Item(200)], AsOf);
        e.Eligible.Should().BeFalse();
        e.SkipReason.Should().Contain("last level");
    }

    [Fact]
    public void Only_items_past_grace_count_toward_the_total()
    {
        var e = DunningRules.Evaluate(Procedure(minimum: 150m), 0, null, [Item(20, 100m), Item(2, 100m, "SINV-2")], AsOf);
        e.Eligible.Should().BeFalse();
        e.OverdueBase.Should().Be(100m);
    }

    [Fact]
    public void Template_renders_every_placeholder()
    {
        var items = new[] { Item(20, 1234.5m, "SINV-20261001-0001"), Item(10, 100m, "SINV-<2>") };
        var model = new DunningTemplateModel("Acme & Sons", "C-001", 2, 1334.5m, "PKR", AsOf.AddDays(-20), items, 50m);
        var engine = new DunningTemplateEngine();

        var subject = engine.RenderSubject("Level {DunningLevel}: {CustomerName} ({CustomerCode}) owes {TotalOverdue} — {InvoiceList}", model);
        subject.Should().Be("Level 2: Acme & Sons (C-001) owes 1,334.50 PKR — SINV-20261001-0001, SINV-<2>");

        var body = engine.RenderBody("<p>{CustomerName}: {Amount} due since {OldestDueDate} / {DueDate}, fee {Charge}.</p>{InvoiceList}{Unknown}", model);
        body.Should().StartWith("<p>Acme &amp; Sons: 1,334.50 PKR due since 05 Oct 2026 / 05 Oct 2026, fee 50.00 PKR.</p><table");
        body.Should().Contain("SINV-&lt;2&gt;").And.Contain("1,234.50 PKR").And.EndWith("</table>{Unknown}");
    }
}
