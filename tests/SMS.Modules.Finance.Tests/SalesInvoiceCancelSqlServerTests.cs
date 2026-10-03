using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Tests.QuickBooks;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// SAP alignment (S-7) — cancelling an issued sales invoice on a real SQL Server, where two requests really do
/// run at once: a double click on Cancel, and a cancellation racing a payment for the same invoice. The
/// in-memory tests simulate the lost race; these let SQL Server's own locks, the unique ledger sequences and
/// the invoice's ModifiedDate concurrency token decide it, under the retrying execution strategy production uses.
/// </summary>
public class SalesInvoiceCancelSqlServerTests
{
    private const int User = 42;

    /// <summary>Holds every save that changes a sales invoice until two have arrived, so both were decided from the same read.</summary>
    private sealed class BothAtTheDoor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            var touchesInvoice = eventData.Context!.ChangeTracker.Entries<SalesInvoice>().Any(e => e.State == EntityState.Modified);
            if (touchesInvoice)
            {
                var n = Interlocked.Increment(ref _arrived);
                if (n == 2) _both.TrySetResult();
                // On a loaded machine the other request may be slow to get here. Waiting is only what makes the
                // race likely; the assertions hold whichever way it goes, so a late partner is not a failure.
                if (n <= 2) await Task.WhenAny(_both.Task, Task.Delay(TimeSpan.FromSeconds(60), ct));
            }
            return result;
        }
    }

    private sealed record Rig(FinanceSqlServerHarness H, Guid Org, Guid Customer, FakeVariants Variants, TestClock Clock,
        RecordingQuickBooksGateway Gateway, Guid A, Guid B, Guid Invoice);

    private static SalesInvoiceService Invoices(Rig r, FinanceDbContext fin, SMS.Modules.Demand.Data.DemandDbContext demand) =>
        new(fin, demand, Mock.Of<IDeliveryFulfillmentReader>(), new CustomerLedgerService(fin, r.Clock),
            new ProductLedgerService(fin, r.Variants, r.Clock), Receivables.Names().Object, Receivables.Lookups().Object,
            Mock.Of<IBackgroundJobClient>(), NullLogger<SalesInvoiceService>.Instance, r.Clock,
            new SalesInvoiceQuickBooksPublisher(
                new SalesInvoiceQuickBooksSource(fin, r.Gateway, NullLogger<SalesInvoiceQuickBooksSource>.Instance),
                NullLogger<SalesInvoiceQuickBooksPublisher>.Instance));

    /// <summary>10 of A at 25 and 3 of B at 7 on the ledger; an invoice selling all of them, issued by the real service.</summary>
    private static async Task<Rig> IssuedAsync(FinanceSqlServerHarness h)
    {
        var org      = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var variants = new FakeVariants();
        var (a, _)   = variants.New();
        var (b, _)   = variants.New();
        var clock    = new TestClock();

        await using (var seed = h.NewContext(org))
        {
            var ledger = new ProductLedgerService(seed, variants, clock);
            await ledger.AppendEntryAsync(new ProductLedgerPosting(a, "PURCHASE", "IN", 10m, 25m, "GRN", Guid.NewGuid(), "GRN-1", User));
            await ledger.AppendEntryAsync(new ProductLedgerPosting(b, "PURCHASE", "IN", 3m, 7m, "GRN", Guid.NewGuid(), "GRN-2", User));
        }

        var invoice = Receivables.Invoice(org, customer, "SINV-20260920-0001", new DateTime(2026, 9, 20), 446.50m, status: "DRAFT");
        invoice.Lines.Add(new SalesInvoiceLine
        {
            UUID = Guid.NewGuid(), OrganizationId = org, LineNo = 1, SoLineUuid = Guid.NewGuid(), VariantUuid = a,
            Description = "A", Quantity = 10m, UnitPrice = 40m, LineTotal = 400m
        });
        invoice.Lines.Add(new SalesInvoiceLine
        {
            UUID = Guid.NewGuid(), OrganizationId = org, LineNo = 2, SoLineUuid = Guid.NewGuid(), VariantUuid = b,
            Description = "B", Quantity = 3m, UnitPrice = 15.5m, LineTotal = 46.50m
        });
        await using (var seed = h.NewContext(org))
            await Receivables.Seed(seed, invoice);

        var rig = new Rig(h, org, customer, variants, clock, new RecordingQuickBooksGateway(), a, b, invoice.UUID);
        await using (var fin = h.NewContext(org))
        await using (var demand = h.NewDemandContext(org))
            await Invoices(rig, fin, demand).IssueAsync(invoice.UUID, User);

        rig.Clock.Value = TestClock.Start.AddDays(1);
        return rig;
    }

    private static async Task<Exception?> CancelAsync(Rig r, IInterceptor door)
    {
        try
        {
            await using var fin    = r.H.NewContext(r.Org, door);
            await using var demand = r.H.NewDemandContext(r.Org);
            await Invoices(r, fin, demand).CancelAsync(r.Invoice, "Billed in error", User);
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    private static async Task<Exception?> PayAsync(Rig r, IInterceptor door, decimal amount)
    {
        try
        {
            await using var fin = r.H.NewContext(r.Org, door);
            await new CustomerPaymentService(fin, new CustomerLedgerService(fin, r.Clock), Receivables.Names().Object,
                    Receivables.Lookups().Object, r.Clock)
                .RecordPaymentAsync(r.Customer, amount, "CASH", new CustomerPaymentDetails("PKR"), User);
            return null;
        }
        catch (Exception ex) { return ex; }
    }

    [FinanceSqlServerFact]
    public async Task Two_cancellations_of_one_invoice_at_the_same_moment_reverse_it_exactly_once()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var r    = await IssuedAsync(h);
        var door = new BothAtTheDoor();

        var outcomes = await Task.WhenAll(Task.Run(() => CancelAsync(r, door)), Task.Run(() => CancelAsync(r, door)));

        outcomes.Count(o => o is null).Should().Be(1, "one request cancels the invoice");
        outcomes.Single(o => o is not null).Should().BeOfType<ConflictException>()
            .Which.Message.Should().Contain("already CANCELLED", "the other finds it done rather than doing it again");

        await using var db = h.NewContext(r.Org);
        (await db.SalesInvoices.SingleAsync()).Should().Match<SalesInvoice>(i => i.Status == "CANCELLED" && i.BalanceDue == 0m);
        (await db.CustomerLedgerEntries.OrderBy(e => e.SequenceNo).Select(e => new { e.EntryType, e.RunningBalance }).ToListAsync())
            .Select(e => (e.EntryType, e.RunningBalance)).Should().Equal(("INVOICE", 446.50m), ("CREDIT_NOTE", 0m));

        foreach (var (variant, value) in new[] { (r.A, 250m), (r.B, 21m) })
        {
            var entries = await db.ProductLedgerEntries.Where(e => e.VariantUuid == variant).OrderBy(e => e.SequenceNo).ToListAsync();
            entries.Select(e => e.EntryType).Should().Equal("PURCHASE", "SALE", "RETURN_IN");
            entries[^1].RunningValue.Should().Be(value, "the stock is back at what it was worth, once");
            ProductLedgerInvariants.Violations(entries).Should().BeEmpty();
        }

        r.Gateway.Voids.Should().ContainSingle("QuickBooks voids its copy once");
    }

    [FinanceSqlServerFact]
    public async Task A_cancellation_and_a_payment_racing_for_one_invoice_never_both_stand()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true);
        var r    = await IssuedAsync(h);
        var door = new BothAtTheDoor();

        var outcomes = await Task.WhenAll(Task.Run(() => CancelAsync(r, door)), Task.Run(() => PayAsync(r, door, 100m)));
        var (cancel, pay) = (outcomes[0], outcomes[1]);

        pay.Should().BeNull("a payment is never refused: what it cannot apply is held on account");

        await using var db = h.NewContext(r.Org);
        var invoice     = await db.SalesInvoices.SingleAsync();
        var allocations = await db.PaymentAllocations.CountAsync();
        var ledger      = await db.CustomerLedgerEntries.OrderBy(e => e.SequenceNo).Select(e => e.EntryType).ToListAsync();

        if (cancel is null)
        {
            // The cancellation won: the payment re-read, found nothing payable and went on account.
            invoice.Should().Match<SalesInvoice>(i => i.Status == "CANCELLED" && i.AmountPaid == 0m && i.BalanceDue == 0m);
            allocations.Should().Be(0);
            ledger.Should().Equal("INVOICE", "CREDIT_NOTE", "PAYMENT");
            r.Gateway.Voids.Should().ContainSingle();
        }
        else
        {
            // The payment won: the cancellation re-read and refused an invoice with money paid against it.
            cancel.Should().BeOfType<ConflictException>().Which.Message.Should().Contain("PARTIALLY_PAID");
            invoice.Should().Match<SalesInvoice>(i => i.Status == "PARTIALLY_PAID" && i.AmountPaid == 100m && i.BalanceDue == 346.50m);
            allocations.Should().Be(1);
            ledger.Should().Equal("INVOICE", "PAYMENT");
            (await db.ProductLedgerEntries.CountAsync(e => e.EntryType == "RETURN_IN")).Should().Be(0);
            r.Gateway.Voids.Should().BeEmpty();
        }
    }
}
