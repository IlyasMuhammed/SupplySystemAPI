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
/// A29-P9-04 §15 R5 — sales by customer: sale orders, invoices, revenue and average order value by
/// customer over the invoices that stand, issued in a range of days, before tax, per currency.
/// </summary>
public class SalesByCustomerReportTests
{
    private readonly ReceivablesReportWorld _w = new();

    private static readonly Guid Item = Guid.NewGuid();

    private static SalesByCustomerFilter On(DateTime? from = null, DateTime? to = null, int page = 1, int pageSize = 20) =>
        new() { DateFrom = from, DateTo = to, Page = page, PageSize = pageSize };

    /// <summary>An invoice for <paramref name="amount"/> of goods before any discount or tax.</summary>
    private SalesInvoice Sell(
        Guid partner, string number, DateTime issued, decimal amount, string currency = "PKR", string status = "ISSUED",
        Guid? order = null, decimal discountPercent = 0m, decimal taxPercent = 0m) =>
        _w.Sale(partner, number, issued, [new ReceivablesReportWorld.SoldLine(Item, 1m, amount, discountPercent, taxPercent)], currency, status, order);

    // ── What a row says ──────────────────────────────────────────────────────

    [Fact]
    public async Task Revenue_is_what_the_invoices_printed_before_tax_and_a_row_carries_orders_invoices_and_the_average()
    {
        // 1000 less 10% is 900; tax is not revenue. Two orders, one invoice each.
        Sell(_w.Acme, "SINV-1", D(9, 5), 1000m, discountPercent: 10m, taxPercent: 17m);
        Sell(_w.Acme, "SINV-2", D(9, 6), 500m);

        var row = (await _w.AnalysisService().GetSalesByCustomerAsync(On())).Items.Should().ContainSingle().Subject;

        (row.PartnerId, row.CustomerName, row.CurrencyCode, row.OrderCount, row.InvoiceCount, row.Revenue, row.AverageOrderValue)
            .Should().Be((_w.Acme, "Acme Ltd", "PKR", 2, 2, 1400m, 700m));
    }

    [Fact]
    public async Task An_order_billed_in_several_invoices_is_one_order()
    {
        var order = Guid.NewGuid();
        Sell(_w.Acme, "SINV-1", D(9, 5), 600m, order: order);
        Sell(_w.Acme, "SINV-2", D(9, 8), 400m, order: order);
        Sell(_w.Acme, "SINV-3", D(9, 9), 200m);

        var row = (await _w.AnalysisService().GetSalesByCustomerAsync(On())).Items.Single();

        (row.OrderCount, row.InvoiceCount, row.Revenue, row.AverageOrderValue).Should().Be((2, 3, 1200m, 600m));

        // The grand total averages over orders too, not over invoices: 1200 over 2 orders, not over 3 invoices.
        var total = (await _w.AnalysisService().GetSalesByCustomerAsync(On())).Totals.Single();
        (total.OrderCount, total.InvoiceCount, total.AverageOrderValue).Should().Be((2, 3, 600m));
    }

    [Fact]
    public async Task The_average_order_value_is_rounded_to_the_cent()
    {
        Sell(_w.Acme, "SINV-1", D(9, 5), 100m);
        Sell(_w.Acme, "SINV-2", D(9, 5), 100m);
        Sell(_w.Acme, "SINV-3", D(9, 5), 100.01m);

        // 300.01 / 3 = 100.00333...
        (await _w.AnalysisService().GetSalesByCustomerAsync(On())).Items.Single().AverageOrderValue.Should().Be(100.00m);
    }

    [Fact]
    public async Task Each_currency_is_its_own_row_and_its_own_total_with_its_own_average()
    {
        Sell(_w.Acme, "SINV-PKR-1", D(9, 5), 1000m);
        Sell(_w.Acme, "SINV-PKR-2", D(9, 5), 500m);
        Sell(_w.Acme, "SINV-USD", D(9, 5), 30m, currency: "USD");
        Sell(_w.Globex, "SINV-G", D(9, 5), 300m);

        var report = await _w.AnalysisService().GetSalesByCustomerAsync(On());

        report.Items.Select(i => (i.CustomerName, i.CurrencyCode, i.Revenue)).Should().Equal(
            ("Acme Ltd", "PKR", 1500m), ("Globex Corp", "PKR", 300m), ("Acme Ltd", "USD", 30m));
        report.Totals.Select(t => (t.CurrencyCode, t.CustomerCount, t.OrderCount, t.InvoiceCount, t.Revenue, t.AverageOrderValue)).Should().Equal(
            ("PKR", 2, 3, 3, 1800m, 600m), ("USD", 1, 1, 1, 30m, 30m));
    }

    [Fact]
    public async Task Highest_revenue_first_and_equal_revenue_by_customer_id()
    {
        Sell(_w.Globex, "SINV-1", D(9, 5), 100m);
        Sell(_w.Acme, "SINV-2", D(9, 5), 100m);
        var big = Guid.NewGuid();
        _w.Names[big] = "Big Buyer";
        Sell(big, "SINV-3", D(9, 5), 5000m);

        var report = await _w.AnalysisService().GetSalesByCustomerAsync(On());

        report.Items.Select(i => i.CustomerName).Should().Equal("Big Buyer", "Acme Ltd", "Globex Corp");
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
        Sell(_w.Acme, "SINV-1", D(9, 5), 100m, status: status);

        (await _w.AnalysisService().GetSalesByCustomerAsync(On())).Items.Any().Should().Be(counted);
    }

    [Fact]
    public async Task An_invoice_never_issued_has_no_ledger_entry_and_is_no_sale_and_nor_is_a_deleted_one()
    {
        _w.Sale(_w.Acme, "SINV-UNBOOKED", D(9, 5), [new ReceivablesReportWorld.SoldLine(Item, 1m, 10m)], booked: false);
        _w.Sale(_w.Acme, "SINV-GONE", D(9, 5), [new ReceivablesReportWorld.SoldLine(Item, 1m, 10m)], deleted: true);

        (await _w.AnalysisService().GetSalesByCustomerAsync(On())).Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sale_is_of_the_day_it_was_issued_not_the_day_the_draft_was_dated()
    {
        var invoice = Sell(_w.Acme, "SINV-1", D(9, 5), 100m);
        using (var db = _w.Db())
        {
            db.SalesInvoices.Single(i => i.Id == invoice.Id).InvoiceDate = D(9, 1);
            db.SaveChanges();
        }

        (await _w.AnalysisService().GetSalesByCustomerAsync(On(D(9, 1), D(9, 4)))).Items.Should().BeEmpty();
        (await _w.AnalysisService().GetSalesByCustomerAsync(On(D(9, 5), D(9, 5)))).Items.Should().ContainSingle();
    }

    [Fact]
    public async Task The_range_is_whole_days_inclusive_at_both_ends_whatever_time_of_day_an_invoice_was_issued()
    {
        Sell(_w.Acme, "SINV-BEFORE", new DateTime(2026, 9, 4, 23, 59, 59), 1m);
        Sell(_w.Acme, "SINV-FIRST", new DateTime(2026, 9, 5, 0, 0, 0), 10m);
        Sell(_w.Acme, "SINV-LAST", new DateTime(2026, 9, 10, 23, 59, 59), 100m);
        Sell(_w.Acme, "SINV-AFTER", new DateTime(2026, 9, 11, 0, 0, 0), 1000m);

        var report = await _w.AnalysisService().GetSalesByCustomerAsync(On(new DateTime(2026, 9, 5, 18, 0, 0), new DateTime(2026, 9, 10, 3, 0, 0)));

        report.Items.Single().Revenue.Should().Be(110m);
        (report.Criteria.DateFrom, report.Criteria.DateTo).Should().Be(((DateTime?)D(9, 5), (DateTime?)D(9, 10)));
    }

    [Fact]
    public async Task Either_end_of_the_range_can_be_left_open()
    {
        Sell(_w.Acme, "SINV-1", D(8, 1), 1m);
        Sell(_w.Acme, "SINV-2", D(9, 10), 10m);
        Sell(_w.Acme, "SINV-3", D(10, 1), 100m);

        (await _w.AnalysisService().GetSalesByCustomerAsync(On(from: D(9, 1)))).Items.Single().Revenue.Should().Be(110m);
        (await _w.AnalysisService().GetSalesByCustomerAsync(On(to: D(9, 30)))).Items.Single().Revenue.Should().Be(11m);
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var act = () => _w.AnalysisService().GetSalesByCustomerAsync(On(D(9, 20), D(9, 1)));

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("start date is after its end date");
    }

    // ── Names ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_customer_the_lookup_does_not_know_keeps_their_row_with_no_name()
    {
        var stranger = Guid.NewGuid();
        Sell(stranger, "SINV-1", D(9, 5), 100m);

        var row = (await _w.AnalysisService().GetSalesByCustomerAsync(On())).Items.Single();

        (row.PartnerId, row.CustomerName, row.Revenue).Should().Be((stranger, (string?)null, 100m));
    }

    [Fact]
    public async Task Customer_names_are_asked_for_once_for_everyone_billed_and_never_when_nobody_was()
    {
        Sell(_w.Acme, "SINV-1", D(9, 5), 100m);
        Sell(_w.Acme, "SINV-2", D(9, 5), 100m);
        Sell(_w.Globex, "SINV-3", D(9, 5), 100m);
        var names = new Mock<ISupplierNameLookupService>();

        await _w.AnalysisService(names: names).GetSalesByCustomerAsync(On());

        names.Verify(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
        names.Verify(n => n.GetNamesAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2)), Times.Once);

        var none = new Mock<ISupplierNameLookupService>();
        await new ReceivablesReportWorld().AnalysisService(names: none).GetSalesByCustomerAsync(On());
        none.Verify(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Never);
    }

    // ── Tenancy, paging, volume ──────────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_sales_are_neither_listed_nor_totalled_and_the_export_is_scoped_too()
    {
        Sell(_w.Acme, "SINV-MINE", D(9, 5), 100m);
        _w.Sale(_w.Acme, "SINV-THEIRS", D(9, 5), [new ReceivablesReportWorld.SoldLine(Item, 1m, 9000m)], org: OtherOrg);

        var page   = await _w.AnalysisService(Org).GetSalesByCustomerAsync(On());
        var export = await _w.AnalysisService(Org).GetSalesByCustomerForExportAsync(On());

        page.Totals.Single().Revenue.Should().Be(100m);
        export.Items.Single().Revenue.Should().Be(100m);
        (await _w.AnalysisService(OtherOrg).GetSalesByCustomerAsync(On())).Totals.Single().Revenue.Should().Be(9000m);
    }

    [Fact]
    public async Task A_page_is_a_slice_and_the_totals_are_over_every_customer()
    {
        for (var i = 1; i <= 5; i++)
        {
            var customer = Guid.NewGuid();
            _w.Names[customer] = $"Customer {i}";
            Sell(customer, $"SINV-{i}", D(9, 5), i * 100m);
        }

        var report = await _w.AnalysisService().GetSalesByCustomerAsync(On(page: 2, pageSize: 2));

        report.Items.Select(i => i.CustomerName).Should().Equal("Customer 3", "Customer 2");
        (report.Page, report.PageSize, report.TotalRecords, report.TotalPages).Should().Be((2, 2, 5, 3));
        report.Totals.Single().Should().Match<SalesByCustomerTotal>(t => t.CustomerCount == 5 && t.Revenue == 1500m);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public async Task Page_size_is_kept_between_one_and_a_hundred(int asked, int used)
    {
        Sell(_w.Acme, "SINV-1", D(9, 5), 100m);

        (await _w.AnalysisService().GetSalesByCustomerAsync(On(pageSize: asked))).PageSize.Should().Be(used);
    }

    [Fact]
    public async Task The_export_carries_every_customer_whatever_the_paging_asked_for_and_an_empty_one_is_no_failure()
    {
        Sell(_w.Acme, "SINV-1", D(9, 5), 100m);
        Sell(_w.Globex, "SINV-2", D(9, 5), 50m);

        var report = await _w.AnalysisService().GetSalesByCustomerForExportAsync(On(page: 3, pageSize: 1));
        report.Items.Should().HaveCount(2);
        (report.Page, report.PageSize, report.TotalPages).Should().Be((1, 2, 1));

        var empty = await new ReceivablesReportWorld().AnalysisService().GetSalesByCustomerForExportAsync(On());
        (empty.Items.Count, empty.Page, empty.PageSize).Should().Be((0, 1, 1));
    }

    [Fact]
    public async Task A_report_takes_exactly_the_most_it_allows_and_refuses_one_more()
    {
        SeedCustomers(SalesAnalysisReportService.MaxRows);
        (await _w.AnalysisService().GetSalesByCustomerForExportAsync(On())).Items.Should().HaveCount(SalesAnalysisReportService.MaxRows);

        SeedCustomers(1);
        foreach (var ask in new Func<Task>[]
        {
            () => _w.AnalysisService().GetSalesByCustomerAsync(On()),
            () => _w.AnalysisService().GetSalesByCustomerForExportAsync(On())
        })
        {
            var message = (await ask.Should().ThrowAsync<BadRequestException>()).Which.Message;
            message.Should().Contain($"{SalesAnalysisReportService.MaxRows + 1} customers").And.Contain("Narrow the date range");
        }
    }

    private int _seeded;

    /// <summary>One issued invoice each for that many new customers, in one save.</summary>
    private void SeedCustomers(int count)
    {
        using var db = _w.Db();
        for (var i = 0; i < count; i++)
        {
            var n = ++_seeded;
            var invoice = new SalesInvoice
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, TraceId = Guid.NewGuid(), InvoiceNumber = $"SINV-BULK-{n:000000}",
                SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = "SO-1", PartnerId = Guid.NewGuid(), PartnerName = "x",
                InvoiceDate = D(9, 5), DueDate = D(10, 5), Subtotal = 1m, GrandTotal = 1m, BalanceDue = 1m, Status = "ISSUED",
                CurrencyCode = "PKR", CreatedBy = 1, CreatedDate = D(9, 5)
            };
            db.SalesInvoices.Add(invoice);
            db.CustomerLedgerEntries.Add(new CustomerLedgerEntry
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, PartnerId = invoice.PartnerId, SequenceNo = 1, EntryDate = D(9, 5), EntryType = "INVOICE",
                ReferenceType = "SalesInvoice", ReferenceId = invoice.UUID, ReferenceNumber = invoice.InvoiceNumber, DebitAmount = 1m, CurrencyCode = "PKR",
                RunningBalance = 1m, CreatedBy = 1, CreatedDate = D(9, 5)
            });
        }
        db.SaveChanges();
    }

    // ── Header facts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_company_name_is_trimmed_null_when_blank_and_the_time_is_the_clocks()
    {
        _w.CompanyName = "  Northwind Trading  ";
        var report = await _w.AnalysisService().GetSalesByCustomerAsync(On());
        (report.CompanyName, report.GeneratedAt).Should().Be(("Northwind Trading", _w.Now));

        _w.CompanyName = " ";
        (await _w.AnalysisService().GetSalesByCustomerAsync(On())).CompanyName.Should().BeNull();
    }

    [Fact]
    public async Task With_nothing_billed_there_are_no_totals_and_no_pages()
    {
        var report = await _w.AnalysisService().GetSalesByCustomerAsync(On());

        (report.Items.Count, report.Totals.Count, report.TotalRecords, report.TotalPages).Should().Be((0, 0, 0, 0));
    }

    [Fact]
    public async Task A_null_filter_is_a_programming_error()
    {
        var act = () => _w.AnalysisService().GetSalesByCustomerAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
