using SMS.Shared.Common;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A38 API-CONTRACT §1 — the POS totals calculator. The server is the source of truth; the PWA mirrors this exactly and
/// both test the worked examples in the contract's appendix. Pure: no I/O, no clock.
/// <list type="number">
/// <item>Per line: <c>gross = round(qty × unitPrice)</c>; line discount PERCENT <c>round(gross × v/100)</c>, AMOUNT
/// <c>min(v, gross)</c>; <c>net = gross − lineDiscount</c>.</item>
/// <item><c>subtotal = Σ net</c>; order discount PERCENT <c>round(subtotal × v/100)</c>, AMOUNT <c>min(v, subtotal)</c>,
/// allocated proportionally to <c>net</c> (<c>round(D × net / subtotal)</c>), the remainder on the last line whose net is
/// not zero.</item>
/// <item>Exclusive: <c>tax = round(netAfter × rate/100)</c>, <c>lineTotal = netAfter + tax</c>. Inclusive:
/// <c>tax = round(netAfter − netAfter / (1 + rate/100))</c>, <c>lineTotal = netAfter</c>.</item>
/// <item>Line <c>netAmount</c> = exclusive amount after every discount; <c>lineTotal = netAmount + taxAmount</c>.</item>
/// </list>
/// Rounding: 2 decimals, midpoint away from zero.
/// </summary>
internal static class PosTotalsCalculator
{
    public const decimal Tolerance = 0.01m;

    public sealed record LineInput(decimal Quantity, decimal UnitPrice, string? DiscountType, decimal DiscountValue);

    public sealed record LineResult(
        decimal Gross, decimal LineDiscount, decimal Net, decimal OrderDiscountShare, decimal NetAfter,
        decimal DiscountAmount, decimal NetAmount, decimal TaxAmount, decimal LineTotal);

    public sealed record Result(IReadOnlyList<LineResult> Lines, decimal Subtotal, decimal DiscountAmount, decimal TaxTotal, decimal GrandTotal);

    public sealed record Tender(string? Method, decimal Amount, decimal ChangeGiven);

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal Discount(string? type, decimal value, decimal basis) => type switch
    {
        "PERCENT" => Round(basis * value / 100m),
        "AMOUNT"  => Math.Min(value, basis),
        _         => 0m
    };

    public static Result Calculate(
        IReadOnlyList<LineInput> lines, string? orderDiscountType, decimal orderDiscountValue, decimal taxPercent, bool taxInclusive)
    {
        var gross = lines.Select(l => Round(l.Quantity * l.UnitPrice)).ToArray();
        var lineDiscount = lines.Select((l, i) => Discount(l.DiscountType, l.DiscountValue, gross[i])).ToArray();
        var net = gross.Select((g, i) => g - lineDiscount[i]).ToArray();
        var subtotal = net.Sum();

        var orderDiscount = Discount(orderDiscountType, orderDiscountValue, subtotal);
        var share = new decimal[lines.Count];
        if (orderDiscount != 0m && subtotal != 0m)
        {
            var last = Array.FindLastIndex(net, n => n != 0m);
            for (var i = 0; i < net.Length; i++)
                if (i != last) share[i] = Round(orderDiscount * net[i] / subtotal);
            share[last] = orderDiscount - share.Where((_, i) => i != last).Sum();
        }

        var rate = taxPercent / 100m;
        var results = new List<LineResult>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var after = net[i] - share[i];
            decimal tax, netAmount, total;
            if (taxInclusive)
            {
                tax       = Round(after - after / (1m + rate));
                netAmount = after - tax;
                total     = after;
            }
            else
            {
                tax       = Round(after * rate);
                netAmount = after;
                total     = after + tax;
            }
            results.Add(new LineResult(gross[i], lineDiscount[i], net[i], share[i], after, lineDiscount[i] + share[i], netAmount, tax, total));
        }

        var taxTotal = results.Sum(r => r.TaxAmount);
        var grand = taxInclusive ? subtotal - orderDiscount : subtotal - orderDiscount + taxTotal;
        return new Result(results, subtotal, orderDiscount, taxTotal, grand);
    }

    /// <summary>
    /// §1.5 — tenders against the grand total, in the order given: only CASH may give change (≤ its amount); CARD, MOBILE,
    /// BANK_TRANSFER and CHEQUE never exceed the balance still open before them (POS-PAY-03); ON_ACCOUNT covers what
    /// remains; <c>Σ(amount − changeGiven) = grandTotal</c> within ±0.01. Null = valid, else the user-facing reason.
    /// </summary>
    public static string? CheckTenders(decimal grandTotal, IReadOnlyList<Tender> tenders)
    {
        if (tenders.Count == 0) return "At least one payment is required.";
        var remaining = grandTotal;
        foreach (var t in tenders)
        {
            if (CheckTender(t) is { } error) return error;
            var method = t.Method!;
            if (method != PosPaymentMethod.Cash && t.Amount > remaining + 0.005m)
                return $"A {method} payment of {t.Amount:0.00} exceeds the remaining balance of {Math.Max(0m, remaining):0.00}.";
            remaining -= t.Amount - t.ChangeGiven;
        }
        if (Math.Abs(remaining) > Tolerance)
            return $"Payments ({grandTotal - remaining:0.00}) do not match the order total ({grandTotal:0.00}).";
        return null;
    }

    /// <summary>One tender on its own: known method, amount &gt; 0 (2dp), change ≥ 0 (2dp), change only on CASH and ≤ its amount.</summary>
    public static string? CheckTender(Tender t)
    {
        if (!IsMethod(t.Method)) return $"Unknown payment method '{t.Method}'.";
        if (t.Amount <= 0m || Round(t.Amount) != t.Amount) return "Payment amounts must be above zero with at most 2 decimals.";
        if (t.ChangeGiven < 0m || Round(t.ChangeGiven) != t.ChangeGiven) return "Change cannot be negative and has at most 2 decimals.";
        if (t.ChangeGiven > 0m && t.Method != PosPaymentMethod.Cash) return "Only cash payments can give change.";
        if (t.ChangeGiven > t.Amount) return "Change cannot exceed the cash tendered.";
        return null;
    }

    public static readonly IReadOnlyList<string> Methods =
    [
        PosPaymentMethod.Cash, PosPaymentMethod.Card, PosPaymentMethod.Mobile, PosPaymentMethod.BankTransfer,
        PosPaymentMethod.Cheque, PosPaymentMethod.OnAccount,
        // A39 D-3 — store credit (tender: redemption; refund: issue). EXCHANGE is server-made only, never sent by a client.
        PosPaymentMethod.StoreCredit
    ];

    public static bool IsMethod(string? method) => method is not null && Methods.Contains(method);

    /// <summary>
    /// A returned quantity's share of an original line: proportional and rounded, the exact remainder when the return
    /// takes the line's last units (so a line returned in parts never credits more or less than it was sold for).
    /// </summary>
    public static (decimal NetAmount, decimal TaxAmount, decimal LineTotal) ReturnShare(
        decimal originalQty, decimal originalTax, decimal originalTotal, decimal returnedQtyBefore, decimal returnedTaxBefore,
        decimal returnedTotalBefore, decimal quantity)
    {
        decimal total, tax;
        if (returnedQtyBefore + quantity >= originalQty)
        {
            total = originalTotal - returnedTotalBefore;
            tax   = originalTax - returnedTaxBefore;
        }
        else
        {
            total = Round(originalTotal * quantity / originalQty);
            tax   = Round(originalTax * quantity / originalQty);
        }
        return (total - tax, tax, total);
    }
}
