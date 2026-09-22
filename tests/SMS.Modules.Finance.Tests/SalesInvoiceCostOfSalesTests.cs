using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Exceptions;
using Xunit;

using static SMS.Modules.Finance.Tests.ReceivablesDesk;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P8-03 §11.2/§11.3 — issuing a sales invoice books the cost of what was sold on the product
/// ledger, in the same unit of work as the status change and the customer's debit: an OUT entry per
/// line, costed at the variant's weighted-average cost and never at the selling price (TC-10).
/// </summary>
public class SalesInvoiceCostOfSalesTests
{
    private const int User = ReceivablesDesk.User;

    private static (ReceivablesWorld World, ReceivablesDesk Desk, Guid Customer) Setup()
    {
        var world = new ReceivablesWorld();
        var desk  = world.For(Guid.NewGuid());
        return (world, desk, desk.NewCustomer());
    }

    /// <summary>A draft invoice for a delivered order — raised, not yet issued.</summary>
    private static async Task<(PlacedOrder Order, Guid Invoice)> DraftAsync(ReceivablesDesk desk, Guid customer, params OrderLine[] lines)
    {
        var order = await desk.PlaceOrderAsync(customer, lines);
        var delivery = desk.Deliver(order, "DELIVERED", [.. lines.Select((l, i) => (i, l.Fulfilled ?? l.Qty))]);

        await using var scope = desk.Scope();
        var created = await scope.Invoices.CreateFromFulfillmentAsync(delivery, User);
        return (order, created.InvoiceUuid);
    }

    // ── TC-10 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_grn_of_100_at_10_and_a_sale_of_30_at_15_books_a_cogs_of_300_at_a_wac_of_10()
    {
        var (_, desk, customer) = Setup();
        var order = await desk.PlaceOrderAsync(customer, new OrderLine(Qty: 30m, Price: 15m, Stocked: false));
        var variant = order.VariantUuids[0];
        await desk.StockAsync(variant, 100m, 10m);                                   // GRN 100 @ 10

        var invoiced = await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 30m)));

        invoiced.Amount.Should().Be(450m, "30 at 15 is what the customer is billed: the revenue");

        var entries = await desk.ProductLedgerAsync(variant);
        entries.Select(e => (e.EntryType, e.Direction, e.Quantity, e.UnitCost, e.TotalCost, e.RunningQty, e.RunningValue)).Should().Equal(
            ("PURCHASE", "IN",  100m, 10m, 1000m, 100m, 1000m),
            ("SALE",     "OUT", 30m,  10m, 300m,  70m,  700m));
        entries[1].UnitCost.Should().NotBe(15m, "a sale is costed at the weighted average, never at its selling price");
        (entries[1].RunningValue / entries[1].RunningQty).Should().Be(10m, "the WAC after the sale is still 10");
    }

    [Fact]
    public async Task The_sale_is_costed_at_the_blend_of_what_was_bought()
    {
        var (_, desk, customer) = Setup();
        var order = await desk.PlaceOrderAsync(customer, new OrderLine(Qty: 30m, Price: 40m, Stocked: false));
        var variant = order.VariantUuids[0];
        await desk.StockAsync(variant, 100m, 10m);
        await desk.StockAsync(variant, 50m, 16m);                                    // 150 worth 1800: WAC 12

        await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 30m)));

        var sale = (await desk.ProductLedgerAsync(variant)).Last();
        (sale.SequenceNo, sale.UnitCost, sale.TotalCost, sale.RunningQty, sale.RunningValue).Should().Be((3, 12m, 360m, 120m, 1440m));
    }

    [Fact]
    public async Task The_entry_names_the_invoice_the_customer_and_the_selling_price()
    {
        var (world, desk, customer) = Setup();
        var order = await desk.PlaceOrderAsync(customer, new OrderLine(Qty: 30m, Price: 15m));

        var invoiced = await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 30m)));

        var sale = (await desk.ProductLedgerAsync(order.VariantUuids[0])).Single(e => e.EntryType == "SALE");
        sale.ReferenceType.Should().Be("SalesInvoice");
        sale.ReferenceId.Should().Be(invoiced.Uuid);
        sale.ReferenceNumber.Should().Be(invoiced.Number);
        sale.PartnerId.Should().Be(customer);
        sale.CreatedBy.Should().Be(User);
        sale.EntryDate.Should().Be(world.Clock.Value);
        sale.Narration.Should().Contain(invoiced.Number).And.Contain("line 1").And.Contain("30 sold at 15");
    }

    [Fact]
    public async Task Raising_a_draft_books_no_cost_only_issuing_does()
    {
        var (_, desk, customer) = Setup();
        var (order, invoice) = await DraftAsync(desk, customer, new OrderLine(Qty: 30m, Price: 15m, Cost: 10m));

        (await desk.ProductLedgerAsync(order.VariantUuids[0])).Should().ContainSingle("only the purchase: a draft sold nothing");

        await using var scope = desk.Scope();
        await scope.Invoices.IssueAsync(invoice, User);

        (await desk.ProductLedgerAsync(order.VariantUuids[0])).Select(e => e.EntryType).Should().Equal("PURCHASE", "SALE");
    }

    // ── One entry per line ───────────────────────────────────────────────────

    [Fact]
    public async Task Each_line_of_an_invoice_is_costed_at_its_own_variants_average()
    {
        var (_, desk, customer) = Setup();
        var (order, invoice) = await DraftAsync(desk, customer,
            new OrderLine(Qty: 10m, Price: 100m, Cost: 30m),
            new OrderLine(Qty: 4m,  Price: 50m,  Cost: 12.5m));

        await using var scope = desk.Scope();
        await scope.Invoices.IssueAsync(invoice, User);

        var first  = (await desk.ProductLedgerAsync(order.VariantUuids[0])).Last();
        var second = (await desk.ProductLedgerAsync(order.VariantUuids[1])).Last();
        (first.EntryType, first.UnitCost, first.TotalCost).Should().Be(("SALE", 30m, 300m));
        (second.EntryType, second.UnitCost, second.TotalCost).Should().Be(("SALE", 12.5m, 50m));
        first.ReferenceId.Should().Be(second.ReferenceId, "both lines are on one invoice");
        second.Narration.Should().Contain("line 2");
    }

    [Fact]
    public async Task Two_lines_of_one_variant_chain_within_the_one_invoice()
    {
        var (_, desk, customer) = Setup();
        var order = await desk.PlaceOrderAsync(customer,
            new OrderLine(Qty: 30m, Price: 15m, Stocked: false),
            new OrderLine(Qty: 20m, Price: 15m, Stocked: false));

        // Both lines are the same variant.
        var sharedVariant = order.VariantUuids[0];
        await using (var edit = desk.Scope())
        {
            var soLines = await edit.Demand.SaleOrderLines.OrderBy(l => l.Id).ToListAsync();
            soLines[1].VariantUuid = sharedVariant;
            await edit.Demand.SaveChangesAsync();
        }
        await desk.StockAsync(sharedVariant, 100m, 10m);

        var invoiced = await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 30m), (1, 20m)));

        var entries = await desk.ProductLedgerAsync(sharedVariant);
        entries.Select(e => (e.SequenceNo, e.EntryType, e.Quantity, e.TotalCost, e.RunningQty, e.RunningValue)).Should().Equal(
            (1, "PURCHASE", 100m, 1000m, 100m, 1000m),
            (2, "SALE",     30m,  300m,  70m,  700m),
            (3, "SALE",     20m,  200m,  50m,  500m));
        invoiced.Amount.Should().Be(750m);
    }

    // ── What is refused ──────────────────────────────────────────────────────

    [Fact]
    public async Task An_invoice_the_ledger_cannot_cost_is_not_issued_and_nothing_is_left_behind()
    {
        var (_, desk, customer) = Setup();
        var (order, invoice) = await DraftAsync(desk, customer, new OrderLine(Qty: 30m, Price: 15m, Stocked: false));
        await desk.StockAsync(order.VariantUuids[0], 20m, 10m);                      // 20 on the ledger, 30 sold

        await using var scope = desk.Scope();
        var act = () => scope.Invoices.IssueAsync(invoice, User);

        var thrown = (await act.Should().ThrowAsync<ConflictException>()).Which;
        thrown.Message.Should().Contain("cannot be issued").And.Contain("holds 20");

        scope.Db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified)
            .Should().BeEmpty("the half-issued invoice and its entries must not wait on the context");

        var books = await desk.BooksAsync(customer);
        books.Invoices.Single().Status.Should().Be("DRAFT");
        books.Ledger.Should().BeEmpty("no receivable was booked either");
        (await desk.ProductLedgerAsync(order.VariantUuids[0])).Should().ContainSingle("no SALE entry");
    }

    [Fact]
    public async Task Once_the_stock_is_on_the_ledger_the_same_invoice_can_be_issued()
    {
        var (_, desk, customer) = Setup();
        var (order, invoice) = await DraftAsync(desk, customer, new OrderLine(Qty: 30m, Price: 15m, Stocked: false));

        await using (var refused = desk.Scope())
            await FluentActions.Awaiting(() => refused.Invoices.IssueAsync(invoice, User)).Should().ThrowAsync<ConflictException>();

        await desk.StockAsync(order.VariantUuids[0], 30m, 10m);

        await using var scope = desk.Scope();
        var issued = await scope.Invoices.IssueAsync(invoice, User);

        issued.Status.Should().Be("ISSUED");
        (await desk.ProductLedgerAsync(order.VariantUuids[0])).Select(e => e.EntryType).Should().Equal("PURCHASE", "SALE");
    }

    [Fact]
    public async Task One_line_the_ledger_cannot_cost_keeps_the_other_lines_entries_out_too()
    {
        var (_, desk, customer) = Setup();
        var (order, invoice) = await DraftAsync(desk, customer,
            new OrderLine(Qty: 10m, Price: 100m, Cost: 30m),
            new OrderLine(Qty: 4m,  Price: 50m,  Stocked: false));

        await using var scope = desk.Scope();
        var act = () => scope.Invoices.IssueAsync(invoice, User);

        await act.Should().ThrowAsync<ConflictException>();
        (await desk.ProductLedgerAsync(order.VariantUuids[0])).Should().ContainSingle("line 1 was costable, but its entry goes with the invoice or not at all");
        (await desk.BooksAsync(customer)).Invoices.Single().Status.Should().Be("DRAFT");
    }

    [Fact]
    public async Task Another_organizations_stock_cannot_cost_this_organizations_sale()
    {
        var world = new ReceivablesWorld();
        var a = world.For(Guid.NewGuid());
        var b = world.For(Guid.NewGuid());
        var customerA = a.NewCustomer();
        var customerB = b.NewCustomer();

        var orderA = await a.PlaceOrderAsync(customerA, new OrderLine(Qty: 5m, Price: 15m, Cost: 10m));
        var orderB = await b.PlaceOrderAsync(customerB, new OrderLine(Qty: 5m, Price: 15m, Stocked: false));

        // B's order line is for A's variant — before B's invoice is raised from it: A holds the stock, B holds none.
        await using (var edit = b.Scope())
        {
            var line = await edit.Demand.SaleOrderLines.SingleAsync();
            line.VariantUuid = orderA.VariantUuids[0];
            await edit.Demand.SaveChangesAsync();
        }

        Guid invoiceB;
        await using (var raise = b.Scope())
            invoiceB = (await raise.Invoices.CreateFromFulfillmentAsync(b.Deliver(orderB, "DELIVERED", (0, 5m)), User)).InvoiceUuid;

        await using var scope = b.Scope();
        var act = () => scope.Invoices.IssueAsync(invoiceB, User);

        await act.Should().ThrowAsync<ConflictException>();
        (await a.ProductLedgerAsync(orderA.VariantUuids[0])).Should().ContainSingle("A's ledger is untouched");
    }

    // ── One transaction ──────────────────────────────────────────────────────

    private sealed class SaveCounter(List<(int Invoices, int CustomerEntries, int ProductEntries)> saves) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var entries = eventData.Context!.ChangeTracker.Entries().ToList();
            saves.Add((
                entries.Count(e => e.Entity is SalesInvoice && e.State == EntityState.Modified),
                entries.Count(e => e.Entity is CustomerLedgerEntry && e.State == EntityState.Added),
                entries.Count(e => e.Entity is ProductLedgerEntry && e.State == EntityState.Added)));
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    [Fact]
    public async Task The_status_the_customers_debit_and_every_lines_cost_are_committed_in_a_single_save()
    {
        var (_, desk, customer) = Setup();
        var (_, invoice) = await DraftAsync(desk, customer,
            new OrderLine(Qty: 10m, Price: 100m, Cost: 30m),
            new OrderLine(Qty: 4m,  Price: 50m,  Cost: 12.5m));

        var saves = new List<(int, int, int)>();
        await using var scope = desk.Scope(new SaveCounter(saves));
        await scope.Invoices.IssueAsync(invoice, User);

        saves.Should().ContainSingle().Which.Should().Be((Invoices: 1, CustomerEntries: 1, ProductEntries: 2));
    }

    [Fact]
    public async Task Losing_the_race_for_a_variants_sequence_retries_and_costs_the_sale_at_the_average_that_moved_on()
    {
        var (_, desk, customer) = Setup();
        var (order, invoice) = await DraftAsync(desk, customer, new OrderLine(Qty: 50m, Price: 40m, Stocked: false));
        var variant = order.VariantUuids[0];
        await desk.StockAsync(variant, 100m, 10m);

        // After the sale has read the ledger, another GRN commits 100 @ 20: 200 worth 3000, so 50 leave at 15.
        var winner = new LoseTheRaceOnce(
            db => db.ChangeTracker.Entries<ProductLedgerEntry>().Any(e => e.State == EntityState.Added),
            () => desk.StockAsync(variant, 100m, 20m));

        await using var scope = desk.Scope(winner);
        var issued = await scope.Invoices.IssueAsync(invoice, User);

        winner.Fired.Should().BeTrue();
        issued.Status.Should().Be("ISSUED");

        var sale = (await desk.ProductLedgerAsync(variant)).Last();
        (sale.SequenceNo, sale.UnitCost, sale.TotalCost, sale.RunningQty, sale.RunningValue).Should().Be((3, 15m, 750m, 150m, 2250m));

        var books = await desk.BooksAsync(customer);
        books.Ledger.Should().ContainSingle("the receivable was booked once, not once per attempt");
    }

    [Fact]
    public async Task A_save_that_keeps_failing_issues_nothing_and_leaves_nothing_tracked()
    {
        var (_, desk, customer) = Setup();
        var (order, invoice) = await DraftAsync(desk, customer, new OrderLine(Qty: 30m, Price: 15m, Cost: 10m));

        var failing = new AlwaysFail();
        await using var scope = desk.Scope(failing);
        var act = () => scope.Invoices.IssueAsync(invoice, User);

        await act.Should().ThrowAsync<DbUpdateException>();
        failing.Attempts.Should().Be(5);
        scope.Db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified).Should().BeEmpty();

        (await desk.BooksAsync(customer)).Invoices.Single().Status.Should().Be("DRAFT");
        (await desk.ProductLedgerAsync(order.VariantUuids[0])).Should().ContainSingle();
    }

    // ── The cost never leaks into the receivable ─────────────────────────────

    [Fact]
    public async Task What_the_customer_owes_is_the_selling_price_whatever_the_goods_cost()
    {
        var (world, desk, customer) = Setup();
        var order = await desk.PlaceOrderAsync(customer, new OrderLine(Qty: 30m, Price: 15m, Cost: 10m));

        var invoiced = await desk.IssueDeliveryAsync(desk.Deliver(order, "DELIVERED", (0, 30m)));

        invoiced.Amount.Should().Be(450m);
        (await desk.OwesAsync(customer)).Should().Be(450m, "the customer's ledger is debited the invoice, not the cost of sales");
        ReceivablesInvariants.Violations(await desk.BooksAsync(customer), world.Clock.Value.Date).Should().BeEmpty();
    }
}
