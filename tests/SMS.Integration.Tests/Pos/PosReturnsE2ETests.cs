using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;
using static SMS.Integration.Tests.Pos.PosKit;

namespace SMS.Integration.Tests.Pos;

/// <summary>
/// A39 (QA) — POS returns, exchanges and store credit end to end on the real host (LocalDB) with the REAL Inventory, Finance,
/// Auth and Tenancy modules wired together (the POS unit tests used fakes). Each test runs in an ENTERPRISE organization of its
/// own (A38 <see cref="PosKit"/> world: MODULE_POS + its sub-features from the plan template). Scenarios → tests:
/// <list type="bullet">
/// <item>Reasons / policies (lazy seed, CRUD, store override + fallback + delete, rowVersion 409, ReturnPolicyChanged) — <see cref="Reasons_and_policies_seed_crud_override_and_rowversion"/>.</item>
/// <item>Same-store returns, blocks, window, manager tokens, fee, refund methods — <see cref="Same_store_returns_statuses_and_blocks"/>, <see cref="Manager_authorization_window_amount_fee_and_refund_methods"/>.</item>
/// <item>Store credit (issue, redeem, split, expiry job, offline, bound customer, cancel, void restore, double spend) — <see cref="Store_credit_issue_redeem_expire_cancel_and_double_spend"/>.</item>
/// <item>Cross-store (lookup, policy, manager, stock into the processing store, original store's window, weekly report) — <see cref="Cross_store_returns_and_the_weekly_report"/>.</item>
/// <item>Exchanges PAY / EVEN / REFUND + fee, cross-store exchange, Finance + ledgers — <see cref="Exchanges_pay_even_refund_and_cross_store"/>.</item>
/// <item>Offline <c>returns/sync</c> (idempotent, flags, grace) — <see cref="Offline_return_sync_flags_and_grace"/>.</item>
/// <item>Permissions per role and the two sub-features — <see cref="Permissions_and_sub_features"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~PosReturnsE2ETests</c>.</para>
/// </summary>
public sealed class PosReturnsE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string ManagerPin = "2468";
    private readonly SapKit _root;
    private readonly SapWebApplicationFactory _f;

    public PosReturnsE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _root = new SapKit(factory, "A39");
    }

    // ── Reasons & policies ───────────────────────────────────────────────────────

    [Fact]
    public async Task Reasons_and_policies_seed_crud_override_and_rowversion()
    {
        var w = await _root.PosWorldAsync("RSN");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var cashier = await w.CashierClientAsync();

        // lazy seed: the 9 reasons in sortOrder, once
        var reasons = (await k.Ok(k.Get("/api/pos/return-reasons"), "reasons")).Items();
        reasons.Select(r => r.S("code")).Should().Equal("DEFECTIVE", "WRONG_ITEM", "WRONG_SIZE", "NOT_AS_EXPECTED", "CHANGED_MIND", "DUPLICATE",
            "PRICE_MATCH", "WARRANTY", "OTHER");
        reasons.Select(r => r.I("sortOrder")).Should().Equal(10, 20, 30, 40, 50, 60, 70, 80, 90);
        var pm = reasons.Single(r => r.S("code") == "PRICE_MATCH");
        (pm.B("requiresNote"), pm.B("requiresManager"), pm.S("appliesTo"), pm.ND("restockingFeeOverride")).Should().Be((true, true, "RETURN", 0m));
        reasons.Single(r => r.S("code") == "CHANGED_MIND").IsNull("restockingFeeOverride").Should().BeTrue();
        (await k.Ok(k.Get("/api/pos/return-reasons", sup), "reasons again (supervisor: POS_ORDER_RETURN)")).Items().Should().HaveCount(9);
        Convert.ToInt32((await _f.QueryAsync("SELECT COUNT(*) AS N FROM pos.return_reasons WHERE OrganizationId = @o", ("@o", w.O.OrgId)))[0]["N"])
            .Should().Be(9, "seeded once");
        (await k.Ok(k.Get("/api/pos/return-reasons?appliesTo=EXCHANGE"), "exchange reasons")).Items().Select(r => r.S("code"))
            .Should().NotContain(new[] { "DUPLICATE", "PRICE_MATCH" }).And.HaveCount(7);

        // CRUD
        object Reason(string code, string name, string appliesTo = "BOTH", decimal? fee = null, bool active = true) =>
            new { code, name, appliesTo, restockingFeeOverride = fee, sortOrder = 95, requiresNote = false, requiresManager = false, isActive = active };
        var created = await k.Ok(k.Post("/api/pos/return-reasons", Reason("store_dmg", "Store damage", "exchange", 2.5m)), "create reason");
        (created.S("code"), created.S("appliesTo"), created.D("restockingFeeOverride")).Should().Be(("STORE_DMG", "EXCHANGE", 2.5m));
        (await k.Post("/api/pos/return-reasons", Reason("STORE_DMG", "Again")))
            .ShouldFail(HttpStatusCode.BadRequest, "unique code", "Reason code STORE_DMG already exists.");
        (await k.Post("/api/pos/return-reasons", Reason("bad code!", "Bad")))
            .ShouldFail(HttpStatusCode.BadRequest, "code pattern", "Reason code must be 1 to 20 characters");
        (await k.Post("/api/pos/return-reasons", Reason("X", "Fee", fee: 101m)))
            .ShouldFail(HttpStatusCode.BadRequest, "fee 0–100", "between 0 and 100");
        var updated = await k.Ok(k.Put($"/api/pos/return-reasons/{created.G("uuid")}", Reason("STORE_DMG", "Damaged in store", "EXCHANGE", 2.5m)), "update");
        updated.S("name").Should().Be("Damaged in store");
        await k.Ok(k.Patch($"/api/pos/return-reasons/{created.G("uuid")}/status", new { isActive = false }), "deactivate");
        (await k.Ok(k.Get("/api/pos/return-reasons"), "active only")).Items().Select(r => r.S("code")).Should().NotContain("STORE_DMG");
        (await k.Ok(k.Get("/api/pos/return-reasons?includeInactive=true"), "all")).Items().Single(r => r.S("code") == "STORE_DMG").B("isActive").Should().BeFalse();

        // PWA sync: inactive included, nextSince strictly after
        var sync = await k.Ok(k.Get("/api/pos/sync/return-reasons", cashier), "sync reasons (cashier: POS_ORDER_CREATE)");
        sync.A("reasons").Should().HaveCount(10).And.Contain(r => r.S("code") == "STORE_DMG" && !r.B("isActive"));
        var since = sync.S("nextSince")!;
        (await k.Ok(k.Get($"/api/pos/sync/return-reasons?since={Uri.EscapeDataString(since)}", cashier), "delta")).A("reasons")
            .Should().BeEmpty($"nothing after {since}");

        // policies: the org default applies
        var eff = await k.Ok(k.Get($"/api/pos/stores/{w.Store}/return-policy", sup), "effective (supervisor: POS_STORE_VIEW)");
        (eff.S("source"), eff.S("name"), eff.ND("returnWindowDays"), eff.IsNull("exchangeWindowDays"), eff.B("allowCrossStoreReturn"),
                eff.D("restockingFeePct"), eff.ND("creditNoteExpiryDays"), eff.IsNull("maxReturnNoAuth"))
            .Should().Be(("ORG_DEFAULT", "Organization default", 30m, true, true, 0m, 365m, true));

        // store override + ReturnPolicyChanged to the store
        await using var hub = await HubProbe.ConnectAsync(_f, HubProbe.TokenOf(sup));
        (await hub.InvokeAsync("JoinTerminal", w.Terminal)).Has("error").Should().BeFalse();
        (await k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy(window: 14), sup)).Status.Should().Be(HttpStatusCode.Forbidden, "POLICY_MANAGE is admin-only");
        var own = await k.Ok(k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy("Store rules", window: 14, fee: 5m)), "store override");
        (own.G("storeUuid"), own.S("name"), own.ND("returnWindowDays"), own.D("restockingFeePct")).Should().Be((w.Store, "Store rules", 14m, 5m));
        (await hub.WaitForPushAsync("ReturnPolicyChanged", 10, x => x.G("storeUuid") == w.Store)).Should().NotBeNull("D-26: pushed to the store");
        (await k.Ok(k.Get($"/api/pos/stores/{w.Store}/return-policy", sup), "effective")).S("source").Should().Be("STORE");
        var all = (await k.Ok(k.Get("/api/pos/return-policies"), "policies")).Items();
        all.Select(p => p.NG("storeUuid")).Should().Equal(null, w.Store);
        (await k.Get("/api/pos/return-policies", sup)).Status.Should().Be(HttpStatusCode.Forbidden);

        // rowVersion: the current one saves, a stale one is 409
        var rv1 = own.S("rowVersion")!;
        var saved = await k.Ok(k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy("Store rules", window: 15, rowVersion: rv1)), "save with rowVersion");
        saved.S("rowVersion").Should().NotBe(rv1);
        (await k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy("Store rules", window: 16, rowVersion: rv1)))
            .ShouldFail(HttpStatusCode.Conflict, "stale rowVersion", "changed by someone else");
        (await k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy(fee: 120m))).ShouldFail(HttpStatusCode.BadRequest, "fee", "between 0 and 100");
        (await k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy(window: -1))).ShouldFail(HttpStatusCode.BadRequest, "days", "Days cannot be negative.");

        // delete → back to the default (idempotent)
        await k.Ok(k.Delete($"/api/pos/stores/{w.Store}/return-policy"), "delete override");
        await k.Ok(k.Delete($"/api/pos/stores/{w.Store}/return-policy"), "delete again (idempotent)");
        (await k.Ok(k.Get($"/api/pos/stores/{w.Store}/return-policy", sup), "effective")).S("source").Should().Be("ORG_DEFAULT");
        (await k.Ok(k.Get($"/api/pos/sync/return-policy?terminalUuid={w.Terminal}", cashier), "PWA policy")).S("source").Should().Be("ORG_DEFAULT");

        // the default switched off and no override ⇒ no policy: reads and returns refused
        var shift = await w.OpenShiftAsync(0m);
        var sale = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)]), cashier)).G("orderUuid"));
        var def = all.Single(p => p.IsNull("storeUuid"));
        var off = await k.Ok(k.Put("/api/pos/return-policies/default", Policy(active: false, rowVersion: def.S("rowVersion"))), "default off");
        (await k.Get($"/api/pos/stores/{w.Store}/return-policy", sup))
            .ShouldFail(HttpStatusCode.BadRequest, "no policy", "No return policy configured. Contact your administrator.");
        var codes = await ReasonsAsync(w);
        (await k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(LineOf(sale, w.A), 1m)], "CASH"), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "no policy", "No return policy configured. Contact your administrator.");
        await k.Ok(k.Put("/api/pos/return-policies/default", Policy(rowVersion: off.S("rowVersion"))), "default on");
        await k.Ok(k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(LineOf(sale, w.A), 1m)], "CASH"), sup), "return with the default");
    }

    // ── Same-store returns ───────────────────────────────────────────────────────

    [Fact]
    public async Task Same_store_returns_statuses_and_blocks()
    {
        var w = await _root.PosWorldAsync("SSR");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var shift = await w.OpenShiftAsync(100m);
        var cashier = await w.CashierClientAsync();
        var codes = await ReasonsAsync(w);

        var sale = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 3m, 10m), Line(w.B, 1m, 5.99m)], [Pay("CASH", 42.11m)]), cashier)).G("orderUuid"));
        Guid lineA = LineOf(sale, w.A), lineB = LineOf(sale, w.B);

        // lookup by receipt (case-insensitive), from the same store
        var lookup = await k.Ok(k.Get($"/api/pos/orders/lookup?receipt={sale.S("orderNumber")!.ToLowerInvariant()}&storeUuid={w.Store}", sup), "lookup");
        lookup.B("found").Should().BeTrue();
        var lo = lookup.P("order");
        (lo.G("uuid"), lo.B("isSameStore"), lo.I("daysSincePurchase"), lo.S("storeCode")).Should().Be((sale.G("uuid"), true, 0, w.StoreCode));
        var la = lo.A("lines").Single(l => l.G("lineUuid") == lineA);
        (la.D("returnableQuantity"), la.D("refundIfAllReturned")).Should().Be((3m, 35.10m));
        lo.A("payments").Select(p => (p.S("method"), p.D("amount"))).Should().Equal(("CASH", 42.11m));
        var lp = lookup.P("policy");
        (lp.S("source"), lp.B("withinReturnWindow"), lp.B("withinExchangeWindow"), lp.ND("windowDaysRemaining")).Should().Be(("ORG_DEFAULT", true, true, 30m));
        (await k.Ok(k.Get("/api/pos/orders/lookup?receipt=POS-NOPE-1", sup), "unknown receipt")).B("found").Should().BeFalse();
        (await k.Ok(k.Get($"/api/pos/orders/{sale.G("uuid")}/returnable-lines", sup), "returnable lines")).P("order").A("lines").Should().HaveCount(2);

        // partial return (A38 path enriched): reason, CASH refund, stock + Finance
        var r1 = await k.Ok(k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(lineA, 1m)], "CASH"), sup), "return 1 A");
        (r1.S("orderType"), r1.D("grandTotal"), r1.D("restockingFeeAmount"), r1.S("returnReasonName"), r1.S("returnReason"), r1.G("returnReasonUuid"))
            .Should().Be(("RETURN", 11.70m, 0m, "Changed mind", "Changed mind", codes["CHANGED_MIND"]));
        (r1.S("paymentStatus"), r1.B("isCrossStore"), r1.B("stockPosted"), r1.B("financePosted")).Should().Be(("REFUNDED", false, true, true));
        r1.A("payments").Select(p => (p.S("method"), p.D("amount"), p.B("isRefund"))).Should().Equal(("CASH", 11.70m, true));
        (await w.StockLedgerAsync(r1.G("uuid"))).Should().Equal((w.A.VariantUuid, "POS_RETURN", 1m, 0m));
        var cn1 = await w.InvoiceRowAsync(r1.G("creditNoteUuid"));
        ((string)cn1["Status"]!, Convert.ToDecimal(cn1["GrandTotal"]), (Guid)cn1["OriginalInvoiceUuid"]!).Should().Be(("CREDIT_NOTE", 11.70m, sale.G("salesInvoiceUuid")));
        var after1 = await w.OrderAsync(sale.G("uuid"));
        after1.S("status").Should().Be("PARTIALLY_RETURNED");
        after1.A("lines").Select(l => (l.D("returnedQuantity"), l.D("returnableQuantity"))).Should().Equal((1m, 2m), (0m, 1m));

        // second partial return of the same line; then the rest of A is gone
        var r2 = await k.Ok(k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(lineA, 2m)], "CASH"), sup), "return 2 A");
        r2.D("grandTotal").Should().Be(23.40m);
        (await w.OrderAsync(sale.G("uuid"))).S("status").Should().Be("PARTIALLY_RETURNED");
        (await k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(lineA, 1m)], "CASH"), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "qty over returnable", $"Cannot return 1 of '{la.S("productName")}'. Only 0 returnable.");
        var r3 = await k.Ok(k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["WRONG_ITEM"], [(lineB, 1m)], "CASH"), sup), "return B");
        r3.D("grandTotal").Should().Be(7.01m);
        var full = await w.OrderAsync(sale.G("uuid"));
        (full.S("status"), full.A("returns").Count).Should().Be(("RETURNED", 3));
        (await k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(lineB, 1m)], "CASH"), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "fully returned", "All items in this order have already been returned.");
        Convert.ToDecimal((await w.InvoiceRowAsync(sale.G("salesInvoiceUuid")))["ReturnedAmount"]).Should().Be(42.11m);
        (await w.OnHandAsync(w.A), await w.OnHandAsync(w.B)).Should().Be((100m, 100m));

        // not a sale / voided
        (await k.Post($"/api/pos/orders/{r1.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(r1.A("lines")[0].G("uuid"), 1m)], "CASH"), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "a RETURN", "Only sales can be returned.");
        var v = await w.SellAsync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)]), cashier);
        var voided = await k.Ok(k.Patch($"/api/pos/orders/{v.G("orderUuid")}/void", new { reason = "mistake" }, sup), "void");
        (await k.Post($"/api/pos/orders/{v.G("orderUuid")}/return", Ret(codes["DEFECTIVE"], [(voided.A("lines")[0].G("uuid"), 1m)], "CASH"), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "voided", "Cannot return a voided order.");

        // reason rules: note, appliesTo, missing method
        var s2 = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m)], [Pay("CASH", 23.40m)]), cashier)).G("orderUuid"));
        var l2 = LineOf(s2, w.A);
        (await k.Post($"/api/pos/orders/{s2.G("uuid")}/return", Ret(codes["NOT_AS_EXPECTED"], [(l2, 1m)], "CASH"), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "requires note", "A note is required for this reason.");
        (await k.Post($"/api/pos/orders/{s2.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(l2, 1m)], null), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "refund method", "Choose a refund method.");
        var exOnly = await k.Ok(k.Post("/api/pos/return-reasons", new { code = "EXCH_ONLY", name = "Swap only", appliesTo = "EXCHANGE", sortOrder = 99, isActive = true }), "exchange-only reason");
        (await k.Post($"/api/pos/orders/{s2.G("uuid")}/return", Ret(exOnly.G("uuid"), [(l2, 1m)], "CASH"), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "appliesTo", "The reason 'Swap only' cannot be used for a return.");
        var noted = await k.Ok(k.Post($"/api/pos/orders/{s2.G("uuid")}/return", Ret(codes["NOT_AS_EXPECTED"], [(l2, 1m)], "CASH", note: "too small"), sup), "noted");
        noted.S("returnReason").Should().Be("Not as expected: too small");
        // the A38 body (text reason + refunds[]) still works
        var a38 = await k.Ok(k.Post($"/api/pos/orders/{s2.G("uuid")}/return", new
        {
            localId = Guid.NewGuid().ToString("N"), reason = "old till", lines = new[] { new { originalLineUuid = l2, quantity = 1m } },
            refunds = new[] { new { method = "CASH", amount = 11.70m } }
        }, sup), "A38 body");
        (a38.S("returnReason"), a38.IsNull("returnReasonUuid")).Should().Be(("old till", true));

        // shift: cash sales 42.11 + 23.40 (void excluded), cash refunds of everything returned, expected cash back to the float
        var sh = await w.ShiftAsync(shift);
        (sh.D("totalCashSales"), sh.D("totalCashRefunds"), sh.I("refundCount"), sh.D("expectedCashClosing"), sh.D("totalStoreCreditIssued"))
            .Should().Be((65.51m, 65.51m, 5, 100m, 0m));
    }

    [Fact]
    public async Task Manager_authorization_window_amount_fee_and_refund_methods()
    {
        var w = await _root.PosWorldAsync("MGR");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var shift = await w.OpenShiftAsync(0m);
        var cashier = await w.CashierClientAsync();
        var codes = await ReasonsAsync(w);
        var mgr = await k.PosUserAsync("STORE_MANAGER", "mgr");
        await k.Ok(k.Staff(w.Store, mgr.Id, "SUPERVISOR", ManagerPin), "store manager as SUPERVISOR staff");

        // reason requiring a manager
        var s = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 3m, 10m)], [Pay("CARD", 35.10m, reference: "AUTH")]), cashier)).G("orderUuid"));
        var l = LineOf(s, w.A);
        ShouldNeedManager(await k.Post($"/api/pos/orders/{s.G("uuid")}/return", Ret(codes["WARRANTY"], [(l, 1m)], "CARD", note: "broken"), sup),
            "Manager authorization required.", "REASON");

        // authorize: PIN rules, a supervisor without POS_RETURN_OVERRIDE cannot
        (await Authorize(w, s.G("uuid"), 11.70m, "12", sup, reasons: "REASON")).ShouldFail(HttpStatusCode.BadRequest, "format", "PIN must be 4 to 6 digits.");
        (await Authorize(w, s.G("uuid"), 11.70m, "1357", sup, reasons: "REASON")).ShouldFail(HttpStatusCode.BadRequest, "wrong PIN", "Invalid PIN.");
        (await Authorize(w, s.G("uuid"), 11.70m, SupervisorPin, sup, reasons: "REASON"))
            .ShouldFail(HttpStatusCode.BadRequest, "POS_SUPERVISOR has no override", "This manager cannot authorize returns.");
        (await w.K.Post("/api/pos/returns/authorize", new { terminalUuid = w.Terminal, originalOrderUuid = s.G("uuid"), amount = 11.70m, pin = CashierPin, managerUserId = w.Cashier.Id }, sup))
            .ShouldFail(HttpStatusCode.BadRequest, "cashier named", "This manager is not a supervisor of this store.");
        var auth = await k.Ok(Authorize(w, s.G("uuid"), 11.70m, ManagerPin, sup, reasons: "REASON"), "authorize");
        (auth.I("managerUserId"), auth.S("authorizationToken")).Should().Match<(int, string?)>(x => x.Item1 == mgr.Id && x.Item2!.Length >= 40);
        auth.P("expiresAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddMinutes(5), TimeSpan.FromMinutes(1));
        var token = auth.S("authorizationToken")!;
        var ok = await k.Ok(k.Post($"/api/pos/orders/{s.G("uuid")}/return", Ret(codes["WARRANTY"], [(l, 1m)], "CARD", note: "broken", token: token), sup), "authorized return");
        (ok.I("authorizedByUserId"), ok.IsNull("authorizedAt"), ok.S("authorizedByName")).Should().Match<(int, bool, string?)>(x => x.Item1 == mgr.Id && !x.Item2 && !string.IsNullOrEmpty(x.Item3));
        // single use
        ShouldNeedManager(await k.Post($"/api/pos/orders/{s.G("uuid")}/return", Ret(codes["WARRANTY"], [(l, 1m)], "CARD", note: "again", token: token), sup),
            "The manager authorization is invalid or has expired.", "REASON");
        // amount ceiling
        var small = (await k.Ok(Authorize(w, s.G("uuid"), 5m, ManagerPin, sup, reasons: "REASON"), "authorize 5.00")).S("authorizationToken");
        ShouldNeedManager(await k.Post($"/api/pos/orders/{s.G("uuid")}/return", Ret(codes["WARRANTY"], [(l, 1m)], "CARD", note: "x", token: small), sup),
            "The refund exceeds the amount the manager authorized.", "REASON");
        // expired
        var stale = (await k.Ok(Authorize(w, s.G("uuid"), 11.70m, ManagerPin, sup, reasons: "REASON"), "authorize")).S("authorizationToken")!;
        await _f.ExecuteAsync("UPDATE pos.return_authorizations SET ExpiresAt = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE TokenHash = @h", ("@h", Hash(stale)));
        ShouldNeedManager(await k.Post($"/api/pos/orders/{s.G("uuid")}/return", Ret(codes["WARRANTY"], [(l, 1m)], "CARD", note: "x", token: stale), sup),
            "The manager authorization is invalid or has expired.", "REASON");
        // another order's token
        var other = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)]), cashier)).G("orderUuid"));
        var foreign = (await k.Ok(Authorize(w, other.G("uuid"), 11.70m, ManagerPin, sup, reasons: "REASON"), "authorize other")).S("authorizationToken");
        ShouldNeedManager(await k.Post($"/api/pos/orders/{s.G("uuid")}/return", Ret(codes["WARRANTY"], [(l, 1m)], "CARD", note: "x", token: foreign), sup),
            "The manager authorization is invalid or has expired.", "REASON");
        // two returns racing on one token: one wins
        var race = (await k.Ok(Authorize(w, s.G("uuid"), 11.70m, ManagerPin, sup, reasons: "REASON"), "authorize")).S("authorizationToken");
        var raced = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            k.Post($"/api/pos/orders/{s.G("uuid")}/return", Ret(codes["WARRANTY"], [(l, 1m)], "CARD", note: "race", token: race), sup)));
        raced.Count(a => a.Status == HttpStatusCode.OK).Should().Be(1, string.Join(" | ", raced.Select(a => a.ToString())));
        raced.Should().Contain(a => a.Status == HttpStatusCode.Conflict || a.Status == HttpStatusCode.BadRequest);

        // window expired (30 days of the original store) → WINDOW, then authorized
        var old = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m)], [Pay("CASH", 23.40m)]), cashier)).G("orderUuid"));
        await AgeAsync(old.G("uuid"), 40);
        var oldLookup = await k.Ok(k.Get($"/api/pos/orders/{old.G("uuid")}/returnable-lines", sup), "aged lookup");
        (oldLookup.P("order").I("daysSincePurchase"), oldLookup.P("policy").B("withinReturnWindow"), oldLookup.P("policy").ND("windowDaysRemaining"))
            .Should().Be((40, false, 0m));
        ShouldNeedManager(await k.Post($"/api/pos/orders/{old.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(LineOf(old, w.A), 1m)], "CASH"), sup),
            "Return window expired (30 days).", "WINDOW");
        var wTok = (await k.Ok(Authorize(w, old.G("uuid"), 11.70m, ManagerPin, sup, reasons: "WINDOW"), "authorize window")).S("authorizationToken");
        (await k.Ok(k.Post($"/api/pos/orders/{old.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(LineOf(old, w.A), 1m)], "CASH", token: wTok), sup), "late return"))
            .I("authorizedByUserId").Should().Be(mgr.Id);

        // store policy: 5 % fee, 15.00 without authorization
        await k.Ok(k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy(fee: 5m, maxNoAuth: 15m)), "fee + amount");
        var f = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 3m, 10m)], [Pay("CASH", 35.10m)]), cashier)).G("orderUuid"));
        var fl = LineOf(f, w.A);
        ShouldNeedManager(await k.Post($"/api/pos/orders/{f.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(fl, 2m)], "CASH"), sup),
            "Manager authorization required.", "AMOUNT");
        var fee = await k.Ok(k.Post($"/api/pos/orders/{f.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(fl, 1m)], "CASH"), sup), "5 % fee");
        (fee.D("restockingFeeAmount"), fee.D("grandTotal")).Should().Be((0.59m, 11.11m), "round(11.70 × 5 %) = 0.59 (away from zero)");
        fee.A("payments").Single().D("amount").Should().Be(11.11m);
        fee.A("lines").Single().D("lineTotal").Should().Be(11.70m, "D-20: lines stay unscaled");
        var feeCn = await w.InvoiceRowAsync(fee.G("creditNoteUuid"));
        Convert.ToDecimal(feeCn["GrandTotal"]).Should().Be(11.11m, "D-5: the credit note credits the return less the fee");
        (await w.StockLedgerAsync(fee.G("uuid"))).Should().Equal((w.A.VariantUuid, "POS_RETURN", 1m, 0m));
        var waived = await k.Ok(k.Post($"/api/pos/orders/{f.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(fl, 1m)], "CASH"), sup), "reason override 0 %");
        (waived.D("restockingFeeAmount"), waived.D("grandTotal")).Should().Be((0m, 11.70m));

        // refund methods: original method forced, cash not allowed
        await k.Ok(k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy(toOriginal: true, cash: false)), "refund rules");
        var card = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m)], [Pay("CARD", 23.40m)]), cashier)).G("orderUuid"));
        var cl = LineOf(card, w.A);
        foreach (var m in new[] { "CASH", "STORE_CREDIT", "MOBILE" })
            (await k.Post($"/api/pos/orders/{card.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(cl, 1m)], m), sup))
                .ShouldFail(HttpStatusCode.BadRequest, $"{m} against the policy", "Refund method not allowed by the store's return policy.");
        (await k.Ok(k.Post($"/api/pos/orders/{card.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(cl, 1m)], "CARD"), sup), "original method"))
            .A("payments").Single().S("method").Should().Be("CARD");
        (await k.Ok(k.Get($"/api/pos/orders/{card.G("uuid")}/returnable-lines", sup), "lookup")).P("policy").B("refundToOriginalMethod").Should().BeTrue();
        await k.Ok(k.Delete($"/api/pos/stores/{w.Store}/return-policy"), "back to default");
        (await k.Post($"/api/pos/orders/{card.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(cl, 1m)], "ON_ACCOUNT"), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "cash sale", "On-account refunds are only possible for credit sales.");
        (await k.Post($"/api/pos/orders/{card.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(cl, 1m)], "EXCHANGE"), sup))
            .Status.Should().Be(HttpStatusCode.BadRequest, "EXCHANGE is never a client refund method (D-22)");

        // a cash refund reduces the drawer
        var sh = await w.ShiftAsync(shift);
        sh.D("expectedCashClosing").Should().Be(sh.D("totalCashSales") - sh.D("totalCashRefunds"));
        sh.D("totalCashRefunds").Should().Be(11.70m + 11.11m + 11.70m);
    }

    // ── Store credit ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Store_credit_issue_redeem_expire_cancel_and_double_spend()
    {
        var w = await _root.PosWorldAsync("SCN");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var shift = await w.OpenShiftAsync(0m);
        var cashier = await w.CashierClientAsync();
        var codes = await ReasonsAsync(w);
        var mgr = await k.PosUserAsync("STORE_MANAGER", "mgr");

        // a walk-in return refunded as store credit → a bearer note, Finance refund STORE_CREDIT with the CN number
        var s1 = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m)], [Pay("CASH", 23.40m)]), cashier)).G("orderUuid"));
        var ret = await k.Ok(k.Post($"/api/pos/orders/{s1.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(LineOf(s1, w.A), 1m)], "STORE_CREDIT"), sup), "return as store credit");
        var sc = ret.P("storeCredit");
        sc.S("number").Should().MatchRegex($"^CN-{w.StoreCode}-\\d{{8}}-0001$");
        (sc.D("amount"), sc.D("balance")).Should().Be((11.70m, 11.70m));
        sc.P("expiresAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(365), TimeSpan.FromDays(1));
        var refund = ret.A("payments").Single();
        (refund.S("method"), refund.S("reference"), refund.G("creditNoteUuid"), refund.D("amount")).Should().Be(("STORE_CREDIT", sc.S("number"), sc.G("uuid"), 11.70m));
        var fr = (await _f.QueryAsync("SELECT Method, Reference, Amount, CreditNoteUuid FROM finance.customer_refunds WHERE SourceUuid = @s", ("@s", refund.G("uuid")))).Single();
        ((string)fr["Method"]!, (string?)fr["Reference"], Convert.ToDecimal(fr["Amount"]), (Guid)fr["CreditNoteUuid"]!)
            .Should().Be(("STORE_CREDIT", sc.S("number"), 11.70m, ret.G("creditNoteUuid")));
        var note = await k.Ok(k.Get($"/api/pos/credit-notes/{sc.G("uuid")}", sup), "note detail");
        (note.IsNull("customerUuid"), note.S("status"), note.S("returnOrderNumber"), note.D("originalAmount"), note.D("redeemedAmount"))
            .Should().Be((true, "ACTIVE", ret.S("orderNumber"), 11.70m, 0m), "walk-in ⇒ bearer");
        note.A("transactions").Select(t => (t.S("type"), t.D("amount"), t.D("balanceAfter"))).Should().Equal(("ISSUE", 11.70m, 11.70m));
        var sh = await w.ShiftAsync(shift);
        (sh.D("totalStoreCreditIssued"), sh.D("totalCashRefunds"), sh.D("totalOtherRefunds"), sh.D("expectedCashClosing")).Should().Be((11.70m, 0m, 0m, 23.40m));

        // redeem exactly the balance: FULLY_REDEEMED, a Finance payment STORE_CREDIT with the CN number
        var redBody = w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(sc.G("uuid"), 11.70m)]);
        var red = await w.SellAsync(redBody, cashier, "redeem in full");
        var ro = await w.OrderAsync(red.G("orderUuid"));
        // FEPOS replays a store-credit sale online with the same localId: the stored order answers, nothing spent twice
        (await w.SellAsync(redBody, cashier, "replay")).G("orderUuid").Should().Be(ro.G("uuid"));
        // FEPOS resolves device line ids through GET orders/{uuid} with the cashier's token: lineNumber + variant
        (await k.Ok(k.Get($"/api/pos/orders/{ro.G("uuid")}", cashier), "order read by the cashier")).A("lines")
            .Select(x => (x.I("lineNumber"), x.G("variantUuid"))).Should().Equal((1, w.A.VariantUuid));
        var tender = ro.A("payments").Single();
        (tender.S("method"), tender.S("reference"), tender.G("creditNoteUuid")).Should().Be(("STORE_CREDIT", sc.S("number"), sc.G("uuid")));
        var fp = (await _f.QueryAsync("SELECT PaymentMethod, BankReference, Amount FROM finance.customer_payments WHERE SourceUuid = @s", ("@s", tender.G("uuid")))).Single();
        ((string)fp["PaymentMethod"]!, (string?)fp["BankReference"], Convert.ToDecimal(fp["Amount"])).Should().Be(("STORE_CREDIT", sc.S("number"), 11.70m));
        var spent = await k.Ok(k.Get($"/api/pos/credit-notes/lookup?number={sc.S("number")!.ToLowerInvariant()}", cashier), "lookup (cashier: REDEEM)");
        (spent.S("status"), spent.D("remainingBalance"), spent.D("redeemedAmount"), spent.IsNull("lastUsedAt")).Should().Be(("FULLY_REDEEMED", 0m, 11.70m, false));
        (await w.ShiftAsync(shift)).D("totalStoreCreditRedeemed").Should().Be(11.70m);
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(sc.G("uuid"), 11.70m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "nothing left", "This credit note has no balance left.");

        // void of the sale paid by store credit puts the value back (ADJUST, ACTIVE again)
        await k.Ok(k.Patch($"/api/pos/orders/{ro.G("uuid")}/void", new { reason = "wrong customer" }, sup), "void");
        var restored = await k.Ok(k.Get($"/api/pos/credit-notes/{sc.G("uuid")}", sup), "restored");
        (restored.S("status"), restored.D("remainingBalance"), restored.D("redeemedAmount")).Should().Be(("ACTIVE", 11.70m, 0m));
        restored.A("transactions").Select(t => (t.S("type"), t.D("amount"))).Should().Equal(("ISSUE", 11.70m), ("REDEEM", -11.70m), ("ADJUST", 11.70m));
        (await w.CustomerPaymentsAsync([tender.G("uuid")])).Values.Single().Status.Should().Be("REVERSED");
        (await w.ShiftAsync(shift)).D("totalStoreCreditRedeemed").Should().Be(0m);

        // split tender: store credit + cash
        var split = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m)], [ScPay(sc.G("uuid"), 11.70m), Pay("CASH", 11.70m)]), cashier, "split")).G("orderUuid"));
        (await w.CustomerPaymentsAsync(split.A("payments").Select(p => p.G("uuid")))).Values.Select(p => (p.Method, p.Amount))
            .Should().BeEquivalentTo(new[] { ("STORE_CREDIT", 11.70m), ("CASH", 11.70m) });
        (await k.Ok(k.Get($"/api/pos/credit-notes/{sc.G("uuid")}", sup), "after split")).S("status").Should().Be("FULLY_REDEEMED");

        // standalone issue (POS_CREDIT_NOTE_ISSUE), idempotent on localId; more than the balance / the order refused
        var localId = Guid.NewGuid().ToString("N");
        var ten = await k.Ok(k.Post("/api/pos/credit-notes", new { storeUuid = w.Store, amount = 10m, note = "goodwill", localId }, sup), "standalone 10");
        (await k.Ok(k.Post("/api/pos/credit-notes", new { storeUuid = w.Store, amount = 10m, localId }, sup), "replay")).G("uuid").Should().Be(ten.G("uuid"));
        ten.P("expiresAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(365), TimeSpan.FromDays(1), "the store's policy days");
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(ten.G("uuid"), 11.70m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "over balance", $"The credit note {ten.S("number")} has only 10.00 left.");
        var fifty = await k.Ok(k.Post("/api/pos/credit-notes", new { storeUuid = w.Store, amount = 50m }, sup), "standalone 50");
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(fifty.G("uuid"), 20m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "over the order", "exceeds the remaining balance of 11.70.");
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("STORE_CREDIT", 11.70m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "no note", "A store credit payment needs the credit note.");
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(fifty.G("uuid"), 11.70m)], offline: true), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "POS-CN-04", "Credit note redemption requires internet connectivity.");
        await w.SellAsync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(ten.G("uuid"), 10m), Pay("CASH", 1.70m)]), cashier, "partial standalone");

        // customer-bound note: refused for another customer, fine for its own (customer ledger balanced)
        var c = (await k.Ok(k.Post("/api/customers", Mods.Customer("Credit Holder")), "customer")).GetGuid();
        var bound = await k.Ok(k.Post("/api/pos/credit-notes", new { storeUuid = w.Store, customerUuid = c, amount = 30m }, sup), "bound note");
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(bound.G("uuid"), 11.70m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "walk-in", "This credit note belongs to another customer.");
        await w.SellAsync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(bound.G("uuid"), 11.70m)], customer: c), cashier, "own customer");
        // the customer's return as store credit is bound to them by default
        var cs = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.B, 1m, 5.99m)], [Pay("CASH", 7.01m)], customer: c), cashier)).G("orderUuid"));
        var cret = await k.Ok(k.Post($"/api/pos/orders/{cs.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(LineOf(cs, w.B), 1m)], "STORE_CREDIT"), sup), "customer return");
        var mine = await k.Ok(k.Get($"/api/pos/customers/{c}/credit-notes", cashier), "customer's notes (cashier)");
        mine.Items().Select(n => n.G("uuid")).Should().BeEquivalentTo(new[] { bound.G("uuid"), cret.P("storeCredit").G("uuid") });
        mine.Items().Should().OnlyContain(n => n.NG("customerUuid") == c);
        (await k.Ok(k.Get($"/api/customers/{c}/balance"), "balance")).D("balance").Should().Be(0m, "invoice/payment and credit note/refund pairs net to zero");
        var cl = await w.CustomerLedgerAsync(c);
        cl.Sum(e => e.Debit).Should().Be(cl.Sum(e => e.Credit));

        // expiry: reads EXPIRED at once, refused, then booked by the nightly job
        var exp = await k.Ok(k.Post("/api/pos/credit-notes", new { storeUuid = w.Store, amount = 5m }, sup), "to expire");
        await _f.ExecuteAsync("UPDATE pos.store_credit_notes SET ExpiresAt = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE Uuid = @u", ("@u", exp.G("uuid")));
        (await k.Ok(k.Get($"/api/pos/credit-notes/{exp.G("uuid")}", sup), "past expiry")).S("status").Should().Be("EXPIRED");
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(exp.G("uuid"), 5m), Pay("CASH", 6.70m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "expired", "This credit note has expired.");
        (await _f.RunPosJobAsync("StoreCreditExpiryJob")).Should().BeGreaterThanOrEqualTo(1);
        var expired = await k.Ok(k.Get($"/api/pos/credit-notes/{exp.G("uuid")}", sup), "after the job");
        (expired.S("status"), expired.D("remainingBalance")).Should().Be(("EXPIRED", 0m));
        var lastTxn = expired.A("transactions").Last();
        (lastTxn.S("type"), lastTxn.D("amount"), lastTxn.D("balanceAfter")).Should().Be(("EXPIRE", -5m, 0m));
        Convert.ToString((await _f.QueryAsync("SELECT Status FROM pos.store_credit_notes WHERE Uuid = @u", ("@u", exp.G("uuid"))))[0]["Status"]).Should().Be("EXPIRED");

        // cancel: only POS_CREDIT_NOTE_CANCEL (store manager), only ACTIVE
        var toCancel = await k.Ok(k.Post("/api/pos/credit-notes", new { storeUuid = w.Store, amount = 7m }, sup), "to cancel");
        (await k.Patch($"/api/pos/credit-notes/{toCancel.G("uuid")}/cancel", new { note = "x" }, cashier)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await k.Patch($"/api/pos/credit-notes/{toCancel.G("uuid")}/cancel", new { note = "x" }, sup)).Status.Should().Be(HttpStatusCode.Forbidden);
        var cancelled = await k.Ok(k.Patch($"/api/pos/credit-notes/{toCancel.G("uuid")}/cancel", new { note = "issued in error" }, mgr.Client), "cancel");
        (cancelled.S("status"), cancelled.D("remainingBalance")).Should().Be(("CANCELLED", 0m));
        (await k.Ok(k.Get($"/api/pos/credit-notes/{toCancel.G("uuid")}/transactions", sup), "txns")).Items()
            .Select(t => (t.S("type"), t.D("amount"), t.D("balanceAfter"))).Should().Equal(("ISSUE", 7m, 7m), ("CANCEL", -7m, 0m));
        (await k.Patch($"/api/pos/credit-notes/{toCancel.G("uuid")}/cancel", new { }, mgr.Client))
            .ShouldFail(HttpStatusCode.BadRequest, "twice", "Only an active credit note can be cancelled.");
        (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(toCancel.G("uuid"), 7m), Pay("CASH", 4.70m)]), cashier))
            .ShouldFail(HttpStatusCode.BadRequest, "cancelled", "This credit note was cancelled.");

        // reads: list filters, lookup 404
        var active = await k.Ok(k.Get($"/api/pos/credit-notes?status=ACTIVE&storeUuid={w.Store}&pageSize=100", sup), "active list");
        active.A("data").Should().NotBeEmpty().And.OnlyContain(n => n.S("status") == "ACTIVE");
        (await k.Get("/api/pos/credit-notes/lookup?number=CN-NOPE", cashier)).Status.Should().Be(HttpStatusCode.NotFound);

        // two tills spending one note at once: one wins, the other is refused, nothing double-spent
        var once = await k.Ok(k.Post("/api/pos/credit-notes", new { storeUuid = w.Store, amount = 11.70m }, sup), "race note");
        var onHand = await w.OnHandAsync(w.A);
        var answers = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(once.G("uuid"), 11.70m)]), cashier)));
        answers.Count(a => a.Status == HttpStatusCode.OK).Should().Be(1, string.Join(" | ", answers.Select(a => a.ToString())));
        answers.Single(a => a.Status != HttpStatusCode.OK).Status.Should().BeOneOf(HttpStatusCode.Conflict, HttpStatusCode.BadRequest);
        var raced = await k.Ok(k.Get($"/api/pos/credit-notes/{once.G("uuid")}", sup), "raced note");
        (raced.S("status"), raced.D("remainingBalance")).Should().Be(("FULLY_REDEEMED", 0m));
        raced.A("transactions").Count(t => t.S("type") == "REDEEM").Should().Be(1);
        (await w.OnHandAsync(w.A)).Should().Be(onHand - 1m, "only the winner's stock moved");
    }

    // ── Cross-store ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cross_store_returns_and_the_weekly_report()
    {
        var w = await _root.PosWorldAsync("XST");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var codes = await ReasonsAsync(w);
        var (store2, code2, t2, wh2) = await SecondStoreAsync(w);
        var s1 = await w.OpenShiftAsync(0m);
        var s2 = await w.OpenShiftAsync(50m, t2);
        var cashier = await w.CashierClientAsync();
        // the original store keeps a 5-day window and a 5 % fee; store 2 follows the default (30 days, no fee)
        await k.Ok(k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy(window: 5, fee: 5m)), "store 1 policy");

        var sale = await w.OrderAsync((await w.SellAsync(w.Order(s1, [Line(w.A, 3m, 10m)], [Pay("CASH", 35.10m)]), cashier)).G("orderUuid"));
        var la = LineOf(sale, w.A);
        var lookup = await k.Ok(k.Get($"/api/pos/orders/lookup?receipt={sale.S("orderNumber")}&storeUuid={store2}", sup), "lookup at store 2");
        (lookup.P("order").B("isSameStore"), lookup.P("order").G("storeUuid"), lookup.P("policy").B("crossStoreAllowed"),
                lookup.P("policy").B("requiresManagerForCrossStore"), lookup.P("policy").ND("returnWindowDays"), lookup.P("policy").D("restockingFeePct"))
            .Should().Be((false, w.Store, true, false, 5m, 0m), "window of the original store, the rest of the processing store");

        // the return at store 2: stock into store 2's warehouse, refund in store 2's shift, credit note on the original invoice
        var x1 = await k.Ok(k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(la, 1m)], "CASH", terminal: t2), sup), "cross-store return");
        (x1.B("isCrossStore"), x1.G("storeUuid"), x1.G("shiftUuid"), x1.G("originalStoreUuid"), x1.D("restockingFeeAmount"), x1.D("grandTotal"))
            .Should().Be((true, store2, s2, w.Store, 0m, 11.70m));
        x1.Flags().Should().Contain("CROSS_STORE");
        (await w.StockLedgerAsync(x1.G("uuid"))).Should().Equal((w.A.VariantUuid, "POS_RETURN", 1m, 0m));
        (await OnHandAtAsync(w, w.A, wh2.Uuid), await w.OnHandAsync(w.A)).Should().Be((1m, 97m));
        ((Guid)(await w.InvoiceRowAsync(x1.G("creditNoteUuid")))["OriginalInvoiceUuid"]!).Should().Be(sale.G("salesInvoiceUuid"));
        (await w.ShiftAsync(s2)).D("expectedCashClosing").Should().Be(50m - 11.70m);
        (await w.ShiftAsync(s1)).D("totalCashRefunds").Should().Be(0m);

        // a returner without POS_RETURN_CROSS_STORE
        var plain = await k.LoginWithPermissionsAsync("plainret", "POS_ORDER_RETURN");
        await k.Ok(k.Staff(store2, ServiceKit.AdminUserId(plain), "CASHIER", "8642"), "plain returner as staff of store 2");
        (await k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(la, 1m)], "CASH", terminal: t2), plain))
            .ShouldFail(HttpStatusCode.Forbidden, "POS_RETURN_CROSS_STORE", "You do not have permission to process returns from other stores.");

        // the processing store refuses cross-store returns
        await k.Ok(k.Put($"/api/pos/stores/{store2}/return-policy", Policy(crossStore: false)), "store 2 refuses");
        (await k.Ok(k.Get($"/api/pos/orders/{sale.G("uuid")}/returnable-lines?storeUuid={store2}", sup), "lookup")).P("policy").B("crossStoreAllowed").Should().BeFalse();
        (await k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(la, 1m)], "CASH", terminal: t2), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "acceptance", "This store does not accept returns from other locations.");

        // … or wants a manager: a supervisor with POS_RETURN_CROSS_STORE may authorize CROSS_STORE alone
        await k.Ok(k.Put($"/api/pos/stores/{store2}/return-policy", Policy(crossManager: true)), "store 2 wants a manager");
        ShouldNeedManager(await k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(la, 1m)], "CASH", terminal: t2), sup),
            "Manager authorization required.", "CROSS_STORE");
        var xTok = await k.Ok(Authorize(w, sale.G("uuid"), 11.70m, SupervisorPin, sup, t2, "CROSS_STORE"), "supervisor authorizes CROSS_STORE");
        xTok.I("managerUserId").Should().Be(w.Supervisor.Id);
        var x2 = await k.Ok(k.Post($"/api/pos/orders/{sale.G("uuid")}/return",
            Ret(codes["CHANGED_MIND"], [(la, 1m)], "CASH", terminal: t2, token: xTok.S("authorizationToken")), sup), "authorized cross-store return");
        x2.I("authorizedByUserId").Should().Be(w.Supervisor.Id);
        (await OnHandAtAsync(w, w.A, wh2.Uuid)).Should().Be(2m);

        // the ORIGINAL store's window (5 days) decides, also at store 2 (30 days)
        var old = await w.OrderAsync((await w.SellAsync(w.Order(s1, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)]), cashier)).G("orderUuid"));
        await AgeAsync(old.G("uuid"), 10);
        await k.Ok(k.Delete($"/api/pos/stores/{store2}/return-policy"), "store 2 back to default");
        var oldLookup = await k.Ok(k.Get($"/api/pos/orders/{old.G("uuid")}/returnable-lines?storeUuid={store2}", sup), "aged lookup at store 2");
        (oldLookup.P("policy").ND("returnWindowDays"), oldLookup.P("policy").B("withinReturnWindow")).Should().Be((5m, false));
        ShouldNeedManager(await k.Post($"/api/pos/orders/{old.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(LineOf(old, w.A), 1m)], "CASH", terminal: t2), sup),
            "Return window expired (5 days).", "WINDOW");

        // never offline
        (await k.Post("/api/pos/returns/sync", SyncBody("RETURN", sale.G("uuid"), codes["CHANGED_MIND"], [(la, 1m)], "CASH", true, t2), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "offline", "Cross-store returns require internet connectivity.");

        // FEATURE_POS_CROSS_STORE_RETURNS off
        await k.FeatureOkAsync(Module, "FEATURE_POS_CROSS_STORE_RETURNS", false);
        (await k.Ok(k.Get($"/api/pos/sync/config?terminalUuid={t2}", sup), "config")).P("features").B("crossStoreReturns").Should().BeFalse();
        (await k.Ok(k.Get($"/api/pos/orders/{sale.G("uuid")}/returnable-lines?storeUuid={store2}", sup), "lookup")).P("policy").B("crossStoreAllowed").Should().BeFalse();
        (await k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["CHANGED_MIND"], [(la, 1m)], "CASH", terminal: t2), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "feature off", "Cross-store returns are not enabled for your organization.");
        await k.FeatureOkAsync(Module, "FEATURE_POS_CROSS_STORE_RETURNS", true);

        // the weekly report: one line per store pair, to POS_RETURN_REPORTS holders only
        (await _f.RunPosJobAsync("CrossStoreReconciliationReport")).Should().BeGreaterThanOrEqualTo(2);
        var msgs = (await _f.QueryAsync("SELECT Message FROM notifications.notifications WHERE Type = 'POS_CROSS_STORE_REPORT' AND UserId = @u",
            ("@u", w.Supervisor.Id))).Select(r => (string)r["Message"]!).ToList();
        msgs.Should().ContainSingle().Which.Should().Contain($"sold at {w.StoreCode} → returned at {code2}: 2 return(s), 23.40");
        (await w.NotificationCountForUserAsync("POS_CROSS_STORE_REPORT", ServiceKit.AdminUserId(k.Admin))).Should().Be(1, "the org admin holds every permission");
        (await w.NotificationCountForUserAsync("POS_CROSS_STORE_REPORT", w.Cashier.Id)).Should().Be(0, "a cashier has no POS_RETURN_REPORTS");
    }

    // ── Exchanges ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exchanges_pay_even_refund_and_cross_store()
    {
        var w = await _root.PosWorldAsync("EXC");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var shift = await w.OpenShiftAsync(0m);
        var cashier = await w.CashierClientAsync();
        var codes = await ReasonsAsync(w);
        var c = (await k.Ok(k.Post("/api/customers", Mods.Customer("Exchange Co")), "customer")).GetGuid();

        var sale = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 3m, 10m), Line(w.B, 1m, 5.99m)], [Pay("CASH", 42.11m)], customer: c), cashier)).G("orderUuid"));
        Guid la = LineOf(sale, w.A), lb = LineOf(sale, w.B);
        var url = $"/api/pos/orders/{sale.G("uuid")}/exchange";

        // PAY: B (7.01) for A (11.70) → the customer pays 4.69
        (await k.Post(url, Exch(codes["WRONG_SIZE"], [(lb, 1m)], [Line(w.A, 1m, 10m)], []), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "net > 0 without payments", "Payments must cover the difference (4.69).");
        (await k.Post(url, Exch(codes["DUPLICATE"], [(lb, 1m)], [Line(w.A, 1m, 10m)], [Pay("CASH", 4.69m)]), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "RETURN-only reason", "cannot be used for an exchange.");
        // a manager reason: the authorized amount is the full return value (7.01), not the net (FEPOS); refundMethod is sent
        // although nothing is refunded (FEPOS) and is ignored
        var mgr = await k.PosUserAsync("STORE_MANAGER", "mgr");
        await k.Ok(k.Staff(w.Store, mgr.Id, "SUPERVISOR", ManagerPin), "manager staff");
        object PayEx(string? token) => Exch(codes["WARRANTY"], [(lb, 1m)], [Line(w.A, 1m, 10m)], [Pay("CASH", 5m, 0.31m)], "CARD", note: "seam split", token: token);
        ShouldNeedManager(await k.Post(url, PayEx(null), sup), "Manager authorization required.", "REASON");
        var netTok = (await k.Ok(Authorize(w, sale.G("uuid"), 4.69m, ManagerPin, sup, reasons: "REASON"), "authorize the net")).S("authorizationToken");
        ShouldNeedManager(await k.Post(url, PayEx(netTok), sup), "The refund exceeds the amount the manager authorized.", "REASON");
        var fullTok = (await k.Ok(Authorize(w, sale.G("uuid"), 7.01m, ManagerPin, sup, reasons: "REASON"), "authorize the return value")).S("authorizationToken");
        var pay = await k.Ok(k.Post(url, PayEx(fullTok), sup), "exchange PAY");
        pay.I("authorizedByUserId").Should().Be(mgr.Id);
        pay.S("orderNumber").Should().MatchRegex($"^EXC-{w.StoreCode}-\\d{{8}}-0001$");
        (pay.S("orderType"), pay.D("grandTotal"), pay.D("netAmount"), pay.S("settlement"), pay.S("paymentStatus")).Should().Be(("EXCHANGE", 4.69m, 4.69m, "PAY", "PAID"));
        pay.A("lines").Select(l => (l.G("variantUuid"), l.D("quantity"), l.D("lineTotal"), l.NG("originalLineUuid")))
            .Should().Equal((w.B.VariantUuid, -1m, -7.01m, lb), (w.A.VariantUuid, 1m, 11.70m, null));
        pay.A("payments").Select(p => (p.S("method"), p.D("amount"), p.B("isRefund"))).Should().Equal(("EXCHANGE", 7.01m, true), ("EXCHANGE", 7.01m, false), ("CASH", 5m, false));
        (pay.B("stockPosted"), pay.B("financePosted"), pay.S("postingError")).Should().Be((true, true, null));
        (await w.StockLedgerAsync(pay.G("uuid"))).Should().BeEquivalentTo(new[] { (w.B.VariantUuid, "POS_RETURN", 1m, 0m), (w.A.VariantUuid, "POS_SALE", 0m, 1m) });
        await VerifyExchangeFinanceAsync(w, pay, sale, creditNote: 7.01m, refunds: [("EXCHANGE", 7.01m)], invoice: 11.70m, tenders: [("EXCHANGE", 7.01m), ("CASH", 4.69m)]);

        // EVEN: A for A — nothing paid either way
        (await k.Post(url, Exch(codes["DEFECTIVE"], [(la, 1m)], [Line(w.A, 1m, 10m)], [Pay("CASH", 1m)]), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "net 0 with a payment", "No payment is taken when the new items do not cost more.");
        var even = await k.Ok(k.Post(url, Exch(codes["DEFECTIVE"], [(la, 1m)], [Line(w.A, 1m, 10m)], []), sup), "exchange EVEN");
        (even.D("grandTotal"), even.D("netAmount"), even.S("settlement")).Should().Be((0m, 0m, "EVEN"));
        even.A("payments").Select(p => (p.S("method"), p.D("amount"), p.B("isRefund"))).Should().Equal(("EXCHANGE", 11.70m, true), ("EXCHANGE", 11.70m, false));
        await VerifyExchangeFinanceAsync(w, even, sale, creditNote: 11.70m, refunds: [("EXCHANGE", 11.70m)], invoice: 11.70m, tenders: [("EXCHANGE", 11.70m)]);

        // REFUND with a 10 % restocking fee: A (11.70, fee 1.17) for D (3.90) → 6.63 back in cash
        await k.Ok(k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy(fee: 10m)), "10 % fee");
        (await k.Post(url, Exch(codes["CHANGED_MIND"], [(la, 1m)], [Line(w.D, 1m, 3.33m)], []), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "no refund method", "Choose how to refund the difference.");
        var back = await k.Ok(k.Post(url, Exch(codes["CHANGED_MIND"], [(la, 1m)], [Line(w.D, 1m, 3.33m)], [], "CASH"), sup), "exchange REFUND");
        (back.D("restockingFeeAmount"), back.D("grandTotal"), back.D("netAmount"), back.S("settlement"), back.S("paymentStatus"))
            .Should().Be((1.17m, -7.80m, -6.63m, "REFUND", "REFUNDED"));
        back.A("payments").Select(p => (p.S("method"), p.D("amount"), p.B("isRefund")))
            .Should().Equal(("EXCHANGE", 3.90m, true), ("EXCHANGE", 3.90m, false), ("CASH", 6.63m, true));
        await VerifyExchangeFinanceAsync(w, back, sale, creditNote: 10.53m, refunds: [("EXCHANGE", 3.90m), ("CASH", 6.63m)], invoice: 3.90m, tenders: [("EXCHANGE", 3.90m)]);
        (await w.StockLedgerAsync(back.G("uuid"))).Should().BeEquivalentTo(new[] { (w.A.VariantUuid, "POS_RETURN", 1m, 0m), (w.D.VariantUuid, "POS_SALE", 0m, 1m) });

        // an exchange is neither voidable nor returnable; the exchange window 0 = no exchanges
        (await k.Patch($"/api/pos/orders/{back.G("uuid")}/void", new { reason = "x" }, sup)).ShouldFail(HttpStatusCode.BadRequest, "void", "Only a sale can be voided.");
        (await k.Post($"/api/pos/orders/{back.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(back.A("lines").Last().G("uuid"), 1m)], "CASH"), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "not a sale", "Only sales can be returned.");
        await k.Ok(k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy(exchangeWindow: 0)), "no exchanges");
        (await k.Ok(k.Get($"/api/pos/orders/{sale.G("uuid")}/returnable-lines", sup), "lookup")).P("policy").B("withinExchangeWindow").Should().BeFalse();
        (await k.Post(url, Exch(codes["DEFECTIVE"], [(la, 1m)], [Line(w.A, 1m, 10m)], []), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "exchange window 0", "Exchanges are not allowed under this store's policy.");
        await k.Ok(k.Delete($"/api/pos/stores/{w.Store}/return-policy"), "default again");

        // remainder rule counts the exchanged units; the original
        var orig = await w.OrderAsync(sale.G("uuid"));
        (orig.S("status"), orig.A("lines").Select(l => l.D("returnedQuantity")).ToList()).Should().Match<(string?, List<decimal>)>(x =>
            x.Item1 == "PARTIALLY_RETURNED" && x.Item2.SequenceEqual(new[] { 2m, 1m }));
        (await k.Ok(k.Get($"/api/pos/orders/{sale.G("uuid")}/returnable-lines", sup), "lookup")).P("order").A("lines")
            .Single(l => l.G("lineUuid") == la).D("refundIfAllReturned").Should().Be(11.70m);

        // shift: sale + 3 exchanges
        var sh = await w.ShiftAsync(shift);
        (sh.I("orderCount"), sh.I("refundCount"), sh.D("totalCashSales"), sh.D("totalCashRefunds"), sh.D("expectedCashClosing"))
            .Should().Be((4, 3, 46.80m, 6.63m, 40.17m));
        (sh.D("grossTotal"), sh.D("netTotal")).Should().Be((69.41m, 40.17m), "gross: 42.11 + 11.70 + 11.70 + 3.90; net loses 7.01 + 11.70 + 10.53");

        // cross-store exchange of the last A at store 2 (store 2's warehouse in and out)
        var (store2, _, t2, wh2) = await SecondStoreAsync(w);
        var s2 = await w.OpenShiftAsync(0m, t2);
        var xs = await k.Ok(k.Post(url, Exch(codes["DEFECTIVE"], [(la, 1m)], [Line(w.A, 1m, 10m)], [], terminal: t2), sup), "cross-store exchange");
        (xs.B("isCrossStore"), xs.G("storeUuid"), xs.G("shiftUuid"), xs.S("settlement")).Should().Be((true, store2, s2, "EVEN"));
        xs.Flags().Should().Contain("CROSS_STORE");
        (await OnHandAtAsync(w, w.A, wh2.Uuid)).Should().Be(0m, "one in, one out at store 2");
        await VerifyExchangeFinanceAsync(w, xs, sale, creditNote: 11.70m, refunds: [("EXCHANGE", 11.70m)], invoice: 11.70m, tenders: [("EXCHANGE", 11.70m)]);
        (await w.OrderAsync(sale.G("uuid"))).S("status").Should().Be("RETURNED");

        // the customer's ledger is balanced: every credit note met by its refunds, every invoice by its payments
        (await k.Ok(k.Get($"/api/customers/{c}/balance"), "balance")).D("balance").Should().Be(0m);
        var ledger = await w.CustomerLedgerAsync(c);
        ledger.Sum(e => e.Debit).Should().Be(ledger.Sum(e => e.Credit), string.Join("; ", ledger));
    }

    /// <summary>The exchange's two Finance documents: credit note (refund tenders) against the original invoice, and the sale on the derived uuid.</summary>
    private async Task VerifyExchangeFinanceAsync(PosWorld w, JsonElement ex, JsonElement sale, decimal creditNote,
        (string Method, decimal Amount)[] refunds, decimal invoice, (string Method, decimal Amount)[] tenders)
    {
        var what = ex.S("orderNumber");
        var cn = await w.InvoiceRowAsync(ex.G("creditNoteUuid"));
        ((string)cn["Status"]!, Convert.ToDecimal(cn["GrandTotal"]), (Guid)cn["OriginalInvoiceUuid"]!).Should().Be(("CREDIT_NOTE", creditNote, sale.G("salesInvoiceUuid")), what);
        (await _f.QueryAsync("SELECT Method, Amount FROM finance.customer_refunds WHERE CreditNoteUuid = @c", ("@c", ex.G("creditNoteUuid"))))
            .Select(r => ((string)r["Method"]!, Convert.ToDecimal(r["Amount"]))).Should().BeEquivalentTo(refunds, $"{what}: refunds");
        var inv = await w.InvoiceRowAsync(ex.G("salesInvoiceUuid"));
        (Convert.ToDecimal(inv["GrandTotal"]), (string)inv["Status"]!).Should().Be((invoice, "PAID"), $"{what}: the sale part");
        ((Guid)inv["SourceUuid"]!).Should().NotBe(ex.G("uuid"), "the sale part has its own derived source uuid");
        var paid = await w.CustomerPaymentsAsync(ex.A("payments").Where(p => !p.B("isRefund")).Select(p => p.G("uuid")));
        paid.Values.Select(p => (p.Method, p.Amount)).Should().BeEquivalentTo(tenders, $"{what}: payments");
    }

    // ── Offline sync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Offline_return_sync_flags_and_grace()
    {
        var w = await _root.PosWorldAsync("OFF");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var shift = await w.OpenShiftAsync(0m);
        var cashier = await w.CashierClientAsync();
        var codes = await ReasonsAsync(w);
        var mgr = await k.PosUserAsync("STORE_MANAGER", "mgr");
        await k.Ok(k.Staff(w.Store, mgr.Id, "SUPERVISOR", ManagerPin), "manager staff");

        var sale = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 10m, 10m)], [Pay("CASH", 117.00m)]), cashier)).G("orderUuid"));
        var la = LineOf(sale, w.A);
        var other = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 1m, 10m)], [Pay("CASH", 11.70m)]), cashier)).G("orderUuid"));

        // idempotent on localId
        var body = SyncBody("RETURN", sale.G("uuid"), codes["CHANGED_MIND"], [(la, 1m)], "CASH", false, w.Terminal);
        var first = await k.Ok(k.Post("/api/pos/returns/sync", body, sup), "sync return");
        (first.B("success"), first.Flags().Count).Should().Be((true, 0));
        (await k.Ok(k.Post("/api/pos/returns/sync", body, sup), "replay")).G("orderUuid").Should().Be(first.G("orderUuid"));
        (await w.StockLedgerAsync(first.G("orderUuid"))).Should().ContainSingle("posted once");
        var localId = JsonSerializer.SerializeToElement(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)).S("localId");
        (await k.Post("/api/pos/returns/sync", SyncBody("RETURN", other.G("uuid"), codes["CHANGED_MIND"], [(LineOf(other, w.A), 1m)], "CASH", false, w.Terminal, localId: localId), sup))
            .ShouldFail(HttpStatusCode.Conflict, "localId of another original", "already belongs to another order");
        (await k.Post("/api/pos/returns/sync", new { kind = "SWAP", originalOrderUuid = sale.G("uuid"), localId = "x" }, sup))
            .ShouldFail(HttpStatusCode.BadRequest, "kind", "kind must be RETURN or EXCHANGE.");

        // window expired: refused online, flagged offline
        await AgeAsync(sale.G("uuid"), 40);
        ShouldNeedManager(await k.Post("/api/pos/returns/sync", SyncBody("RETURN", sale.G("uuid"), codes["CHANGED_MIND"], [(la, 1m)], "CASH", false, w.Terminal), sup),
            "Return window expired (30 days).", "WINDOW");
        var flagged = await k.Ok(k.Post("/api/pos/returns/sync", SyncBody("RETURN", sale.G("uuid"), codes["CHANGED_MIND"], [(la, 1m)], "CASH", true, w.Terminal), sup), "offline, no manager");
        flagged.Flags().Should().BeEquivalentTo("OFFLINE", "POLICY_OVERRIDDEN_OFFLINE");
        flagged.A("warnings").Select(x => x.GetString()).Should().Contain("Return window expired (30 days).");
        var byMgr = await k.Ok(k.Post("/api/pos/returns/sync", SyncBody("RETURN", sale.G("uuid"), codes["CHANGED_MIND"], [(la, 1m)], "CASH", true, w.Terminal, mgr.Id), sup), "offline, manager PIN");
        byMgr.Flags().Should().BeEquivalentTo("OFFLINE", "AUTHORIZED_OFFLINE");
        (await w.OrderAsync(byMgr.G("orderUuid"))).I("authorizedByUserId").Should().Be(mgr.Id);
        var bySup = await k.Ok(k.Post("/api/pos/returns/sync", SyncBody("RETURN", sale.G("uuid"), codes["CHANGED_MIND"], [(la, 1m)], "CASH", true, w.Terminal, w.Supervisor.Id), sup), "offline, supervisor PIN");
        bySup.Flags().Should().BeEquivalentTo("OFFLINE", "AUTHORIZED_OFFLINE", "AUTHORIZATION_INVALID");
        bySup.A("warnings").Select(x => x.GetString()).Should().Contain(x => x!.Contains("cannot authorize this return"));

        // refund method against the policy, exchanges not allowed: flagged offline
        await k.Ok(k.Put($"/api/pos/stores/{w.Store}/return-policy", Policy(window: 60, exchangeWindow: 0, cash: false)), "strict policy");
        var cashOff = await k.Ok(k.Post("/api/pos/returns/sync", SyncBody("RETURN", sale.G("uuid"), codes["CHANGED_MIND"], [(la, 1m)], "CASH", true, w.Terminal), sup), "offline cash");
        cashOff.Flags().Should().BeEquivalentTo("OFFLINE", "POLICY_OVERRIDDEN_OFFLINE");
        cashOff.A("warnings").Select(x => x.GetString()).Should().Contain("Refund method not allowed by the store's return policy.");
        var exOff = await k.Ok(k.Post("/api/pos/returns/sync", SyncBody("EXCHANGE", sale.G("uuid"), codes["DEFECTIVE"], [(la, 1m)], null, true, w.Terminal,
            newLines: [Line(w.A, 1m, 10m)]), sup), "offline exchange");
        exOff.Flags().Should().Contain("POLICY_OVERRIDDEN_OFFLINE");
        exOff.A("warnings").Select(x => x.GetString()).Should().Contain("Exchanges are not allowed under this store's policy.");
        exOff.S("orderNumber").Should().StartWith($"EXC-{w.StoreCode}-");
        (await k.Post("/api/pos/returns/sync", SyncBody("EXCHANGE", sale.G("uuid"), codes["DEFECTIVE"], [(la, 1m)], null, false, w.Terminal,
                newLines: [Line(w.A, 1m, 10m)]), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "online", "Exchanges are not allowed under this store's policy.");
        await k.Ok(k.Delete($"/api/pos/stores/{w.Store}/return-policy"), "default again");

        // an offline store credit: the device's number is kept and found
        var offNo = $"CN-{w.StoreCode}-OFF-{Guid.NewGuid():N}"[..(w.StoreCode.Length + 15)].ToUpperInvariant();
        var scOff = await k.Ok(k.Post("/api/pos/returns/sync", SyncBody("RETURN", sale.G("uuid"), codes["DEFECTIVE"], [(la, 1m)], "STORE_CREDIT", true, w.Terminal,
            creditOffline: offNo), sup), "offline store credit");
        var scOrder = await w.OrderAsync(scOff.G("orderUuid"));
        scOrder.P("storeCredit").S("number").Should().MatchRegex($"^CN-{w.StoreCode}-\\d{{8}}-\\d{{4}}$");
        var found = await k.Ok(k.Get($"/api/pos/credit-notes/lookup?number={offNo}", cashier), "lookup by the offline number");
        (found.G("uuid"), found.S("offlineNumber")).Should().Be((scOrder.P("storeCredit").G("uuid"), offNo));

        // FEPOS extras: cashierUserId (active staff with POS_ORDER_RETURN, as A38 sale sync) and sessionLocalId (the device session
        // pushed before the return) are honoured; a named user who may not return falls back to the pusher with a warning
        var sessionLocal = Guid.NewGuid().ToString("N");
        var offSale = await w.SellAsync(new
        {
            localId = Guid.NewGuid().ToString("N"), terminalUuid = w.Terminal, shiftUuid = shift, sessionLocalId = sessionLocal, cashierUserId = mgr.Id,
            isOffline = true, createdAtLocal = DateTimeOffset.UtcNow.AddMinutes(-5), lines = new[] { Line(w.B, 1m, 5.99m) },
            orderDiscountType = "NONE", orderDiscountValue = 0m, payments = new[] { Pay("CASH", 7.01m) }
        }, sup, "offline sale opening the device session");
        var deviceSession = (await w.OrderAsync(offSale.G("orderUuid"))).G("sessionUuid");
        var named = await k.Ok(k.Post("/api/pos/returns/sync", With(SyncBody("RETURN", sale.G("uuid"), codes["DEFECTIVE"], [(la, 1m)], "CASH", true, w.Terminal),
            ("cashierUserId", mgr.Id), ("sessionLocalId", sessionLocal)), sup), "offline return by the named cashier");
        named.A("warnings").Select(x => x.GetString()).Should().NotContain(x => x!.Contains("cannot process returns"));
        var namedOrder = await w.OrderAsync(named.G("orderUuid"));
        (namedOrder.I("cashierUserId"), namedOrder.I("syncedByUserId"), namedOrder.NG("sessionUuid")).Should().Be((mgr.Id, w.Supervisor.Id, deviceSession));
        var refusedName = await k.Ok(k.Post("/api/pos/returns/sync", With(SyncBody("RETURN", sale.G("uuid"), codes["DEFECTIVE"], [(la, 1m)], "CASH", true, w.Terminal),
            ("cashierUserId", w.Cashier.Id)), sup), "named cashier without POS_ORDER_RETURN");
        refusedName.A("warnings").Select(x => x.GetString()).Should().Contain(x => x!.Contains("cannot process returns in this store"));
        (await w.OrderAsync(refusedName.G("orderUuid"))).I("cashierUserId").Should().Be(w.Supervisor.Id);

        // beyond what is returnable: refused offline too (10 sold: 7 above incl. the exchange and the store credit, + these 2 = 9)
        (await k.Post("/api/pos/returns/sync", SyncBody("RETURN", sale.G("uuid"), codes["DEFECTIVE"], [(la, 2m)], "CASH", true, w.Terminal), sup))
            .ShouldFail(HttpStatusCode.BadRequest, "over returnable offline", "Only 1 returnable.");

        // grace: POS switched off with the shift open — the offline return still lands
        var impact = await k.Ok(k.Disable(Module), "POS off (grace)");
        impact.S("status").Should().Be("GRACE");
        try
        {
            var late = await k.Ok(k.Post("/api/pos/returns/sync", SyncBody("RETURN", other.G("uuid"), codes["DEFECTIVE"], [(LineOf(other, w.A), 1m)], "CASH", true, w.Terminal), sup),
                "returns/sync in grace");
            late.B("success").Should().BeTrue();
        }
        finally
        {
            await k.EnableOkAsync(Module);
        }
        (await w.OrderAsync(other.G("uuid"))).S("status").Should().Be("RETURNED");
    }

    // ── Permissions & sub-features ───────────────────────────────────────────────

    [Fact]
    public async Task Permissions_and_sub_features()
    {
        var w = await _root.PosWorldAsync("PRM");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var shift = await w.OpenShiftAsync(0m);
        var cashier = await w.CashierClientAsync();
        var mgr = await k.PosUserAsync("STORE_MANAGER", "mgr");
        var codes = await ReasonsAsync(w);
        var sale = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 2m, 10m)], [Pay("CASH", 23.40m)]), cashier)).G("orderUuid"));
        var la = LineOf(sale, w.A);
        var walkIn = sale.G("customerUuid");
        var note = await k.Ok(k.Post("/api/pos/credit-notes", new { storeUuid = w.Store, amount = 3m }, sup), "a note (supervisor: ISSUE)");
        var reasonBody = new { code = "PRM_X", name = "x", appliesTo = "BOTH", sortOrder = 1, isActive = true };

        async Task Status(HttpClient who, string label, HttpMethod method, string url, object? body, HttpStatusCode expected)
        {
            var api = await k.Send(method, url, body, who);
            if (expected == HttpStatusCode.OK) api.Status.Should().NotBe(HttpStatusCode.Forbidden, $"{label} {method} {url} — {api}");
            else api.Status.Should().Be(expected, $"{label} {method} {url} — {api}");
        }
        var get = HttpMethod.Get; var post = HttpMethod.Post; var put = HttpMethod.Put; var patch = HttpMethod.Patch;
        var ret = Ret(codes["DEFECTIVE"], [(la, 1m)], "CASH");
        const HttpStatusCode no = HttpStatusCode.Forbidden, yes = HttpStatusCode.OK;

        // cashier: POS_ORDER_CREATE + POS_CREDIT_NOTE_REDEEM
        await Status(cashier, "cashier", get, "/api/pos/return-reasons", null, no);
        await Status(cashier, "cashier", get, "/api/pos/sync/return-reasons", null, yes);
        await Status(cashier, "cashier", get, $"/api/pos/sync/return-policy?terminalUuid={w.Terminal}", null, yes);
        await Status(cashier, "cashier", get, $"/api/pos/orders/lookup?receipt={sale.S("orderNumber")}", null, no);
        await Status(cashier, "cashier", post, $"/api/pos/orders/{sale.G("uuid")}/return", ret, no);
        await Status(cashier, "cashier", post, $"/api/pos/orders/{sale.G("uuid")}/exchange", ret, no);
        await Status(cashier, "cashier", post, "/api/pos/returns/sync", ret, no);
        await Status(cashier, "cashier", post, "/api/pos/returns/authorize", new { terminalUuid = w.Terminal, originalOrderUuid = sale.G("uuid"), amount = 1m, pin = ManagerPin }, no);
        await Status(cashier, "cashier", get, "/api/pos/credit-notes", null, no);
        await Status(cashier, "cashier", get, $"/api/pos/credit-notes/{note.G("uuid")}", null, no);
        await Status(cashier, "cashier", get, $"/api/pos/credit-notes/lookup?number={note.S("number")}", null, yes);
        await Status(cashier, "cashier", get, $"/api/pos/customers/{walkIn}/credit-notes", null, yes);
        await Status(cashier, "cashier", post, "/api/pos/credit-notes", new { storeUuid = w.Store, amount = 1m }, no);
        await Status(cashier, "cashier", patch, $"/api/pos/credit-notes/{note.G("uuid")}/cancel", new { }, no);
        await Status(cashier, "cashier", get, $"/api/pos/stores/{w.Store}/return-policy", null, no);

        // supervisor: returns, cross-store, reports, issue, view — no override/cancel/policy/reason management
        await Status(sup, "supervisor", get, "/api/pos/return-reasons", null, yes);
        await Status(sup, "supervisor", post, "/api/pos/return-reasons", reasonBody, no);
        await Status(sup, "supervisor", get, "/api/pos/return-policies", null, no);
        await Status(sup, "supervisor", put, "/api/pos/return-policies/default", Policy(), no);
        await Status(sup, "supervisor", get, $"/api/pos/stores/{w.Store}/return-policy", null, yes);
        await Status(sup, "supervisor", get, "/api/pos/credit-notes", null, yes);
        await Status(sup, "supervisor", get, $"/api/pos/credit-notes/{note.G("uuid")}/transactions", null, yes);
        await Status(sup, "supervisor", patch, $"/api/pos/credit-notes/{note.G("uuid")}/cancel", new { }, no);
        await Status(sup, "supervisor", post, $"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(la, 1m)], "CASH"), yes);

        // store manager: + cancel (and override), still no policy / reason management
        await Status(mgr.Client, "manager", post, "/api/pos/return-reasons", reasonBody, no);
        await Status(mgr.Client, "manager", put, $"/api/pos/stores/{w.Store}/return-policy", Policy(), no);
        await Status(mgr.Client, "manager", patch, $"/api/pos/credit-notes/{note.G("uuid")}/cancel", new { }, yes);

        // the org admin: policy and reason management
        await Status(k.Admin, "admin", post, "/api/pos/return-reasons", reasonBody, yes);
        await Status(k.Admin, "admin", get, "/api/pos/return-policies", null, yes);

        // config with both sub-features on
        var cfg = await k.Ok(k.Get($"/api/pos/sync/config?terminalUuid={w.Terminal}", sup), "config");
        (cfg.P("features").B("creditNotes"), cfg.P("features").B("crossStoreReturns"), cfg.Strings("paymentMethods").Contains("STORE_CREDIT"))
            .Should().Be((true, true, true));

        // FEATURE_POS_CREDIT_NOTES off: the issue endpoint is unlicensed, no STORE_CREDIT tender or refund; reads stay
        await k.FeatureOkAsync(Module, "FEATURE_POS_CREDIT_NOTES", false);
        try
        {
            var refused = await k.Post("/api/pos/credit-notes", new { storeUuid = w.Store, amount = 1m }, sup);
            refused.Status.Should().Be(HttpStatusCode.Forbidden, refused.ToString());
            refused.Body.S("errorCode").Should().Be("MODULE_NOT_LICENSED");
            var off = await k.Ok(k.Get($"/api/pos/sync/config?terminalUuid={w.Terminal}", sup), "config without credit notes");
            (off.P("features").B("creditNotes"), off.Strings("paymentMethods").Contains("STORE_CREDIT")).Should().Be((false, false));
            (await k.Post($"/api/pos/orders/{sale.G("uuid")}/return", Ret(codes["DEFECTIVE"], [(la, 1m)], "STORE_CREDIT"), sup))
                .ShouldFail(HttpStatusCode.BadRequest, "store credit refund", "Refund method not allowed by the store's return policy.");
            (await k.Ok(k.Get($"/api/pos/credit-notes/{note.G("uuid")}", sup), "reads stay")).G("uuid").Should().Be(note.G("uuid"));
            (await w.Sync(w.Order(shift, [Line(w.A, 1m, 10m)], [ScPay(note.G("uuid"), 1m), Pay("CASH", 10.70m)]), cashier))
                .ShouldFail(HttpStatusCode.BadRequest, "redeem off", "Store credit is not enabled for your organization.");
        }
        finally
        {
            await k.FeatureOkAsync(Module, "FEATURE_POS_CREDIT_NOTES", true);
        }

        // FEATURE_POS_RETURNS off: the exchange endpoint is unlicensed like the return endpoint
        await k.FeatureOkAsync(Module, "FEATURE_POS_RETURNS", false);
        try
        {
            var refused = await k.Post($"/api/pos/orders/{sale.G("uuid")}/exchange", ret, sup);
            refused.Status.Should().Be(HttpStatusCode.Forbidden, refused.ToString());
            refused.Body.S("errorCode").Should().Be("MODULE_NOT_LICENSED");
            (await k.Post("/api/pos/returns/sync", SyncBody("RETURN", sale.G("uuid"), codes["DEFECTIVE"], [(la, 1m)], "CASH", false, w.Terminal), sup))
                .ShouldFail(HttpStatusCode.BadRequest, "returns off, online", "Returns are not enabled for your organization.");
            // an offline return already happened at the till: accepted with a warning (also what lets it land during POS grace)
            var late = await k.Ok(k.Post("/api/pos/returns/sync", SyncBody("RETURN", sale.G("uuid"), codes["DEFECTIVE"], [(la, 1m)], "CASH", true, w.Terminal), sup), "offline, returns off");
            late.A("warnings").Select(x => x.GetString()).Should().Contain("Returns are not enabled for your organization.");
        }
        finally
        {
            await k.FeatureOkAsync(Module, "FEATURE_POS_RETURNS", true);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static Guid LineOf(JsonElement order, Product p) => order.A("lines").Single(l => l.G("variantUuid") == p.VariantUuid).G("uuid");

    private static async Task<Dictionary<string, Guid>> ReasonsAsync(PosWorld w) =>
        (await w.K.Ok(w.K.Get("/api/pos/return-reasons?includeInactive=true"), "reasons")).Items().ToDictionary(r => r.S("code")!, r => r.G("uuid"));

    private static object Ret(Guid? reason, (Guid Line, decimal Qty)[] lines, string? method, string? note = null, string? token = null,
        Guid? terminal = null, Guid? creditCustomer = null) => new
    {
        localId = Guid.NewGuid().ToString("N"), terminalUuid = terminal, reasonUuid = reason, note, authorizationToken = token,
        lines = lines.Select(l => new { originalLineUuid = l.Line, quantity = l.Qty }).ToArray(), refundMethod = method,
        storeCreditCustomerUuid = creditCustomer
    };

    private static object Exch(Guid reason, (Guid Line, decimal Qty)[] lines, object[] newLines, object[] payments, string? method = null,
        Guid? terminal = null, string? note = null, string? token = null) => new
    {
        localId = Guid.NewGuid().ToString("N"), terminalUuid = terminal, reasonUuid = reason, note, authorizationToken = token,
        lines = lines.Select(l => new { originalLineUuid = l.Line, quantity = l.Qty }).ToArray(), refundMethod = method,
        newLines, orderDiscountType = "NONE", orderDiscountValue = 0m, payments
    };

    private static object SyncBody(string kind, Guid original, Guid reason, (Guid Line, decimal Qty)[] lines, string? method, bool offline, Guid terminal,
        int? authorizedBy = null, string? localId = null, string? creditOffline = null, object[]? newLines = null) => new
    {
        kind, originalOrderUuid = original, createdAtLocal = DateTimeOffset.UtcNow, isOffline = offline, authorizedByUserId = authorizedBy,
        storeCreditOfflineNumber = creditOffline, localId = localId ?? Guid.NewGuid().ToString("N"), terminalUuid = terminal, reasonUuid = reason,
        lines = lines.Select(l => new { originalLineUuid = l.Line, quantity = l.Qty }).ToArray(), refundMethod = method,
        newLines = newLines ?? Array.Empty<object>(), orderDiscountType = "NONE", orderDiscountValue = 0m, payments = Array.Empty<object>()
    };

    /// <summary>A request body plus fields the FEPOS client sends beyond the contract.</summary>
    private static Dictionary<string, object?> With(object body, params (string Key, object? Value)[] extra)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var d = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(body, options), options)!;
        foreach (var (key, value) in extra) d[key] = value;
        return d;
    }

    private static object ScPay(Guid note, decimal amount) =>
        new { localId = Guid.NewGuid().ToString("N"), method = "STORE_CREDIT", amount, changeGiven = 0m, creditNoteUuid = note };

    private static object Policy(string? name = null, int? window = 30, int? exchangeWindow = null, bool crossStore = true, bool crossManager = false,
        decimal fee = 0m, decimal? maxNoAuth = null, bool toOriginal = false, bool cash = true, bool credit = true, int? expiry = 365, bool active = true,
        string? rowVersion = null) => new
    {
        name, returnWindowDays = window, exchangeWindowDays = exchangeWindow, requireReceipt = true, allowNoReceiptExchange = false,
        allowCrossStoreReturn = crossStore, crossStoreRequiresManager = crossManager, restockingFeePct = fee, maxReturnNoAuth = maxNoAuth,
        refundToOriginalMethod = toOriginal, allowCashRefund = cash, allowCreditNoteRefund = credit, creditNoteExpiryDays = expiry,
        isActive = active, rowVersion
    };

    private static Task<Api> Authorize(PosWorld w, Guid order, decimal amount, string pin, HttpClient client, Guid? terminal = null, params string[] reasons) =>
        w.K.Post("/api/pos/returns/authorize", new { terminalUuid = terminal ?? w.Terminal, originalOrderUuid = order, amount, pin, reasons }, client);

    private static void ShouldNeedManager(Api api, string message, params string[] reasons)
    {
        api.ShouldFail(HttpStatusCode.BadRequest, "manager needed", message);
        api.Result.B("requiresManager").Should().BeTrue(api.ToString());
        api.Result.A("reasons").Select(x => x.GetString()).Should().Equal(reasons);
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Moves a sale <paramref name="days"/> into the past (its store-local date decides the return window).</summary>
    private Task AgeAsync(Guid order, int days) =>
        _f.ExecuteAsync("UPDATE pos.orders SET CreatedAt = DATEADD(DAY, -@d, CreatedAt), CreatedAtLocal = DATEADD(DAY, -@d, CreatedAtLocal) WHERE Uuid = @u",
            ("@d", days), ("@u", order));

    private async Task<decimal> OnHandAtAsync(PosWorld w, Product p, Guid warehouse)
    {
        var rows = await _f.QueryAsync(
            "SELECT ISNULL(SUM(i.QtyOnHand), 0) AS Q FROM inventory.InventoryItems i JOIN inventory.ProductVariants v ON v.Id = i.VariantId " +
            "JOIN inventory.Warehouses wh ON wh.Id = i.WarehouseId WHERE v.Uuid = @v AND wh.Uuid = @w", ("@v", p.VariantUuid), ("@w", warehouse));
        return Convert.ToDecimal(rows[0]["Q"]);
    }

    /// <summary>A second store (its own warehouse, terminal T1) with the world's supervisor and cashier as staff.</summary>
    private static async Task<(Guid Store, string Code, Guid Terminal, Warehouse Wh)> SecondStoreAsync(PosWorld w)
    {
        var k = w.K;
        var wh2 = await k.CreateWarehouseAsync();
        var code = $"X{Guid.NewGuid():N}"[..8].ToUpperInvariant();
        var store = (await k.Ok(k.CreateStore(code, wh2.Uuid), "second store")).G("uuid");
        var t = (await k.Ok(k.Post($"/api/pos/stores/{store}/terminals", new { code = "T1", name = "Till 1" }), "terminal of store 2")).G("uuid");
        await k.Ok(k.Staff(store, w.Supervisor.Id, "SUPERVISOR", SupervisorPin), "supervisor staff of store 2");
        await k.Ok(k.Staff(store, w.Cashier.Id, "CASHIER", CashierPin), "cashier staff of store 2");
        return (store, code, t, wh2);
    }
}
