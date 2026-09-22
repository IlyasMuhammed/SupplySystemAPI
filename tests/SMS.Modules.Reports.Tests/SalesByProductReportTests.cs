using FluentAssertions;
using Moq;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Reports.Tests.ReceivablesReportWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-04 §15 R4 — sales by product: units and revenue by product over the invoices that stand, issued
/// in a range of days, before tax, per currency.
/// </summary>
public class SalesByProductReportTests
{
    private readonly ReceivablesReportWorld _w = new();

    private static readonly Guid Laptop = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Mouse  = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid LaptopBlack = Guid.NewGuid();
    private static readonly Guid LaptopSilver = Guid.NewGuid();
    private static readonly Guid MouseVariant = Guid.NewGuid();

    public SalesByProductReportTests()
    {
        _w.Stock(LaptopBlack, Laptop, "Laptop");
        _w.Stock(LaptopSilver, Laptop, "Laptop");
        _w.Stock(MouseVariant, Mouse, "Mouse");
    }

    private static SalesByProductFilter On(DateTime? from = null, DateTime? to = null, Guid? partner = null, int page = 1, int pageSize = 20) =>
        new() { DateFrom = from, DateTo = to, PartnerId = partner, Page = page, PageSize = pageSize };

    private static ReceivablesReportWorld.SoldLine Line(Guid variant, decimal qty, decimal price, decimal discount = 0m, decimal tax = 0m) =>
        new(variant, qty, price, discount, tax);

    private SalesInvoice Sell(string number, DateTime issued, IReadOnlyList<ReceivablesReportWorld.SoldLine> lines, Guid? partner = null, string currency = "PKR", string status = "ISSUED") =>
        _w.Sale(partner ?? _w.Acme, number, issued, lines, currency, status);

    private SalesInvoice Sell(string number, DateTime issued, ReceivablesReportWorld.SoldLine lines, Guid? partner = null, string currency = "PKR", string status = "ISSUED") =>
        Sell(number, issued, [lines], partner, currency, status);

    // ── What a row says ──────────────────────────────────────────────────────

    [Fact]
    public async Task Revenue_is_the_goods_at_their_price_less_the_discount_and_before_tax()
    {
        // 10 at 40 is 400; 10% off is 360; 17% tax on that is not revenue.
        Sell("SINV-1", D(9, 5), lines: Line(LaptopBlack, 10m, 40m, discount: 10m, tax: 17m));

        var report = await _w.AnalysisService().GetSalesByProductAsync(On());

        var row = report.Items.Should().ContainSingle().Subject;
        (row.ProductUuid, row.ProductName, row.CurrencyCode, row.QuantitySold, row.Revenue, row.AverageUnitPrice)
            .Should().Be((Laptop, "Laptop", "PKR", 10m, 360m, 36m));
    }

    [Fact]
    public async Task Every_variant_of_a_product_is_one_product()
    {
        Sell("SINV-1", D(9, 5), lines: [Line(LaptopBlack, 2m, 1000m), Line(LaptopSilver, 3m, 1100m)]);

        var report = await _w.AnalysisService().GetSalesByProductAsync(On());

        var row = report.Items.Should().ContainSingle().Subject;
        (row.ProductUuid, row.QuantitySold, row.Revenue, row.AverageUnitPrice).Should().Be((Laptop, 5m, 5300m, 1060m));
    }

    [Fact]
    public async Task The_same_product_in_two_currencies_is_two_rows_and_two_totals()
    {
        Sell("SINV-PKR", D(9, 5), lines: Line(MouseVariant, 4m, 50m));
        Sell("SINV-USD", D(9, 6), currency: "USD", lines: Line(MouseVariant, 1m, 30m));

        var report = await _w.AnalysisService().GetSalesByProductAsync(On());

        report.Items.Select(i => (i.CurrencyCode, i.QuantitySold, i.Revenue)).Should().BeEquivalentTo(new[] { ("PKR", 4m, 200m), ("USD", 1m, 30m) });
        report.Totals.Select(t => (t.CurrencyCode, t.ProductCount, t.Revenue)).Should().Equal(("PKR", 1, 200m), ("USD", 1, 30m));
    }

    [Fact]
    public async Task Highest_revenue_first_and_the_totals_are_over_every_product_not_the_page()
    {
        Sell("SINV-1", D(9, 5), lines: [Line(MouseVariant, 10m, 10m), Line(LaptopBlack, 1m, 900m)]);
        Sell("SINV-2", D(9, 6), lines: Line(MouseVariant, 1m, 5m));

        var report = await _w.AnalysisService().GetSalesByProductAsync(On(page: 2, pageSize: 1));

        report.Items.Select(i => i.ProductName).Should().Equal("Mouse");
        (report.TotalRecords, report.TotalPages).Should().Be((2, 2));
        report.Totals.Single().Should().Match<SalesByProductTotal>(t => t.ProductCount == 2 && t.Revenue == 1005m);
    }

    [Fact]
    public async Task Equal_revenue_is_ordered_by_product_id_so_the_order_never_moves()
    {
        Sell("SINV-1", D(9, 5), lines: [Line(MouseVariant, 1m, 100m), Line(LaptopBlack, 1m, 100m)]);

        var report = await _w.AnalysisService().GetSalesByProductAsync(On());

        report.Items.Select(i => i.ProductUuid).Should().Equal(Laptop, Mouse);
    }

    [Fact]
    public async Task Revenue_is_rounded_once_on_the_products_total_as_an_invoice_rounds_its_header()
    {
        // Three lines of 0.335: 1.005 in all, which is 1.01. Rounding each line first would make 1.02.
        Sell("SINV-1", D(9, 5), lines: [Line(MouseVariant, 1m, 0.335m), Line(MouseVariant, 1m, 0.335m), Line(MouseVariant, 1m, 0.335m)]);

        (await _w.AnalysisService().GetSalesByProductAsync(On())).Items.Single().Revenue.Should().Be(1.01m);
    }

    [Fact]
    public async Task Revenue_is_the_rounded_gross_less_the_rounded_discount_each_to_the_cent_not_the_rounded_difference()
    {
        // 0.335 at 10% off: gross 0.335 is 0.34 and the discount 0.0335 is 0.03, so 0.31. The difference, 0.3015, would round to 0.30.
        Sell("SINV-1", D(9, 5), lines: Line(MouseVariant, 1m, 0.335m, discount: 10m));

        (await _w.AnalysisService().GetSalesByProductAsync(On())).Items.Single().Revenue.Should().Be(0.31m);
    }

    [Fact]
    public async Task A_product_with_nothing_sold_by_the_unit_has_no_average_price()
    {
        Sell("SINV-1", D(9, 5), lines: Line(MouseVariant, 0m, 50m));

        var row = (await _w.AnalysisService().GetSalesByProductAsync(On())).Items.Single();

        (row.QuantitySold, row.Revenue, row.AverageUnitPrice).Should().Be((0m, 0m, (decimal?)null));
    }

    // ── What counts as a sale ────────────────────────────────────────────────

    [Theory]
    [InlineData("DRAFT", false)]
    [InlineData("CANCELLED", false)]
    [InlineData("CREDIT_NOTE", false)]
    [InlineData("ISSUED", true)]
    [InlineData("PARTIALLY_PAID", true)]
    [InlineData("PAID", true)]
    [InlineData("OVERDUE", true)]
    public async Task Only_an_invoice_that_stands_is_a_sale(string status, bool counted)
    {
        Sell("SINV-1", D(9, 5), status: status, lines: Line(MouseVariant, 1m, 10m));

        (await _w.AnalysisService().GetSalesByProductAsync(On())).Items.Any().Should().Be(counted);
    }

    [Fact]
    public async Task An_invoice_never_issued_has_no_ledger_entry_and_is_no_sale_and_nor_is_a_deleted_one()
    {
        _w.Sale(_w.Acme, "SINV-UNBOOKED", D(9, 5), [Line(MouseVariant, 1m, 10m)], booked: false);
        _w.Sale(_w.Acme, "SINV-GONE", D(9, 5), [Line(MouseVariant, 1m, 10m)], deleted: true);

        (await _w.AnalysisService().GetSalesByProductAsync(On())).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sale_is_of_the_day_it_was_issued_not_the_day_the_draft_was_dated()
    {
        var invoice = _w.Sale(_w.Acme, "SINV-1", D(9, 5), [Line(MouseVariant, 1m, 10m)]);
        using (var db = _w.Db())
        {
            db.SalesInvoices.Single(i => i.Id == invoice.Id).InvoiceDate = D(9, 1);
            db.SaveChanges();
        }

        (await _w.AnalysisService().GetSalesByProductAsync(On(D(9, 1), D(9, 4)))).Items.Should().BeEmpty();
        (await _w.AnalysisService().GetSalesByProductAsync(On(D(9, 5), D(9, 5)))).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task The_range_is_whole_days_inclusive_at_both_ends_whatever_time_of_day_an_invoice_was_issued()
    {
        Sell("SINV-BEFORE", new DateTime(2026, 9, 4, 23, 59, 59), lines: Line(MouseVariant, 1m, 1m));
        Sell("SINV-FIRST", new DateTime(2026, 9, 5, 0, 0, 0), lines: Line(MouseVariant, 1m, 10m));
        Sell("SINV-LAST", new DateTime(2026, 9, 10, 23, 59, 59), lines: Line(MouseVariant, 1m, 100m));
        Sell("SINV-AFTER", new DateTime(2026, 9, 11, 0, 0, 0), lines: Line(MouseVariant, 1m, 1000m));

        var report = await _w.AnalysisService().GetSalesByProductAsync(On(new DateTime(2026, 9, 5, 18, 0, 0), new DateTime(2026, 9, 10, 3, 0, 0)));

        report.Items.Single().Revenue.Should().Be(110m);
    }

    [Fact]
    public async Task Either_end_of_the_range_can_be_left_open_and_the_criteria_say_the_days()
    {
        Sell("SINV-1", D(8, 1), lines: Line(MouseVariant, 1m, 1m));
        Sell("SINV-2", D(9, 10), lines: Line(MouseVariant, 1m, 10m));
        Sell("SINV-3", D(10, 1), lines: Line(MouseVariant, 1m, 100m));

        (await _w.AnalysisService().GetSalesByProductAsync(On(from: D(9, 1)))).Items.Single().Revenue.Should().Be(110m);
        var upTo = await _w.AnalysisService().GetSalesByProductAsync(On(to: new DateTime(2026, 9, 30, 15, 0, 0)));
        upTo.Items.Single().Revenue.Should().Be(11m);
        (upTo.Criteria.DateFrom, upTo.Criteria.DateTo).Should().Be(((DateTime?)null, (DateTime?)D(9, 30)));
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var act = () => _w.AnalysisService().GetSalesByProductAsync(On(D(9, 20), D(9, 1)));

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("start date is after its end date");
    }

    // ── One customer ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_customer_filter_reports_only_that_customers_purchases_and_names_them()
    {
        Sell("SINV-A", D(9, 5), partner: _w.Acme, lines: Line(MouseVariant, 1m, 10m));
        Sell("SINV-G", D(9, 5), partner: _w.Globex, lines: Line(MouseVariant, 1m, 700m));

        var report = await _w.AnalysisService().GetSalesByProductAsync(On(partner: _w.Acme));

        report.Items.Single().Revenue.Should().Be(10m);
        (report.Criteria.PartnerId, report.Criteria.CustomerName).Should().Be(((Guid?)_w.Acme, "Acme Ltd"));
    }

    [Fact]
    public async Task A_customer_the_lookup_does_not_know_keeps_their_sales_but_has_no_name()
    {
        var stranger = Guid.NewGuid();
        _w.Sale(stranger, "SINV-S", D(9, 5), [Line(MouseVariant, 1m, 10m)]);

        var report = await _w.AnalysisService().GetSalesByProductAsync(On(partner: stranger));

        report.Items.Should().ContainSingle();
        report.Criteria.CustomerName.Should().BeNull();
    }

    // ── Products the books cannot place ──────────────────────────────────────

    [Fact]
    public async Task A_variant_the_product_ledger_never_saw_is_its_own_product_rather_than_dropped()
    {
        var orphan = Guid.NewGuid();
        Sell("SINV-1", D(9, 5), lines: [Line(orphan, 2m, 30m), Line(MouseVariant, 1m, 10m)]);

        var report = await _w.AnalysisService().GetSalesByProductAsync(On());

        report.Items.Select(i => (i.ProductUuid, i.Revenue)).Should().Equal((orphan, 60m), (Mouse, 10m));
        report.Items.First().ProductName.Should().BeNull("nothing knows what it is");
    }

    [Fact]
    public async Task Another_organizations_product_ledger_does_not_decide_which_product_a_variant_belongs_to()
    {
        // The same variant id, filed under a different (lower) product by another organization.
        using (var db = _w.Db(OtherOrg))
        {
            db.ProductLedgerEntries.Add(new ProductLedgerEntry
            {
                UUID = Guid.NewGuid(), OrganizationId = OtherOrg, VariantUuid = MouseVariant, ProductUuid = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                SequenceNo = 1, EntryDate = D(1, 1), EntryType = "PURCHASE", ReferenceType = "GRN", ReferenceId = Guid.NewGuid(), ReferenceNumber = "GRN-X",
                Direction = "IN", Quantity = 1m, UnitCost = 1m, TotalCost = 1m, RunningQty = 1m, RunningValue = 1m, CreatedBy = 1, CreatedDate = D(1, 1)
            });
            db.SaveChanges();
        }

        Sell("SINV-1", D(9, 5), lines: Line(MouseVariant, 1m, 10m));

        (await _w.AnalysisService(Org).GetSalesByProductAsync(On())).Items.Single().ProductUuid.Should().Be(Mouse);
    }

    [Fact]
    public async Task The_page_count_rounds_up_so_a_short_last_page_is_a_page()
    {
        Sell("SINV-1", D(9, 5), lines: [Line(MouseVariant, 1m, 30m), Line(LaptopBlack, 1m, 20m), Line(Guid.NewGuid(), 1m, 10m)]);

        var report = await _w.AnalysisService().GetSalesByProductAsync(On(pageSize: 2));

        (report.TotalRecords, report.TotalPages).Should().Be((3, 2));
    }

    [Fact]
    public async Task A_variant_the_lookup_can_no_longer_name_still_sells_under_its_product_with_no_name()
    {
        _w.Variants.Remove(MouseVariant);
        Sell("SINV-1", D(9, 5), lines: Line(MouseVariant, 1m, 10m));

        var row = (await _w.AnalysisService().GetSalesByProductAsync(On())).Items.Single();

        (row.ProductUuid, row.ProductName).Should().Be((Mouse, (string?)null));
    }

    [Fact]
    public async Task Product_names_are_asked_for_once_with_one_variant_of_each_product_on_the_page()
    {
        Sell("SINV-1", D(9, 5), lines: [Line(LaptopBlack, 1m, 900m), Line(LaptopSilver, 1m, 800m), Line(MouseVariant, 1m, 10m)]);
        var resolver = new Mock<IProductVariantResolver>();

        await _w.AnalysisService(resolver: resolver).GetSalesByProductAsync(On());

        resolver.Verify(r => r.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
        resolver.Verify(r => r.DescribeVariantsAsync(It.Is<IReadOnlyList<Guid>>(v => v.Count == 2)), Times.Once);
    }

    [Fact]
    public async Task With_nothing_sold_there_is_no_lookup_no_totals_and_no_pages()
    {
        var resolver = new Mock<IProductVariantResolver>();

        var report = await _w.AnalysisService(resolver: resolver).GetSalesByProductAsync(On());

        resolver.Verify(r => r.DescribeVariantsAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Never);
        (report.Items.Count, report.Totals.Count, report.TotalRecords, report.TotalPages).Should().Be((0, 0, 0, 0));
    }

    // ── Tenancy, paging, volume ──────────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_sales_are_neither_listed_nor_totalled_and_the_export_is_scoped_too()
    {
        _w.Stock(MouseVariant, Mouse, "Mouse", org: OtherOrg);
        Sell("SINV-MINE", D(9, 5), lines: Line(MouseVariant, 1m, 10m));
        _w.Sale(_w.Acme, "SINV-THEIRS", D(9, 5), [Line(MouseVariant, 1m, 5000m)], org: OtherOrg);

        var page   = await _w.AnalysisService(Org).GetSalesByProductAsync(On());
        var export = await _w.AnalysisService(Org).GetSalesByProductForExportAsync(On());
        var theirs = await _w.AnalysisService(OtherOrg).GetSalesByProductAsync(On());

        page.Totals.Single().Revenue.Should().Be(10m);
        export.Items.Single().Revenue.Should().Be(10m);
        theirs.Totals.Single().Revenue.Should().Be(5000m);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public async Task Page_size_is_kept_between_one_and_a_hundred(int asked, int used)
    {
        Sell("SINV-1", D(9, 5), lines: Line(MouseVariant, 1m, 10m));

        (await _w.AnalysisService().GetSalesByProductAsync(On(pageSize: asked))).PageSize.Should().Be(used);
    }

    [Fact]
    public async Task The_export_carries_every_product_whatever_the_paging_asked_for()
    {
        Sell("SINV-1", D(9, 5), lines: [Line(LaptopBlack, 1m, 900m), Line(MouseVariant, 1m, 10m)]);

        var report = await _w.AnalysisService().GetSalesByProductForExportAsync(On(page: 3, pageSize: 1));

        report.Items.Should().HaveCount(2);
        (report.Page, report.PageSize, report.TotalRecords, report.TotalPages).Should().Be((1, 2, 2, 1));
    }

    [Fact]
    public async Task An_export_of_nothing_is_an_empty_report_not_a_failure()
    {
        var report = await _w.AnalysisService().GetSalesByProductForExportAsync(On());

        (report.Items.Count, report.Page, report.PageSize).Should().Be((0, 1, 1));
    }

    [Fact]
    public async Task A_report_takes_exactly_the_most_it_allows_and_refuses_one_more()
    {
        void SellProducts(string number, int count) =>
            _w.Sale(_w.Acme, number, D(9, 5), [.. Enumerable.Range(0, count).Select(_ => Line(Guid.NewGuid(), 1m, 1m))]);

        SellProducts("SINV-AT-THE-LIMIT", SalesAnalysisReportService.MaxRows);
        (await _w.AnalysisService().GetSalesByProductForExportAsync(On())).Items.Should().HaveCount(SalesAnalysisReportService.MaxRows);

        SellProducts("SINV-ONE-MORE", 1);
        foreach (var ask in new Func<Task>[]
        {
            () => _w.AnalysisService().GetSalesByProductAsync(On()),
            () => _w.AnalysisService().GetSalesByProductForExportAsync(On())
        })
        {
            var message = (await ask.Should().ThrowAsync<BadRequestException>()).Which.Message;
            message.Should().Contain($"{SalesAnalysisReportService.MaxRows + 1} products").And.Contain("Narrow the date range");
        }
    }

    // ── Header facts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_company_name_is_trimmed_and_the_time_is_the_clocks()
    {
        _w.CompanyName = "  Northwind Trading  ";

        var report = await _w.AnalysisService().GetSalesByProductAsync(On());

        (report.CompanyName, report.GeneratedAt).Should().Be(("Northwind Trading", _w.Now));
    }

    [Fact]
    public async Task With_no_letterhead_company_the_name_is_null()
    {
        _w.CompanyName = " ";

        (await _w.AnalysisService().GetSalesByProductAsync(On())).CompanyName.Should().BeNull();
    }

    [Fact]
    public async Task A_null_filter_is_a_programming_error()
    {
        var act = () => _w.AnalysisService().GetSalesByProductAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
