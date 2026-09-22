using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P8-06 §11.3, §17.1 — no hand-picked scenario covers every order in which stock can be received
/// at different prices, sold, returned, adjusted, written off and sold down to nothing. These drive the
/// real services — sales through real invoices, everything else straight onto the ledger — through a few
/// hundred seeded random operations over three variants and, after every one, hold every variant's
/// ledger to <see cref="ProductLedgerInvariants"/>, its quantity to an exact model, and its summary to
/// its entries. At the end the profitability report is reconciled to the raw sales. A failure names the
/// seed and every step so far, so it can be replayed exactly.
/// </summary>
public class ProductLedgerReconciliationTests
{
    private const int Steps = 35;
    private const int User = ReceivablesDesk.User;

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 25).Select(seed => new object[] { seed });

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Whatever_happens_to_a_variant_its_ledger_reconciles_after_every_step(int seed) => await RunAsync(seed);

    /// <summary>
    /// A run that never sold a variant down to nothing, or bought into an empty ledger, or was refused,
    /// would pass the test above and prove nothing about the edges; so the runs together must have
    /// done each of the things worth checking.
    /// </summary>
    [Fact]
    public async Task The_random_runs_between_them_reach_every_edge()
    {
        var log = new List<string>();
        foreach (var seed in Enumerable.Range(1, 25)) log.AddRange(await RunAsync(seed));

        int Count(string pattern) => log.Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, pattern));

        Count(@"\. receive ").Should().BeGreaterThan(150);
        Count(@"\. receive .* at 0\b").Should().BeGreaterThan(5, "goods that came in free");
        Count(@"\. receive .*into an empty ledger").Should().BeGreaterThan(20, "a receipt after being sold out, or the first ever");
        Count(@"\. sell ").Should().BeGreaterThan(80);
        Count(@"\. sell all ").Should().BeGreaterThan(25, "sold down to exactly nothing");
        Count(@"\. refused: sell").Should().BeGreaterThan(15, "asked for more than the ledger held");
        Count(@"\. adjust in ").Should().BeGreaterThan(5);
        Count(@"\. adjust out ").Should().BeGreaterThan(5);
        Count(@"\. write off ").Should().BeGreaterThan(5);
        Count(@"\. customer return ").Should().BeGreaterThan(5);
        Count(@"\. return to supplier ").Should().BeGreaterThan(5);
    }

    // ── Random quantities and costs, mostly to the cent and sometimes to four places ────────────────

    private static decimal Quantity(Random rng) =>
        rng.Next(10) < 7 ? rng.Next(1, 100_000) / 100m : rng.Next(1, 10_000_000) / 10_000m;

    private static decimal Cost(Random rng) =>
        rng.Next(100) < 5 ? 0m
        : rng.Next(10) < 7 ? rng.Next(1, 100_000) / 100m
        : rng.Next(1, 10_000_000) / 10_000m;

    /// <summary>Some of what is held, to four places, and at least the smallest amount there is.</summary>
    private static decimal PartOf(Random rng, decimal held) =>
        Math.Min(held, Math.Max(0.0001m, Math.Round(held * rng.Next(1, 100) / 100m, 4)));

    private static ProductLedgerPosting Direct(Guid variant, string type, string direction, decimal qty, decimal? cost) =>
        new(variant, type, direction, qty, cost, "Doc", Guid.NewGuid(), "DOC-1", User);

    // ── One run ──────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<string>> RunAsync(int seed)
    {
        var rng      = new Random(seed);
        var world    = new ReceivablesWorld();
        var desk     = world.For(Guid.NewGuid());
        var customer = desk.NewCustomer();
        var variants = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        var held     = variants.ToDictionary(v => v, _ => 0m);          // an exact model of what each variant holds
        var log      = new List<string>();

        for (var step = 1; step <= Steps; step++)
        {
            var v = variants[rng.Next(variants.Length)];
            var before = (await desk.ProductLedgerAsync(v)).Count;

            var (what, refused) = held[v] == 0m || rng.Next(100) < 30
                ? (await ReceiveAsync(rng, desk, held, v), false)
                : rng.Next(100) switch
                {
                    < 45 => (await SellAsync(rng, desk, customer, held, v), false),
                    < 55 => (await RefuseSaleAsync(rng, desk, customer, held, v), true),
                    < 65 => (await AdjustAsync(rng, desk, held, v), false),
                    < 75 => (await WriteOffAsync(rng, desk, held, v), false),
                    < 85 => (await CustomerReturnAsync(rng, desk, held, v), false),
                    _    => (await ReturnToSupplierAsync(rng, desk, held, v), false)
                };

            log.Add($"{step,3}. {what}");
            var context = $"seed {seed} broke a rule at step {step} ({what}). Steps so far:\n{string.Join("\n", log)}\n";

            foreach (var variant in variants)
            {
                var entries = await desk.ProductLedgerAsync(variant);

                ProductLedgerInvariants.Violations(entries).Should().BeEmpty(context);
                (entries.LastOrDefault()?.RunningQty ?? 0m).Should().Be(held[variant], context);

                if (variant == v && refused)
                    entries.Should().HaveCount(before, "a refused sale wrote nothing\n" + context);
            }

            await ShouldSummarizeItsEntriesAsync(world, desk, v, context);
        }

        await ShouldReconcileTheProfitabilityReportAsync(world, desk, variants);
        ReceivablesInvariants.Violations(await desk.BooksAsync(customer), world.Clock.Value.Date)
            .Should().BeEmpty($"seed {seed}: the receivables must be unharmed by what the cost side did");

        return log;
    }

    // ── Operations ───────────────────────────────────────────────────────────

    private static async Task<string> ReceiveAsync(Random rng, ReceivablesDesk desk, Dictionary<Guid, decimal> held, Guid v)
    {
        var (qty, cost) = (Quantity(rng), Cost(rng));
        var wasEmpty = held[v] == 0m;

        await desk.StockAsync(v, qty, cost);
        held[v] += qty;

        return $"receive {qty:0.####} at {cost:0.####}" + (wasEmpty ? " into an empty ledger" : "");
    }

    /// <summary>An order for the variant, delivered, and its draft invoice raised — not yet issued.</summary>
    private static async Task<(Guid Invoice, decimal Price)> DraftSaleAsync(Random rng, ReceivablesDesk desk, Guid customer, Guid variant, decimal qty)
    {
        // An invoice for nothing is refused, so a sale of a tiny quantity is priced high enough to be worth a cent.
        var price    = Math.Max(rng.Next(1, 50_000) / 100m, Math.Ceiling(0.05m / qty * 100m) / 100m);
        var discount = rng.Next(3) == 0 ? 10m : 0m;

        var order    = await desk.PlaceOrderAsync(customer, new ReceivablesDesk.OrderLine(qty, price, Discount: discount, Stocked: false, Variant: variant));
        var delivery = desk.Deliver(order, "DELIVERED", (0, qty));

        await using var scope = desk.Scope();
        var created = await scope.Invoices.CreateFromFulfillmentAsync(delivery, User);
        return (created.InvoiceUuid, price);
    }

    private static async Task<string> SellAsync(Random rng, ReceivablesDesk desk, Guid customer, Dictionary<Guid, decimal> held, Guid v)
    {
        var all = rng.Next(100) < 25;
        var qty = all ? held[v] : PartOf(rng, held[v]);

        var (invoice, price) = await DraftSaleAsync(rng, desk, customer, v, qty);
        await using (var scope = desk.Scope())
            await scope.Invoices.IssueAsync(invoice, User);

        held[v] -= qty;
        return $"sell {(all ? "all " : "")}{qty:0.####} at {price:0.##}";
    }

    private static async Task<string> RefuseSaleAsync(Random rng, ReceivablesDesk desk, Guid customer, Dictionary<Guid, decimal> held, Guid v)
    {
        var qty = held[v] + new[] { 0.0001m, 1m, 50m }[rng.Next(3)];

        var (invoice, _) = await DraftSaleAsync(rng, desk, customer, v, qty);
        await using var scope = desk.Scope();
        var act = () => scope.Invoices.IssueAsync(invoice, User);

        await act.Should().ThrowAsync<ConflictException>();
        (await desk.BooksAsync(customer)).Invoices.Single(i => i.UUID == invoice).Status.Should().Be("DRAFT");

        return $"refused: sell {qty:0.####} of {held[v]:0.####} held";
    }

    private static async Task<string> AdjustAsync(Random rng, ReceivablesDesk desk, Dictionary<Guid, decimal> held, Guid v)
    {
        if (rng.Next(2) == 0)
        {
            var (qty, cost) = (Quantity(rng), Cost(rng));
            await desk.AppendAsync(Direct(v, "ADJUSTMENT", "IN", qty, cost));
            held[v] += qty;
            return $"adjust in {qty:0.####} at {cost:0.####}";
        }

        var take = PartOf(rng, held[v]);
        await desk.AppendAsync(Direct(v, "ADJUSTMENT", "OUT", take, null));
        held[v] -= take;
        return $"adjust out {take:0.####}";
    }

    private static async Task<string> WriteOffAsync(Random rng, ReceivablesDesk desk, Dictionary<Guid, decimal> held, Guid v)
    {
        var take = rng.Next(4) == 0 ? held[v] : PartOf(rng, held[v]);
        await desk.AppendAsync(Direct(v, "WRITE_OFF", "OUT", take, null));
        held[v] -= take;
        return $"write off {take:0.####}";
    }

    private static async Task<string> CustomerReturnAsync(Random rng, ReceivablesDesk desk, Dictionary<Guid, decimal> held, Guid v)
    {
        var (qty, cost) = (Quantity(rng), Cost(rng));
        await desk.AppendAsync(Direct(v, "RETURN_IN", "IN", qty, cost));
        held[v] += qty;
        return $"customer return {qty:0.####} at {cost:0.####}";
    }

    private static async Task<string> ReturnToSupplierAsync(Random rng, ReceivablesDesk desk, Dictionary<Guid, decimal> held, Guid v)
    {
        var take = PartOf(rng, held[v]);
        await desk.AppendAsync(Direct(v, "RETURN_OUT", "OUT", take, null));
        held[v] -= take;
        return $"return to supplier {take:0.####}";
    }

    // ── Checks that need the read side ───────────────────────────────────────

    private static async Task ShouldSummarizeItsEntriesAsync(ReceivablesWorld world, ReceivablesDesk desk, Guid variant, string context)
    {
        var entries = await desk.ProductLedgerAsync(variant);
        await using var scope = desk.Scope();
        var summary = await new ProductLedgerQueryService(scope.Db, world.Variants).GetSummaryAsync(variant);

        if (entries.Count == 0) { summary.EntryCount.Should().Be(0, context); return; }

        var last = entries[^1];
        (summary.CurrentQuantity, summary.StockValue, summary.EntryCount).Should().Be((last.RunningQty, last.RunningValue, entries.Count), context);
        summary.PurchasedQuantity.Should().Be(entries.Where(e => e.EntryType == "PURCHASE").Sum(e => e.Quantity), context);
        summary.PurchasedCost.Should().Be(entries.Where(e => e.EntryType == "PURCHASE").Sum(e => e.TotalCost), context);
        summary.SoldQuantity.Should().Be(entries.Where(e => e.EntryType == "SALE").Sum(e => e.Quantity), context);
        summary.CostOfGoodsSold.Should().Be(entries.Where(e => e.EntryType == "SALE").Sum(e => e.TotalCost), context);
        summary.WeightedAverageCost.Should().Be(last.RunningQty == 0m ? 0m : Math.Round(last.RunningValue / last.RunningQty, 4, MidpointRounding.AwayFromZero), context);
    }

    private static async Task ShouldReconcileTheProfitabilityReportAsync(ReceivablesWorld world, ReceivablesDesk desk, Guid[] variants)
    {
        var sales = new List<ProductLedgerEntry>();
        foreach (var v in variants) sales.AddRange((await desk.ProductLedgerAsync(v)).Where(e => e.EntryType == "SALE"));

        await using var scope = desk.Scope();
        var report = await new ProductLedgerQueryService(scope.Db, world.Variants)
            .GetProfitabilityAsync(new ProductProfitabilityFilter { PageSize = 100 });

        // The cost side is exactly the SALE entries: every one of them is on an issued invoice here.
        report.Data.Sum(r => r.CostOfGoodsSold).Should().Be(sales.Sum(e => e.TotalCost));
        report.Data.Sum(r => r.QuantitySold).Should().Be(sales.Sum(e => e.Quantity));
        report.Data.Should().OnlyContain(r => r.GrossProfit == r.Revenue - r.CostOfGoodsSold);

        // The revenue side, worked separately from the invoice lines: gross less discount, each rounded on the product's total.
        var lines = scope.Db.SalesInvoiceLines.Where(l => l.SalesInvoice.Status != "DRAFT").ToList();
        foreach (var row in report.Data)
        {
            var mine = lines.Where(l => world.ProductOf(l.VariantUuid) == row.ProductUuid).ToList();
            var expected = Math.Round(mine.Sum(l => l.Quantity * l.UnitPrice), 2, MidpointRounding.AwayFromZero)
                         - Math.Round(mine.Sum(l => l.Quantity * l.UnitPrice * l.DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero);
            row.Revenue.Should().Be(expected, $"product {row.ProductUuid}");
        }
    }
}

/// <summary>
/// The same random runs against a real SQL Server, ledger only: what is stored is rounded by the
/// <c>decimal(18,4)</c> and <c>decimal(18,2)</c> columns, which the in-memory provider never does, so this
/// is where an amount that only survives in memory would show — the running totals read <i>back</i> must
/// still satisfy every rule.
/// </summary>
public class ProductLedgerReconciliationSqlServerTests
{
    private const int User = ReceivablesDesk.User;

    [FinanceSqlServerTheory]
    [InlineData(101)]
    [InlineData(202)]
    [InlineData(303)]
    public async Task Whatever_is_written_the_ledger_read_back_from_storage_reconciles(int seed)
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);
        var rng = new Random(seed);
        var org = Guid.NewGuid();
        var variants = new FakeVariants();
        var ids = Enumerable.Range(0, 2).Select(_ => variants.New().Variant).ToArray();
        var held = ids.ToDictionary(v => v, _ => 0m);

        decimal Quantity() => rng.Next(10) < 7 ? rng.Next(1, 100_000) / 100m : rng.Next(1, 10_000_000) / 10_000m;
        decimal Cost() => rng.Next(100) < 5 ? 0m : rng.Next(10) < 7 ? rng.Next(1, 100_000) / 100m : rng.Next(1, 10_000_000) / 10_000m;
        decimal Part(decimal of) => Math.Min(of, Math.Max(0.0001m, Math.Round(of * rng.Next(1, 100) / 100m, 4)));

        for (var step = 1; step <= 60; step++)
        {
            var v = ids[rng.Next(ids.Length)];
            ProductLedgerPosting posting;

            if (held[v] == 0m || rng.Next(100) < 35)
            {
                var (qty, cost) = (Quantity(), Cost());
                posting = new ProductLedgerPosting(v, "PURCHASE", "IN", qty, cost, "GRN", Guid.NewGuid(), "GRN-1", User);
                held[v] += qty;
            }
            else
            {
                var all = rng.Next(100) < 25;
                var take = all ? held[v] : Part(held[v]);
                var type = new[] { "SALE", "SALE", "WRITE_OFF", "RETURN_OUT", "ADJUSTMENT" }[rng.Next(5)];
                posting = new ProductLedgerPosting(v, type, "OUT", take, null, "Doc", Guid.NewGuid(), "DOC-1", User);
                held[v] -= take;
            }

            await using var db = h.NewContext(org);
            await new ProductLedgerService(db, variants).AppendEntryAsync(posting);
        }

        await using var read = h.NewContext(org);
        foreach (var v in ids)
        {
            var entries = await read.ProductLedgerEntries.AsNoTracking().Where(e => e.VariantUuid == v).OrderBy(e => e.SequenceNo).ToListAsync();

            entries.Should().NotBeEmpty();
            ProductLedgerInvariants.Violations(entries).Should().BeEmpty($"seed {seed}, variant {v}");
            entries[^1].RunningQty.Should().Be(held[v], $"seed {seed}");
        }
    }
}

/// <summary>A theory that skips itself when no SQL Server is reachable, like <see cref="FinanceSqlServerFactAttribute"/>.</summary>
internal sealed class FinanceSqlServerTheoryAttribute : TheoryAttribute
{
    public FinanceSqlServerTheoryAttribute()
    {
        if (!FinanceSqlServerHarness.IsAvailable)
            Skip = "No SQL Server reachable. Set SMS_TEST_SQLSERVER to run the product ledger transaction tests.";
    }
}