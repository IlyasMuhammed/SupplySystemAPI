using FluentAssertions;
using SMS.Modules.Reports.Models;
using Xunit;
using static SMS.Modules.Reports.Tests.ReceivablesReportWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-04 §15 R4, R5 and R6 — what the in-memory provider cannot show: that the queries translate to SQL
/// Server at all (the grouped sums over invoice lines, the distinct count of orders, the semi-join to the
/// ledger, the three group-bys and the line sums of the deliveries, the day bounds on a real datetime2, the
/// tenant filter) and that SQL Server and the in-memory provider agree on every answer.
/// </summary>
public class SalesAnalysisSqlServerTests
{
    // ── R4 and R5 ────────────────────────────────────────────────────────────

    /// <summary>The same fifty invoices in a real SQL Server database and in the in-memory one; the ids each generates differ, so numbers, dates and amounts are compared.</summary>
    private static async Task<(ReceivablesReportWorld Sql, ReceivablesReportWorld Memory, SqlServerHarness Harness)> SalesBothAsync()
    {
        var harness = await SqlServerHarness.CreateAsync(finance: true);
        var sql     = new ReceivablesReportWorld { ContextFactory = org => harness.NewFinanceContext(org) };
        var memory  = new ReceivablesReportWorld();

        sql.SeedSales();
        memory.SeedSales();
        return (sql, memory, harness);
    }

    private static string Fingerprint(SalesByProductReport r) => string.Join('\n',
        new[]
        {
            $"records {r.TotalRecords} page {r.Page} size {r.PageSize} pages {r.TotalPages}",
            $"criteria {r.Criteria.DateFrom:O} {r.Criteria.DateTo:O} {r.Criteria.PartnerId} {r.Criteria.CustomerName}"
        }
        .Concat(r.Totals.Select(t => $"total {t.CurrencyCode} {t.ProductCount} {t.Revenue:F2}"))
        .Concat(r.Items.Select(i => $"{i.ProductUuid} {i.ProductName} {i.CurrencyCode} {i.QuantitySold:F4} {i.Revenue:F2} {i.AverageUnitPrice:F2}")));

    private static string Fingerprint(SalesByCustomerReport r) => string.Join('\n',
        new[]
        {
            $"records {r.TotalRecords} page {r.Page} size {r.PageSize} pages {r.TotalPages}",
            $"criteria {r.Criteria.DateFrom:O} {r.Criteria.DateTo:O}"
        }
        .Concat(r.Totals.Select(t => $"total {t.CurrencyCode} {t.CustomerCount} {t.OrderCount} {t.InvoiceCount} {t.Revenue:F2} {t.AverageOrderValue:F2}"))
        .Concat(r.Items.Select(i => $"{i.PartnerId} {i.CustomerName} {i.CurrencyCode} {i.OrderCount} {i.InvoiceCount} {i.Revenue:F2} {i.AverageOrderValue:F2}")));

    private static readonly (DateTime? From, DateTime? To)[] Ranges =
    [
        (null, null), (D(8, 1), D(8, 31)), (D(9, 1), null), (null, D(8, 20)), (D(8, 15), D(8, 15)), (D(12, 1), null),
    ];

    [SqlServerFact]
    public async Task SQL_Server_and_the_in_memory_provider_give_the_same_sales_by_product_for_every_range_customer_and_page()
    {
        var (sql, memory, harness) = await SalesBothAsync();
        await using var _ = harness;

        var compared = 0;
        foreach (var partner in new Guid?[] { null, sql.Acme, sql.Globex, Stranger })
            foreach (var (from, to) in Ranges)
                foreach (var (page, size) in new[] { (1, 20), (2, 3) })
                {
                    var filter   = new SalesByProductFilter { DateFrom = from, DateTo = to, PartnerId = partner, Page = page, PageSize = size };
                    var onSql    = await sql.AnalysisService().GetSalesByProductAsync(filter);
                    var inMemory = await memory.AnalysisService().GetSalesByProductAsync(filter);

                    Fingerprint(onSql).Should().Be(Fingerprint(inMemory), $"customer {partner}, {from:d} to {to:d}, page {page}");
                    compared += onSql.TotalRecords;
                }

        compared.Should().BeGreaterThan(100, "guards the comparison: books that matched nothing would agree with anything");
    }

    [SqlServerFact]
    public async Task SQL_Server_and_the_in_memory_provider_give_the_same_sales_by_customer_for_every_range_and_page()
    {
        var (sql, memory, harness) = await SalesBothAsync();
        await using var _ = harness;

        var compared = 0;
        foreach (var (from, to) in Ranges)
            foreach (var (page, size) in new[] { (1, 20), (2, 1) })
            {
                var filter   = new SalesByCustomerFilter { DateFrom = from, DateTo = to, Page = page, PageSize = size };
                var onSql    = await sql.AnalysisService().GetSalesByCustomerAsync(filter);
                var inMemory = await memory.AnalysisService().GetSalesByCustomerAsync(filter);

                Fingerprint(onSql).Should().Be(Fingerprint(inMemory), $"{from:d} to {to:d}, page {page}");
                compared += onSql.TotalRecords;
            }

        compared.Should().BeGreaterThan(8);
    }

    [SqlServerFact]
    public async Task The_sales_exports_read_every_row_in_the_same_order_on_SQL_Server()
    {
        var (sql, memory, harness) = await SalesBothAsync();
        await using var _ = harness;

        var product  = new SalesByProductFilter { Page = 3, PageSize = 2 };
        var customer = new SalesByCustomerFilter { Page = 3, PageSize = 2 };

        var productOnSql = await sql.AnalysisService().GetSalesByProductForExportAsync(product);
        productOnSql.Items.Count.Should().BeGreaterThan(5);
        Fingerprint(productOnSql).Should().Be(Fingerprint(await memory.AnalysisService().GetSalesByProductForExportAsync(product)));

        var customerOnSql = await sql.AnalysisService().GetSalesByCustomerForExportAsync(customer);
        customerOnSql.Items.Count.Should().BeGreaterThan(2);
        Fingerprint(customerOnSql).Should().Be(Fingerprint(await memory.AnalysisService().GetSalesByCustomerForExportAsync(customer)));
    }

    [SqlServerFact]
    public async Task The_sales_day_bounds_hold_to_the_last_tick_of_the_day_on_a_real_datetime2()
    {
        await using var harness = await SqlServerHarness.CreateAsync(finance: true);
        var w = new ReceivablesReportWorld { ContextFactory = org => harness.NewFinanceContext(org) };
        var item = Guid.NewGuid();
        w.Stock(item, Guid.NewGuid(), "Thing");

        foreach (var (number, issued, price) in new[]
        {
            ("SINV-BEFORE",    D(9, 4).AddDays(1).AddTicks(-1), 1m),
            ("SINV-FIRST",     D(9, 5),                          10m),
            ("SINV-LAST-TICK", D(9, 10).AddDays(1).AddTicks(-1), 100m),
            ("SINV-NEXT-DAY",  D(9, 11),                         1000m),
        })
            w.Sale(w.Acme, number, issued, [new ReceivablesReportWorld.SoldLine(item, 1m, price)]);

        var byProduct  = await w.AnalysisService().GetSalesByProductAsync(new SalesByProductFilter { DateFrom = D(9, 5), DateTo = D(9, 10) });
        var byCustomer = await w.AnalysisService().GetSalesByCustomerAsync(new SalesByCustomerFilter { DateFrom = D(9, 5), DateTo = D(9, 10) });

        byProduct.Items.Single().Revenue.Should().Be(110m);
        byCustomer.Items.Single().Should().Match<SalesByCustomerItem>(i => i.Revenue == 110m && i.InvoiceCount == 2);
    }

    [SqlServerFact]
    public async Task Another_organizations_sales_never_reach_either_sales_report_on_SQL_Server()
    {
        var (sql, _, harness) = await SalesBothAsync();
        await using var __ = harness;

        var mine   = await sql.AnalysisService(Org).GetSalesByCustomerForExportAsync(new SalesByCustomerFilter());
        var theirs = await sql.AnalysisService(OtherOrg).GetSalesByCustomerForExportAsync(new SalesByCustomerFilter());
        var all    = mine.Totals.Sum(t => t.InvoiceCount) + theirs.Totals.Sum(t => t.InvoiceCount);

        theirs.Totals.Should().NotBeEmpty();
        mine.Totals.Sum(t => t.InvoiceCount).Should().BeLessThan(all);

        // Each organization's product report adds up to what its own customer report does, before rounding at the header.
        var theirProducts = await sql.AnalysisService(OtherOrg).GetSalesByProductForExportAsync(new SalesByProductFilter());
        foreach (var currency in theirs.Totals.Select(t => t.CurrencyCode))
            Math.Abs(theirProducts.Totals.Single(t => t.CurrencyCode == currency).Revenue - theirs.Totals.Single(t => t.CurrencyCode == currency).Revenue)
                .Should().BeLessThan(0.05m);
    }

    // ── R6 ───────────────────────────────────────────────────────────────────

    private static async Task<(FulfilmentWorld Sql, FulfilmentWorld Memory, SqlServerHarness Harness)> FulfilmentBothAsync()
    {
        var harness = await SqlServerHarness.CreateAsync(demand: true, logistics: true, inventory: true);
        var sql = new FulfilmentWorld(seedWarehouses: false)
        {
            LogisticsFactory = org => harness.NewLogisticsContext(org),
            DemandFactory    = org => harness.NewDemandContext(org),
            InventoryFactory = org => harness.NewInventoryContext(org)
        };
        sql.SeedWarehouses();
        var memory = new FulfilmentWorld();

        sql.SeedScatter();
        memory.SeedScatter();
        return (sql, memory, harness);
    }

    private static string Fingerprint(FulfilmentStatusReport r) => string.Join('\n',
        new[]
        {
            $"records {r.TotalRecords} page {r.Page} size {r.PageSize} pages {r.TotalPages}",
            $"criteria {r.Criteria.Status} {r.Criteria.WarehouseUuid} {r.Criteria.WarehouseName} {r.Criteria.DeliveryMode}"
        }
        .Concat(r.ByStatus.Select(s => $"status {s.Status} {s.Count}"))
        .Concat(r.ByWarehouse.Select(w => $"warehouse {w.WarehouseUuid} {w.WarehouseName} {w.Count}"))
        .Concat(r.ByDeliveryMode.Select(m => $"mode {m.DeliveryMode} {m.Count}"))
        .Concat(r.Items.Select(i =>
            $"{i.DeliveryNumber} {i.SaleOrderNumber} {i.PartnerId} {i.CustomerName} {i.Status} {i.DeliveryMode} {i.WarehouseUuid} {i.WarehouseName} " +
            $"{i.RequestedDate:O} {i.PromisedDate:O} {i.CreatedDate:O} {i.DaysOpen} {i.LineCount} {i.QuantityOrdered:F4} {i.QuantityDelivered:F4}")));

    [SqlServerFact]
    public async Task SQL_Server_and_the_in_memory_provider_give_the_same_fulfilment_report_for_every_filter_and_page()
    {
        var (sql, memory, harness) = await FulfilmentBothAsync();
        await using var _ = harness;

        var filters = new List<FulfilmentStatusFilter> { new() { PageSize = 100 } };
        filters.AddRange(FulfilmentReportStatuses().Select(s => new FulfilmentStatusFilter { Status = s, PageSize = 100 }));
        filters.AddRange(new Guid?[] { FulfilmentWorld.Lahore, FulfilmentWorld.Karachi, FulfilmentWorld.Multan }.Select(w => new FulfilmentStatusFilter { WarehouseId = w, PageSize = 100 }));
        filters.AddRange(new[] { "SHIP", "self_pickup" }.Select(m => new FulfilmentStatusFilter { DeliveryMode = m, PageSize = 100 }));
        filters.Add(new FulfilmentStatusFilter { Status = "released", WarehouseId = FulfilmentWorld.Lahore, DeliveryMode = "SHIP", PageSize = 100 });
        filters.AddRange(new[] { 1, 2, 3, 5 }.Select(p => new FulfilmentStatusFilter { Page = p, PageSize = 7 }));

        var compared = 0;
        foreach (var filter in filters)
        {
            var onSql    = await sql.Service().GetFulfilmentStatusAsync(filter);
            var inMemory = await memory.Service().GetFulfilmentStatusAsync(filter);

            Fingerprint(onSql).Should().Be(Fingerprint(inMemory), $"filter {System.Text.Json.JsonSerializer.Serialize(filter)}");
            compared += onSql.TotalRecords;
        }

        compared.Should().BeGreaterThan(100, "guards the comparison: books that matched nothing would agree with anything");
    }

    private static IEnumerable<string> FulfilmentReportStatuses() => Services.FulfilmentReportService.OpenStatuses;

    [SqlServerFact]
    public async Task The_fulfilment_export_reads_every_open_delivery_in_the_same_order_on_SQL_Server()
    {
        var (sql, memory, harness) = await FulfilmentBothAsync();
        await using var _ = harness;

        var filter   = new FulfilmentStatusFilter { Page = 4, PageSize = 2 };
        var onSql    = await sql.Service().GetFulfilmentStatusForExportAsync(filter);
        var inMemory = await memory.Service().GetFulfilmentStatusForExportAsync(filter);

        onSql.Items.Count.Should().BeGreaterThan(20);
        Fingerprint(onSql).Should().Be(Fingerprint(inMemory));
    }

    [SqlServerFact]
    public async Task Another_organizations_deliveries_and_warehouses_never_reach_the_fulfilment_report_on_SQL_Server()
    {
        var (sql, _, harness) = await FulfilmentBothAsync();
        await using var __ = harness;

        var mine   = await sql.Service(FulfilmentWorld.Org).GetFulfilmentStatusForExportAsync(new FulfilmentStatusFilter());
        var theirs = await sql.Service(FulfilmentWorld.OtherOrg).GetFulfilmentStatusForExportAsync(new FulfilmentStatusFilter());

        theirs.Items.Should().NotBeEmpty();
        mine.Items.Select(i => i.DeliveryNumber).Should().NotIntersectWith(theirs.Items.Select(i => i.DeliveryNumber));
        mine.Items.Should().OnlyContain(i => i.SaleOrderNumber != "SO-THEIRS");
    }
}
