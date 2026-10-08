using System.Data;
using System.Net;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;
using static SMS.Integration.Tests.RouteClassification.Rc;

namespace SMS.Integration.Tests.RouteClassification;

/// <summary>
/// A34 locking and recovery on the real host (LocalDB), for what only SQL Server can show (R-6, R-7, R-8):
/// <list type="bullet">
/// <item><b>Confirm race:</b> two confirms of a make-to-order order: one wins, one production order per line.</item>
/// <item><b>Production recoveries:</b> two "Create production orders" clicks and the D-17 sweep at once re-create a lost
/// production order exactly once per line.</item>
/// <item><b>Cancel vs production creation:</b> creation waits for a cancel holding the order's lock and then creates
/// nothing; a real race leaves no live production order on the cancelled order.</item>
/// <item><b>Cancel vs the FGR hand-over:</b> the hand-over waits for (or is swept after) a cancel holding the order's lock,
/// then creates nothing; a real race leaves no live delivery on the cancelled order.</item>
/// <item><b>Replays:</b> a replayed hand-over (sweep, "Create delivery now", both racing) creates 0; after a lost
/// hand-over the sweep re-creates the delivery once.</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~RouteClassificationLockingHostTests</c>.</para>
/// </summary>
public sealed class RouteClassificationLockingHostTests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public RouteClassificationLockingHostTests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "LK");
    }

    private string ConnectionString =>
        $"Server=(localdb)\\mssqllocaldb;Database={_f.DatabaseName};Trusted_Connection=True;MultipleActiveResultSets=true";

    private static readonly string[] OpenBelowInProgress = ["DRAFT", "PLANNED", "MATERIAL_PENDING", "READY"];

    /// <summary>Takes the order's lock as Demand's cancel does: an exclusive transaction-owned app lock.</summary>
    private async Task<(SqlConnection Conn, SqlTransaction Tx)> HoldOrderLockAsync(Guid so)
    {
        var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        await using var take = new SqlCommand(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r;",
            conn, tx);
        take.Parameters.Add(new SqlParameter("@res", SqlDbType.NVarChar, 255) { Value = SaleOrderLocks.HoldsResource(so) });
        Convert.ToInt32(await take.ExecuteScalarAsync()).Should().BeGreaterOrEqualTo(0, "the test holds the order's lock");
        return (conn, tx);
    }

    private static async Task CancelInsideAsync(SqlConnection conn, SqlTransaction tx, Guid so)
    {
        await using var cmd = new SqlCommand("UPDATE demand.sale_orders SET Status = 'CANCELLED' WHERE UUID = @so", conn, tx);
        cmd.Parameters.AddWithValue("@so", so);
        (await cmd.ExecuteNonQueryAsync()).Should().Be(1);
        await tx.CommitAsync();
    }

    private async Task<List<Guid>> LivePosAsync(Guid so) =>
        (await _k.ProductionOrdersOfAsync(so)).Where(p => p.S("status") != "CANCELLED").Select(p => p.G("uuid")).ToList();

    [Fact]
    public async Task Racing_confirms_and_racing_recoveries_create_one_production_order_per_line()
    {
        var w = await _k.MtoWorldAsync("RACE", rawStock: 100m);
        for (var round = 1; round <= 3; round++)
        {
            var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 1m), RLine(w.Fg, 2m));
            var results = await Task.WhenAll(_k.TryConfirm(so), _k.TryConfirm(so));
            results.Count(r => r.Status == HttpStatusCode.OK).Should().Be(1, $"round {round}: one confirm wins — {string.Join(" / ", results.Select(r => r.ToString()))}");
            results.Single(r => r.Status != HttpStatusCode.OK).Status.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
            var pos = await LivePosAsync(so);
            pos.Should().HaveCount(2, $"round {round}: one production order per line");

            // Both lost (cancelled by hand), the order marked pending: two clicks and the sweep at once re-create each once.
            foreach (var po in pos)
                await _k.Ok(_k.Post($"/api/production-orders/{po}/cancel", new { Reason = "simulate a lost creation" }), "cancel");
            await _k.BackdateProductionPendingAsync(so);
            var clicks = await Task.WhenAll(_k.TryCreateProductionOrders(so), _k.TryCreateProductionOrders(so), RunSweepAsApi());
            clicks.Where(c => c is not null).Should().OnlyContain(c => c!.Status == HttpStatusCode.OK || c.Status == HttpStatusCode.Conflict);
            var after = await LivePosAsync(so);
            after.Should().HaveCount(2, $"round {round}: re-created exactly once per line");
            after.Should().NotIntersectWith(pos);
            var lines = (await _k.ProductionOrdersOfAsync(so)).Where(p => p.S("status") != "CANCELLED").Select(p => p.G("sourceLineUuid")).ToList();
            lines.Should().OnlyHaveUniqueItems($"round {round}: one per SO line");
            (await _k.ProductionPendingSinceAsync(so)).Should().BeNull();
        }
    }

    private async Task<Api?> RunSweepAsApi()
    {
        await _k.RunProductionSweepAsync();
        return null;
    }

    [Fact]
    public async Task Production_creation_waits_for_a_cancel_holding_the_orders_lock_and_a_real_race_leaves_nothing_live()
    {
        var w = await _k.MtoWorldAsync("CVP", rawStock: 100m);

        // ── Deterministic: the cancel holds the lock; the recovery waits, then sees CANCELLED ──
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 1m));
        var po = (await _k.ConfirmAsync(so)).A("productionOrders").Single().G("productionOrderUuid");
        await _k.Ok(_k.Post($"/api/production-orders/{po}/cancel", new { Reason = "lost" }), "cancel the PO");

        var (conn, tx) = await HoldOrderLockAsync(so);
        await using (conn)
        await using (tx)
        {
            var recovery = _k.TryCreateProductionOrders(so);
            await Task.WhenAny(recovery, Task.Delay(TimeSpan.FromSeconds(3)));
            recovery.IsCompleted.Should().BeFalse("D-17: creation takes the order's lock and must wait for it");
            await CancelInsideAsync(conn, tx, so);
            var answer = await recovery;
            answer.Status.Should().BeOneOf(new[] { HttpStatusCode.OK, HttpStatusCode.BadRequest }, $"under the lock it re-read a CANCELLED order — {answer}");
        }
        (await LivePosAsync(so)).Should().BeEmpty("nothing is created for a cancelled order");

        // ── A real race, a few times: whichever wins, the cancelled order keeps no live production order below IN_PROGRESS ──
        for (var round = 1; round <= 3; round++)
        {
            var r = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 1m));
            var first = (await _k.ConfirmAsync(r)).A("productionOrders").Single().G("productionOrderUuid");
            await _k.Ok(_k.Post($"/api/production-orders/{first}/cancel", new { Reason = "lost" }), "cancel the PO");
            var (cancel, recover) = (_k.TryCancelOrder(r, "race"), _k.TryCreateProductionOrders(r));
            await Task.WhenAll(cancel, recover);
            (await cancel).ShouldBe(HttpStatusCode.OK, $"round {round}: the cancel goes through");
            (await _k.GetSaleOrderAsync(r)).S("status").Should().Be("CANCELLED");
            var live = (await _k.ProductionOrdersOfAsync(r)).Where(p => OpenBelowInProgress.Contains(p.S("status"))).ToList();
            live.Should().BeEmpty($"round {round}: D-22 — a PO created just before the cancel is cancelled by it; one after it is never created");
            (await _k.SoDemandsAsync(r)).Where(d => (string?)d["Status"] == "OPEN").Should().BeEmpty($"round {round}: no open SALES_ORDER demand");
        }
    }

    [Fact]
    public async Task The_FGR_hand_over_waits_for_a_cancel_holding_the_orders_lock_and_a_real_race_leaves_no_live_delivery()
    {
        var w = await _k.MtoWorldAsync("CVF", rawStock: 100m);
        async Task<(Guid So, Guid Po)> AcceptedAsync(decimal qty)
        {
            var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, qty));
            var po = (await _k.ConfirmAsync(so)).A("productionOrders").Single().G("productionOrderUuid");
            await _k.IssueAndStartAsync(po);
            await _k.ReportAndCompleteAsync(po, qty);
            await _k.Ok(_k.TryInspect(po, await _k.InspectorAsync(), qty, 0m), "QI");
            return (so, po);
        }

        // ── Deterministic ──
        var (so, po) = await AcceptedAsync(2m);
        var (conn, tx) = await HoldOrderLockAsync(so);
        Task<Api> fgr;
        await using (conn)
        await using (tx)
        {
            fgr = _k.TryFgr(po, 2m);
            await Task.WhenAny(fgr, Task.Delay(TimeSpan.FromSeconds(3)));
            // Either the FGR call is still waiting in its hand-over, or the hand-over gave up and left the flag for the sweep.
            if (fgr.IsCompleted)
                (await _k.DeliveryPendingSinceAsync(po)).Should().NotBeNull("a hand-over that couldn't take the lock stays pending");
            await CancelInsideAsync(conn, tx, so);
        }
        (await fgr).ShouldBe(HttpStatusCode.OK, "the FGR itself commits whatever the hand-over meets");
        (await _k.PoStatusAsync(po)).Should().Be("COMPLETED");
        await _k.BackdateDeliveryPendingIfSetAsync(po);
        await _k.RunProductionDeliverySweepAsync();
        (await _k.DeliveriesFromPoAsync(po)).Should().BeEmpty("the creator re-read a CANCELLED order under its lock");
        (await _k.DeliveryPendingSinceAsync(po)).Should().BeNull("nothing left pending");

        // ── A real race, a few times ──
        for (var round = 1; round <= 3; round++)
        {
            var (r, rpo) = await AcceptedAsync(1m);
            var cancel = _k.TryCancelOrder(r, "race");
            var receipt = _k.TryFgr(rpo, 1m);
            await Task.WhenAll(cancel, receipt);
            (await cancel).ShouldBe(HttpStatusCode.OK, $"round {round}: cancel");
            (await receipt).ShouldBe(HttpStatusCode.OK, $"round {round}: FGR");
            await _k.BackdateDeliveryPendingIfSetAsync(rpo);
            await _k.RunProductionDeliverySweepAsync();
            var live = (await _k.SoDeliveriesAsync(r)).Where(d => d.S("status") != "CANCELLED").ToList();
            live.Should().BeEmpty($"round {round}: a delivery made just before the cancel is cancelled by it (A33 D-15); none is made after");
        }
    }

    [Fact]
    public async Task A_replayed_hand_over_creates_nothing_and_a_lost_one_is_recreated_once()
    {
        var w = await _k.MtoWorldAsync("RPL", rawStock: 100m);
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 3m));
        var po = (await _k.ConfirmAsync(so)).A("productionOrders").Single().G("productionOrderUuid");
        await _k.FloorToFgrAsync(po, 3m, 3m, 0m);
        var d1 = (await _k.ProductionOrderAsync(po)).G("deliveryOrderUuid");

        // Replays: the sweep (flag forced), and two "Create delivery now" clicks at once.
        await _k.BackdateDeliveryPendingAsync(po);
        await _k.RunProductionDeliverySweepAsync();
        var clicks = await Task.WhenAll(_k.TryCreateDeliveryNow(po), _k.TryCreateDeliveryNow(po));
        clicks.Should().OnlyContain(c => c.Status == HttpStatusCode.OK || c.Status == HttpStatusCode.Conflict);
        clicks.Where(c => c.Status == HttpStatusCode.OK).Should().OnlyContain(c => c.Result.D("quantityCreated") == 0m);
        (await _k.DeliveriesFromPoAsync(po)).Select(d => d.G("uuid")).Should().Equal(d1);
        (await _k.QtyOnLiveDeliveriesFromPoAsync(po)).Should().Be(3m);
        var p = await _k.ProductionOrderAsync(po);
        (p.NG("deliveryOrderUuid"), p.B("deliveryCreationPending")).Should().Be(((Guid?)d1, false), "the sweep stamped the latest delivery and cleared the flag");

        // A lost hand-over: the DRAFT is gone and the PO still pending → the sweep, two clicks and the sweep again: once.
        await _k.Ok(_k.Delete($"/api/logistics/deliveries/{d1}"), "delete the DRAFT");
        await _k.BackdateDeliveryPendingAsync(po);
        await Task.WhenAll(_k.RunProductionDeliverySweepAsync(), _k.TryCreateDeliveryNow(po), _k.TryCreateDeliveryNow(po));
        await _k.RunProductionDeliverySweepAsync();
        var live = await _k.DeliveriesFromPoAsync(po);
        live.Where(d => d.S("status") != "CANCELLED").Should().ContainSingle("re-created exactly once");
        (await _k.QtyOnLiveDeliveriesFromPoAsync(po)).Should().Be(3m, "the D-20 formula: accepted − already on deliveries");
        (await _k.ProductionOrderAsync(po)).NG("deliveryOrderUuid").Should().Be(live.Single(d => d.S("status") != "CANCELLED").G("uuid"));
    }
}
