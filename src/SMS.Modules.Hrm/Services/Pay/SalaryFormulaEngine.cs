using System.Globalization;

namespace SMS.Modules.Hrm.Services.Pay;

/// <summary>A formula that cannot be parsed or evaluated. The message is safe to show: it never holds a value.</summary>
public sealed class SalaryFormulaException : Exception
{
    public SalaryFormulaException(string message) : base(message) { }
}

/// <summary>What a formula sees. <see cref="Slab"/> is the progressive table (annual income → annual tax); null = SLAB is refused.</summary>
public sealed class FormulaContext
{
    public IReadOnlyDictionary<string, decimal> Variables { get; init; } = new Dictionary<string, decimal>();
    public Func<decimal, decimal>? Slab { get; init; }
}

/// <summary>
/// Tasks 175–180. Salary formulas: decimals only, a hand-written parser into a small tree. There is no eval, no compilation, no
/// reflection, no dynamic LINQ: the only things a formula can name are the variables it is given and five functions, so it cannot
/// reach a file, a network or a type. Length, nesting, node count and argument count are limited, evaluation is one walk of a bounded
/// tree (no loops, no recursion in the language), and overflow is an error, not a wrong number.
/// </summary>
public interface ISalaryFormulaEngine
{
    /// <summary>Parses, throwing <see cref="SalaryFormulaException"/>; returns the upper-cased variable names it uses.</summary>
    IReadOnlySet<string> Validate(string formula);
    decimal Evaluate(string formula, FormulaContext context);
}

internal sealed class SalaryFormulaEngine : ISalaryFormulaEngine
{
    public const int MaxLength = 1000, MaxDepth = 32, MaxNodes = 400, MaxArguments = 16, MaxRoundDigits = 6;

    public static readonly IReadOnlySet<string> BuiltInVariables = new HashSet<string>(StringComparer.Ordinal)
        { "BASIC", "GROSS", "TOTAL_EARNINGS", "TAXABLE_INCOME", "WORKING_DAYS", "PAID_DAYS", "OT_HOURS" };

    private static readonly IReadOnlySet<string> Functions = new HashSet<string>(StringComparer.Ordinal) { "MIN", "MAX", "IF", "ROUND", "SLAB" };

    public IReadOnlySet<string> Validate(string formula)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        new Parser(formula).Parse().CollectVariables(names);
        return names;
    }

    public decimal Evaluate(string formula, FormulaContext context)
    {
        var root = new Parser(formula).Parse();
        try
        {
            return root.Eval(context);
        }
        catch (OverflowException)
        {
            throw new SalaryFormulaException("The formula's result is too large.");
        }
        catch (DivideByZeroException)
        {
            throw new SalaryFormulaException("The formula divides by zero.");
        }
    }

    // ── tree ───────────────────────────────────────────────────────────────────────────────────────────────────────

    private abstract class Node
    {
        public abstract decimal Eval(FormulaContext c);
        public virtual void CollectVariables(HashSet<string> names) { }
    }

    private sealed class Number(decimal value) : Node
    {
        public override decimal Eval(FormulaContext c) => value;
    }

    private sealed class Variable(string name) : Node
    {
        public override decimal Eval(FormulaContext c) =>
            c.Variables.TryGetValue(name, out var value) ? value : throw new SalaryFormulaException($"Unknown variable '{name}'.");
        public override void CollectVariables(HashSet<string> names) => names.Add(name);
    }

    private sealed class Negate(Node inner) : Node
    {
        public override decimal Eval(FormulaContext c) => -inner.Eval(c);
        public override void CollectVariables(HashSet<string> names) => inner.CollectVariables(names);
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override decimal Eval(FormulaContext c)
        {
            var l = left.Eval(c);
            var r = right.Eval(c);
            return op switch
            {
                "+" => l + r,
                "-" => l - r,
                "*" => l * r,
                "/" => l / r,
                ">" => l > r ? 1m : 0m,
                "<" => l < r ? 1m : 0m,
                ">=" => l >= r ? 1m : 0m,
                "<=" => l <= r ? 1m : 0m,
                "=" => l == r ? 1m : 0m,
                "<>" => l != r ? 1m : 0m,
                _ => throw new SalaryFormulaException($"Unknown operator '{op}'.")
            };
        }

        public override void CollectVariables(HashSet<string> names)
        {
            left.CollectVariables(names);
            right.CollectVariables(names);
        }
    }

    private sealed class Call(string name, IReadOnlyList<Node> args) : Node
    {
        public override decimal Eval(FormulaContext c)
        {
            switch (name)
            {
                case "MIN": return args.Select(a => a.Eval(c)).Min();
                case "MAX": return args.Select(a => a.Eval(c)).Max();
                // Only the branch taken runs: IF(PAID_DAYS > 0, X / PAID_DAYS, 0) is safe.
                case "IF": return args[0].Eval(c) != 0m ? args[1].Eval(c) : args[2].Eval(c);
                case "ROUND":
                    var digits = args.Count == 2 ? args[1].Eval(c) : 0m;
                    if (digits != decimal.Truncate(digits) || digits < 0 || digits > MaxRoundDigits)
                        throw new SalaryFormulaException($"ROUND takes 0 to {MaxRoundDigits} decimal places.");
                    return Math.Round(args[0].Eval(c), (int)digits, MidpointRounding.AwayFromZero);
                case "SLAB":
                    if (c.Slab is null) throw new SalaryFormulaException("No tax table is available to SLAB.");
                    return c.Slab(args[0].Eval(c));
                default: throw new SalaryFormulaException($"Unknown function '{name}'.");
            }
        }

        public override void CollectVariables(HashSet<string> names)
        {
            foreach (var a in args) a.CollectVariables(names);
        }
    }

    // ── parser: comparison → additive → multiplicative → unary → primary ───────────────────────────────────────────

    private sealed class Parser
    {
        private readonly string _s;
        private int _i, _depth, _nodes;

        public Parser(string? formula)
        {
            if (string.IsNullOrWhiteSpace(formula)) throw new SalaryFormulaException("The formula is empty.");
            if (formula.Length > MaxLength) throw new SalaryFormulaException($"A formula cannot be longer than {MaxLength} characters.");
            _s = formula;
        }

        public Node Parse()
        {
            var node = Comparison();
            SkipSpace();
            if (_i < _s.Length) throw new SalaryFormulaException($"Unexpected '{Printable(_s[_i])}' at position {_i + 1}.");
            return node;
        }

        private T Counted<T>(T node) where T : Node
        {
            if (++_nodes > MaxNodes) throw new SalaryFormulaException("The formula is too long.");
            return node;
        }

        private void Enter()
        {
            if (++_depth > MaxDepth) throw new SalaryFormulaException("The formula is nested too deeply.");
        }

        private void SkipSpace()
        {
            while (_i < _s.Length && _s[_i] is ' ' or '\t' or '\r' or '\n') _i++;
        }

        private bool Accept(string token)
        {
            SkipSpace();
            if (string.CompareOrdinal(_s, _i, token, 0, token.Length) != 0) return false;
            _i += token.Length;
            return true;
        }

        private Node Comparison()
        {
            var left = Additive();
            foreach (var op in new[] { ">=", "<=", "<>", ">", "<", "=" })
                if (Accept(op)) return Counted(new Binary(op, left, Additive()));
            return left;
        }

        private Node Additive()
        {
            var left = Multiplicative();
            while (true)
            {
                if (Accept("+")) left = Counted(new Binary("+", left, Multiplicative()));
                else if (Accept("-")) left = Counted(new Binary("-", left, Multiplicative()));
                else return left;
            }
        }

        private Node Multiplicative()
        {
            var left = Unary();
            while (true)
            {
                if (Accept("*")) left = Counted(new Binary("*", left, Unary()));
                else if (Accept("/")) left = Counted(new Binary("/", left, Unary()));
                else return left;
            }
        }

        private Node Unary()
        {
            Enter();
            try
            {
                if (Accept("-")) return Counted(new Negate(Unary()));
                if (Accept("+")) return Unary();
                return Primary();
            }
            finally { _depth--; }
        }

        private Node Primary()
        {
            SkipSpace();
            if (_i >= _s.Length) throw new SalaryFormulaException("The formula ends too early.");
            var ch = _s[_i];
            if (ch == '(')
            {
                _i++;
                var inner = Comparison();
                if (!Accept(")")) throw new SalaryFormulaException("A closing bracket is missing.");
                return inner;
            }
            if (char.IsAsciiDigit(ch) || ch == '.') return Counted(new Number(ReadNumber()));
            if (char.IsAsciiLetter(ch) || ch == '_') return Identifier();
            throw new SalaryFormulaException($"Unexpected '{Printable(ch)}' at position {_i + 1}.");
        }

        private decimal ReadNumber()
        {
            var start = _i;
            while (_i < _s.Length && (char.IsAsciiDigit(_s[_i]) || _s[_i] == '.')) _i++;
            var text = _s[start.._i];
            // NumberStyles.AllowDecimalPoint only: no exponent, sign, currency or thousands, whatever the culture.
            if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                throw new SalaryFormulaException($"'{Printable(text)}' is not a number.");
            return value;
        }

        private Node Identifier()
        {
            var start = _i;
            while (_i < _s.Length && (char.IsAsciiLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            var name = _s[start.._i].ToUpperInvariant();
            if (name.Length > 40) throw new SalaryFormulaException("A name in a formula is too long.");

            SkipSpace();
            if (_i >= _s.Length || _s[_i] != '(')
            {
                if (Functions.Contains(name)) throw new SalaryFormulaException($"{name} needs its arguments in brackets.");
                return Counted(new Variable(name));
            }

            if (!Functions.Contains(name)) throw new SalaryFormulaException($"Unknown function '{Printable(name)}'.");
            _i++;
            var args = new List<Node>();
            if (!Accept(")"))
            {
                do
                {
                    if (args.Count == MaxArguments) throw new SalaryFormulaException($"{name} has too many arguments.");
                    args.Add(Comparison());
                }
                while (Accept(","));
                if (!Accept(")")) throw new SalaryFormulaException("A closing bracket is missing.");
            }
            var ok = name switch
            {
                "MIN" or "MAX" => args.Count >= 1,
                "IF" => args.Count == 3,
                "ROUND" => args.Count is 1 or 2,
                _ => args.Count == 1
            };
            if (!ok) throw new SalaryFormulaException($"{name} has the wrong number of arguments.");
            return Counted(new Call(name, args));
        }

        private static string Printable(char c) => char.IsControl(c) ? "?" : c.ToString();
        private static string Printable(string s) => s.Length > 40 ? s[..40] : s;
    }
}

/// <summary>Task 179 — the progressive table as a function. One bracket: tax = FixedAmount + Rate% × (income − FromAmount), in [From, To).</summary>
internal static class TaxSlabCalculator
{
    public static decimal Annual(IReadOnlyCollection<Domain.TaxSlab> slabs, decimal annualIncome)
    {
        if (annualIncome <= 0m) return 0m;
        var slab = slabs.Where(s => s.FromAmount <= annualIncome && (s.ToAmount is null || annualIncome < s.ToAmount))
            .OrderByDescending(s => s.FromAmount).FirstOrDefault()
            ?? throw new SalaryFormulaException("The tax table has no bracket for this income.");
        return slab.FixedAmount + slab.RatePercent * (annualIncome - slab.FromAmount) / 100m;
    }
}
