using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Common;
using Xunit;
using static SMS.Modules.Logistics.Tests.Routes.SaleOrderDeliveryCreatorTests;

namespace SMS.Modules.Logistics.Tests.Routes;

/// <summary>
/// A33 PD-04 / T-C4-06/07 (BR-C4-06/07, D-15) — cancelling a sale order cancels its deliveries that have not been
/// goods-issued, giving their holds back to the order, and reports the issued ones it must leave alone.
/// </summary>
public class SaleOrderDeliveryCancellerTests
{
    private const int User = 42;

    private static (DeliveryStatusRepository Status, SaleOrderDeliveryCanceller Canceller) Wire(Harness h)
    {
        var status = new DeliveryStatusRepository(h.Db, h.Reservations);
        return (status, new SaleOrderDeliveryCanceller(status));
    }

    private static async Task<List<DeliveryOrder>> CreateFor(Harness h, params L[] lines)
    {
        var (so, soLines) = await SeedOrder(h, lines: lines);
        await h.Creator.CreateForConfirmedOrderAsync(h.OrgId, so.UUID, RoutesOf(soLines), User);
        h.Db.ChangeTracker.Clear();
        return await Deliveries(h, so.UUID);
    }

    private static async Task SetStatus(Harness h, Guid deliveryUuid, string status, string? before = null, bool issued = false)
    {
        var d = await h.Db.DeliveryOrders.SingleAsync(x => x.UUID == deliveryUuid);
        d.Status = status;
        d.StatusBeforeHold = before;
        if (issued) d.GoodsIssuedAt = DateTime.UtcNow;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task T_C4_06_deliveries_in_draft_and_released_are_all_cancelled()
    {
        var h = await NewHarness();
        var ds = await CreateFor(h, new L("PICK_ONLY"), new L("PICK_AND_SHIP"));
        await SetStatus(h, ds[1].UUID, "RELEASED");
        var so = ds[0].SaleOrderUuid!.Value;

        var result = await Wire(h).Canceller.CancelOpenAsync(h.OrgId, so, "Customer withdrew the order", User);

        result.Cancelled.Select(c => c.DeliveryUuid).Should().BeEquivalentTo(ds.Select(d => d.UUID));
        result.Cancelled.Should().OnlyContain(c => c.Status == "CANCELLED");
        result.AlreadyIssued.Should().BeEmpty();
        var after = await Deliveries(h, so);
        after.Should().OnlyContain(d => d.Status == "CANCELLED" && d.ClosureReason == "Customer withdrew the order");
    }

    [Fact]
    public async Task T_C4_07_a_goods_issued_delivery_stays_and_is_reported()
    {
        var h = await NewHarness();
        var ds = await CreateFor(h, new L("PICK_ONLY"), new L("PICK_AND_SHIP"), new L("PICK_PACK_SHIP"));
        await SetStatus(h, ds[0].UUID, "GOODS_ISSUED", issued: true);
        await SetStatus(h, ds[1].UUID, "RELEASED");
        var so = ds[0].SaleOrderUuid!.Value;

        var result = await Wire(h).Canceller.CancelOpenAsync(h.OrgId, so, "Customer withdrew the order", User);

        result.AlreadyIssued.Should().ContainSingle().Which.Should().Be(
            new SaleOrderDeliveryRef(ds[0].UUID, ds[0].DeliveryNumber, "GOODS_ISSUED"));
        result.Cancelled.Select(c => c.DeliveryUuid).Should().BeEquivalentTo([ds[1].UUID, ds[2].UUID]);
        (await Deliveries(h, so)).Single(d => d.UUID == ds[0].UUID).Status.Should().Be("GOODS_ISSUED",
            "BR-C4-07: issued stock needs a manual reversal");
    }

    [Theory]
    [InlineData("PICKING", null)]
    [InlineData("PICKED", null)]
    [InlineData("PACKED", null)]
    [InlineData("STAGED", null)]
    [InlineData("PENDING_APPROVAL", null)]
    [InlineData("ON_HOLD", "PICKED")]
    public async Task Every_status_before_goods_issue_is_cancelled(string status, string? before)
    {
        var h = await NewHarness();
        var d = (await CreateFor(h, new L("PICK_AND_SHIP"))).Single();
        await SetStatus(h, d.UUID, status, before);

        var result = await Wire(h).Canceller.CancelOpenAsync(h.OrgId, d.SaleOrderUuid!.Value, "Order cancelled", User);

        result.Cancelled.Should().ContainSingle();
    }

    [Theory]
    [InlineData("IN_TRANSIT")]
    [InlineData("DELIVERED")]
    [InlineData("CLOSED")]
    public async Task Past_goods_issue_nothing_is_cancelled(string status)
    {
        var h = await NewHarness();
        var d = (await CreateFor(h, new L("PICK_AND_SHIP"))).Single();
        await SetStatus(h, d.UUID, status, issued: true);

        var result = await Wire(h).Canceller.CancelOpenAsync(h.OrgId, d.SaleOrderUuid!.Value, "Order cancelled", User);

        result.Cancelled.Should().BeEmpty();
        result.AlreadyIssued.Single().Status.Should().Be(status);
    }

    [Fact]
    public async Task A_second_call_cancels_nothing_more_and_reports_nothing_cancelled()
    {
        var h = await NewHarness();
        var d = (await CreateFor(h, new L("PICK_AND_SHIP"))).Single();
        var canceller = Wire(h).Canceller;
        await canceller.CancelOpenAsync(h.OrgId, d.SaleOrderUuid!.Value, "Order cancelled", User);
        h.Db.ChangeTracker.Clear();

        var again = await canceller.CancelOpenAsync(h.OrgId, d.SaleOrderUuid!.Value, "Order cancelled", User);

        again.Cancelled.Should().BeEmpty();
        again.AlreadyIssued.Should().BeEmpty();
    }

    [Fact]
    public async Task A_released_deliverys_hold_goes_back_to_the_order_before_the_order_releases_it()
    {
        var h = await NewHarness();
        var d = (await CreateFor(h, new L("PICK_AND_SHIP", Qty: 10m))).Single();
        var line = d.Lines.Single();
        h.Reservations.SetAvailable(line.VariantUuid!.Value, 10m);
        (await h.Reservations.ReserveAsync(ReservationSourceType.Delivery, d.UUID,
            [new ReservationRequest(line.VariantUuid!.Value, null, 10m, line.UUID)], User)).Succeeded.Should().BeTrue();
        await SetStatus(h, d.UUID, "RELEASED");

        await Wire(h).Canceller.CancelOpenAsync(h.OrgId, d.SaleOrderUuid!.Value, "Order cancelled", User);

        h.Reservations.ActiveFor(d.UUID).Should().Be(0m, "the delivery holds nothing once cancelled");
        h.Reservations.ActiveFor(d.SaleOrderUuid!.Value).Should().Be(10m,
            "the units go back to the order, whose own release (Demand, right after) frees them");
    }

    [Fact]
    public async Task Another_organizations_deliveries_are_never_touched()
    {
        var a = await NewHarness();
        var d = (await CreateFor(a, new L("PICK_AND_SHIP"))).Single();
        var b = await NewHarness(dbName: a.DbName, superAdmin: true);

        var result = await Wire(b).Canceller.CancelOpenAsync(b.OrgId, d.SaleOrderUuid!.Value, "Not yours", User);

        result.Cancelled.Should().BeEmpty();
        (await Deliveries(a, d.SaleOrderUuid!.Value)).Single().Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task The_single_cancel_endpoint_does_not_reach_another_organizations_delivery_even_for_a_super_admin()
    {
        var a = await NewHarness();
        var d = (await CreateFor(a, new L("PICK_AND_SHIP"))).Single();
        var b = await NewHarness(dbName: a.DbName, superAdmin: true);

        var found = await Wire(b).Status.CancelAsync(d.UUID, new DeliveryReasonRequest { Reason = "Not yours" }, User);

        found.Should().BeFalse("another organization's delivery is a 404, super admin included");
        (await Deliveries(a, d.SaleOrderUuid!.Value)).Single().Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task Cancel_is_route_independent_and_a_null_route_delivery_cancels_as_today()
    {
        // T-C5-08 as the repo has it (BR-C5-05): cancel from any status before goods issue, whatever the route.
        var h = await NewHarness();
        var ds = await CreateFor(h, new L("PICK_ONLY"), new L("FULL"));
        var status = Wire(h).Status;

        foreach (var d in ds)
            (await status.CancelAsync(d.UUID, new DeliveryReasonRequest { Reason = "No longer needed" }, User)).Should().BeTrue();

        (await Deliveries(h, ds[0].SaleOrderUuid!.Value)).Should().OnlyContain(d => d.Status == "CANCELLED");
    }
}
