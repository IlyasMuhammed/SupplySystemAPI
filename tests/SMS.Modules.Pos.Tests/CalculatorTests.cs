using FluentAssertions;
using SMS.Modules.Pos.Services;
using SMS.Shared.Common;
using Xunit;
using Calc = SMS.Modules.Pos.Services.PosTotalsCalculator;

namespace SMS.Modules.Pos.Tests;

/// <summary>A38 API-CONTRACT §1 — the worked examples of the contract appendix (the PWA tests the same numbers).</summary>
public class CalculatorTests
{
    private static Calc.LineInput L(decimal qty, decimal price, string type = "NONE", decimal value = 0m) => new(qty, price, type, value);

    [Fact]
    public void E1_exclusive_no_discount()
    {
        var r = Calc.Calculate([L(2, 10.00m), L(1, 5.99m)], "NONE", 0m, 17m, false);
        r.Lines.Select(l => l.TaxAmount).Should().Equal(3.40m, 1.02m);
        r.Lines.Select(l => l.LineTotal).Should().Equal(23.40m, 7.01m);
        (r.Subtotal, r.DiscountAmount, r.TaxTotal, r.GrandTotal).Should().Be((25.99m, 0m, 4.42m, 30.41m));
    }

    [Fact]
    public void E2_exclusive_line_percent_discount()
    {
        var r = Calc.Calculate([L(3, 3.33m, "PERCENT", 10m)], "NONE", 0m, 17m, false);
        var l = r.Lines[0];
        (l.Gross, l.LineDiscount, l.NetAmount, l.TaxAmount, l.LineTotal).Should().Be((9.99m, 1.00m, 8.99m, 1.53m, 10.52m));
        r.GrandTotal.Should().Be(10.52m);
    }

    [Fact]
    public void E3_exclusive_order_percent_discount_allocated_proportionally()
    {
        var r = Calc.Calculate([L(2, 10.00m), L(1, 5.99m)], "PERCENT", 10m, 17m, false);
        r.DiscountAmount.Should().Be(2.60m);
        r.Lines.Select(l => l.OrderDiscountShare).Should().Equal(2.00m, 0.60m);
        r.Lines.Select(l => l.NetAmount).Should().Equal(18.00m, 5.39m);
        r.Lines.Select(l => l.TaxAmount).Should().Equal(3.06m, 0.92m);
        (r.Subtotal, r.TaxTotal, r.GrandTotal).Should().Be((25.99m, 3.98m, 27.37m));
        r.Lines.Sum(l => l.LineTotal).Should().Be(r.GrandTotal);
    }

    [Fact]
    public void E4_exclusive_order_amount_discount_remainder_on_last_line()
    {
        var r = Calc.Calculate([L(1, 3.33m), L(1, 3.33m), L(1, 3.33m)], "AMOUNT", 5.00m, 17m, false);
        r.Lines.Select(l => l.OrderDiscountShare).Should().Equal(1.67m, 1.67m, 1.66m);
        r.Lines.Select(l => l.NetAmount).Should().Equal(1.66m, 1.66m, 1.67m);
        r.Lines.Select(l => l.TaxAmount).Should().Equal(0.28m, 0.28m, 0.28m);
        (r.Subtotal, r.DiscountAmount, r.TaxTotal, r.GrandTotal).Should().Be((9.99m, 5.00m, 0.84m, 5.83m));
        r.Lines.Sum(l => l.LineTotal).Should().Be(5.83m);
    }

    [Fact]
    public void E5_inclusive_no_discount()
    {
        var r = Calc.Calculate([L(1, 11.70m), L(2, 4.99m)], "NONE", 0m, 17m, true);
        r.Lines.Select(l => l.TaxAmount).Should().Equal(1.70m, 1.45m);
        r.Lines.Select(l => l.NetAmount).Should().Equal(10.00m, 8.53m);
        r.Lines.Select(l => l.LineTotal).Should().Equal(11.70m, 9.98m);
        (r.Subtotal, r.TaxTotal, r.GrandTotal).Should().Be((21.68m, 3.15m, 21.68m));
    }

    [Fact]
    public void E6_inclusive_line_amount_and_order_percent_discount()
    {
        var r = Calc.Calculate([L(1, 11.70m, "AMOUNT", 1.00m), L(2, 4.99m)], "PERCENT", 5m, 17m, true);
        r.Subtotal.Should().Be(20.68m);
        r.DiscountAmount.Should().Be(1.03m);
        r.Lines.Select(l => l.OrderDiscountShare).Should().Equal(0.53m, 0.50m);
        r.Lines.Select(l => l.DiscountAmount).Should().Equal(1.53m, 0.50m);
        r.Lines.Select(l => l.TaxAmount).Should().Equal(1.48m, 1.38m);
        r.Lines.Select(l => l.NetAmount).Should().Equal(8.69m, 8.10m);
        r.Lines.Select(l => l.LineTotal).Should().Equal(10.17m, 9.48m);
        (r.TaxTotal, r.GrandTotal).Should().Be((2.86m, 19.65m));
    }

    [Fact]
    public void E7_midpoints_round_away_from_zero()
    {
        var r = Calc.Calculate([L(1.5m, 2.35m, "PERCENT", 50m)], "NONE", 0m, 17m, false);
        var l = r.Lines[0];
        (l.Gross, l.LineDiscount, l.NetAmount, l.TaxAmount, l.LineTotal).Should().Be((3.53m, 1.77m, 1.76m, 0.30m, 2.06m));
    }

    [Fact]
    public void Amount_discounts_are_capped_and_the_line_total_always_adds_up()
    {
        var r = Calc.Calculate([L(1, 2.00m, "AMOUNT", 5.00m), L(1, 3.00m)], "AMOUNT", 10.00m, 17m, false);
        r.Lines[0].LineDiscount.Should().Be(2.00m);
        r.DiscountAmount.Should().Be(3.00m);
        r.GrandTotal.Should().Be(0m);
        r.Lines.Should().OnlyContain(x => x.LineTotal == x.NetAmount + x.TaxAmount && x.NetAfter >= 0m);
    }

    [Fact]
    public void E8_tenders()
    {
        Calc.CheckTenders(27.37m, [new Calc.Tender(PosPaymentMethod.Cash, 30.00m, 2.63m)]).Should().BeNull();
        Calc.CheckTenders(27.37m, [new Calc.Tender(PosPaymentMethod.Card, 30.00m, 0m)]).Should().Contain("exceeds the remaining balance");
        Calc.CheckTenders(27.37m, [new Calc.Tender(PosPaymentMethod.Cash, 20.00m, 0m), new Calc.Tender(PosPaymentMethod.OnAccount, 7.37m, 0m)]).Should().BeNull();
        Calc.CheckTenders(27.37m, [new Calc.Tender(PosPaymentMethod.Cash, 27.36m, 0m)]).Should().BeNull(); // ±0.01
        Calc.CheckTenders(27.37m, [new Calc.Tender(PosPaymentMethod.Cash, 27.35m, 0m)]).Should().Contain("do not match");
        Calc.CheckTenders(27.37m, [new Calc.Tender(PosPaymentMethod.Card, 30.00m, 2.63m)]).Should().Be("Only cash payments can give change.");
        Calc.CheckTenders(27.37m, [new Calc.Tender("BITCOIN", 27.37m, 0m)]).Should().StartWith("Unknown payment method");
    }

    [Fact]
    public void Return_share_is_proportional_and_the_last_part_takes_the_remainder()
    {
        // Line: 3 units, tax 1.53, total 10.52 (E2). Return 1, then 1, then the last.
        var first = Calc.ReturnShare(3, 1.53m, 10.52m, 0, 0, 0, 1);
        first.Should().Be((3.00m, 0.51m, 3.51m));
        var second = Calc.ReturnShare(3, 1.53m, 10.52m, 1, 0.51m, 3.51m, 1);
        var last = Calc.ReturnShare(3, 1.53m, 10.52m, 2, 1.02m, 7.02m, 1);
        last.Should().Be((2.99m, 0.51m, 3.50m));
        (first.LineTotal + second.LineTotal + last.LineTotal).Should().Be(10.52m);
        (first.TaxAmount + second.TaxAmount + last.TaxAmount).Should().Be(1.53m);
    }
}
