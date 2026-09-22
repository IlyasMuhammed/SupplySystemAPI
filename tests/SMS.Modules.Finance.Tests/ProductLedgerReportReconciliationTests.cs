using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P9-06 §15 R9 and R10 against the books the <i>real</i> ledger writer produces. R9's own tests build
/// their ledgers by hand, which shows what the report decides; these show that what it decides is what the
/// writer did. Random histories over the three variants of one product — receipts at different prices, sales,
/// returns either way, adjustments, write-offs, sold down to nothing — are written through
/// <see cref="ProductLedgerService"/>, and the report's product-level running figures are held to each
/// variant's own running totals as the ledger stored them. R10 is held to the endpoint it wraps.
/// </summary>
public class ProductLedgerReportReconciliationTests
{
    private const int User = ReceivablesDesk.User;
    private const int Steps = 40;

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 15).Select(seed => new object[] { seed });

    private static ProductLedgerPosting Direct(Guid variant, string type, string direction, decimal qty, decimal? cost) =>
        new(variant, type, direction, qty, cost, "Doc", Guid.NewGuid(), "DOC-1", User);

    private static decimal Quantity(Random rng) => rng.Next(10) < 7 ? rng.Next(1, 100_000) / 100m : rng.Next(1, 10_000_000) / 10_000m;
    private static decimal Cost(Random rng) => rng.Next(100) < 5 ? 0m : rng.Next(10) < 7 ? rng.Next(1, 100_000) / 100m : rng.Next(1, 10_000_000) / 10_000m;
    private static decimal PartOf(Random rng, decimal held) => Math.Min(held, Math.Max(0.0001m, Math.Round(held * rng.Next(1, 100) / 100m, 4)));

    private sealed record Run(ReceivablesWorld World, ReceivablesDesk Desk, Guid Product, Guid[] Variants);

    /// <summary>Forty operations, one every thirty hours, on the three variants of one product.</summary>
    private static async Task<Run> HistoryAsync(int seed)
    {
        var rng   = new Random(seed);
        var world = new ReceivablesWorld();
        var desk  = world.For(Guid.NewGuid());

        var (first, product) = world.Variants.New();
        var variants = new[] { first, world.Variants.New(product).Variant, world.Variants.New(product).Variant };
        world.Variants.ProductNames[product] = "Laptop";
        var held = variants.ToDictionary(v => v, _ => 0m);

        var start = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
        for (var step = 1; step <= Steps; step++)
        {
            world.Clock.Value = start.AddHours(step * 30);
            var v = variants[rng.Next(variants.Length)];

            if (held[v] == 0m || rng.Next(100) < 35)
            {
                var qty = Quantity(rng);
                await desk.AppendAsync(Direct(v, "PURCHASE", "IN", qty, Cost(rng)));
                held[v] += qty;
                continue;
            }

            switch (rng.Next(6))
            {
                case 0 or 1:
                {
                    var take = rng.Next(4) == 0 ? held[v] : PartOf(rng, held[v]);
                    await desk.AppendAsync(Direct(v, "SALE", "OUT", take, null));
                    held[v] -= take;
                    break;
                }
                case 2:
                {
                    var take = PartOf(rng, held[v]);
                    await desk.AppendAsync(Direct(v, rng.Next(2) == 0 ? "WRITE_OFF" : "RETURN_OUT", "OUT", take, null));
                    held[v] -= take;
                    break;
                }
                case 3:
                {
                    var take = PartOf(rng, held[v]);
                    await desk.AppendAsync(Direct(v, "ADJUSTMENT", "OUT", take, null));
                    held[v] -= take;
                    break;
                }
                default:
                {
                    var qty = Quantity(rng);
                    await desk.AppendAsync(Direct(v, rng.Next(2) == 0 ? "RETURN_IN" : "ADJUSTMENT", "IN", qty, Cost(rng)));
                    held[v] += qty;
                    break;
                }
            }
        }

        return new Run(world, desk, product, variants);
    }

    private static ProductLedgerReportService ReportFor(Run run, ReceivablesScope scope)
    {
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync((IReadOnlyList<Guid> _) => new Dictionary<Guid, string>());

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);

        return new ProductLedgerReportService(
            scope.Db, names.Object, run.World.Variants, new ProductLedgerQueryService(scope.Db, run.World.Variants), templates.Object, run.World.Clock);
    }

    // ── R9 ───────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task The_product_ends_where_its_three_variants_own_ledgers_end_added_together_and_every_row_carries_its_variants_stored_totals(int seed)
    {
        var run = await HistoryAsync(seed);
        await using var scope = run.Desk.Scope();

        var report = await ReportFor(run, scope).GetProductLedgerForExportAsync(new ProductLedgerReportFilter { ProductId = run.Product });

        var stored = await scope.Db.ProductLedgerEntries.AsNoTracking().Where(e => e.ProductUuid == run.Product).ToListAsync();
        var lasts  = run.Variants.Select(v => stored.Where(e => e.VariantUuid == v).OrderByDescending(e => e.SequenceNo).FirstOrDefault()).Where(e => e is not null).ToList();
        var context = $"seed {seed}";

        stored.Count.Should().BeGreaterThan(20, context);
        report.Items.Count.Should().Be(stored.Count, context);
        (report.Summary.ClosingQuantity, report.Summary.ClosingValue).Should().Be((lasts.Sum(e => e!.RunningQty), lasts.Sum(e => e!.RunningValue)), context);

        // Each row shows exactly what the ledger stored for its own variant.
        var byUuid = stored.ToDictionary(e => e.UUID);
        foreach (var item in report.Items)
        {
            var entry = byUuid[item.EntryUuid];
            (item.VariantRunningQty, item.VariantRunningValue, item.Quantity, item.TotalCost, item.UnitCost)
                .Should().Be((entry.RunningQty, entry.RunningValue, entry.Quantity, entry.TotalCost, entry.UnitCost), context);
        }

        // And the product's own running figures follow from the movements, row by row, and never go below nothing.
        decimal qty = 0m, value = 0m;
        foreach (var item in report.Items)
        {
            (qty, value) = item.Direction == "IN" ? (qty + item.Quantity, value + item.TotalCost) : (qty - item.Quantity, value - item.TotalCost);
            (item.RunningQty, item.RunningValue).Should().Be((qty, value), context);
        }

        report.Items.Should().OnlyContain(i => i.RunningQty >= 0m, context);
        (report.Items.Last().RunningQty, report.Items.Last().RunningValue).Should().Be((report.Summary.ClosingQuantity, report.Summary.ClosingValue), context);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Naming_one_variant_gives_that_variants_own_ledger_and_its_stored_end_position(int seed)
    {
        var run = await HistoryAsync(seed);
        await using var scope = run.Desk.Scope();
        var service = ReportFor(run, scope);

        foreach (var variant in run.Variants)
        {
            var entries = await scope.Db.ProductLedgerEntries.AsNoTracking().Where(e => e.VariantUuid == variant).OrderBy(e => e.SequenceNo).ToListAsync();
            var report  = await service.GetProductLedgerForExportAsync(new ProductLedgerReportFilter { ProductId = run.Product, VariantId = variant });
            var context = $"seed {seed}, variant {variant}";

            report.Items.Select(i => i.EntryUuid).Should().Equal(entries.Select(e => e.UUID), context);
            if (entries.Count == 0) continue;

            (report.Summary.ClosingQuantity, report.Summary.ClosingValue).Should().Be((entries[^1].RunningQty, entries[^1].RunningValue), context);
            report.Items.Should().OnlyContain(i => i.RunningQty == i.VariantRunningQty && i.RunningValue == i.VariantRunningValue, context);
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Adjacent_ranges_join_up_where_one_ends_the_next_begins_and_together_they_are_the_whole(int seed)
    {
        var run = await HistoryAsync(seed);
        await using var scope = run.Desk.Scope();
        var service = ReportFor(run, scope);

        var full = await service.GetProductLedgerForExportAsync(new ProductLedgerReportFilter { ProductId = run.Product });
        var splits = full.Items.Select(i => i.EntryDate.Date).Distinct().Order().Where((_, n) => n % 5 == 2).ToList();
        splits.Should().NotBeEmpty();

        foreach (var day in splits)
        {
            var before = await service.GetProductLedgerForExportAsync(new ProductLedgerReportFilter { ProductId = run.Product, DateTo = day });
            var after  = await service.GetProductLedgerForExportAsync(new ProductLedgerReportFilter { ProductId = run.Product, DateFrom = day.AddDays(1) });
            var context = $"seed {seed}, split after {day:d}";

            (before.Summary.ClosingQuantity, before.Summary.ClosingValue).Should().Be((after.Summary.OpeningQuantity, after.Summary.OpeningValue), context);
            (after.Summary.ClosingQuantity, after.Summary.ClosingValue).Should().Be((full.Summary.ClosingQuantity, full.Summary.ClosingValue), context);
            (before.Items.Count + after.Items.Count).Should().Be(full.Items.Count, context);
            before.Items.Concat(after.Items).Select(i => (i.EntryUuid, i.RunningQty, i.RunningValue)).Should().Equal(full.Items.Select(i => (i.EntryUuid, i.RunningQty, i.RunningValue)), context);
        }
    }

    // ── R10 ──────────────────────────────────────────────────────────────────

    private static async Task<(ReceivablesWorld World, ReceivablesDesk Desk)> SoldAsync()
    {
        var world = new ReceivablesWorld();
        var desk  = world.For(Guid.NewGuid());
        var acme  = desk.NewCustomer("Acme Ltd");

        var (laptopBlack, laptop) = world.Variants.New();
        var (laptopSilver, _)     = world.Variants.New(laptop);
        var (mouse, _)            = world.Variants.New();
        var (dock, _)             = world.Variants.New();
        var (cable, _)            = world.Variants.New();

        foreach (var (day, variant, qty, price, cost) in new[]
        {
            (2, laptopBlack, 10m, 40m, 25m), (3, laptopSilver, 2m, 1200.5m, 900m), (4, mouse, 7m, 25.5m, 30m),
            (5, dock, 1m, 300m, 100m), (6, cable, 20m, 3.25m, 1m), (7, mouse, 3m, 26m, 12m), (8, laptopBlack, 4m, 41m, 25m),
        })
        {
            world.Clock.Value = new DateTime(2026, 9, day, 10, 30, 0, DateTimeKind.Utc);
            var order = await desk.PlaceOrderAsync(acme, new ReceivablesDesk.OrderLine(qty, price, Cost: cost, Variant: variant));
            await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, qty)));
        }

        return (world, desk);
    }

    [Fact]
    public async Task The_full_ranking_is_the_endpoints_pages_end_to_end_for_any_range()
    {
        var (world, desk) = await SoldAsync();
        await using var scope = desk.Scope();
        var query = new ProductLedgerQueryService(scope.Db, world.Variants);

        foreach (var (from, to) in new (DateTime?, DateTime?)[] { (null, null), (new DateTime(2026, 9, 3), new DateTime(2026, 9, 6)), (new DateTime(2026, 9, 7), null), (new DateTime(2027, 1, 1), null) })
        {
            var all = await query.GetProfitabilityRowsAsync(new ProductProfitabilityFilter { DateFrom = from, DateTo = to, Page = 9, PageSize = 1 });

            var paged = new List<ProductProfitabilityItemModel>();
            for (var page = 1; ; page++)
            {
                var result = await query.GetProfitabilityAsync(new ProductProfitabilityFilter { DateFrom = from, DateTo = to, Page = page, PageSize = 2 });
                paged.AddRange(result.Data);
                if (page >= result.TotalPages) break;
            }

            all.Should().BeEquivalentTo(paged, o => o.WithStrictOrdering(), $"{from:d} to {to:d}");
            if (from is null) all.Should().HaveCountGreaterThan(3, "guards the comparison: an empty ranking equals an empty ranking");
        }
    }

    [Fact]
    public async Task The_full_ranking_refuses_a_range_that_ends_before_it_starts_like_the_paged_one_does()
    {
        var (world, desk) = await SoldAsync();
        await using var scope = desk.Scope();
        var query = new ProductLedgerQueryService(scope.Db, world.Variants);
        var filter = new ProductProfitabilityFilter { DateFrom = new DateTime(2026, 9, 10), DateTo = new DateTime(2026, 9, 5) };

        await FluentActions.Awaiting(() => query.GetProfitabilityRowsAsync(filter)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => query.GetProfitabilityAsync(filter)).Should().ThrowAsync<BadRequestException>();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(3, 6)]
    [InlineData(7, null)]
    public async Task R10_is_the_ranking_of_real_sales_with_ranks_and_totals_and_its_cost_is_the_ledgers(int? fromDay, int? toDay)
    {
        var (world, desk) = await SoldAsync();
        await using var scope = desk.Scope();
        DateTime? from = fromDay is { } f ? new DateTime(2026, 9, f) : null;
        DateTime? to   = toDay is { } t ? new DateTime(2026, 9, t) : null;

        var report = await ReportFor(new Run(world, desk, Guid.Empty, []), scope).GetProductProfitabilityForExportAsync(new ProfitabilityReportFilter { DateFrom = from, DateTo = to });

        var sales = scope.Db.ProductLedgerEntries.AsNoTracking().Where(e => e.EntryType == "SALE").ToList()
            .Where(e => (from is null || e.EntryDate.Date >= from) && (to is null || e.EntryDate.Date <= to)).ToList();

        report.Items.Should().NotBeEmpty();
        report.Items.Select(i => i.Rank).Should().Equal(Enumerable.Range(1, report.Items.Count), "one currency here, so ranks run 1 to n");
        report.Items.Select(i => i.GrossProfit).Should().BeInDescendingOrder();
        report.Items.Sum(i => i.CostOfGoodsSold).Should().Be(sales.Sum(e => e.TotalCost));
        report.Items.Sum(i => i.QuantitySold).Should().Be(sales.Sum(e => e.Quantity));
        (report.Totals.Single().Revenue, report.Totals.Single().CostOfGoodsSold, report.Totals.Single().GrossProfit)
            .Should().Be((report.Items.Sum(i => i.Revenue), report.Items.Sum(i => i.CostOfGoodsSold), report.Items.Sum(i => i.GrossProfit)));
    }
}
