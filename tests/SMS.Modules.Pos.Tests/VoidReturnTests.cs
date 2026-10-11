using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Hubs;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Pos.Tests.PosHarness;

namespace SMS.Modules.Pos.Tests;

/// <summary>A38 API-CONTRACT §5 — void (same shift) and returns (quantities, refunds, postings, totals).</summary>
public class VoidReturnTests
{
    private static readonly PosCaller Supervisor_ = Caller(Supervisor, PosPermissions.OrderCreate, PosPermissions.OrderVoid, PosPermissions.OrderReturn);

    [Fact]
    public async Task Void_reverses_stock_and_finance_and_the_shift_totals()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        var sale = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 2)], 23.40m), CashierCaller);

        await FluentActions.Awaiting(() => h.Orders.VoidAsync(sale.OrderUuid!.Value, " ", Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("A reason is required*");
        var voided = await h.Orders.VoidAsync(sale.OrderUuid!.Value, "Wrong item", Supervisor_);

        (voided.Status, voided.VoidReason, voided.StockPosted, voided.FinancePosted).Should().Be((PosOrderStatus.Voided, "Wrong item", true, true));
        voided.CreditNoteUuid.Should().NotBeNull();
        voided.Lines.Single().ReturnableQuantity.Should().Be(0);
        h.Stock.Posted.Select(p => p.TransactionType).Should().Equal(PosStockTransactionType.Sale, PosStockTransactionType.Void);
        h.Stock.Posted[1].IsOutbound.Should().BeFalse();
        h.Finance.VoidSourceUuids.Should().Equal(PosPostingService.VoidSourceUuid(sale.OrderUuid!.Value));
        var s = await h.Shifts.GetAsync(shift.Uuid, AdminCaller);
        (s.TotalCashSales, s.OrderCount, s.ExpectedCashClosing).Should().Be((0m, 0, 100m));

        await FluentActions.Awaiting(() => h.Orders.VoidAsync(sale.OrderUuid!.Value, "again", Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The order is already voided.");
    }

    [Fact]
    public async Task Void_is_refused_on_another_shift_and_finance_refusals_surface_unchanged()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        var sale = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m), CashierCaller);

        h.Finance.RefuseVoid = "Invoice SI-1 has a back-office payment; it cannot be voided at the POS.";
        await FluentActions.Awaiting(() => h.Orders.VoidAsync(sale.OrderUuid!.Value, "x", Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage(h.Finance.RefuseVoid);
        (await h.Orders.GetAsync(sale.OrderUuid!.Value, AdminCaller)).Status.Should().Be(PosOrderStatus.Completed); // nothing changed
        h.Finance.RefuseVoid = null;

        await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 111.70m }, AdminCaller);
        await FluentActions.Awaiting(() => h.Orders.VoidAsync(sale.OrderUuid!.Value, "x", Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Orders can only be voided within the same shift.");
    }

    [Fact]
    public async Task Void_of_a_sale_never_posted_posts_sale_and_reversal_later()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        h.Finance.Fail = true;
        h.Stock.Fail = true;
        var sale = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m), CashierCaller);
        var voided = await h.Orders.VoidAsync(sale.OrderUuid!.Value, "x", Supervisor_);
        (voided.Status, voided.StockPosted, voided.FinancePosted).Should().Be((PosOrderStatus.Voided, false, false));

        h.Finance.Fail = false;
        h.Stock.Fail = false;
        var order = await h.Db.Orders.Include(o => o.Lines).Include(o => o.Payments).SingleAsync();
        await h.Posting.PostAsync(order);
        (order.StockPosted, order.FinancePosted).Should().Be((true, true));
        h.Stock.Posted.Select(p => p.TransactionType).Should().Equal(PosStockTransactionType.Sale, PosStockTransactionType.Void);
        h.Finance.Sales.Should().ContainKey(order.Uuid);
        h.Finance.VoidSourceUuids.Should().ContainSingle();
    }

    [Fact]
    public async Task Partial_then_full_return_with_cash_refunds()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        var sale = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 3)], 35.10m), CashierCaller); // 3 × 11.70
        var original = await h.Orders.GetAsync(sale.OrderUuid!.Value, AdminCaller);
        var line = original.Lines.Single().Uuid;

        PosReturnRequest Ret(string id, decimal qty, decimal refund, string method = PosPaymentMethod.Cash) => new()
        {
            LocalId = id, Reason = "Damaged", Lines = [new PosReturnLineRequest { OriginalLineUuid = line, Quantity = qty }],
            Refunds = [new PosReturnRefundRequest { Method = method, Amount = refund }]
        };

        await FluentActions.Awaiting(() => h.Orders.ReturnAsync(original.Uuid, Ret("r0", 4, 46.80m), Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Cannot return 4 of 'Product A'. Only 3 returnable.");
        await FluentActions.Awaiting(() => h.Orders.ReturnAsync(original.Uuid, Ret("r0", 1, 10m), Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Refunds (10.00) must equal the return total (11.70).");
        await FluentActions.Awaiting(() => h.Orders.ReturnAsync(original.Uuid, Ret("r0", 1, 11.70m, PosPaymentMethod.OnAccount), Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("On-account refunds are only possible for credit sales.");

        var first = await h.Orders.ReturnAsync(original.Uuid, Ret("r1", 1, 11.70m), Supervisor_);
        (first.OrderType, first.OrderNumber, first.GrandTotal, first.TaxTotal, first.PaymentStatus).Should()
            .Be((PosOrderType.Return, "RET-S1-20261010-0001", 11.70m, 1.70m, PosPaymentStatus.Refunded));
        first.OriginalOrderUuid.Should().Be(original.Uuid);
        first.Lines.Single().OriginalLineUuid.Should().Be(line);
        first.Payments.Single().IsRefund.Should().BeTrue();
        (await h.Orders.ReturnAsync(original.Uuid, Ret("r1", 1, 11.70m), Supervisor_)).Uuid.Should().Be(first.Uuid); // idempotent

        var after = await h.Orders.GetAsync(original.Uuid, AdminCaller);
        (after.Status, after.Lines.Single().ReturnedQuantity, after.Lines.Single().ReturnableQuantity).Should().Be((PosOrderStatus.PartiallyReturned, 1m, 2m));
        after.Returns.Should().ContainSingle(r => r.Uuid == first.Uuid);

        await FluentActions.Awaiting(() => h.Orders.ReturnAsync(original.Uuid, Ret("r2", 3, 35.10m), Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Cannot return 3 of 'Product A'. Only 2 returnable.");
        var rest = await h.Orders.ReturnAsync(original.Uuid, Ret("r2", 2, 23.40m), Supervisor_);
        rest.OrderNumber.Should().Be("RET-S1-20261010-0002");
        (await h.Orders.GetAsync(original.Uuid, AdminCaller)).Status.Should().Be(PosOrderStatus.Returned);
        await FluentActions.Awaiting(() => h.Orders.ReturnAsync(original.Uuid, Ret("r3", 1, 11.70m), Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("All items in this order have already been returned.");
        await FluentActions.Awaiting(() => h.Orders.VoidAsync(original.Uuid, "x", Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("A returned order cannot be voided.");

        // Postings: POS_RETURN per return; Finance credits against the sale's invoice, lines adding up to the sale.
        h.Stock.Posted.Count(p => p.TransactionType == PosStockTransactionType.Return).Should().Be(2);
        var credits = h.Finance.Returns.Values.ToList();
        credits.Should().OnlyContain(c => c.OriginalSalesInvoiceUuid == after.SalesInvoiceUuid);
        credits.SelectMany(c => c.Lines).Sum(l => l.LineTotal).Should().Be(35.10m);
        credits.SelectMany(c => c.Lines).Should().OnlyContain(l => l.OriginalSourceLineUuid == line && l.LineTotal == l.NetAmount + l.TaxAmount);

        var s = await h.Shifts.GetAsync(shift.Uuid, AdminCaller);
        (s.TotalCashRefunds, s.RefundCount, s.NetTotal, s.ExpectedCashClosing).Should().Be((35.10m, 2, 0m, 100m));
    }

    [Fact]
    public async Task On_account_refund_reduces_a_credit_sale_and_needs_the_feature()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        var acme = h.Customers.Add("ACME", creditLimit: 500m);
        await h.Settings.PutCustomerAsync(acme.Uuid, new PosCustomerSettingsModel { AllowPosCredit = true }, Admin);
        var sale = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 2)], customer: acme.Uuid,
            payments: [Pay(PosPaymentMethod.Cash, 10m), Pay(PosPaymentMethod.OnAccount, 13.40m)]), CashierCaller);
        var line = (await h.Orders.GetAsync(sale.OrderUuid!.Value, AdminCaller)).Lines.Single().Uuid;
        PosReturnRequest Ret(string id, params (string Method, decimal Amount)[] refunds) => new()
        {
            LocalId = id, Reason = "Changed mind", Lines = [new PosReturnLineRequest { OriginalLineUuid = line, Quantity = 1 }],
            Refunds = refunds.Select(r => new PosReturnRefundRequest { Method = r.Method, Amount = r.Amount }).ToList()
        };

        h.Gate.Off.Add(PosFeatureCodes.Returns);
        await FluentActions.Awaiting(() => h.Orders.ReturnAsync(sale.OrderUuid!.Value, Ret("x", (PosPaymentMethod.OnAccount, 11.70m)), Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Returns are not enabled*");
        h.Gate.Off.Clear();

        var r = await h.Orders.ReturnAsync(sale.OrderUuid!.Value, Ret("r1", (PosPaymentMethod.OnAccount, 11.70m)), Supervisor_);
        r.Payments.Single().Method.Should().Be(PosPaymentMethod.OnAccount);
        h.Hub.Sent.Should().Contain(e => e.Event == PosHubEvents.CustomerBalanceChanged && e.Target == acme.Uuid);
        // 13.40 on account, 11.70 refunded on account → 1.70 still open; another 11.70 is too much.
        await FluentActions.Awaiting(() => h.Orders.ReturnAsync(sale.OrderUuid!.Value, Ret("r2", (PosPaymentMethod.OnAccount, 11.70m)), Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*still open on this sale (1.70)*");
        (await h.Shifts.GetAsync(shift.Uuid, AdminCaller)).TotalOtherRefunds.Should().Be(11.70m);
    }

    [Fact]
    public async Task Return_needs_an_open_shift_of_the_store()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        var sale = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m), CashierCaller);
        var line = (await h.Orders.GetAsync(sale.OrderUuid!.Value, AdminCaller)).Lines.Single().Uuid;
        await h.Shifts.CloseAsync(shift.Uuid, new PosCloseShiftRequest { ActualCashClosing = 111.70m }, AdminCaller);

        var req = new PosReturnRequest
        {
            LocalId = "r", Reason = "x", Lines = [new PosReturnLineRequest { OriginalLineUuid = line, Quantity = 1 }],
            Refunds = [new PosReturnRefundRequest { Method = PosPaymentMethod.Cash, Amount = 11.70m }]
        };
        await FluentActions.Awaiting(() => h.Orders.ReturnAsync(sale.OrderUuid!.Value, req, Supervisor_))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Open a shift on the terminal*");

        var next = await h.Shifts.OpenAsync(terminal.Uuid, new PosOpenShiftRequest { OpeningFloat = 50m }, AdminCaller);
        var ret = await h.Orders.ReturnAsync(sale.OrderUuid!.Value, req, Supervisor_);
        ret.ShiftUuid.Should().Be(next.Uuid);
        (await h.Shifts.GetAsync(next.Uuid, AdminCaller)).ExpectedCashClosing.Should().Be(38.30m);
    }

    [Fact]
    public async Task Return_waits_for_the_sale_in_finance()
    {
        var h = new PosHarness();
        var (_, terminal, shift, item) = await h.SellingSetupAsync();
        h.Finance.Fail = true;
        var sale = await h.Orders.SyncAsync(Sale(terminal, shift, [(item, 1)], 11.70m), CashierCaller);
        h.Finance.Fail = false;
        var line = (await h.Orders.GetAsync(sale.OrderUuid!.Value, AdminCaller)).Lines.Single().Uuid;
        var ret = await h.Orders.ReturnAsync(sale.OrderUuid!.Value, new PosReturnRequest
        {
            LocalId = "r", Reason = "x", Lines = [new PosReturnLineRequest { OriginalLineUuid = line, Quantity = 1 }],
            Refunds = [new PosReturnRefundRequest { Method = PosPaymentMethod.Cash, Amount = 11.70m }]
        }, Supervisor_);
        (ret.StockPosted, ret.FinancePosted).Should().Be((true, false));
        ret.PostingError.Should().Contain("not in Finance yet");

        // The retry job takes them in id order: the sale first, then its return.
        h.Clock.Advance(TimeSpan.FromHours(1));
        var db = h.NewDb(new OrderSyncTests.HangfireLikeTenant());
        (await new PosPostingRetryJob(db, new PosPostingService(db, h.Stock, h.Finance), new FakeGate(), h.Clock).RunAsync()).Should().Be(2);
        h.Finance.Returns.Should().ContainSingle();
    }
}
