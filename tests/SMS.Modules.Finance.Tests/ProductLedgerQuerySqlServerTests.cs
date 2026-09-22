using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using Xunit;
using Line = SMS.Modules.Finance.Tests.SalesBookSeeder.Line;

using static SMS.Modules.Finance.Tests.ProductLedgerRig;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P8-05 §11.4 on a real SQL Server. The in-memory provider will run any query that can be written,
/// including ones SQL Server cannot: these are the same reads as <c>ProductLedgerQueryTests</c>, over a
/// real database with the production retry strategy, to prove each of them translates and answers alike.
/// </summary>
public class ProductLedgerQuerySqlServerTests
{
    private static readonly DateTime Jan = new(2026, 1, 1);

    [FinanceSqlServerFact]
    public async Task The_profitability_report_translates_and_agrees_with_a_hand_worked_book_of_sales()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);
        var org = Guid.NewGuid();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var variants = new FakeVariants();
        variants.Products[a] = a; variants.Products[b] = b;
        variants.ProductNames[a] = "Alpha"; variants.ProductNames[b] = "Beta";
        var seeder = new SalesBookSeeder();

        await using (var seed = h.NewContext(org))
            await SalesBookSeeder.WriteAsync(seed,
                seeder.Sale(org, "SINV-1", "ISSUED", "PKR", Jan.AddDays(4),
                    new Line(a, a, Qty: 30m, Price: 15m, Cost: 10m),
                    new Line(b, b, Qty: 10m, Price: 100m, Cost: 40m, Discount: 10m, Tax: 17m)),
                seeder.Sale(org, "SINV-2", "PAID", "PKR", Jan.AddDays(19),
                    new Line(a, a, Qty: 20m, Price: 15m, Cost: 10m),
                    new Line(a, a, Qty: 10m, Price: 15m, Cost: 10m)),                    // two lines of one variant
                seeder.Sale(org, "SINV-3", "DRAFT", "PKR", Jan.AddDays(5), new Line(a, a, Qty: 99m, Price: 15m, Cost: 10m)),
                seeder.Sale(org, "SINV-4", "ISSUED", "USD", Jan.AddDays(5), new Line(a, a, Qty: 3m, Price: 2m, Cost: 10m)));

        await using var db = h.NewContext(org);
        var svc = new ProductLedgerQueryService(db, variants);

        var all = await svc.GetProfitabilityAsync(new ProductProfitabilityFilter());

        all.Data.Select(r => (r.ProductName, r.CurrencyCode, r.QuantitySold, r.Revenue, r.CostOfGoodsSold, r.GrossProfit)).Should().Equal(
            ("Beta",  "PKR", 10m, 900m, 400m, 500m),
            ("Alpha", "PKR", 60m, 900m, 600m, 300m),
            ("Alpha", "USD", 3m,  6m,   30m,  -24m));
        all.TotalRecords.Should().Be(3);

        var early = await svc.GetProfitabilityAsync(new ProductProfitabilityFilter { DateFrom = Jan, DateTo = Jan.AddDays(9) });
        early.Data.Select(r => (r.ProductName, r.CurrencyCode, r.QuantitySold, r.Revenue)).Should().Equal(
            ("Beta", "PKR", 10m, 900m), ("Alpha", "PKR", 30m, 450m), ("Alpha", "USD", 3m, 6m));

        var page = await svc.GetProfitabilityAsync(new ProductProfitabilityFilter { PageSize = 1, Page = 2 });
        page.Data.Should().ContainSingle().Which.ProductName.Should().Be("Alpha");
        page.TotalPages.Should().Be(3);
    }

    [FinanceSqlServerFact]
    public async Task A_variants_ledger_and_summary_translate_and_carry_the_running_figures()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);
        var org = Guid.NewGuid();
        var variants = new FakeVariants();
        var (variant, product) = variants.New();

        await using (var write = h.NewContext(org))
        {
            var ledger = new ProductLedgerService(write, variants);
            await ledger.AppendEntryAsync(Buy(variant, 100m, 10m));
            await ledger.AppendEntryAsync(Sell(variant, 30m));
            await ledger.AppendEntryAsync(Buy(variant, 50m, 16m));
        }

        await using var db = h.NewContext(org);
        var svc = new ProductLedgerQueryService(db, variants);

        var page = await svc.GetLedgerAsync(variant, new ProductLedgerFilter { PageSize = 2 });
        page.TotalRecords.Should().Be(3);
        page.Data.Select(e => (e.SequenceNo, e.EntryType, e.RunningQty, e.RunningValue, e.WeightedAverageCost)).Should().Equal(
            (3, "PURCHASE", 120m, 1500m, 12.5m),
            (2, "SALE", 70m, 700m, 10m));

        (await svc.GetLedgerAsync(variant, new ProductLedgerFilter { EntryType = "PURCHASE", Direction = "IN" })).Data.Should().HaveCount(2);

        var summary = await svc.GetSummaryAsync(variant);
        (summary.ProductUuid, summary.PurchasedQuantity, summary.PurchasedCost, summary.SoldQuantity, summary.CostOfGoodsSold)
            .Should().Be((product, 150m, 1800m, 30m, 300m));
        (summary.CurrentQuantity, summary.StockValue, summary.WeightedAverageCost, summary.EntryCount).Should().Be((120m, 1500m, 12.5m, 3));

        (await svc.GetSummaryAsync(Guid.NewGuid())).EntryCount.Should().Be(0);
    }
}
