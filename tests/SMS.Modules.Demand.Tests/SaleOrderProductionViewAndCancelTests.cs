using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A34 D-25 (T-C5-06, T-C6-08): the SO detail's productionOrders[] (make-to-order and A30 make-to-shortage, readable with
/// the detail's own permission) and productionCreationPending; and D-22 (T-C5-07, C-10): cancelling the order cancels its
/// production orders that have not started and every open SALES_ORDER allocation demand, before the hold release.
/// </summary>
public class SaleOrderProductionViewAndCancelTests
{
    private const int User = A34SoHarness.User;

    private static async Task<(A34SoHarness H, Guid So, Guid StockLine, Guid MtoLine)> ConfirmedMixedAsync()
    {
        var h = A34SoHarness.Create();
        var stock = Guid.NewGuid();
        h.Available[stock] = 100m;
        var mto = h.MakeToOrderVariant();
        var so = await h.DraftAsync((stock, null), (mto, null));
        await h.Service.ConfirmWithResultAsync(so, User);
        var lines = (await h.ReadAsync(so)).Lines;
        return (h, so, lines.Single(l => l.VariantUuid == stock).UUID, lines.Single(l => l.VariantUuid == mto).UUID);
    }

    // ── D-25: the detail ──────────────────────────────────────────────────

    [Fact]
    public async Task T_C5_06_the_detail_lists_every_production_order_of_the_order_with_its_line_route_and_delivery()
    {
        var (h, so, stockLine, mtoLine) = await ConfirmedMixedAsync();
        var made = h.Production.Pos.Single();
        made.DeliveryUuid = Guid.NewGuid();
        made.DeliveryNumber = "DLV-2026-00009";
        made.Accepted = 10m;
        made.Status = "COMPLETED";
        var shortage = h.Production.Add(h.OrgId, so, stockLine, "PLANNED", 6m);   // A30 make-to-shortage: no route

        var model = (await h.Service.GetByIdAsync(so))!;

        model.ProductionOrders.Select(p => p.ProductionNumber).Should().Equal(made.Number, shortage.Number);
        var mto = model.ProductionOrders[0];
        mto.IsMakeToOrder.Should().BeTrue();
        mto.SoLineUuid.Should().Be(mtoLine);
        mto.LineNumber.Should().Be(2);
        mto.Status.Should().Be("COMPLETED");
        mto.PlannedQuantity.Should().Be(10m);
        mto.AcceptedQuantity.Should().Be(10m);
        mto.FulfillmentRouteUuid.Should().Be(h.MfgShip.Uuid);
        mto.FulfillmentRouteCode.Should().Be("MFG_PICK_SHIP");
        mto.FulfillmentRouteName.Should().Be("MFG PICK SHIP");
        mto.DeliveryNumber.Should().Be("DLV-2026-00009");
        mto.Created.Should().BeFalse();
        var a30 = model.ProductionOrders[1];
        a30.IsMakeToOrder.Should().BeFalse();
        a30.LineNumber.Should().Be(1);
        a30.FulfillmentRouteCode.Should().BeNull();
        model.ProductionCreationPending.Should().BeFalse();
        model.Lines.Single(l => l.Uuid == mtoLine).EffectiveRouteCategory.Should().Be("MANUFACTURE");
    }

    [Fact]
    public async Task Production_creation_is_pending_while_the_flag_is_set_or_a_make_to_order_line_has_no_order()
    {
        var (h, so, _, mtoLine) = await ConfirmedMixedAsync();
        (await h.Service.GetByIdAsync(so))!.ProductionCreationPending.Should().BeFalse();

        h.Production.Pos.Single().Status = "CANCELLED";   // e.g. cancelled by hand in Manufacturing
        (await h.Service.GetByIdAsync(so))!.ProductionCreationPending.Should().BeTrue("REV-05: the line has no live order");

        h.Production.Pos.Clear();
        var order = await h.Db.SaleOrders.SingleAsync(o => o.UUID == so);
        order.ProductionCreationPendingSince = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();
        (await h.Service.GetByIdAsync(so))!.ProductionCreationPending.Should().BeTrue();
    }

    [Fact]
    public async Task T_C6_08_a_lines_production_shortfall_is_on_the_detail()
    {
        var (h, so, _, mtoLine) = await ConfirmedMixedAsync();
        var line = await h.Db.SaleOrderLines.SingleAsync(l => l.UUID == mtoLine);
        line.ProductionShortfallQty = 5m;
        await h.Db.SaveChangesAsync();

        (await h.Service.GetByIdAsync(so))!.Lines.Single(l => l.Uuid == mtoLine).ProductionShortfallQty.Should().Be(5m);
    }

    [Fact]
    public async Task A_draft_order_asks_Material_for_nothing()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((h.MakeToOrderVariant(), null));

        var model = (await h.Service.GetByIdAsync(so))!;

        model.ProductionOrders.Should().BeEmpty();
        model.ProductionCreationPending.Should().BeFalse();
    }

    // ── D-22: the cancel cascade ──────────────────────────────────────────

    [Fact]
    public async Task T_C5_07_cancel_cancels_unstarted_production_and_open_demands_before_releasing_and_reports_the_rest()
    {
        var (h, so, stockLine, mtoLine) = await ConfirmedMixedAsync();
        var draft = h.Production.Pos.Single();                                             // make-to-order, DRAFT → PLANNED
        var ready = h.Production.Add(h.OrgId, so, stockLine, "READY", 4m);                  // A30 make-to-shortage
        var issued = h.Production.Add(h.OrgId, so, stockLine, "READY", 2m, issued: true);   // material issued: keeps running
        var running = h.Production.Add(h.OrgId, so, mtoLine, "IN_PROGRESS", 10m, h.MfgShip.Uuid);
        var demands = new List<DemandAllocationSummary>
        {
            new(Guid.NewGuid(), AllocationDemandType.SalesOrder, so, mtoLine, "SO", Guid.NewGuid(), null, 10m, 0m, 0m, 0m, 10m, DateTime.UtcNow, 1, "OPEN"),
            new(Guid.NewGuid(), AllocationDemandType.SalesOrder, so, stockLine, "SO", Guid.NewGuid(), null, 4m, 0m, 0m, 0m, 4m, DateTime.UtcNow, 1, "OPEN")
        };
        h.Allocation.Setup(a => a.GetDemandsAsync(null, AllocationDemandType.SalesOrder, so, true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(demands);
        h.Allocation.Setup(a => a.CancelDemandAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => h.Calls.Add("cancel-demand")).Returns(Task.CompletedTask);
        h.Production.OnCancel = () => h.Calls.Add("cancel-production");
        var order = await h.Db.SaleOrders.SingleAsync(o => o.UUID == so);
        order.ProductionCreationPendingSince = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();
        h.Calls.Clear();

        var result = (await h.Service.CancelWithResultAsync(so, User, "Customer changed their mind"))!;

        h.Calls.Should().Equal("cancel-deliveries", "cancel-production", "cancel-demand", "cancel-demand", "release");
        h.Production.CancelCalls.Should().ContainSingle().Which.Should().Be((h.OrgId, so, "Customer changed their mind"));
        result.CancelledProductionOrders.Select(p => p.ProductionNumber).Should().BeEquivalentTo([draft.Number, ready.Number]);
        result.RunningProductionOrders.Select(p => p.ProductionNumber).Should().BeEquivalentTo([issued.Number, running.Number]);
        result.RunningProductionOrders.Single(p => p.ProductionNumber == running.Number).LineNumber.Should().Be(2);
        result.CancelledAllocationDemands.Should().Be(2);
        foreach (var d in demands)
            h.Allocation.Verify(a => a.CancelDemandAsync(d.Uuid, It.IsAny<string>(), User, It.IsAny<CancellationToken>()), Times.Once);

        var note = h.Timeline.Single(e => e.EventType == "SO_PRODUCTION_CANCELLED").Notes!;
        note.Should().Contain(draft.Number).And.Contain(running.Number);
        var saved = await h.ReadAsync(so);
        saved.Status.Should().Be("CANCELLED");
        saved.ProductionCreationPendingSince.Should().BeNull();
    }

    [Fact]
    public async Task Cancelling_a_draft_asks_Material_and_the_allocation_engine_for_nothing()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((h.MakeToOrderVariant(), null));

        var result = (await h.Service.CancelWithResultAsync(so, User, null))!;

        h.Production.CancelCalls.Should().BeEmpty();
        h.Allocation.Verify(a => a.GetDemandsAsync(It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        result.CancelledProductionOrders.Should().BeEmpty();
        result.CancelledAllocationDemands.Should().Be(0);
    }
}
