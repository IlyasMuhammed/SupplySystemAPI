using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// One exchange difference, worked out (spec §8, BR-C7-01..03).
/// </summary>
/// <param name="BookedBase">The amount × the rate the document was booked at, rounded at the base currency's decimals.</param>
/// <param name="SettledBase">The amount × the settlement (or revaluation) rate, rounded the same way.</param>
/// <param name="Difference">Positive = exchange gain, negative = exchange loss (BR-C7-02), from the organization's point of view.</param>
internal sealed record ExchangeDifferenceAmounts(decimal BookedBase, decimal SettledBase, decimal Difference)
{
    public bool IsGain => Difference > 0m;
    public bool IsLoss => Difference < 0m;
}

/// <summary>
/// A35 C7 — the arithmetic of an exchange difference, shared by the realized difference at payment (§8.1, §8.3) and the
/// unrealized one at revaluation (§8.2). Both base amounts are rounded at the base currency's decimals, half away from
/// zero (D-13), before they are subtracted — exactly the spec's §8.1 code.
/// <para>
/// <b>Sign.</b> On a receivable, more base money for the same foreign amount is a gain (BR-C7-01: settled − booked). On a
/// payable it is the opposite: paying (or owing) more base money than was booked is a loss, so the difference is
/// booked − settled. The spec's T-C8-07 calls a payable whose currency fell a "loss"; it is a gain for the organization —
/// recorded as a deviation in ADDENDUM-35-TASKS.md.
/// </para>
/// </summary>
internal static class ExchangeDifferenceMath
{
    public static ExchangeDifferenceAmounts Compute(
        decimal amountInDocumentCurrency, decimal bookedRate, decimal settlementRate, int baseDecimalPlaces, string side)
    {
        var receivable = side switch
        {
            ExchangeDifferenceSides.Receivable => true,
            ExchangeDifferenceSides.Payable    => false,
            _ => throw new ArgumentException($"'{side}' is not an exchange-difference side.", nameof(side))
        };

        var booked  = CurrencyConventions.RoundAmount(amountInDocumentCurrency * bookedRate, baseDecimalPlaces);
        var settled = CurrencyConventions.RoundAmount(amountInDocumentCurrency * settlementRate, baseDecimalPlaces);

        return new ExchangeDifferenceAmounts(booked, settled, receivable ? settled - booked : booked - settled);
    }
}
