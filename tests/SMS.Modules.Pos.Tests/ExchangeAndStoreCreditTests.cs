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
using static SMS.Modules.Pos.Tests.A39;
using static SMS.Modules.Pos.Tests.PosHarness;

namespace SMS.Modules.Pos.Tests;

/// <summary>A39 §3–§4 — exchanges (D-6) and store credit (D-2, D-3, D-10, D-11).</summary>
public class ExchangeAndStoreCreditTests
{
    private static PosExchangeRequest Exchange(Guid line, decimal qty, Guid reason, IEnumerable<PosSyncLineRequest> newLines, string? method = null,
        params PosSyncPaymentRequest[] payments) => new()
    {
        LocalId = Guid.NewGuid().ToString("N"), ReasonUuid = reason, RefundMethod = method,
        Lines = [new PosReturnLineRequest { OriginalLineUuid = line, Quantity = qty }], NewLines = newLines.ToList(), Payments = payments.ToList()
    };

    // ── exchanges ──

    [Fact]
    public async Task Exchange_PAY_carries_the_return_value_and_posts_a_credit_note_plus_a_sale()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SetupAsync();
        var bigger = h.Catalog.Add("B", 20m); // 23.40
        var sale = await h.SellAsync(terminal, shift, item, 2);
        var reason = await h.ReasonAsync("WRONG_SIZE");

        var ex = await h.Orders.ExchangeAsync(sale.Uuid, Exchange(sale.Lines[0].Uuid, 1, reason, [Line(bigger, 1)], null,
            Pay(PosPaymentMethod.Cash, 20m, 8.30m)), Returner);

        (ex.OrderType, ex.Settlement, ex.NetAmount, ex.GrandTotal).Should().Be((PosOrderType.Exchange, "PAY", 11.70m, 11.70m));
        ex.OrderNumber.Should().StartWith("EXC-S1-");
        ex.Lines.Select(l => (l.Quantity, l.LineTotal)).Should().Equal((-1m, -11.70m), (1m, 23.40m));
        ex.Lines[0].OriginalLineUuid.Should().Be(sale.Lines[0].Uuid);
        ex.Payments.Select(p => (p.Method, p.Amount, p.IsRefund)).Should().BeEquivalentTo(
            [("EXCHANGE", 11.70m, true), ("EXCHANGE", 11.70m, false), ("CASH", 20m, false)]);

        // Finance: return part credited with EXCHANGE; new part sold for EXCHANGE + cash on a derived source.
        h.Finance.Returns[ex.Uuid].Refunds.Should().ContainSingle(r => r.Method == "EXCHANGE" && r.Amount == 11.70m);
        var newSale = h.Finance.Sales[PosPostingService.ExchangeSaleUuid(ex.Uuid)];
        newSale.Tenders.Select(t => (t.Method, t.Amount)).Should().Equal(("EXCHANGE", 11.70m), ("CASH", 11.70m));
        newSale.Lines.Single().LineTotal.Should().Be(23.40m);
        (ex.FinancePosted, ex.StockPosted, ex.SalesInvoiceUuid is not null, ex.CreditNoteUuid is not null).Should().Be((true, true, true, true));
        h.Stock.Posted.Where(p => p.SourceUuid == ex.Uuid).Select(p => (p.TransactionType, p.Lines.Single().Quantity))
            .Should().Equal(("POS_RETURN", 1m), ("POS_SALE", 1m));

        var s = await h.Shifts.GetAsync(shift.Uuid, AdminCaller);
        (s.TotalCashSales, s.OrderCount, s.RefundCount, s.GrossTotal, s.NetTotal).Should().Be((23.40m + 11.70m, 2, 1, 46.80m, 35.10m));
        var summary = await h.Shifts.GetSummaryAsync(shift.Uuid, AdminCaller);
        summary.ByMethod.Single(m => m.Method == "EXCHANGE").Should().Match<PosMethodTotalModel>(m => m.Sales == 11.70m && m.Refunds == 11.70m && m.Net == 0m);
        (await h.Orders.GetAsync(sale.Uuid, AdminCaller)).Status.Should().Be(PosOrderStatus.PartiallyReturned);

        // Retrying the posting is a no-op (each part idempotent).
        var tracked = await h.Db.Orders.Include(o => o.Lines).Include(o => o.Payments).FirstAsync(o => o.Uuid == ex.Uuid);
        tracked.StockPosted = tracked.FinancePosted = false;
        await h.Posting.PostAsync(tracked);
        (h.Finance.Sales.Count, h.Finance.Returns.Count, h.Stock.Posted.Count(p => p.SourceUuid == ex.Uuid)).Should().Be((2, 1, 2));
    }

    [Fact]
    public async Task Exchange_EVEN_takes_no_payment_and_REFUND_pays_the_difference_back()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SetupAsync();
        var same = h.Catalog.Add("A2", 10m);
        var cheaper = h.Catalog.Add("C", 5m); // 5.85
        var sale = await h.SellAsync(terminal, shift, item, 2);
        var line = sale.Lines[0].Uuid;
        var reason = await h.ReasonAsync("WRONG_SIZE");

        await FluentActions.Awaiting(async () => await h.Orders.ExchangeAsync(sale.Uuid, Exchange(line, 1, reason, [Line(same, 1)], null, Pay("CASH", 1m)), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("No payment is taken*");
        var even = await h.Orders.ExchangeAsync(sale.Uuid, Exchange(line, 1, reason, [Line(same, 1)]), Returner);
        (even.Settlement, even.NetAmount, even.PaymentStatus).Should().Be(("EVEN", 0m, PosPaymentStatus.Paid));
        even.Payments.Should().OnlyContain(p => p.Method == "EXCHANGE" && p.Amount == 11.70m);

        await FluentActions.Awaiting(async () => await h.Orders.ExchangeAsync(sale.Uuid, Exchange(line, 1, reason, [Line(cheaper, 1)]), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Choose how to refund the difference.");
        var refund = await h.Orders.ExchangeAsync(sale.Uuid, Exchange(line, 1, reason, [Line(cheaper, 1)], "CASH"), Returner);
        (refund.Settlement, refund.NetAmount, refund.PaymentStatus).Should().Be(("REFUND", -5.85m, PosPaymentStatus.Refunded));
        h.Finance.Returns[refund.Uuid].Refunds.Select(r => (r.Method, r.Amount)).Should().Equal(("EXCHANGE", 5.85m), ("CASH", 5.85m));
        h.Finance.Sales[PosPostingService.ExchangeSaleUuid(refund.Uuid)].Tenders.Single().Should()
            .Match<CounterSaleTender>(t => t.Method == "EXCHANGE" && t.Amount == 5.85m);
        (await h.Shifts.GetAsync(shift.Uuid, AdminCaller)).TotalCashRefunds.Should().Be(5.85m);
        (await h.Orders.GetAsync(sale.Uuid, AdminCaller)).Status.Should().Be(PosOrderStatus.Returned);

        // The exchanged lines count as returned for the remainder rule (stored negative).
        (await h.Orders.ReturnableLinesAsync(sale.Uuid, null)).Order!.Lines[0].Should()
            .Match<PosLookupLineModel>(l => l.ReturnableQuantity == 0m && l.RefundIfAllReturned == 0m);
    }

    [Fact]
    public async Task Exchange_window_zero_means_no_exchanges_and_return_only_reasons_are_refused()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SetupAsync();
        var sale = await h.SellAsync(terminal, shift, item, 1);
        await FluentActions.Awaiting(async () => await h.Orders.ExchangeAsync(sale.Uuid, Exchange(sale.Lines[0].Uuid, 1, await h.ReasonAsync("DUPLICATE"), [Line(item, 1)]), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The reason 'Duplicate purchase' cannot be used for an exchange.");
        await h.Policies.PutStorePolicyAsync(store.Uuid, Policy(p => p.ExchangeWindowDays = 0), Admin);
        (await h.Orders.LookupAsync(sale.OrderNumber, null)).Policy!.WithinExchangeWindow.Should().BeFalse();
        await FluentActions.Awaiting(async () => await h.Orders.ExchangeAsync(sale.Uuid, Exchange(sale.Lines[0].Uuid, 1, await h.ReasonAsync("WRONG_SIZE"), [Line(item, 1)]), Returner))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Exchanges are not allowed under this store's policy.");
    }

    // ── store credit issued as a refund ──

    [Fact]
    public async Task Store_credit_refund_issues_a_bearer_note_for_walk_in_with_policy_expiry()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SetupAsync();
        var sale = await h.SellAsync(terminal, shift, item, 2);
        var ret = await h.Orders.ReturnAsync(sale.Uuid, Return(sale.Lines[0].Uuid, 1, await h.ReasonAsync("DEFECTIVE"), PosPaymentMethod.StoreCredit), Returner);

        ret.StoreCredit.Should().NotBeNull();
        ret.StoreCredit!.Number.Should().Be("CN-S1-20261010-0001");
        (ret.StoreCredit.Amount, ret.StoreCredit.Balance, ret.StoreCredit.ExpiresAt).Should().Be((11.70m, 11.70m, h.Clock.UtcNow.AddDays(365)));
        var payment = ret.Payments.Single();
        (payment.Method, payment.Reference, payment.CreditNoteUuid).Should().Be(("STORE_CREDIT", "CN-S1-20261010-0001", ret.StoreCredit.Uuid));
        h.Finance.Returns[ret.Uuid].Refunds.Single().Should().Match<CounterSaleTender>(r => r.Method == "STORE_CREDIT" && r.Reference == "CN-S1-20261010-0001");

        var detail = await h.Credit.GetAsync(ret.StoreCredit.Uuid);
        (detail.CustomerUuid, detail.Status, detail.ReturnOrderNumber, detail.IssuedAtStoreName).Should().Be((null, "ACTIVE", ret.OrderNumber, "Store S1"));
        detail.Transactions.Single().Should().Match<PosStoreCreditTxnModel>(t => t.Type == "ISSUE" && t.Amount == 11.70m && t.BalanceAfter == 11.70m
            && t.OrderUuid == ret.Uuid);
        (await h.Shifts.GetAsync(shift.Uuid, AdminCaller)).Should().Match<PosShiftModel>(s => s.TotalStoreCreditIssued == 11.70m
            && s.TotalOtherRefunds == 0m && s.ExpectedCashClosing == 100m + 23.40m);
        h.Hub.StoreEvents.Should().Contain(e => e.Event == PosHubEvents.CreditNoteIssued && e.Store == store.Uuid);
        (await h.Db.Orders.FirstAsync(o => o.Uuid == ret.Uuid)).StoreCreditNoteId.Should().NotBeNull();

        // A named customer's note is bound to them.
        var acme = h.Customers.Add("ACME");
        var named = await h.SellAsync(terminal, shift, item, 1, acme.Uuid);
        var r2 = await h.Orders.ReturnAsync(named.Uuid, Return(named.Lines[0].Uuid, 1, await h.ReasonAsync("DEFECTIVE"), PosPaymentMethod.StoreCredit), Returner);
        (await h.Credit.GetAsync(r2.StoreCredit!.Uuid)).CustomerUuid.Should().Be(acme.Uuid);
        (await h.Credit.ListForCustomerAsync(acme.Uuid, "ACTIVE")).Should().ContainSingle();
    }

    // ── redemption ──

    [Fact]
    public async Task Redemption_partial_exact_split_and_refusals()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SetupAsync();
        var acme = h.Customers.Add("ACME");
        var beta = h.Customers.Add("BETA");
        var note = await h.Credit.IssueAsync(new PosStoreCreditIssueRequest { StoreUuid = (await h.Db.Stores.FirstAsync()).Uuid, CustomerUuid = acme.Uuid, Amount = 30m }, Returner);
        PosSyncPaymentRequest Credit(decimal amount, Guid? cn = null) => new()
            { LocalId = Guid.NewGuid().ToString("N"), Method = "STORE_CREDIT", Amount = amount, CreditNoteUuid = cn ?? note.Uuid };

        // Refusals: offline, another customer, over balance, more than the order.
        var offline = Sale(terminal, shift, [(item, 1)], customer: acme.Uuid, offline: true, payments: Credit(11.70m));
        await FluentActions.Awaiting(async () => await h.Orders.SyncAsync(offline, Redeemer))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Credit note redemption requires internet connectivity.");
        await FluentActions.Awaiting(async () => await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], customer: beta.Uuid, payments: Credit(11.70m)), Redeemer))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This credit note belongs to another customer.");
        await FluentActions.Awaiting(async () => await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], customer: acme.Uuid, payments: Credit(11.70m)), CashierCaller))
            .Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(async () => await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 3)], customer: acme.Uuid, payments: Credit(35.10m)), Redeemer))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*has only 30.00 left.");
        await FluentActions.Awaiting(async () => await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], customer: acme.Uuid, payments: Credit(12m)), Redeemer))
            .Should().ThrowAsync<BadRequestException>().WithMessage("A STORE_CREDIT payment of 12.00 exceeds the remaining balance of 11.70.");

        // Partial: 11.70 of 30 → 18.30 left, ACTIVE.
        var p1 = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], customer: acme.Uuid, payments: Credit(11.70m)), Redeemer);
        (await h.Credit.LookupAsync(note.Number.ToLowerInvariant())).Should().Match<PosStoreCreditModel>(n => n.RemainingBalance == 18.30m
            && n.RedeemedAmount == 11.70m && n.Status == "ACTIVE" && n.LastUsedAtStoreName == "Store S1");
        var order1 = await h.Orders.GetAsync(p1.OrderUuid!.Value, AdminCaller);
        order1.Payments.Single().Should().Match<PosPaymentModel>(p => p.CreditNoteUuid == note.Uuid && p.Reference == note.Number);
        h.Finance.Sales[order1.Uuid].Tenders.Single().Should().Match<CounterSaleTender>(t => t.Method == "STORE_CREDIT" && t.Reference == note.Number);
        h.Hub.StoreEvents.Should().Contain(e => e.Event == PosHubEvents.CreditNoteRedeemed);

        // Split: 18.30 credit + 5.10 cash on 23.40 → exact, FULLY_REDEEMED.
        await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 2)], customer: acme.Uuid, payments: [Credit(18.30m), Pay("CASH", 5.10m)]), Redeemer);
        var spent = await h.Credit.GetAsync(note.Uuid);
        (spent.RemainingBalance, spent.Status).Should().Be((0m, "FULLY_REDEEMED"));
        spent.Transactions.Select(t => (t.Type, t.Amount, t.BalanceAfter)).Should().Equal(("ISSUE", 30m, 30m), ("REDEEM", -11.70m, 18.30m), ("REDEEM", -18.30m, 0m));
        await FluentActions.Awaiting(async () => await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], customer: acme.Uuid, payments: Credit(11.70m)), Redeemer))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This credit note has no balance left.");

        var s = await h.Shifts.GetAsync(shift.Uuid, AdminCaller);
        (s.TotalStoreCreditRedeemed, s.TotalCashSales).Should().Be((30m, 5.10m));
        h.Gate.Off.Add(PosFeatureCodes.CreditNotes);
        (await h.Sync.ConfigAsync(terminal.Uuid)).PaymentMethods.Should().NotContain("STORE_CREDIT");
    }

    [Fact]
    public async Task Bearer_note_expiry_cancel_and_the_expiry_job()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SetupAsync();
        var bearer = await h.Credit.IssueAsync(new PosStoreCreditIssueRequest { StoreUuid = store.Uuid, Amount = 20m, LocalId = "iss-1" }, Returner);
        (await h.Credit.IssueAsync(new PosStoreCreditIssueRequest { StoreUuid = store.Uuid, Amount = 20m, LocalId = "iss-1" }, Returner)).Uuid.Should().Be(bearer.Uuid);
        bearer.ExpiresAt.Should().Be(h.Clock.UtcNow.AddDays(365)); // policy default when none given
        var shortLived = await h.Credit.IssueAsync(new PosStoreCreditIssueRequest { StoreUuid = store.Uuid, Amount = 10m, ExpiresAt = h.Clock.UtcNow.AddDays(1) }, Returner);
        var cancelMe = await h.Credit.IssueAsync(new PosStoreCreditIssueRequest { StoreUuid = store.Uuid, Amount = 5m }, Returner);
        await FluentActions.Awaiting(async () => await h.Credit.IssueAsync(new PosStoreCreditIssueRequest { StoreUuid = store.Uuid, Amount = 0m }, Returner))
            .Should().ThrowAsync<BadRequestException>();

        // A bearer note pays for anyone's sale.
        var other = h.Customers.Add("ANY");
        await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], customer: other.Uuid,
            payments: new PosSyncPaymentRequest { LocalId = "b1", Method = "STORE_CREDIT", Amount = 11.70m, CreditNoteUuid = bearer.Uuid }), Redeemer);

        // Cancel.
        var cancelled = await h.Credit.CancelAsync(cancelMe.Uuid, "issued by mistake", Returner);
        (cancelled.Status, cancelled.RemainingBalance).Should().Be(("CANCELLED", 0m));
        await FluentActions.Awaiting(async () => await h.Credit.CancelAsync(cancelMe.Uuid, null, Returner)).Should().ThrowAsync<BadRequestException>();
        (await h.Credit.TransactionsAsync(cancelMe.Uuid)).Last().Should().Match<PosStoreCreditTxnModel>(t => t.Type == "CANCEL" && t.Amount == -5m);
        await FluentActions.Awaiting(async () => await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)],
                payments: new PosSyncPaymentRequest { LocalId = "c1", Method = "STORE_CREDIT", Amount = 5m, CreditNoteUuid = cancelMe.Uuid }), Redeemer))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This credit note was cancelled.");

        // Expiry: refused at the till as soon as it is past, booked by the nightly job.
        h.Clock.Advance(TimeSpan.FromDays(2));
        (await h.Credit.GetAsync(shortLived.Uuid)).Status.Should().Be("EXPIRED");
        await FluentActions.Awaiting(async () => await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)],
                payments: new PosSyncPaymentRequest { LocalId = "e1", Method = "STORE_CREDIT", Amount = 5m, CreditNoteUuid = shortLived.Uuid }), Redeemer))
            .Should().ThrowAsync<BadRequestException>().WithMessage("This credit note has expired.");

        var jobDb = h.NewDb(new OrderSyncTests.HangfireLikeTenant());
        h.Gate.Off.Add(PosFeatureCodes.CreditNotes);
        (await new StoreCreditExpiryJob(jobDb, h.Hub, h.Gate, h.Clock).RunAsync()).Should().Be(0); // feature off: skipped
        h.Gate.Off.Clear();
        (await new StoreCreditExpiryJob(jobDb, h.Hub, h.Gate, h.Clock).RunAsync()).Should().Be(1);
        var expired = await h.Credit.GetAsync(shortLived.Uuid);
        (expired.Status, expired.RemainingBalance, expired.RedeemedAmount).Should().Be(("EXPIRED", 0m, 0m));
        expired.Transactions.Last().Should().Match<PosStoreCreditTxnModel>(t => t.Type == "EXPIRE" && t.Amount == -10m);
        h.Hub.StoreEvents.Should().Contain(e => e.Event == PosHubEvents.CreditNoteExpired && e.Store == store.Uuid);
        (await new StoreCreditExpiryJob(h.NewDb(new OrderSyncTests.HangfireLikeTenant()), h.Hub, h.Gate, h.Clock).RunAsync()).Should().Be(0);

        var page = await h.Credit.ListAsync(new PosStoreCreditListFilter { Status = "ACTIVE,CANCELLED", StoreUuid = store.Uuid });
        page.Data.Select(n => n.Uuid).Should().BeEquivalentTo([bearer.Uuid, cancelMe.Uuid]);
        await FluentActions.Awaiting(async () => await h.Credit.LookupAsync("CN-NOPE")).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Voiding_a_sale_paid_by_store_credit_restores_the_note()
    {
        var h = new PosHarness();
        var (store, terminal, shift, item) = await h.SetupAsync();
        var note = await h.Credit.IssueAsync(new PosStoreCreditIssueRequest { StoreUuid = store.Uuid, Amount = 11.70m }, Returner);
        var sale = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)],
            payments: new PosSyncPaymentRequest { LocalId = "v1", Method = "STORE_CREDIT", Amount = 11.70m, CreditNoteUuid = note.Uuid }), Redeemer);
        (await h.Credit.GetAsync(note.Uuid)).Status.Should().Be("FULLY_REDEEMED");

        await h.Orders.VoidAsync(sale.OrderUuid!.Value, "wrong customer", Returner);
        var restored = await h.Credit.GetAsync(note.Uuid);
        (restored.Status, restored.RemainingBalance, restored.RedeemedAmount).Should().Be(("ACTIVE", 11.70m, 0m));
        restored.Transactions.Last().Should().Match<PosStoreCreditTxnModel>(t => t.Type == "ADJUST" && t.Amount == 11.70m && t.BalanceAfter == 11.70m);
        (await h.Shifts.GetAsync(shift.Uuid, AdminCaller)).TotalStoreCreditRedeemed.Should().Be(0m);
    }

    [Fact]
    public async Task Offline_store_credit_refund_keeps_the_device_number_and_exchange_can_redeem_online()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SetupAsync();
        var bigger = h.Catalog.Add("B", 20m);
        var sale = await h.SellAsync(terminal, shift, item, 2);
        var line = sale.Lines[0].Uuid;
        var res = await h.Orders.SyncReturnAsync(new PosReturnSyncRequest
        {
            Kind = "RETURN", OriginalOrderUuid = sale.Uuid, IsOffline = true, LocalId = "off-cn", TerminalUuid = terminal.Uuid,
            ReasonUuid = await h.ReasonAsync("DEFECTIVE"), RefundMethod = "STORE_CREDIT", StoreCreditOfflineNumber = "cn-s1-off-abcd1234",
            Lines = [new PosReturnLineRequest { OriginalLineUuid = line, Quantity = 1 }]
        }, Returner);
        var ret = await h.Orders.GetAsync(res.OrderUuid!.Value, AdminCaller);
        ret.StoreCredit!.Number.Should().StartWith("CN-S1-2026");
        var byOffline = await h.Credit.LookupAsync("CN-S1-OFF-ABCD1234");
        byOffline.Uuid.Should().Be(ret.StoreCredit.Uuid);

        // Exchange paying the difference partly with that (bearer) note: online redemption.
        var ex = await h.Orders.ExchangeAsync(sale.Uuid, Exchange(line, 1, await h.ReasonAsync("WRONG_SIZE"), [Line(bigger, 1)], null,
            new PosSyncPaymentRequest { LocalId = "x1", Method = "STORE_CREDIT", Amount = 11.70m, CreditNoteUuid = byOffline.Uuid }), Returner);
        ex.Payments.Should().Contain(p => p.Method == "STORE_CREDIT" && p.CreditNoteUuid == byOffline.Uuid && !p.IsRefund);
        (await h.Credit.GetAsync(byOffline.Uuid)).Status.Should().Be("FULLY_REDEEMED");
    }

    // ── SQL Server: double spend ──

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

    /// <summary>
    /// SQL Server (LocalDB): the A39 migration applies; two tills spending the same note at once — the second save loses on the
    /// note's RowVersion (409, nothing saved), the balance never goes below zero. Skipped quietly without LocalDB.
    /// </summary>
    [Fact]
    public async Task Sql_server_concurrent_double_redeem_spends_a_note_once()
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
            var note = await new PosStoreCreditService(setup, h.Tenant, read, new PosNumberSequenceService(setup), h.Hub, h.Customers, h.Gate)
                .IssueAsync(new PosStoreCreditIssueRequest { StoreUuid = store.Uuid, Amount = 11.70m }, Returner);
            note.Number.Should().Be("CN-S1-20261010-0001");
            (await new PosReturnPolicyService(setup, h.Tenant, read, h.Hub).ListReasonsAsync(new PosReasonListFilter())).Should().HaveCount(9);

            PosOrderService Service(PosDbContext db, IPosNumberSequence? numbers = null) => new(db, h.Tenant,
                new PosReadModel(db, h.Tenant, h.Pins, h.TaxCodes, clock: h.Clock), numbers ?? new PosNumberSequenceService(db),
                new PosShiftTotals(db), new PosPostingService(db, h.Stock, h.Finance), h.Catalog, h.Customers, h.Balances, h.Pins, h.Gate);
            PosSyncOrderRequest Spend(string id) => Sale(terminal, shift, [(item, 1)], localId: id,
                payments: new PosSyncPaymentRequest { LocalId = id + "-p", Method = "STORE_CREDIT", Amount = 11.70m, CreditNoteUuid = note.Uuid });

            PosSyncResultModel? winner = null;
            await using var loserDb = Db();
            await using var winnerDb = Db();
            var loser = Service(loserDb, new RacingNumbers(new PosNumberSequenceService(loserDb),
                async () => winner = await Service(winnerDb).SyncAsync(Spend("w"), Redeemer)));
            await FluentActions.Awaiting(() => loser.SyncAsync(Spend("l"), Redeemer))
                .Should().ThrowAsync<ConflictException>().WithMessage("The credit note was used at the same time*");
            winner!.Success.Should().BeTrue();

            await using var check = Db();
            var cn = await check.StoreCreditNotes.SingleAsync();
            (cn.RemainingBalance, cn.Status).Should().Be((0m, PosStoreCreditStatus.FullyRedeemed));
            (await check.Orders.CountAsync()).Should().Be(1);
            (await check.StoreCreditTransactions.CountAsync(t => t.Type == PosStoreCreditTxnType.Redeem)).Should().Be(1);
            await FluentActions.Awaiting(() => Service(check).SyncAsync(Spend("again"), Redeemer))
                .Should().ThrowAsync<BadRequestException>().WithMessage("This credit note has no balance left.");

            // Shift totals with the A39 columns translate on SQL Server.
            var shiftId = (await check.Shifts.SingleAsync()).Id;
            await using (var db = Db()) await new PosShiftTotals(db).RecalculateAsync(shiftId);
            (await Db().Shifts.SingleAsync()).TotalStoreCreditRedeemed.Should().Be(11.70m);
        }
        finally
        {
            await setup.Database.EnsureDeletedAsync();
        }
    }
}
