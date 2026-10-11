using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A38 D-5 — the shift's totals, always recomputed from its orders' payments and its cash movements (never trusted from a
/// client). POS-B calls <see cref="RecalculateAsync"/> after every saved order / void / return.
/// <list type="bullet">
/// <item>Sales = non-refund payments of SALE orders that are not VOIDED, net of change: CASH / CARD / MOBILE /
/// BANK_TRANSFER+CHEQUE (other) / ON_ACCOUNT (credit).</item>
/// <item>Refunds = refund payments (IsRefund, on RETURN orders): CASH vs everything else (incl. ON_ACCOUNT).</item>
/// <item><c>expectedCashClosing = openingFloat + cashSales + cashIn − cashOut − cashRefunds</c>; when the drawer was counted,
/// the variance follows (actual − expected).</item>
/// </list>
/// </summary>
internal interface IPosShiftTotals
{
    /// <summary>Recomputes and saves the shift (retries on a concurrent update). Call after the order itself is saved.</summary>
    Task RecalculateAsync(int shiftId, CancellationToken ct = default);

    /// <summary>Recomputes onto the tracked <paramref name="shift"/> without saving (for callers that save it themselves).</summary>
    Task ApplyAsync(PosShift shift, CancellationToken ct = default);
}

/// <summary>A39 — for an EXCHANGE: SalePart = Σ new (positive) line totals, ReturnPart = Σ |returned line totals| − fee.</summary>
internal sealed record PosTotalsOrder(int Id, string OrderType, string Status, decimal GrandTotal, decimal SalePart = 0m, decimal ReturnPart = 0m);
internal sealed record PosTotalsPayment(string OrderType, string OrderStatus, string Method, decimal Amount, decimal ChangeGiven, bool IsRefund);
internal sealed record PosTotalsMovement(string Direction, decimal Amount);

internal sealed class PosShiftTotals : IPosShiftTotals
{
    private const int MaxAttempts = 3;
    private readonly PosDbContext _db;

    public PosShiftTotals(PosDbContext db) => _db = db;

    public async Task RecalculateAsync(int shiftId, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var shift = await _db.Shifts.FirstOrDefaultAsync(s => s.Id == shiftId, ct);
            if (shift is null) return;
            await ApplyAsync(shift, ct);
            try
            {
                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Another order / movement recalculated at the same time: reload and recompute.
                foreach (var entry in _db.ChangeTracker.Entries<PosShift>().Where(e => e.Entity.Id == shiftId).ToList())
                    await entry.ReloadAsync(ct);
            }
        }
    }

    public async Task ApplyAsync(PosShift shift, CancellationToken ct = default)
    {
        var orders = await _db.Orders.AsNoTracking().Where(o => o.ShiftId == shift.Id)
            .Select(o => new PosTotalsOrder(o.Id, o.OrderType, o.Status, o.GrandTotal,
                o.OrderType == PosOrderType.Exchange ? o.Lines.Where(l => l.Quantity > 0).Sum(l => l.LineTotal) : 0m,
                o.OrderType == PosOrderType.Exchange ? -o.Lines.Where(l => l.Quantity < 0).Sum(l => l.LineTotal) - o.RestockingFeeAmount : 0m))
            .ToListAsync(ct);
        var payments = await _db.Payments.AsNoTracking().Where(p => p.Order.ShiftId == shift.Id)
            .Select(p => new PosTotalsPayment(p.Order.OrderType, p.Order.Status, p.Method, p.Amount, p.ChangeGiven, p.IsRefund))
            .ToListAsync(ct);
        var movements = await _db.CashMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id)
            .Select(m => new PosTotalsMovement(m.Direction, m.Amount)).ToListAsync(ct);
        Compute(shift, orders, payments, movements);
        shift.TotalsRecalculatedAt = DateTime.UtcNow;
    }

    /// <summary>The pure formula (unit-tested on its own).</summary>
    internal static void Compute(
        PosShift s, IReadOnlyList<PosTotalsOrder> orders, IReadOnlyList<PosTotalsPayment> payments, IReadOnlyList<PosTotalsMovement> movements)
    {
        var sales   = payments.Where(p => !p.IsRefund && PosOrderType.Sells(p.OrderType) && p.OrderStatus != PosOrderStatus.Voided).ToList();
        var refunds = payments.Where(p => p.IsRefund).ToList();
        decimal Sales(params string[] methods) => sales.Where(p => methods.Contains(p.Method)).Sum(p => p.Amount - p.ChangeGiven);

        s.TotalCashSales    = Sales(PosPaymentMethod.Cash);
        s.TotalCardSales    = Sales(PosPaymentMethod.Card);
        s.TotalMobileSales  = Sales(PosPaymentMethod.Mobile);
        s.TotalOtherSales   = Sales(PosPaymentMethod.BankTransfer, PosPaymentMethod.Cheque);
        s.TotalCreditSales  = Sales(PosPaymentMethod.OnAccount);
        s.TotalCashRefunds  = refunds.Where(p => p.Method == PosPaymentMethod.Cash).Sum(p => p.Amount);
        // A39 D-10 — store credit and the exchange carry-over are internal tenders with their own rows, not "other" refunds.
        s.TotalOtherRefunds = refunds.Where(p => p.Method is not (PosPaymentMethod.Cash or PosPaymentMethod.StoreCredit or PosPaymentMethod.Exchange))
                                     .Sum(p => p.Amount);
        s.TotalStoreCreditIssued   = refunds.Where(p => p.Method == PosPaymentMethod.StoreCredit).Sum(p => p.Amount);
        s.TotalStoreCreditRedeemed = Sales(PosPaymentMethod.StoreCredit);
        s.TotalCashIn       = movements.Where(m => m.Direction == PosCashDirection.In).Sum(m => m.Amount);
        s.TotalCashOut      = movements.Where(m => m.Direction == PosCashDirection.Out).Sum(m => m.Amount);
        s.ExpectedCashClosing = s.OpeningFloat + s.TotalCashSales + s.TotalCashIn - s.TotalCashOut - s.TotalCashRefunds;
        if (s.ActualCashClosing is { } actual) s.CashVariance = actual - s.ExpectedCashClosing;

        var saleOrders   = orders.Where(o => o.OrderType == PosOrderType.Sale && o.Status != PosOrderStatus.Voided).ToList();
        var returnOrders = orders.Where(o => o.OrderType == PosOrderType.Return).ToList();
        var exchanges    = orders.Where(o => o.OrderType == PosOrderType.Exchange).ToList();
        s.OrderCount  = saleOrders.Count + exchanges.Count;
        s.RefundCount = returnOrders.Count + exchanges.Count;
        s.GrossTotal  = saleOrders.Sum(o => o.GrandTotal) + exchanges.Sum(o => o.SalePart);
        // RETURN GrandTotal is the refund (return total − restocking fee): the fee stays in the takings (D-5).
        s.NetTotal    = s.GrossTotal - returnOrders.Sum(o => o.GrandTotal) - exchanges.Sum(o => o.ReturnPart);
    }
}
