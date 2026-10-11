using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using SMS.Modules.Auth.Domain;
using Xunit;
using static SMS.Integration.Tests.Pos.PosKit;

namespace SMS.Integration.Tests.Pos;

/// <summary>
/// A38 (QA, G1–G12) — Point of Sale end to end on the real host (LocalDB) with the REAL Inventory, Finance, Auth, Tenancy and
/// Suppliers implementations wired together (the module unit tests used fakes). Each test runs in an ENTERPRISE organization of
/// its own created through the platform admin's create path (MODULE_POS licensed from the plan template, D-20), driven by its
/// admin, a POS_SUPERVISOR (the device sign-in) and a POS_CASHIER (PIN sign-in). Spec scenarios (TS-038-nn) → tests:
/// <list type="bullet">
/// <item>TS-01..05, 15..20, hub (TS-56/57 at API level) — <see cref="Stores_terminals_staff_pins_and_the_hub"/>.</item>
/// <item>TS-06..14 — <see cref="Shift_lifecycle_cash_reconciliation_and_stale_alert"/>.</item>
/// <item>TS-21..28, 44..48, 60 — <see cref="Sales_post_to_real_inventory_and_finance"/>.</item>
/// <item>TS-49, 50, 51, 59 — <see cref="Order_sync_is_idempotent_and_batches_replay_in_order"/>.</item>
/// <item>TS-31..36 — <see cref="Credit_sales_follow_the_limit_online_and_flag_offline"/>.</item>
/// <item>TS-29, 30, 37..43 — <see cref="Voids_and_returns_reverse_stock_money_and_shift_totals"/>.</item>
/// <item>Posting retry (E4) — <see cref="A_failed_finance_posting_is_finished_by_the_retry_job"/>.</item>
/// <item>TS-52..55, 58, permissions/roles — <see cref="Module_licence_grace_permissions_and_pwa_sync"/>.</item>
/// </list>
/// PWA-only behaviour (IndexedDB, service worker, the SignalR client UI) is covered by the FEPOS specs; here the server side of it:
/// offline-flagged pushes, batch replay, the hub's JoinTerminal / Heartbeat / pushes over a raw SignalR WebSocket.
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~PosE2ETests</c>.</para>
/// </summary>
public sealed class PosE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;
    private readonly SapWebApplicationFactory _f;

    public PosE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _root = new SapKit(factory, "A38");
    }

    // ── TS-01..05, 15..20 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Stores_terminals_staff_pins_and_the_hub()
    {
        var w = await _root.PosWorldAsync("STO", stock: false);
        var k = w.K;
        var sup = w.Supervisor.Client;

        // TS-01/02: one active store per warehouse
        var store = await k.Ok(k.Get($"/api/pos/stores/{w.Store}"), "read store");
        (store.G("warehouseUuid"), store.S("currencyCode"), store.D("defaultTaxPercent"), store.B("isActive")).Should().Be((w.Wh.Uuid, "PKR", 17m, true));
        (await k.CreateStore("S2" + Guid.NewGuid().ToString("N")[..5], w.Wh.Uuid))
            .ShouldFail(HttpStatusCode.BadRequest, "POS-STR-02", $"Warehouse is already linked to store {w.StoreCode}.");
        var wh2 = await k.CreateWarehouseAsync();
        (await k.CreateStore(w.StoreCode, wh2.Uuid))
            .ShouldFail(HttpStatusCode.BadRequest, "unique code", $"Store code {w.StoreCode} already exists.");
        // FEATURE_POS_MULTI_STORE off ⇒ one active store only (D-30)
        await k.FeatureOkAsync(Module, "FEATURE_POS_MULTI_STORE", false);
        (await k.CreateStore("S2" + Guid.NewGuid().ToString("N")[..5], wh2.Uuid))
            .ShouldFail(HttpStatusCode.BadRequest, "multi-store off", "Multi-store is not enabled for your organization: only one active store is allowed.");
        await k.FeatureOkAsync(Module, "FEATURE_POS_MULTI_STORE", true);
        await k.Ok(k.CreateStore("S2" + Guid.NewGuid().ToString("N")[..5], wh2.Uuid), "a second store on another warehouse");

        // TS-03: terminals
        (await k.Post($"/api/pos/stores/{w.Store}/terminals", new { code = "t1", name = "Again" }))
            .ShouldFail(HttpStatusCode.BadRequest, "code per store", "Terminal code already exists in this store.");
        var t2 = (await k.Ok(k.Post($"/api/pos/stores/{w.Store}/terminals", new { code = "t2", name = "Till 2" }), "terminal 2")).G("uuid");
        var terminals = await k.Ok(k.Get($"/api/pos/stores/{w.Store}/terminals"), "terminals");
        terminals.Items().Select(t => t.S("code")).Should().BeEquivalentTo("T1", "T2");

        // TS-04: bind / by-device / unbind (the supervisor binds with POS_SHIFT_OPEN as store staff)
        var device = Guid.NewGuid();
        var config = await k.Ok(k.Post($"/api/pos/terminals/{w.Terminal}/bind", new { deviceUuid = device }, sup), "bind");
        (config.P("terminal").B("deviceBound"), config.P("store").G("uuid")).Should().Be((true, w.Store));
        await k.Ok(k.Post($"/api/pos/terminals/{w.Terminal}/bind", new { deviceUuid = device }, sup), "re-binding the same device is idempotent");
        (await k.Post($"/api/pos/terminals/{w.Terminal}/bind", new { deviceUuid = Guid.NewGuid() }, sup))
            .ShouldFail(HttpStatusCode.BadRequest, "bound elsewhere", "The terminal is bound to another device. Unbind first.");
        (await k.Post($"/api/pos/terminals/{t2}/bind", new { deviceUuid = device }, sup))
            .ShouldFail(HttpStatusCode.BadRequest, "device in use", "This device is already bound to terminal T1. Unbind it first.");
        (await k.Ok(k.Get($"/api/pos/terminals/by-device/{device}", sup), "by-device")).P("terminal").G("uuid").Should().Be(w.Terminal);
        (await k.Post($"/api/pos/terminals/{w.Terminal}/unbind", null, sup)).Status.Should().Be(HttpStatusCode.Forbidden, "unbind needs POS_TERMINAL_MANAGE");

        // hub: JoinTerminal / Heartbeat, then the unbind pushes ForceLogout to the terminal group (D-32)
        await using (var hub = await HubProbe.ConnectAsync(_f, HubProbe.TokenOf(sup)))
        {
            hub.Closed.Should().BeFalse("a supervisor of an organization with MODULE_POS may connect");
            (await hub.InvokeAsync("JoinTerminal", w.Terminal)).Has("error").Should().BeFalse();
            (await hub.InvokeAsync("Heartbeat", w.Terminal)).Has("error").Should().BeFalse();
            (await hub.InvokeAsync("JoinTerminal", Guid.NewGuid())).S("error").Should().Contain("Terminal not found.");
            var status = await k.Ok(k.Get($"/api/pos/terminals/{w.Terminal}/status"), "terminal status");
            (status.B("isOnline"), status.B("deviceBound")).Should().Be((true, true), "JoinTerminal stamps lastSeenAt");

            await k.Ok(k.Post($"/api/pos/terminals/{w.Terminal}/unbind"), "unbind");
            var logout = await hub.WaitForPushAsync("ForceLogout");
            logout.Should().NotBeNull("unbind pushes ForceLogout to the terminal");
            logout!.Value.S("reason").Should().Be("UNBOUND");
        }
        (await k.Get($"/api/pos/terminals/by-device/{device}", sup))
            .ShouldFail(HttpStatusCode.NotFound, "unbound", "This device is not bound to a terminal.");
        // a user without any POS permission is refused by the hub
        var nobody = await k.LoginWithPermissionsAsync("nopos", "INVENTORY_VIEW");
        await using (var refused = await HubProbe.ConnectAsync(_f, HubProbe.TokenOf(nobody)))
            refused.Closed.Should().BeTrue("D-32: no POS_* permission ⇒ aborted");

        // TS-05: deactivation with an open shift
        var shift = await w.OpenShiftAsync(100m);
        var t1 = terminals.Items().Single(t => t.S("code") == "T1");
        (await k.Put($"/api/pos/terminals/{w.Terminal}", new { code = "T1", name = "Till 1", isActive = false, rowVersion = (string?)null }))
            .ShouldFail(HttpStatusCode.BadRequest, "open shift", "Close the terminal's shift before deactivating it.");
        (await k.Patch($"/api/pos/stores/{w.Store}/status", new { isActive = false }))
            .ShouldFail(HttpStatusCode.BadRequest, "open shift", "Close all shifts before deactivating the store.");
        t1.S("rowVersion").Should().NotBeNullOrEmpty();

        // TS-15/16: staff and PINs
        var staff = await k.Ok(k.Get($"/api/pos/stores/{w.Store}/staff"), "staff");
        staff.Items().Select(s => (s.I("userId"), s.S("posRole"), s.B("hasPin")))
            .Should().BeEquivalentTo(new[] { (w.Cashier.Id, "CASHIER", true), (w.Supervisor.Id, "SUPERVISOR", true) });
        (await k.Staff(w.Store, w.Supervisor.Id, "SUPERVISOR", CashierPin))
            .ShouldFail(HttpStatusCode.BadRequest, "POS-CSH-02", "This PIN is already used by another staff member of this store.");
        (await k.Staff(w.Store, w.Supervisor.Id, "SUPERVISOR", "12a4")).ShouldFail(HttpStatusCode.BadRequest, "format", "PIN must be 4 to 6 digits.");
        (await k.Staff(w.Store, w.Supervisor.Id, "SUPERVISOR", "1234567")).ShouldFail(HttpStatusCode.BadRequest, "format", "PIN must be 4 to 6 digits.");

        // TS-17: PIN sign-in returns the cashier's own working tokens
        var signIn = await k.Ok(w.PinSignIn(CashierPin, sup), "PIN sign-in");
        UserIdOf(signIn.S("accessToken")!).Should().Be(w.Cashier.Id);
        (signIn.P("user").I("userId"), signIn.P("user").S("posRole")).Should().Be((w.Cashier.Id, "CASHIER"));
        signIn.P("user").A("permissions").Select(p => p.GetString()).Where(p => p!.StartsWith("POS_")).Should().Equal("POS_ORDER_CREATE", "POS_CREDIT_NOTE_REDEEM"); // A39 D-9
        signIn.S("refreshToken").Should().NotBeNullOrEmpty();
        var cashier = _f.CreateBearerClient(signIn.S("accessToken")!);
        (await k.Ok(k.Get($"/api/pos/terminals/{w.Terminal}/shifts/current", cashier), "the cashier token works")).G("uuid").Should().Be(shift);
        (await k.Get($"/api/pos/stores/{w.Store}", cashier)).Status.Should().Be(HttpStatusCode.Forbidden, "the cashier's own permissions, not the device's");
        // with userId: the same PIN of another user is wrong for them
        (await w.PinSignIn(CashierPin, sup, userId: w.Supervisor.Id)).ShouldFail(HttpStatusCode.BadRequest, "userId given", "Invalid PIN.");
        (await w.PinSignIn("12", sup)).ShouldFail(HttpStatusCode.BadRequest, "malformed does not count", "PIN must be 4 to 6 digits.");

        // TS-18/19: five wrong PINs lock the terminal, the right one does not open it, a supervisor unlocks
        for (var i = 1; i <= 3; i++)
            (await w.PinSignIn("9999", sup)).ShouldFail(HttpStatusCode.BadRequest, $"wrong PIN {i + 1}", "Invalid PIN.");
        (await w.PinSignIn("9999", sup)).ShouldFail(HttpStatusCode.BadRequest, "5th wrong PIN (the userId miss counted too)", "Terminal locked until");
        (await w.PinSignIn(CashierPin, sup)).ShouldFail(HttpStatusCode.BadRequest, "locked even with the right PIN", "Terminal locked until");
        (await k.Ok(k.Get($"/api/pos/terminals/{w.Terminal}/status"), "status")).IsNull("pinLockedUntil").Should().BeFalse();
        (await k.Post($"/api/pos/terminals/{w.Terminal}/unlock", null, cashier)).Status.Should().Be(HttpStatusCode.Forbidden, "unlock needs POS_SHIFT_OPEN");
        await k.Ok(k.Post($"/api/pos/terminals/{w.Terminal}/unlock", null, sup), "TS-20: supervisor unlocks");
        await k.Ok(w.PinSignIn(CashierPin, sup), "the right PIN works again");

        // a removed staff member can no longer sign in (soft remove)
        var extra = await k.PosUserAsync("POS_CASHIER", "extra");
        await k.Ok(k.Staff(w.Store, extra.Id, "CASHIER", "4321"), "extra cashier");
        await k.Ok(w.PinSignIn("4321", sup), "extra signs in");
        await k.Ok(k.Delete($"/api/pos/stores/{w.Store}/staff/{extra.Id}"), "remove staff");
        (await k.Ok(k.Get($"/api/pos/stores/{w.Store}/staff"), "staff")).Items().Single(s => s.I("userId") == extra.Id).B("isActive").Should().BeFalse();
        (await w.PinSignIn("4321", sup)).ShouldFail(HttpStatusCode.BadRequest, "removed staff", "Invalid PIN.");
        await k.Ok(k.Post($"/api/pos/terminals/{w.Terminal}/unlock", null, sup), "unlock after the probe");
    }

    // ── TS-06..14 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Shift_lifecycle_cash_reconciliation_and_stale_alert()
    {
        var w = await _root.PosWorldAsync("SHF", inclusive: true, variance: 5m);
        var k = w.K;
        var sup = w.Supervisor.Client;

        // TS-06/07: open with float 200 — the cashier may not, a second open is refused
        (await w.OpenShift(200m, w.Cashier.Client)).Status.Should().Be(HttpStatusCode.Forbidden, "TS-07: POS_SHIFT_OPEN");
        var opened = await k.Ok(w.OpenShift(200m), "TS-06: open");
        var s1 = opened.G("uuid");
        (opened.S("status"), opened.D("openingFloat"), opened.D("expectedCashClosing")).Should().Be(("OPEN", 200m, 200m));
        opened.S("shiftNumber").Should().MatchRegex($"^SH-{w.StoreCode}-\\d{{8}}-01$");
        (await w.OpenShift(200m)).ShouldFail(HttpStatusCode.BadRequest, "one shift per terminal", "Terminal already has an active shift.");

        // TS-08/09: three cash sales 50/30/20 (tax-inclusive store: the shelf price is the total), the first with change
        var cashier = await w.CashierClientAsync();
        var session = await k.Ok(k.Post("/api/pos/sessions", new { shiftUuid = s1 }, cashier), "cashier session");
        foreach (var (qty, paid, change) in new[] { (5m, 60m, 10m), (3m, 30m, 0m), (2m, 20m, 0m) })
        {
            var r = await w.SellAsync(w.Order(s1, [Line(w.A, qty, 10m)], [Pay("CASH", paid, change)], session: session.G("uuid")), cashier);
            var o = await w.OrderAsync(r.G("orderUuid"));
            (o.D("grandTotal"), o.D("changeAmount"), o.B("taxInclusive"), o.I("cashierUserId")).Should().Be((qty * 10m, change, true, w.Cashier.Id));
            o.D("taxTotal").Should().Be(Math.Round(qty * 10m - qty * 10m / 1.17m, 2, MidpointRounding.AwayFromZero));
        }
        var shift = await w.ShiftAsync(s1);
        (shift.D("totalCashSales"), shift.I("orderCount"), shift.D("grossTotal"), shift.D("expectedCashClosing")).Should().Be((100m, 3, 100m, 300m), "TS-09");
        var sess = shift.A("sessions").Single(x => x.G("uuid") == session.G("uuid"));
        (sess.D("cashSales"), sess.I("orderCount"), sess.I("userId"), sess.B("isActive")).Should().Be((100m, 3, w.Cashier.Id, true));

        // TS-10: safe drop
        (await k.Post($"/api/pos/shifts/{s1}/cash-movements", new { direction = "OUT", amount = 100m, reason = "SAFE_DROP" }, cashier))
            .Status.Should().Be(HttpStatusCode.Forbidden, "POS_CASH_MOVEMENT");
        var drop = new { direction = "OUT", amount = 100m, reason = "SAFE_DROP", note = "to the safe", localId = Guid.NewGuid().ToString("N") };
        await k.Ok(k.Post($"/api/pos/shifts/{s1}/cash-movements", drop, sup), "safe drop");
        await k.Ok(k.Post($"/api/pos/shifts/{s1}/cash-movements", drop, sup), "same localId: idempotent");
        shift = await w.ShiftAsync(s1);
        (shift.D("totalCashOut"), shift.D("expectedCashClosing"), shift.A("cashMovements").Count).Should().Be((100m, 200m, 1));
        var summary = await k.Ok(k.Get($"/api/pos/shifts/{s1}/summary"), "summary");
        summary.A("byMethod").Select(m => m.S("method")).Should().BeEquivalentTo("CASH", "CARD", "MOBILE", "BANK_TRANSFER", "CHEQUE", "ON_ACCOUNT",
            "STORE_CREDIT", "EXCHANGE"); // A39 API-CONTRACT §7
        summary.A("byMethod").Single(m => m.S("method") == "CASH").D("sales").Should().Be(100m);

        // start-close stops new online sales; close at 195 (|−5| ≤ 5) → RECONCILED by the closer (TS-11)
        await k.Ok(k.Patch($"/api/pos/shifts/{s1}/start-close", null, sup), "start close");
        (await w.Sync(w.Order(s1, [Line(w.A, 1m, 10m)], [Pay("CASH", 10m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "closing", "The shift is closing: no new sales can be made on it.");
        var closed = await k.Ok(w.CloseShift(s1, 195m), "TS-11: close within threshold");
        (closed.S("status"), closed.D("cashVariance"), closed.D("actualCashClosing")).Should().Be(("RECONCILED", -5m, 195m));
        closed.S("reconciledByName").Should().NotBeNullOrEmpty().And.Be(closed.S("closedByName"));
        (await k.Ok(k.Get($"/api/pos/shifts/{s1}"), "detail")).A("sessions").Should().OnlyContain(x => !x.B("isActive"), "closing ends the sessions");

        // TS-12/13: a second shift closed short → DISPUTED → reconciled with a note
        var s2 = await w.OpenShiftAsync(200m);
        (await k.Ok(k.Get($"/api/pos/terminals/{w.Terminal}/shifts/current"), "current")).S("shiftNumber").Should().EndWith("-02");
        await w.SellAsync(w.Order(s2, [Line(w.A, 5m, 10m)], [Pay("CASH", 50m)]), cashier);
        var disputed = await k.Ok(w.CloseShift(s2, 230m), "TS-12: close short");
        (disputed.S("status"), disputed.D("cashVariance"), disputed.D("expectedCashClosing")).Should().Be(("DISPUTED", -20m, 250m));
        (await k.Patch($"/api/pos/shifts/{s2}/reconcile", new { note = "x" }, sup)).Status.Should().Be(HttpStatusCode.Forbidden, "POS_SHIFT_RECONCILE");
        (await k.Patch($"/api/pos/shifts/{s2}/reconcile", new { note = " " })).ShouldFail(HttpStatusCode.BadRequest, "note required", "A reconciliation note is required.");
        var reconciled = await k.Ok(k.Patch($"/api/pos/shifts/{s2}/reconcile", new { note = "counted twice, 20 in the float bag" }), "TS-13: reconcile");
        reconciled.S("status").Should().Be("RECONCILED");
        reconciled.S("reconcileNotes").Should().Contain("counted twice, 20 in the float bag");

        // closed shift: online refused; a late offline order is booked to it, totals recomputed, status kept
        (await w.Sync(w.Order(s2, [Line(w.A, 1m, 10m)], [Pay("CASH", 10m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "closed shift online", "The shift is not open.");
        var late = await w.SellAsync(w.Order(s2, [Line(w.A, 1m, 10m)], [Pay("CASH", 10m)], offline: true, at: DateTimeOffset.UtcNow.AddMinutes(-30)), cashier);
        late.A("warnings").Select(x => x.GetString()).Should().Contain(x => x!.Contains("booked to it late"));
        var after = await w.ShiftAsync(s2);
        (after.S("status"), after.D("totalCashSales"), after.D("expectedCashClosing"), after.D("cashVariance")).Should().Be(("RECONCILED", 60m, 260m, -30m));
        after.S("reconcileNotes").Should().Contain("System: late offline order");

        // TS-14: a shift open longer than the store's 12 h → one notification to the store's supervisors (never auto-closed)
        var s3 = await w.OpenShiftAsync(50m);
        await _f.ExecuteAsync("UPDATE pos.shifts SET OpenedAt = DATEADD(HOUR, -13, SYSUTCDATETIME()) WHERE Uuid = @u", ("@u", s3));
        await using var hub = await HubProbe.ConnectAsync(_f, HubProbe.TokenOf(sup));
        (await hub.InvokeAsync("JoinTerminal", w.Terminal)).Has("error").Should().BeFalse();
        await _f.RunPosJobAsync("StaleShiftAlertJob");
        var alert = await hub.WaitForPushAsync("ShiftAlert");
        alert.Should().NotBeNull("the terminal gets a ShiftAlert");
        (alert!.Value.S("severity"), alert.Value.S("message")).Should().Match<(string?, string?)>(x => x.Item1 == "WARNING" && x.Item2!.Contains("13 hours"));
        var stale = await w.NotificationsAsync("POS_STALE_SHIFT", s3);
        stale.Select(n => n.User).Should().Equal(w.Supervisor.Id);
        stale.Single().Message.Should().Contain("has been open for 13 hours");
        await _f.RunPosJobAsync("StaleShiftAlertJob");
        (await w.NotificationsAsync("POS_STALE_SHIFT", s3)).Should().HaveCount(1, "reported once");
        (await w.ShiftAsync(s3)).S("status").Should().Be("OPEN", "nothing is closed automatically");
    }

    // ── TS-21..28, 44..48, 60 ────────────────────────────────────────────────────

    [Fact]
    public async Task Sales_post_to_real_inventory_and_finance()
    {
        var w = await _root.PosWorldAsync("ORD");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var shift = await w.OpenShiftAsync(0m);
        var cashier = await w.CashierClientAsync();
        var walkIn = (await k.Ok(k.Get("/api/customers/search"), "walk-in")).Items().First().G("uuid");
        await using var hub = await HubProbe.ConnectAsync(_f, HubProbe.TokenOf(sup));
        (await hub.InvokeAsync("JoinTerminal", w.Terminal)).Has("error").Should().BeFalse();

        // TS-21/22: cash sale with change (E1: 2 × 10.00 + 1 × 5.99 at 17 % = 30.41)
        var r1 = await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m), Line(w.B, 1m, 5.99m)], [Pay("CASH", 50m, 19.59m)]), cashier);
        // D-25: the real Inventory change hook → PosChangeListener → StockUpdated to the store group, with the new available
        var pushed = await hub.WaitForPushAsync("StockUpdated", 15, a => a.G("variantUuid") == w.A.VariantUuid);
        pushed.Should().NotBeNull("the sale's stock change reaches the store's terminals");
        pushed!.Value.D("available").Should().Be(98m);
        r1.S("orderNumber").Should().MatchRegex($"^POS-{w.StoreCode}-\\d{{8}}-0001$");
        r1.Flags().Should().BeEmpty();
        var o1 = await w.OrderAsync(r1.G("orderUuid"));
        (o1.D("subtotal"), o1.D("taxTotal"), o1.D("grandTotal"), o1.D("paidAmount"), o1.D("changeAmount"), o1.S("paymentStatus"))
            .Should().Be((25.99m, 4.42m, 30.41m, 50m, 19.59m, "PAID"));
        (o1.G("customerUuid"), o1.B("isWalkIn")).Should().Be((walkIn, true));
        await VerifyPostedSaleAsync(w, o1, walkIn);
        (await w.OnHandAsync(w.A), await w.OnHandAsync(w.B)).Should().Be((98m, 99m));

        // TS-23: split cash + card — one customer payment per tender
        var r2 = await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m), Line(w.B, 1m, 5.99m)], [Pay("CASH", 20m), Pay("CARD", 10.41m, reference: "AUTH-1")]), cashier);
        var o2 = await w.OrderAsync(r2.G("orderUuid"));
        await VerifyPostedSaleAsync(w, o2, walkIn);
        var pays = await w.CustomerPaymentsAsync(o2.A("payments").Select(p => p.G("uuid")));
        pays.Values.Select(p => (p.Amount, p.Method)).Should().BeEquivalentTo(new[] { (20m, "CASH"), (10.41m, "CARD") });

        // TS-24: discounts — the cashier has no POS_ORDER_DISCOUNT: refused online, flagged offline; the supervisor's E3 passes
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m, "PERCENT", 10m)], [Pay("CASH", 10.53m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "TS-25: discount without permission", "You do not have permission to give discounts.");
        var off = await w.SellAsync(w.Order(shift, [Line(w.A, 1m, 10m, "PERCENT", 10m)], [Pay("CASH", 10.53m)], offline: true), cashier);
        off.Flags().Should().BeEquivalentTo("OFFLINE", "DISCOUNT_UNAUTHORIZED");
        var e3 = await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m), Line(w.B, 1m, 5.99m)], [Pay("CASH", 30m, 2.63m)],
            orderDiscountType: "PERCENT", orderDiscountValue: 10m), sup);
        var o3 = await w.OrderAsync(e3.G("orderUuid"));
        (o3.D("subtotal"), o3.D("discountAmount"), o3.D("taxTotal"), o3.D("grandTotal")).Should().Be((25.99m, 2.60m, 3.98m, 27.37m), "E3");
        o3.A("lines").Select(l => (l.D("discountAmount"), l.D("netAmount"), l.D("taxAmount"), l.D("lineTotal")))
            .Should().Equal((2.00m, 18.00m, 3.06m, 21.06m), (0.60m, 5.39m, 0.92m, 6.31m));
        await VerifyPostedSaleAsync(w, o3, walkIn);
        var inv3 = await w.InvoiceRowAsync(o3.G("salesInvoiceUuid"));
        (Convert.ToDecimal(inv3["DiscountAmount"]), Convert.ToDecimal(inv3["TaxAmount"]), Convert.ToDecimal(inv3["Subtotal"]))
            .Should().Be((2.60m, 3.98m, 25.99m), "D-35: Subtotal = Grand − Tax + Discount");

        // TS-26: unknown / unusable variants
        var ghost = new Product(0, Guid.NewGuid(), Guid.NewGuid(), "ghost");
        (await w.Sync(w.Order(shift, [Line(ghost, 1m, 1m)], [Pay("CASH", 1.17m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "unknown variant", $"Product variant {ghost.VariantUuid} was not found.");
        (await w.Sync(w.Order(shift, [Line(ghost, 1m, 1m)], [Pay("CASH", 1.17m)], offline: true), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "unknown variant offline too", "was not found.");
        (await w.Sync(w.Order(shift, [Line(w.Hidden, 1m, 8m)], [Pay("CASH", 9.36m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "not available for POS", "is not available for sale on POS.");
        var hiddenOff = await w.SellAsync(w.Order(shift, [Line(w.Hidden, 1m, 8m)], [Pay("CASH", 9.36m)], offline: true), cashier);
        hiddenOff.A("warnings").Select(x => x.GetString()).Should().Contain(x => x!.Contains("is not available for sale on POS."));

        // TS-27: price changed at the till — the cashier's price stands; client totals that differ → server wins + warning
        var pc = await w.SellAsync(w.Order(shift, [Line(w.A, 1m, 9.50m)], [Pay("CASH", 11.12m)],
            clientTotals: new { subtotal = 9.5m, discountAmount = 0m, taxTotal = 0m, grandTotal = 9.5m }), cashier);
        pc.Flags().Should().Equal("PRICE_CHANGED");
        pc.A("warnings").Select(x => x.GetString()).Should().Contain(x => x!.Contains("Totals were recalculated by the server: grand total 11.12"));

        // tender rules (E8)
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CARD", 50m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "POS-PAY-03", "A CARD payment of 50.00 exceeds the remaining balance of 11.70.");
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 5m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "short", "Payments (5.00) do not match the order total (11.70).");
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CARD", 20m, 8.30m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "change on card", "Only cash payments can give change.");

        // TS-28/60: zero stock → sold anyway: negative on-hand, NEGATIVE_STOCK, notification, negative product ledger (POS only)
        var neg = await w.SellAsync(w.Order(shift, [Line(w.Zero, 1m, 7m)], [Pay("CASH", 8.19m)]), cashier);
        neg.Flags().Should().Equal("NEGATIVE_STOCK");
        var oNeg = await w.OrderAsync(neg.G("orderUuid"));
        await VerifyPostedSaleAsync(w, oNeg, walkIn);
        (await w.OnHandAsync(w.Zero)).Should().Be(-1m);
        (await k.Ok(k.Get($"/api/pos/sync/stock?terminalUuid={w.Terminal}", sup), "stock")).A("items")
            .Single(i => i.G("variantUuid") == w.Zero.VariantUuid).D("available").Should().Be(-1m);
        (await w.ProductLedgerAsync(w.Zero)).Should().ContainSingle(e => e.Type == "SALE").Which.Running.Should().Be(-1m, "D-36: POS may run the product ledger below zero");
        (await w.NotificationsAsync("POS_NEGATIVE_STOCK", oNeg.G("uuid"))).Select(n => n.User).Should().Equal(w.Supervisor.Id);
        // the daily negative-stock report lists it to the store's supervisors
        await _f.RunPosJobAsync("NegativeStockReportJob");
        var report = await w.NotificationsAsync("POS_NEGATIVE_STOCK_REPORT", w.Store);
        report.Select(n => n.User).Should().Equal(w.Supervisor.Id);
        report.Single().Message.Should().Contain("(-1)");

        // shift and session totals over all of it (cash net of change; the offline discount sale and hidden sale included)
        var s = await w.ShiftAsync(shift);
        var sales = new[] { o1, o2, o3, oNeg }.Select(o => o.D("grandTotal")).Sum() + 10.53m + 9.36m + 11.12m;
        (s.D("grossTotal"), s.D("totalCardSales"), s.D("totalCashSales"), s.I("orderCount")).Should().Be((sales, 10.41m, sales - 10.41m, 7));

        // POS settings isPosActive = false makes a variant unusable online (catalog shows it inactive)
        await k.Ok(k.Put($"/api/pos/products/{w.B.ProductUuid}/settings", new { isPosActive = false, isWeighable = false, isFavorite = false, sortOrder = 0 }), "B off POS");
        (await w.Sync(w.Order(shift, [Line(w.B, 1m, 5.99m)], [Pay("CASH", 7.01m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "isPosActive false", "is not available for sale on POS.");

        // the order list / by-local reads
        var list = await k.Ok(k.Get($"/api/pos/orders?shiftUuid={shift}&pageSize=50"), "orders list");
        list.A("data").Should().HaveCount(7);
        (await k.Ok(k.Get($"/api/pos/orders/by-local/{o1.S("localId")}", cashier), "by-local (own order)")).G("uuid").Should().Be(o1.G("uuid"));
        (await k.Get($"/api/pos/orders?shiftUuid={shift}", cashier)).Status.Should().Be(HttpStatusCode.Forbidden, "list needs POS_ORDER_VIEW");
    }

    /// <summary>A completed sale's postings: invoice = order, a payment per tender, customer ledger, product ledger, stock and master ledgers.</summary>
    private static async Task VerifyPostedSaleAsync(PosWorld w, JsonElement o, Guid customer)
    {
        var what = o.S("orderNumber");
        (o.B("stockPosted"), o.B("financePosted"), o.IsNull("postingError")).Should().Be((true, true, true), $"{what} posted — postingError: {o.S("postingError")}");

        // the sales invoice: number, amounts = the order's, source POS, paid in full unless on account
        var invoiceUuid = o.G("salesInvoiceUuid");
        var invoice = await w.K.Ok(w.K.Get($"/api/sales-invoices/{invoiceUuid}"), "read the POS invoice");
        (invoice.S("invoiceNumber"), invoice.D("grandTotal"), invoice.S("sourceType"), invoice.S("sourceNumber"))
            .Should().Be((o.S("salesInvoiceNumber"), o.D("grandTotal"), "POS", what));
        invoice.IsNull("saleOrderUuid").Should().BeTrue("a counter sale has no sale order");
        var row = await w.InvoiceRowAsync(invoiceUuid);
        Convert.ToDecimal(row["TaxAmount"]).Should().Be(o.D("taxTotal"));
        Convert.ToDecimal(row["BalanceDue"]).Should().Be(o.D("balanceDue"));
        ((Guid)row["SourceUuid"]!).Should().Be(o.G("uuid"));
        invoice.A("lines").Select(l => l.D("lineTotal")).Should().Equal(o.A("lines").Select(l => l.D("lineTotal")));

        // one customer payment per non-ON_ACCOUNT tender, net of change, linked back to the POS payment
        var tenders = o.A("payments").Where(p => !p.B("isRefund") && p.S("method") != "ON_ACCOUNT").ToList();
        var pays = await w.CustomerPaymentsAsync(tenders.Select(p => p.G("uuid")));
        pays.Should().HaveCount(tenders.Count, $"{what}: one payment per tender");
        foreach (var t in tenders)
        {
            pays[t.G("uuid")].Amount.Should().Be(t.D("amount") - t.D("changeGiven"));
            pays[t.G("uuid")].Uuid.Should().Be(t.G("customerPaymentUuid"));
        }

        // customer ledger: the invoice debit and the payment credits
        var ledger = await w.CustomerLedgerAsync(customer);
        ledger.Where(e => e.Reference == invoiceUuid).Sum(e => e.Debit).Should().Be(o.D("grandTotal"), $"{what}: INVOICE debit");
        ledger.Where(e => pays.Values.Any(p => p.Uuid == e.Reference)).Sum(e => e.Credit).Should().Be(pays.Values.Sum(p => p.Amount), $"{what}: PAYMENT credits");

        // stock: POS_SALE out per variant (inventory ledger) mirrored in the master product ledger (WAREHOUSE → CUSTOMER)
        var byVariant = o.A("lines").GroupBy(l => l.G("variantUuid")).ToDictionary(g => g.Key, g => g.Sum(l => l.D("quantity")));
        var stock = await w.StockLedgerAsync(o.G("uuid"));
        stock.Where(s => s.Type == "POS_SALE").GroupBy(s => s.Variant).ToDictionary(g => g.Key, g => g.Sum(s => s.Out))
            .Should().BeEquivalentTo(byVariant, $"{what}: inventory ledger");
        var master = await w.MasterLedgerAsync(o.G("uuid"));
        master.Where(m => m.Type == "POS_SALE").Sum(m => m.Out).Should().Be(byVariant.Values.Sum(), $"{what}: master product ledger");
        master.Where(m => m.Type == "POS_SALE").Should().OnlyContain(m => m.Source == "WAREHOUSE" && m.Destination == "CUSTOMER");

        // finance product ledger: a SALE (COGS) per line
        var productLedgerSales = (await w.F.QueryAsync(
            "SELECT VariantUuid AS V, Quantity AS Q FROM finance.product_ledger WHERE EntryType = 'SALE' AND ReferenceId = @r", ("@r", invoiceUuid)))
            .GroupBy(r => (Guid)r["V"]!).ToDictionary(g => g.Key, g => g.Sum(r => Math.Abs(Convert.ToDecimal(r["Q"]))));
        productLedgerSales.Should().BeEquivalentTo(byVariant, $"{what}: product ledger SALE");
    }

    // ── TS-49, 50, 51, 59 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Order_sync_is_idempotent_and_batches_replay_in_order()
    {
        var w = await _root.PosWorldAsync("IDM");
        var k = w.K;
        var shift = await w.OpenShiftAsync(0m);
        var cashier = await w.CashierClientAsync();

        // TS-50: the same localId twice, sequentially
        var order = w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)]);
        var first = await w.SellAsync(order, cashier);
        var again = await w.SellAsync(order, cashier);
        (again.G("orderUuid"), again.S("orderNumber"), again.A("warnings").Count).Should().Be((first.G("orderUuid"), first.S("orderNumber"), 0));
        (await w.InvoiceCountForSourceAsync(first.G("orderUuid"))).Should().Be(1);
        (await w.StockLedgerAsync(first.G("orderUuid"))).Should().ContainSingle();

        // … and concurrently (six pushes of one order at once)
        var raced = w.Order(shift, [Line(w.A, 2m, 10m)], [Pay("CASH", 23.40m)]);
        var answers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => w.Sync(raced, cashier)));
        answers.Should().OnlyContain(a => a.Status == HttpStatusCode.OK, string.Join(" | ", answers.Select(a => a.ToString())));
        var uuids = answers.Select(a => a.Result.G("orderUuid")).Distinct().ToList();
        uuids.Should().ContainSingle("POS-SYN-05: one order whoever wins");
        (await w.InvoiceCountForSourceAsync(uuids[0])).Should().Be(1, "one invoice");
        (await w.StockLedgerAsync(uuids[0])).Should().ContainSingle("one stock posting").Which.Out.Should().Be(2m);
        Convert.ToInt32((await _f.QueryAsync("SELECT COUNT(*) AS N FROM pos.payments p JOIN pos.orders o ON o.Id = p.OrderId WHERE o.Uuid = @u", ("@u", uuids[0])))[0]["N"])
            .Should().Be(1);
        (await w.OnHandAsync(w.A)).Should().Be(97m);

        // a payment localId already used by another order
        var reused = (await w.OrderAsync(first.G("orderUuid"))).A("payments")[0];
        var stolen = new { localId = Guid.NewGuid().ToString("N"), terminalUuid = w.Terminal, shiftUuid = shift, isOffline = false,
            lines = new[] { Line(w.A, 1m, 10m) }, orderDiscountType = "NONE", orderDiscountValue = 0m,
            payments = new[] { new { localId = (await _f.QueryAsync("SELECT LocalId FROM pos.payments WHERE Uuid = @u", ("@u", reused.G("uuid"))))[0]["LocalId"], method = "CASH", amount = 11.70m, changeGiven = 0m } } };
        (await w.Sync(stolen, cashier)).ShouldFail(HttpStatusCode.BadRequest, "payment id reuse", "A payment of this order was already synced with another order.");

        // TS-49/59: 50 offline orders pushed in one batch, out of order — processed by createdAtLocal, answered in request order
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        var batch = Enumerable.Range(0, 50)
            .Select(i => (At: start.AddMinutes(i), Body: w.Order(shift, [Line(w.D, 1m, 3.33m)], [Pay("CASH", 5m, 1.10m)], offline: true, at: start.AddMinutes(i))))
            .OrderBy(_ => Guid.NewGuid()).ToList();
        var results = (await k.Ok(k.Post("/api/pos/orders/sync/batch", new { orders = batch.Select(b => b.Body).ToArray() }, cashier), "batch")).Items();
        results.Should().HaveCount(50).And.OnlyContain(r => r.B("success"));
        results.Select(r => r.S("localId")).Should().Equal(batch.Select(b => JsonSerializer.SerializeToElement(b.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web)).S("localId")));
        var numbers = results.Zip(batch, (r, b) => (b.At, Number: r.S("orderNumber")!)).OrderBy(x => x.At).Select(x => x.Number).ToList();
        numbers.Should().BeInAscendingOrder(StringComparer.Ordinal, "numbers follow createdAtLocal").And.OnlyHaveUniqueItems();
        numbers.Should().OnlyContain(n => n.StartsWith($"POS-{w.StoreCode}-"));
        results.Should().OnlyContain(r => r.Flags().SequenceEqual(new[] { "OFFLINE" }));
        var posted = await _f.QueryAsync("SELECT COUNT(*) AS N FROM pos.orders WHERE OrderNumber IN (" + string.Join(",", numbers.Select(n => $"'{n}'")) +
                                         ") AND StockPosted = 1 AND FinancePosted = 1 AND OfflineNumber IS NOT NULL");
        Convert.ToInt32(posted[0]["N"]).Should().Be(50, "each offline order posted to stock and finance");
        (await w.OnHandAsync(w.D)).Should().Be(50m);

        // replaying the whole batch changes nothing
        var replay = (await k.Ok(k.Post("/api/pos/orders/sync/batch", new { orders = batch.Select(b => b.Body).ToArray() }, cashier), "replay")).Items();
        replay.Select(r => r.S("orderNumber")).Should().Equal(results.Select(r => r.S("orderNumber")));
        (await w.OnHandAsync(w.D)).Should().Be(50m, "no second stock posting");

        // one bad order does not stop the rest; > 100 refused
        var mixed = (await k.Ok(k.Post("/api/pos/orders/sync/batch", new
        {
            orders = new[]
            {
                w.Order(shift, [Line(new Product(0, Guid.NewGuid(), Guid.NewGuid(), "x"), 1m, 1m)], [Pay("CASH", 1.17m)], offline: true),
                w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)], offline: true)
            }
        }, cashier), "mixed batch")).Items();
        (mixed[0].B("success"), mixed[1].B("success")).Should().Be((false, true));
        mixed[0].S("error").Should().Contain("was not found.");
        (await k.Post("/api/pos/orders/sync/batch", new { orders = Enumerable.Range(0, 101).Select(_ => w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)])).ToArray() }, cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "batch cap", "At most 100 orders per batch.");

        // an offline order from an unknown session attaches to a closed session of its own (D-46), idempotent on sessionLocalId
        var sessionLocal = Guid.NewGuid().ToString("N");
        var withSession = new { localId = Guid.NewGuid().ToString("N"), terminalUuid = w.Terminal, shiftUuid = shift, sessionLocalId = sessionLocal,
            cashierUserId = w.Cashier.Id, isOffline = true, createdAtLocal = DateTimeOffset.UtcNow.AddMinutes(-5),
            lines = new[] { Line(w.A, 1m, 10m) }, orderDiscountType = "NONE", orderDiscountValue = 0m, payments = new[] { Pay("CASH", 11.70m) } };
        var ws = await w.SellAsync(withSession, w.Supervisor.Client, "offline order pushed by the device (supervisor) for the cashier");
        var wso = await w.OrderAsync(ws.G("orderUuid"));
        (wso.I("cashierUserId"), wso.I("syncedByUserId")).Should().Be((w.Cashier.Id, w.Supervisor.Id), "cashierUserId honoured, pusher kept");
        wso.IsNull("sessionUuid").Should().BeFalse();
    }

    // ── TS-31..36 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Credit_sales_follow_the_limit_online_and_flag_offline()
    {
        var w = await _root.PosWorldAsync("CRD", tolerance: 10m);
        var k = w.K;
        var shift = await w.OpenShiftAsync(0m);
        var cashier = await w.CashierClientAsync();
        var c = (await k.Ok(k.Post("/api/customers", Mods.Customer("Credit Shop", creditLimit: 100m)), "credit customer")).GetGuid();
        var noCredit = (await k.Ok(k.Post("/api/customers", Mods.Customer("Cash Only", creditLimit: 500m)), "customer without POS credit")).GetGuid();

        // settings: allow POS credit (walk-in may not)
        var walkIn = (await k.Ok(k.Get("/api/customers/search"), "walk-in")).Items().First().G("uuid");
        (await k.Put($"/api/pos/customers/{walkIn}/settings", new { allowPosCredit = true }))
            .ShouldFail(HttpStatusCode.BadRequest, "walk-in", "Walk-In customers cannot use credit.");
        await k.Ok(k.Put($"/api/pos/customers/{c}/settings", new { allowPosCredit = true, requiresIdForCredit = false }), "allow credit");
        var credit = await k.Ok(k.Get($"/api/pos/customers/{c}/credit", cashier), "credit");
        (credit.D("balance"), credit.D("creditLimit"), credit.D("available"), credit.B("allowPosCredit"), credit.B("isWalkIn"))
            .Should().Be((0m, 100m, 100m, true, false));

        // TS-31: within the limit online → CREDIT_SALE, an open invoice, the balance (pushed to the organization's terminals)
        await using var hub = await HubProbe.ConnectAsync(_f, HubProbe.TokenOf(w.Supervisor.Client));
        var sale = await w.SellAsync(w.Order(shift, [Line(w.A, 5m, 10m)], [Pay("ON_ACCOUNT", 58.50m)], customer: c), cashier);
        var balancePush = await hub.WaitForPushAsync("CustomerBalanceChanged", 15, x => x.G("customerUuid") == c);
        balancePush.Should().NotBeNull("D-44: POS pushes CustomerBalanceChanged after an ON_ACCOUNT sale");
        (balancePush!.Value.D("balance"), balancePush.Value.D("creditLimit")).Should().Be((58.50m, 100m));
        var o = await w.OrderAsync(sale.G("orderUuid"));
        (o.S("paymentStatus"), o.D("balanceDue"), o.D("paidAmount")).Should().Be(("CREDIT_SALE", 58.50m, 0m));
        await VerifyPostedSaleAsync(w, o, c);
        Convert.ToString((await w.InvoiceRowAsync(o.G("salesInvoiceUuid")))["Status"]).Should().Be("ISSUED");
        (await k.Ok(k.Get($"/api/customers/{c}/balance"), "balance")).D("balance").Should().Be(58.50m);

        // TS-32: over the limit online
        (await w.Sync(w.Order(shift, [Line(w.A, 5m, 10m)], [Pay("ON_ACCOUNT", 58.50m)], customer: c), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "TS-32", "Credit limit exceeded. Limit: 100.00. Balance: 58.50. This order: 58.50.");
        // TS-33: walk-in, a customer without POS credit
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("ON_ACCOUNT", 11.70m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "TS-33", "Walk-In customers cannot use credit.");
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("ON_ACCOUNT", 11.70m)], customer: noCredit), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "allowPosCredit", "This customer is not allowed to buy on credit at the POS.");

        // TS-34: offline within the tolerance (limit 100 × 1.10 = 110; 58.50 + 46.80 = 105.30) → accepted, OFFLINE_CREDIT only
        var within = await w.SellAsync(w.Order(shift, [Line(w.A, 4m, 10m)], [Pay("ON_ACCOUNT", 46.80m)], customer: c, offline: true, offlineCredit: true), cashier);
        within.Flags().Should().BeEquivalentTo("OFFLINE", "OFFLINE_CREDIT");
        // TS-35: offline beyond the tolerance (152.10 > 110) → CREDIT_OVER_LIMIT + a notification to the store's supervisors
        var over = await w.SellAsync(w.Order(shift, [Line(w.A, 4m, 10m)], [Pay("ON_ACCOUNT", 46.80m)], customer: c, offline: true, offlineCredit: true), cashier);
        over.Flags().Should().BeEquivalentTo("OFFLINE", "OFFLINE_CREDIT", "CREDIT_OVER_LIMIT");
        over.A("warnings").Select(x => x.GetString()).Should().Contain("Credit limit exceeded. Limit: 110.00. Balance: 105.30. This order: 46.80.");
        (await w.NotificationsAsync("POS_CREDIT_OVER_LIMIT", over.G("orderUuid"))).Select(n => n.User).Should().Equal(w.Supervisor.Id);

        // TS-36: balance through the customer API and the POS credit read; partial credit
        (await k.Ok(k.Get($"/api/customers/{c}/balance"), "balance")).D("balance").Should().Be(152.10m);
        var after = await k.Ok(k.Get($"/api/pos/customers/{c}/credit", cashier), "credit after");
        (after.D("balance"), after.D("available")).Should().Be((152.10m, 0m));
        await k.Ok(k.Put($"/api/customers/{c}", Mods.Customer("Credit Shop", creditLimit: 1000m)), "raise the limit");
        var partial = await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m)], [Pay("CASH", 20m), Pay("ON_ACCOUNT", 3.40m)], customer: c), cashier);
        var po = await w.OrderAsync(partial.G("orderUuid"));
        (po.S("paymentStatus"), po.D("balanceDue"), po.D("paidAmount")).Should().Be(("PARTIAL_CREDIT", 3.40m, 20m));
        await VerifyPostedSaleAsync(w, po, c);
        (await k.Ok(k.Get($"/api/customers/{c}/balance"), "balance")).D("balance").Should().Be(155.50m);
        var shiftRow = await w.ShiftAsync(shift);
        (shiftRow.D("totalCreditSales"), shiftRow.D("totalCashSales")).Should().Be((58.50m + 46.80m + 46.80m + 3.40m, 20m));

        // the customer's sync row carries the balance and the POS credit settings
        var customers = await k.Ok(k.Get($"/api/pos/sync/customers?terminalUuid={w.Terminal}", w.Supervisor.Client), "customer sync");
        var row = customers.A("customers").Single(x => x.G("uuid") == c);
        (row.D("balance"), row.B("allowPosCredit"), row.D("creditLimit")).Should().Be((155.50m, true, 1000m));
        customers.A("customers").Single(x => x.G("uuid") == walkIn).B("allowPosCredit").Should().BeFalse();
    }

    // ── TS-29, 30, 37..43 ────────────────────────────────────────────────────────

    [Fact]
    public async Task Voids_and_returns_reverse_stock_money_and_shift_totals()
    {
        var w = await _root.PosWorldAsync("RET");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var shift = await w.OpenShiftAsync(100m);
        var cashier = await w.CashierClientAsync();

        // TS-29: void in the same shift — stock back (POS_VOID), payments REVERSED, a full credit note
        var v = await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m)], [Pay("CASH", 23.40m)]), cashier);
        var vo = await w.OrderAsync(v.G("orderUuid"));
        (await k.Patch($"/api/pos/orders/{vo.G("uuid")}/void", new { reason = "wrong item" }, cashier)).Status.Should().Be(HttpStatusCode.Forbidden, "POS_ORDER_VOID");
        (await k.Patch($"/api/pos/orders/{vo.G("uuid")}/void", new { reason = "" }, sup)).ShouldFail(HttpStatusCode.BadRequest, "reason", "A reason is required to void an order.");
        (vo.B("financePosted"), vo.S("postingError")).Should().Be((true, null), "the sale is in Finance before it is voided");
        var voided = await k.Ok(k.Patch($"/api/pos/orders/{vo.G("uuid")}/void", new { reason = "wrong item" }, sup), "void");
        (voided.S("status"), voided.S("voidReason"), voided.B("stockPosted"), voided.B("financePosted")).Should().Be(("VOIDED", "wrong item", true, true));
        voided.S("creditNoteNumber").Should().StartWith("SCN-");
        (await w.StockLedgerAsync(vo.G("uuid"))).Where(s => s.Type == "POS_VOID").Sum(s => s.In).Should().Be(2m);
        (await w.MasterLedgerAsync(vo.G("uuid"))).Where(m => m.Type == "POS_VOID").Should().OnlyContain(m => m.Source == "CUSTOMER" && m.Destination == "WAREHOUSE");
        (await w.OnHandAsync(w.A)).Should().Be(100m);
        (await w.CustomerPaymentsAsync(vo.A("payments").Select(p => p.G("uuid")))).Values.Should().OnlyContain(p => p.Status == "REVERSED");
        var original = await w.InvoiceRowAsync(vo.G("salesInvoiceUuid"));
        (Convert.ToDecimal(original["ReturnedAmount"]), (string)original["Status"]!).Should().Be((23.40m, "PAID"), "D-37: a voided sale reads PAID with ReturnedAmount = GrandTotal");
        var cn = await w.InvoiceRowAsync(voided.G("creditNoteUuid"));
        ((string)cn["Status"]!, Convert.ToDecimal(cn["GrandTotal"]), (Guid)cn["OriginalInvoiceUuid"]!).Should().Be(("CREDIT_NOTE", 23.40m, vo.G("salesInvoiceUuid")));
        (await k.Patch($"/api/pos/orders/{vo.G("uuid")}/void", new { reason = "again" }, sup)).ShouldFail(HttpStatusCode.BadRequest, "twice", "The order is already voided.");
        var s = await w.ShiftAsync(shift);
        (s.I("orderCount"), s.D("totalCashSales"), s.D("expectedCashClosing")).Should().Be((0, 0m, 100m), "voided sales are not in the totals");

        // TS-37..40: partial returns with the remainder rule (E2/E9 line: 3 × 3.33 − 10 % = 10.52; 3.51 + 3.51 + 3.50)
        var r = await w.SellAsync(w.Order(shift, [Line(w.D, 3m, 3.33m, "PERCENT", 10m)], [Pay("CASH", 10.52m)]), sup);
        var ro = await w.OrderAsync(r.G("orderUuid"));
        var line = ro.A("lines").Single();
        (line.D("lineTotal"), line.D("taxAmount")).Should().Be((10.52m, 1.53m));
        object Ret(decimal qty, params object[] refunds) => new
        {
            localId = Guid.NewGuid().ToString("N"), reason = "damaged", lines = new[] { new { originalLineUuid = line.G("uuid"), quantity = qty } }, refunds
        };
        object Refund(string method, decimal amount) => new { method, amount };

        var ret1 = await k.Ok(k.Post($"/api/pos/orders/{ro.G("uuid")}/return", Ret(1m, Refund("CASH", 3.51m)), sup), "return 1");
        (ret1.S("orderType"), ret1.D("grandTotal"), ret1.D("taxTotal"), ret1.S("paymentStatus")).Should().Be(("RETURN", 3.51m, 0.51m, "REFUNDED"));
        ret1.S("orderNumber").Should().MatchRegex($"^RET-{w.StoreCode}-\\d{{8}}-0001$");
        (ret1.B("stockPosted"), ret1.B("financePosted")).Should().Be((true, true));
        (await w.OrderAsync(ro.G("uuid"))).S("status").Should().Be("PARTIALLY_RETURNED");
        await k.Ok(k.Post($"/api/pos/orders/{ro.G("uuid")}/return", Ret(1m, Refund("CASH", 3.51m)), sup), "return 1 more");
        (await k.Post($"/api/pos/orders/{ro.G("uuid")}/return", Ret(2m, Refund("CASH", 7.01m)), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "TS-39: over-return (A39 §0 text)", $"Cannot return 2 of '{line.S("productName")}'. Only 1 returnable.");
        (await k.Post($"/api/pos/orders/{ro.G("uuid")}/return", Ret(1m, Refund("CASH", 3.51m)), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "remainder rule", "Refunds (3.51) must equal the return total (3.50).");
        (await k.Post($"/api/pos/orders/{ro.G("uuid")}/return", Ret(1m, Refund("ON_ACCOUNT", 3.50m)), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "cash sale", "On-account refunds are only possible for credit sales.");
        var last = await k.Ok(k.Post($"/api/pos/orders/{ro.G("uuid")}/return", Ret(1m, Refund("CASH", 3.50m)), sup), "the last unit");
        (last.D("grandTotal"), last.D("taxTotal")).Should().Be((3.50m, 0.51m));
        var full = await w.OrderAsync(ro.G("uuid"));
        (full.S("status"), full.A("returns").Count, full.A("lines").Single().D("returnableQuantity")).Should().Be(("RETURNED", 3, 0m));
        (await k.Patch($"/api/pos/orders/{ro.G("uuid")}/void", new { reason = "x" }, sup)).ShouldFail(HttpStatusCode.BadRequest, "returned", "A returned order cannot be voided.");
        (await k.Post($"/api/pos/orders/{ro.G("uuid")}/return", Ret(1m, Refund("CASH", 3.50m)), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "fully returned (A39 §0 text)", "All items in this order have already been returned.");

        // stock POS_RETURN per return, credit notes against the original invoice, ReturnedAmount = the whole line
        foreach (var rr in full.A("returns"))
        {
            (await w.StockLedgerAsync(rr.G("uuid"))).Should().ContainSingle(x => x.Type == "POS_RETURN" && x.In == 1m);
            var ret = await w.OrderAsync(rr.G("uuid"));
            var cnr = await w.InvoiceRowAsync(ret.G("creditNoteUuid"));
            ((string)cnr["Status"]!, (Guid)cnr["OriginalInvoiceUuid"]!, Convert.ToDecimal(cnr["GrandTotal"])).Should().Be(("CREDIT_NOTE", ro.G("salesInvoiceUuid"), ret.D("grandTotal")));
            ret.A("payments").Should().OnlyContain(p => p.B("isRefund") && !p.IsNull("customerPaymentUuid"), "refund linked to Finance's refund");
        }
        Convert.ToDecimal((await w.InvoiceRowAsync(ro.G("salesInvoiceUuid")))["ReturnedAmount"]).Should().Be(10.52m);
        (await w.OnHandAsync(w.D)).Should().Be(100m);

        // TS-41: cash refunds reduce the drawer's expected cash
        s = await w.ShiftAsync(shift);
        (s.D("totalCashSales"), s.D("totalCashRefunds"), s.I("refundCount"), s.D("expectedCashClosing"), s.D("netTotal"))
            .Should().Be((10.52m, 10.52m, 3, 100m, 0m));

        // TS-42: a credit sale's return reduces the customer's balance (ON_ACCOUNT refund)
        var c = (await k.Ok(k.Post("/api/customers", Mods.Customer("Account Co", creditLimit: 1000m)), "credit customer")).GetGuid();
        await k.Ok(k.Put($"/api/pos/customers/{c}/settings", new { allowPosCredit = true }), "allow credit");
        var cs = await w.SellAsync(w.Order(shift, [Line(w.A, 5m, 10m)], [Pay("ON_ACCOUNT", 58.50m)], customer: c), cashier);
        var cso = await w.OrderAsync(cs.G("orderUuid"));
        var cline = cso.A("lines").Single().G("uuid");
        var cret = await k.Ok(k.Post($"/api/pos/orders/{cso.G("uuid")}/return", new
        {
            localId = Guid.NewGuid().ToString("N"), reason = "one too many", lines = new[] { new { originalLineUuid = cline, quantity = 1m } },
            refunds = new[] { new { method = "ON_ACCOUNT", amount = 11.70m } }
        }, sup), "credit return");
        cret.D("grandTotal").Should().Be(11.70m);
        (await k.Ok(k.Get($"/api/customers/{c}/balance"), "balance")).D("balance").Should().Be(46.80m, "TS-42");
        (await w.CustomerLedgerAsync(c)).Should().Contain(e => e.Type == "CREDIT_NOTE" && e.Credit == 11.70m);
        s = await w.ShiftAsync(shift);
        (s.D("totalOtherRefunds"), s.D("expectedCashClosing")).Should().Be((11.70m, 100m), "an ON_ACCOUNT refund moves no cash");

        // TS-43: the void of another shift's sale is refused; a return from the next shift is fine
        var y = await w.SellAsync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)]), cashier);
        await k.Ok(w.CloseShift(shift, 111.70m), "close shift 1");
        var shift2 = await w.OpenShiftAsync(50m);
        (await k.Patch($"/api/pos/orders/{y.G("orderUuid")}/void", new { reason = "late" }, sup))
            .ShouldFail(HttpStatusCode.BadRequest, "TS-43", "Orders can only be voided within the same shift.");
        var yret = await k.Ok(k.Post($"/api/pos/orders/{y.G("orderUuid")}/return", new
        {
            localId = Guid.NewGuid().ToString("N"), reason = "changed mind", lines = new[] { new { originalLineUuid = (await w.OrderAsync(y.G("orderUuid"))).A("lines").Single().G("uuid"), quantity = 1m } },
            refunds = new[] { new { method = "CASH", amount = 11.70m } }
        }, sup), "return in the next shift");
        yret.G("shiftUuid").Should().Be(shift2);
        (await w.ShiftAsync(shift2)).D("expectedCashClosing").Should().Be(50m - 11.70m);

        // reports: sales by product and product profitability net the returns (D-39) — D fully returned, A: 5 + 1 sold, 1 + 1 returned
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var byProduct = await k.Ok(k.Get($"/api/reports/sales/sales-by-product?dateFrom={today}&dateTo={today}&pageSize=100"), "sales by product");
        var dRow = byProduct.A("items").SingleOrDefault(i => i.G("productUuid") == w.D.ProductUuid);
        if (dRow.ValueKind == JsonValueKind.Object) (dRow.D("quantitySold"), dRow.D("revenue")).Should().Be((0m, 0m), $"D sold 3 and took 3 back — {J.Short(dRow)}");
        var aRow = byProduct.A("items").Single(i => i.G("productUuid") == w.A.ProductUuid);
        (aRow.D("quantitySold"), aRow.D("revenue")).Should().Be((4m, 40m), $"A: 5 + 1 sold (+2 voided), 2 returned — {J.Short(aRow)}");
        var profit = await k.Ok(k.Get($"/api/reports/product-profitability?dateFrom={today}&dateTo={today}&pageSize=100"), "product profitability");
        var pA = profit.A("data").Single(i => i.G("productUuid") == w.A.ProductUuid);
        (pA.D("quantitySold"), pA.D("revenue"), pA.D("costOfGoodsSold")).Should().Be((4m, 40m, 16m), $"A at WAC 4.00 — {J.Short(pA)}");
        var pD = profit.A("data").SingleOrDefault(i => i.G("productUuid") == w.D.ProductUuid);
        if (pD.ValueKind == JsonValueKind.Object) (pD.D("quantitySold"), pD.D("revenue"), pD.D("costOfGoodsSold")).Should().Be((0m, 0m, 0m), J.Short(pD));
    }

    // ── E4 retry ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failed_finance_posting_is_finished_by_the_retry_job()
    {
        var w = await _root.PosWorldAsync("RTY");
        var shift = await w.OpenShiftAsync(0m);
        var cashier = await w.CashierClientAsync();

        // Finance down for this organization (a test-only trigger refusing its POS invoices); the order is saved anyway
        await _f.ExecuteAsync(
            "CREATE TRIGGER finance.tr_a38_qa_finance_down ON finance.sales_invoices AFTER INSERT AS " +
            $"IF EXISTS (SELECT 1 FROM inserted WHERE OrganizationId = '{w.O.OrgId}') THROW 50000, 'A38 QA: finance is down', 1;");
        JsonElement r, r2;
        JsonElement voided;
        try
        {
            r = await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m)], [Pay("CASH", 30m, 6.60m)]), cashier);
            r2 = await w.SellAsync(w.Order(shift, [Line(w.B, 1m, 5.99m)], [Pay("CARD", 7.01m)]), cashier);
            // D-44: a void of a sale never posted to Finance — stock goes back now, Finance gets sale + reversal later
            voided = await w.K.Ok(w.K.Patch($"/api/pos/orders/{r2.G("orderUuid")}/void", new { reason = "card declined later" }, w.Supervisor.Client), "void unposted sale");
        }
        finally
        {
            await _f.ExecuteAsync("DROP TRIGGER IF EXISTS finance.tr_a38_qa_finance_down");
        }
        var o = await w.OrderAsync(r.G("orderUuid"));
        (o.B("stockPosted"), o.B("financePosted"), o.IsNull("salesInvoiceUuid")).Should().Be((true, false, true));
        o.S("postingError").Should().StartWith("Finance:");
        (await w.InvoiceCountForSourceAsync(o.G("uuid"))).Should().Be(0);
        (voided.S("status"), voided.B("stockPosted"), voided.B("financePosted")).Should().Be(("VOIDED", true, false));
        (await w.StockLedgerAsync(r2.G("orderUuid"))).Select(x => (x.Type, x.In + x.Out)).Should().BeEquivalentTo(new[] { ("POS_SALE", 1m), ("POS_VOID", 1m) });
        (await w.ShiftAsync(shift)).D("totalCashSales").Should().Be(23.40m, "the shift counts the sale regardless of Finance");

        // a return of the unposted sale waits for it (D-44)
        var line = o.A("lines").Single().G("uuid");
        var ret = await w.K.Ok(w.K.Post($"/api/pos/orders/{o.G("uuid")}/return", new
        {
            localId = Guid.NewGuid().ToString("N"), reason = "faulty", lines = new[] { new { originalLineUuid = line, quantity = 1m } },
            refunds = new[] { new { method = "CASH", amount = 11.70m } }
        }, w.Supervisor.Client), "return of an unposted sale");
        (ret.B("stockPosted"), ret.B("financePosted")).Should().Be((true, false));
        ret.S("postingError").Should().Contain("is not in Finance yet");

        // the job respects the backoff (15 min after the first attempt) …
        await _f.RunPosJobAsync("PosPostingRetryJob");
        (await w.OrderAsync(o.G("uuid"))).B("financePosted").Should().BeFalse("not due yet");

        // … and posts all three once due, in id order (the sale before its return), inside the organization's tenant scope
        await _f.ExecuteAsync("UPDATE pos.orders SET LastPostingAttemptAt = DATEADD(HOUR, -1, SYSUTCDATETIME()) WHERE OrganizationId = @o", ("@o", w.O.OrgId));
        (await _f.RunPosJobAsync("PosPostingRetryJob")).Should().BeGreaterThanOrEqualTo(3);
        var done = await w.OrderAsync(o.G("uuid"));
        var walkIn = done.G("customerUuid");
        await VerifyPostedSaleAsync(w, done, walkIn);
        (await w.InvoiceCountForSourceAsync(o.G("uuid"))).Should().Be(1);
        (await w.StockLedgerAsync(o.G("uuid"))).Should().ContainSingle("stock was not posted twice");
        var retDone = await w.OrderAsync(ret.G("uuid"));
        (retDone.B("financePosted"), retDone.IsNull("creditNoteUuid")).Should().Be((true, false));
        Convert.ToDecimal((await w.InvoiceRowAsync(done.G("salesInvoiceUuid")))["ReturnedAmount"]).Should().Be(11.70m);
        var v = await w.OrderAsync(r2.G("orderUuid"));
        (v.B("financePosted"), v.IsNull("salesInvoiceUuid"), v.IsNull("creditNoteUuid")).Should().Be((true, false, false), "sale + full credit note, net zero");
        var vInvoice = await w.InvoiceRowAsync(v.G("salesInvoiceUuid"));
        (Convert.ToDecimal(vInvoice["ReturnedAmount"]), (string)vInvoice["Status"]!).Should().Be((7.01m, "PAID"));
        (await w.CustomerPaymentsAsync(v.A("payments").Select(p => p.G("uuid")))).Values.Should().OnlyContain(p => p.Status == "REVERSED");
    }

    // ── TS-52..55, 58, permissions & roles ───────────────────────────────────────

    [Fact]
    public async Task Module_licence_grace_permissions_and_pwa_sync()
    {
        var w = await _root.PosWorldAsync("MOD");
        var k = w.K;
        var sup = w.Supervisor.Client;

        // permissions and the three built-in roles (§9)
        var codes = (await _f.QueryAsync("SELECT Code FROM auth.Permissions WHERE Code LIKE 'POS[_]%'")).Select(x => (string)x["Code"]!).ToList();
        codes.Should().HaveCount(27, "17 from A38 + 10 from A39 (D-9)");
        async Task<List<string>> RoleCodes(string role) => (await _f.QueryAsync(
                "SELECT DISTINCT p.Code FROM auth.RolePermissions rp JOIN auth.Permissions p ON p.PermissionID = rp.PermissionID " +
                "JOIN auth.Roles r ON r.RoleID = rp.RoleID WHERE r.RoleCode = @r AND rp.IsAllowed = 1 AND p.Code LIKE 'POS[_]%'", ("@r", role)))
            .Select(x => (string)x["Code"]!).ToList();
        (await RoleCodes("POS_CASHIER")).Should().BeEquivalentTo("POS_ORDER_CREATE", "POS_CREDIT_NOTE_REDEEM");
        (await RoleCodes("POS_SUPERVISOR")).Should().BeEquivalentTo("POS_ORDER_CREATE", "POS_ORDER_VIEW", "POS_ORDER_DISCOUNT", "POS_ORDER_VOID",
            "POS_ORDER_RETURN", "POS_SHIFT_OPEN", "POS_SHIFT_CLOSE", "POS_SHIFT_VIEW", "POS_CASH_MOVEMENT", "POS_TERMINAL_VIEW", "POS_STORE_VIEW",
            // A39 D-9
            "POS_CREDIT_NOTE_REDEEM", "POS_RETURN_CROSS_STORE", "POS_RETURN_REPORTS", "POS_CREDIT_NOTE_ISSUE", "POS_CREDIT_NOTE_VIEW");
        (await RoleCodes("STORE_MANAGER")).Should().Contain(new[] { "POS_SHIFT_RECONCILE", "POS_TERMINAL_MANAGE", "POS_PRODUCT_SETTINGS",
            "POS_CUSTOMER_SETTINGS", "POS_REPORTS_VIEW", "POS_ORDER_VOID" }).And.NotContain("POS_STORE_MANAGE");

        // TS-52: config / catalog (strictly > since, paging, POS settings included) / stock / customers / users
        var cfg = await k.Ok(k.Get($"/api/pos/sync/config?terminalUuid={w.Terminal}", sup), "config");
        (cfg.P("taxCode").S("code"), cfg.P("taxCode").D("percent"), cfg.P("currency").S("code")).Should().Be(("GST17", 17m, "PKR"));
        cfg.Strings("paymentMethods").Should().Contain("ON_ACCOUNT");
        (cfg.P("features").B("returns"), cfg.P("features").B("creditSales")).Should().Be((true, true));
        var full = await k.Ok(k.Get($"/api/pos/sync/catalog?terminalUuid={w.Terminal}", sup), "full catalog");
        full.B("hasMore").Should().BeFalse();
        var items = full.A("items");
        items.Single(i => i.G("variantUuid") == w.A.VariantUuid).D("price").Should().Be(10m);
        items.Single(i => i.G("variantUuid") == w.A.VariantUuid).B("isActive").Should().BeTrue();
        items.Single(i => i.G("variantUuid") == w.Hidden.VariantUuid).B("isActive").Should().BeFalse("not available for POS");
        var since = full.S("nextSince")!;
        var delta = await k.Ok(k.Get($"/api/pos/sync/catalog?terminalUuid={w.Terminal}&since={Uri.EscapeDataString(since)}", sup), "delta with nextSince");
        delta.A("items").Should().BeEmpty($"strictly after since ({since}) — {J.Short(delta)}");
        await k.Ok(k.Put($"/api/pos/products/{w.A.ProductUuid}/settings", new { posName = "Apple", color = "#ff0000", isWeighable = false, isFavorite = true, sortOrder = 1, isPosActive = true }), "POS settings");
        var changed = await k.Ok(k.Get($"/api/pos/sync/catalog?terminalUuid={w.Terminal}&since={Uri.EscapeDataString(since)}", sup), "delta after a settings change");
        var a = changed.A("items").Single(i => i.G("variantUuid") == w.A.VariantUuid);
        (a.S("posName"), a.S("color"), a.B("isFavorite")).Should().Be(("Apple", "#FF0000", true));
        changed.A("items").Should().HaveCount(1);
        var paged = new List<Guid>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var p = await k.Ok(k.Get($"/api/pos/sync/catalog?terminalUuid={w.Terminal}&limit=2" + (cursor is null ? "" : $"&since={Uri.EscapeDataString(cursor)}"), sup), "page");
            paged.AddRange(p.A("items").Select(i => i.G("variantUuid")));
            cursor = p.S("nextSince");
            if (!p.B("hasMore")) break;
        }
        paged.Distinct().Should().BeEquivalentTo(items.Select(i => i.G("variantUuid")), "paging with limit 2 reaches every item");
        var stock = await k.Ok(k.Get($"/api/pos/sync/stock?terminalUuid={w.Terminal}", sup), "stock");
        stock.A("items").Single(i => i.G("variantUuid") == w.B.VariantUuid).D("available").Should().Be(100m);
        var cust = await k.Ok(k.Get($"/api/pos/sync/customers?terminalUuid={w.Terminal}", sup), "customers");
        cust.A("customers").Should().Contain(c => c.B("isSystem") && c.S("code") == "C-WALKIN");

        // TS-53: users (PIN hashes) only for the bound device
        (await k.Get($"/api/pos/sync/users?terminalUuid={w.Terminal}", sup)).ShouldFail(HttpStatusCode.Forbidden, "not bound", "This device is not bound to the terminal.");
        var device = Guid.NewGuid();
        await k.Ok(k.Post($"/api/pos/terminals/{w.Terminal}/bind", new { deviceUuid = device }, sup), "bind");
        (await k.Get($"/api/pos/sync/users?terminalUuid={w.Terminal}", sup)).ShouldFail(HttpStatusCode.Forbidden, "no header", "This device is not bound to the terminal.");
        using (var withHeader = _f.CreateBearerClient(HubProbe.TokenOf(sup)))
        {
            withHeader.DefaultRequestHeaders.Add("X-Pos-Device", Guid.NewGuid().ToString());
            (await k.Get($"/api/pos/sync/users?terminalUuid={w.Terminal}", withHeader)).Status.Should().Be(HttpStatusCode.Forbidden, "another device");
            withHeader.DefaultRequestHeaders.Remove("X-Pos-Device");
            withHeader.DefaultRequestHeaders.Add("X-Pos-Device", device.ToString());
            var users = (await k.Ok(k.Get($"/api/pos/sync/users?terminalUuid={w.Terminal}", withHeader), "users")).Items();
            users.Select(u => u.I("userId")).Should().BeEquivalentTo(new[] { w.Cashier.Id, w.Supervisor.Id });
            using var scope = _f.Services.CreateScope();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserAccount>>();
            var cashierHash = users.Single(u => u.I("userId") == w.Cashier.Id).S("pinHash")!;
            hasher.VerifyHashedPassword(null!, cashierHash, CashierPin).Should().NotBe(PasswordVerificationResult.Failed, "the PWA verifies offline with the same Identity v3 hash");
            hasher.VerifyHashedPassword(null!, cashierHash, SupervisorPin).Should().Be(PasswordVerificationResult.Failed);
            Convert.FromBase64String(cashierHash)[0].Should().Be(0x01, "Identity v3 format marker");
        }

        // toggleable sub-features: credit sales off ⇒ no ON_ACCOUNT offered / accepted; returns off ⇒ the return endpoint refused
        await k.FeatureOkAsync(Module, "FEATURE_POS_CREDIT_SALES", false);
        var noCredit = await k.Ok(k.Get($"/api/pos/sync/config?terminalUuid={w.Terminal}", sup), "config without credit");
        (noCredit.Strings("paymentMethods").Contains("ON_ACCOUNT"), noCredit.P("features").B("creditSales")).Should().Be((false, false));
        await k.FeatureOkAsync(Module, "FEATURE_POS_CREDIT_SALES", true);
        await k.FeatureOkAsync(Module, "FEATURE_POS_RETURNS", false);
        var refused = await k.Post($"/api/pos/orders/{Guid.NewGuid()}/return", new { localId = "x", reason = "x" }, sup);
        refused.Status.Should().Be(HttpStatusCode.Forbidden, refused.ToString());
        refused.Body.S("errorCode").Should().Be("MODULE_NOT_LICENSED");
        await k.FeatureOkAsync(Module, "FEATURE_POS_RETURNS", true);

        // TS-54/55: the impact counts open shifts; switching POS off with one open → grace: reads, the open shift's own
        // records and its sales (pushed late or made offline — [AllowDuringGrace]) still land; no new shift or terminal
        // ([BlockDuringGrace]), so selling ends once the open shifts close
        var shift = await w.OpenShiftAsync(0m);
        var t9 = (await k.Ok(k.Post($"/api/pos/stores/{w.Store}/terminals", new { code = "T9", name = "Till 9" }), "terminal 9")).G("uuid");
        var impact = await k.Ok(k.Get($"/api/tenant/modules/{Module}/impact"), "impact");
        impact.A("inProgress").Single(l => l.S("label") == "POS shifts – Open").I("count").Should().Be(1);
        var grace = await k.Ok(k.Disable(Module), "POS off with the default grace");
        grace.S("status").Should().Be("GRACE");
        await k.Ok(w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)]), sup), "TS-55: a sale of the open shift still lands in grace");
        (await k.Get($"/api/pos/shifts/{shift}")).ShouldPassTheGate("reads in grace");
        (await w.OpenShift(0m, terminal: t9)).ShouldBeModuleRefusal(Module, inGrace: true, "no new shift in grace");
        (await k.Post($"/api/pos/stores/{w.Store}/terminals", new { code = "T10", name = "Till 10" })).ShouldBeModuleRefusal(Module, inGrace: true, "no new terminal in grace");
        await k.Ok(w.CloseShift(shift, 11.70m), "the open shift can still be closed in grace");
        await k.EnableOkAsync(Module);
        (await k.Ok(k.Get($"/api/pos/stores/{w.Store}"), "back on")).G("uuid").Should().Be(w.Store);

        // TS-58: unlicensed by the super admin → writes 403 MODULE_NOT_LICENSED (reads pass: the org once had it), the hub refuses
        await _root.LicenceAsync(w.O.OrgId, (Module, false));
        (await k.Post($"/api/pos/stores/{w.Store}/terminals", new { code = "T3", name = "x" })).ShouldBeModuleRefusal(Module, inGrace: false, "TS-58 unlicensed");
        await using (var hub = await HubProbe.ConnectAsync(_f, HubProbe.TokenOf(sup)))
            hub.Closed.Should().BeTrue("the hub checks MODULE_POS on connect");
        await _root.LicenceAsync(w.O.OrgId, (Module, true));
        await k.Ok(k.Post($"/api/pos/stores/{w.Store}/terminals", new { code = "T3", name = "Till 3" }), "licensed again");

        // a BASIC organization never had it: no POS card to switch on, every POS call refused (D-2)
        var basic = await _root.OrgAsync("BAS", plan: "BASIC", currency: false);
        var card = await basic.K.CardAsync(Module);
        (card.S("status"), card.B("isLicensed")).Should().Be(("NOT_LICENSED", false));
        (await basic.K.Enable(Module)).Status.Should().Be(HttpStatusCode.BadRequest, "not in the Basic plan");
        (await basic.K.Get("/api/pos/stores")).ShouldBeModuleRefusal(Module, inGrace: false, "TS-58: a Basic org");
        // the module depends on Inventory, Customers and Finance
        card.A("dependsOn").Select(d => d.S("code")).Should().BeEquivalentTo("MODULE_INVENTORY", "MODULE_CUSTOMERS", "MODULE_FINANCE");
    }
}
