using FluentAssertions;
using SMS.Modules.Reports.Models;
using Xunit;
using static SMS.Modules.Reports.Tests.ReceivablesReportWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-06 §15 R9 and R10 — what the in-memory provider cannot show: that the queries translate to SQL Server
/// at all (R9's signed opening sums grouped by direction, the distinct variants of a product, the day bounds on a
/// real datetime2, the tenant filter, and R10's join through Finance's own ranking) and that SQL Server and the
/// in-memory provider agree on every answer.
/// </summary>
public class ProductLedgerSqlServerTests
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

    // ── R9 ───────────────────────────────────────────────────────────────────

    private static async Task<(ReceivablesReportWorld Sql, ReceivablesReportWorld Memory, SqlServerHarness Harness)> LedgerBothAsync()
    {
        var harness = await SqlServerHarness.CreateAsync(finance: true);
        var sql     = new ReceivablesReportWorld { ContextFactory = org => harness.NewFinanceContext(org) };
        var memory  = new ReceivablesReportWorld();

        sql.SeedLedgerScatter();
        memory.SeedLedgerScatter();
        return (sql, memory, harness);
    }

    private static string Fingerprint(ProductLedgerReport r) => string.Join('\n',
        new[]
        {
            $"records {r.TotalRecords} page {r.Page} size {r.PageSize} pages {r.TotalPages}",
            $"criteria {r.Criteria.ProductUuid} {r.Criteria.ProductName} {r.Criteria.VariantUuid} {r.Criteria.VariantName} {r.Criteria.DateFrom:O} {r.Criteria.DateTo:O}",
            $"summary {r.Summary.OpeningQuantity:F4} {r.Summary.OpeningValue:F2} {r.Summary.QuantityIn:F4} {r.Summary.ValueIn:F2} {r.Summary.QuantityOut:F4} {r.Summary.ValueOut:F2} " +
            $"{r.Summary.ClosingQuantity:F4} {r.Summary.ClosingValue:F2} {r.Summary.ClosingWeightedAverageCost:F4} {r.Summary.MovementCount}"
        }
        .Concat(r.Items.Select(i =>
            $"{i.VariantUuid} {i.Sku} {i.VariantName} {i.EntryDate:O} {i.EntryType} {i.ReferenceNumber} {i.PartnerId} {i.PartnerName} {i.Direction} {i.Quantity:F4} {i.UnitCost:F4} {i.TotalCost:F2} " +
            $"{i.RunningQty:F4} {i.RunningValue:F2} {i.WeightedAverageCost:F4} {i.VariantRunningQty:F4} {i.VariantRunningValue:F2}")));

    private static readonly (DateTime? From, DateTime? To)[] Ranges =
    [
        (null, null), (D(8, 1), D(8, 31)), (D(9, 1), null), (null, D(8, 20)), (D(8, 15), D(8, 15)), (D(12, 1), null),
    ];

    private static readonly Guid FirstVariantOfFirstProduct = Guid.Parse("f1000000-0000-0000-0000-000000000001");

    [SqlServerFact]
    public async Task SQL_Server_and_the_in_memory_provider_give_the_same_product_ledger_for_every_product_variant_range_and_page()
    {
        var (sql, memory, harness) = await LedgerBothAsync();
        await using var _ = harness;

        var compared = 0;
        foreach (var product in ScatterProducts)
            foreach (var variant in new Guid?[] { null, FirstVariantOfFirstProduct })
                foreach (var (from, to) in Ranges)
                    foreach (var (page, size) in new[] { (1, 100), (2, 5) })
                    {
                        var filter   = new ProductLedgerReportFilter { ProductId = product, VariantId = variant, DateFrom = from, DateTo = to, Page = page, PageSize = size };
                        var onSql    = await sql.LedgerService().GetProductLedgerAsync(filter);
                        var inMemory = await memory.LedgerService().GetProductLedgerAsync(filter);

                        Agree(Fingerprint(onSql), Fingerprint(inMemory), $"{product}, variant {variant}, {from:d} to {to:d}, page {page}");
                        compared += onSql.TotalRecords + (int)onSql.Summary.OpeningQuantity;
                    }

        compared.Should().BeGreaterThan(300, "guards the comparison: books that matched nothing would agree with anything");
    }

    [SqlServerFact]
    public async Task The_product_ledger_export_reads_every_movement_in_the_same_order_on_SQL_Server()
    {
        var (sql, memory, harness) = await LedgerBothAsync();
        await using var _ = harness;

        var filter   = new ProductLedgerReportFilter { ProductId = ScatterProducts[0], Page = 3, PageSize = 2 };
        var onSql    = await sql.LedgerService().GetProductLedgerForExportAsync(filter);
        var inMemory = await memory.LedgerService().GetProductLedgerForExportAsync(filter);

        onSql.Items.Count.Should().BeGreaterThan(20);
        Agree(Fingerprint(onSql), Fingerprint(inMemory), "export");
    }

    [SqlServerFact]
    public async Task The_ledger_day_bounds_hold_to_the_last_tick_of_the_day_on_a_real_datetime2()
    {
        await using var harness = await SqlServerHarness.CreateAsync(finance: true);
        var w = new ReceivablesReportWorld { ContextFactory = org => harness.NewFinanceContext(org) };
        var (variant, product) = (Guid.NewGuid(), Guid.NewGuid());
        w.Catalog(variant, product, "Thing");

        w.Move(variant, product, "PURCHASE", 1m, D(9, 4).AddDays(1).AddTicks(-1), unitCost: 1m);
        w.Move(variant, product, "PURCHASE", 10m, D(9, 5), unitCost: 1m);
        w.Move(variant, product, "PURCHASE", 100m, D(9, 10).AddDays(1).AddTicks(-1), unitCost: 1m);
        w.Move(variant, product, "PURCHASE", 1000m, D(9, 11), unitCost: 1m);

        var report = await w.LedgerService().GetProductLedgerAsync(new ProductLedgerReportFilter { ProductId = product, DateFrom = D(9, 5), DateTo = D(9, 10) });

        report.Items.Select(i => i.Quantity).Should().Equal(10m, 100m);
        (report.Summary.OpeningQuantity, report.Summary.ClosingQuantity).Should().Be((1m, 111m));
    }

    [SqlServerFact]
    public async Task Another_organizations_movements_never_reach_a_product_ledger_or_its_opening_balance_on_SQL_Server()
    {
        var (sql, _, harness) = await LedgerBothAsync();
        await using var __ = harness;

        var filter = new ProductLedgerReportFilter { ProductId = ScatterProducts[0], DateFrom = D(8, 7) };
        var mine   = await sql.LedgerService(Org).GetProductLedgerForExportAsync(filter);
        var theirs = await sql.LedgerService(OtherOrg).GetProductLedgerForExportAsync(filter);

        theirs.Items.Should().NotBeEmpty();
        theirs.Summary.OpeningQuantity.Should().Be(3m + 4m, "their purchases of the 5th and 6th, and only theirs");
        mine.Items.Select(i => i.EntryUuid).Should().NotIntersectWith(theirs.Items.Select(i => i.EntryUuid));
    }

    // ── R10 ──────────────────────────────────────────────────────────────────

    private static async Task<(ReceivablesReportWorld Sql, ReceivablesReportWorld Memory, SqlServerHarness Harness)> SalesBothAsync()
    {
        var harness = await SqlServerHarness.CreateAsync(finance: true);
        var sql     = new ReceivablesReportWorld { ContextFactory = org => harness.NewFinanceContext(org) };
        var memory  = new ReceivablesReportWorld();

        sql.SeedSalesWithCosts();
        memory.SeedSalesWithCosts();
        return (sql, memory, harness);
    }

    private static string Fingerprint(ProfitabilityReport r) => string.Join('\n',
        new[]
        {
            $"records {r.TotalRecords} page {r.Page} size {r.PageSize} pages {r.TotalPages}",
            $"criteria {r.Criteria.DateFrom:O} {r.Criteria.DateTo:O}"
        }
        .Concat(r.Totals.Select(t => $"total {t.CurrencyCode} {t.ProductCount} {t.Revenue:F2} {t.CostOfGoodsSold:F2} {t.GrossProfit:F2} {t.MarginPercent:F2}"))
        .Concat(r.Items.Select(i => $"{i.Rank} {i.ProductUuid} {i.ProductName} {i.CurrencyCode} {i.QuantitySold:F4} {i.Revenue:F2} {i.CostOfGoodsSold:F2} {i.GrossProfit:F2} {i.MarginPercent:F2}")));

    [SqlServerFact]
    public async Task SQL_Server_and_the_in_memory_provider_give_the_same_product_profitability_for_every_range_and_page()
    {
        var (sql, memory, harness) = await SalesBothAsync();
        await using var _ = harness;

        var compared = 0;
        foreach (var (from, to) in Ranges)
            foreach (var (page, size) in new[] { (1, 100), (2, 3) })
            {
                var filter   = new ProfitabilityReportFilter { DateFrom = from, DateTo = to, Page = page, PageSize = size };
                var onSql    = await sql.LedgerService().GetProductProfitabilityAsync(filter);
                var inMemory = await memory.LedgerService().GetProductProfitabilityAsync(filter);

                Agree(Fingerprint(onSql), Fingerprint(inMemory), $"{from:d} to {to:d}, page {page}");
                compared += onSql.TotalRecords;
            }

        compared.Should().BeGreaterThan(8);
    }

    [SqlServerFact]
    public async Task The_profitability_export_reads_every_product_in_the_same_order_on_SQL_Server()
    {
        var (sql, memory, harness) = await SalesBothAsync();
        await using var _ = harness;

        var filter   = new ProfitabilityReportFilter { Page = 3, PageSize = 2 };
        var onSql    = await sql.LedgerService().GetProductProfitabilityForExportAsync(filter);
        var inMemory = await memory.LedgerService().GetProductProfitabilityForExportAsync(filter);

        onSql.Items.Count.Should().BeGreaterThan(4);
        Agree(Fingerprint(onSql), Fingerprint(inMemory), "export");
    }

    [SqlServerFact]
    public async Task Another_organizations_sales_never_reach_the_ranking_on_SQL_Server()
    {
        var (sql, _, harness) = await SalesBothAsync();
        await using var __ = harness;

        var mine   = await sql.LedgerService(Org).GetProductProfitabilityForExportAsync(new ProfitabilityReportFilter());
        var theirs = await sql.LedgerService(OtherOrg).GetProductProfitabilityForExportAsync(new ProfitabilityReportFilter());

        theirs.Items.Should().NotBeEmpty();
        mine.Totals.Sum(t => t.Revenue).Should().NotBe(theirs.Totals.Sum(t => t.Revenue));
    }
}
