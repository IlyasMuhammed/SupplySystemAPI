using System.Net;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;

namespace SMS.Integration.Tests.FulfillmentRoutes;

/// <summary>
/// A33-PF-06: what A33 must not break, and the concurrency and recovery paths it adds.
/// <list type="bullet">
/// <item><b>No route = today's path (R-1):</b> a transfer, a PO's inbound ASN and a hand-raised sale-order delivery
/// carry no route and an empty tracker. Confirm-pick leaves them PICKED (no auto LOOSE unit), and packing,
/// staging, goods issue and pickup work as before.</item>
/// <item><b>C-2:</b> an auto-created DRAFT delivery holds nothing. A SPLIT line keeps its reservable quantity and its
/// deficit, also through the reconcile in the expiry sweep, and the back-to-back PO's GRN still reserves for
/// it.</item>
/// <item><b>D-15:</b> SO cancel cancels the DRAFT and RELEASED deliveries (holds returned) and keeps the
/// goods-issued one, listing both.</item>
/// <item><b>D-12:</b> the sweep and the recovery button create missing deliveries exactly once. A delivery a user
/// cancelled is not re-created by the sweep, and with auto-create off nothing is created at confirm.</item>
/// <item><b>R-6:</b> two racing confirms reserve once and create the deliveries once, and two racing recoveries
/// create nothing twice.</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~FulfillmentRouteRegressionE2ETests</c>.</para>
/// </summary>
public sealed class FulfillmentRouteRegressionE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public FulfillmentRouteRegressionE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "R6");
    }

    private async Task<(Guid Pkr, Warehouse Wh, Partner Vendor, Partner Customer)> SetupAsync()
    {
        var pkr = await _k.PkrBaseAsync();
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        return (pkr, await _k.CreateWarehouseAsync(), await _k.CreateVendorAsync("Reg Vendor"), await _k.CreateCustomerAsync("Reg Customer"));
    }

    /// <summary>
    /// DEM's D-12 sweep, run in-process from a fresh scope as Hangfire would: the concrete job type of the Demand
    /// module whose name says delivery + sweep, and its parameterless (or all-optional) Task method.
    /// </summary>
    private async Task RunDeliverySweepAsync()
    {
        var asm = typeof(SMS.Modules.Demand.Data.DemandDbContext).Assembly;
        var type = asm.GetTypes().Single(t => t is { IsClass: true, IsAbstract: false }
                                              && t.Name.Contains("Delivery") && t.Name.Contains("Sweep") && t.Name.EndsWith("Job"));
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => typeof(Task).IsAssignableFrom(m.ReturnType) && m.DeclaringType == type && m.GetParameters().All(p => p.IsOptional));
        await using var scope = _f.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService(type);
        await (Task)method.Invoke(job, method.GetParameters().Select(p => p.DefaultValue).ToArray())!;
    }

    private async Task<int> LiveDeliveryCountAsync(Guid so) =>
        (await _k.SoDeliveriesAsync(so)).Count(d => d.S("status") is not "CANCELLED");

    private async Task<DateTime?> PendingSinceAsync(Guid so)
    {
        var v = (await _f.QueryAsync("SELECT DeliveryCreationPendingSince AS P FROM demand.sale_orders WHERE UUID = @so", ("@so", so))).Single()["P"];
        return v as DateTime?;
    }

    // ── R-1: no route, today's path ─────────────────────────────────────────────

    [Fact]
    public async Task Deliveries_without_a_route_walk_todays_full_path()
    {
        var (pkr, whA, vendor, customer) = await SetupAsync();
        var whB = await _k.CreateWarehouseAsync();
        var p = await _k.CreateProductAsync("Legacy Widget", 10m, 20m);
        var (po, _) = await _k.StockUpAsync(vendor, whA, (p, 30m, 10m));

        // A transfer (plain create).
        var transfer = (await _k.Ok(_k.Post("/api/logistics/deliveries", new
        {
            SourceType = "TRANSFER", ShipFromWarehouseUuid = whA.Uuid, ShipToWarehouseUuid = whB.Uuid,
            Lines = new[] { new { VariantUuid = p.VariantUuid, ItemDescription = p.Name, QtyOrdered = 5m } }
        }), "create a transfer")).GetGuid();
        var t = await _k.DeliveryAsync(transfer);
        (t.IsNull("fulfillmentRouteUuid"), t.A("routeSteps").Count, t.B("requiresApproval")).Should().Be((true, 0, false), "R-1: no route");
        await _k.ReleaseAsync(transfer);
        await _k.PickAllAsync(transfer);
        (await _k.DeliveryStatusAsync(transfer)).Should().Be("PICKED", "no route: confirm-pick never auto-packs");
        (await _k.PackagesAsync(transfer)).Should().BeEmpty();
        await _k.PackAllAsync(transfer, "PALLET");
        (await _k.DeliveryStatusAsync(transfer)).Should().Be("PACKED", "no route: never auto-staged");
        await _k.Ok(_k.Stage(transfer), "stage");
        (await _k.Approve(transfer)).ShouldBe(HttpStatusCode.BadRequest, "no route has no APPROVAL step");
        var gi = await _k.Ok(_k.GoodsIssue(transfer), "goods issue the transfer");
        (gi.S("status"), gi.B("postedStock")).Should().Be(("GOODS_ISSUED", true));

        // An inbound ASN from a PO.
        var po2 = await _k.CreatePurchaseOrderAsync(vendor, whA, (p, 3m, 10m));
        await _k.SubmitApproveAndSendPoAsync(po2);
        var asn = (await _k.Ok(_k.Post("/api/logistics/deliveries/from-source", new { SourceType = "PO", SourceUuid = po2 }), "ASN from the PO")).GetGuid();
        var a = await _k.DeliveryAsync(asn);
        (a.S("direction"), a.IsNull("fulfillmentRouteUuid"), a.A("routeSteps").Count).Should().Be(("INBOUND", true, 0));
        _ = po;

        // A sale-order delivery raised by hand (auto-create off): null route, legacy SELF_PICKUP path.
        (await _k.SaleOrderConfigAsync()).B("autoCreateDeliveriesOnConfirm").Should().BeTrue("D-1: on by default");
        await _k.SetSaleOrderConfigAsync(("autoCreateDeliveriesOnConfirm", false));
        // An older client that doesn't know the field can't switch it back on (or off) by omission (contract §5).
        await _k.SaveSaleOrderConfigOmittingAsync("autoCreateDeliveriesOnConfirm");
        (await _k.SaleOrderConfigAsync()).B("autoCreateDeliveriesOnConfirm").Should().BeFalse("null keeps the current value");
        try
        {
            var so = await _k.CreateOrderAsync(customer, pkr, "SELF_PICKUP", null, RLine(p, 4m));
            var confirmed = await _k.ConfirmAsync(so);
            (confirmed.A("deliveries").Count, confirmed.B("deliveryCreationFailed")).Should().Be((0, false), "auto-create is off");
            (await PendingSinceAsync(so)).Should().BeNull("nothing is pending when auto-create is off");
            await RunDeliverySweepAsync();
            (await _k.SoDeliveriesAsync(so)).Should().BeEmpty("the sweep creates only what confirm meant to");

            var legacy = (await _k.Ok(_k.Post($"/api/sale-orders/{so}/create-delivery", new { }), "create-delivery by hand")).GetGuid();
            var l = await _k.DeliveryAsync(legacy);
            (l.IsNull("fulfillmentRouteUuid"), l.A("routeSteps").Count, l.S("deliveryMode")).Should().Be((true, 0, "SELF_PICKUP"));
            await _k.ReleaseAsync(legacy);
            await _k.PickAllAsync(legacy);
            (await _k.DeliveryStatusAsync(legacy)).Should().Be("PICKED");
            await _k.PackAllAsync(legacy);
            await _k.Ok(_k.RecordCollection(legacy), "pickup from PACKED, as today");
            (await _k.DeliveryStatusAsync(legacy)).Should().Be("DELIVERED");
            (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("FULFILLED");
            await _k.InvoiceAsync(legacy);
        }
        finally
        {
            await _k.SetSaleOrderConfigAsync(("autoCreateDeliveriesOnConfirm", true));
        }
    }

    // ── C-2 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_auto_created_draft_delivery_holds_nothing_so_the_deficit_stays_and_the_back_to_back_GRN_still_reserves()
    {
        var (pkr, wh, vendor, customer) = await SetupAsync();
        var p = await _k.CreateProductAsync("Split Widget", 10m, 20m);
        await _k.StockUpAsync(vendor, wh, (p, 4m, 10m));
        // The vendor is the variant's preferred supplier, so the auto-PO for the deficit names it; DRAFT_ONLY lets the
        // test submit, approve and send it like any PO.
        var card = (await _k.Ok(_k.Post("/api/rate-cards", new
        {
            VariantUuid = p.VariantUuid, SupplierUuid = vendor.Uuid, VendorUnitCost = 10m, EffectiveFrom = PreOrder.Day(PreOrder.Today), CurrencyId = pkr
        }), "rate card")).GetGuid();
        await _k.Ok(_k.Patch($"/api/rate-cards/{card}/preferred", null), "preferred supplier");
        await _k.SetSaleOrderConfigAsync(("autoPoEnabled", true), ("supplierSelectionMode", "DEFAULT_SUPPLIER"), ("autoPoApprovalMode", "DRAFT_ONLY"));

        var so = await _k.CreateOrderAsync(customer, pkr, "SELF_PICKUP", null, RLine(p, 10m));
        var confirmed = await _k.ConfirmAsync(so);
        var d = confirmed.A("deliveries").Should().ContainSingle().Which.G("deliveryUuid");
        (await _k.DeliveryAsync(d)).A("lines").Single().D("qtyOrdered").Should().Be(10m, "the DRAFT covers the whole line (D-8)");

        async Task<JsonElement> Line() => (await _k.GetSaleOrderAsync(so)).A("lines").Single();
        var line = await Line();
        (line.S("fulfillmentMode"), line.D("reservedQty"), line.D("reservableQty"), line.ND("deficitQty"), line.S("deliveryIndicator"))
            .Should().Be(("SPLIT", 4m, 6m, (decimal?)6m, "YELLOW"), "C-2: a DRAFT delivery is not a hold");

        // The expiry sweep reconciles every open line; it must not read the DRAFT as held and zero the deficit.
        await _f.RunInScopeAsync<SMS.Modules.Demand.Services.ReservationExpirySweepJob>(j => j.RunAsync());
        (await Line()).ND("deficitQty").Should().Be(6m, "C-2: Reconcile keeps the deficit");

        // The back-to-back PO arrives and its GRN reserves the 6 for the line.
        var linked = await SapKit.WaitForAsync(Line, l => !l.IsNull("linkedPoId"), "the auto-PO for the deficit", 60);
        var poUuid = (Guid)(await _f.QueryAsync("SELECT UUID FROM demand.purchase_orders WHERE Id = @id", ("@id", linked.I("linkedPoId")))).Single()["UUID"]!;
        await _k.SubmitApproveAndSendPoAsync(poUuid);
        await _k.ReceiveAllAsync(poUuid, wh);
        var covered = await SapKit.WaitForAsync(Line, l => l.D("reservedQty") == 10m, "the GRN reserves for the line", 30);
        (covered.ND("deficitQty"), covered.D("reservableQty"), covered.S("deliveryIndicator")).Should().Be(((decimal?)0m, 0m, "BLUE"));
        (await _k.SoLedgerHeldAsync(so)).Should().Be(10m);

        // The DRAFT then releases for all 10, taking the order's holds.
        await _k.ReleaseAsync(d);
        (await _k.SoLedgerHeldAsync(so), await _k.DeliveryLedgerHeldAsync(d)).Should().Be((0m, 10m));
        var held = await Line();
        (held.D("reservedQty"), held.D("reservableQty"), held.S("deliveryIndicator")).Should().Be((0m, 0m, "BLUE"),
            "reservedQty is the order's own holds (A32); the released delivery's 10 still cover the line and leave nothing reservable");
        await _k.PickAllAsync(d);
        await _k.Ok(_k.RecordCollection(d), "collect");
        (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("FULFILLED");
    }

    // ── C-3 / C-4 / D-8 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task One_route_over_two_warehouses_makes_two_deliveries_and_partial_fulfilment_off_does_not_refuse_the_split()
    {
        var (pkr, wh1, vendor, customer) = await SetupAsync();
        var wh2 = await _k.CreateWarehouseAsync();
        var address = await _k.ShippingAddressAsync(customer);
        var north = await _k.CreateProductAsync("North Widget", 5m, 9m);
        var south = await _k.CreateProductAsync("South Widget", 5m, 9m);
        await _k.StockUpAsync(vendor, wh1, (north, 10m, 5m));
        await _k.StockUpAsync(vendor, wh2, (south, 10m, 5m));
        var pickPackShip = await _k.RouteUuidAsync(PickPackShip);

        await _k.SetSaleOrderConfigAsync(("partialFulfillmentAllowed", false));
        try
        {
            // north and south on PICK_AND_SHIP (the SHIP default) but held in different warehouses; a third line on PICK_PACK_SHIP.
            var so = await _k.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(north, 2m), RLine(south, 3m), RLine(north, 1m, route: pickPackShip));
            var confirmed = await _k.ConfirmAsync(so);
            confirmed.B("deliveryCreationFailed").Should().BeFalse($"D-8: the split is not partial fulfilment — {J.Short(confirmed)}");
            confirmed.A("deliveries").Select(d => (d.S("routeCode"), d.NG("shipFromWarehouseUuid"), d.I("lineCount")))
                .Should().BeEquivalentTo(new[] { (PickAndShip, (Guid?)wh1.Uuid, 1), (PickAndShip, (Guid?)wh2.Uuid, 1), (PickPackShip, (Guid?)wh1.Uuid, 1) },
                    "C-4: route × ship-from warehouse");

            // Each releases from its own warehouse, and nothing is left for a hand-raised delivery (C-1a).
            foreach (var d in confirmed.A("deliveries"))
                await _k.ReleaseAsync(d.G("deliveryUuid"));
            (await _k.SoLedgerHeldAsync(so)).Should().Be(0m, "every hold moved to its delivery");
            (await _k.Post($"/api/sale-orders/{so}/create-delivery", new { })).Status.Should().NotBe(HttpStatusCode.OK, "nothing is outstanding");
        }
        finally
        {
            await _k.SetSaleOrderConfigAsync(("partialFulfillmentAllowed", true));
        }
    }

    // ── D-15 ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_the_order_cancels_its_open_deliveries_and_keeps_the_goods_issued_one()
    {
        var (pkr, wh, vendor, customer) = await SetupAsync();
        var address = await _k.ShippingAddressAsync(customer);
        var a = await _k.CreateProductAsync("Issued Widget", 10m, 20m);
        var b = await _k.CreateProductAsync("Released Widget", 10m, 20m);
        var c = await _k.CreateProductAsync("Draft Widget", 10m, 20m);
        await _k.StockUpAsync(vendor, wh, (a, 10m, 10m), (b, 10m, 10m), (c, 10m, 10m));

        var so = await _k.CreateOrderAsync(customer, pkr, "SHIP", address,
            RLine(a, 2m, route: await _k.RouteUuidAsync(PickAndShip)),
            RLine(b, 3m, route: await _k.RouteUuidAsync(PickPackShip)),
            RLine(c, 4m, route: await _k.RouteUuidAsync(PickOnly)));
        var created = (await _k.ConfirmAsync(so)).A("deliveries");
        var dA = created.Single(d => d.S("routeCode") == PickAndShip).G("deliveryUuid");
        var dB = created.Single(d => d.S("routeCode") == PickPackShip).G("deliveryUuid");
        var dC = created.Single(d => d.S("routeCode") == PickOnly).G("deliveryUuid");

        await _k.ReleaseAsync(dA);
        await _k.PickAllAsync(dA);
        await _k.Ok(_k.GoodsIssue(dA), "issue A");
        await _k.ReleaseAsync(dB);
        (await _k.DeliveryLedgerHeldAsync(dB)).Should().Be(3m);
        (await _k.SoLedgerHeldAsync(so)).Should().Be(4m, "only C's hold is still the order's");

        var result = await _k.Ok(_k.Post($"/api/sale-orders/{so}/cancel", new { Reason = "customer withdrew" }), "cancel the order");
        result.A("cancelledDeliveries").Select(d => (d.G("deliveryUuid"), d.S("status")))
            .Should().BeEquivalentTo(new[] { (dB, "CANCELLED"), (dC, "CANCELLED") }, "T-C4-06");
        result.A("issuedDeliveries").Select(d => (d.G("deliveryUuid"), d.S("status")))
            .Should().BeEquivalentTo(new[] { (dA, "GOODS_ISSUED") }, "T-C4-07: kept, needs a manual reversal");

        (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("CANCELLED");
        (await _k.DeliveryStatusAsync(dA)).Should().Be("GOODS_ISSUED");
        (await _k.DeliveryStatusAsync(dB)).Should().Be("CANCELLED");
        (await _k.DeliveryStatusAsync(dC)).Should().Be("CANCELLED");
        (await _k.DeliveryLedgerHeldAsync(dB)).Should().Be(0m, "the released delivery's hold is given back");
        (await _k.SoLedgerHeldAsync(so)).Should().Be(0m);
        (await PendingSinceAsync(so)).Should().BeNull();

        // Neither the sweep nor a recovery call brings anything back for a cancelled order.
        await RunDeliverySweepAsync();
        (await LiveDeliveryCountAsync(so)).Should().Be(1);
        (await _k.Post($"/api/sale-orders/{so}/create-deliveries")).ShouldBe(HttpStatusCode.BadRequest, "recovery is for CONFIRMED / PARTIALLY_FULFILLED orders");
        (await _k.Post($"/api/sale-orders/{so}/cancel", new { Reason = "again" })).Status.Should().NotBe(HttpStatusCode.OK, "an order cancels once");
    }

    // ── D-12 ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_sweep_and_the_recovery_button_create_missing_deliveries_exactly_once()
    {
        var (pkr, wh, vendor, customer) = await SetupAsync();
        var address = await _k.ShippingAddressAsync(customer);
        var a = await _k.CreateProductAsync("Sweep A", 10m, 20m);
        var b = await _k.CreateProductAsync("Sweep B", 10m, 20m);
        await _k.StockUpAsync(vendor, wh, (a, 10m, 10m), (b, 10m, 10m));
        var pickPackShip = await _k.RouteUuidAsync(PickPackShip);

        var so = await _k.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(a, 2m), RLine(b, 3m, route: pickPackShip));
        var first = (await _k.ConfirmAsync(so)).A("deliveries");
        first.Should().HaveCount(2);
        (await PendingSinceAsync(so)).Should().BeNull("the confirm's own creator call succeeded");

        // Simulate a post-commit creation that never happened: the drafts gone, the order still marked pending.
        foreach (var d in first)
            await _k.Ok(_k.Delete($"/api/logistics/deliveries/{d.G("deliveryUuid")}"), "delete the draft");
        (await _k.SoDeliveriesAsync(so)).Should().BeEmpty();
        await _f.ExecuteAsync("UPDATE demand.sale_orders SET DeliveryCreationPendingSince = DATEADD(HOUR, -1, SYSUTCDATETIME()) WHERE UUID = @so", ("@so", so));

        await RunDeliverySweepAsync();
        var swept = await _k.SoDeliveriesAsync(so);
        swept.Select(d => d.S("fulfillmentRouteCode")).Should().BeEquivalentTo(new[] { PickAndShip, PickPackShip }, "the sweep re-created both");
        (await PendingSinceAsync(so)).Should().BeNull();

        await RunDeliverySweepAsync();
        var recovery = await _k.Ok(_k.Post($"/api/sale-orders/{so}/create-deliveries"), "recovery after the sweep");
        recovery.A("created").Should().BeEmpty("idempotent");
        (await LiveDeliveryCountAsync(so)).Should().Be(2, "nothing doubled");

        // A delivery a user cancels on purpose: the sweep leaves it; the recovery button raises the remainder once.
        var cancelled = swept.Single(d => d.S("fulfillmentRouteCode") == PickPackShip).G("uuid");
        await _k.Ok(_k.Post($"/api/logistics/deliveries/{cancelled}/cancel", new { Reason = "re-plan" }), "cancel one delivery");
        await RunDeliverySweepAsync();
        (await LiveDeliveryCountAsync(so)).Should().Be(1, "the sweep doesn't undo a person's cancel");
        var again = await _k.Ok(_k.Post($"/api/sale-orders/{so}/create-deliveries"), "recovery for the remainder");
        again.A("created").Should().ContainSingle().Which.S("routeCode").Should().Be(PickPackShip);
        (await _k.Ok(_k.Post($"/api/sale-orders/{so}/create-deliveries"), "recovery again")).A("created").Should().BeEmpty();
        (await LiveDeliveryCountAsync(so)).Should().Be(2);
    }

    // ── R-6 ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Racing_confirms_and_racing_recoveries_create_the_deliveries_once()
    {
        var (pkr, wh, vendor, customer) = await SetupAsync();
        var address = await _k.ShippingAddressAsync(customer);
        var pickPackShip = await _k.RouteUuidAsync(PickPackShip);

        for (var round = 1; round <= 3; round++)
        {
            var a = await _k.CreateProductAsync($"Race A{round}", 10m, 20m);
            var b = await _k.CreateProductAsync($"Race B{round}", 10m, 20m);
            await _k.StockUpAsync(vendor, wh, (a, 10m, 10m), (b, 10m, 10m));
            var so = await _k.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(a, 4m), RLine(b, 2m, route: pickPackShip));

            var results = await Task.WhenAll(_k.TryConfirm(so), _k.TryConfirm(so));
            results.Count(r => r.Status == HttpStatusCode.OK).Should().Be(1, $"round {round}: one confirm wins — {string.Join(" / ", results.Select(r => r.ToString()))}");
            results.Single(r => r.Status != HttpStatusCode.OK).Status.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
            (await _k.SoLedgerHeldAsync(so)).Should().Be(6m, $"round {round}: held once");
            (await _k.SoDeliveriesAsync(so)).Should().HaveCount(2, $"round {round}: one delivery per route, not two");

            // Racing recoveries (two clicks, or a click while the sweep runs) create nothing more…
            var recoveries = await Task.WhenAll(
                _k.Post($"/api/sale-orders/{so}/create-deliveries"), _k.Post($"/api/sale-orders/{so}/create-deliveries"));
            recoveries.Should().OnlyContain(r => r.Status == HttpStatusCode.OK || r.Status == HttpStatusCode.Conflict);
            (await _k.SoDeliveriesAsync(so)).Should().HaveCount(2, $"round {round}: idempotent under a race");

            // …and after the drafts are gone, racing recoveries create them exactly once.
            foreach (var d in await _k.SoDeliveriesAsync(so))
                await _k.Ok(_k.Delete($"/api/logistics/deliveries/{d.G("uuid")}"), "delete the draft");
            await Task.WhenAll(
                _k.Post($"/api/sale-orders/{so}/create-deliveries"), _k.Post($"/api/sale-orders/{so}/create-deliveries"), RunDeliverySweepAsync());
            var after = await _k.SoDeliveriesAsync(so);
            after.Should().HaveCount(2, $"round {round}: one per route after a racing re-create");
            after.SelectMany(d => new[] { d.G("uuid") }).Should().OnlyHaveUniqueItems();
            var qty = await _f.QueryAsync(
                "SELECT ISNULL(SUM(l.QtyOrdered), 0) AS Q FROM logistics.delivery_order_lines l JOIN logistics.delivery_orders d ON d.Id = l.DeliveryOrderId " +
                "WHERE d.SaleOrderUuid = @so AND d.IsDelete = 0", ("@so", so));
            Convert.ToDecimal(qty[0]["Q"]).Should().Be(6m, $"round {round}: the order's 6 units sit on deliveries once");
        }
    }
}
