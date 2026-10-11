using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Pos.Tests.PosHarness;

namespace SMS.Modules.Pos.Tests;

/// <summary>A38 API-CONTRACT §5 — order sync rules, flags, postings, credit, retry job.</summary>
public class OrderSyncTests
{
    private static async Task<PosOrder> OrderAsync(PosHarness h, Guid uuid) =>
        await h.NewDb(h.Tenant).Orders.Include(o => o.Lines).Include(o => o.Payments).SingleAsync(o => o.Uuid == uuid);

    [Fact]
    public async Task Online_sale_is_saved_numbered_posted_and_totalled()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SellingSetupAsync();

        var r = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 2)], payments: Pay(PosPaymentMethod.Cash, 30m, 6.60m)), CashierCaller);

        r.Success.Should().BeTrue();
        r.OrderNumber.Should().Be("POS-S1-20261010-0001");
        r.Flags.Should().BeEmpty();
        var order = await OrderAsync(h, r.OrderUuid!.Value);
        (order.Subtotal, order.TaxTotal, order.GrandTotal, order.PaymentStatus).Should().Be((20m, 3.40m, 23.40m, PosPaymentStatus.Paid));
        (order.PaidAmount, order.ChangeAmount, order.IsWalkIn, order.CustomerCode).Should().Be((30m, 6.60m, true, "C-WALKIN"));
        order.StockPosted.Should().BeTrue();
        order.FinancePosted.Should().BeTrue();
        order.SalesInvoiceNumber.Should().NotBeNull();
        order.Payments.Single().CustomerPaymentUuid.Should().NotBeNull();

        var posting = h.Stock.Posted.Single();
        (posting.TransactionType, posting.IsOutbound, posting.WarehouseUuid, posting.SourceUuid).Should()
            .Be((PosStockTransactionType.Sale, true, store.WarehouseUuid, order.Uuid));
        var sale = h.Finance.Sales.Single().Value;
        sale.Tenders.Single().Amount.Should().Be(23.40m); // net of change
        sale.Lines.Single().Should().Match<CounterSaleLine>(l => l.NetAmount == 20m && l.TaxAmount == 3.40m && l.TaxPercent == 17m && l.TaxCode == "VAT17");
        sale.CustomerPartnerUuid.Should().Be(h.Customers.WalkIn.Uuid);

        var s = await h.Shifts.GetAsync(shift.Uuid, AdminCaller);
        (s.TotalCashSales, s.OrderCount, s.ExpectedCashClosing).Should().Be((23.40m, 1, 123.40m));
    }

    [Fact]
    public async Task Sync_is_idempotent_on_local_id()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        var req = Sale(terminal, shift, [(item, 1)], 11.70m, localId: "dev-1");

        var first = await h.Orders.SyncAsync(req, CashierCaller);
        var again = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 5)], 58.50m, localId: "dev-1"), CashierCaller);

        again.OrderUuid.Should().Be(first.OrderUuid!.Value);
        again.OrderNumber.Should().Be(first.OrderNumber);
        (await h.Db.Orders.CountAsync()).Should().Be(1);
        h.Stock.Posted.Should().HaveCount(1);
        h.Finance.Sales.Should().HaveCount(1);
    }

    [Fact]
    public async Task Batch_runs_in_created_at_local_order_and_one_failure_does_not_stop_the_rest()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        PosSyncOrderRequest At(int minute, string id, PosCatalogVariant v)
        {
            var r = Sale(terminal, shift, [(v, 1)], v.Price * 1.17m, localId: id, offline: true);
            r.CreatedAtLocal = new DateTimeOffset(2026, 10, 10, 9, minute, 0, TimeSpan.Zero);
            return r;
        }
        var missing = new PosCatalogVariant(Guid.NewGuid(), Guid.NewGuid(), "X", "Gone", null, null, null, null, null, 1m, null, true, true, true, DateTime.UtcNow);

        var results = await h.Orders.SyncBatchAsync(new PosSyncBatchRequest
        {
            Orders = [At(30, "late", item), At(10, "early", item), At(20, "broken", missing)]
        }, CashierCaller);

        results.Select(r => r.LocalId).Should().Equal("late", "early", "broken"); // answered in request order…
        results.Select(r => r.Success).Should().Equal(true, true, false);
        results[2].Error.Should().Contain("was not found");
        results[1].OrderNumber.Should().Be("POS-S1-20261010-0001");           // …processed in createdAtLocal order
        results[0].OrderNumber.Should().Be("POS-S1-20261010-0002");
        results[0].Flags.Should().Contain(PosOrderFlag.Offline);
    }

    [Fact]
    public async Task Shift_state_rules_including_late_offline_orders()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        await h.Shifts.StartCloseAsync(shift.Uuid, AdminCaller);

        var after = Sale(terminal, shift, [(item, 1)], 11.70m);
        after.CreatedAtLocal = new DateTimeOffset(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);
        await FluentActions.Awaiting(() => h.Orders.SyncAsync(after, CashierCaller)).Should().ThrowAsync<BadRequestException>().WithMessage("*closing*");
        var before = Sale(terminal, shift, [(item, 1)], 11.70m);
        before.CreatedAtLocal = new DateTimeOffset(2026, 10, 10, 8, 59, 0, TimeSpan.Zero);
        (await h.Orders.SyncAsync(before, CashierCaller)).Success.Should().BeTrue();

        await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 111.70m }, AdminCaller);
        await FluentActions.Awaiting(() => h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m), CashierCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The shift is not open.");

        var late = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m, offline: true), CashierCaller);
        late.Success.Should().BeTrue();
        late.Warnings.Should().ContainSingle(w => w.Contains("late"));
        var closed = await h.Shifts.GetAsync(shift.Uuid, AdminCaller);
        closed.Status.Should().Be(PosShiftStatus.Reconciled);  // status unchanged…
        closed.TotalCashSales.Should().Be(23.40m);             // …totals and variance recomputed
        closed.CashVariance.Should().Be(111.70m - 123.40m);
        closed.ReconcileNotes.Should().Contain(late.OrderNumber);
    }

    [Fact]
    public async Task Variant_checks_refuse_online_and_warn_offline()
    {
        var h = new PosHarness();
        var (_, terminal, shift, _) = await h.SellingSetupAsync();
        var inactive = h.Catalog.Add("I", 10m, active: false);
        var notPos = h.Catalog.Add("N", 10m, pos: false);
        var hidden = h.Catalog.Add("H", 10m);
        h.Db.ProductSettings.Add(new PosProductSettings { OrganizationId = h.Org, ProductUuid = hidden.ProductUuid, IsPosActive = false });
        await h.Db.SaveChangesAsync();

        foreach (var v in new[] { inactive, notPos, hidden })
        {
            await FluentActions.Awaiting(() => h.Orders.SyncAsync(Sale(terminal, shift, [(v, 1)], 11.70m), CashierCaller))
                .Should().ThrowAsync<BadRequestException>().WithMessage("*not available for sale on POS*");
            var offline = await h.Orders.SyncAsync(Sale(terminal, shift, [(v, 1)], 11.70m, offline: true), CashierCaller);
            offline.Success.Should().BeTrue();
            offline.Warnings.Should().ContainSingle(w => w.Contains("not available"));
        }
    }

    [Fact]
    public async Task Offline_order_names_its_cashier_and_joins_or_creates_the_offline_session()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SellingSetupAsync();
        await h.StaffAsync(store.Uuid, Cashier2);
        PosSyncOrderRequest Offline(int? cashier, string? sessionLocalId)
        {
            var r = Sale(terminal, shift, [(item, 1)], 11.70m, offline: true);
            r.CashierUserId = cashier;
            r.SessionUuid = Guid.NewGuid(); // a session the server never saw
            r.SessionLocalId = sessionLocalId;
            return r;
        }

        var first = await h.Orders.SyncAsync(Offline(Cashier2, "off-1"), CashierCaller); // Cathy's token pushes Carl's sale
        var order = await OrderAsync(h, first.OrderUuid!.Value);
        (order.CashierUserId, order.SyncedByUserId).Should().Be((Cashier2, Cashier));
        var session = await h.Db.CashierSessions.SingleAsync();
        (session.UserId, session.LocalId, session.IsActive, order.SessionId).Should().Be((Cashier2, "off-1", false, session.Id));

        var second = await h.Orders.SyncAsync(Offline(Cashier2, "off-1"), CashierCaller);
        (await OrderAsync(h, second.OrderUuid!.Value)).SessionId.Should().Be(session.Id);
        (await h.Db.CashierSessions.CountAsync()).Should().Be(1);
        var detail = await h.Shifts.GetAsync(shift.Uuid, AdminCaller);
        detail.Sessions.Single().Should().Match<PosCashierSessionModel>(s => s.CashSales == 23.40m && s.OrderCount == 2 && s.UserName == "Carl");

        // Not a seller of this store: the pusher is the cashier, with a warning.
        var refused = await h.Orders.SyncAsync(Offline(NoPos, null), CashierCaller);
        refused.Warnings.Should().Contain(w => w.Contains("cannot sell in this store"));
        (await OrderAsync(h, refused.OrderUuid!.Value)).CashierUserId.Should().Be(Cashier);
        (await h.Orders.GetAsync(refused.OrderUuid!.Value, AdminCaller)).SyncedByName.Should().Be("Cathy");
    }

    [Fact]
    public async Task Price_change_flags_but_the_cashiers_price_stands()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        var req = Sale(terminal, shift, [(item, 1)], 10.53m);
        req.Lines[0].UnitPrice = 9.00m;

        var r = await h.Orders.SyncAsync(req, CashierCaller);
        r.Flags.Should().Equal(PosOrderFlag.PriceChanged);
        var line = (await OrderAsync(h, r.OrderUuid!.Value)).Lines.Single();
        (line.UnitPrice, line.CatalogPrice, line.LineTotal).Should().Be((9.00m, 10.00m, 10.53m));
    }

    [Fact]
    public async Task Discounts_need_the_session_users_permission_online_and_are_flagged_offline()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        PosSyncOrderRequest Discounted(bool offline)
        {
            var r = Sale(terminal, shift, [(item, 1)], 10.53m, offline: offline);
            r.OrderDiscountType = "PERCENT";
            r.OrderDiscountValue = 10m;
            return r;
        }

        await FluentActions.Awaiting(() => h.Orders.SyncAsync(Discounted(false), CashierCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("You do not have permission to give discounts.");
        (await h.Orders.SyncAsync(Discounted(true), CashierCaller)).Flags.Should().Contain(PosOrderFlag.DiscountUnauthorized);
        (await h.Orders.SyncAsync(Discounted(false), DiscountCaller)).Flags.Should().BeEmpty();

        // The session's user counts, not whoever pushes: Cathy's session, pushed by an admin.
        var session = await h.Sessions.StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid }, CashierCaller);
        var viaAdmin = Discounted(false);
        viaAdmin.SessionUuid = session.Uuid;
        await FluentActions.Awaiting(() => h.Orders.SyncAsync(viaAdmin, AdminCaller)).Should().ThrowAsync<BadRequestException>();
        h.Pins.Users[Cashier] = h.Pins.Users[Cashier] with { Permissions = [PosPermissions.OrderCreate, PosPermissions.OrderDiscount] };
        var viaAdmin2 = Discounted(false);
        viaAdmin2.SessionUuid = session.Uuid;
        var ok = await h.Orders.SyncAsync(viaAdmin2, AdminCaller);
        ok.Success.Should().BeTrue();
        (await OrderAsync(h, ok.OrderUuid!.Value)).CashierUserId.Should().Be(Cashier);
    }

    [Fact]
    public async Task Server_totals_win_with_a_warning_and_tender_rules_apply()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        var req = Sale(terminal, shift, [(item, 1)], 11.70m);
        req.ClientTotals = new PosClientTotals { Subtotal = 10m, TaxTotal = 1.50m, GrandTotal = 11.50m };
        var r = await h.Orders.SyncAsync(req, CashierCaller);
        r.Warnings.Should().ContainSingle(w => w.Contains("recalculated") && w.Contains("11.70"));

        await FluentActions.Awaiting(() => h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], payments: Pay(PosPaymentMethod.Card, 12m)), CashierCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*exceeds the remaining balance*");
        await FluentActions.Awaiting(() => h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], payments: Pay(PosPaymentMethod.Card, 12m, 0.30m)), CashierCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Only cash payments can give change.");
        await FluentActions.Awaiting(() => h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.00m), CashierCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Payments (11.00) do not match the order total (11.70).");

        // Offline: the money was taken — accepted with a warning; Finance gets the shortfall on account.
        var offline = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.00m, offline: true), CashierCaller);
        offline.Warnings.Should().Contain(w => w.Contains("do not match"));
        var tenders = h.Finance.Sales[offline.OrderUuid!.Value].Tenders;
        tenders.Should().HaveCount(2);
        tenders.Should().Contain(t => t.Method == PosPaymentMethod.OnAccount && t.Amount == 0.70m);
    }

    [Fact]
    public async Task Credit_sales_online()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync(); // 1 × 10.00 → 11.70
        var acme = h.Customers.Add("ACME", creditLimit: 100m);
        PosSyncOrderRequest Credit(Guid? customer, decimal qty = 1) =>
            Sale(terminal, shift, [(item, qty)], customer: customer, payments: Pay(PosPaymentMethod.OnAccount, 11.70m * qty));

        await FluentActions.Awaiting(() => h.Orders.SyncAsync(Credit(null), CashierCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Walk-In customers cannot use credit.");
        await FluentActions.Awaiting(() => h.Orders.SyncAsync(Credit(acme.Uuid), CashierCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*not allowed to buy on credit*");

        await h.Settings.PutCustomerAsync(acme.Uuid, new PosCustomerSettingsModel { AllowPosCredit = true, MaxSingleCreditOrder = 50m }, Admin);
        await FluentActions.Awaiting(() => h.Orders.SyncAsync(Credit(acme.Uuid, 5), CashierCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Credit for one order cannot exceed 50.00.");
        h.Balances.Balances[acme.Uuid] = 90m;
        await FluentActions.Awaiting(() => h.Orders.SyncAsync(Credit(acme.Uuid), CashierCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Credit limit exceeded. Limit: 100.00. Balance: 90.00. This order: 11.70.");

        h.Balances.Balances[acme.Uuid] = 80m;
        var ok = await h.Orders.SyncAsync(Credit(acme.Uuid), CashierCaller);
        var order = await OrderAsync(h, ok.OrderUuid!.Value);
        (order.PaymentStatus, order.BalanceDue).Should().Be((PosPaymentStatus.CreditSale, 11.70m));
        h.Finance.Sales[order.Uuid].Tenders.Single().Method.Should().Be(PosPaymentMethod.OnAccount);
        h.Balances.Balances[acme.Uuid].Should().Be(91.70m);
        h.Hub.Sent.Should().Contain(e => e.Event == PosHubEvents.CustomerBalanceChanged && e.Target == acme.Uuid);
        (await h.Shifts.GetAsync(shift.Uuid, AdminCaller)).TotalCreditSales.Should().Be(11.70m);

        var credit = await h.Orders.GetCreditAsync(acme.Uuid);
        (credit.Balance, credit.CreditLimit, credit.Available, credit.AllowPosCredit, credit.IsWalkIn).Should().Be((91.70m, 100m, 8.30m, true, false));

        h.Gate.Off.Add(PosFeatureCodes.CreditSales);
        await FluentActions.Awaiting(() => h.Orders.SyncAsync(Credit(acme.Uuid), CashierCaller))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Credit sales are not enabled.");
    }

    [Fact]
    public async Task Partial_credit_sale()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        var acme = h.Customers.Add("ACME", creditLimit: 100m);
        await h.Settings.PutCustomerAsync(acme.Uuid, new PosCustomerSettingsModel { AllowPosCredit = true }, Admin);
        var r = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], customer: acme.Uuid,
            payments: [Pay(PosPaymentMethod.Cash, 5m), Pay(PosPaymentMethod.OnAccount, 6.70m)]), CashierCaller);
        (await OrderAsync(h, r.OrderUuid!.Value)).PaymentStatus.Should().Be(PosPaymentStatus.PartialCredit);
    }

    [Fact]
    public async Task Offline_credit_over_the_tolerated_limit_is_accepted_flagged_and_supervisors_told()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SellingSetupAsync();
        var row = await h.Db.Stores.SingleAsync();
        row.OfflineCreditTolerancePct = 10m;
        await h.Db.SaveChangesAsync();
        var acme = h.Customers.Add("ACME", creditLimit: 100m);
        await h.Settings.PutCustomerAsync(acme.Uuid, new PosCustomerSettingsModel { AllowPosCredit = true }, Admin);
        h.Balances.Balances[acme.Uuid] = 95m;

        // 95 + 11.70 = 106.70 ≤ 110 (100 + 10 %): fine offline, only OFFLINE_CREDIT.
        var within = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], customer: acme.Uuid, offline: true,
            payments: Pay(PosPaymentMethod.OnAccount, 11.70m)), CashierCaller);
        within.Flags.Should().BeEquivalentTo([PosOrderFlag.Offline, PosOrderFlag.OfflineCredit]);
        h.Notifications.Sent.Should().BeEmpty();

        // Balance now 106.70: another 11.70 is over 110 → accepted + CREDIT_OVER_LIMIT + supervisors (only) told.
        var over = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], customer: acme.Uuid, offline: true,
            payments: Pay(PosPaymentMethod.OnAccount, 11.70m)), CashierCaller);
        over.Success.Should().BeTrue();
        over.Flags.Should().Contain([PosOrderFlag.OfflineCredit, PosOrderFlag.CreditOverLimit]);
        h.Notifications.Sent.Should().ContainSingle().Which.Should().Match<NotificationRequest>(n =>
            n.UserId == Supervisor && n.Type == PosOrderService.CreditOverLimitNotification && n.EntityUuid == over.OrderUuid.ToString());

        // Override: the customer's own offline limit wins over the credit limit.
        await h.Settings.PutCustomerAsync(acme.Uuid, new PosCustomerSettingsModel { AllowPosCredit = true, OfflineCreditLimitOverride = 1000m }, Admin);
        (await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], customer: acme.Uuid, offline: true,
            payments: Pay(PosPaymentMethod.OnAccount, 11.70m)), CashierCaller)).Flags.Should().NotContain(PosOrderFlag.CreditOverLimit);
    }

    [Fact]
    public async Task Negative_stock_is_flagged_and_the_supervisors_notified()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SellingSetupAsync();
        await h.StaffAsync(store.Uuid, Cashier2, PosRole.Supervisor);
        h.Stock.Negatives.Add(item.VariantUuid);

        var r = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m), CashierCaller);

        r.Flags.Should().Contain(PosOrderFlag.NegativeStock);
        h.Notifications.Sent.Where(n => n.Type == PosPostingService.NegativeStockNotification).Select(n => n.UserId)
            .Should().BeEquivalentTo([Supervisor, Cashier2]); // the store's supervisors, not the cashier
    }

    [Fact]
    public async Task Posting_failure_keeps_the_order_and_the_retry_job_finishes_it_inside_the_tenant_scope()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        h.Stock.Fail = true;
        h.Finance.Fail = true;

        var r = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m), CashierCaller);
        r.Success.Should().BeTrue();
        var order = await OrderAsync(h, r.OrderUuid!.Value);
        (order.StockPosted, order.FinancePosted, order.PostingAttempts).Should().Be((false, false, 1));
        order.PostingError.Should().Contain("Inventory is down").And.Contain("Finance is down");
        (await h.Shifts.GetAsync(shift.Uuid, AdminCaller)).TotalCashSales.Should().Be(11.70m); // the sale counts anyway

        h.Stock.Fail = false;
        h.Finance.Fail = false;
        var db = h.NewDb(new HangfireLikeTenant());
        var job = new PosPostingRetryJob(db, new PosPostingService(db, h.Stock, h.Finance, h.Notifications, h.Clock), new FakeGate(), h.Clock);
        (await job.RunAsync()).Should().Be(0); // backoff: not yet
        h.Clock.Advance(TimeSpan.FromMinutes(15));
        (await job.RunAsync()).Should().Be(1);

        order = await OrderAsync(h, order.Uuid);
        (order.StockPosted, order.FinancePosted, order.PostingError, order.PostingAttempts).Should().Be((true, true, null, 2));
        h.Finance.ScopesSeen.Should().EndWith(h.Org); // posted inside HangfireTenantScope = the order's organization
        HangfireTenantScope.OrganizationId.Should().BeNull();
        (await job.RunAsync()).Should().Be(0);
    }

    [Fact]
    public void Backoff_doubles_and_caps_at_a_day()
    {
        PosPostingRetryJob.Backoff(1).Should().Be(TimeSpan.FromMinutes(15));
        PosPostingRetryJob.Backoff(3).Should().Be(TimeSpan.FromMinutes(60));
        PosPostingRetryJob.Backoff(30).Should().Be(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task Retry_job_skips_organizations_without_pos()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        h.Stock.Fail = true;
        await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m), CashierCaller);
        h.Stock.Fail = false;
        h.Clock.Advance(TimeSpan.FromHours(1));
        var gate = new FakeGate();
        gate.Off.Add(ModuleCodes.Pos);
        var db = h.NewDb(new HangfireLikeTenant());
        (await new PosPostingRetryJob(db, new PosPostingService(db, h.Stock, h.Finance), gate, h.Clock).RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Negative_stock_report_notifies_each_store_s_supervisors_once()
    {
        var h = new PosHarness();
        var (store, _, _, item) = await h.SellingSetupAsync();
        var quiet = await h.StoreAsync("S2"); // no supervisors, no negatives
        h.Stock.Levels[item.VariantUuid] = -3m;
        var db = h.NewDb(new HangfireLikeTenant());

        (await new NegativeStockReportJob(db, h.Stock, h.Catalog, h.Notifications, new FakeGate()).RunAsync()).Should().Be(1);
        h.Notifications.Sent.Should().ContainSingle().Which.Should().Match<NotificationRequest>(n =>
            n.UserId == Supervisor && n.Type == NegativeStockReportJob.NotificationType && n.Message.Contains("A (-3)") && n.EntityUuid == store.Uuid.ToString());
        _ = quiet;
    }

    [Fact]
    public async Task Reads_list_and_by_local_id()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        var a = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m, localId: "a"), CashierCaller);
        h.Clock.Advance(TimeSpan.FromMinutes(1));
        await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 2)], 23.40m, localId: "b", offline: true), CashierCaller);

        var byLocal = await h.Orders.GetByLocalIdAsync("a", CashierCaller);
        byLocal.Uuid.Should().Be(a.OrderUuid!.Value);
        byLocal.Lines.Single().ReturnableQuantity.Should().Be(1);
        (byLocal.ShiftNumber, byLocal.CashierName, byLocal.TerminalName).Should().Be((shift.ShiftNumber, "Cathy", "Till T1"));

        await FluentActions.Awaiting(() => h.Orders.GetAsync(a.OrderUuid!.Value, Caller(Cashier2, PosPermissions.OrderCreate)))
            .Should().ThrowAsync<ForbiddenException>(); // not staff, not the cashier
        (await h.Orders.GetAsync(a.OrderUuid!.Value, Caller(Cashier2, PosPermissions.OrderView))).OrderNumber.Should().Be(a.OrderNumber);

        var page = await h.Orders.ListAsync(new PosOrderListFilter { ShiftUuid = shift.Uuid, PageSize = 1 });
        (page.TotalRecords, page.Data.Single().OrderNumber).Should().Be((2, "POS-S1-20261010-0002")); // newest first
        (await h.Orders.ListAsync(new PosOrderListFilter { Search = a.OrderNumber })).Data.Should().ContainSingle();
        (await h.Orders.ListAsync(new PosOrderListFilter { Status = "VOIDED" })).TotalRecords.Should().Be(0);
    }

    internal sealed class HangfireLikeTenant : ITenantContext
    {
        public Guid OrganizationId => HangfireTenantScope.OrganizationId ?? Guid.Empty;
        public bool IsSuperAdmin => HangfireTenantScope.OrganizationId is null;
    }

    /// <summary>A number source that lets a competing push of the same order win between the localId check and the save.</summary>
    private sealed class RacingNumbers(IPosNumberSequence inner, Func<Task> competitor) : IPosNumberSequence
    {
        private bool _raced;
        public Task<int> NextValueAsync(PosStore store, string prefix, DateTime businessDate, CancellationToken ct = default) =>
            inner.NextValueAsync(store, prefix, businessDate, ct);
        public async Task<string> NextNumberAsync(PosStore store, string prefix, DateTime utcNow, CancellationToken ct = default)
        {
            if (!_raced) { _raced = true; await competitor(); }
            return await inner.NextNumberAsync(store, prefix, utcNow, ct);
        }
    }

    /// <summary>A catalog that lets a competing push of the same order win right after the localId check (before the payment-id check).</summary>
    private sealed class RacingCatalog(IPosCatalogSource inner, Func<Task> competitor) : IPosCatalogSource
    {
        private bool _raced;
        public Task<IReadOnlyList<PosCatalogVariant>> GetChangedAsync(Guid organizationId, DateTime? since, int limit, CancellationToken ct = default) =>
            inner.GetChangedAsync(organizationId, since, limit, ct);
        public async Task<IReadOnlyList<PosCatalogVariant>> GetByVariantUuidsAsync(Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
        {
            if (!_raced) { _raced = true; await competitor(); }
            return await inner.GetByVariantUuidsAsync(organizationId, variantUuids, ct);
        }
        public Task<IReadOnlyList<PosCatalogVariant>> GetByProductUuidsAsync(Guid organizationId, IReadOnlyCollection<Guid> productUuids, CancellationToken ct = default) =>
            inner.GetByProductUuidsAsync(organizationId, productUuids, ct);
        public Task<IReadOnlyList<(int Id, string Name)>> GetCategoriesAsync(Guid organizationId, CancellationToken ct = default) =>
            inner.GetCategoriesAsync(organizationId, ct);
    }

    /// <summary>
    /// SQL Server (LocalDB): a duplicate push that loses on the unique (org, LocalId) index answers with the winner's order,
    /// and concurrent session starts with one localId make one session. Skipped quietly without LocalDB.
    /// </summary>
    [Fact]
    public async Task Sql_server_concurrent_duplicate_order_and_session_push()
    {
        var name = $"PosTests_{Guid.NewGuid():N}";
        var conn = $@"Server=(localdb)\mssqllocaldb;Database={name};Trusted_Connection=True;TrustServerCertificate=True";
        var h = new PosHarness();
        PosDbContext Db() => new(new DbContextOptionsBuilder<PosDbContext>().UseSqlServer(conn).Options, h.Tenant);

        await using var setup = Db();
        try { await setup.Database.MigrateAsync(); }
        catch (SqlException) { return; }
        try
        {
            var read = new PosReadModel(setup, h.Tenant, h.Pins, h.TaxCodes, clock: h.Clock);
            var stores = new PosStoreService(setup, h.Tenant, read, h.Hub, h.Pins, h.Warehouses, h.TaxCodes, h.Gate);
            var store = await stores.CreateStoreAsync(h.StoreRequest("S1"), Admin);
            var terminal = await stores.CreateTerminalAsync(store.Uuid, new PosTerminalCreateRequest { Code = "T1", Name = "Till" }, Admin);
            await stores.UpsertStaffAsync(store.Uuid, Cashier, new PosStaffUpsertRequest { PosRole = PosRole.Cashier }, Admin);
            var shift = await new PosShiftService(setup, h.Tenant, read, new PosNumberSequenceService(setup), new PosShiftTotals(setup))
                .OpenAsync(terminal.Uuid, new PosOpenShiftRequest(), AdminCaller);
            var item = h.Catalog.Add("A", 10m);

            PosOrderService Service(PosDbContext db, IPosNumberSequence? numbers = null) => new(db, h.Tenant,
                new PosReadModel(db, h.Tenant, h.Pins, h.TaxCodes, clock: h.Clock), numbers ?? new PosNumberSequenceService(db),
                new PosShiftTotals(db), new PosPostingService(db, h.Stock, h.Finance), h.Catalog, h.Customers, h.Balances, h.Pins, h.Gate);

            var req = Sale(terminal, shift, [(item, 1)], 11.70m, localId: "dup");
            PosSyncResultModel? winner = null;
            await using var loserDb = Db();
            await using var winnerDb = Db();
            var loser = Service(loserDb, new RacingNumbers(new PosNumberSequenceService(loserDb),
                async () => winner = await Service(winnerDb).SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m, localId: "dup"), CashierCaller)));

            var answer = await loser.SyncAsync(req, CashierCaller);
            winner.Should().NotBeNull();
            answer.Success.Should().BeTrue();
            answer.OrderUuid.Should().Be(winner!.OrderUuid!.Value);
            await using (var check = Db()) (await check.Orders.CountAsync()).Should().Be(1);

            // A38 QA — the same push (same payment localIds) winning earlier, between the localId check and the payment-id
            // check: the loser answers with the winner's order, not "A payment of this order was already synced…".
            var same = Sale(terminal, shift, [(item, 1)], 11.70m, localId: "dup-2");
            PosSyncResultModel? first = null;
            await using var loserDb2 = Db();
            await using var winnerDb2 = Db();
            PosOrderService Racing(PosDbContext db, IPosCatalogSource catalog) => new(db, h.Tenant,
                new PosReadModel(db, h.Tenant, h.Pins, h.TaxCodes, clock: h.Clock), new PosNumberSequenceService(db),
                new PosShiftTotals(db), new PosPostingService(db, h.Stock, h.Finance), catalog, h.Customers, h.Balances, h.Pins, h.Gate);
            var loser2 = Racing(loserDb2, new RacingCatalog(h.Catalog,
                async () => first = await Service(winnerDb2).SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m, localId: "dup-2",
                    payments: same.Payments.Select(p => new PosSyncPaymentRequest { LocalId = p.LocalId, Method = p.Method, Amount = p.Amount }).ToArray()),
                CashierCaller)));
            var answer2 = await loser2.SyncAsync(same, CashierCaller);
            first.Should().NotBeNull();
            (answer2.Success, answer2.OrderUuid).Should().Be((true, first!.OrderUuid));
            await using (var check = Db()) (await check.Orders.CountAsync()).Should().Be(2);

            var sessions = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                await using var db = Db();
                return await new PosSessionService(db, h.Tenant, new PosReadModel(db, h.Tenant, h.Pins, clock: h.Clock), h.Pins)
                    .StartAsync(new PosSessionStartRequest { ShiftUuid = shift.Uuid, LocalId = "sess-1" }, CashierCaller);
            }));
            sessions.Select(s => s.Uuid).Distinct().Should().ContainSingle();
            await using (var check = Db()) (await check.CashierSessions.CountAsync()).Should().Be(1);
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }
}
