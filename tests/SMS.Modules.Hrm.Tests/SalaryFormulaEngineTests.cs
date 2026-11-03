using System.Diagnostics;
using FluentAssertions;
using SMS.Modules.Hrm.Domain;
using SMS.Modules.Hrm.Services.Pay;
using Xunit;

namespace SMS.Modules.Hrm.Tests;

/// <summary>Tasks 175–184: arithmetic, functions, SLAB, and input meant to escape, hang or crash the evaluator.</summary>
public sealed class SalaryFormulaEngineTests
{
    private readonly SalaryFormulaEngine _engine = new();

    private static readonly TaxSlab[] Brackets =
    [
        new() { FromAmount = 0m, ToAmount = 600_000m, FixedAmount = 0m, RatePercent = 0m },
        new() { FromAmount = 600_000m, ToAmount = 1_200_000m, FixedAmount = 0m, RatePercent = 1m },
        new() { FromAmount = 1_200_000m, ToAmount = 2_200_000m, FixedAmount = 6_000m, RatePercent = 11m },
        new() { FromAmount = 2_200_000m, FixedAmount = 116_000m, RatePercent = 23m },
    ];

    private decimal Eval(string formula, params (string, decimal)[] vars) => _engine.Evaluate(formula, new FormulaContext
    {
        Variables = vars.ToDictionary(v => v.Item1, v => v.Item2),
        Slab = income => TaxSlabCalculator.Annual(Brackets, income)
    });

    [Theory]
    [InlineData("1 + 2 * 3", 7)]
    [InlineData("(1 + 2) * 3", 9)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("-5 + 2", -3)]
    [InlineData("2 - -2", 4)]
    [InlineData("MIN(3, 1, 2)", 1)]
    [InlineData("MAX(3, 1, 2)", 3)]
    [InlineData("ROUND(2.345, 2)", 2.35)]
    [InlineData("ROUND(2.5)", 3)]
    [InlineData("ROUND(-2.5)", -3)]
    [InlineData("IF(1 > 0, 10, 20)", 10)]
    [InlineData("IF(1 = 2, 10, 20)", 20)]
    [InlineData("IF(1 <> 2, 10, 20)", 10)]
    public void Arithmetic_and_functions(string formula, double expected) => Eval(formula).Should().Be((decimal)expected);

    [Fact]
    public void Decimal_arithmetic_is_exact_where_a_double_would_not_be() => Eval("0.1 + 0.2").Should().Be(0.3m);

    [Fact]
    public void Variables_are_case_insensitive_and_task_178_example_works()
    {
        Eval("basic * 0.45", ("BASIC", 100_000m)).Should().Be(45_000m);
        Eval("BASIC * PAID_DAYS / WORKING_DAYS", ("BASIC", 31_000m), ("PAID_DAYS", 16m), ("WORKING_DAYS", 31m)).Should().Be(16_000m);
    }

    [Fact]
    public void An_untaken_branch_does_not_run() => Eval("IF(PAID_DAYS > 0, BASIC / PAID_DAYS, 0)", ("BASIC", 100m), ("PAID_DAYS", 0m)).Should().Be(0m);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(500_000, 0)]
    [InlineData(600_000, 0)]
    [InlineData(900_000, 3_000)]
    [InlineData(1_200_000, 6_000)]
    [InlineData(1_500_000, 39_000)]
    [InlineData(2_200_000, 116_000)]
    [InlineData(3_000_000, 300_000)]
    public void Slab_is_progressive_at_and_between_the_edges(int income, int tax) =>
        Eval("SLAB(TAXABLE_INCOME)", ("TAXABLE_INCOME", income)).Should().Be(tax);

    [Fact]
    public void Slab_of_negative_income_is_zero_and_a_gap_is_an_error()
    {
        Eval("SLAB(-5)").Should().Be(0m);
        var gap = new[] { new TaxSlab { FromAmount = 0m, ToAmount = 10m, RatePercent = 1m } };
        var act = () => TaxSlabCalculator.Annual(gap, 50m);
        act.Should().Throw<SalaryFormulaException>();
    }

    [Theory]
    // Reaching out of the language
    [InlineData("System.IO.File.ReadAllText(\"c:\\\\windows\\\\win.ini\")")]
    [InlineData("File.Delete('x')")]
    [InlineData("GetType()")]
    [InlineData("BASIC.GetType()")]
    [InlineData("typeof(string)")]
    [InlineData("Activator.CreateInstance(\"x\")")]
    [InlineData("new System.Net.WebClient()")]
    [InlineData("Process.Start(\"cmd\")")]
    [InlineData("EVAL(\"1\")")]
    [InlineData("EXEC(1)")]
    [InlineData("Assembly.Load(1)")]
    // Injection shapes
    [InlineData("1; DROP TABLE hrm.PayrollRuns")]
    [InlineData("1 -- comment")]
    [InlineData("1 /* c */ + 1")]
    [InlineData("'1' + 1")]
    [InlineData("\"1\"")]
    [InlineData("<script>")]
    [InlineData("${7*7}")]
    [InlineData("{{7*7}}")]
    [InlineData("@@version")]
    // Malformed
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(1 + 2")]
    [InlineData("1 + 2)")]
    [InlineData("1 +")]
    [InlineData("* 2")]
    [InlineData("MIN()")]
    [InlineData("IF(1, 2)")]
    [InlineData("ROUND(1, 2, 3)")]
    [InlineData("SLAB(1, 2)")]
    [InlineData("MIN")]
    [InlineData("1e308")]
    [InlineData("1.2.3")]
    [InlineData("0x10")]
    public void Malicious_or_malformed_input_is_refused(string formula)
    {
        var act = () => _engine.Evaluate(formula, new FormulaContext { Variables = new Dictionary<string, decimal> { ["BASIC"] = 1m } });
        act.Should().Throw<SalaryFormulaException>();
    }

    [Fact]
    public void Unknown_variable_is_an_error_not_zero()
    {
        var act = () => Eval("NOT_A_VARIABLE + 1");
        act.Should().Throw<SalaryFormulaException>().WithMessage("*NOT_A_VARIABLE*");
    }

    [Fact]
    public void Divide_by_zero_and_overflow_are_errors_not_wrong_numbers()
    {
        var zero = () => Eval("1 / 0");
        zero.Should().Throw<SalaryFormulaException>().WithMessage("*zero*");
        var big = () => Eval("79228162514264337593543950335 * 79228162514264337593543950335");
        big.Should().Throw<SalaryFormulaException>().WithMessage("*too large*");
    }

    [Theory]
    [InlineData(100_000, '(')]
    [InlineData(100_000, '-')]
    [InlineData(800, '-')]
    [InlineData(800, '(')]
    public void Deep_nesting_is_refused_without_overflowing_the_stack(int count, char ch)
    {
        var act = () => _engine.Validate(new string(ch, count) + "1");
        act.Should().Throw<SalaryFormulaException>();
    }

    [Fact]
    public void Nesting_within_the_limit_is_accepted() => Eval(new string('(', 20) + "1" + new string(')', 20)).Should().Be(1m);

    [Fact]
    public void Size_limits_hold()
    {
        var tooLong = () => _engine.Validate("1" + string.Concat(Enumerable.Repeat("+1", 600)));
        tooLong.Should().Throw<SalaryFormulaException>();
        var tooMany = () => _engine.Validate("1" + string.Concat(Enumerable.Repeat("+1", 450)));
        tooMany.Should().Throw<SalaryFormulaException>();
        var args = () => _engine.Validate("MIN(" + string.Join(",", Enumerable.Repeat("1", 17)) + ")");
        args.Should().Throw<SalaryFormulaException>();
    }

    [Fact]
    public void Worst_case_input_finishes_quickly()
    {
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
            _engine.Evaluate("MAX(1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16) * ROUND(MIN(1,2) + IF(1 > 0, 2, 3), 2) / (1 + 1)", new FormulaContext());
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Round_refuses_a_silly_precision()
    {
        var act = () => Eval("ROUND(1.5, 99)");
        act.Should().Throw<SalaryFormulaException>();
        var fraction = () => Eval("ROUND(1.5, 1.5)");
        fraction.Should().Throw<SalaryFormulaException>();
    }

    [Fact]
    public void Slab_without_a_table_is_refused_and_validate_lists_the_names()
    {
        var act = () => _engine.Evaluate("SLAB(1)", new FormulaContext());
        act.Should().Throw<SalaryFormulaException>();
        _engine.Validate("BASIC + hrent * MIN(OT_HOURS, 2)").Should().BeEquivalentTo("BASIC", "HRENT", "OT_HOURS");
    }
}
