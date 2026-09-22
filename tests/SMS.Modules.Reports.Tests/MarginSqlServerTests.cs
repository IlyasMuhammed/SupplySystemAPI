using FluentAssertions;
using SMS.Modules.Reports.Models;
using Xunit;
using static SMS.Modules.Reports.Tests.ReceivablesReportWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-05 §15 R7 and R8 — what the in-memory provider cannot show: that the queries translate to SQL Server
/// at all (R7's correlated sums over the purchase order lines behind each order line, R8's earliest-ledger-day
/// subquery and grouped sum of cost, the day bounds on a real datetime2, the tenant filter) and that SQL Server
/// and the in-memory provider agree on every answer.
/// </summary>
public class MarginSqlServerTests
{
    /// <summary>Two fingerprints that must be equal; when they are not, the first line that differs, which is what anyone needs to see.</summary>
    private static void Agree(string sql, string memory, string context)
    {
        var a = sql.Split('\n');
        var b = memory.Split('\n');
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            if (a.ElementAtOrDefault(i) != b.ElementAtOrDefault(i))
                throw new Xunit.Sdk.XunitException($"{context}: line {i} differs.\n  SQL:    {a.ElementAtOrDefault(i)}\n  memory: {b.ElementAtOrDefault(i)}");
    }
    // ── R7 ───────────────────────────────────────────────────────────────────

    private static async Task<(MarginWorld Sql, MarginWorld Memory, SqlServerHarness Harness)> OrdersBothAsync()
    {
        var harness = await SqlServerHarness.CreateAsync(demand: true);
        var sql     = new MarginWorld { ContextFactory = org => harness.NewDemandContext(org) };
        var memory  = new MarginWorld();

        sql.SeedScatter();
        memory.SeedScatter();
        return (sql, memory, harness);
    }

    /// <summary>An order's id is generated, so a row by order is compared by its number and customer, and the rest by id.</summary>
    private static string Fingerprint(MarginAnalysisReport r) => string.Join('\n',
        new[]
        {
            $"records {r.TotalRecords} page {r.Page} size {r.PageSize} pages {r.TotalPages}",
            $"criteria {r.Criteria.DateFrom:O} {r.Criteria.DateTo:O} {r.Criteria.GroupBy}"
        }
        .Concat(r.Totals.Select(t => $"total {t.CurrencyCode} {t.GroupCount} {t.LineCount} {t.UncostedLineCount} {t.SellingValue:F2} {t.Cost:F2} {t.Margin:F2} {t.MarginPercent:F2}"))
        .Concat(r.Items.Select(i =>
            $"{(r.Criteria.GroupBy == MarginGroupings.Order ? "-" : i.GroupId.ToString())} {i.Name} {i.Detail} {i.CurrencyCode} {i.LineCount} {i.Quantity:F4} " +
            $"{i.SellingValue:F2} {i.Cost:F2} {i.Margin:F2} {i.MarginPercent:F2} {i.AverageSellingPrice:F2} {i.AverageCost:F2}")));

    private static readonly (DateTime? From, DateTime? To)[] OrderRanges =
    [
        (null, null), (D(8, 1), D(8, 31)), (D(9, 1), null), (null, D(8, 20)), (D(8, 15), D(8, 15)), (D(12, 1), null),
    ];

    [SqlServerFact]
    public async Task SQL_Server_and_the_in_memory_provider_give_the_same_margin_analysis_for_every_grouping_range_and_page()
    {
        var (sql, memory, harness) = await OrdersBothAsync();
        await using var _ = harness;

        var compared = 0;
        foreach (var groupBy in new[] { "product", "customer", "order" })
            foreach (var (from, to) in OrderRanges)
                foreach (var (page, size) in new[] { (1, 20), (2, 3) })
                {
                    var filter   = new MarginAnalysisFilter { GroupBy = groupBy, DateFrom = from, DateTo = to, Page = page, PageSize = size };
                    var onSql    = await sql.Service().GetMarginAnalysisAsync(filter);
                    var inMemory = await memory.Service().GetMarginAnalysisAsync(filter);

                    Agree(Fingerprint(onSql), Fingerprint(inMemory), $"{groupBy}, {from:d} to {to:d}, page {page}");
                    compared += onSql.TotalRecords + onSql.Totals.Sum(t => t.UncostedLineCount);
                }

        compared.Should().BeGreaterThan(100, "guards the comparison: books that matched nothing would agree with anything");
    }

    [SqlServerFact]
    public async Task The_margin_export_reads_every_row_in_the_same_order_on_SQL_Server()
    {
        var (sql, memory, harness) = await OrdersBothAsync();
        await using var _ = harness;

        foreach (var groupBy in new[] { "product", "customer", "order" })
        {
            var filter   = new MarginAnalysisFilter { GroupBy = groupBy, Page = 3, PageSize = 2 };
            var onSql    = await sql.Service().GetMarginAnalysisForExportAsync(filter);
            var inMemory = await memory.Service().GetMarginAnalysisForExportAsync(filter);

            onSql.Items.Count.Should().BeGreaterThan(2, groupBy);
            Agree(Fingerprint(onSql), Fingerprint(inMemory), groupBy);
        }
    }

    [SqlServerFact]
    public async Task The_order_date_bounds_hold_to_the_last_tick_of_the_day_on_a_real_datetime2()
    {
        await using var harness = await SqlServerHarness.CreateAsync(demand: true);
        var w = new MarginWorld { ContextFactory = org => harness.NewDemandContext(org) };
        var thing = Guid.NewGuid();
        w.Catalog(thing, Guid.NewGuid(), "Thing");

        foreach (var (number, date, price) in new[]
        {
            ("SO-BEFORE",    D(9, 4).AddDays(1).AddTicks(-1), 1m),
            ("SO-FIRST",     D(9, 5),                          10m),
            ("SO-LAST-TICK", D(9, 10).AddDays(1).AddTicks(-1), 100m),
            ("SO-NEXT-DAY",  D(9, 11),                         1000m),
        })
            w.Order(number, date, [MarginWorld.Line(thing, 1m, price, new MarginWorld.Buy(1m, price / 2m))]);

        var report = await w.Service().GetMarginAnalysisAsync(new MarginAnalysisFilter { DateFrom = D(9, 5), DateTo = D(9, 10) });

        report.Totals.Single().SellingValue.Should().Be(110m);
    }

    [SqlServerFact]
    public async Task Another_organizations_orders_and_purchase_orders_never_reach_the_margin_analysis_on_SQL_Server()
    {
        var (sql, _, harness) = await OrdersBothAsync();
        await using var __ = harness;

        var mine   = await sql.Service(MarginWorld.Org).GetMarginAnalysisForExportAsync(new MarginAnalysisFilter { GroupBy = "order" });
        var theirs = await sql.Service(MarginWorld.OtherOrg).GetMarginAnalysisForExportAsync(new MarginAnalysisFilter { GroupBy = "order" });

        theirs.Items.Should().NotBeEmpty();
        mine.Items.Select(i => i.Name).Should().NotIntersectWith(theirs.Items.Select(i => i.Name));
    }

    // ── R8 ───────────────────────────────────────────────────────────────────

    private static async Task<(ReceivablesReportWorld Sql, ReceivablesReportWorld Memory, SqlServerHarness Harness)> SalesBothAsync()
    {
        var harness = await SqlServerHarness.CreateAsync(finance: true);
        var sql     = new ReceivablesReportWorld { ContextFactory = org => harness.NewFinanceContext(org) };
        var memory  = new ReceivablesReportWorld();

        sql.SeedSalesWithCosts();
        memory.SeedSalesWithCosts();
        return (sql, memory, harness);
    }

    private static string Fingerprint(SalesVsPurchaseReport r) => string.Join('\n',
        new[]
        {
            $"records {r.TotalRecords} page {r.Page} size {r.PageSize} pages {r.TotalPages}",
            $"criteria {r.Criteria.DateFrom:O} {r.Criteria.DateTo:O} {r.Criteria.Period}"
        }
        .Concat(r.Totals.Select(t => $"total {t.CurrencyCode} {t.PeriodCount} {t.InvoiceCount} {t.Revenue:F2} {t.CostOfGoodsSold:F2} {t.GrossMargin:F2} {t.GrossMarginPercent:F2} {t.UncostedRevenue:F2}"))
        .Concat(r.Items.Select(i => $"{i.PeriodLabel} {i.PeriodStart:O} {i.CurrencyCode} {i.InvoiceCount} {i.Revenue:F2} {i.CostOfGoodsSold:F2} {i.GrossMargin:F2} {i.GrossMarginPercent:F2} {i.UncostedRevenue:F2}")));

    [SqlServerFact]
    public async Task SQL_Server_and_the_in_memory_provider_give_the_same_sales_vs_purchase_for_every_period_range_and_page()
    {
        var (sql, memory, harness) = await SalesBothAsync();
        await using var _ = harness;

        var compared = 0;
        foreach (var period in new[] { "day", "week", "month" })
            foreach (var (from, to) in OrderRanges)
                foreach (var (page, size) in new[] { (1, 20), (2, 3) })
                {
                    var filter   = new SalesVsPurchaseFilter { Period = period, DateFrom = from, DateTo = to, Page = page, PageSize = size };
                    var onSql    = await sql.AnalysisService().GetSalesVsPurchaseAsync(filter);
                    var inMemory = await memory.AnalysisService().GetSalesVsPurchaseAsync(filter);

                    Agree(Fingerprint(onSql), Fingerprint(inMemory), $"{period}, {from:d} to {to:d}, page {page}");
                    compared += onSql.TotalRecords;
                }

        compared.Should().BeGreaterThan(100, "guards the comparison: books that matched nothing would agree with anything");
    }

    [SqlServerFact]
    public async Task The_sales_vs_purchase_export_reads_every_period_in_the_same_order_on_SQL_Server()
    {
        var (sql, memory, harness) = await SalesBothAsync();
        await using var _ = harness;

        var filter   = new SalesVsPurchaseFilter { Period = "week", Page = 2, PageSize = 2 };
        var onSql    = await sql.AnalysisService().GetSalesVsPurchaseForExportAsync(filter);
        var inMemory = await memory.AnalysisService().GetSalesVsPurchaseForExportAsync(filter);

        onSql.Items.Count.Should().BeGreaterThan(8);
        Agree(Fingerprint(onSql), Fingerprint(inMemory), "week export");
    }

    [SqlServerFact]
    public async Task The_sales_vs_purchase_day_bounds_hold_to_the_last_tick_of_the_day_and_cost_follows_its_invoice_on_a_real_datetime2()
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
            w.BookCost(w.Sale(w.Acme, number, issued, [new ReceivablesReportWorld.SoldLine(item, 1m, price)]), price / 2m);

        var report = await w.AnalysisService().GetSalesVsPurchaseAsync(new SalesVsPurchaseFilter { DateFrom = D(9, 5), DateTo = D(9, 10), Period = "DAY" });

        report.Items.Select(i => (i.PeriodLabel, i.Revenue, i.CostOfGoodsSold)).Should().Equal(("2026-09-05", 10m, 5m), ("2026-09-10", 100m, 50m));
    }

    [SqlServerFact]
    public async Task Another_organizations_sales_and_cost_never_reach_sales_vs_purchase_on_SQL_Server()
    {
        var (sql, _, harness) = await SalesBothAsync();
        await using var __ = harness;

        var mine   = await sql.AnalysisService(Org).GetSalesVsPurchaseForExportAsync(new SalesVsPurchaseFilter());
        var theirs = await sql.AnalysisService(OtherOrg).GetSalesVsPurchaseForExportAsync(new SalesVsPurchaseFilter());

        theirs.Totals.Should().NotBeEmpty();
        mine.Totals.Sum(t => t.InvoiceCount).Should().BeLessThan(mine.Totals.Sum(t => t.InvoiceCount) + theirs.Totals.Sum(t => t.InvoiceCount));

        // What each organization's report says agrees with its own customer report, so neither has taken the other's invoices.
        foreach (var org in new[] { Org, OtherOrg })
        {
            var vs       = await sql.AnalysisService(org).GetSalesVsPurchaseForExportAsync(new SalesVsPurchaseFilter());
            var customer = await sql.AnalysisService(org).GetSalesByCustomerForExportAsync(new SalesByCustomerFilter());
            vs.Totals.Select(t => (t.CurrencyCode, t.InvoiceCount, t.Revenue)).Should().Equal(customer.Totals.Select(t => (t.CurrencyCode, t.InvoiceCount, t.Revenue)));
        }
    }
}
