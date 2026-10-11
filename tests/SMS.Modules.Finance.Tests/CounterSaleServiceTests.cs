using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.MultiCurrency;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A38 (FIN) — POS counter sales, returns and voids (<see cref="CounterSaleService"/>) over the real invoice, payment,
/// customer-ledger and product-ledger services on one in-memory database; only other modules are faked. After every step the
/// customer's books are checked whole (<see cref="PosBooks"/>), with what the balance lookup and the aging report read.
/// </summary>
public sealed class CounterSaleServiceTests
{
    private readonly ReceivablesWorld _world = new();
    private readonly ReceivablesDesk  _desk;
    private readonly Guid             _customer;
    private int _seq;

    public CounterSaleServiceTests()
    {
        _desk     = _world.For(Guid.NewGuid());
        _customer = _desk.NewCustomer("Counter Customer");
    }

    // ── Sales ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_cash_sale_is_an_issued_paid_invoice_with_one_payment_and_cost_of_sales_at_wac()
    {
        var a = await StockedVariantAsync(10m, 20m);
        var b = await StockedVariantAsync(5m, 3m);

        var result = await SaleAsync([Line(a, 2m, 50m, tax: 15m), Line(b, 1m, 30m)], Cash(145m));

        result.GrandTotal.Should().Be(145m);
        result.PaymentUuids.Should().HaveCount(1);

        var books = await BooksAsync();
        var invoice = books.Invoice(result.SalesInvoiceUuid);
        invoice.SourceType.Should().Be("POS");
        invoice.SaleOrderUuid.Should().BeNull();
        invoice.Status.Should().Be("PAID");
        invoice.AmountPaid.Should().Be(145m);
        invoice.BalanceDue.Should().Be(0m);
        invoice.DueDate.Should().Be(TestClock.Start.Date, "a fully paid sale falls due the day it is made");
        invoice.InvoiceNumber.Should().StartWith("SINV-20260920-");
        invoice.Subtotal.Should().Be(130m);
        invoice.TaxAmount.Should().Be(15m);
        invoice.ExchangeRate.Should().Be(1m);

        var payment = books.Payment(result.PaymentUuids[0]);
        payment.PaymentMethod.Should().Be("CASH");
        payment.Allocations.Should().ContainSingle().Which.AllocatedAmount.Should().Be(145m);

        books.Ledger.Select(e => (e.EntryType, e.DebitAmount, e.CreditAmount)).Should().Equal(
            ("INVOICE", 145m, 0m), ("PAYMENT", 0m, 145m));

        (await _desk.ProductLedgerAsync(a)).Last().Should().Match<ProductLedgerEntry>(e =>
            e.EntryType == "SALE" && e.Quantity == 2m && e.UnitCost == 20m && e.TotalCost == 40m && e.RunningQty == 8m);

        await AssertBooksAsync(owes: 0m);
    }

    [Fact]
    public async Task A_split_sale_records_one_payment_per_tender_with_mobile_and_cheque_numbers()
    {
        var v = await StockedVariantAsync(10m, 10m);

        var result = await SaleAsync([Line(v, 4m, 25m)],
            Tender("CASH", 40m), Tender("MOBILE", 30m, "EP-778"), Tender("CHEQUE", 20m), Tender("CARD", 10m, "4411"));

        var books = await BooksAsync();
        books.Invoice(result.SalesInvoiceUuid).Status.Should().Be("PAID");
        result.PaymentUuids.Select(p => books.Payment(p).PaymentMethod).Should().Equal("CASH", "MOBILE", "CHEQUE", "CARD");
        books.Payment(result.PaymentUuids[1]).BankReference.Should().Be("EP-778");
        books.Payment(result.PaymentUuids[2]).ChequeNumber.Should().Be(SourceNumberOf(result), "a cheque with no reference carries the POS order number");
        books.Payments.Select(p => p.PaymentNumber).Should().OnlyHaveUniqueItems();

        await AssertBooksAsync(owes: 0m);
    }

    [Fact]
    public async Task A_credit_sale_is_an_issued_invoice_due_after_the_customers_terms_and_shows_in_balance_and_aging()
    {
        var v = await StockedVariantAsync(10m, 10m);

        var result = await SaleAsync([Line(v, 2m, 60m)], [Tender("ON_ACCOUNT", 120m)], terms: 15);

        result.PaymentUuids.Should().BeEmpty();
        var invoice = (await BooksAsync()).Invoice(result.SalesInvoiceUuid);
        invoice.Status.Should().Be("ISSUED");
        invoice.BalanceDue.Should().Be(120m);
        invoice.DueDate.Should().Be(TestClock.Start.Date.AddDays(15));

        await AssertBooksAsync(owes: 120m);
        (await AgingAsync()).Should().Be(120m);
    }

    [Fact]
    public async Task Without_customer_terms_a_credit_sale_falls_due_in_30_days()
    {
        var v = await StockedVariantAsync(10m, 10m);
        var result = await SaleAsync([Line(v, 1m, 60m)], [Tender("ON_ACCOUNT", 60m)], terms: 0);

        (await BooksAsync()).Invoice(result.SalesInvoiceUuid).DueDate.Should().Be(TestClock.Start.Date.AddDays(30));
    }

    [Fact]
    public async Task A_partial_credit_sale_is_partially_paid_with_the_rest_on_the_customers_balance()
    {
        var v = await StockedVariantAsync(10m, 10m);

        var result = await SaleAsync([Line(v, 1m, 100m)], Tender("CASH", 30m), Tender("ON_ACCOUNT", 70m));

        var invoice = (await BooksAsync()).Invoice(result.SalesInvoiceUuid);
        invoice.Status.Should().Be("PARTIALLY_PAID");
        invoice.AmountPaid.Should().Be(30m);
        invoice.BalanceDue.Should().Be(70m);

        await AssertBooksAsync(owes: 70m);
        (await AgingAsync()).Should().Be(70m);
    }

    [Fact]
    public async Task Posting_the_same_sale_twice_returns_the_first_result_and_writes_nothing_more()
    {
        var v = await StockedVariantAsync(10m, 10m);
        var request = SaleRequest([Line(v, 1m, 50m)], [Tender("CASH", 50m)]);

        var first  = await PostAsync(s => s.PostSaleAsync(request));
        var before = (await BooksAsync()).Fingerprint();
        var second = await PostAsync(s => s.PostSaleAsync(request));

        second.SalesInvoiceUuid.Should().Be(first.SalesInvoiceUuid);
        second.InvoiceNumber.Should().Be(first.InvoiceNumber);
        second.PaymentUuids.Should().Equal(first.PaymentUuids);
        (await BooksAsync()).Fingerprint().Should().Be(before);
        (await _desk.ProductLedgerAsync(v)).Should().HaveCount(2, "one GRN, one sale — not two sales");
    }

    [Theory]
    [InlineData("total")]      // line total ≠ net + tax
    [InlineData("tenders")]    // tenders do not add up to the total
    [InlineData("method")]     // unknown tender method
    [InlineData("nothing")]    // a sale of nothing
    [InlineData("lines")]      // two lines with one id
    [InlineData("decimals")]   // three decimals
    public async Task A_sale_that_breaks_an_invariant_is_refused_and_books_nothing(string broken)
    {
        var v = await StockedVariantAsync(10m, 10m);
        var good = Line(v, 1m, 50m);
        var request = broken switch
        {
            "total"    => SaleRequest([good with { LineTotal = 51m }], [Tender("CASH", 51m)]),
            "tenders"  => SaleRequest([good], [Tender("CASH", 40m)]),
            "method"   => SaleRequest([good], [Tender("VOUCHER", 50m)]),
            "nothing"  => SaleRequest([good with { UnitPrice = 0m, NetAmount = 0m, LineTotal = 0m }], [Tender("CASH", 0.01m)]),
            "lines"    => SaleRequest([good, good with { VariantUuid = v }], [Tender("CASH", 100m)]),
            "decimals" => SaleRequest([good with { NetAmount = 50.001m, LineTotal = 50.001m }], [Tender("CASH", 50.001m)]),
            _          => throw new ArgumentOutOfRangeException(nameof(broken))
        };

        var act = () => PostAsync(s => s.PostSaleAsync(request));

        await act.Should().ThrowAsync<BadRequestException>();
        (await BooksAsync()).Invoices.Should().BeEmpty();
        (await BooksAsync()).Ledger.Should().BeEmpty();
    }

    // ── Negative COGS (D-9) ──────────────────────────────────────────────────

    [Fact]
    public async Task A_pos_sale_beyond_the_ledgers_stock_is_costed_and_runs_the_quantity_negative_a_back_office_sale_is_still_refused()
    {
        var v = await StockedVariantAsync(2m, 10m);

        await SaleAsync([Line(v, 5m, 30m)], Cash(150m));

        var sale = (await _desk.ProductLedgerAsync(v)).Last();
        sale.EntryType.Should().Be("SALE");
        sale.UnitCost.Should().Be(10m);
        sale.TotalCost.Should().Be(50m, "the 2 held leave with their 20, the 3 short at the WAC of 10");
        sale.RunningQty.Should().Be(-3m);
        sale.RunningValue.Should().Be(-30m);

        // The existing rule stands for everyone else: a delivery invoice cannot be issued for stock the ledger lacks.
        var order    = await _desk.PlaceOrderAsync(_customer, new ReceivablesDesk.OrderLine(1m, 40m, Stocked: false, Variant: v));
        var delivery = _desk.Deliver(order, "DELIVERED", (0, 1m));
        var issue    = () => _desk.IssueDeliveryAsync(delivery);
        await issue.Should().ThrowAsync<ConflictException>();

        // D-17: a GRN onto the negative quantity adds what it cost (value conserved); WAC is value / qty again once positive.
        await _desk.StockAsync(v, 10m, 12m);
        var grn = (await _desk.ProductLedgerAsync(v)).Last();
        grn.TotalCost.Should().Be(120m);
        grn.RunningQty.Should().Be(7m);
        grn.RunningValue.Should().Be(90m);

        await AssertBooksAsync(owes: 0m);
    }

    [Fact]
    public async Task A_pos_sale_of_a_variant_the_ledger_never_saw_is_costed_at_zero()
    {
        var v = Guid.NewGuid();
        _world.ProductOf(v);

        await SaleAsync([Line(v, 2m, 10m)], Cash(20m));

        var sale = (await _desk.ProductLedgerAsync(v)).Single();
        sale.TotalCost.Should().Be(0m);
        sale.RunningQty.Should().Be(-2m);
    }

    [Theory]
    // prevQty, prevValue, qty, lastUnitCost → unit, total, runningQty, runningValue
    [InlineData(0, 0, 3, 7, 7, 21, -3, -21)]          // nothing on hand: the last known cost
    [InlineData(-3, -30, 2, 10, 10, 20, -5, -50)]     // already short: still the last known cost
    [InlineData(4, 10, 6, 99, 2.5, 15, -2, -5)]       // part held: all 10 of it leaves, the 2 short at WAC 2.5
    [InlineData(5, 50, 2, 99, 10, 20, 3, 30)]         // enough held: the ordinary OUT
    public void Costing_an_out_beyond_the_running_quantity(
        double prevQty, double prevValue, double qty, double lastCost, double unit, double total, double runQty, double runValue)
    {
        var step = WeightedAverageCosting.OutAllowingNegative((decimal)prevQty, (decimal)prevValue, (decimal)qty, (decimal)lastCost);

        step.Should().Be(new CostingStep((decimal)unit, (decimal)total, (decimal)runQty, (decimal)runValue));
        ((decimal)prevValue - step.TotalCost).Should().Be(step.RunningValue, "running value = previous − total, always");
    }

    // ── Returns (D-10) ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_return_refunded_in_cash_is_a_credit_note_with_a_refund_and_stock_back_at_the_sales_cost()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 2m, 50m)], Cash(100m));

        var ret = await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 50m)], [Tender("CASH", 50m)]);

        ret.Total.Should().Be(50m);
        ret.CreditNoteNumber.Should().StartWith("SCN-20260920-");
        ret.RefundUuids.Should().ContainSingle();

        var books = await BooksAsync();
        var credit = books.Invoice(ret.CreditNoteUuid);
        credit.Status.Should().Be("CREDIT_NOTE");
        credit.OriginalInvoiceUuid.Should().Be(sale.SalesInvoiceUuid);
        credit.CreditedAmount.Should().Be(0m, "a cash refund does not touch the (paid) invoice's balance");

        var original = books.Invoice(sale.SalesInvoiceUuid);
        original.ReturnedAmount.Should().Be(50m);
        original.BalanceDue.Should().Be(0m);
        original.Status.Should().Be("PAID");

        books.Ledger.Select(e => e.EntryType).Should().Equal("INVOICE", "PAYMENT", "CREDIT_NOTE", "REFUND");

        var back = (await _desk.ProductLedgerAsync(v)).Last();
        back.Should().Match<ProductLedgerEntry>(e => e.EntryType == "RETURN_IN" && e.Quantity == 1m && e.UnitCost == 20m && e.RunningQty == 9m);

        await AssertBooksAsync(owes: 0m);
        (await AgingAsync()).Should().Be(0m);
    }

    [Fact]
    public async Task Returning_part_of_a_credit_sale_on_account_reduces_its_balance()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 2m, 50m)], [Tender("ON_ACCOUNT", 100m)]);

        var ret = await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 50m)], [Tender("ON_ACCOUNT", 50m)]);

        ret.RefundUuids.Should().BeEmpty();
        var books = await BooksAsync();
        books.Invoice(ret.CreditNoteUuid).CreditedAmount.Should().Be(50m);
        var original = books.Invoice(sale.SalesInvoiceUuid);
        original.CreditedAmount.Should().Be(50m);
        original.BalanceDue.Should().Be(50m);
        original.Status.Should().Be("PARTIALLY_PAID");

        await AssertBooksAsync(owes: 50m);
        (await AgingAsync()).Should().Be(50m);

        // The rest comes back too: nothing is owed and the invoice is settled.
        await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 50m)], [Tender("ON_ACCOUNT", 50m)]);
        (await BooksAsync()).Invoice(sale.SalesInvoiceUuid).Status.Should().Be("PAID");
        await AssertBooksAsync(owes: 0m);
        (await AgingAsync()).Should().Be(0m);
    }

    [Fact]
    public async Task Partial_returns_add_up_to_the_sold_quantity_and_no_further_and_each_is_idempotent()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 3m, 30m, tax: 9m)], Cash(99m));

        var firstRequest = ReturnRequest(sale, [ReturnLine(sale, 0, 1m, 30m, 3m)], [Tender("CASH", 33m)]);
        var first  = await PostAsync(s => s.PostReturnAsync(firstRequest));
        var again  = await PostAsync(s => s.PostReturnAsync(firstRequest));
        again.CreditNoteUuid.Should().Be(first.CreditNoteUuid);
        again.RefundUuids.Should().Equal(first.RefundUuids);

        await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 30m, 3m)], [Tender("CARD", 33m)]);

        var tooMany = () => ReturnAsync(sale, [ReturnLine(sale, 0, 2m, 60m, 6m)], [Tender("CASH", 66m)]);
        (await tooMany.Should().ThrowAsync<BadRequestException>()).WithMessage("Return quantity cannot exceed the original quantity.");

        var books = await BooksAsync();
        books.Invoice(sale.SalesInvoiceUuid).ReturnedAmount.Should().Be(66m);
        books.Invoices.Count(i => i.Status == "CREDIT_NOTE").Should().Be(2);
        (await _desk.ProductLedgerAsync(v)).Last().RunningQty.Should().Be(9m);
        await AssertBooksAsync(owes: 0m);
    }

    // ── Voids ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_void_reverses_the_sales_payments_and_credits_the_whole_invoice()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 2m, 50m)], Tender("CASH", 60m), Tender("CARD", 40m));
        var voidId = Guid.NewGuid();

        var voided = await PostAsync(s => s.VoidSaleAsync(_desk.Org, sale.SalesInvoiceUuid, voidId, "Customer changed mind", Receivables.User));
        var again  = await PostAsync(s => s.VoidSaleAsync(_desk.Org, sale.SalesInvoiceUuid, voidId, "Customer changed mind", Receivables.User));

        again.CreditNoteUuid.Should().Be(voided.CreditNoteUuid);
        voided.Total.Should().Be(100m);

        var books = await BooksAsync();
        books.Payments.Should().OnlyContain(p => p.Status == "REVERSED" && p.Allocations.Count == 0);
        var original = books.Invoice(sale.SalesInvoiceUuid);
        original.AmountPaid.Should().Be(0m);
        original.CreditedAmount.Should().Be(100m);
        original.ReturnedAmount.Should().Be(100m);
        original.BalanceDue.Should().Be(0m);
        books.Ledger.Select(e => e.EntryType).Should().Equal("INVOICE", "PAYMENT", "PAYMENT", "PAYMENT", "PAYMENT", "CREDIT_NOTE");
        books.Ledger.Count(e => e.EntryType == "CREDIT_NOTE").Should().Be(1);
        (await _desk.ProductLedgerAsync(v)).Last().RunningQty.Should().Be(10m, "the goods are back");

        await AssertBooksAsync(owes: 0m);
        (await AgingAsync()).Should().Be(0m);
    }

    [Fact]
    public async Task A_sale_that_has_a_return_can_no_longer_be_voided()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 2m, 50m)], Cash(100m));
        await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 50m)], [Tender("CASH", 50m)]);

        var act = () => PostAsync(s => s.VoidSaleAsync(_desk.Org, sale.SalesInvoiceUuid, Guid.NewGuid(), "Oops", Receivables.User));

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*already has a return*");
        (await BooksAsync()).Payments.Should().OnlyContain(p => p.Status == "RECEIVED");
    }

    [Fact]
    public async Task A_pos_invoice_cannot_be_cancelled_in_the_back_office()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 1m, 50m)], [Tender("ON_ACCOUNT", 50m)]);

        await using var scope = _desk.Scope();
        var act = () => scope.Invoices.CancelAsync(sale.SalesInvoiceUuid, "wrong", Receivables.User);

        await act.Should().ThrowAsync<ConflictException>().WithMessage("*POS sale*");
    }

    // ── Reports ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sales_reports_count_pos_sales_at_their_net_and_take_credit_notes_off()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 2m, 50m, discount: 10m, tax: 13.5m)], Cash(103.5m));   // net 90
        await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 45m, 6.75m)], [Tender("CASH", 51.75m)]);

        var reports = SalesReports();

        var byCustomer = await reports.GetSalesByCustomerAsync(new SalesByCustomerFilter());
        byCustomer.Items.Should().ContainSingle().Which.Should().Match<SalesByCustomerItem>(i =>
            i.PartnerId == _customer && i.Revenue == 45m && i.OrderCount == 1 && i.InvoiceCount == 1);

        var byProduct = await reports.GetSalesByProductAsync(new SalesByProductFilter());
        byProduct.Items.Should().ContainSingle().Which.Should().Match<SalesByProductItem>(i => i.Revenue == 45m && i.QuantitySold == 1m);

        var vsPurchase = await reports.GetSalesVsPurchaseAsync(new SalesVsPurchaseFilter { Period = "DAY" });
        vsPurchase.Items.Should().ContainSingle().Which.Should().Match<SalesVsPurchaseItem>(i =>
            i.Revenue == 45m && i.CostOfGoodsSold == 20m && i.InvoiceCount == 1);
    }

    [Fact]
    public async Task Product_profitability_nets_a_partial_pos_return_out_of_revenue_and_cost()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 2m, 50m, discount: 10m, tax: 13.5m)], Cash(103.5m));   // net 90, cost 40
        await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 45m, 6.75m)], [Tender("CASH", 51.75m)]);  // net 45, cost 20 back

        await using var scope = _desk.Scope();
        var report = await new ProductLedgerQueryService(scope.Db, _world.Variants).GetProfitabilityAsync(new SMS.Modules.Finance.Models.ProductProfitabilityFilter());

        report.Data.Should().ContainSingle().Which.Should().Match<SMS.Modules.Finance.Models.ProductProfitabilityItemModel>(r =>
            r.QuantitySold == 1m && r.Revenue == 45m && r.CostOfGoodsSold == 20m && r.GrossProfit == 25m);
    }

    [Fact]
    public async Task Product_profitability_of_sale_order_invoices_is_unchanged_by_pos_activity_elsewhere()
    {
        var order = await _desk.PlaceOrderAsync(_customer, new ReceivablesDesk.OrderLine(3m, 40m, Discount: 10m, Cost: 15m));
        await _desk.IssueDeliveryAsync(_desk.Deliver(order, "DELIVERED", (0, 3m)));
        var product = _world.ProductOf(order.VariantUuids[0]);

        async Task<(decimal Qty, decimal Revenue, decimal Cost, decimal Profit)> Row()
        {
            await using var scope = _desk.Scope();
            var r = (await new ProductLedgerQueryService(scope.Db, _world.Variants)
                    .GetProfitabilityAsync(new SMS.Modules.Finance.Models.ProductProfitabilityFilter())).Data
                .Single(x => x.ProductUuid == product);
            return (r.QuantitySold, r.Revenue, r.CostOfGoodsSold, r.GrossProfit);
        }

        var before = await Row();
        before.Should().Be((3m, 108m, 45m, 63m));

        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 2m, 50m)], Cash(100m));
        await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 50m)], [Tender("CASH", 50m)]);

        (await Row()).Should().Be(before);
    }

    [Fact]
    public async Task The_r7_margin_report_reads_sale_orders_only_so_pos_sales_and_returns_leave_it_as_it_was()
    {
        await _desk.PlaceOrderAsync(_customer, new ReceivablesDesk.OrderLine(2m, 40m));

        async Task<string> Snapshot()
        {
            await using var scope = _desk.Scope();
            var lookups = new Mock<ILookupsService>();
            lookups.Setup(l => l.GetCurrencies()).Returns([new CurrencyModel { Id = ReceivablesWorld.PkrId, Name = "Pakistani Rupee", Code = "PKR" }]);
            var templates = new Mock<IPoDocumentTemplateService>();
            templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);
            var report = await new MarginAnalysisReportService(scope.Demand, Mock.Of<ISupplierNameLookupService>(), lookups.Object,
                _world.Variants, templates.Object, _world.Clock).GetMarginAnalysisAsync(new MarginAnalysisFilter());
            return System.Text.Json.JsonSerializer.Serialize(new { report.Totals, report.Items, report.TotalRecords });
        }

        var before = await Snapshot();

        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 2m, 50m, discount: 10m, tax: 13.5m)], Cash(103.5m));
        await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 45m, 6.75m)], [Tender("CASH", 51.75m)]);

        (await Snapshot()).Should().Be(before);
    }

    // ── A39 (XFIN): store credit, exchange, restocking fee ───────────────────

    [Fact]
    public async Task A_sale_paid_partly_by_store_credit_records_a_store_credit_payment_allocated_like_cash()
    {
        var v = await StockedVariantAsync(10m, 20m);

        var sale = await SaleAsync([Line(v, 2m, 50m)], Tender("STORE_CREDIT", 70m, "CN-S1-20260920-0001"), Tender("CASH", 30m));

        var books = await BooksAsync();
        books.Invoice(sale.SalesInvoiceUuid).Should().Match<SalesInvoice>(i => i.Status == "PAID" && i.AmountPaid == 100m && i.BalanceDue == 0m);
        var credit = books.Payment(sale.PaymentUuids[0]);
        credit.PaymentMethod.Should().Be("STORE_CREDIT");
        credit.BankReference.Should().Be("CN-S1-20260920-0001");
        credit.ChequeNumber.Should().BeNull();
        credit.Allocations.Should().ContainSingle().Which.AllocatedAmount.Should().Be(70m);
        books.Payment(sale.PaymentUuids[1]).PaymentMethod.Should().Be("CASH");
        books.Ledger.Select(e => (e.EntryType, e.DebitAmount, e.CreditAmount)).Should().Equal(
            ("INVOICE", 100m, 0m), ("PAYMENT", 0m, 70m), ("PAYMENT", 0m, 30m));

        await AssertBooksAsync(owes: 0m);
        (await AgingAsync()).Should().Be(0m);
    }

    [Fact]
    public async Task A_return_refunded_as_store_credit_is_a_credit_note_with_a_store_credit_refund()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 2m, 50m)], Cash(100m));

        var ret = await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 50m)], [Tender("STORE_CREDIT", 50m, "CN-S1-20260920-0002")]);

        var books = await BooksAsync();
        books.Refunds.Should().ContainSingle().Which.Should().Match<CustomerRefund>(r =>
            r.UUID == ret.RefundUuids[0] && r.Method == "STORE_CREDIT" && r.Amount == 50m && r.Reference == "CN-S1-20260920-0002");
        books.Invoice(ret.CreditNoteUuid).CreditedAmount.Should().Be(0m, "store credit is paid out, not applied to the paid invoice");
        books.Ledger.Select(e => e.EntryType).Should().Equal("INVOICE", "PAYMENT", "CREDIT_NOTE", "REFUND");
        (await _desk.ProductLedgerAsync(v)).Last().Should().Match<ProductLedgerEntry>(e => e.EntryType == "RETURN_IN" && e.Quantity == 1m);

        await AssertBooksAsync(owes: 0m);
    }

    [Fact]
    public async Task A_return_with_a_restocking_fee_credits_the_scaled_amounts_and_takes_the_full_quantity_back_at_cost()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var sale = await SaleAsync([Line(v, 2m, 50m, tax: 15m)], Cash(115m));   // 100 net + 15 tax

        // D-5: return both units (115) with a 10% fee (11.50) → refund 103.50; lines scaled by 0.9: net 90, tax 13.50.
        var ret = await ReturnAsync(sale, [ReturnLine(sale, 0, 2m, 90m, 13.5m)], [Tender("CASH", 103.5m)]);

        ret.Total.Should().Be(103.5m);
        var books = await BooksAsync();
        var credit = books.Invoice(ret.CreditNoteUuid);
        credit.Should().Match<SalesInvoice>(c => c.GrandTotal == 103.5m && c.TaxAmount == 13.5m && c.Subtotal == 90m);
        books.Invoice(sale.SalesInvoiceUuid).ReturnedAmount.Should().Be(103.5m);

        var back = (await _desk.ProductLedgerAsync(v)).Last();
        back.Should().Match<ProductLedgerEntry>(e => e.EntryType == "RETURN_IN" && e.Quantity == 2m && e.UnitCost == 20m && e.RunningQty == 10m);

        // Every unit is back: nothing more can come back, and the fee stays as revenue (net 100 − 90 credited = 10).
        var more = () => ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 1m)], [Tender("CASH", 1m)]);
        (await more.Should().ThrowAsync<BadRequestException>()).WithMessage("Return quantity cannot exceed the original quantity.");

        var byProduct = await SalesReports().GetSalesByProductAsync(new SalesByProductFilter());
        byProduct.Items.Should().ContainSingle().Which.Revenue.Should().Be(10m);

        await AssertBooksAsync(owes: 0m);
    }

    [Fact]
    public async Task An_exchange_return_then_a_sale_paid_partly_by_the_exchange_value_leaves_the_customer_ledger_balanced()
    {
        var v = await StockedVariantAsync(10m, 20m);
        var w = await StockedVariantAsync(10m, 30m);
        var sale = await SaleAsync([Line(v, 1m, 80m)], Cash(80m));

        // D-6: return part (80, fee 0) refunded by tender EXCHANGE; the new part (120) paid 80 EXCHANGE + 40 cash.
        var ret = await ReturnAsync(sale, [ReturnLine(sale, 0, 1m, 80m)], [Tender("EXCHANGE", 80m)]);
        var next = await SaleAsync([Line(w, 1m, 120m)], Tender("EXCHANGE", 80m), Tender("CASH", 40m));

        var books = await BooksAsync();
        books.Refunds.Should().ContainSingle(r => r.UUID == ret.RefundUuids[0]).Which.Method.Should().Be("EXCHANGE");
        books.Payment(next.PaymentUuids[0]).PaymentMethod.Should().Be("EXCHANGE");
        books.Invoice(next.SalesInvoiceUuid).Status.Should().Be("PAID");

        var exchangeIn  = books.Refunds.Where(r => r.Method == "EXCHANGE").Sum(r => r.Amount);
        var exchangeOut = books.Payments.Where(p => p.PaymentMethod == "EXCHANGE").Sum(p => p.Amount);
        exchangeIn.Should().Be(exchangeOut, "the exchange value carried across nets to zero");
        books.Ledger.Select(e => (e.EntryType, e.DebitAmount, e.CreditAmount)).Should().Equal(
            ("INVOICE", 80m, 0m), ("PAYMENT", 0m, 80m), ("CREDIT_NOTE", 0m, 80m), ("REFUND", 80m, 0m),
            ("INVOICE", 120m, 0m), ("PAYMENT", 0m, 80m), ("PAYMENT", 0m, 40m));

        await AssertBooksAsync(owes: 0m);
        (await AgingAsync()).Should().Be(0m);
    }

    [Fact]
    public async Task Store_credit_and_exchange_are_not_cash_the_back_office_cannot_key_them_and_a_cash_filter_leaves_them_out()
    {
        var v = await StockedVariantAsync(10m, 20m);
        await SaleAsync([Line(v, 1m, 100m)], Tender("STORE_CREDIT", 60m), Tender("EXCHANGE", 25m), Tender("CASH", 15m));

        await using var scope = _desk.Scope();

        var cash = await scope.Payments.ListAsync(new SMS.Modules.Finance.Models.CustomerPaymentFilter { Method = "CASH" });
        cash.Data.Should().ContainSingle().Which.Amount.Should().Be(15m);
        (await scope.Payments.ListAsync(new SMS.Modules.Finance.Models.CustomerPaymentFilter { Method = "STORE_CREDIT" })).Data
            .Should().ContainSingle().Which.Amount.Should().Be(60m);
        (await scope.Payments.ListAsync(new SMS.Modules.Finance.Models.CustomerPaymentFilter { Method = "EXCHANGE" })).Data
            .Should().ContainSingle().Which.Amount.Should().Be(25m);

        foreach (var method in new[] { "STORE_CREDIT", "exchange" })
        {
            var keyed = () => scope.Payments.RecordPaymentAsync(_customer, 10m, method, new CustomerPaymentDetails("PKR"), Receivables.User);
            await keyed.Should().ThrowAsync<BadRequestException>().WithMessage("*recorded by the POS only*");
        }
    }

    // ══ Harness ══════════════════════════════════════════════════════════════

    private static CounterSaleLine Line(Guid variant, decimal qty, decimal price, decimal discount = 0m, decimal tax = 0m)
    {
        var net = Math.Round(qty * price, 2) - discount;
        return new CounterSaleLine(Guid.NewGuid(), variant, "Counter item", qty, price, discount, net, null, tax == 0m ? null : "GST",
            tax == 0m ? 0m : 15m, tax, net + tax);
    }

    private static CounterSaleTender Tender(string method, decimal amount, string? reference = null) =>
        new(Guid.NewGuid(), method, amount, reference);

    private static CounterSaleTender[] Cash(decimal amount) => [Tender("CASH", amount)];

    private CounterSaleRequest SaleRequest(IReadOnlyList<CounterSaleLine> lines, IReadOnlyList<CounterSaleTender> tenders) =>
        new(_desk.Org, Guid.NewGuid(), $"POS-S1-20260920-{++_seq:0000}", _customer, _world.Clock.Value, lines, tenders, Receivables.User);

    private readonly Dictionary<Guid, (CounterSaleRequest Request, CounterSaleResult Result)> _sales = [];

    private Task<CounterSaleResult> SaleAsync(IReadOnlyList<CounterSaleLine> lines, params CounterSaleTender[] tenders) =>
        SaleAsync(lines, tenders, terms: null);

    private async Task<CounterSaleResult> SaleAsync(IReadOnlyList<CounterSaleLine> lines, CounterSaleTender[] tenders, int? terms)
    {
        var request = SaleRequest(lines, tenders);
        var result  = await PostAsync(s => s.PostSaleAsync(request), terms);
        _sales[result.SalesInvoiceUuid] = (request, result);
        return result;
    }

    private string SourceNumberOf(CounterSaleResult sale) => _sales[sale.SalesInvoiceUuid].Request.SourceNumber;

    private CounterReturnLine ReturnLine(CounterSaleResult sale, int index, decimal qty, decimal net, decimal tax = 0m) =>
        new(_sales[sale.SalesInvoiceUuid].Request.Lines[index].SourceLineUuid, Guid.NewGuid(), qty, net, tax, net + tax);

    private CounterReturnRequest ReturnRequest(CounterSaleResult sale, IReadOnlyList<CounterReturnLine> lines, IReadOnlyList<CounterSaleTender> refunds) =>
        new(_desk.Org, Guid.NewGuid(), $"RET-S1-20260920-{++_seq:0000}", sale.SalesInvoiceUuid, _world.Clock.Value, lines, refunds, "Faulty", Receivables.User);

    private Task<CounterReturnResult> ReturnAsync(CounterSaleResult sale, IReadOnlyList<CounterReturnLine> lines, IReadOnlyList<CounterSaleTender> refunds) =>
        PostAsync(s => s.PostReturnAsync(ReturnRequest(sale, lines, refunds)));

    private async Task<Guid> StockedVariantAsync(decimal qty, decimal cost)
    {
        var v = Guid.NewGuid();
        await _desk.StockAsync(v, qty, cost);
        return v;
    }

    /// <summary>One request's scope: the real services, the counter-sale service on top, sale base PKR.</summary>
    private async Task<T> PostAsync<T>(Func<CounterSaleService, Task<T>> call, int? terms = null)
    {
        await using var scope = _desk.Scope();

        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => _world.NamesFor(_desk.Org, ids));

        var termsLookup = new Mock<ICustomerPaymentTermsLookup>();
        termsLookup.Setup(t => t.GetPaymentTermsDaysAsync(_desk.Org, _customer, It.IsAny<CancellationToken>())).ReturnsAsync(terms);

        var service = new CounterSaleService(
            scope.Db, scope.Invoices, scope.Payments, scope.Ledger, scope.ProductLedger, names.Object,
            new FakeCurrencyService(ReceivablesWorld.PkrId, "PKR"), NullLogger<CounterSaleService>.Instance, _world.Clock,
            termsLookup.Object);

        return await call(service);
    }

    private async Task<PosBooks> BooksAsync()
    {
        await using var scope = _desk.Scope();
        var db = scope.Db;
        return new PosBooks(
            await db.SalesInvoices.AsNoTracking().Where(i => i.PartnerId == _customer).OrderBy(i => i.Id).ToListAsync(),
            await db.CustomerPayments.AsNoTracking().Include(p => p.Allocations).Where(p => p.PartnerId == _customer).OrderBy(p => p.Id).ToListAsync(),
            await db.CustomerRefunds.AsNoTracking().Where(r => r.PartnerId == _customer).OrderBy(r => r.Id).ToListAsync(),
            await db.CustomerLedgerEntries.AsNoTracking().Where(e => e.PartnerId == _customer).OrderBy(e => e.SequenceNo).ToListAsync());
    }

    /// <summary>The books agree with themselves, the ledger says the customer owes <paramref name="owes"/>, and so does the
    /// balance lookup's read (open invoices' balance due — the query <c>SalesInvoiceCustomerBalanceLookup</c> makes).</summary>
    private async Task AssertBooksAsync(decimal owes)
    {
        var books = await BooksAsync();
        books.Violations().Should().BeEmpty();
        (books.Ledger.LastOrDefault()?.RunningBalance ?? 0m).Should().Be(owes);
        books.Invoices.Where(i => i.Status is "ISSUED" or "PARTIALLY_PAID" or "OVERDUE").Sum(i => i.BalanceDue).Should().Be(owes);
    }

    private async Task<decimal> AgingAsync()
    {
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => _world.NamesFor(_desk.Org, ids));
        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);

        var report = await new ReceivablesReportService(Receivables.Db(_desk.Org, _world.DbName), names.Object, templates.Object, _world.Clock)
            .GetAgingReceivablesAsync(new AgingReceivablesFilter { PartnerId = _customer });
        return report.Invoices.Sum(i => i.Outstanding);
    }

    private SalesAnalysisReportService SalesReports()
    {
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => _world.NamesFor(_desk.Org, ids));
        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);
        return new SalesAnalysisReportService(Receivables.Db(_desk.Org, _world.DbName), names.Object, _world.Variants, templates.Object, _world.Clock);
    }
}

/// <summary>A customer's receivables including A38 credit notes and refunds, and what must hold of them.</summary>
internal sealed record PosBooks(
    IReadOnlyList<SalesInvoice> Invoices, IReadOnlyList<CustomerPayment> Payments,
    IReadOnlyList<CustomerRefund> Refunds, IReadOnlyList<CustomerLedgerEntry> Ledger)
{
    public SalesInvoice Invoice(Guid uuid) => Invoices.Single(i => i.UUID == uuid);
    public CustomerPayment Payment(Guid uuid) => Payments.Single(p => p.UUID == uuid);

    public string Fingerprint() => string.Join("|",
        Invoices.Select(i => $"I:{i.UUID}:{i.Status}:{i.AmountPaid}:{i.BalanceDue}:{i.ReturnedAmount}:{i.CreditedAmount}")
            .Concat(Payments.Select(p => $"P:{p.UUID}:{p.Status}:{p.Allocations.Count}"))
            .Concat(Refunds.Select(r => $"R:{r.UUID}"))
            .Concat(Ledger.Select(e => $"L:{e.SequenceNo}:{e.DebitAmount}:{e.CreditAmount}:{e.RunningBalance}")));

    public IReadOnlyList<string> Violations()
    {
        var bad = new List<string>();

        decimal running = 0m;
        var seq = 1;
        foreach (var e in Ledger)
        {
            if (e.SequenceNo != seq++) bad.Add($"ledger sequence {e.SequenceNo} out of order");
            if ((e.DebitAmount > 0m) == (e.CreditAmount > 0m)) bad.Add($"ledger entry {e.SequenceNo} is not one of debit / credit");
            running += e.DebitAmount - e.CreditAmount;
            if (e.RunningBalance != running) bad.Add($"ledger entry {e.SequenceNo} balance {e.RunningBalance} ≠ {running}");
        }

        var invoices = Invoices.Where(i => i.Status is not ("DRAFT" or "CANCELLED" or "CREDIT_NOTE")).ToList();
        var credits  = Invoices.Where(i => i.Status == "CREDIT_NOTE").ToList();
        var received = Payments.Where(p => p.Status == "RECEIVED").ToList();

        var expected = invoices.Sum(i => i.GrandTotal) - received.Sum(p => p.Amount) - credits.Sum(c => c.GrandTotal) + Refunds.Sum(r => r.Amount);
        if (running != expected) bad.Add($"ledger balance {running} ≠ billed − received − credited + refunded = {expected}");

        var unapplied = received.Sum(p => p.Amount - p.Allocations.Sum(a => a.AllocatedAmount))
                      + credits.Sum(c => c.GrandTotal - c.CreditedAmount - Refunds.Where(r => r.CreditNoteUuid == c.UUID).Sum(r => r.Amount));
        if (running != invoices.Sum(i => i.BalanceDue) - unapplied)
            bad.Add($"ledger balance {running} ≠ open balances {invoices.Sum(i => i.BalanceDue)} − unapplied {unapplied}");

        var live = received.SelectMany(p => p.Allocations).ToList();
        foreach (var i in invoices)
        {
            var paid = live.Where(a => a.SalesInvoiceId == i.Id).Sum(a => a.AllocatedAmount);
            if (i.AmountPaid != paid) bad.Add($"{i.InvoiceNumber}: paid {i.AmountPaid} ≠ allocations {paid}");
            if (i.BalanceDue != i.GrandTotal - i.AmountPaid - i.CreditedAmount) bad.Add($"{i.InvoiceNumber}: balance {i.BalanceDue} is not total − paid − credited");
            var mine = credits.Where(c => c.OriginalInvoiceUuid == i.UUID).ToList();
            if (i.ReturnedAmount != mine.Sum(c => c.GrandTotal)) bad.Add($"{i.InvoiceNumber}: returned {i.ReturnedAmount} ≠ its credit notes");
            if (i.CreditedAmount != mine.Sum(c => c.CreditedAmount)) bad.Add($"{i.InvoiceNumber}: credited {i.CreditedAmount} ≠ its credit notes' applied part");
            if ((i.BalanceDue == 0m) != (i.Status == "PAID")) bad.Add($"{i.InvoiceNumber} is {i.Status} with {i.BalanceDue} owing");
        }

        foreach (var c in credits)
        {
            if (c.BalanceDue != 0m || c.AmountPaid != 0m) bad.Add($"{c.InvoiceNumber}: a credit note carries no balance");
            if (Ledger.Count(e => e.ReferenceId == c.UUID && e.EntryType == "CREDIT_NOTE" && e.CreditAmount == c.GrandTotal) != 1)
                bad.Add($"{c.InvoiceNumber}: no single CREDIT_NOTE ledger credit");
        }

        foreach (var r in Refunds)
            if (Ledger.Count(e => e.ReferenceId == r.UUID && e.EntryType == "REFUND" && e.DebitAmount == r.Amount) != 1)
                bad.Add($"{r.RefundNumber}: no single REFUND ledger debit");

        foreach (var p in Payments.Where(p => p.Status == "REVERSED"))
            if (Ledger.Count(e => e.ReferenceId == p.UUID && e.EntryType == "PAYMENT" && e.DebitAmount == p.Amount) != 1)
                bad.Add($"{p.PaymentNumber}: reversed without its offsetting debit");

        return bad;
    }
}
