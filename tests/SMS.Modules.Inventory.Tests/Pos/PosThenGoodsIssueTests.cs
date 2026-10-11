using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests.Pos;

/// <summary>A38 R-5 — a POS sale's shortfall survives the later goods issue of the reserved delivery.</summary>
public class PosThenGoodsIssueTests
{
    [Fact]
    public async Task Issuing_a_delivery_after_a_POS_oversale_drives_on_hand_negative_instead_of_hiding_it()
    {
        var h = new PosHarness();
        var w = await h.WarehouseAsync();
        var v = await h.VariantAsync();
        await h.RowAsync(v, w, 10m);

        var reservations = new StockReservationService(h.Db);
        var delivery = Guid.NewGuid();
        await reservations.ReserveAsync(ReservationSourceType.Delivery, delivery, [new ReservationRequest(v.Uuid, w.Uuid, 6m)], 7);

        await h.Poster.PostAsync(h.Sale(w, Guid.NewGuid(), (v, 8m)));
        (await h.RowsAsync(v, w)).Single().Should().BeEquivalentTo(new { QtyOnHand = 2m, QtyReserved = 6m });

        var issuer = new GoodsIssuePoster(h.Db, new InventoryLedgerService(h.Db, NullLogger<InventoryLedgerService>.Instance));
        var result = await issuer.PostAsync(ReservationSourceType.Delivery, delivery,
            new GoodsIssuePosting("DELIVERY", delivery, "DLV-2026-00001", null, "Acme Ltd", "Issued."), 7);

        result.QuantityOut.Should().Be(6m);
        (await h.RowsAsync(v, w)).Single().Should().BeEquivalentTo(new { QtyOnHand = -4m, QtyReserved = 0m });

        var issue = await h.Db.InventoryLedgerEntries.AsNoTracking()
            .SingleAsync(e => e.TransactionType == GoodsIssueTransactionType.DeliveryIssue);
        issue.QuantityOut.Should().Be(6m, "the ledger row takes 6 out");
        issue.BalanceAfter.Should().Be(-14m, "ledger balance: POS −8, then delivery −6 (no opening ledger row was seeded)");
    }
}
