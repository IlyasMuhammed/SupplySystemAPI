using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Finance.Integration.Posting;

/// <summary>
/// A44 (API-CONTRACT §3) — builds a posting's Dr/Cr lines in document currency. Every amount is rounded to the cent
/// (<see cref="MidpointRounding.AwayFromZero"/>, as SCM rounds), zero lines are dropped, a negative amount goes to the
/// other side, and <see cref="Build"/> puts whatever the rounding left between the two sides on DEFAULT_ROUNDING, so
/// the lines always balance (BR-01) — a builder that would need more than a few cents of that is a bug, not rounding.
/// </summary>
internal sealed class GlLines
{
    /// <summary>The most a balanced document may differ by before <see cref="Build"/> refuses it.</summary>
    internal const decimal MaxRoundingDifference = 0.05m;

    private readonly List<(string Role, decimal Debit, decimal Credit, string? TaxCode, decimal? TaxAmount, string? Label, Guid? Partner)> _lines = [];
    private readonly Guid? _partner;

    public GlLines(Guid? partner = null) => _partner = partner;

    public GlLines Debit(string role, decimal amount, string? label = null, string? taxCode = null) =>
        Add(role, amount, label, taxCode, debit: true);

    public GlLines Credit(string role, decimal amount, string? label = null, string? taxCode = null) =>
        Add(role, amount, label, taxCode, debit: false);

    private GlLines Add(string role, decimal amount, string? label, string? taxCode, bool debit)
    {
        var value = Round(amount);
        if (value == 0m) return this;
        if (value < 0m) { value = -value; debit = !debit; }

        var tax = role is PostingRoles.TaxPayable or PostingRoles.TaxReceivable ? value : (decimal?)null;
        _lines.Add((role, debit ? value : 0m, debit ? 0m : value, taxCode, tax, label, _partner));
        return this;
    }

    public decimal TotalDebit  => _lines.Sum(l => l.Debit);
    public decimal TotalCredit => _lines.Sum(l => l.Credit);

    /// <summary>The lines, numbered from 1, balanced on DEFAULT_ROUNDING when rounding left a cent or two between the sides.</summary>
    public IReadOnlyList<FinancialTransactionLine> Build()
    {
        var difference = TotalDebit - TotalCredit;
        if (Math.Abs(difference) > MaxRoundingDifference)
            throw new InvalidOperationException(
                $"The posting lines do not balance: debits {TotalDebit:0.00}, credits {TotalCredit:0.00}.");
        if (difference != 0m)
            Add(PostingRoles.Rounding, difference, "Rounding", null, debit: false);

        return _lines.Select((l, i) => new FinancialTransactionLine
        {
            LineNumber  = i + 1,
            AccountRole = l.Role,
            Debit       = l.Debit,
            Credit      = l.Credit,
            TaxCode     = l.TaxCode,
            TaxAmount   = l.TaxAmount,
            Label       = l.Label,
            PartnerId   = l.Partner
        }).ToList();
    }

    internal static decimal Round(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Splits <paramref name="total"/> over the keys in proportion to <paramref name="weights"/>, to the cent, the
    /// rounding remainder on the heaviest key — so the parts add up to the total exactly. All weights zero: the first
    /// key takes it all. Used to split a document's tax by tax code.
    /// </summary>
    internal static List<(TKey Key, decimal Amount)> Apportion<TKey>(decimal total, IReadOnlyList<(TKey Key, decimal Weight)> weights)
    {
        if (weights.Count == 0) return [];
        var sum = weights.Sum(w => w.Weight);
        if (sum == 0m) return [(weights[0].Key, Round(total)), .. weights.Skip(1).Select(w => (w.Key, 0m))];

        var parts = weights.Select(w => (w.Key, Amount: Round(total * w.Weight / sum))).ToList();
        var remainder = Round(total) - parts.Sum(p => p.Amount);
        if (remainder != 0m)
        {
            var heaviest = weights.Select((w, i) => (w.Weight, i)).MaxBy(x => Math.Abs(x.Weight)).i;
            parts[heaviest] = (parts[heaviest].Key, parts[heaviest].Amount + remainder);
        }
        return parts;
    }
}
