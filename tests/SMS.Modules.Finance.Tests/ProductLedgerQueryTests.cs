using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using Line = SMS.Modules.Finance.Tests.SalesBookSeeder.Line;

using static SMS.Modules.Finance.Tests.ProductLedgerRig;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// Sales written straight into the tables — an invoice, its lines and the SALE entries the ledger would
/// hold for them — for the report tests that need exact control of statuses, currencies and dates.
/// </summary>
internal sealed class SalesBookSeeder
{
    private readonly Dictionary<(Guid Org, Guid Variant), int> _sequence = [];

    public sealed record Line(Guid Product, Guid Variant, decimal Qty, decimal Price, decimal Cost, decimal Discount = 0m, decimal Tax = 0m);

    /// <summary>An invoice with one line per <paramref name="lines"/> entry, and a SALE entry per line costing <c>Qty × Cost</c>.</summary>
    public (SalesInvoice Invoice, List<ProductLedgerEntry> Entries) Sale(
        Guid org, string number, string status, string currency, DateTime date, params Line[] lines)
    {
        var invoice = Receivables.Invoice(org, Guid.NewGuid(), number, date, 1m, status: status, currency: currency);
        var entries = new List<ProductLedgerEntry>();
        var lineNo  = 1;

        foreach (var l in lines)
        {
            invoice.Lines.Add(new SalesInvoiceLine
            {
                UUID = Guid.NewGuid(), OrganizationId = org, LineNo = lineNo++, SoLineUuid = Guid.NewGuid(), VariantUuid = l.Variant,
                Description = "Item", Quantity = l.Qty, UnitPrice = l.Price, DiscountPercent = l.Discount, TaxPercent = l.Tax,
                LineTotal = SalesInvoiceTotals.LineTotal(l.Qty, l.Price, l.Discount, l.Tax)
            });

            var seq = _sequence[(org, l.Variant)] = _sequence.GetValueOrDefault((org, l.Variant)) + 1;
            entries.Add(new ProductLedgerEntry
            {
                UUID = Guid.NewGuid(), OrganizationId = org, VariantUuid = l.Variant, ProductUuid = l.Product, SequenceNo = seq,
                EntryDate = date, EntryType = "SALE", ReferenceType = "SalesInvoice", ReferenceId = invoice.UUID, ReferenceNumber = number,
                Quantity = l.Qty, UnitCost = l.Cost, TotalCost = decimal.Round(l.Qty * l.Cost, 2), Direction = "OUT",
                RunningQty = 0m, RunningValue = 0m, CreatedBy = 1, CreatedDate = date
            });
        }

        return (invoice, entries);
    }

    public static async Task WriteAsync(FinanceDbContext db, params (SalesInvoice Invoice, List<ProductLedgerEntry> Entries)[] sales)
    {
        foreach (var (invoice, entries) in sales)
        {
            db.SalesInvoices.Add(invoice);
            db.ProductLedgerEntries.AddRange(entries);
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}

/// <summary>
/// A29-P8-05 §11.4 — reading the product ledger: a variant's history, where it stands, and revenue
/// against cost by product. The figures of TC-10 are the ones the tests keep coming back to.
/// </summary>
public class ProductLedgerQueryTests
{
    private const int User = ReceivablesDesk.User;
    private static readonly DateTime Jan = new(2026, 1, 1);

    private static (ReceivablesWorld World, ReceivablesDesk Desk, Guid Customer) Setup()
    {
        var world = new ReceivablesWorld();
        var desk  = world.For(Guid.NewGuid());
        return (world, desk, desk.NewCustomer());
    }

    private static ProductLedgerQueryService Queries(ReceivablesWorld world, FinanceDbContext db) => new(db, world.Variants);

    // ── A variant's ledger ───────────────────────────────────────────────────

    [Fact]
    public async Task The_ledger_pages_newest_first_and_each_row_says_where_the_variant_stood_after_it()
    {
        var rig = new ProductLedgerRig();
        var (variant, product) = rig.Variants.New();
        var other = rig.Variants.New().Variant;

        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));
        await rig.Service().AppendEntryAsync(Sell(variant, 30m));
        await rig.Service().AppendEntryAsync(Buy(variant, 50m, 16m));
        await rig.Service().AppendEntryAsync(Buy(other, 5m, 99m));                  // another variant's, not to be seen
        await rig.Service().AppendEntryAsync(Sell(variant, 20m));
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 12m));

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        var svc = new ProductLedgerQueryService(db, rig.Variants);

        var first  = await svc.GetLedgerAsync(variant, new ProductLedgerFilter { PageSize = 4 });
        var second = await svc.GetLedgerAsync(variant, new ProductLedgerFilter { PageSize = 4, Page = 2 });

        first.TotalRecords.Should().Be(5);
        first.TotalPages.Should().Be(2);
        first.Data.Select(e => e.SequenceNo).Should().Equal(5, 4, 3, 2);
        second.Data.Select(e => e.SequenceNo).Should().Equal(1);

        var newest = first.Data[0];
        newest.Should().Match<ProductLedgerEntryModel>(e =>
            e.EntryType == "PURCHASE" && e.Direction == "IN" && e.Quantity == 10m && e.UnitCost == 12m && e.TotalCost == 120m
            && e.VariantUuid == variant && e.ProductUuid == product && e.ReferenceType == "GRN");
        // 100@10 → sell 30 at 10 (70 worth 700) → +50@16 (120 worth 1500) → sell 20 at 12.5 (100 worth 1250) → +10@12.
        (newest.RunningQty, newest.RunningValue, newest.WeightedAverageCost).Should().Be((110m, 1_370m, 12.4545m));

        first.Data.Should().OnlyContain(e => e.VariantUuid == variant);
    }

    [Fact]
    public async Task The_ledger_can_be_cut_by_date_kind_and_direction()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();

        rig.Clock.Value = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m) with { EntryDate = rig.Clock.Value });
        rig.Clock.Value = new DateTime(2026, 9, 10, 15, 0, 0, DateTimeKind.Utc);
        await rig.Service().AppendEntryAsync(Sell(variant, 30m) with { EntryDate = rig.Clock.Value });
        rig.Clock.Value = new DateTime(2026, 9, 20, 23, 30, 0, DateTimeKind.Utc);
        await rig.Service().AppendEntryAsync(new ProductLedgerPosting(variant, "WRITE_OFF", "OUT", 5m, null, "WriteOff", Guid.NewGuid(), "WO-1", User, EntryDate: rig.Clock.Value));

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        var svc = new ProductLedgerQueryService(db, rig.Variants);

        (await svc.GetLedgerAsync(variant, new ProductLedgerFilter { DateFrom = new DateTime(2026, 9, 10), DateTo = new DateTime(2026, 9, 20) }))
            .Data.Select(e => e.EntryType).Should().Equal("WRITE_OFF", "SALE");                // the 20th at 23:30 is in

        (await svc.GetLedgerAsync(variant, new ProductLedgerFilter { DateFrom = new DateTime(2026, 9, 11) }))
            .Data.Select(e => e.EntryType).Should().Equal("WRITE_OFF");

        (await svc.GetLedgerAsync(variant, new ProductLedgerFilter { EntryType = "sale" })).Data.Should().ContainSingle().Which.EntryType.Should().Be("SALE");
        (await svc.GetLedgerAsync(variant, new ProductLedgerFilter { Direction = " out " })).Data.Select(e => e.EntryType).Should().Equal("WRITE_OFF", "SALE");
        (await svc.GetLedgerAsync(variant, new ProductLedgerFilter { Direction = "IN" })).Data.Should().ContainSingle();
    }

    [Theory]
    [InlineData("TRANSFER", null)]
    [InlineData(null, "SIDEWAYS")]
    public async Task A_kind_or_direction_the_ledger_does_not_have_is_refused_with_what_it_does(string? type, string? direction)
    {
        var rig = new ProductLedgerRig();
        await using var db = Receivables.Db(rig.Org, rig.DbName);

        var act = () => new ProductLedgerQueryService(db, rig.Variants).GetLedgerAsync(Guid.NewGuid(), new ProductLedgerFilter { EntryType = type, Direction = direction });

        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message.Should().Contain("Valid:");
    }

    [Fact]
    public async Task A_start_date_after_the_end_date_is_refused()
    {
        var rig = new ProductLedgerRig();
        await using var db = Receivables.Db(rig.Org, rig.DbName);

        var act = () => new ProductLedgerQueryService(db, rig.Variants).GetLedgerAsync(Guid.NewGuid(),
            new ProductLedgerFilter { DateFrom = new DateTime(2026, 9, 20), DateTo = new DateTime(2026, 9, 1) });

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(-5, 500, 1, 100)]
    [InlineData(3, 7, 3, 7)]
    public async Task Page_and_page_size_are_held_to_sensible_bounds_rather_than_refused(int page, int pageSize, int wantPage, int wantSize)
    {
        var rig = new ProductLedgerRig();
        await using var db = Receivables.Db(rig.Org, rig.DbName);

        var result = await new ProductLedgerQueryService(db, rig.Variants).GetLedgerAsync(Guid.NewGuid(), new ProductLedgerFilter { Page = page, PageSize = pageSize });

        (result.Page, result.PageSize).Should().Be((wantPage, wantSize));
        result.Data.Should().BeEmpty("a variant with no entries has an empty page");
    }

    [Fact]
    public async Task Another_organizations_ledger_for_the_same_variant_is_invisible()
    {
        var rig = new ProductLedgerRig();
        var (variant, product) = rig.Variants.New();
        var otherOrg = Guid.NewGuid();

        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));
        await rig.Service(otherOrg).AppendEntryAsync(Buy(variant, 7m, 99m) with { ProductUuid = product });

        await using var db = Receivables.Db(otherOrg, rig.DbName);
        var svc = new ProductLedgerQueryService(db, rig.Variants);

        var theirs = await svc.GetLedgerAsync(variant, new ProductLedgerFilter());
        theirs.Data.Should().ContainSingle().Which.Quantity.Should().Be(7m);
        (await svc.GetSummaryAsync(variant)).StockValue.Should().Be(693m);
    }

    // ── Where it stands ──────────────────────────────────────────────────────

    [Fact]
    public async Task TC10_the_summary_of_100_bought_at_10_and_30_sold_says_purchased_100_sold_30_stock_700_wac_10()
    {
        var (world, desk, customer) = Setup();
        var order = await desk.PlaceOrderAsync(customer, new ReceivablesDesk.OrderLine(Qty: 30m, Price: 15m, Stocked: false));
        var variant = order.VariantUuids[0];
        await desk.StockAsync(variant, 100m, 10m);
        await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 30m)));

        await using var scope = desk.Scope();
        var summary = await Queries(world, scope.Db).GetSummaryAsync(variant);

        summary.Should().BeEquivalentTo(new ProductLedgerSummaryModel
        {
            VariantUuid = variant, ProductUuid = world.ProductOf(variant),
            PurchasedQuantity = 100m, PurchasedCost = 1000m,
            SoldQuantity = 30m, CostOfGoodsSold = 300m,
            CurrentQuantity = 70m, StockValue = 700m, WeightedAverageCost = 10m,
            EntryCount = 2, LastEntryDate = summary.LastEntryDate
        });
        summary.LastEntryDate.Should().Be(world.Clock.Value);
    }

    [Fact]
    public async Task Returns_adjustments_and_write_offs_move_the_stock_but_are_neither_purchased_nor_sold()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));
        await rig.Service().AppendEntryAsync(Sell(variant, 30m));
        await rig.Service().AppendEntryAsync(new ProductLedgerPosting(variant, "RETURN_IN", "IN", 5m, 10m, "SalesReturn", Guid.NewGuid(), "SRET-1", User));
        await rig.Service().AppendEntryAsync(new ProductLedgerPosting(variant, "WRITE_OFF", "OUT", 10m, null, "WriteOff", Guid.NewGuid(), "WO-1", User));
        await rig.Service().AppendEntryAsync(new ProductLedgerPosting(variant, "ADJUSTMENT", "IN", 2m, 10m, "Adj", Guid.NewGuid(), "ADJ-1", User));

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        var summary = await new ProductLedgerQueryService(db, rig.Variants).GetSummaryAsync(variant);

        (summary.PurchasedQuantity, summary.PurchasedCost, summary.SoldQuantity, summary.CostOfGoodsSold).Should().Be((100m, 1000m, 30m, 300m));
        (summary.CurrentQuantity, summary.StockValue, summary.WeightedAverageCost).Should().Be((67m, 670m, 10m));
        summary.EntryCount.Should().Be(5);
    }

    [Fact]
    public async Task Purchases_at_different_prices_show_their_blend_as_the_weighted_average()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 100m, 10m));
        await rig.Service().AppendEntryAsync(Buy(variant, 50m, 16m));

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        var summary = await new ProductLedgerQueryService(db, rig.Variants).GetSummaryAsync(variant);

        (summary.PurchasedQuantity, summary.PurchasedCost, summary.StockValue, summary.WeightedAverageCost).Should().Be((150m, 1800m, 1800m, 12m));
    }

    [Fact]
    public async Task A_variant_with_nothing_on_the_ledger_summarizes_as_zeros_not_as_an_error()
    {
        var rig = new ProductLedgerRig();
        var variant = Guid.NewGuid();
        await using var db = Receivables.Db(rig.Org, rig.DbName);

        var summary = await new ProductLedgerQueryService(db, rig.Variants).GetSummaryAsync(variant);

        summary.VariantUuid.Should().Be(variant);
        summary.ProductUuid.Should().BeNull();
        (summary.PurchasedQuantity, summary.SoldQuantity, summary.CurrentQuantity, summary.StockValue, summary.WeightedAverageCost, summary.EntryCount)
            .Should().Be((0m, 0m, 0m, 0m, 0m, 0));
        summary.LastEntryDate.Should().BeNull();
    }

    [Fact]
    public async Task A_variant_sold_down_to_nothing_reports_a_zero_average_not_a_division_by_zero()
    {
        var rig = new ProductLedgerRig();
        var (variant, _) = rig.Variants.New();
        await rig.Service().AppendEntryAsync(Buy(variant, 10m, 3m));
        await rig.Service().AppendEntryAsync(Sell(variant, 10m));

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        var summary = await new ProductLedgerQueryService(db, rig.Variants).GetSummaryAsync(variant);

        (summary.CurrentQuantity, summary.StockValue, summary.WeightedAverageCost, summary.CostOfGoodsSold).Should().Be((0m, 0m, 0m, 30m));
    }

    // ── Revenue against cost ─────────────────────────────────────────────────

    [Fact]
    public async Task TC10_revenue_450_against_cogs_300_is_a_profit_of_150_at_a_margin_of_a_third()
    {
        var (world, desk, customer) = Setup();
        var order = await desk.PlaceOrderAsync(customer, new ReceivablesDesk.OrderLine(Qty: 30m, Price: 15m, Stocked: false));
        var variant = order.VariantUuids[0];
        var product = world.ProductOf(variant);
        world.Variants.ProductNames[product] = "Business Laptop";
        await desk.StockAsync(variant, 100m, 10m);
        await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 30m)));

        await using var scope = desk.Scope();
        var report = await Queries(world, scope.Db).GetProfitabilityAsync(new ProductProfitabilityFilter());

        report.TotalRecords.Should().Be(1);
        report.Data.Single().Should().BeEquivalentTo(new ProductProfitabilityItemModel
        {
            ProductUuid = product, ProductName = "Business Laptop", CurrencyCode = "PKR", QuantitySold = 30m,
            Revenue = 450m, CostOfGoodsSold = 300m, GrossProfit = 150m, MarginPercent = 33.33m
        });
    }

    [Fact]
    public async Task Products_are_ranked_by_profit_and_a_loss_ranks_last()
    {
        var org = Guid.NewGuid();
        var rig = new ProductLedgerRig();
        var (winner, big, loser) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var seeder = new SalesBookSeeder();

        await using (var db = Receivables.Db(rig.Org, rig.DbName))
        {
            await SalesBookSeeder.WriteAsync(db,
                seeder.Sale(rig.Org, "SINV-1", "ISSUED", "PKR", Jan.AddDays(1),
                    new Line(winner, winner, Qty: 10m, Price: 50m, Cost: 20m),       // 500 - 200 = 300
                    new(big,    big,    Qty: 100m, Price: 12m, Cost: 10m),       // 1200 - 1000 = 200
                    new(loser,  loser,  Qty: 5m, Price: 8m, Cost: 10m)),         // 40 - 50 = -10
                seeder.Sale(rig.Org, "SINV-2", "PAID", "PKR", Jan.AddDays(2),
                    new Line(winner, winner, Qty: 2m, Price: 50m, Cost: 20m)));       // +100 - 40 = +60 → 360
            rig.Variants.Products[winner] = winner; rig.Variants.Products[big] = big; rig.Variants.Products[loser] = loser;
            rig.Variants.ProductNames[winner] = "Winner"; rig.Variants.ProductNames[big] = "Big"; rig.Variants.ProductNames[loser] = "Loser";

            var report = await new ProductLedgerQueryService(db, rig.Variants).GetProfitabilityAsync(new ProductProfitabilityFilter());

            report.Data.Select(r => (r.ProductName, r.QuantitySold, r.Revenue, r.CostOfGoodsSold, r.GrossProfit)).Should().Equal(
                ("Winner", 12m,  600m,  240m,  360m),
                ("Big",    100m, 1200m, 1000m, 200m),
                ("Loser",  5m,   40m,   50m,   -10m));
            report.Data[2].MarginPercent.Should().Be(-25m);
        }
        _ = org;
    }

    [Fact]
    public async Task Revenue_is_after_discount_and_before_tax()
    {
        var rig = new ProductLedgerRig();
        var p = Guid.NewGuid();
        rig.Variants.Products[p] = p;
        var seeder = new SalesBookSeeder();

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        await SalesBookSeeder.WriteAsync(db,
            seeder.Sale(rig.Org, "SINV-1", "ISSUED", "PKR", Jan, new Line(p, p, Qty: 2m, Price: 100m, Cost: 30m, Discount: 10m, Tax: 17m)));   // 200 - 20 = 180; tax is not revenue

        var row = (await new ProductLedgerQueryService(db, rig.Variants).GetProfitabilityAsync(new ProductProfitabilityFilter())).Data.Single();

        (row.Revenue, row.CostOfGoodsSold, row.GrossProfit, row.MarginPercent).Should().Be((180m, 60m, 120m, 66.67m));
    }

    [Fact]
    public async Task Only_sales_that_stand_are_counted_not_drafts_cancelled_credited_or_deleted_ones()
    {
        var rig = new ProductLedgerRig();
        var p = Guid.NewGuid();
        rig.Variants.Products[p] = p;
        var seeder = new SalesBookSeeder();
        var line = new SalesBookSeeder.Line(p, p, Qty: 1m, Price: 100m, Cost: 40m);

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        var deleted = seeder.Sale(rig.Org, "SINV-DEL", "ISSUED", "PKR", Jan, line);
        deleted.Invoice.IsDelete = true;
        await SalesBookSeeder.WriteAsync(db,
            seeder.Sale(rig.Org, "SINV-1", "ISSUED", "PKR", Jan, line),
            seeder.Sale(rig.Org, "SINV-2", "PARTIALLY_PAID", "PKR", Jan, line),
            seeder.Sale(rig.Org, "SINV-3", "PAID", "PKR", Jan, line),
            seeder.Sale(rig.Org, "SINV-4", "OVERDUE", "PKR", Jan, line),
            seeder.Sale(rig.Org, "SINV-5", "DRAFT", "PKR", Jan, line),
            seeder.Sale(rig.Org, "SINV-6", "CANCELLED", "PKR", Jan, line),
            seeder.Sale(rig.Org, "SINV-7", "CREDIT_NOTE", "PKR", Jan, line),
            deleted);

        var row = (await new ProductLedgerQueryService(db, rig.Variants).GetProfitabilityAsync(new ProductProfitabilityFilter())).Data.Single();

        (row.QuantitySold, row.Revenue, row.CostOfGoodsSold).Should().Be((4m, 400m, 160m), "the four issued statuses, and nothing else");
    }

    [Fact]
    public async Task A_product_sold_in_two_currencies_is_two_rows_never_one_figure_adding_rupees_to_dollars()
    {
        var rig = new ProductLedgerRig();
        var p = Guid.NewGuid();
        rig.Variants.Products[p] = p;
        var seeder = new SalesBookSeeder();

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        await SalesBookSeeder.WriteAsync(db,
            seeder.Sale(rig.Org, "SINV-1", "ISSUED", "PKR", Jan, new Line(p, p, Qty: 10m, Price: 100m, Cost: 40m)),
            seeder.Sale(rig.Org, "SINV-2", "ISSUED", "USD", Jan, new Line(p, p, Qty: 3m,  Price: 2m,   Cost: 40m)));

        var rows = (await new ProductLedgerQueryService(db, rig.Variants).GetProfitabilityAsync(new ProductProfitabilityFilter())).Data;

        rows.Select(r => (r.CurrencyCode, r.QuantitySold, r.Revenue, r.CostOfGoodsSold)).Should().BeEquivalentTo(
            [("PKR", 10m, 1000m, 400m), ("USD", 3m, 6m, 120m)]);
    }

    [Fact]
    public async Task Two_lines_of_one_variant_on_one_invoice_are_counted_once_each_not_multiplied()
    {
        var rig = new ProductLedgerRig();
        var p = Guid.NewGuid();
        rig.Variants.Products[p] = p;
        var seeder = new SalesBookSeeder();

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        await SalesBookSeeder.WriteAsync(db,
            seeder.Sale(rig.Org, "SINV-1", "ISSUED", "PKR", Jan,
                new Line(p, p, Qty: 30m, Price: 15m, Cost: 10m),
                new Line(p, p, Qty: 20m, Price: 15m, Cost: 10m)));                   // two lines, two entries, one variant

        var row = (await new ProductLedgerQueryService(db, rig.Variants).GetProfitabilityAsync(new ProductProfitabilityFilter())).Data.Single();

        (row.QuantitySold, row.Revenue, row.CostOfGoodsSold, row.GrossProfit).Should().Be((50m, 750m, 500m, 250m));
    }

    [Fact]
    public async Task A_sale_from_before_the_ledger_was_written_to_has_no_cost_so_it_is_not_in_the_report_at_all()
    {
        var rig = new ProductLedgerRig();
        var p = Guid.NewGuid();
        rig.Variants.Products[p] = p;
        var seeder = new SalesBookSeeder();

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        var counted = seeder.Sale(rig.Org, "SINV-NEW", "ISSUED", "PKR", Jan, new Line(p, p, Qty: 1m, Price: 100m, Cost: 40m));
        var old = seeder.Sale(rig.Org, "SINV-OLD", "ISSUED", "PKR", Jan, new Line(p, p, Qty: 1m, Price: 100m, Cost: 40m));
        old.Entries.Clear();                                                       // issued before P8-03: an invoice, no entry
        await SalesBookSeeder.WriteAsync(db, counted, old);

        var row = (await new ProductLedgerQueryService(db, rig.Variants).GetProfitabilityAsync(new ProductProfitabilityFilter())).Data.Single();

        (row.Revenue, row.CostOfGoodsSold, row.MarginPercent).Should().Be((100m, 40m, 60m), "not 200 of revenue against 40 of cost");
    }

    [Fact]
    public async Task The_period_is_the_day_the_sales_cost_was_booked_inclusive_at_both_ends()
    {
        var rig = new ProductLedgerRig();
        var p = Guid.NewGuid();
        rig.Variants.Products[p] = p;
        var seeder = new SalesBookSeeder();
        var line = new SalesBookSeeder.Line(p, p, Qty: 1m, Price: 100m, Cost: 40m);

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        await SalesBookSeeder.WriteAsync(db,
            seeder.Sale(rig.Org, "SINV-1", "ISSUED", "PKR", new DateTime(2026, 9, 1, 9, 0, 0), line),
            seeder.Sale(rig.Org, "SINV-2", "ISSUED", "PKR", new DateTime(2026, 9, 15, 23, 59, 0), line),
            seeder.Sale(rig.Org, "SINV-3", "ISSUED", "PKR", new DateTime(2026, 9, 16, 0, 1, 0), line));
        var svc = new ProductLedgerQueryService(db, rig.Variants);

        (await svc.GetProfitabilityAsync(new ProductProfitabilityFilter { DateFrom = new DateTime(2026, 9, 1), DateTo = new DateTime(2026, 9, 15) }))
            .Data.Single().QuantitySold.Should().Be(2m, "the 15th at 23:59 is in, the 16th is not");
        (await svc.GetProfitabilityAsync(new ProductProfitabilityFilter { DateFrom = new DateTime(2026, 9, 16) }))
            .Data.Single().QuantitySold.Should().Be(1m);
        (await svc.GetProfitabilityAsync(new ProductProfitabilityFilter { DateTo = new DateTime(2026, 8, 31) })).Data.Should().BeEmpty();
    }

    [Fact]
    public async Task The_report_pages_the_ranked_products_and_names_only_the_page()
    {
        var rig = new ProductLedgerRig();
        var seeder = new SalesBookSeeder();
        var products = Enumerable.Range(1, 5).Select(_ => Guid.NewGuid()).ToList();
        foreach (var (p, i) in products.Select((p, i) => (p, i)))
        {
            rig.Variants.Products[p] = p;
            rig.Variants.ProductNames[p] = $"Product {i + 1}";
        }

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        // Profit 100, 200, 300, 400, 500 for products 1..5.
        await SalesBookSeeder.WriteAsync(db, [.. products.Select((p, i) =>
            seeder.Sale(rig.Org, $"SINV-{i + 1}", "ISSUED", "PKR", Jan, new Line(p, p, Qty: 1m, Price: (i + 1) * 100m + 10m, Cost: 10m)))]);
        var svc = new ProductLedgerQueryService(db, rig.Variants);

        var page1 = await svc.GetProfitabilityAsync(new ProductProfitabilityFilter { PageSize = 2 });
        var page3 = await svc.GetProfitabilityAsync(new ProductProfitabilityFilter { PageSize = 2, Page = 3 });

        page1.TotalRecords.Should().Be(5);
        page1.TotalPages.Should().Be(3);
        page1.Data.Select(r => r.ProductName).Should().Equal("Product 5", "Product 4");
        page3.Data.Select(r => r.ProductName).Should().Equal("Product 1");
        rig.Variants.Calls.Should().Be(2, "inventory is asked once per page, for that page's products");
    }

    [Fact]
    public async Task A_product_inventory_no_longer_knows_still_reports_with_no_name()
    {
        var rig = new ProductLedgerRig();
        var p = Guid.NewGuid();                                                    // never registered with inventory
        var seeder = new SalesBookSeeder();

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        await SalesBookSeeder.WriteAsync(db, seeder.Sale(rig.Org, "SINV-1", "ISSUED", "PKR", Jan, new Line(p, p, Qty: 1m, Price: 100m, Cost: 40m)));

        var row = (await new ProductLedgerQueryService(db, rig.Variants).GetProfitabilityAsync(new ProductProfitabilityFilter())).Data.Single();

        row.ProductName.Should().BeNull();
        row.ProductUuid.Should().Be(p);
        row.Revenue.Should().Be(100m);
    }

    [Fact]
    public async Task An_empty_period_is_an_empty_report_and_asks_inventory_nothing()
    {
        var rig = new ProductLedgerRig();
        await using var db = Receivables.Db(rig.Org, rig.DbName);

        var report = await new ProductLedgerQueryService(db, rig.Variants).GetProfitabilityAsync(new ProductProfitabilityFilter());

        (report.TotalRecords, report.Data.Count, report.TotalPages).Should().Be((0, 0, 0));
        rig.Variants.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Another_organizations_sales_are_not_in_this_organizations_report()
    {
        var rig = new ProductLedgerRig();
        var p = Guid.NewGuid();
        rig.Variants.Products[p] = p;
        var otherOrg = Guid.NewGuid();
        var seeder = new SalesBookSeeder();
        var line = new SalesBookSeeder.Line(p, p, Qty: 1m, Price: 100m, Cost: 40m);

        await using (var seed = Receivables.Auditor(rig.DbName))
            await SalesBookSeeder.WriteAsync(seed,
                seeder.Sale(rig.Org, "SINV-1", "ISSUED", "PKR", Jan, line),
                seeder.Sale(otherOrg, "SINV-2", "ISSUED", "PKR", Jan, line with { Qty = 50m }));

        await using var db = Receivables.Db(rig.Org, rig.DbName);
        var row = (await new ProductLedgerQueryService(db, rig.Variants).GetProfitabilityAsync(new ProductProfitabilityFilter())).Data.Single();

        (row.QuantitySold, row.Revenue).Should().Be((1m, 100m));
    }

    [Fact]
    public async Task A_start_date_after_the_end_date_is_refused_by_the_report_too()
    {
        var rig = new ProductLedgerRig();
        await using var db = Receivables.Db(rig.Org, rig.DbName);

        var act = () => new ProductLedgerQueryService(db, rig.Variants).GetProfitabilityAsync(
            new ProductProfitabilityFilter { DateFrom = new DateTime(2026, 9, 20), DateTo = new DateTime(2026, 9, 1) });

        await act.Should().ThrowAsync<BadRequestException>();
    }
}
