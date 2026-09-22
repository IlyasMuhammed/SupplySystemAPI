using FluentAssertions;
using Moq;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P9-04 §15 R4 and R5 against the books the <i>real</i> invoice service writes. The reports' own tests
/// build their invoices by hand, which shows what the reports decide; these show that what they decide is
/// what the services did, and that R4, R5 and the product profitability report of P8-05 — three readers of
/// the same invoices — say the same things. Two customers buy over a fortnight: one order billed in two
/// invoices, a discount, tax, two variants of one product, and a draft that is never issued.
/// </summary>
public class SalesAnalysisReconciliationTests
{
    private static DateTime At(int day) => new(2026, 9, day, 10, 30, 0, DateTimeKind.Utc);

    private static DateTime Sept(int day) => new(2026, 9, day);

    private sealed record Fortnight(
        ReceivablesWorld World, ReceivablesDesk Desk, Guid Acme, Guid Globex, Guid Laptop, Guid Mouse, Guid Dock, Books AcmeBooks, Books GlobexBooks);

    private static async Task<Fortnight> LiveAsync()
    {
        var world  = new ReceivablesWorld();
        var desk   = world.For(Guid.NewGuid());
        var acme   = desk.NewCustomer("Acme Ltd");
        var globex = desk.NewCustomer("Globex Corp");

        var (laptopBlack, laptop) = world.Variants.New();
        var (laptopSilver, _)     = world.Variants.New(laptop);
        var (mouseVariant, mouse) = world.Variants.New();
        var (dockVariant, dock)   = world.Variants.New();
        world.Variants.ProductNames[laptop] = "Laptop";
        world.Variants.ProductNames[mouse]  = "Mouse";
        world.Variants.ProductNames[dock]   = "Dock";

        // 2 Sept: Acme takes ten laptops at 40 less 10% with 17% tax, and three mice, in one delivery.
        world.Clock.Value = At(2);
        var orderA = await desk.PlaceOrderAsync(acme,
            new ReceivablesDesk.OrderLine(10m, 40m, Discount: 10m, Tax: 17m, Variant: laptopBlack),
            new ReceivablesDesk.OrderLine(3m, 25.5m, Variant: mouseVariant));
        await desk.IssueDeliveryAsync(desk.Deliver(orderA, "DELIVERED", (0, 10m), (1, 3m)));

        // 3 Sept: Globex takes a silver laptop, mice and a dock.
        world.Clock.Value = At(3);
        var orderG = await desk.PlaceOrderAsync(globex,
            new ReceivablesDesk.OrderLine(2m, 1200.5m, Discount: 5m, Variant: laptopSilver),
            new ReceivablesDesk.OrderLine(7m, 25.5m, Discount: 2.5m, Variant: mouseVariant),
            new ReceivablesDesk.OrderLine(1m, 300m, Tax: 17m, Variant: dockVariant));
        await desk.IssueDeliveryAsync(desk.Deliver(orderG, "DELIVERED", (0, 2m), (1, 7m), (2, 1m)));

        // 4 Sept: Acme's second order is delivered in two parts, and billed in two invoices.
        world.Clock.Value = At(4);
        var orderB = await desk.PlaceOrderAsync(acme, new ReceivablesDesk.OrderLine(10m, 15.25m, Variant: mouseVariant));
        await desk.IssueDeliveryAsync(desk.Deliver(orderB, "DELIVERED", (0, 4m)));
        await desk.IssueDeliveryAsync(desk.Deliver(orderB, "DELIVERED", (0, 6m)));

        // 5 Sept: a draft for Globex, raised and never issued.
        world.Clock.Value = At(5);
        var orderDraft = await desk.PlaceOrderAsync(globex, new ReceivablesDesk.OrderLine(1m, 999m, Variant: dockVariant));
        await using (var scope = desk.Scope())
            await scope.Invoices.CreateFromFulfillmentAsync(desk.Deliver(orderDraft, "DELIVERED", (0, 1m)), ReceivablesDesk.User);

        // 12 Sept: five more laptops for Acme, after the period the tests look at first.
        world.Clock.Value = At(12);
        var orderLate = await desk.PlaceOrderAsync(acme, new ReceivablesDesk.OrderLine(5m, 40m, Variant: laptopBlack));
        await desk.IssueDeliveryAsync(desk.Deliver(orderLate, "DELIVERED", (0, 5m)));

        return new Fortnight(world, desk, acme, globex, laptop, mouse, dock, await desk.BooksAsync(acme), await desk.BooksAsync(globex));
    }

    private static SalesAnalysisReportService ReportsFor(Fortnight f)
    {
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => f.World.NamesFor(f.Desk.Org, ids));

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);

        return new SalesAnalysisReportService(Receivables.Db(f.Desk.Org, f.World.DbName), names.Object, f.World.Variants, templates.Object, f.World.Clock);
    }

    /// <summary>The invoices that stand and were issued in the range, worked out from the committed rows alone.</summary>
    private static List<SalesInvoice> Standing(Fortnight f, int fromDay, int toDay)
    {
        var books = new[] { f.AcmeBooks, f.GlobexBooks };
        return [.. books.SelectMany(b => b.Invoices.Where(i => i.Status != SalesInvoiceStatuses.Draft && i.Status != SalesInvoiceStatuses.Cancelled
            && b.Ledger.Any(e => e.EntryType == "INVOICE" && e.ReferenceId == i.UUID && e.EntryDate.Date >= Sept(fromDay) && e.EntryDate.Date <= Sept(toDay))))];
    }

    [Fact]
    public async Task The_fortnight_is_what_the_scenario_claims_so_the_comparisons_below_mean_something()
    {
        var f = await LiveAsync();

        f.AcmeBooks.Invoices.Should().HaveCount(4, "two orders in one, the second billed twice, and a late one");
        f.GlobexBooks.Invoices.Should().HaveCount(2).And.Contain(i => i.Status == SalesInvoiceStatuses.Draft);
        f.AcmeBooks.Invoices.Select(i => i.SaleOrderUuid).Distinct().Should().HaveCount(3);
        Standing(f, 1, 10).Should().HaveCount(4);
    }

    // ── R5 sales by customer ─────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 10)]
    [InlineData(1, 30)]
    [InlineData(3, 4)]
    [InlineData(12, 12)]
    public async Task Each_customers_revenue_is_the_sum_of_what_their_real_invoices_printed_before_tax(int fromDay, int toDay)
    {
        var f = await LiveAsync();

        var report = await ReportsFor(f).GetSalesByCustomerForExportAsync(new SalesByCustomerFilter { DateFrom = Sept(fromDay), DateTo = Sept(toDay) });

        var invoices = Standing(f, fromDay, toDay);
        foreach (var customer in new[] { f.Acme, f.Globex })
        {
            var mine = invoices.Where(i => i.PartnerId == customer).ToList();
            var row  = report.Items.SingleOrDefault(r => r.PartnerId == customer);

            if (mine.Count == 0) { row.Should().BeNull(); continue; }

            row!.Revenue.Should().Be(mine.Sum(i => i.Subtotal - i.DiscountAmount));
            row.InvoiceCount.Should().Be(mine.Count);
            row.OrderCount.Should().Be(mine.Select(i => i.SaleOrderUuid).Distinct().Count());
            row.AverageOrderValue.Should().Be(Math.Round(row.Revenue / row.OrderCount, 2, MidpointRounding.AwayFromZero));
        }

        report.Totals.Single().Revenue.Should().Be(invoices.Sum(i => i.Subtotal - i.DiscountAmount));
        report.Items.Should().BeInDescendingOrder(r => r.Revenue);
    }

    [Fact]
    public async Task An_order_the_service_billed_in_two_invoices_is_one_order_in_the_report_and_a_draft_is_no_sale()
    {
        var f = await LiveAsync();

        var acme = (await ReportsFor(f).GetSalesByCustomerForExportAsync(new SalesByCustomerFilter { DateTo = Sept(10) })).Items.Single(r => r.PartnerId == f.Acme);

        (acme.OrderCount, acme.InvoiceCount).Should().Be((2, 3));
        var globex = (await ReportsFor(f).GetSalesByCustomerForExportAsync(new SalesByCustomerFilter())).Items.Single(r => r.PartnerId == f.Globex);
        (globex.OrderCount, globex.InvoiceCount).Should().Be((1, 1), "the draft raised on the 5th was never issued");
    }

    // ── R4 sales by product ──────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 10)]
    [InlineData(1, 30)]
    [InlineData(2, 2)]
    public async Task Each_products_units_and_revenue_are_what_the_real_invoice_lines_come_to_by_product(int fromDay, int toDay)
    {
        var f = await LiveAsync();

        var report = await ReportsFor(f).GetSalesByProductForExportAsync(new SalesByProductFilter { DateFrom = Sept(fromDay), DateTo = Sept(toDay) });

        await using var scope = f.Desk.Scope();
        var standing = Standing(f, fromDay, toDay).Select(i => i.UUID).ToHashSet();
        var lines = scope.Db.SalesInvoiceLines.Where(l => standing.Contains(l.SalesInvoice.UUID)).ToList();

        foreach (var group in lines.GroupBy(l => f.World.ProductOf(l.VariantUuid)))
        {
            var row = report.Items.Single(r => r.ProductUuid == group.Key);
            row.QuantitySold.Should().Be(group.Sum(l => l.Quantity));
            row.Revenue.Should().Be(
                Math.Round(group.Sum(l => l.Quantity * l.UnitPrice), 2, MidpointRounding.AwayFromZero)
                - Math.Round(group.Sum(l => l.Quantity * l.UnitPrice * l.DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero));
        }

        report.Items.Should().HaveCount(lines.GroupBy(l => f.World.ProductOf(l.VariantUuid)).Count());
    }

    [Fact]
    public async Task Two_variants_of_the_laptop_are_one_product_named_by_the_lookup()
    {
        var f = await LiveAsync();

        var report = await ReportsFor(f).GetSalesByProductForExportAsync(new SalesByProductFilter { DateTo = Sept(10) });

        var laptop = report.Items.Single(r => r.ProductUuid == f.Laptop);
        laptop.ProductName.Should().Be("Laptop");
        laptop.QuantitySold.Should().Be(12m, "ten black and two silver");
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(1, 30)]
    public async Task R4_agrees_with_the_product_profitability_report_on_every_products_revenue_and_units(int fromDay, int toDay)
    {
        var f = await LiveAsync();

        var sales = await ReportsFor(f).GetSalesByProductForExportAsync(new SalesByProductFilter { DateFrom = Sept(fromDay), DateTo = Sept(toDay) });

        await using var scope = f.Desk.Scope();
        var profitability = await new ProductLedgerQueryService(scope.Db, f.World.Variants)
            .GetProfitabilityAsync(new ProductProfitabilityFilter { DateFrom = Sept(fromDay), DateTo = Sept(toDay), PageSize = 100 });

        profitability.Data.Should().NotBeEmpty();
        sales.Items.Select(i => (i.ProductUuid, i.CurrencyCode, i.QuantitySold, i.Revenue))
            .Should().BeEquivalentTo(profitability.Data.Select(p => (p.ProductUuid, p.CurrencyCode, p.QuantitySold, p.Revenue)));
        sales.Items.Select(i => i.ProductName).Should().BeEquivalentTo(profitability.Data.Select(p => p.ProductName));
    }

    // ── The two together ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 10)]
    [InlineData(1, 30)]
    public async Task What_R4_says_was_sold_and_what_R5_says_was_billed_are_the_same_money_to_within_header_rounding(int fromDay, int toDay)
    {
        var f = await LiveAsync();
        var reports = ReportsFor(f);

        var byProduct  = await reports.GetSalesByProductForExportAsync(new SalesByProductFilter { DateFrom = Sept(fromDay), DateTo = Sept(toDay) });
        var byCustomer = await reports.GetSalesByCustomerForExportAsync(new SalesByCustomerFilter { DateFrom = Sept(fromDay), DateTo = Sept(toDay) });

        // An invoice rounds its own header; a product rounds its own total. They differ by at most a cent a piece.
        var tolerance = 0.01m * (byProduct.Items.Count + Standing(f, fromDay, toDay).Count);
        Math.Abs(byProduct.Totals.Single().Revenue - byCustomer.Totals.Single().Revenue).Should().BeLessThanOrEqualTo(tolerance);
        byProduct.Totals.Single().Revenue.Should().BeGreaterThan(1000m);
    }

    // ── R8 sales vs purchase (A29-P9-05) ─────────────────────────────────────

    private static DateTime IssuedOn(Fortnight f, SalesInvoice invoice) =>
        new[] { f.AcmeBooks, f.GlobexBooks }.SelectMany(b => b.Ledger)
            .Single(e => e.EntryType == "INVOICE" && e.ReferenceId == invoice.UUID).EntryDate.Date;

    [Theory]
    [InlineData(1, 10)]
    [InlineData(1, 30)]
    [InlineData(3, 4)]
    public async Task Each_days_revenue_and_cost_are_what_the_real_invoices_and_the_ledger_entries_they_wrote_come_to(int fromDay, int toDay)
    {
        var f = await LiveAsync();

        var report = await ReportsFor(f).GetSalesVsPurchaseForExportAsync(new SalesVsPurchaseFilter { DateFrom = Sept(fromDay), DateTo = Sept(toDay), Period = "DAY" });

        var standing = Standing(f, fromDay, toDay);
        var ids = standing.Select(i => i.UUID).ToHashSet();
        await using var scope = f.Desk.Scope();
        var costs = scope.Db.ProductLedgerEntries.Where(e => e.EntryType == "SALE" && ids.Contains(e.ReferenceId)).ToList();

        costs.Should().NotBeEmpty("guards the comparison: issuing an invoice books its cost of sales");
        var days = standing.GroupBy(i => IssuedOn(f, i)).OrderBy(g => g.Key).ToList();
        report.Items.Select(i => i.PeriodStart).Should().Equal(days.Select(g => g.Key));

        foreach (var day in days)
        {
            var row = report.Items.Single(i => i.PeriodStart == day.Key);
            var uuids = day.Select(i => i.UUID).ToHashSet();

            row.InvoiceCount.Should().Be(day.Count());
            row.Revenue.Should().Be(day.Sum(i => i.Subtotal - i.DiscountAmount));
            row.CostOfGoodsSold.Should().Be(Math.Round(costs.Where(c => uuids.Contains(c.ReferenceId)).Sum(c => c.TotalCost), 2, MidpointRounding.AwayFromZero));
            row.GrossMargin.Should().Be(row.Revenue - row.CostOfGoodsSold);
        }

        report.Totals.Single().UncostedRevenue.Should().Be(0m, "every invoice the real service issued has its cost of sales booked");
    }

    [Fact]
    public async Task A_draft_that_was_never_issued_books_no_cost_and_is_in_no_period()
    {
        var f = await LiveAsync();

        var report = await ReportsFor(f).GetSalesVsPurchaseForExportAsync(new SalesVsPurchaseFilter { Period = "DAY" });

        report.Items.Should().NotContain(i => i.PeriodStart == Sept(5));
        report.Totals.Single().InvoiceCount.Should().Be(Standing(f, 1, 30).Count);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(1, 30)]
    public async Task R8_agrees_with_the_product_profitability_report_on_what_the_goods_cost_and_what_they_sold_for(int fromDay, int toDay)
    {
        var f = await LiveAsync();

        var vs = await ReportsFor(f).GetSalesVsPurchaseForExportAsync(new SalesVsPurchaseFilter { DateFrom = Sept(fromDay), DateTo = Sept(toDay) });

        await using var scope = f.Desk.Scope();
        var profitability = await new ProductLedgerQueryService(scope.Db, f.World.Variants)
            .GetProfitabilityAsync(new ProductProfitabilityFilter { DateFrom = Sept(fromDay), DateTo = Sept(toDay), PageSize = 100 });

        profitability.Data.Should().NotBeEmpty();
        var total = vs.Totals.Single();

        // Cost is the ledger's own figure in both, added product by product in one and period by period in the other.
        Math.Abs(total.CostOfGoodsSold - profitability.Data.Sum(p => p.CostOfGoodsSold)).Should().BeLessThanOrEqualTo(0.01m * (profitability.Data.Count + vs.Items.Count));
        Math.Abs(total.Revenue - profitability.Data.Sum(p => p.Revenue)).Should().BeLessThanOrEqualTo(0.01m * (profitability.Data.Count + vs.Items.Count));
        total.CostOfGoodsSold.Should().BeGreaterThan(0m);
    }
}
