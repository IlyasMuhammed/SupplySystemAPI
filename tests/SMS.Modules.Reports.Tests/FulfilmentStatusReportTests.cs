using FluentAssertions;
using Moq;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-04 §15 R6 — fulfilment status: the sale-order deliveries not yet delivered, counted by status,
/// warehouse and delivery mode and listed oldest first.
/// </summary>
public class FulfilmentStatusReportTests
{
    private readonly FulfilmentWorld _w = new();

    private static FulfilmentStatusFilter Ask(string? status = null, Guid? warehouse = null, string? mode = null, int page = 1, int pageSize = 20) =>
        new() { Status = status, WarehouseId = warehouse, DeliveryMode = mode, Page = page, PageSize = pageSize };

    private static string[] Numbers(FulfilmentStatusReport r) => [.. r.Items.Select(i => i.DeliveryNumber)];

    // ── What "open" means ────────────────────────────────────────────────────

    /// <summary>Whether the delivery state machine can still take a delivery in <paramref name="from"/> to DELIVERED.</summary>
    private static bool CanStillBeDelivered(DeliveryStatus from)
    {
        var seen  = new HashSet<DeliveryStatus>();
        var queue = new Queue<DeliveryStatus>(DeliveryStateMachine.Instance.From(from));

        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (next == DeliveryStatus.Delivered) return true;
            if (seen.Add(next))
                foreach (var onward in DeliveryStateMachine.Instance.From(next)) queue.Enqueue(onward);
        }

        return false;
    }

    [Fact]
    public void A_status_is_open_exactly_when_the_state_machine_can_still_take_the_delivery_to_delivered()
    {
        var expected = Enum.GetValues<DeliveryStatus>().Where(CanStillBeDelivered).Select(LogisticsCode.Of).ToList();

        FulfilmentReportService.OpenStatuses.Should().BeEquivalentTo(expected,
            "a status added to the delivery lifecycle must be classified here, not silently left out of the report");
        FulfilmentReportService.OpenStatuses.Should().HaveCountGreaterThan(8);
    }

    [Fact]
    public void The_finished_statuses_are_the_four_that_have_reached_the_customer_or_been_given_up_on()
    {
        var finished = Enum.GetValues<DeliveryStatus>().Select(LogisticsCode.Of).Except(FulfilmentReportService.OpenStatuses);

        finished.Should().BeEquivalentTo("DELIVERED", "CLOSED", "SHORT_CLOSED", "CANCELLED");
    }

    [Fact]
    public void The_open_statuses_are_in_the_order_a_delivery_moves_through_them()
    {
        FulfilmentReportService.OpenStatuses.Should().Equal(
            "DRAFT", "RELEASED", "PICKING", "PICKED", "PACKED", "STAGED", "PENDING_APPROVAL", "GOODS_ISSUED", "IN_TRANSIT",
            "ON_HOLD", "PARTIALLY_DELIVERED");
    }

    public static IEnumerable<object[]> EveryStatus() => Enum.GetValues<DeliveryStatus>().Select(s => new object[] { LogisticsCode.Of(s), CanStillBeDelivered(s) });

    [Theory]
    [MemberData(nameof(EveryStatus))]
    public async Task A_delivery_is_listed_when_its_status_is_open_and_not_otherwise(string status, bool open)
    {
        _w.Delivery("DLV-1", status);

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask());

        report.Items.Any().Should().Be(open);
        report.TotalRecords.Should().Be(open ? 1 : 0);
    }

    // ── Which deliveries ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("PO")]
    [InlineData("SRO")]
    [InlineData("MIV")]
    [InlineData("TRANSFER")]
    [InlineData("MANUAL")]
    public async Task Only_sale_order_deliveries_are_fulfilments(string sourceType)
    {
        _w.Delivery("DLV-OTHER", sourceType: sourceType);
        _w.Delivery("DLV-SALE");

        Numbers(await _w.Service().GetFulfilmentStatusAsync(Ask())).Should().Equal("DLV-SALE");
    }

    [Fact]
    public async Task A_deleted_delivery_is_not_open()
    {
        _w.Delivery("DLV-GONE", deleted: true);

        (await _w.Service().GetFulfilmentStatusAsync(Ask())).Items.Should().BeEmpty();
    }

    // ── What a row says ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_row_carries_the_delivery_order_customer_status_mode_warehouse_dates_and_quantities()
    {
        var order = _w.Order("SO-2026-00042", _w.Globex());
        var d = _w.Delivery("DLV-2026-00007", "PICKING", "SELF_PICKUP", FulfilmentWorld.Karachi, order,
            created: new DateTime(2026, 9, 10, 9, 0, 0), promised: new DateTime(2026, 9, 25), requested: new DateTime(2026, 9, 22),
            lines: [(10m, 4m), (5.5m, 0m)]);

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask());

        var row = report.Items.Should().ContainSingle().Subject;
        row.DeliveryUuid.Should().Be(d.UUID);
        (row.DeliveryNumber, row.SaleOrderUuid, row.SaleOrderNumber, row.PartnerId, row.CustomerName)
            .Should().Be(("DLV-2026-00007", (Guid?)order.UUID, "SO-2026-00042", (Guid?)FulfilmentWorld.GlobexId, "Globex Corp"));
        (row.Status, row.DeliveryMode, row.WarehouseUuid, row.WarehouseName).Should().Be(("PICKING", "SELF_PICKUP", (Guid?)FulfilmentWorld.Karachi, "Karachi Depot"));
        (row.RequestedDate, row.PromisedDate, row.CreatedDate).Should().Be(((DateTime?)new DateTime(2026, 9, 22), (DateTime?)new DateTime(2026, 9, 25), new DateTime(2026, 9, 10, 9, 0, 0)));
        (row.DaysOpen, row.LineCount, row.QuantityOrdered, row.QuantityDelivered).Should().Be((10, 2, 15.5m, 4m));
    }

    [Fact]
    public async Task Oldest_first_and_deliveries_raised_together_in_the_order_they_were_made()
    {
        _w.Delivery("DLV-B", created: new DateTime(2026, 9, 12));
        _w.Delivery("DLV-A", created: new DateTime(2026, 9, 5));
        _w.Delivery("DLV-C1", created: new DateTime(2026, 9, 12));
        _w.Delivery("DLV-Z", created: new DateTime(2026, 9, 1));

        Numbers(await _w.Service().GetFulfilmentStatusAsync(Ask())).Should().Equal("DLV-Z", "DLV-A", "DLV-B", "DLV-C1");
    }

    [Theory]
    [InlineData("2026-09-20T10:00:00", 0)]
    [InlineData("2026-09-19T23:59:59", 1)]
    [InlineData("2026-09-10T09:00:00", 10)]
    [InlineData("2026-09-30T09:00:00", 0)]
    public async Task Days_open_are_whole_days_from_the_day_it_was_raised_to_the_day_of_the_report_and_never_negative(string created, int days)
    {
        _w.Delivery("DLV-1", created: DateTime.Parse(created));

        (await _w.Service().GetFulfilmentStatusAsync(Ask())).Items.Single().DaysOpen.Should().Be(days);
    }

    [Fact]
    public async Task A_delivery_whose_sale_order_cannot_be_found_keeps_the_number_the_delivery_recorded_and_no_customer()
    {
        _w.Delivery("DLV-1");

        var row = (await _w.Service().GetFulfilmentStatusAsync(Ask())).Items.Single();

        (row.SaleOrderNumber, row.PartnerId, row.CustomerName).Should().Be((row.SaleOrderNumber, (Guid?)null, (string?)null));
        row.SaleOrderNumber.Should().StartWith("SRC-");
    }

    [Fact]
    public async Task A_customer_the_lookup_does_not_know_has_no_name_and_a_delivery_with_no_warehouse_or_lines_is_still_a_row()
    {
        var stranger = Guid.NewGuid();
        var order = _w.Order("SO-1", stranger);
        _w.Delivery("DLV-1", order: order, noWarehouse: true);

        var row = (await _w.Service().GetFulfilmentStatusAsync(Ask())).Items.Single();

        (row.PartnerId, row.CustomerName, row.WarehouseUuid, row.WarehouseName).Should().Be(((Guid?)stranger, (string?)null, (Guid?)null, (string?)null));
        (row.LineCount, row.QuantityOrdered, row.QuantityDelivered).Should().Be((0, 0m, 0m));
    }

    // ── The counts ───────────────────────────────────────────────────────────

    private void Spread()
    {
        _w.Delivery("DLV-1", "RELEASED", "SHIP", FulfilmentWorld.Lahore);
        _w.Delivery("DLV-2", "RELEASED", "SELF_PICKUP", FulfilmentWorld.Lahore);
        _w.Delivery("DLV-3", "PICKING", "SHIP", FulfilmentWorld.Karachi);
        _w.Delivery("DLV-4", "IN_TRANSIT", "SHIP", FulfilmentWorld.Karachi);
        _w.Delivery("DLV-5", "IN_TRANSIT", "SHIP", FulfilmentWorld.Lahore);
        _w.Delivery("DLV-6", "DRAFT", "SHIP", noWarehouse: true);
        _w.Delivery("DLV-DONE", "DELIVERED");
    }

    [Fact]
    public async Task The_counts_by_status_are_in_the_order_a_delivery_moves_and_leave_finished_deliveries_out()
    {
        Spread();

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask());

        report.ByStatus.Select(s => (s.Status, s.Count)).Should().Equal(("DRAFT", 1), ("RELEASED", 2), ("PICKING", 1), ("IN_TRANSIT", 2));
    }

    [Fact]
    public async Task The_counts_by_warehouse_are_busiest_first_named_and_a_delivery_with_none_is_counted_last()
    {
        Spread();

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask());

        report.ByWarehouse.Select(w => (w.WarehouseUuid, w.WarehouseName, w.Count)).Should().Equal(
            ((Guid?)FulfilmentWorld.Lahore, "Lahore Main", 3), ((Guid?)FulfilmentWorld.Karachi, "Karachi Depot", 2), ((Guid?)null, (string?)null, 1));
    }

    [Fact]
    public async Task A_delivery_with_no_warehouse_is_counted_after_the_named_ones_it_ties_with()
    {
        _w.Delivery("DLV-1", warehouse: FulfilmentWorld.Lahore);
        _w.Delivery("DLV-2", noWarehouse: true);

        (await _w.Service().GetFulfilmentStatusAsync(Ask())).ByWarehouse.Select(w => w.WarehouseName).Should().Equal("Lahore Main", null);
    }

    [Fact]
    public async Task A_delivery_that_names_another_organizations_sale_order_shows_no_customer_and_no_name_from_it()
    {
        var theirOrder = _w.Order("SO-THEIRS", org: FulfilmentWorld.OtherOrg);
        _w.Delivery("DLV-1", order: theirOrder);

        var row = (await _w.Service(FulfilmentWorld.Org).GetFulfilmentStatusAsync(Ask())).Items.Single();

        (row.PartnerId, row.CustomerName).Should().Be(((Guid?)null, (string?)null));
    }

    [Fact]
    public async Task Warehouses_with_equal_counts_are_ordered_by_name()
    {
        _w.Delivery("DLV-1", warehouse: FulfilmentWorld.Lahore);
        _w.Delivery("DLV-2", warehouse: FulfilmentWorld.Karachi);

        (await _w.Service().GetFulfilmentStatusAsync(Ask())).ByWarehouse.Select(w => w.WarehouseName).Should().Equal("Karachi Depot", "Lahore Main");
    }

    [Fact]
    public async Task The_counts_by_delivery_mode_are_ship_then_self_pickup()
    {
        Spread();

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask());

        report.ByDeliveryMode.Select(m => (m.DeliveryMode, m.Count)).Should().Equal(("SHIP", 5), ("SELF_PICKUP", 1));
    }

    [Fact]
    public async Task A_delivery_with_no_delivery_mode_is_counted_under_an_empty_mode_after_the_known_ones()
    {
        _w.Delivery("DLV-1", mode: "SELF_PICKUP");
        _w.Delivery("DLV-2", mode: null);
        _w.Delivery("DLV-3", mode: "SHIP");

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask());

        report.ByDeliveryMode.Select(m => (m.DeliveryMode, m.Count)).Should().Equal(("SHIP", 1), ("SELF_PICKUP", 1), ("", 1));
        report.ByDeliveryMode.Sum(m => m.Count).Should().Be(report.TotalRecords);
    }

    [Fact]
    public async Task Each_breakdown_adds_up_to_the_number_of_open_deliveries()
    {
        Spread();

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask());

        report.TotalRecords.Should().Be(6);
        report.ByStatus.Sum(s => s.Count).Should().Be(6);
        report.ByWarehouse.Sum(w => w.Count).Should().Be(6);
        report.ByDeliveryMode.Sum(m => m.Count).Should().Be(6);
    }

    [Fact]
    public async Task The_counts_are_over_every_open_delivery_not_only_the_page()
    {
        Spread();

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask(page: 1, pageSize: 2));

        report.Items.Should().HaveCount(2);
        report.ByStatus.Sum(s => s.Count).Should().Be(6);
    }

    // ── Filters ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("in_transit")]
    [InlineData(" IN_TRANSIT ")]
    public async Task The_status_filter_is_any_case_and_narrows_the_list_and_the_counts(string status)
    {
        Spread();

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask(status));

        Numbers(report).Should().BeEquivalentTo("DLV-4", "DLV-5");
        report.ByStatus.Select(s => s.Status).Should().Equal("IN_TRANSIT");
        report.ByWarehouse.Sum(w => w.Count).Should().Be(2);
        report.Criteria.Status.Should().Be("IN_TRANSIT");
    }

    [Theory]
    [InlineData("DELIVERED")]
    [InlineData("CANCELLED")]
    [InlineData("SHIPPED")]
    public async Task A_status_that_is_not_open_is_refused_and_the_open_ones_are_listed(string status)
    {
        var act = () => _w.Service().GetFulfilmentStatusAsync(Ask(status));

        var message = (await act.Should().ThrowAsync<BadRequestException>()).Which.Message;
        message.Should().Contain($"'{status}'").And.Contain("RELEASED").And.Contain("PARTIALLY_DELIVERED").And.NotContain("DELIVERED,");
    }

    [Fact]
    public async Task The_warehouse_filter_lists_only_deliveries_leaving_that_warehouse_and_names_it()
    {
        Spread();

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask(warehouse: FulfilmentWorld.Karachi));

        Numbers(report).Should().BeEquivalentTo("DLV-3", "DLV-4");
        (report.Criteria.WarehouseUuid, report.Criteria.WarehouseName).Should().Be(((Guid?)FulfilmentWorld.Karachi, "Karachi Depot"));
        report.ByWarehouse.Select(w => w.WarehouseName).Should().Equal("Karachi Depot");
    }

    [Fact]
    public async Task A_warehouse_nobody_has_heard_of_is_not_found_rather_than_an_empty_one()
    {
        var act = () => _w.Service().GetFulfilmentStatusAsync(Ask(warehouse: Guid.NewGuid()));

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("self_pickup", "DLV-2")]
    [InlineData("SHIP", "DLV-1,DLV-3,DLV-4,DLV-5,DLV-6")]
    public async Task The_delivery_mode_filter_is_any_case(string mode, string expected)
    {
        Spread();

        Numbers(await _w.Service().GetFulfilmentStatusAsync(Ask(mode: mode))).Should().BeEquivalentTo(expected.Split(','));
    }

    [Fact]
    public async Task A_delivery_mode_that_does_not_exist_is_refused()
    {
        var act = () => _w.Service().GetFulfilmentStatusAsync(Ask(mode: "COURIER"));

        var message = (await act.Should().ThrowAsync<BadRequestException>()).Which.Message;
        message.Should().Contain("'COURIER'").And.Contain("SHIP").And.Contain("SELF_PICKUP");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_status_or_mode_means_no_filter(string blank)
    {
        Spread();

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask(blank, mode: blank));

        report.TotalRecords.Should().Be(6);
        (report.Criteria.Status, report.Criteria.DeliveryMode).Should().Be(((string?)null, (string?)null));
    }

    [Fact]
    public async Task All_the_filters_are_ANDed()
    {
        Spread();

        Numbers(await _w.Service().GetFulfilmentStatusAsync(Ask("released", FulfilmentWorld.Lahore, "self_pickup"))).Should().Equal("DLV-2");
        (await _w.Service().GetFulfilmentStatusAsync(Ask("released", FulfilmentWorld.Karachi, "self_pickup"))).Items.Should().BeEmpty();
    }

    // ── Paging and volume ────────────────────────────────────────────────────

    [Fact]
    public async Task A_page_is_a_slice_of_the_oldest_first_list_and_says_how_many_there_are_in_all()
    {
        for (var i = 1; i <= 5; i++) _w.Delivery($"DLV-{i}", created: new DateTime(2026, 9, i));

        var report = await _w.Service().GetFulfilmentStatusAsync(Ask(page: 2, pageSize: 2));

        Numbers(report).Should().Equal("DLV-3", "DLV-4");
        (report.Page, report.PageSize, report.TotalRecords, report.TotalPages).Should().Be((2, 2, 5, 3));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    public async Task Page_size_is_kept_between_one_and_a_hundred(int asked, int used)
    {
        _w.Delivery("DLV-1");

        (await _w.Service().GetFulfilmentStatusAsync(Ask(pageSize: asked))).PageSize.Should().Be(used);
    }

    [Fact]
    public async Task The_export_carries_every_open_delivery_whatever_the_paging_asked_for_and_an_empty_one_is_no_failure()
    {
        Spread();

        var report = await _w.Service().GetFulfilmentStatusForExportAsync(Ask(page: 4, pageSize: 1));
        report.Items.Should().HaveCount(6);
        (report.Page, report.PageSize, report.TotalPages).Should().Be((1, 6, 1));

        var empty = await new FulfilmentWorld().Service().GetFulfilmentStatusForExportAsync(Ask());
        (empty.Items.Count, empty.Page, empty.PageSize, empty.TotalPages).Should().Be((0, 1, 1, 0));
    }

    [Fact]
    public async Task An_export_takes_exactly_the_most_it_allows_and_refuses_one_more_until_filtered_but_a_page_is_never_refused()
    {
        _w.BulkDeliveries(FulfilmentReportService.MaxRows);
        (await _w.Service().GetFulfilmentStatusForExportAsync(Ask())).Items.Should().HaveCount(FulfilmentReportService.MaxRows);

        _w.Delivery("DLV-ONE-MORE", "PICKING");
        var act = () => _w.Service().GetFulfilmentStatusForExportAsync(Ask());

        var message = (await act.Should().ThrowAsync<BadRequestException>()).Which.Message;
        message.Should().Contain($"{FulfilmentReportService.MaxRows + 1} deliveries").And.Contain("Filter by status, warehouse or delivery mode");

        (await _w.Service().GetFulfilmentStatusForExportAsync(Ask("picking"))).Items.Should().ContainSingle();
        (await _w.Service().GetFulfilmentStatusAsync(Ask())).TotalRecords.Should().Be(FulfilmentReportService.MaxRows + 1);
    }

    // ── Tenancy and lookups ──────────────────────────────────────────────────

    [Fact]
    public async Task Another_organizations_deliveries_warehouses_and_orders_never_appear()
    {
        _w.Warehouse(FulfilmentWorld.Lahore, "Their Lahore", org: FulfilmentWorld.OtherOrg);
        _w.Warehouse(Guid.Parse("c0000000-0000-0000-0000-0000000000ff"), "Theirs Only", org: FulfilmentWorld.OtherOrg);
        var theirOrder = _w.Order("SO-THEIRS", org: FulfilmentWorld.OtherOrg);
        _w.Delivery("DLV-MINE");
        _w.Delivery("DLV-THEIRS", order: theirOrder, org: FulfilmentWorld.OtherOrg);
        _w.Delivery("DLV-THEIRS-2", warehouse: Guid.Parse("c0000000-0000-0000-0000-0000000000ff"), org: FulfilmentWorld.OtherOrg);

        var mine   = await _w.Service(FulfilmentWorld.Org).GetFulfilmentStatusAsync(Ask());
        var theirs = await _w.Service(FulfilmentWorld.OtherOrg).GetFulfilmentStatusAsync(Ask());

        Numbers(mine).Should().Equal("DLV-MINE");
        mine.ByWarehouse.Single().WarehouseName.Should().Be("Lahore Main");
        Numbers(theirs).Should().BeEquivalentTo("DLV-THEIRS", "DLV-THEIRS-2");
        theirs.ByWarehouse.Select(w => w.WarehouseName).Should().BeEquivalentTo("Their Lahore", "Theirs Only");
    }

    [Fact]
    public async Task Another_organizations_warehouse_is_not_found_and_the_export_is_scoped_too()
    {
        var theirs = Guid.Parse("c0000000-0000-0000-0000-0000000000ee");
        _w.Warehouse(theirs, "Theirs Only", org: FulfilmentWorld.OtherOrg);
        _w.Delivery("DLV-MINE");
        _w.Delivery("DLV-THEIRS", warehouse: theirs, org: FulfilmentWorld.OtherOrg);

        var act = () => _w.Service(FulfilmentWorld.Org).GetFulfilmentStatusAsync(Ask(warehouse: theirs));
        await act.Should().ThrowAsync<NotFoundException>();

        Numbers(await _w.Service(FulfilmentWorld.Org).GetFulfilmentStatusForExportAsync(Ask())).Should().Equal("DLV-MINE");
    }

    [Fact]
    public async Task Customer_names_are_asked_for_once_for_the_orders_on_the_page_and_never_when_there_are_none()
    {
        _w.Delivery("DLV-1", order: _w.Order("SO-1", _w.Acme()));
        _w.Delivery("DLV-2", order: _w.Order("SO-2", _w.Acme()));
        _w.Delivery("DLV-3", order: _w.Order("SO-3", _w.Globex()));
        var names = new Mock<ISupplierNameLookupService>();

        await _w.Service(names: names).GetFulfilmentStatusAsync(Ask());

        names.Verify(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Once);
        names.Verify(n => n.GetNamesAsync(It.Is<IReadOnlyList<Guid>>(ids => ids.Count == 2)), Times.Once);

        var none = new Mock<ISupplierNameLookupService>();
        await new FulfilmentWorld().Service(names: none).GetFulfilmentStatusAsync(Ask());
        none.Verify(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()), Times.Never);
    }

    // ── Header facts ─────────────────────────────────────────────────────────

    [Fact]
    public async Task The_company_name_is_trimmed_null_when_blank_and_the_time_is_the_clocks()
    {
        _w.CompanyName = "  Northwind Trading  ";
        var report = await _w.Service().GetFulfilmentStatusAsync(Ask());
        (report.CompanyName, report.GeneratedAt).Should().Be(("Northwind Trading", _w.Now));

        _w.CompanyName = " ";
        (await _w.Service().GetFulfilmentStatusAsync(Ask())).CompanyName.Should().BeNull();
    }

    [Fact]
    public async Task With_nothing_open_there_are_no_counts_and_no_pages()
    {
        var report = await _w.Service().GetFulfilmentStatusAsync(Ask());

        (report.ByStatus.Count, report.ByWarehouse.Count, report.ByDeliveryMode.Count, report.TotalRecords, report.TotalPages).Should().Be((0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task A_null_filter_is_a_programming_error()
    {
        var act = () => _w.Service().GetFulfilmentStatusAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}

file static class FulfilmentWorldNames
{
    internal static Guid Acme(this FulfilmentWorld _) => FulfilmentWorld.AcmeId;
    internal static Guid Globex(this FulfilmentWorld _) => FulfilmentWorld.GlobexId;
}
