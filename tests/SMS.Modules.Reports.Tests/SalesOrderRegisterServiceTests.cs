using FluentAssertions;
using Moq;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-01 §15 R1 — the sale order register: which orders it lists, in what order, with what totals, and
/// what it refuses. In-memory, so it shows what the service decides; the SQL Server tests show the query translates.
/// </summary>
public class SalesOrderRegisterServiceTests
{
    private readonly RegisterWorld _w = new();

    private static string[] Numbers(SalesOrderRegisterReport report) => [.. report.Items.Select(i => i.SoNumber)];

    // ── What a row says ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_row_carries_the_number_customer_status_delivery_mode_currency_lines_and_every_total()
    {
        var order = _w.Order(new DateTime(2026, 9, 10, 14, 30, 0), "PARTIALLY_FULFILLED", "SELF_PICKUP", _w.Globex, _w.Usd,
            subtotal: 1000m, discount: 50m, tax: 152m, lines: 3, number: "SO-2026-00077", expected: new DateTime(2026, 9, 25));

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        var row = report.Items.Should().ContainSingle().Subject;
        row.Uuid.Should().Be(order.UUID);
        row.SoNumber.Should().Be("SO-2026-00077");
        row.OrderDate.Should().Be(new DateTime(2026, 9, 10, 14, 30, 0));
        row.ExpectedDeliveryDate.Should().Be(new DateTime(2026, 9, 25));
        row.PartnerId.Should().Be(_w.Globex);
        row.CustomerName.Should().Be("Globex Corp");
        row.Status.Should().Be("PARTIALLY_FULFILLED");
        row.DeliveryMode.Should().Be("SELF_PICKUP");
        row.CurrencyCode.Should().Be("USD");
        row.LineCount.Should().Be(3);
        (row.Subtotal, row.DiscountAmount, row.TaxAmount, row.GrandTotal).Should().Be((1000m, 50m, 152m, 1102m));
    }

    [Fact]
    public async Task Newest_first_and_orders_on_the_same_day_newest_created_first()
    {
        _w.Order(new DateTime(2026, 9, 1), number: "SO-A");
        _w.Order(new DateTime(2026, 9, 12), number: "SO-B");
        _w.Order(new DateTime(2026, 9, 12), number: "SO-C");
        _w.Order(new DateTime(2026, 9, 5), number: "SO-D");

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        Numbers(report).Should().Equal("SO-C", "SO-B", "SO-D", "SO-A");
    }

    [Fact]
    public async Task A_customer_the_lookup_does_not_know_has_no_name_but_keeps_its_row()
    {
        var stranger = Guid.NewGuid();
        _w.Order(partner: stranger);

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        report.Items.Should().ContainSingle().Which.CustomerName.Should().BeNull();
    }

    [Fact]
    public async Task An_order_with_no_lines_says_so()
    {
        _w.Order(lines: 0);

        (await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter())).Items.Single().LineCount.Should().Be(0);
    }

    [Fact]
    public async Task Draft_and_cancelled_orders_are_in_the_register_because_it_is_a_register_of_all_of_them()
    {
        _w.Order(status: "DRAFT", number: "SO-1");
        _w.Order(status: "CANCELLED", number: "SO-2");

        Numbers(await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter())).Should().BeEquivalentTo("SO-1", "SO-2");
    }

    [Fact]
    public async Task Soft_deleted_orders_are_not_listed_or_counted_or_totalled()
    {
        _w.Order(number: "SO-KEPT", subtotal: 100m);
        _w.Order(number: "SO-GONE", subtotal: 900m, deleted: true);

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        Numbers(report).Should().Equal("SO-KEPT");
        report.TotalRecords.Should().Be(1);
        report.Totals.Single().GrandTotal.Should().Be(100m);
    }

    // ── Date range ───────────────────────────────────────────────────────────

    [Fact]
    public async Task The_date_range_is_whole_days_inclusive_at_both_ends_whatever_time_of_day_the_order_was_placed()
    {
        _w.Order(new DateTime(2026, 9, 4, 23, 59, 59), number: "SO-BEFORE");
        _w.Order(new DateTime(2026, 9, 5, 0, 0, 0),    number: "SO-FIRST-DAY-START");
        _w.Order(new DateTime(2026, 9, 5, 16, 45, 0),  number: "SO-FIRST-DAY-LATE");
        _w.Order(new DateTime(2026, 9, 10, 23, 59, 59), number: "SO-LAST-DAY-END");
        _w.Order(new DateTime(2026, 9, 11, 0, 0, 0),   number: "SO-AFTER");

        // A time on the boundary dates changes nothing: the range is in days.
        var filter = new SalesOrderRegisterFilter { DateFrom = new DateTime(2026, 9, 5, 18, 0, 0), DateTo = new DateTime(2026, 9, 10, 3, 0, 0) };
        var report = await _w.Service().GetOrderRegisterAsync(filter);

        Numbers(report).Should().BeEquivalentTo("SO-FIRST-DAY-START", "SO-FIRST-DAY-LATE", "SO-LAST-DAY-END");
    }

    [Fact]
    public async Task One_day_can_be_asked_for_by_giving_it_as_both_ends()
    {
        _w.Order(new DateTime(2026, 9, 9, 23, 0, 0), number: "SO-YESTERDAY");
        _w.Order(new DateTime(2026, 9, 10, 8, 0, 0), number: "SO-TODAY");
        _w.Order(new DateTime(2026, 9, 11, 1, 0, 0), number: "SO-TOMORROW");

        var day    = new DateTime(2026, 9, 10);
        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { DateFrom = day, DateTo = day });

        Numbers(report).Should().Equal("SO-TODAY");
    }

    [Fact]
    public async Task Either_end_of_the_range_can_be_left_open()
    {
        _w.Order(new DateTime(2026, 8, 1),  number: "SO-AUG");
        _w.Order(new DateTime(2026, 9, 10), number: "SO-SEP");
        _w.Order(new DateTime(2026, 10, 1), number: "SO-OCT");

        Numbers(await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { DateFrom = new DateTime(2026, 9, 1) }))
            .Should().BeEquivalentTo("SO-SEP", "SO-OCT");
        Numbers(await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { DateTo = new DateTime(2026, 9, 30) }))
            .Should().BeEquivalentTo("SO-AUG", "SO-SEP");
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused_not_answered_with_nothing()
    {
        var act = () => _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter
        {
            DateFrom = new DateTime(2026, 9, 20), DateTo = new DateTime(2026, 9, 1)
        });

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("start date is after its end date");
    }

    // ── Status, customer, delivery mode ──────────────────────────────────────

    [Theory]
    [InlineData("CONFIRMED")]
    [InlineData("confirmed")]
    [InlineData("  Confirmed ")]
    public async Task Status_filters_in_any_case_and_ignores_padding(string status)
    {
        _w.Order(status: "CONFIRMED", number: "SO-YES");
        _w.Order(status: "DRAFT",     number: "SO-NO");

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { Status = status });

        Numbers(report).Should().Equal("SO-YES");
        report.Criteria.Status.Should().Be("CONFIRMED", "the criteria say what was applied, in the orders' own spelling");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("CONFIRMED")]
    [InlineData("PARTIALLY_FULFILLED")]
    [InlineData("FULFILLED")]
    [InlineData("INVOICED")]
    [InlineData("CLOSED")]
    [InlineData("CANCELLED")]
    public async Task Every_status_an_order_can_have_can_be_asked_for(string status)
    {
        _w.Order(status: status, number: "SO-WANTED");
        foreach (var other in new[] { "DRAFT", "CONFIRMED", "PARTIALLY_FULFILLED", "FULFILLED", "INVOICED", "CLOSED", "CANCELLED" }.Where(s => s != status))
            _w.Order(status: other);

        Numbers(await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { Status = status })).Should().Equal("SO-WANTED");
    }

    [Fact]
    public async Task A_status_no_order_can_have_is_refused_and_the_valid_ones_are_listed()
    {
        var act = () => _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { Status = "SHIPPED" });

        var message = (await act.Should().ThrowAsync<BadRequestException>()).Which.Message;
        message.Should().Contain("'SHIPPED'").And.Contain("DRAFT").And.Contain("PARTIALLY_FULFILLED").And.Contain("CANCELLED");
    }

    [Theory]
    [InlineData("SELF_PICKUP", "SO-PICKUP")]
    [InlineData("self_pickup", "SO-PICKUP")]
    [InlineData("ship", "SO-SHIP")]
    public async Task Delivery_mode_filters_in_any_case(string mode, string expected)
    {
        _w.Order(deliveryMode: "SHIP",        number: "SO-SHIP");
        _w.Order(deliveryMode: "SELF_PICKUP", number: "SO-PICKUP");

        Numbers(await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { DeliveryMode = mode })).Should().Equal(expected);
    }

    [Fact]
    public async Task A_delivery_mode_that_does_not_exist_is_refused()
    {
        var act = () => _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { DeliveryMode = "COURIER" });

        var message = (await act.Should().ThrowAsync<BadRequestException>()).Which.Message;
        message.Should().Contain("'COURIER'").And.Contain("SHIP").And.Contain("SELF_PICKUP");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_status_or_mode_means_no_filter_rather_than_an_error(string blank)
    {
        _w.Order(status: "DRAFT", deliveryMode: "SHIP");
        _w.Order(status: "CLOSED", deliveryMode: "SELF_PICKUP");

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { Status = blank, DeliveryMode = blank });

        report.TotalRecords.Should().Be(2);
        (report.Criteria.Status, report.Criteria.DeliveryMode).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public async Task The_customer_filter_lists_only_that_customers_orders_and_names_them_in_the_criteria()
    {
        _w.Order(partner: _w.Acme,   number: "SO-ACME-1");
        _w.Order(partner: _w.Globex, number: "SO-GLOBEX");
        _w.Order(partner: _w.Acme,   number: "SO-ACME-2");

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { PartnerId = _w.Acme });

        Numbers(report).Should().BeEquivalentTo("SO-ACME-1", "SO-ACME-2");
        report.Criteria.PartnerId.Should().Be(_w.Acme);
        report.Criteria.CustomerName.Should().Be("Acme Ltd");
    }

    [Fact]
    public async Task A_customer_with_no_orders_gets_an_empty_register_that_still_names_them()
    {
        _w.Order(partner: _w.Acme);

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { PartnerId = _w.Globex });

        report.Items.Should().BeEmpty();
        report.Criteria.CustomerName.Should().Be("Globex Corp");
    }

    [Fact]
    public async Task All_the_filters_are_ANDed()
    {
        var day = new DateTime(2026, 9, 10);
        _w.Order(day, "CONFIRMED", "SHIP", _w.Acme, number: "SO-ALL-FOUR");
        _w.Order(day.AddDays(-30), "CONFIRMED", "SHIP", _w.Acme, number: "SO-WRONG-DATE");
        _w.Order(day, "DRAFT", "SHIP", _w.Acme, number: "SO-WRONG-STATUS");
        _w.Order(day, "CONFIRMED", "SELF_PICKUP", _w.Acme, number: "SO-WRONG-MODE");
        _w.Order(day, "CONFIRMED", "SHIP", _w.Globex, number: "SO-WRONG-CUSTOMER");

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter
        {
            DateFrom = day.AddDays(-1), DateTo = day.AddDays(1), Status = "CONFIRMED", PartnerId = _w.Acme, DeliveryMode = "SHIP"
        });

        Numbers(report).Should().Equal("SO-ALL-FOUR");
    }

    // ── Totals ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_totals_are_over_every_matching_order_not_only_the_page_shown()
    {
        for (var i = 0; i < 7; i++) _w.Order(new DateTime(2026, 9, 1).AddDays(i), subtotal: 100m, discount: 10m, tax: 5m);

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { Page = 1, PageSize = 3 });

        report.Items.Should().HaveCount(3);
        var total = report.Totals.Should().ContainSingle().Subject;
        (total.CurrencyCode, total.OrderCount, total.Subtotal, total.DiscountAmount, total.TaxAmount, total.GrandTotal)
            .Should().Be(("PKR", 7, 700m, 70m, 35m, 665m));
    }

    [Fact]
    public async Task Totals_are_kept_per_currency_because_there_is_no_rate_to_add_them_with()
    {
        _w.Order(currency: _w.Pkr, subtotal: 1000m);
        _w.Order(currency: _w.Usd, subtotal: 40m);
        _w.Order(currency: _w.Pkr, subtotal: 500m);

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        report.Totals.Select(t => (t.CurrencyCode, t.OrderCount, t.GrandTotal))
              .Should().Equal(("PKR", 2, 1500m), ("USD", 1, 40m));
    }

    [Fact]
    public async Task Currencies_the_catalog_no_longer_has_are_one_question_mark_row_not_one_row_each()
    {
        _w.Order(currency: Guid.NewGuid(), subtotal: 10m);
        _w.Order(currency: Guid.NewGuid(), subtotal: 20m);
        _w.Order(currency: _w.Pkr, subtotal: 5m);

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        report.Totals.Select(t => (t.CurrencyCode, t.OrderCount, t.GrandTotal)).Should().Equal(("?", 2, 30m), ("PKR", 1, 5m));
        report.Items.Where(i => i.CurrencyCode == "?").Should().HaveCount(2);
    }

    [Fact]
    public async Task A_currency_without_a_code_is_treated_as_unknown_rather_than_shown_blank()
    {
        _w.Currencies.Clear();
        _w.Currencies.Add(new CurrencyModel { Id = _w.Pkr, Name = "Pakistani Rupee", Code = "  " });
        _w.Order(currency: _w.Pkr);

        (await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter())).Items.Single().CurrencyCode.Should().Be("?");
    }

    [Fact]
    public async Task Totals_follow_the_filters_so_filtering_by_status_gives_the_figure_for_that_status()
    {
        _w.Order(status: "CONFIRMED", subtotal: 300m);
        _w.Order(status: "CONFIRMED", subtotal: 200m);
        _w.Order(status: "CANCELLED", subtotal: 9000m);

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { Status = "CONFIRMED" });

        report.Totals.Single().GrandTotal.Should().Be(500m);
    }

    [Fact]
    public async Task The_totals_are_the_sum_of_the_rows_when_everything_fits_on_one_page()
    {
        _w.Order(subtotal: 123.45m, discount: 3.45m, tax: 21.6m);
        _w.Order(subtotal: 0.10m, discount: 0m, tax: 0.02m);
        _w.Order(subtotal: 999.99m, discount: 100m, tax: 0m);

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        var total = report.Totals.Single();
        total.Subtotal.Should().Be(report.Items.Sum(i => i.Subtotal));
        total.DiscountAmount.Should().Be(report.Items.Sum(i => i.DiscountAmount));
        total.TaxAmount.Should().Be(report.Items.Sum(i => i.TaxAmount));
        total.GrandTotal.Should().Be(report.Items.Sum(i => i.GrandTotal));
        total.OrderCount.Should().Be(report.Items.Count);
    }

    // ── Paging ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pages_are_slices_of_the_same_ordering_that_together_are_every_order_once()
    {
        for (var i = 0; i < 25; i++) _w.Order(new DateTime(2026, 9, 1).AddDays(i % 10), number: $"SO-{i:00}");

        var seen = new List<string>();
        for (var page = 1; page <= 3; page++)
        {
            var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { Page = page, PageSize = 10 });
            (report.Page, report.PageSize, report.TotalRecords, report.TotalPages).Should().Be((page, 10, 25, 3));
            seen.AddRange(Numbers(report));
        }

        seen.Should().OnlyHaveUniqueItems().And.HaveCount(25);
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_but_still_says_how_many_there_are()
    {
        _w.Order();
        _w.Order();

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { Page = 9, PageSize = 10 });

        report.Items.Should().BeEmpty();
        (report.TotalRecords, report.TotalPages).Should().Be((2, 1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(100000, 100)]
    public async Task Page_size_is_kept_between_one_and_a_hundred(int asked, int used)
    {
        _w.Order();

        (await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { PageSize = asked })).PageSize.Should().Be(used);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public async Task A_page_number_below_one_is_the_first_page(int asked)
    {
        _w.Order(number: "SO-ONLY");

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { Page = asked });

        (report.Page, report.Items.Count).Should().Be((1, 1));
    }

    [Fact]
    public async Task With_no_orders_the_register_is_empty_with_no_totals_and_no_pages()
    {
        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        report.Items.Should().BeEmpty();
        report.Totals.Should().BeEmpty();
        (report.TotalRecords, report.TotalPages).Should().Be((0, 0));
    }

    // ── Tenancy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_orders_are_neither_listed_nor_counted_nor_totalled()
    {
        _w.Order(number: "SO-MINE", subtotal: 100m);
        _w.Order(number: "SO-THEIRS", subtotal: 5000m, org: RegisterWorld.OtherOrg);

        var mine   = await _w.Service(RegisterWorld.Org).GetOrderRegisterAsync(new SalesOrderRegisterFilter());
        var theirs = await _w.Service(RegisterWorld.OtherOrg).GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        Numbers(mine).Should().Equal("SO-MINE");
        (mine.TotalRecords, mine.Totals.Single().GrandTotal).Should().Be((1, 100m));
        Numbers(theirs).Should().Equal("SO-THEIRS");
        (theirs.TotalRecords, theirs.Totals.Single().GrandTotal).Should().Be((1, 5000m));
    }

    [Fact]
    public async Task Asking_for_another_organizations_customer_finds_nothing_of_theirs()
    {
        _w.Order(partner: _w.Acme, org: RegisterWorld.OtherOrg, number: "SO-THEIRS");

        var report = await _w.Service(RegisterWorld.Org).GetOrderRegisterAsync(new SalesOrderRegisterFilter { PartnerId = _w.Acme });

        report.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task The_export_is_tenant_scoped_too()
    {
        _w.Order(number: "SO-MINE");
        _w.Order(number: "SO-THEIRS", org: RegisterWorld.OtherOrg);

        Numbers(await _w.Service(RegisterWorld.Org).GetOrderRegisterForExportAsync(new SalesOrderRegisterFilter())).Should().Equal("SO-MINE");
    }

    // ── Header facts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_criteria_echo_the_dates_as_whole_days()
    {
        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter
        {
            DateFrom = new DateTime(2026, 9, 5, 18, 0, 0), DateTo = new DateTime(2026, 9, 10, 3, 0, 0)
        });

        (report.Criteria.DateFrom, report.Criteria.DateTo).Should().Be(((DateTime?)new DateTime(2026, 9, 5), (DateTime?)new DateTime(2026, 9, 10)));
    }

    [Fact]
    public async Task The_company_name_comes_from_the_letterhead_and_is_trimmed()
    {
        _w.CompanyName = "  Northwind Trading  ";

        (await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter())).CompanyName.Should().Be("Northwind Trading");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task With_no_letterhead_company_the_name_is_null_rather_than_blank(string? name)
    {
        _w.CompanyName = name;

        (await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter())).CompanyName.Should().BeNull();
    }

    [Fact]
    public async Task It_says_when_it_was_generated_by_the_clock_it_was_given()
    {
        (await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter())).GeneratedAt.Should().Be(FixedClock.Start);
    }

    [Fact]
    public async Task Customer_names_are_asked_for_once_for_the_page_and_the_named_customer_not_once_per_row()
    {
        for (var i = 0; i < 6; i++) _w.Order(partner: i % 2 == 0 ? _w.Acme : _w.Globex);
        var names = new Mock<ISupplierNameLookupService>();

        await _w.Service(names: names).GetOrderRegisterAsync(new SalesOrderRegisterFilter { PartnerId = _w.Acme });

        names.Verify(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
        names.Verify(n => n.GetNamesAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 1 && ids[0] == _w.Acme)), Times.Once);
    }

    [Fact]
    public async Task No_customer_lookup_is_made_when_there_is_nobody_to_name()
    {
        var names = new Mock<ISupplierNameLookupService>();

        await _w.Service(names: names).GetOrderRegisterAsync(new SalesOrderRegisterFilter());

        names.Verify(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Never);
    }

    [Fact]
    public async Task A_null_filter_is_a_programming_error_not_an_empty_register()
    {
        var act = () => _w.Service().GetOrderRegisterAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Export ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_export_carries_every_matching_order_whatever_the_paging_asked_for()
    {
        for (var i = 0; i < 12; i++) _w.Order(new DateTime(2026, 9, 1).AddDays(i));

        var report = await _w.Service().GetOrderRegisterForExportAsync(new SalesOrderRegisterFilter { Page = 3, PageSize = 2 });

        report.Items.Should().HaveCount(12);
        (report.Page, report.PageSize, report.TotalRecords, report.TotalPages).Should().Be((1, 12, 12, 1));
    }

    [Fact]
    public async Task The_export_applies_the_same_filters_and_ordering_as_the_page()
    {
        _w.Order(new DateTime(2026, 9, 1), "CONFIRMED", number: "SO-OLD");
        _w.Order(new DateTime(2026, 9, 9), "CONFIRMED", number: "SO-NEW");
        _w.Order(new DateTime(2026, 9, 5), "DRAFT", number: "SO-DRAFT");
        var filter = new SalesOrderRegisterFilter { Status = "confirmed" };

        var page   = await _w.Service().GetOrderRegisterAsync(filter);
        var export = await _w.Service().GetOrderRegisterForExportAsync(filter);

        Numbers(export).Should().Equal("SO-NEW", "SO-OLD").And.Equal(Numbers(page));
        export.Totals.Should().BeEquivalentTo(page.Totals);
    }

    [Fact]
    public async Task An_export_of_nothing_is_an_empty_document_not_a_failure()
    {
        var report = await _w.Service().GetOrderRegisterForExportAsync(new SalesOrderRegisterFilter());

        (report.Items.Count, report.TotalRecords, report.Page, report.PageSize).Should().Be((0, 0, 1, 1));
    }

    [Fact]
    public async Task The_export_takes_exactly_the_most_it_allows_and_refuses_one_more()
    {
        _w.Orders(SalesReportService.MaxExportRows, i => new DateTime(2026, 1, 1).AddMinutes(i));

        var atTheLimit = await _w.Service().GetOrderRegisterForExportAsync(new SalesOrderRegisterFilter());
        atTheLimit.Items.Should().HaveCount(SalesReportService.MaxExportRows);

        _w.Order();
        var act = () => _w.Service().GetOrderRegisterForExportAsync(new SalesOrderRegisterFilter());

        var message = (await act.Should().ThrowAsync<BadRequestException>()).Which.Message;
        message.Should().Contain($"{SalesReportService.MaxExportRows + 1} orders match").And.Contain("Narrow the date range");
    }

    [Fact]
    public async Task Narrowing_the_filter_brings_an_over_large_export_back_within_the_limit()
    {
        _w.Orders(SalesReportService.MaxExportRows + 5, i => i < 5 ? new DateTime(2025, 1, 1) : new DateTime(2026, 1, 1));

        var narrowed = await _w.Service().GetOrderRegisterForExportAsync(
            new SalesOrderRegisterFilter { DateTo = new DateTime(2025, 12, 31) });

        narrowed.Items.Should().HaveCount(5);
    }

    [Fact]
    public async Task The_page_is_not_limited_by_the_export_limit()
    {
        _w.Orders(SalesReportService.MaxExportRows + 50, i => new DateTime(2026, 1, 1).AddMinutes(i));

        var report = await _w.Service().GetOrderRegisterAsync(new SalesOrderRegisterFilter { PageSize = 100 });

        (report.Items.Count, report.TotalRecords).Should().Be((100, SalesReportService.MaxExportRows + 50));
    }
}
