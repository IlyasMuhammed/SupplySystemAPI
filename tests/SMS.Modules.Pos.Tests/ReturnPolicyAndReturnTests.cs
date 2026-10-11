using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Pos.Tests.A39;
using static SMS.Modules.Pos.Tests.PosHarness;

namespace SMS.Modules.Pos.Tests;

/// <summary>A39 §1–§3 — reasons, policies, lookup, manager authorization, the enriched return path (D-1, D-4, D-5, D-7, D-8).</summary>
public class ReturnPolicyAndReturnTests
{
    // ── §1 reasons & policies ──

    [Fact]
    public async Task Reasons_and_the_org_default_policy_are_seeded_lazily_once()
    {
        var h = new PosHarness();
        var all = await h.Policies.ListReasonsAsync(new PosReasonListFilter());
        all.Select(r => r.Code).Should().Equal("DEFECTIVE", "WRONG_ITEM", "WRONG_SIZE", "NOT_AS_EXPECTED", "CHANGED_MIND", "DUPLICATE",
            "PRICE_MATCH", "WARRANTY", "OTHER");
        all.Single(r => r.Code == "PRICE_MATCH").Should().Match<PosReturnReasonModel>(r => r.RequiresNote && r.RequiresManager);
        (await h.Policies.ListReasonsAsync(new PosReasonListFilter())).Should().HaveCount(9);   // no second seed
        (await h.Policies.ListReasonsAsync(new PosReasonListFilter { AppliesTo = "exchange" })).Select(r => r.Code)
            .Should().NotContain(["DUPLICATE", "PRICE_MATCH"]).And.HaveCount(7);

        var policies = await h.Policies.ListPoliciesAsync();
        policies.Should().ContainSingle();
        policies[0].Should().Match<PosReturnPolicyModel>(p => p.StoreUuid == null && p.ReturnWindowDays == 30 && p.CreditNoteExpiryDays == 365
            && p.AllowCashRefund && p.AllowCreditNoteRefund && p.AllowCrossStoreReturn && p.RestockingFeePct == 0m);
        (await h.Db.ReturnReasons.CountAsync()).Should().Be(9);
    }

    [Fact]
    public async Task Reason_upsert_validates_and_syncs_by_modifiedAt()
    {
        var h = new PosHarness();
        var created = await h.Policies.CreateReasonAsync(new PosReturnReasonUpsertRequest { Code = "late_delivery", Name = "Late", AppliesTo = "return", SortOrder = 99 });
        created.Code.Should().Be("LATE_DELIVERY");
        await FluentActions.Awaiting(async () => await h.Policies.CreateReasonAsync(new PosReturnReasonUpsertRequest { Code = "LATE_DELIVERY", Name = "x" }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Reason code LATE_DELIVERY already exists.");
        await FluentActions.Awaiting(async () => await h.Policies.CreateReasonAsync(new PosReturnReasonUpsertRequest { Code = "BAD CODE", Name = "x" }))
            .Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(async () => await h.Policies.CreateReasonAsync(new PosReturnReasonUpsertRequest { Code = "FEE", Name = "x", RestockingFeeOverride = 101m }))
            .Should().ThrowAsync<BadRequestException>();

        var before = h.Clock.UtcNow;
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        var off = await h.Policies.SetReasonStatusAsync(created.Uuid, false);
        off.IsActive.Should().BeFalse();
        (await h.Policies.ListReasonsAsync(new PosReasonListFilter())).Should().NotContain(r => r.Uuid == created.Uuid);
        var sync = await h.Policies.SyncReasonsAsync(before);
        sync.Reasons.Should().ContainSingle(r => r.Uuid == created.Uuid && !r.IsActive);
        sync.NextSince.Should().Be(off.ModifiedAt);
    }

    [Fact]
    public async Task Policy_resolution_store_override_then_org_default_then_none()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SetupAsync();
        (await h.Policies.GetEffectiveAsync(store.Uuid)).Source.Should().Be("ORG_DEFAULT");

        var own = await h.Policies.PutStorePolicyAsync(store.Uuid, Policy(p => { p.Name = "S1"; p.ReturnWindowDays = 14; }), Admin);
        own.StoreUuid.Should().Be(store.Uuid);
        var eff = await h.Policies.GetEffectiveAsync(store.Uuid);
        (eff.Source, eff.ReturnWindowDays).Should().Be(("STORE", 14));
        (await h.Policies.GetEffectiveForTerminalAsync(terminal.Uuid)).ReturnWindowDays.Should().Be(14);
        h.Hub.StoreEvents.Should().Contain(e => e.Event == PosHubEvents.ReturnPolicyChanged && e.Store == store.Uuid);
        (await h.Policies.ListPoliciesAsync()).Select(p => p.StoreUuid).Should().Equal(null, store.Uuid); // default first

        // An inactive store policy falls back to the default; with the default inactive too there is none.
        await h.Policies.PutStorePolicyAsync(store.Uuid, Policy(p => p.IsActive = false), Admin);
        (await h.Policies.GetEffectiveAsync(store.Uuid)).Source.Should().Be("ORG_DEFAULT");
        await h.Policies.PutDefaultPolicyAsync(Policy(p => p.IsActive = false), Admin);
        await FluentActions.Awaiting(async () => await h.Policies.GetEffectiveAsync(store.Uuid))
            .Should().ThrowAsync<BadRequestException>().WithMessage("No return policy configured. Contact your administrator.");
        var sale = await h.SellAsync(terminal, shift, item, 1);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(sale.Lines[0].Uuid, 1, await h.ReasonAsync("DEFECTIVE")), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("No return policy configured. Contact your administrator.");

        await h.Policies.DeleteStorePolicyAsync(store.Uuid);
        await h.Policies.PutDefaultPolicyAsync(Policy(), Admin);
        (await h.Policies.GetEffectiveAsync(store.Uuid)).Source.Should().Be("ORG_DEFAULT");
        await FluentActions.Awaiting(async () => await h.Policies.PutDefaultPolicyAsync(Policy(p => p.RestockingFeePct = 120m), Admin))
            .Should().ThrowAsync<BadRequestException>();
    }

    // ── §3 return rules ──

    [Fact]
    public async Task Return_with_a_reason_note_rules_and_the_A39_messages()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SetupAsync();
        var sale = await h.SellAsync(terminal, shift, item, 2);
        var line = sale.Lines[0].Uuid;

        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, await h.ReasonAsync("OTHER")), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("A note is required for this reason.");
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 3, await h.ReasonAsync("DEFECTIVE")), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Cannot return 3 of 'Product A'. Only 2 returnable.");
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, await h.ReasonAsync("DEFECTIVE"), method: null), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Choose a refund method.");

        var ret = await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, await h.ReasonAsync("OTHER"), note: "Box crushed"), Returner);
        (ret.ReturnReasonName, ret.ReturnReason, ret.GrandTotal, ret.RestockingFeeAmount).Should().Be(("Other", "Other: Box crushed", 11.70m, 0m));
        ret.ReturnReasonUuid.Should().Be(await h.ReasonAsync("OTHER"));
        h.Hub.StoreEvents.Should().Contain(e => e.Event == PosHubEvents.ReturnProcessed);

        await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, await h.ReasonAsync("DEFECTIVE")), Returner);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, await h.ReasonAsync("DEFECTIVE")), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("All items in this order have already been returned.");

        var other = await h.SellAsync(terminal, shift, item, 1);
        await h.Orders.VoidAsync(other.Uuid, "mistake", Returner);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(other.Uuid, Return(other.Lines[0].Uuid, 1, await h.ReasonAsync("DEFECTIVE")), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Cannot return a voided order.");
        var retOrder = await h.Db.Orders.FirstAsync(o => o.OrderType == PosOrderType.Return);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(retOrder.Uuid, Return(line, 1, await h.ReasonAsync("DEFECTIVE")), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Only sales can be returned.");
    }

    [Fact]
    public async Task Restocking_fee_rounds_away_from_zero_reason_override_waives_and_finance_lines_are_scaled()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SetupAsync();
        await h.Policies.PutStorePolicyAsync(store.Uuid, Policy(p => p.RestockingFeePct = 15m), Admin);
        var sale = await h.SellAsync(terminal, shift, item, 3); // 35.10
        var line = sale.Lines[0].Uuid;

        // 1 × 11.70 × 15 % = 1.755 → 1.76; refund 9.94.
        var ret = await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, await h.ReasonAsync("CHANGED_MIND")), Returner);
        (ret.RestockingFeeAmount, ret.GrandTotal, ret.Lines[0].LineTotal).Should().Be((1.76m, 9.94m, 11.70m));
        ret.Payments.Single().Amount.Should().Be(9.94m);
        var credit = h.Finance.Returns[ret.Uuid];
        credit.Lines.Single().LineTotal.Should().Be(9.94m);
        credit.Lines.Single().Should().Match<CounterReturnLine>(l => l.NetAmount + l.TaxAmount == l.LineTotal && l.Quantity == 1m);

        // DEFECTIVE overrides the fee with 0 %.
        var waived = await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, await h.ReasonAsync("DEFECTIVE")), Returner);
        (waived.RestockingFeeAmount, waived.GrandTotal).Should().Be((0m, 11.70m));

        var s = await h.Shifts.GetAsync(shift.Uuid, AdminCaller);
        s.TotalCashRefunds.Should().Be(21.64m);
        s.NetTotal.Should().Be(35.10m - 21.64m); // the fee stays in the takings
    }

    [Fact]
    public async Task Remainder_rule_holds_across_partial_returns()
    {
        var h = new PosHarness();
        var (_, terminal, shift, _) = await h.SetupAsync();
        var item = h.Catalog.Add("E2", 3.33m);
        var sync = await h.Orders.SyncAsync(new PosSyncOrderRequest
        {
            LocalId = "e2", TerminalUuid = terminal.Uuid, ShiftUuid = shift.Uuid,
            Lines = [new PosSyncLineRequest { VariantUuid = item.VariantUuid, Quantity = 3, UnitPrice = 3.33m, DiscountType = "PERCENT", DiscountValue = 10 }],
            Payments = [Pay(PosPaymentMethod.Cash, 10.52m)]
        }, Returner);
        var sale = await h.Orders.GetAsync(sync.OrderUuid!.Value, AdminCaller);
        sale.GrandTotal.Should().Be(10.52m);
        var line = sale.Lines[0].Uuid;
        var reason = await h.ReasonAsync("DEFECTIVE");

        var lookup = await h.Orders.ReturnableLinesAsync(sale.Uuid, null);
        lookup.Order!.Lines[0].RefundIfAllReturned.Should().Be(10.52m);
        (await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason), Returner)).GrandTotal.Should().Be(3.51m);
        (await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason), Returner)).GrandTotal.Should().Be(3.51m);
        (await h.Orders.ReturnableLinesAsync(sale.Uuid, null)).Order!.Lines[0].Should()
            .Match<PosLookupLineModel>(l => l.RefundIfAllReturned == 3.50m && l.ReturnableQuantity == 1m);
        var last = await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason), Returner);
        (last.GrandTotal, last.TaxTotal).Should().Be((3.50m, 0.51m));
    }

    // ── window, authorization ──

    [Fact]
    public async Task Expired_window_needs_a_manager_token_which_is_single_use_amount_capped_and_expires()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SetupAsync();
        var sale = await h.SellAsync(terminal, shift, item, 3);
        var line = sale.Lines[0].Uuid;
        var reason = await h.ReasonAsync("DEFECTIVE");
        h.Clock.Advance(TimeSpan.FromDays(31));

        var lookup = await h.Orders.LookupAsync(sale.OrderNumber.ToLowerInvariant(), null);
        lookup.Policy!.Should().Match<PosLookupPolicyModel>(p => !p.WithinReturnWindow && p.WindowDaysRemaining == 0);
        lookup.Order!.DaysSincePurchase.Should().Be(31);

        var refused = await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason), Returner))
            .Should().ThrowAsync<PosRuleException>().WithMessage("Return window expired (30 days).");
        refused.Which.Result.Should().BeOfType<PosManagerRequiredResult>().Which.Reasons.Should().Equal("WINDOW");

        // Wrong PIN, then a supervisor without POS_RETURN_OVERRIDE, then the manager.
        PosReturnAuthorizeRequest Auth(string pin, decimal amount = 11.70m, int? manager = null) =>
            new() { TerminalUuid = terminal.Uuid, OriginalOrderUuid = sale.Uuid, Amount = amount, Pin = pin, ManagerUserId = manager };
        await FluentActions.Awaiting(async () => await h.Orders.AuthorizeReturnAsync(Auth("9999"), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Invalid PIN.");
        await FluentActions.Awaiting(async () => await h.Orders.AuthorizeReturnAsync(Auth("2222"), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This manager cannot authorize returns.");
        var token = await h.Orders.AuthorizeReturnAsync(Auth("4444"), Returner);
        (token.ManagerUserId, token.ManagerName, token.ExpiresAt).Should().Be((Manager, "Mona", h.Clock.UtcNow.AddMinutes(5)));
        (await h.Db.ReturnAuthorizations.SingleAsync()).TokenHash.Should().NotBe(token.AuthorizationToken).And.HaveLength(64); // only the hash

        var ret = await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason, token: token.AuthorizationToken), Returner);
        (ret.AuthorizedByUserId, ret.AuthorizedByName).Should().Be((Manager, "Mona"));
        ret.AuthorizedAt.Should().NotBeNull();

        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason, token: token.AuthorizationToken), Returner))
            .Should().ThrowAsync<PosRuleException>().WithMessage("The manager authorization is invalid or has expired.");

        var small = await h.Orders.AuthorizeReturnAsync(Auth("4444", 5m), Returner);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason, token: small.AuthorizationToken), Returner))
            .Should().ThrowAsync<PosRuleException>().WithMessage("The refund exceeds the amount the manager authorized.");

        var stale = await h.Orders.AuthorizeReturnAsync(Auth("4444", manager: Manager), Returner);
        h.Clock.Advance(TimeSpan.FromMinutes(6));
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason, token: stale.AuthorizationToken), Returner))
            .Should().ThrowAsync<PosRuleException>().WithMessage("The manager authorization is invalid or has expired.");
    }

    [Fact]
    public async Task Authorization_shares_the_terminal_pin_lockout()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SetupAsync();
        var sale = await h.SellAsync(terminal, shift, item, 1);
        var req = new PosReturnAuthorizeRequest { TerminalUuid = terminal.Uuid, OriginalOrderUuid = sale.Uuid, Amount = 11.70m, Pin = "0000" };
        for (var i = 0; i < 4; i++)
            await FluentActions.Awaiting(async () => await h.Orders.AuthorizeReturnAsync(req, Returner)).Should().ThrowAsync<BadRequestException>().WithMessage("Invalid PIN.");
        await FluentActions.Awaiting(async () => await h.Orders.AuthorizeReturnAsync(req, Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Terminal locked until *");
        req.Pin = "4444";
        await FluentActions.Awaiting(async () => await h.Orders.AuthorizeReturnAsync(req, Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Terminal locked until *");
        await FluentActions.Awaiting(async () => await h.Sessions.PinSignInAsync(new PosPinSignInRequest { TerminalUuid = terminal.Uuid, Pin = "1111" }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Terminal locked until *");
    }

    [Fact]
    public async Task Reason_and_amount_manager_needs_are_reported()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SetupAsync();
        await h.Policies.PutStorePolicyAsync(store.Uuid, Policy(p => p.MaxReturnNoAuth = 20m), Admin);
        var sale = await h.SellAsync(terminal, shift, item, 3);
        var line = sale.Lines[0].Uuid;

        var amount = await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 2, await h.ReasonAsync("DEFECTIVE")), Returner))
            .Should().ThrowAsync<PosRuleException>().WithMessage("Manager authorization required.");
        ((PosManagerRequiredResult)amount.Which.Result).Reasons.Should().Equal("AMOUNT");
        var both = await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 2, await h.ReasonAsync("PRICE_MATCH"), note: "cheaper"), Returner))
            .Should().ThrowAsync<PosRuleException>();
        ((PosManagerRequiredResult)both.Which.Result).Reasons.Should().Equal("REASON", "AMOUNT");
        (await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, await h.ReasonAsync("DEFECTIVE")), Returner)).GrandTotal.Should().Be(11.70m);

        // A cross-store-only manager cannot cover REASON/AMOUNT.
        var token = await h.Orders.AuthorizeReturnAsync(new PosReturnAuthorizeRequest
        {
            TerminalUuid = terminal.Uuid, OriginalOrderUuid = sale.Uuid, Amount = 30m, Pin = "5555", Reasons = ["CROSS_STORE"]
        }, Returner);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 2, await h.ReasonAsync("DEFECTIVE"), token: token.AuthorizationToken), Returner))
            .Should().ThrowAsync<PosRuleException>().WithMessage("This manager cannot authorize this return.");
    }

    // ── refund methods ──

    [Fact]
    public async Task Refund_method_policy_rules()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SetupAsync();
        var reason = await h.ReasonAsync("DEFECTIVE");
        var sale = await h.SellAsync(terminal, shift, item, 3, payments: [Pay(PosPaymentMethod.Cash, 5m), Pay(PosPaymentMethod.Card, 30.10m)]);
        var line = sale.Lines[0].Uuid;
        PosOrderService.OriginalRefundMethod(await h.Db.Orders.Include(o => o.Payments).FirstAsync(o => o.Uuid == sale.Uuid))
            .Should().Be(PosPaymentMethod.Card);

        await h.Policies.PutStorePolicyAsync(store.Uuid, Policy(p => p.RefundToOriginalMethod = true), Admin);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason, PosPaymentMethod.Cash), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Refund method not allowed by the store's return policy.");
        (await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason, PosPaymentMethod.Card), Returner)).Payments.Single().Method.Should().Be("CARD");

        await h.Policies.PutStorePolicyAsync(store.Uuid, Policy(p => { p.AllowCashRefund = false; p.AllowCreditNoteRefund = false; }), Admin);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason, PosPaymentMethod.Cash), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Refund method not allowed by the store's return policy.");
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason, PosPaymentMethod.StoreCredit), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Refund method not allowed by the store's return policy.");

        await h.Policies.PutStorePolicyAsync(store.Uuid, Policy(), Admin);
        h.Gate.Off.Add(PosFeatureCodes.CreditNotes);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, Return(line, 1, reason, PosPaymentMethod.StoreCredit), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Refund method not allowed by the store's return policy.");
    }

    // ── cross-store (D-7) ──

    [Fact]
    public async Task Cross_store_return_uses_the_original_window_and_the_processing_store_stock_and_shift()
    {
        var h = new PosHarness();
        var (s1, t1, shift1, item) = await h.SetupAsync();
        var s2 = await h.StoreAsync("S2");
        var t2 = await h.TerminalAsync(s2.Uuid, "T2");
        await h.StaffAsync(s2.Uuid, Supervisor, PosRole.Supervisor);
        var shift2 = await h.Shifts.OpenAsync(t2.Uuid, new PosOpenShiftRequest { OpeningFloat = 50m }, Caller(Supervisor, PosPermissions.ShiftOpen));
        await h.Policies.PutStorePolicyAsync(s2.Uuid, Policy(p => p.ReturnWindowDays = 60), Admin);
        var sale = await h.SellAsync(t1, shift1, item, 2);
        var line = sale.Lines[0].Uuid;
        var reason = await h.ReasonAsync("DEFECTIVE");
        PosReturnRequest At2(decimal qty = 1) { var r = Return(line, qty, reason); r.TerminalUuid = t2.Uuid; return r; }

        // Lookup from S2: not the same store; crossStoreAllowed from S2's policy; window from S1's (30).
        var lookup = await h.Orders.LookupAsync(sale.OrderNumber, s2.Uuid);
        (lookup.Order!.IsSameStore, lookup.Policy!.CrossStoreAllowed, lookup.Policy.ReturnWindowDays, lookup.Policy.Source)
            .Should().Be((false, true, 30, "STORE"));

        // Feature, permission, offline, acceptance.
        h.Gate.Off.Add(PosFeatureCodes.CrossStoreReturns);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, At2(), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Cross-store returns are not enabled*");
        h.Gate.Off.Clear();
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, At2(), Caller(Supervisor, PosPermissions.OrderReturn)))
            .Should().ThrowAsync<ForbiddenException>();
        var offline = new PosReturnSyncRequest
        {
            Kind = "RETURN", OriginalOrderUuid = sale.Uuid, IsOffline = true, LocalId = "x-off", TerminalUuid = t2.Uuid, ReasonUuid = reason,
            RefundMethod = "CASH", Lines = [new PosReturnLineRequest { OriginalLineUuid = line, Quantity = 1 }]
        };
        await FluentActions.Awaiting(async () => await h.Orders.SyncReturnAsync(offline, Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Cross-store returns require internet connectivity.");
        await h.Policies.PutStorePolicyAsync(s2.Uuid, Policy(p => { p.ReturnWindowDays = 60; p.AllowCrossStoreReturn = false; }), Admin);
        await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, At2(), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This store does not accept returns from other locations.");
        await h.Policies.PutStorePolicyAsync(s2.Uuid, Policy(p => { p.ReturnWindowDays = 60; p.CrossStoreRequiresManager = true; }), Admin);

        // Day 40: inside S2's 60 days, outside S1's 30 — the original store's window applies (+ CROSS_STORE).
        h.Clock.Advance(TimeSpan.FromDays(40));
        var need = await FluentActions.Awaiting(async () => await h.Orders.ReturnAsync(sale.Uuid, At2(), Returner)).Should().ThrowAsync<PosRuleException>();
        ((PosManagerRequiredResult)need.Which.Result).Reasons.Should().Equal("WINDOW", "CROSS_STORE");
        h.Clock.Advance(TimeSpan.FromDays(-40));

        // Same day: only CROSS_STORE — a manager with POS_RETURN_CROSS_STORE suffices (S2 staff).
        h.Pins.AddUser(42, "Xena", PosPermissions.OrderCreate, PosPermissions.ReturnCrossStore);
        await h.StaffAsync(s2.Uuid, 42, PosRole.Supervisor, "6666");
        var token = await h.Orders.AuthorizeReturnAsync(new PosReturnAuthorizeRequest
        {
            TerminalUuid = t2.Uuid, OriginalOrderUuid = sale.Uuid, Amount = 11.70m, Pin = "6666", Reasons = ["CROSS_STORE"]
        }, Returner);
        var req = At2();
        req.AuthorizationToken = token.AuthorizationToken;
        var ret = await h.Orders.ReturnAsync(sale.Uuid, req, Returner);

        (ret.IsCrossStore, ret.OriginalStoreUuid, ret.StoreUuid, ret.ShiftUuid).Should().Be((true, s1.Uuid, s2.Uuid, shift2.Uuid));
        ret.Flags.Should().Contain("CROSS_STORE");
        h.Stock.Posted.Single(p => p.SourceUuid == ret.Uuid).WarehouseUuid.Should().Be(s2.WarehouseUuid);
        h.Finance.Returns[ret.Uuid].OriginalSalesInvoiceUuid.Should().Be(sale.SalesInvoiceUuid!.Value); // credit on the original invoice
        (await h.Shifts.GetAsync(shift2.Uuid, AdminCaller)).TotalCashRefunds.Should().Be(11.70m);
        (await h.Shifts.GetAsync(shift1.Uuid, AdminCaller)).TotalCashRefunds.Should().Be(0m);
        h.Hub.StoreEvents.Should().Contain(e => e.Event == PosHubEvents.ReturnProcessed && e.Store == s2.Uuid);

        // Weekly report → users holding POS_RETURN_REPORTS only (store staff + org search), never a broadcast.
        h.Pins.AddUser(50, "Rita", PosPermissions.ReturnReports);
        h.Clock.Advance(TimeSpan.FromDays(1));
        var report = new CrossStoreReconciliationReport(h.NewDb(new OrderSyncTests.HangfireLikeTenant()), h.Pins, h.Notifications, h.Gate, h.Clock);
        (await report.RunAsync()).Should().Be(2); // Admin (all permissions) and Rita
        h.Notifications.Sent.Where(n => n.Type == CrossStoreReconciliationReport.NotificationType).Select(n => n.UserId).Should().BeEquivalentTo([Admin, 50]);
        h.Notifications.Sent.Last().Message.Should().Contain("sold at S1 → returned at S2: 1 return(s), 11.70");
    }

    // ── offline sync (D-1, D-8) ──

    [Fact]
    public async Task Offline_return_sync_flags_instead_of_refusing_and_is_idempotent()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SetupAsync();
        await h.Policies.PutStorePolicyAsync(store.Uuid, Policy(p => p.AllowCashRefund = false), Admin);
        var sale = await h.SellAsync(terminal, shift, item, 4);
        var line = sale.Lines[0].Uuid;
        var reason = await h.ReasonAsync("DEFECTIVE");
        h.Clock.Advance(TimeSpan.FromDays(31));
        PosReturnSyncRequest Sync(string id, int? manager = null) => new()
        {
            Kind = "RETURN", OriginalOrderUuid = sale.Uuid, IsOffline = true, LocalId = id, TerminalUuid = terminal.Uuid, ReasonUuid = reason,
            RefundMethod = "CASH", AuthorizedByUserId = manager, CreatedAtLocal = new DateTimeOffset(h.Clock.UtcNow.AddHours(-1)),
            Lines = [new PosReturnLineRequest { OriginalLineUuid = line, Quantity = 1 }]
        };

        // Window expired + cash not allowed, no manager: accepted, POLICY_OVERRIDDEN_OFFLINE.
        var first = await h.Orders.SyncReturnAsync(Sync("off-1"), Returner);
        first.Success.Should().BeTrue();
        first.Flags.Should().Contain(["OFFLINE", "POLICY_OVERRIDDEN_OFFLINE"]);
        first.Warnings.Should().Contain(["Return window expired (30 days).", "Refund method not allowed by the store's return policy."]);
        (await h.Orders.SyncReturnAsync(Sync("off-1"), Returner)).OrderUuid.Should().Be(first.OrderUuid); // replay

        // A qualifying manager verified on the device → AUTHORIZED_OFFLINE; one who does not qualify → + AUTHORIZATION_INVALID.
        var ok = await h.Orders.SyncReturnAsync(Sync("off-2", Manager), Returner);
        ok.Flags.Should().Contain("AUTHORIZED_OFFLINE").And.NotContain("AUTHORIZATION_INVALID");
        (await h.Orders.GetAsync(ok.OrderUuid!.Value, AdminCaller)).AuthorizedByUserId.Should().Be(Manager);
        var bad = await h.Orders.SyncReturnAsync(Sync("off-3", Supervisor), Returner);
        bad.Flags.Should().Contain(["AUTHORIZED_OFFLINE", "AUTHORIZATION_INVALID"]);

        // Online through the same endpoint: refused as at the till.
        var online = Sync("on-1");
        online.IsOffline = false;
        online.Lines[0].Quantity = 0.5m;
        await FluentActions.Awaiting(async () => await h.Orders.SyncReturnAsync(online, Returner)).Should().ThrowAsync<PosRuleException>();
        await FluentActions.Awaiting(async () => await h.Orders.SyncReturnAsync(Sync("off-1"), Returner)).Should().NotThrowAsync();
        var conflict = Sync("off-1");
        conflict.Kind = "EXCHANGE";
        await FluentActions.Awaiting(async () => await h.Orders.SyncReturnAsync(conflict, Returner)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Lookup_unknown_receipt_answers_found_false()
    {
        var h = new PosHarness();
        await h.SetupAsync();
        (await h.Orders.LookupAsync("POS-NOPE", null)).Found.Should().BeFalse();
        await FluentActions.Awaiting(async () => await h.Orders.LookupAsync(" ", null)).Should().ThrowAsync<BadRequestException>();
    }
}
