using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A38 E1/E2 — brings an order's stock and finance postings up to what its state requires. Called right after the order
/// is saved (sync, void, return) and by <see cref="PosPostingRetryJob"/>. Every external call is idempotent on the order's
/// own ids, so a repeat after a partial failure is safe:
/// <list type="bullet">
/// <item>SALE: stock POS_SALE (and POS_VOID once voided); finance <c>PostSaleAsync</c> (and <c>VoidSaleAsync</c> once
/// voided). A void resets both flags, so a sale that never reached stock/finance gets sale + reversal (net zero).</item>
/// <item>RETURN: stock POS_RETURN; finance <c>PostReturnAsync</c> against the original sale's invoice (waits — fails and
/// is retried — until the original sale is in Finance).</item>
/// </list>
/// Sets StockPosted / FinancePosted / PostingError / PostingAttempts and the NEGATIVE_STOCK flag (+ a notification to the
/// store's supervisors the first time). Never throws for a posting failure: the error is kept on the order.
/// </summary>
internal interface IPosPostingService
{
    /// <summary><paramref name="order"/> tracked by the POS context with Lines and Payments loaded.</summary>
    Task PostAsync(PosOrder order, CancellationToken ct = default);
}

internal sealed class PosPostingService : IPosPostingService
{
    internal const string NegativeStockNotification = "POS_NEGATIVE_STOCK";

    private readonly PosDbContext          _db;
    private readonly IPosStockPoster?      _stock;
    private readonly ICounterSaleService?  _finance;
    private readonly INotificationService? _notifications;
    private readonly TimeProvider          _clock;
    private readonly ILogger               _log;

    public PosPostingService(
        PosDbContext db, IPosStockPoster? stock = null, ICounterSaleService? finance = null, INotificationService? notifications = null,
        TimeProvider? clock = null, ILogger<PosPostingService>? log = null)
    {
        _db            = db;
        _stock         = stock;
        _finance       = finance;
        _notifications = notifications;
        _clock         = clock ?? TimeProvider.System;
        _log           = log ?? (ILogger)NullLogger.Instance;
    }

    public async Task PostAsync(PosOrder order, CancellationToken ct = default)
    {
        if (order.StockPosted && order.FinancePosted) return;
        var store = await _db.Stores.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(s => s.Id == order.StoreId && s.OrganizationId == order.OrganizationId, ct);

        var errors = new List<string>();
        IReadOnlyList<Guid> negatives = [];
        if (!order.StockPosted)
        {
            try
            {
                negatives = await PostStockAsync(order, store, ct);
                order.StockPosted = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add("Stock: " + ex.Message);
                _log.LogWarning(ex, "POS order {Order}: stock posting failed.", order.OrderNumber);
            }
        }
        if (!order.FinancePosted)
        {
            try
            {
                await PostFinanceAsync(order, ct);
                order.FinancePosted = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add("Finance: " + ex.Message);
                _log.LogWarning(ex, "POS order {Order}: finance posting failed.", order.OrderNumber);
            }
        }

        order.PostingAttempts++;
        order.LastPostingAttemptAt = _clock.GetUtcNow().UtcDateTime;
        order.PostingError = errors.Count == 0 ? null : Truncate(string.Join(" | ", errors), 1000);
        var newlyNegative = negatives.Count > 0 && PosOrderType.Sells(order.OrderType) && order.Status != PosOrderStatus.Voided
                            && !order.FlagList.Contains(PosOrderFlag.NegativeStock);
        if (newlyNegative) order.AddFlag(PosOrderFlag.NegativeStock);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // Someone changed the order meanwhile (e.g. a void): keep theirs. Every posting is idempotent — the retry job
            // re-runs it and records the result.
            _log.LogWarning(ex, "POS order {Order}: posting state not saved (changed concurrently); the retry job finishes it.", order.OrderNumber);
            foreach (var entry in _db.ChangeTracker.Entries<PosOrder>().Where(e => e.Entity.Id == order.Id).ToList())
                await entry.ReloadAsync(ct);
            return;
        }

        if (newlyNegative)
            await PosSupervisors.NotifyAsync(_db, _notifications, order.OrganizationId, order.StoreId, NegativeStockNotification,
                "POS sale below zero stock",
                $"Order {order.OrderNumber} ({store.Name}) sold {negatives.Count} product(s) below zero stock in {store.WarehouseName}.",
                "PosOrder", order.Uuid, $"/portal/pages/pos/orders/{order.Uuid}", ct);
    }

    // ── stock ──

    private async Task<IReadOnlyList<Guid>> PostStockAsync(PosOrder order, PosStore store, CancellationToken ct)
    {
        if (_stock is null) throw new InvalidOperationException("Stock posting is not available on this server.");
        static List<PosStockLine> Group(IEnumerable<PosOrderLine> ls) =>
            ls.GroupBy(l => l.VariantUuid).Select(g => new PosStockLine(g.Key, g.Sum(l => Math.Abs(l.Quantity)))).ToList();
        var lines = Group(order.Lines);
        PosStockPosting Posting(string type, bool outbound, int userId, List<PosStockLine>? only = null) =>
            new(order.OrganizationId, store.WarehouseUuid, order.Uuid, order.OrderNumber, type, outbound, only ?? lines, userId);

        if (order.OrderType == PosOrderType.Return)
        {
            await _stock.PostAsync(Posting(PosStockTransactionType.Return, false, order.CashierUserId), ct);
            return [];
        }
        if (order.OrderType == PosOrderType.Exchange)
        {
            // A39 D-6 — returned items back into the processing store's warehouse, new items out; each idempotent on its type.
            await _stock.PostAsync(Posting(PosStockTransactionType.Return, false, order.CashierUserId, Group(order.Lines.Where(l => l.Quantity < 0m))), ct);
            var outLines = Group(order.Lines.Where(l => l.Quantity > 0m));
            return outLines.Count == 0 ? []
                : (await _stock.PostAsync(Posting(PosStockTransactionType.Sale, true, order.CashierUserId, outLines), ct)).NegativeVariantUuids;
        }

        var sale = await _stock.PostAsync(Posting(PosStockTransactionType.Sale, true, order.CashierUserId), ct);
        if (order.Status != PosOrderStatus.Voided) return sale.NegativeVariantUuids;
        await _stock.PostAsync(Posting(PosStockTransactionType.Void, false, order.VoidedBy ?? order.CashierUserId), ct);
        return [];
    }

    // ── finance ──

    private async Task PostFinanceAsync(PosOrder order, CancellationToken ct)
    {
        if (order.OrderType == PosOrderType.Return)
        {
            await PostReturnAsync(order, ct);
            return;
        }
        if (order.OrderType == PosOrderType.Exchange)
        {
            await PostExchangeAsync(order, ct);
            return;
        }
        if (order.GrandTotal <= 0m) return; // nothing to invoice (fully discounted): Finance refuses a sale for nothing.
        var finance = Finance;

        if (order.SalesInvoiceUuid is null)
        {
            var tenders = SaleTenders(order);
            var result = await finance.PostSaleAsync(new CounterSaleRequest(
                order.OrganizationId, order.Uuid, order.OrderNumber, order.CustomerUuid, SaleDate(order),
                order.Lines.OrderBy(l => l.LineNumber).Select(l => new CounterSaleLine(
                    l.Uuid, l.VariantUuid, Describe(l), l.Quantity, l.UnitPrice, l.DiscountAmount, l.NetAmount,
                    order.TaxCodeUuid, order.TaxCode, l.TaxPercent, l.TaxAmount, l.LineTotal)).ToList(),
                tenders, order.CashierUserId, Truncate(order.Note, 500)), ct);
            order.SalesInvoiceUuid   = result.SalesInvoiceUuid;
            order.SalesInvoiceNumber = result.InvoiceNumber;
            LinkPayments(order.Payments.Where(p => !p.IsRefund), tenders, result.PaymentUuids);
        }

        if (order.Status == PosOrderStatus.Voided && order.CreditNoteUuid is null)
        {
            var voided = await finance.VoidSaleAsync(order.OrganizationId, order.SalesInvoiceUuid!.Value, VoidSourceUuid(order.Uuid),
                order.VoidReason ?? "Voided at the POS.", order.VoidedBy ?? order.CashierUserId, ct);
            order.CreditNoteUuid   = voided.CreditNoteUuid;
            order.CreditNoteNumber = voided.CreditNoteNumber;
        }
    }

    private async Task PostReturnAsync(PosOrder order, CancellationToken ct)
    {
        if (order.CreditNoteUuid is not null) return;
        var original = order.OriginalOrder ?? await _db.Orders.IgnoreQueryFilters().Include(o => o.Lines)
            .FirstAsync(o => o.Id == order.OriginalOrderId && o.OrganizationId == order.OrganizationId, ct);
        if (original.Lines.Count == 0)
            await _db.Entry(original).Collection(o => o.Lines).LoadAsync(ct);
        if (original.SalesInvoiceUuid is not { } invoice)
        {
            if (original.FinancePosted) return; // a sale for nothing was never invoiced: nothing to credit.
            throw new InvalidOperationException($"The original sale {original.OrderNumber} is not in Finance yet; the return follows it.");
        }

        var refunds = order.Payments.Where(p => p.IsRefund)
            .Select(p => new CounterSaleTender(p.Uuid, p.Method, p.Amount, p.Reference)).ToList();
        if (refunds.Sum(r => r.Amount) <= 0m) return; // a 100 % restocking fee leaves nothing to credit
        var result = await Finance.PostReturnAsync(new CounterReturnRequest(
            order.OrganizationId, order.Uuid, order.OrderNumber, invoice, SaleDate(order),
            CreditLines(order, original), refunds, order.ReturnReason ?? "Returned at the POS.", order.CashierUserId), ct);
        order.CreditNoteUuid   = result.CreditNoteUuid;
        order.CreditNoteNumber = result.CreditNoteNumber;
        LinkPayments(order.Payments.Where(p => p.IsRefund), refunds, result.RefundUuids);
    }

    /// <summary>
    /// A39 D-6 — the return part as a credit note (refund tenders: EXCHANGE for the carried value + the refund method for a
    /// negative net), the new part as a counter sale on a derived source uuid (tenders: EXCHANGE + the net payments).
    /// Each part is idempotent on its own id and skipped once its document is linked.
    /// </summary>
    private async Task PostExchangeAsync(PosOrder order, CancellationToken ct)
    {
        Exception? first = null;
        try { await PostReturnAsync(order, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { first = ex; }

        var newLines = order.Lines.Where(l => l.Quantity > 0m && l.OriginalLineId == null).OrderBy(l => l.LineNumber).ToList();
        var newTotal = newLines.Sum(l => l.LineTotal);
        if (order.SalesInvoiceUuid is null && newTotal > 0m)
        {
            var tenders = SaleTenders(order, newTotal, order.Uuid);
            var result = await Finance.PostSaleAsync(new CounterSaleRequest(
                order.OrganizationId, ExchangeSaleUuid(order.Uuid), order.OrderNumber, order.CustomerUuid, SaleDate(order),
                newLines.Select(l => new CounterSaleLine(l.Uuid, l.VariantUuid, Describe(l), l.Quantity, l.UnitPrice, l.DiscountAmount, l.NetAmount,
                    order.TaxCodeUuid, order.TaxCode, l.TaxPercent, l.TaxAmount, l.LineTotal)).ToList(),
                tenders, order.CashierUserId, Truncate(order.Note, 500)), ct);
            order.SalesInvoiceUuid   = result.SalesInvoiceUuid;
            order.SalesInvoiceNumber = result.InvoiceNumber;
            LinkPayments(order.Payments.Where(p => !p.IsRefund), tenders, result.PaymentUuids);
        }
        if (first is not null) throw first;
    }

    /// <summary>The exchange's sale-part idempotency key for Finance (Finance's SourceUuid is unique across sale/return/void).</summary>
    internal static Guid ExchangeSaleUuid(Guid orderUuid) => DerivedUuid(orderUuid, "EXCHANGE_SALE");

    /// <summary>
    /// D-5 — the credit note's lines: the returned lines (absolute values) scaled by refund / return total when a restocking
    /// fee was kept, the remainder on the last line so Σ = the refund exactly; tax scaled alike, net = total − tax.
    /// </summary>
    internal static List<CounterReturnLine> CreditLines(PosOrder order, PosOrder original)
    {
        var lines = order.Lines.Where(l => l.OriginalLineId != null).OrderBy(l => l.LineNumber).ToList();
        var total = lines.Sum(l => Math.Abs(l.LineTotal));
        var refund = total - order.RestockingFeeAmount;
        var result = new List<CounterReturnLine>(lines.Count);
        var given = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            decimal lineTotal = Math.Abs(l.LineTotal), tax = Math.Abs(l.TaxAmount);
            if (order.RestockingFeeAmount != 0m && total != 0m)
            {
                var scaled = i == lines.Count - 1 ? refund - given : PosTotalsCalculator.Round(lineTotal * refund / total);
                tax = Math.Min(PosTotalsCalculator.Round(tax * refund / total), scaled);
                lineTotal = scaled;
            }
            given += lineTotal;
            result.Add(new CounterReturnLine(original.Lines.First(o => o.Id == l.OriginalLineId).Uuid, l.Uuid, Math.Abs(l.Quantity),
                lineTotal - tax, tax, lineTotal));
        }
        return result;
    }

    private ICounterSaleService Finance => _finance ?? throw new InvalidOperationException("Finance posting is not available on this server.");

    /// <summary>
    /// Tenders net of change (ON_ACCOUNT kept as on-account), summing exactly to the grand total as Finance requires: an
    /// excess (only possible on an offline order the server re-totalled) is taken off the last tenders, a shortfall is
    /// booked ON_ACCOUNT (the customer owes it — visible on the receivables for a supervisor to follow up).
    /// </summary>
    internal static List<CounterSaleTender> SaleTenders(PosOrder order) => SaleTenders(order, order.GrandTotal, order.Uuid);

    internal static List<CounterSaleTender> SaleTenders(PosOrder order, decimal total, Guid shortfallSource)
    {
        var tenders = order.Payments.Where(p => !p.IsRefund).OrderBy(p => p.Id).ThenBy(p => p.CreatedAt)
            .Select(p => new CounterSaleTender(p.Uuid, p.Method, p.Amount - p.ChangeGiven, p.Reference))
            .Where(t => t.Amount > 0m).ToList();
        var diff = total - tenders.Sum(t => t.Amount);
        if (diff > 0m)
            tenders.Add(new CounterSaleTender(DerivedUuid(shortfallSource, "SHORTFALL"), PosPaymentMethod.OnAccount, diff, "Offline shortfall"));
        for (var i = tenders.Count - 1; diff < 0m && i >= 0; i--)
        {
            var take = Math.Min(-diff, tenders[i].Amount);
            tenders[i] = tenders[i] with { Amount = tenders[i].Amount - take };
            diff += take;
        }
        return tenders.Where(t => t.Amount > 0m).ToList();
    }

    /// <summary>Finance answers one payment per non-ON_ACCOUNT tender, in the order sent.</summary>
    private static void LinkPayments(IEnumerable<PosPayment> payments, IReadOnlyList<CounterSaleTender> sent, IReadOnlyList<Guid> created)
    {
        var paid = sent.Where(t => t.Method != PosPaymentMethod.OnAccount).ToList();
        if (paid.Count != created.Count) return;
        var byUuid = payments.ToDictionary(p => p.Uuid);
        for (var i = 0; i < paid.Count; i++)
            if (byUuid.TryGetValue(paid[i].SourceUuid, out var p)) p.CustomerPaymentUuid = created[i];
    }

    private static DateTime SaleDate(PosOrder o) => o.CreatedAtLocal > o.CreatedAt || o.CreatedAtLocal == default ? o.CreatedAt : o.CreatedAtLocal;

    private static string Describe(PosOrderLine l) =>
        Truncate(string.IsNullOrWhiteSpace(l.VariantName) ? l.ProductName : $"{l.ProductName} – {l.VariantName}", 300)!;

    /// <summary>The void's own idempotency key for Finance (stable per order).</summary>
    internal static Guid VoidSourceUuid(Guid orderUuid) => DerivedUuid(orderUuid, "VOID");

    internal static Guid DerivedUuid(Guid source, string label)
    {
        var bytes = source.ToByteArray().Concat(System.Text.Encoding.UTF8.GetBytes(label)).ToArray();
        return new Guid(MD5.HashData(bytes));
    }

    private static string? Truncate(string? v, int max) => v is null || v.Length <= max ? v : v[..max];
}

/// <summary>A38 — a store's SUPERVISOR staff (D-34 / R-1: recipients per store, never a role broadcast). Explicit org.</summary>
internal static class PosSupervisors
{
    public static async Task<IReadOnlyList<int>> OfStoreAsync(PosDbContext db, Guid org, int storeId, CancellationToken ct = default) =>
        await db.StoreStaff.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == org && s.StoreId == storeId && s.IsActive && s.PosRole == PosRole.Supervisor)
            .Select(s => s.UserId).Distinct().ToListAsync(ct);

    public static async Task<int> NotifyAsync(
        PosDbContext db, INotificationService? notifications, Guid org, int storeId, string type, string title, string message,
        string entityType, Guid entityUuid, string url, CancellationToken ct = default)
    {
        if (notifications is null) return 0;
        var users = await OfStoreAsync(db, org, storeId, ct);
        foreach (var userId in users)
            await notifications.TryCreateAsync(new NotificationRequest(userId, type, title, message, "POS", entityType, entityUuid.ToString(), url));
        return users.Count;
    }
}
