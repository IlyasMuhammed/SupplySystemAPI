using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-08 — sales invoices and customer payments carry optimistic concurrency on
/// <c>ModifiedDate</c>: a save made from a stale read matches no row and fails rather than
/// overwriting. It needs no new column, but it depends on a convention — every writer of these rows
/// sets <c>ModifiedDate</c> to a fresh value — so this file checks both halves: that the token
/// works, and that each writer keeps to the convention.
/// </summary>
public class ReceivablesConcurrencyTokenTests
{
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateTime Earlier = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    // ── The token itself ─────────────────────────────────────────────────────

    [Fact]
    public void Modified_date_is_the_concurrency_token_of_both_entities()
    {
        using var db = Receivables.Db(Guid.NewGuid(), Guid.NewGuid().ToString());

        foreach (var type in new[] { typeof(SalesInvoice), typeof(CustomerPayment) })
            db.Model.FindEntityType(type)!.FindProperty("ModifiedDate")!.IsConcurrencyToken
              .Should().BeTrue($"{type.Name}.ModifiedDate");
    }

    [Fact]
    public async Task A_stale_write_to_an_invoice_fails_instead_of_overwriting()
    {
        var org = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var invoice = Receivables.Invoice(org, Customer, "SINV-1", new DateTime(2026, 9, 1), 1000m);
        await Receivables.Seed(Receivables.Db(org, dbName), invoice);

        var first  = Receivables.Db(org, dbName);
        var second = Receivables.Db(org, dbName);
        var a = await first.SalesInvoices.SingleAsync();
        var b = await second.SalesInvoices.SingleAsync();   // both read the same version

        a.Status = "PAID"; a.ModifiedDate = Earlier.AddDays(1);
        await first.SaveChangesAsync();

        b.Status = "OVERDUE"; b.ModifiedDate = Earlier.AddDays(2);
        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        (await Receivables.Auditor(dbName).SalesInvoices.AsNoTracking().SingleAsync()).Status.Should().Be("PAID");
    }

    [Fact]
    public async Task A_stale_write_to_a_payment_fails_instead_of_overwriting()
    {
        var org = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var payment = new CustomerPayment
        {
            UUID = Guid.NewGuid(), OrganizationId = org, PartnerId = Customer, PartnerName = "Acme", PaymentNumber = "CPAY-1",
            PaymentDate = Earlier, Amount = 100m, PaymentMethod = "CASH", CreatedBy = 1, CreatedDate = Earlier
        };
        await Receivables.Seed(Receivables.Db(org, dbName), payment);

        var first  = Receivables.Db(org, dbName);
        var second = Receivables.Db(org, dbName);
        var a = await first.CustomerPayments.SingleAsync();
        var b = await second.CustomerPayments.SingleAsync();

        a.ModifiedBy = 1; a.ModifiedDate = Earlier.AddDays(1);
        await first.SaveChangesAsync();

        b.ModifiedBy = 2; b.ModifiedDate = Earlier.AddDays(2);
        var act = async () => await second.SaveChangesAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task A_save_from_a_current_read_still_succeeds()
    {
        var org = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        await Receivables.Seed(Receivables.Db(org, dbName), Receivables.Invoice(org, Customer, "SINV-1", new DateTime(2026, 9, 1), 1000m));

        for (var day = 1; day <= 3; day++)
        {
            var db = Receivables.Db(org, dbName);
            var row = await db.SalesInvoices.SingleAsync();
            row.Notes = $"edit {day}";
            row.ModifiedDate = Earlier.AddDays(day);
            await db.SaveChangesAsync();
        }

        (await Receivables.Auditor(dbName).SalesInvoices.AsNoTracking().SingleAsync()).Notes.Should().Be("edit 3");
    }

    // ── Every writer bumps it ────────────────────────────────────────────────

    private sealed record W(FinanceDbContext Db, SalesInvoiceService Invoices, CustomerPaymentService Payments, InvoiceOverdueJob Sweep, TestClock Clock, Guid Org, string DbName);

    private static W NewWriter(DateTime now)
    {
        var org = Guid.NewGuid();
        var dbName = Guid.NewGuid().ToString();
        var clock = new TestClock { Value = now };
        var db = Receivables.Db(org, dbName);
        var demand = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = org });
        var ledger = new CustomerLedgerService(db, clock);

        return new W(
            db,
            new SalesInvoiceService(db, demand, Mock.Of<IDeliveryFulfillmentReader>(), ledger, new NoProductLedger(), Receivables.Names().Object,
                Receivables.Lookups().Object, Mock.Of<IBackgroundJobClient>(), NullLogger<SalesInvoiceService>.Instance, clock),
            new CustomerPaymentService(db, ledger, Receivables.Names().Object, Receivables.Lookups().Object, clock),
            new InvoiceOverdueJob(new FinanceDbContext(
                    new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options,
                    new StaticTenantContext { IsSuperAdmin = true }),
                NullLogger<InvoiceOverdueJob>.Instance, clock),
            clock, org, dbName);
    }

    private static async Task<SalesInvoice> SeedInvoice(W w, string status, DateTime invoiceDate, decimal grand = 1000m)
    {
        var invoice = Receivables.Invoice(w.Org, Customer, "SINV-1", invoiceDate, grand, status);
        invoice.ModifiedBy = 1;
        invoice.ModifiedDate = Earlier;   // a prior modification, so "changed" means a new value
        await Receivables.Seed(w.Db, invoice);
        return invoice;
    }

    private static async Task<DateTime?> ModifiedDate(W w) =>
        (await Receivables.Auditor(w.DbName).SalesInvoices.AsNoTracking().SingleAsync()).ModifiedDate;

    [Fact]
    public async Task Issuing_bumps_it()
    {
        var w = NewWriter(Earlier.AddDays(5));
        var invoice = await SeedInvoice(w, "DRAFT", new DateTime(2026, 9, 1));

        await w.Invoices.IssueAsync(invoice.UUID, 5);

        (await ModifiedDate(w)).Should().Be(Earlier.AddDays(5));
    }

    [Fact]
    public async Task Recording_a_payment_against_it_bumps_it()
    {
        var w = NewWriter(Earlier.AddDays(5));
        await SeedInvoice(w, "ISSUED", new DateTime(2026, 9, 1));

        await w.Payments.RecordPaymentAsync(Customer, 400m, "CASH", new CustomerPaymentDetails("PKR"), 5);

        (await ModifiedDate(w)).Should().Be(Earlier.AddDays(5));
    }

    [Fact]
    public async Task Allocating_a_payment_to_it_bumps_it_and_the_payment()
    {
        var w = NewWriter(Earlier.AddDays(5));
        await SeedInvoice(w, "ISSUED", new DateTime(2026, 9, 1));
        var payment = await w.Payments.RecordPaymentAsync(Customer, 400m, "CASH", new CustomerPaymentDetails("PKR", Allocations: []), 5);
        w.Clock.Value = Earlier.AddDays(6);

        await w.Payments.AllocateAsync(payment.PaymentUuid, null, 5);

        (await ModifiedDate(w)).Should().Be(Earlier.AddDays(6));
        (await Receivables.Auditor(w.DbName).CustomerPayments.AsNoTracking().SingleAsync()).ModifiedDate.Should().Be(Earlier.AddDays(6));
    }

    [Fact]
    public async Task Editing_it_bumps_it()
    {
        var w = NewWriter(Earlier.AddDays(5));
        var invoice = await SeedInvoice(w, "DRAFT", new DateTime(2026, 9, 1));

        await w.Invoices.UpdateAsync(invoice.UUID, new UpdateSalesInvoiceRequest { DueDate = new DateTime(2026, 10, 30) }, 5);

        (await ModifiedDate(w)).Should().Be(Earlier.AddDays(5));
    }

    [Fact]
    public async Task Deleting_it_bumps_it()
    {
        var w = NewWriter(Earlier.AddDays(5));
        var invoice = await SeedInvoice(w, "DRAFT", new DateTime(2026, 9, 1));

        await w.Invoices.DeleteAsync(invoice.UUID, 5);

        (await ModifiedDate(w)).Should().Be(Earlier.AddDays(5));
    }

    [Fact]
    public async Task Bouncing_a_cheque_bumps_the_invoice_and_the_payment()
    {
        var w = NewWriter(Earlier.AddDays(5));
        await SeedInvoice(w, "ISSUED", new DateTime(2026, 9, 1));
        var payment = await w.Payments.RecordPaymentAsync(
            Customer, 400m, "CHEQUE", new CustomerPaymentDetails("PKR", ChequeNumber: "CHQ-1"), 5);
        w.Clock.Value = Earlier.AddDays(6);

        await w.Payments.BounceAsync(payment.PaymentUuid, null, 5);

        (await ModifiedDate(w)).Should().Be(Earlier.AddDays(6));
        (await Receivables.Auditor(w.DbName).CustomerPayments.AsNoTracking().SingleAsync()).ModifiedDate.Should().Be(Earlier.AddDays(6));
    }

    [Fact]
    public async Task The_overdue_sweep_bumps_it()
    {
        var w = NewWriter(Earlier.AddDays(60));
        await SeedInvoice(w, "ISSUED", new DateTime(2026, 8, 1));

        (await w.Sweep.SweepAsync()).Should().Be(1);

        (await ModifiedDate(w)).Should().Be(Earlier.AddDays(60));
    }
}
