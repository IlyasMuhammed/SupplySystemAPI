using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A32 PE-02 — what a sale order's lines hold right now, read from the stock ledger rather than stored: the order's own
/// ACTIVE <c>SALES_ORDER</c> reservations per line (what Release frees) plus what its not-yet-issued deliveries carry.
/// One source of truth — every path that changes a hold (confirm, the GRN link, the allocation listener, delivery
/// release and cancel in Logistics, consumption at goods issue, the expiry sweep, manual reserve/release) changes the
/// ledger, so nothing here can drift from it.
/// </summary>
internal sealed record SaleOrderLineHolds(
    IReadOnlyDictionary<Guid, decimal> Reserved, IReadOnlyDictionary<Guid, decimal> InFlight)
{
    public decimal ReservedFor(Guid lineUuid) => Reserved.GetValueOrDefault(lineUuid);
    public decimal InFlightFor(Guid lineUuid) => InFlight.GetValueOrDefault(lineUuid);

    /// <summary>Quantity − fulfilled − held by the order − on its open deliveries, never below zero. A36: 0 for a service line.</summary>
    public decimal ReservableFor(SaleOrderLine line) => SaleOrderServiceLines.IsService(line)
        ? 0m
        : Math.Max(0m, line.Quantity - line.FulfilledQty - ReservedFor(line.UUID) - InFlightFor(line.UUID));

    public string IndicatorFor(SaleOrder order, SaleOrderLine line) =>
        SaleOrderHolds.Indicator(order.Status, line.Status, line.Quantity, line.FulfilledQty,
            ReservedFor(line.UUID) + InFlightFor(line.UUID));
}

internal static class SaleOrderHolds
{
    private static readonly string Cancelled     = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Cancelled);
    private static readonly string Fulfilled     = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Fulfilled);
    private static readonly string Invoiced      = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Invoiced);
    private static readonly string OrderCancelled = EnumCode<SaleOrderStatus>.Of(SaleOrderStatus.Cancelled);

    public static async Task<SaleOrderLineHolds> ReadAsync(
        IStockReservationService stock, ISaleOrderDeliveryQuantities? deliveries, Guid orderUuid, CancellationToken ct = default)
    {
        var holds = await stock.GetBySourceAsync(ReservationSourceType.SalesOrder, orderUuid, ct) ?? [];
        var reserved = holds
            .Where(h => h.Status == "ACTIVE" && h.SourceLineUuid is not null)
            .GroupBy(h => h.SourceLineUuid!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(h => h.ReservedQty));

        // A33 C-2: only deliveries that actually HOLD stock count — RELEASED onwards (release moved the order's hold onto
        // them). A DRAFT delivery holds nothing, and since confirm now creates DRAFT deliveries for every line, counting
        // them made every line look fully held: Reserve was dead and the GRN link reserved nothing.
        IReadOnlyDictionary<Guid, decimal> inFlight = deliveries is null
            ? new Dictionary<Guid, decimal>()
            : await deliveries.GetHeldBySoLineAsync(orderUuid, ct) ?? new Dictionary<Guid, decimal>();

        return new SaleOrderLineHolds(reserved, inFlight);
    }

    /// <summary>
    /// §6.3 (BR-C4-07), computed at read time. <paramref name="held"/> is what the order and its open deliveries hold
    /// for the line. A cancelled order greys every line, whatever the lines themselves still say.
    /// </summary>
    public static string Indicator(string orderStatus, string lineStatus, decimal quantity, decimal fulfilled, decimal held)
    {
        if (orderStatus == OrderCancelled || lineStatus == Cancelled)
            return EnumCode<SaleOrderDeliveryIndicator>.Of(SaleOrderDeliveryIndicator.Grey);
        if (lineStatus == Fulfilled || lineStatus == Invoiced || fulfilled >= quantity)
            return EnumCode<SaleOrderDeliveryIndicator>.Of(SaleOrderDeliveryIndicator.Green);
        if (fulfilled > 0m)
            return EnumCode<SaleOrderDeliveryIndicator>.Of(SaleOrderDeliveryIndicator.Yellow);
        if (held >= quantity)
            return EnumCode<SaleOrderDeliveryIndicator>.Of(SaleOrderDeliveryIndicator.Blue);
        if (held > 0m)
            return EnumCode<SaleOrderDeliveryIndicator>.Of(SaleOrderDeliveryIndicator.Yellow);
        return EnumCode<SaleOrderDeliveryIndicator>.Of(SaleOrderDeliveryIndicator.Red);
    }

    /// <summary>
    /// The line fields the other reservation paths keep, brought in line with what the ledger now says: DeficitQty is the
    /// line's unreserved balance (what the GRN link may still reserve, SaleOrderGrnLinkService), and an OPEN line holding
    /// something is RESERVED, a RESERVED line holding nothing is OPEN again. Fulfilment statuses are never walked back.
    /// </summary>
    public static void Reconcile(SaleOrderLine line, SaleOrderLineHolds holds)
    {
        var held = holds.ReservedFor(line.UUID) + holds.InFlightFor(line.UUID);
        line.DeficitQty = Math.Max(0m, line.Quantity - line.FulfilledQty - held);

        var open     = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Open);
        var reserved = EnumCode<SaleOrderLineStatus>.Of(SaleOrderLineStatus.Reserved);
        if (line.Status == open && holds.ReservedFor(line.UUID) > 0m)
            line.Status = reserved;
        else if (line.Status == reserved && held <= 0m)
            line.Status = open;
    }

    // ── One change to an order's holds at a time ─────────────────────────────

    /// <summary>How long a reserve/release/cancel waits for another one on the same order before giving up with a 409.</summary>
    internal const int LockTimeoutMilliseconds = SaleOrderLocks.TimeoutMilliseconds;

    /// <summary>A33 REV-02: the one shared string — Logistics' delivery creator takes the same lock before it re-reads the order.</summary>
    internal static string LockResource(Guid orderUuid) => SaleOrderLocks.HoldsResource(orderUuid);

    /// <summary>
    /// Runs <paramref name="work"/> so that no other reserve, release or cancel of the same order runs alongside it: on
    /// SQL Server, in a transaction holding an exclusive application lock named after the order (the TaxCodeService
    /// pattern) — the second caller waits, then reads what the first committed, so two "reserve the rest" calls cannot
    /// both see the same unreserved balance. The stock ledger itself is Inventory's and commits on its own; the lock only
    /// has to cover the read-decide-reserve sequence. The in-memory provider has no locks and runs the work as it is.
    /// </summary>
    public static async Task<T> OneChangeAtATimeAsync<T>(DemandDbContext db, Guid orderUuid, Func<Task<T>> work)
    {
        if (!db.Database.IsRelational())
            return await work();

        if (db.Database.CurrentTransaction is not null)
        {
            await LockAsync(db, orderUuid);
            return await work();
        }

        var strategy = db.Database.CreateExecutionStrategy();
        var attempt  = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync();
            await LockAsync(db, orderUuid);
            var result = await work();
            await transaction.CommitAsync();
            return result;
        });
    }

    private static async Task LockAsync(DemandDbContext db, Guid orderUuid)
    {
        var outcome = new SqlParameter("@outcome", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await db.Database.ExecuteSqlRawAsync(
            "DECLARE @result int; "
          + "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout; "
          + "SET @outcome = @result;",
            [
                new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = LockResource(orderUuid) },
                new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMilliseconds },
                outcome
            ]);

        if (outcome.Value is not int granted || granted < 0)
            throw new ConflictException("Someone else is changing this sale order's reservations right now. Try again in a moment.");
    }
}
