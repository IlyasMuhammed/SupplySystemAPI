using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Exceptions;
using Xunit;

using static SMS.Modules.Finance.Tests.ReceivablesDesk;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29 TC-12 on the receivables side — a customer of organization A, and everything owed by or paid by
/// them, is invisible to organization B and cannot be referenced from it. Two organizations share one
/// database here, as they do in production, and every question is asked from B about A's records.
/// (That the customer itself cannot be found or put on a sale order from another organization is
/// covered where the customer lives: <c>BusinessPartnerMultiTenantIsolationTests</c> in Suppliers, and
/// Demand and Logistics beside it.)
/// </summary>
public class ReceivablesTenantIsolationTests
{
    private const int User = ReceivablesDesk.User;

    private sealed class TwoOrganizations
    {
        public required ReceivablesWorld World;
        public required ReceivablesDesk A;
        public required ReceivablesDesk B;
        public required Guid CustomerA;
        public required Guid CustomerB;
        public required Invoiced InvoiceA;
        public required Invoiced InvoiceB;
        public required CustomerPaymentRecorded ChequeA;
        public required CustomerPaymentRecorded PaymentB;
    }

    /// <summary>Both organizations have a customer who has been billed and has paid part by cheque (A) or transfer (B).</summary>
    private static async Task<TwoOrganizations> Both()
    {
        var world = new ReceivablesWorld();
        var a = world.For(Guid.NewGuid());
        var b = world.For(Guid.NewGuid());

        var customerA = a.NewCustomer("Alpha Traders");
        var customerB = b.NewCustomer("Beta Stores");

        var invoiceA = await a.InvoiceAsync(customerA, 1000m);
        var invoiceB = await b.InvoiceAsync(customerB, 700m);

        return new TwoOrganizations
        {
            World = world, A = a, B = b, CustomerA = customerA, CustomerB = customerB,
            InvoiceA = invoiceA, InvoiceB = invoiceB,
            ChequeA  = await a.PayAsync(customerA, 400m, "CHEQUE"),
            PaymentB = await b.PayAsync(customerB, 200m)
        };
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Organization_B_sees_none_of_organization_As_receivables_anywhere()
    {
        var t = await Both();
        await using var b = t.B.Scope();

        (await b.Invoices.GetAsync(t.InvoiceA.Uuid)).Should().BeNull();
        (await b.Payments.GetAsync(t.ChequeA.PaymentUuid)).Should().BeNull();

        var invoicesOfA = await b.Invoices.ListAsync(new SalesInvoiceFilter { PartnerId = t.CustomerA });
        invoicesOfA.Data.Should().BeEmpty();
        invoicesOfA.TotalRecords.Should().Be(0);

        (await b.Payments.ListAsync(new CustomerPaymentFilter { PartnerId = t.CustomerA })).Data.Should().BeEmpty();
        (await b.Ledger.GetLedgerAsync(t.CustomerA, new CustomerLedgerFilter())).Data.Should().BeEmpty();

        // Unfiltered, B's own are all it gets.
        (await b.Invoices.ListAsync(new SalesInvoiceFilter())).Data.Select(i => i.Uuid).Should().Equal(t.InvoiceB.Uuid);
        (await b.Payments.ListAsync(new CustomerPaymentFilter())).Data.Select(p => p.Uuid).Should().Equal(t.PaymentB.PaymentUuid);
    }

    [Fact]
    public async Task Each_of_the_five_receivables_tables_shows_an_organization_only_its_own_rows()
    {
        var t = await Both();
        await using var a = t.A.Scope();
        await using var b = t.B.Scope();

        // One invoice, one line, one payment, one allocation, two ledger entries each.
        foreach (var scope in new[] { a, b })
        {
            (await scope.Db.SalesInvoices.CountAsync()).Should().Be(1);
            (await scope.Db.SalesInvoiceLines.CountAsync()).Should().Be(1);
            (await scope.Db.CustomerPayments.CountAsync()).Should().Be(1);
            (await scope.Db.PaymentAllocations.CountAsync()).Should().Be(1);
            (await scope.Db.CustomerLedgerEntries.CountAsync()).Should().Be(2);
        }

        // Between them they hold everything: nothing is hidden from both.
        await using var auditor = Receivables.Auditor(t.World.DbName);
        (await auditor.SalesInvoices.CountAsync()).Should().Be(2);
        (await auditor.SalesInvoiceLines.CountAsync()).Should().Be(2);
        (await auditor.CustomerPayments.CountAsync()).Should().Be(2);
        (await auditor.PaymentAllocations.CountAsync()).Should().Be(2);
        (await auditor.CustomerLedgerEntries.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task What_each_organization_writes_carries_its_own_organization_and_no_other()
    {
        var t = await Both();
        await using var auditor = Receivables.Auditor(t.World.DbName);

        var ownerOf = new Dictionary<Guid, Guid> { [t.CustomerA] = t.A.Org, [t.CustomerB] = t.B.Org };

        (await auditor.SalesInvoices.ToListAsync()).Should().OnlyContain(i => i.OrganizationId == ownerOf[i.PartnerId]);
        (await auditor.CustomerPayments.ToListAsync()).Should().OnlyContain(p => p.OrganizationId == ownerOf[p.PartnerId]);
        (await auditor.CustomerLedgerEntries.ToListAsync()).Should().OnlyContain(e => e.OrganizationId == ownerOf[e.PartnerId]);

        var allocations = await auditor.PaymentAllocations.Include(a => a.CustomerPayment).Include(a => a.SalesInvoice).ToListAsync();
        allocations.Should().NotBeEmpty().And.OnlyContain(a =>
            a.OrganizationId == a.CustomerPayment.OrganizationId && a.OrganizationId == a.SalesInvoice.OrganizationId,
            "a payment is only ever applied to an invoice of its own organization");
    }

    // ── Acting on it ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Organization_B_cannot_record_a_payment_for_organization_As_customer()
    {
        var t = await Both();
        var before = (await t.A.BooksAsync(t.CustomerA)).Fingerprint();

        var act = () => t.B.PayAsync(t.CustomerA, 100m);

        await act.Should().ThrowAsync<NotFoundException>("the customer is not one B can see, so there is no one to credit");
        (await t.A.BooksAsync(t.CustomerA)).Fingerprint().Should().Be(before);
        (await t.B.BooksAsync(t.CustomerA)).Payments.Should().BeEmpty();
    }

    [Fact]
    public async Task Organization_B_cannot_bounce_or_allocate_organization_As_payment()
    {
        var t = await Both();
        var before = (await t.A.BooksAsync(t.CustomerA)).Fingerprint();

        var bounce = () => t.B.BounceAsync(t.ChequeA.PaymentUuid);
        await bounce.Should().ThrowAsync<NotFoundException>();

        await using var b = t.B.Scope();
        var allocate = () => b.Payments.AllocateAsync(t.ChequeA.PaymentUuid, null, User);
        await allocate.Should().ThrowAsync<NotFoundException>();

        (await t.A.BooksAsync(t.CustomerA)).Fingerprint().Should().Be(before, "A's cheque is still RECEIVED and its ledger untouched");
    }

    [Fact]
    public async Task Organization_B_cannot_name_organization_As_invoice_in_its_own_payments_allocation()
    {
        var t = await Both();
        var beforeA = (await t.A.BooksAsync(t.CustomerA)).Fingerprint();
        var beforeB = (await t.B.BooksAsync(t.CustomerB)).Fingerprint();

        var act = () => t.B.PayAsync(t.CustomerB, 100m, allocations: [new ManualPaymentAllocation(t.InvoiceA.Uuid, 100m)]);

        await act.Should().ThrowAsync<NotFoundException>();
        (await t.A.BooksAsync(t.CustomerA)).Fingerprint().Should().Be(beforeA);
        (await t.B.BooksAsync(t.CustomerB)).Fingerprint().Should().Be(beforeB, "the refused payment left nothing behind");
    }

    [Fact]
    public async Task Organization_B_cannot_issue_edit_or_delete_organization_As_draft_invoice()
    {
        var world = new ReceivablesWorld();
        var a = world.For(Guid.NewGuid());
        var b = world.For(Guid.NewGuid());
        var customerA = a.NewCustomer();

        var order = await a.PlaceOrderAsync(customerA, new OrderLine(10m, 25m));
        await using var scopeA = a.Scope();
        var draft = await scopeA.Invoices.CreateFromFulfillmentAsync(a.Deliver(order, "DELIVERED", (0, 10m)), User);

        await using var scopeB = b.Scope();
        var issue  = () => scopeB.Invoices.IssueAsync(draft.InvoiceUuid, User);
        var edit   = () => scopeB.Invoices.UpdateAsync(draft.InvoiceUuid, new UpdateSalesInvoiceRequest { Notes = "hijacked" }, User);
        var delete = () => scopeB.Invoices.DeleteAsync(draft.InvoiceUuid, User);

        await issue.Should().ThrowAsync<NotFoundException>();
        await edit.Should().ThrowAsync<NotFoundException>();
        await delete.Should().ThrowAsync<NotFoundException>();

        var books = await a.BooksAsync(customerA);
        books.Invoices.Should().ContainSingle().Which.Should().Match<SalesInvoice>(i => i.Status == "DRAFT" && !i.IsDelete && i.Notes != "hijacked");
        books.Ledger.Should().BeEmpty("B issuing A's draft would have booked a receivable on A's customer");
    }

    [Fact]
    public async Task Organization_B_cannot_invoice_organization_As_delivery_or_bill_against_organization_As_order()
    {
        var world = new ReceivablesWorld();
        var a = world.For(Guid.NewGuid());
        var b = world.For(Guid.NewGuid());
        var customerA = a.NewCustomer();

        var orderA    = await a.PlaceOrderAsync(customerA, new OrderLine(10m, 25m));
        var deliveryA = a.Deliver(orderA, "DELIVERED", (0, 10m));

        // A delivery of B's own that points at A's order: the order is A's, so B cannot see it to bill it.
        var forgedByB = b.Deliver(orderA, "DELIVERED", (0, 10m));

        await using var scopeB = b.Scope();
        var theirDelivery = () => scopeB.Invoices.CreateFromFulfillmentAsync(deliveryA, User);
        var theirOrder    = () => scopeB.Invoices.CreateFromFulfillmentAsync(forgedByB, User);

        (await theirDelivery.Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Contain("Delivery");
        (await theirOrder.Should().ThrowAsync<NotFoundException>()).Which.Message.Should().Contain("SaleOrder");

        (await a.BooksAsync(customerA)).Invoices.Should().BeEmpty();
        (await b.BooksAsync(customerA)).Invoices.Should().BeEmpty();
    }

    [Fact]
    public async Task A_payments_fifo_only_ever_reaches_its_own_organizations_invoices_even_for_the_same_customer_id()
    {
        // Not a state production can reach — a customer belongs to one organization — but the filter, not
        // the data, is what must keep it that way: seed A's older invoice for the very same customer id.
        var world = new ReceivablesWorld();
        var a = world.For(Guid.NewGuid());
        var b = world.For(Guid.NewGuid());
        var shared = b.NewCustomer("Shared Id Ltd");

        var olderInA = Receivables.Invoice(a.Org, shared, "SINV-A-OLD", new DateTime(2026, 8, 1), 100m);
        var newerInB = Receivables.Invoice(b.Org, shared, "SINV-B-NEW", new DateTime(2026, 9, 1), 100m);
        await using (var seed = Receivables.Auditor(world.DbName))
            await Receivables.Seed(seed, olderInA, newerInB);

        var payment = await b.PayAsync(shared, 100m);

        payment.Allocations.Should().ContainSingle().Which.InvoiceNumber.Should().Be("SINV-B-NEW");

        await using var auditor = Receivables.Auditor(world.DbName);
        (await auditor.SalesInvoices.SingleAsync(i => i.InvoiceNumber == "SINV-A-OLD")).BalanceDue.Should().Be(100m, "A's invoice is not B's to pay");
        (await auditor.SalesInvoices.SingleAsync(i => i.InvoiceNumber == "SINV-B-NEW")).Status.Should().Be("PAID");
    }

    // ── Independent counters, independent ledgers ────────────────────────────

    [Fact]
    public async Task Each_organization_numbers_its_invoices_and_payments_from_its_own_counter()
    {
        var t = await Both();

        t.InvoiceA.Number.Should().Be(t.InvoiceB.Number, "both are the first invoice of the day in their own organization");
        t.ChequeA.PaymentNumber.Should().Be(t.PaymentB.PaymentNumber);
        t.InvoiceA.Number.Should().MatchRegex(@"^SINV-20260920-0001$");

        (await t.A.InvoiceAsync(t.CustomerA, 10m)).Number.Should().Be("SINV-20260920-0002", "B's activity never consumed a number of A's");
    }

    [Fact]
    public async Task Ledger_sequences_and_balances_are_kept_per_organizations_customer()
    {
        var t = await Both();
        await t.B.BounceAsync((await t.B.PayAsync(t.CustomerB, 50m, "CHEQUE")).PaymentUuid);

        (await t.A.OwesAsync(t.CustomerA)).Should().Be(600m, "1000 billed, 400 received");
        (await t.B.OwesAsync(t.CustomerB)).Should().Be(500m, "700 billed, 200 received; the 50 cheque came and went");

        (await t.A.BooksAsync(t.CustomerA)).Ledger.Select(e => e.SequenceNo).Should().Equal(1, 2);
        (await t.B.BooksAsync(t.CustomerB)).Ledger.Select(e => e.SequenceNo).Should().Equal(1, 2, 3, 4);

        ReceivablesInvariants.Violations(await t.A.BooksAsync(t.CustomerA), t.World.Clock.Value.Date).Should().BeEmpty();
        ReceivablesInvariants.Violations(await t.B.BooksAsync(t.CustomerB), t.World.Clock.Value.Date).Should().BeEmpty();
    }

    // ── The job that has no organization ─────────────────────────────────────

    [Fact]
    public async Task The_overdue_sweep_reaches_every_organization_and_leaves_each_invoice_with_its_owner()
    {
        var t = await Both();
        t.World.Clock.Value = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);

        (await t.World.SweepOverdueAsync()).Should().Be(2);

        var booksA = await t.A.BooksAsync(t.CustomerA);
        var booksB = await t.B.BooksAsync(t.CustomerB);
        booksA.Invoices.Should().ContainSingle().Which.Status.Should().Be("OVERDUE");
        booksB.Invoices.Should().ContainSingle().Which.Status.Should().Be("OVERDUE");

        await using var auditor = Receivables.Auditor(t.World.DbName);
        (await auditor.SalesInvoices.ToListAsync()).Should().OnlyContain(i =>
            (i.PartnerId == t.CustomerA && i.OrganizationId == t.A.Org) || (i.PartnerId == t.CustomerB && i.OrganizationId == t.B.Org));
    }

    // ── Isolation by replay ──────────────────────────────────────────────────

    [Fact]
    public async Task What_organization_A_ends_up_with_is_the_same_whether_or_not_organization_B_was_busy_beside_it()
    {
        var alone = await RunScriptForA(withNoiseFromB: false);
        var beside = await RunScriptForA(withNoiseFromB: true);

        beside.Should().Equal(alone, "nothing B did may change a number, a balance, a status or a sequence of A's");
        alone.Should().HaveCountGreaterThan(10, "the script leaves a real set of records to compare");
    }

    /// <summary>A's month, as a list of comparable facts with no ids or timestamps in it.</summary>
    private static async Task<List<string>> RunScriptForA(bool withNoiseFromB)
    {
        var world = new ReceivablesWorld();
        var a = world.For(Guid.NewGuid());
        var b = world.For(Guid.NewGuid());
        var customerA = a.NewCustomer("Alpha Traders");
        var customerB = b.NewCustomer("Beta Stores");

        async Task Noise()
        {
            if (!withNoiseFromB) return;
            await b.InvoiceAsync(customerB, 333.33m);
            var cheque = await b.PayAsync(customerB, 111m, "CHEQUE");
            await b.PayAsync(customerB, 50m);
            await b.BounceAsync(cheque.PaymentUuid, "noise");
        }

        var first = await a.InvoiceAsync(customerA, 1000m);                                     await Noise();
        await a.InvoiceAsync(customerA, 250.75m);                                                await Noise();
        var cheque1 = await a.PayAsync(customerA, 600m, "CHEQUE");                               await Noise();
        await a.PayAsync(customerA, 100m, allocations: [new ManualPaymentAllocation(first.Uuid, 100m)]); await Noise();
        await a.BounceAsync(cheque1.PaymentUuid, "Refer to drawer");                             await Noise();
        world.Clock.Value = world.Clock.Value.AddDays(40);
        await world.SweepOverdueAsync();                                                         await Noise();
        await a.PayAsync(customerA, 900m);                                                       await Noise();

        var books = await a.BooksAsync(customerA);

        return
        [
            .. books.Invoices.Select(i => $"I {i.InvoiceNumber} {i.Status} {i.GrandTotal} {i.AmountPaid} {i.BalanceDue} {i.DueDate:yyyy-MM-dd}"),
            .. books.Payments.Select(p => $"P {p.PaymentNumber} {p.PaymentMethod} {p.Status} {p.Amount} applied={p.Allocations.Sum(x => x.AllocatedAmount)}"),
            .. books.Ledger.Select(e => $"L {e.SequenceNo} {e.EntryType} {e.DebitAmount} {e.CreditAmount} {e.RunningBalance} {e.ReferenceNumber}")
        ];
    }
}
