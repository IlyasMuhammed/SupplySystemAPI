using System.Data.Common;
using FluentAssertions;
using Hangfire;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// The payments and the credit/debit notes that settle or reduce a supplier invoice, on a real SQL Server (LocalDB).
/// The <c>invoices</c> and <c>supplier_payments</c> tables have no concurrency token, so before this every one of
/// them read the invoice (or the payment), decided, and wrote — and two of them at once acted on the same stale
/// row: a payment drafted on an invoice being reversed, two postings each adding 500 to a PaidAmount of 0, a
/// cancellation written over a posting already in the ledger, a credit note deducted from an invoice just reversed.
/// Each now takes the invoice rows' locks first (ascending Id, the one platform-wide order), then the payment's or
/// note's own row, and re-reads what it checks.
/// <para>
/// As in <see cref="InvoiceTransitionConcurrencyTests"/>: every racer is its own set of contexts with the retrying
/// strategy production uses, and a racer is held at a chosen read until the other has finished (or <see cref="Hold"/>
/// passes) — so without the locks the race is certain, and with them the held one simply waits out the hold.
/// </para>
/// </summary>
public class PaymentAndNoteLockSqlServerTests
{
    private const int User = 7;

    /// <summary>How long a held racer waits for the other before it carries on by itself.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(120);

    private enum Outcome { Done, Refused, NotFound }

    // ── (a) A payment drafted while its invoice is being reversed ────────────

    /// <summary>
    /// The draft reads the invoice (Approved) and is held there while the reversal is fired. Before, the reversal
    /// found no payment on the invoice and reversed it, and the draft then saved its line: money scheduled against an
    /// invoice that no longer stands. Now exactly one of them wins.
    /// </summary>
    [FinanceSqlServerFact]
    public async Task A_payment_drafted_while_its_invoice_is_reversed_never_leaves_a_line_on_a_reversed_invoice()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.ApprovedInvoiceAsync(1000m);

        var (draft, reverse) = await HeldThenFiredAsync(
            world, "invoices",
            held:  async r => { await r.Payments.CreateAsync(world.Pay((invoice, 100m)), User); return true; },
            fired: r => r.Invoices.ReverseAsync(invoice, "Entered twice", User));

        new[] { draft, reverse }.Count(o => o == Outcome.Done).Should().Be(1, "either the draft or the reversal, not both");

        var stored = await world.InvoiceAsync(invoice);
        var lines  = await world.LiveLinesAsync(invoice);
        if (InvoiceMatchStatus.Is(stored.MatchStatus, InvoiceMatchStatus.Reversed))
            lines.Should().BeEmpty("a reversed invoice owes nothing, so nothing may be scheduled against it");
        else
            lines.Should().ContainSingle();
        await world.ShouldBeBalancedAsync();
    }

    // ── (b) Two postings on one invoice ──────────────────────────────────────

    /// <summary>
    /// Two approved payments of 500 on one invoice of 1000, posted at once: both read PaidAmount 0 and both wrote 500
    /// (the E2E tester saw exactly this). Now the second waits for the first and adds to what it wrote.
    /// </summary>
    [FinanceSqlServerFact]
    public async Task Two_payments_of_500_posted_at_once_pay_an_invoice_of_1000_in_full()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.ApprovedInvoiceAsync(1000m);
        var first   = await world.ApprovedPaymentAsync((invoice, 500m));
        var second  = await world.ApprovedPaymentAsync((invoice, 500m));

        var meet    = new Rendezvous(2);
        var results = await Race(
            () => world.RunAsync(r => r.Payments.PostAsync(first, User), new MeetAfterReading("invoices", meet)),
            () => world.RunAsync(r => r.Payments.PostAsync(second, User), new MeetAfterReading("invoices", meet)));

        results.Should().Equal(Outcome.Done, Outcome.Done);
        var stored = await world.InvoiceAsync(invoice);
        (stored.PaidAmount, stored.PaymentStatus).Should().Be((1000m, InvoicePaymentStatus.FullyPaid), "500 + 500, neither lost");
        (await world.LedgerOfAsync(first)).Should().ContainSingle(e => e.TransactionType == "PAYMENT_POSTED");
        (await world.LedgerOfAsync(second)).Should().ContainSingle(e => e.TransactionType == "PAYMENT_POSTED");
        await world.ShouldBeBalancedAsync();
    }

    /// <summary>A double-click on Post: both read APPROVED, and both credited the ledger and added to PaidAmount.</summary>
    [FinanceSqlServerFact]
    public async Task One_payment_posted_twice_at_once_is_posted_once()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.ApprovedInvoiceAsync(1000m);
        var payment = await world.ApprovedPaymentAsync((invoice, 100m));

        var meet    = new Rendezvous(2);
        var results = await Race(
            () => world.RunAsync(r => r.Payments.PostAsync(payment, User), new MeetAfterReading("supplier_payments", meet)),
            () => world.RunAsync(r => r.Payments.PostAsync(payment, User), new MeetAfterReading("supplier_payments", meet)));

        results.Should().BeEquivalentTo([Outcome.Done, Outcome.Refused]);
        (await world.LedgerOfAsync(payment)).Should().ContainSingle(e => e.TransactionType == "PAYMENT_POSTED");
        (await world.InvoiceAsync(invoice)).PaidAmount.Should().Be(100m);
        await world.ShouldBeBalancedAsync();
    }

    // ── (c) Posting and cancelling one payment at once ───────────────────────

    /// <summary>
    /// The cancellation reads the payment (APPROVED) and is held while the posting runs to the end — ledger credit,
    /// PaidAmount. Before, the cancellation then wrote CANCELLED over it: a cancelled payment whose money the ledger
    /// says was paid.
    /// </summary>
    [FinanceSqlServerFact]
    public async Task A_posting_fired_while_a_cancellation_is_under_way_never_leaves_a_cancelled_payment_in_the_ledger()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.ApprovedInvoiceAsync(1000m);
        var payment = await world.ApprovedPaymentAsync((invoice, 100m));

        var (cancel, post) = await HeldThenFiredAsync(
            world, "supplier_payments",
            held:  r => r.Payments.CancelAsync(payment, User),
            fired: r => r.Payments.PostAsync(payment, User));

        await ShouldBeOneOrTheOtherAsync(world, invoice, payment, cancel, post);
    }

    /// <summary>The other way round: the posting is held after reading the payment while the cancellation runs.</summary>
    [FinanceSqlServerFact]
    public async Task A_cancellation_fired_while_a_posting_is_under_way_is_never_reported_done_for_a_posted_payment()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.ApprovedInvoiceAsync(1000m);
        var payment = await world.ApprovedPaymentAsync((invoice, 100m));

        var (post, cancel) = await HeldThenFiredAsync(
            world, "supplier_payments",
            held:  r => r.Payments.PostAsync(payment, User),
            fired: r => r.Payments.CancelAsync(payment, User));

        await ShouldBeOneOrTheOtherAsync(world, invoice, payment, cancel, post);
    }

    private static async Task ShouldBeOneOrTheOtherAsync(World world, Guid invoice, Guid payment, Outcome cancel, Outcome post)
    {
        new[] { cancel, post }.Count(o => o == Outcome.Done).Should().Be(1, "a payment is either cancelled or posted, and the loser is told so");

        var status = await world.PaymentStatusAsync(payment);
        var posted = (await world.LedgerOfAsync(payment)).Count(e => e.TransactionType == "PAYMENT_POSTED");
        var paid   = (await world.InvoiceAsync(invoice)).PaidAmount;

        if (cancel == Outcome.Done)
            (status, posted, paid).Should().Be(("CANCELLED", 0, 0m), "a cancelled payment moved no money");
        else
            (status, posted, paid).Should().Be(("POSTED", 1, 100m));
        await world.ShouldBeBalancedAsync();
    }

    // ── (d) A note applied while its invoice is being reversed ───────────────

    /// <summary>
    /// The note's application reads the invoice (Approved) and is held while the reversal is fired. Before, the
    /// reversal found no note deducted and reversed the invoice; the note was then deducted from it anyway.
    /// </summary>
    [FinanceSqlServerFact]
    public async Task A_credit_note_applied_while_its_invoice_is_reversed_is_never_left_on_a_reversed_invoice() =>
        await NoteVersusReversalAsync("credit");

    [FinanceSqlServerFact]
    public async Task A_debit_note_applied_while_its_invoice_is_reversed_is_never_left_on_a_reversed_invoice() =>
        await NoteVersusReversalAsync("debit");

    private static async Task NoteVersusReversalAsync(string kind)
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.ApprovedInvoiceAsync(1000m);
        var note    = await world.CarriedForwardNoteAsync(kind, 100m);

        var (apply, reverse) = await HeldThenFiredAsync(
            world, "invoices",
            held:  r => r.ApplyAsync(kind, note, invoice),
            fired: r => r.Invoices.ReverseAsync(invoice, "Entered twice", User));

        new[] { apply, reverse }.Count(o => o == Outcome.Done).Should().Be(1, "either the note is deducted or the invoice reversed, not both");

        var stored = await world.InvoiceAsync(invoice);
        var (status, appliedTo) = await world.NoteAsync(kind, note);
        if (InvoiceMatchStatus.Is(stored.MatchStatus, InvoiceMatchStatus.Reversed))
            (status, appliedTo, stored.TotalAmount).Should().Be(("CARRIED_FORWARD", (Guid?)null, 1000m),
                "a note deducted from a reversed invoice is credit lost to an invoice that no longer stands");
        else
            (status, appliedTo, stored.TotalAmount).Should().Be(("APPLIED", (Guid?)invoice, 900m));
        await world.ShouldBeBalancedAsync();
    }

    // ── (e) Two multi-invoice payments over the same invoices ────────────────

    /// <summary>
    /// Two payments over the same two invoices, their lines in opposite order (A then B, B then A), posted at once.
    /// Before, both read both invoices and wrote them back: one payment's amounts were lost. Locking in line order
    /// would deadlock them; the locks are taken in ascending invoice Id, so both complete, neither waits forever, and
    /// each invoice's PaidAmount is the sum of both payments' lines.
    /// </summary>
    [FinanceSqlServerFact]
    public async Task Two_payments_over_the_same_two_invoices_in_opposite_order_post_at_once_without_deadlock()
    {
        await using var world = await World.CreateAsync();
        var a = await world.ApprovedInvoiceAsync(1000m);
        var b = await world.ApprovedInvoiceAsync(1000m);
        var first  = await world.ApprovedPaymentAsync((a, 300m), (b, 200m));
        var second = await world.ApprovedPaymentAsync((b, 400m), (a, 100m));

        var deadlocks = new DeadlockCounter();
        var meet      = new Rendezvous(2);
        var results   = await Race(
            () => world.RunAsync(r => r.Payments.PostAsync(first, User), new MeetAfterReading("invoices", meet), deadlocks),
            () => world.RunAsync(r => r.Payments.PostAsync(second, User), new MeetAfterReading("invoices", meet), deadlocks));

        results.Should().Equal(Outcome.Done, Outcome.Done);
        deadlocks.Count.Should().Be(0, "both take the invoice locks in the same (ascending Id) order");
        (await world.InvoiceAsync(a)).PaidAmount.Should().Be(400m, "300 + 100");
        (await world.InvoiceAsync(b)).PaidAmount.Should().Be(600m, "200 + 400");
        await world.ShouldBeBalancedAsync();
    }

    // ── (f) Two notes numbered at once ───────────────────────────────────────

    /// <summary>
    /// Two notes raised at once both counted the same notes and took the same "count + 1" number; the second then
    /// failed on the unique (organization, number) index — five times over, in the ledger's retry loop — and the
    /// request failed. The numbering is now one at a time per organization.
    /// </summary>
    [FinanceSqlServerFact]
    public async Task Two_credit_notes_raised_at_once_get_two_numbers() => await TwoNotesAtOnceAsync("credit");

    [FinanceSqlServerFact]
    public async Task Two_debit_notes_raised_at_once_get_two_numbers() => await TwoNotesAtOnceAsync("debit");

    private static async Task TwoNotesAtOnceAsync(string kind)
    {
        await using var world = await World.CreateAsync();
        var first  = await world.ReturnAsync();
        var second = await world.ReturnAsync();
        var table  = kind == "credit" ? "credit_notes" : "debit_notes";

        var meet    = new Rendezvous(2);
        var results = await Race(
            () => world.RunAsync(r => r.RaiseAsync(kind, first), new MeetAfterReading(table, meet)),
            () => world.RunAsync(r => r.RaiseAsync(kind, second), new MeetAfterReading(table, meet)));

        results.Should().Equal(Outcome.Done, Outcome.Done);
        var year = DateTime.UtcNow.Year;
        var prefix = kind == "credit" ? "CN" : "DN";
        (await world.NoteNumbersAsync(kind)).Should().BeEquivalentTo([$"{prefix}-{year}-00001", $"{prefix}-{year}-00002"]);
        await world.ShouldBeBalancedAsync();
    }

    /// <summary>Two supplier payments drafted at once take two SPAY numbers.</summary>
    [FinanceSqlServerFact]
    public async Task Two_payments_drafted_at_once_get_two_numbers()
    {
        await using var world = await World.CreateAsync();
        // Two invoices, so no invoice lock puts the two drafts one after the other: only the numbering can.
        var a = await world.ApprovedInvoiceAsync(1000m);
        var b = await world.ApprovedInvoiceAsync(1000m);

        var meet    = new Rendezvous(2);
        var results = await Race(
            () => world.RunAsync(async r => { await r.Payments.CreateAsync(world.Pay((a, 100m)), User); return true; }, new MeetAfterReading("supplier_payments", meet)),
            () => world.RunAsync(async r => { await r.Payments.CreateAsync(world.Pay((b, 200m)), User); return true; }, new MeetAfterReading("supplier_payments", meet)));

        results.Should().Equal(Outcome.Done, Outcome.Done);
        var year = DateTime.UtcNow.Year;
        (await world.PaymentNumbersAsync()).Should().BeEquivalentTo([$"SPAY-{year}-00001", $"SPAY-{year}-00002"]);
    }

    // ── (g) One return resolved twice at once ────────────────────────────────

    /// <summary>
    /// A credit note and a debit note raised at once from the same supplier return: both read it as
    /// SUPPLIER_RECEIVED, so both were raised — the supplier credited twice for one return. The return's row is now
    /// locked first, and the second sees it resolved.
    /// </summary>
    [FinanceSqlServerFact]
    public async Task One_return_resolved_by_a_credit_and_a_debit_note_at_once_gets_one_note()
    {
        await using var world = await World.CreateAsync();
        var sro = await world.ReturnAsync();

        var meet    = new Rendezvous(2);
        var results = await Race(
            () => world.RunWithWarehouseAsync(r => r.RaiseAsync("credit", sro), new MeetAfterReading("supplier_return_orders", meet, "warehouse")),
            () => world.RunWithWarehouseAsync(r => r.RaiseAsync("debit", sro), new MeetAfterReading("supplier_return_orders", meet, "warehouse")));

        results.Should().BeEquivalentTo([Outcome.Done, Outcome.Refused]);
        var notes = (await world.NoteNumbersAsync("credit")).Count + (await world.NoteNumbersAsync("debit")).Count;
        notes.Should().Be(1, "one return, one note");
        (await world.ReturnStatusAsync(sro)).Should().BeOneOf("RESOLVED_CREDIT", "RESOLVED_DEBIT");
        (await world.LedgerTotalAsync()).Should().Be(-100m, "the supplier is credited for the return once");
        await world.ShouldBeBalancedAsync();
    }

    // ── Running racers ───────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="held"/> until it has read <paramref name="table"/>, holds it there, fires
    /// <paramref name="fired"/>, and lets the held one go on once the fired one has finished — or after
    /// <see cref="Hold"/>, when the fired one is (rightly) waiting for a lock the held one has.
    /// </summary>
    private static async Task<(Outcome Held, Outcome Fired)> HeldThenFiredAsync(
        World world, string table, Func<Repos, Task<bool>> held, Func<Repos, Task<bool>> fired)
    {
        var firedDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause     = new PauseAfterReading(table, firedDone.Task);

        var first = Task.Run(() => world.RunAsync(held, pause));
        await pause.Reached.Task.WaitAsync(Patience);

        var second = Task.Run(() => world.RunAsync(fired));
        _ = second.ContinueWith(_ => firedDone.TrySetResult(), TaskScheduler.Default);

        var results = await Task.WhenAll(first, second).WaitAsync(Patience);
        return (results[0], results[1]);
    }

    private static async Task<Outcome[]> Race(params Func<Task<Outcome>>[] racers) =>
        await Task.WhenAll(racers.Select(r => Task.Run(r))).WaitAsync(Patience);

    /// <summary>One request's repositories, over its own contexts.</summary>
    private sealed class Repos(InvoiceRepository invoices, SupplierPaymentRepository payments, CreditNoteRepository credits, DebitNoteRepository debits)
    {
        public InvoiceRepository         Invoices { get; } = invoices;
        public SupplierPaymentRepository Payments { get; } = payments;

        public async Task<bool> ApplyAsync(string kind, Guid note, Guid invoice)
        {
            if (kind == "credit") await credits.ApplyCarriedForwardAsync(note, new ApplyCreditNoteRequest { InvoiceUuid = invoice }, User);
            else                  await debits.ApplyCarriedForwardAsync(note, new ApplyDebitNoteRequest { InvoiceUuid = invoice }, User);
            return true;
        }

        /// <summary>A note of 100 for the return, naming no invoice.</summary>
        public async Task<bool> RaiseAsync(string kind, Guid sro)
        {
            if (kind == "credit")
                await credits.CreateAsync(new CreateCreditNoteRequest
                {
                    SroId = sro, SupplierCreditNoteNo = "S-CN-1", CreditDate = new DateTime(2026, 9, 25), CreditAmount = 100m
                }, User);
            else
                await debits.CreateAsync(new CreateDebitNoteRequest { SroId = sro, DebitReason = "DAMAGED_GOODS", DebitAmount = 100m }, User);
            return true;
        }
    }

    // ── The world: one throwaway database, one organization, one supplier ────

    private sealed class World : IAsyncDisposable
    {
        private readonly FinanceSqlServerHarness _harness;
        private int _invoices;

        private World(FinanceSqlServerHarness harness) => _harness = harness;

        public Guid Org      { get; } = Guid.NewGuid();
        public Guid Supplier { get; } = Guid.NewGuid();

        public static async Task<World> CreateAsync()
        {
            var world = new World(await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true));

            // Warehouse's tables too, for the supplier returns a note is raised against.
            await using var warehouse = world.NewWarehouse(world.Org);
            await warehouse.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
            return world;
        }

        private int _returns;

        /// <summary>A supplier return received by the supplier (10 at 100), ready for a credit or debit note.</summary>
        public async Task<Guid> ReturnAsync()
        {
            await using var warehouse = NewWarehouse(Org);
            var sro = new SupplierReturnOrder
            {
                UUID = Guid.NewGuid(), ReturnNumber = $"SRO-2026-{++_returns:D5}", SroType = "POST_RECEIPT_DEFECT",
                SupplierId = Supplier, SupplierName = "Karachi Steel", ReturnReason = "DAMAGED", Status = "SUPPLIER_RECEIVED",
                IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow,
                Lines = [new SupplierReturnOrderLine { UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "Steel bar", QtyToReturn = 10m, UnitCost = 100m }]
            };
            warehouse.SupplierReturnOrders.Add(sro);
            await warehouse.SaveChangesAsync();
            return sro.UUID;
        }

        /// <summary>A freight-style invoice (no purchase order) of <paramref name="total"/>, created and approved through the repository.</summary>
        public async Task<Guid> ApprovedInvoiceAsync(decimal total)
        {
            var uuid = Guid.Empty;
            (await RunAsync(async r =>
            {
                uuid = await r.Invoices.CreateAsync(new CreateInvoiceRequest
                {
                    SupplierId = Supplier, SupplierInvoiceNo = $"KSW/{++_invoices}",
                    InvoiceDate = new DateTime(2026, 9, 15), ReceivedDate = new DateTime(2026, 9, 16), DueDate = new DateTime(2026, 10, 15),
                    Currency = "PKR", Subtotal = total
                }, User);
                return true;
            })).Should().Be(Outcome.Done);
            (await RunAsync(r => r.Invoices.ApproveAsync(uuid, null, User))).Should().Be(Outcome.Done);
            return uuid;
        }

        public CreateSupplierPaymentRequest Pay(params (Guid Invoice, decimal Amount)[] lines) => new()
        {
            SupplierId = Supplier, SupplierName = "Karachi Steel", PaymentDate = new DateTime(2026, 9, 20), PaymentMethod = "CASH",
            TotalAmount = lines.Sum(l => l.Amount),
            Lines = [.. lines.Select(l => new CreateSupplierPaymentLineRequest { InvoiceUuid = l.Invoice, AllocatedAmount = l.Amount })]
        };

        /// <summary>A payment over these lines, in this order, drafted and approved.</summary>
        public async Task<Guid> ApprovedPaymentAsync(params (Guid Invoice, decimal Amount)[] lines)
        {
            var uuid = Guid.Empty;
            (await RunAsync(async r => { uuid = await r.Payments.CreateAsync(Pay(lines), User); return true; })).Should().Be(Outcome.Done);
            (await RunAsync(r => r.Payments.ApproveAsync(uuid, User))).Should().Be(Outcome.Done);
            return uuid;
        }

        public async Task<Guid> CarriedForwardNoteAsync(string kind, decimal amount)
        {
            await using var db = NewFinance(Org);
            var uuid = Guid.NewGuid();
            if (kind == "credit")
                db.CreditNotes.Add(new CreditNote
                {
                    UUID = uuid, CreditNoteNumber = "CN-2026-00001", SupplierCreditNoteNo = "S-CN-1", SroUuid = Guid.NewGuid(), SroNumber = "SRO-1",
                    SupplierId = Supplier, SupplierName = "Karachi Steel", CreditDate = new DateTime(2026, 9, 20), CreditAmount = amount,
                    ApplicationStatus = "CARRIED_FORWARD", CarriedForwardAmount = amount, IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
                });
            else
                db.DebitNotes.Add(new DebitNote
                {
                    UUID = uuid, DebitNoteNumber = "DN-2026-00001", SroUuid = Guid.NewGuid(), SroNumber = "SRO-2", SupplierId = Supplier,
                    SupplierName = "Karachi Steel", DebitReason = "SHORT_SUPPLY", DebitAmount = amount, ApplicationStatus = "CARRIED_FORWARD",
                    CarriedForwardAmount = amount, Status = "ISSUED", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
            return uuid;
        }

        /// <summary>One request: its own contexts, its own repositories, the action, and how it ended.</summary>
        public Task<Outcome> RunAsync(Func<Repos, Task<bool>> action, params IInterceptor[] financeInterceptors) =>
            RunCoreAsync(action, financeInterceptors, null);

        /// <summary>As <see cref="RunAsync"/>, with <paramref name="warehouseInterceptor"/> on Warehouse's context.</summary>
        public Task<Outcome> RunWithWarehouseAsync(Func<Repos, Task<bool>> action, IInterceptor warehouseInterceptor) =>
            RunCoreAsync(action, [], warehouseInterceptor);

        private async Task<Outcome> RunCoreAsync(Func<Repos, Task<bool>> action, IInterceptor[] financeInterceptors, IInterceptor? warehouseInterceptor)
        {
            await using var finance   = NewFinance(Org, financeInterceptors);
            await using var demand    = NewDemand(Org);
            await using var warehouse = NewWarehouse(Org, warehouseInterceptor);
            var ledger = new SupplierLedgerService(finance);
            var repos  = new Repos(
                new InvoiceRepository(finance, demand, warehouse, ledger, new FakeSupplierNameLookup()),
                new SupplierPaymentRepository(finance, ledger, Mock.Of<INotificationService>()),
                new CreditNoteRepository(finance, warehouse, ledger),
                new DebitNoteRepository(finance, warehouse, Mock.Of<IBackgroundJobClient>(), Mock.Of<INotificationService>(),
                    Mock.Of<IAuditService>(), ledger));

            try
            {
                return await action(repos) ? Outcome.Done : Outcome.NotFound;
            }
            catch (Exception ex) when (ex is ConflictException or BadRequestException or UnprocessableEntityException)
            {
                return Outcome.Refused;
            }
            catch (NotFoundException)
            {
                return Outcome.NotFound;
            }
        }

        /// <summary>
        /// The retrying strategy production uses, and a command timeout well past the default 30 seconds: a racer held
        /// on purpose keeps its locks while the other waits for them inside SQL Server, and in a full parallel run the
        /// held racer's continuation can be starved of a thread for long enough to time the waiter out (SqlException -2).
        /// </summary>
        private static void Retrying(SqlServerDbContextOptionsBuilder sql) =>
            sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null).CommandTimeout(180);

        private FinanceDbContext NewFinance(Guid org, params IInterceptor[] interceptors)
        {
            var options = new DbContextOptionsBuilder<FinanceDbContext>().UseSqlServer(_harness.ConnectionString, Retrying);
            if (interceptors.Length > 0) options.AddInterceptors(interceptors);
            return new FinanceDbContext(options.Options, new StaticTenantContext { OrganizationId = org });
        }

        private DemandDbContext NewDemand(Guid org) =>
            new(new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(_harness.ConnectionString, Retrying).Options,
                new StaticTenantContext { OrganizationId = org });

        private WarehouseDbContext NewWarehouse(Guid org, IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(_harness.ConnectionString, Retrying);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new WarehouseDbContext(options.Options, new StaticTenantContext { OrganizationId = org });
        }

        public async Task<List<string>> NoteNumbersAsync(string kind)
        {
            await using var db = NewFinance(Org);
            return kind == "credit"
                ? await db.CreditNotes.AsNoTracking().Select(c => c.CreditNoteNumber).ToListAsync()
                : await db.DebitNotes.AsNoTracking().Select(d => d.DebitNoteNumber).ToListAsync();
        }

        public async Task<List<string>> PaymentNumbersAsync()
        {
            await using var db = NewFinance(Org);
            return await db.SupplierPayments.AsNoTracking().Select(p => p.PaymentNumber).ToListAsync();
        }

        public async Task<string> ReturnStatusAsync(Guid sro)
        {
            await using var db = NewWarehouse(Org);
            return await db.SupplierReturnOrders.AsNoTracking().Where(s => s.UUID == sro).Select(s => s.Status).SingleAsync();
        }

        /// <summary>The supplier ledger's balance: debits less credits.</summary>
        public async Task<decimal> LedgerTotalAsync()
        {
            await using var db = NewFinance(Org);
            return (await db.SupplierLedgerEntries.AsNoTracking().ToListAsync()).Sum(e => e.DebitAmount - e.CreditAmount);
        }

        // ── What is stored ──────────────────────────────────────────────────

        public async Task<Invoice> InvoiceAsync(Guid uuid)
        {
            await using var db = NewFinance(Org);
            return await db.Invoices.AsNoTracking().SingleAsync(i => i.UUID == uuid);
        }

        public async Task<string> PaymentStatusAsync(Guid uuid)
        {
            await using var db = NewFinance(Org);
            return await db.SupplierPayments.AsNoTracking().Where(p => p.UUID == uuid).Select(p => p.Status).SingleAsync();
        }

        /// <summary>The lines on the invoice of payments that are not cancelled or bounced.</summary>
        public async Task<List<SupplierPaymentLine>> LiveLinesAsync(Guid invoice)
        {
            await using var db = NewFinance(Org);
            return await db.SupplierPaymentLines.AsNoTracking()
                .Where(l => l.InvoiceUuid == invoice && l.SupplierPayment.Status != "CANCELLED" && l.SupplierPayment.Status != "BOUNCED")
                .ToListAsync();
        }

        public async Task<List<SupplierLedgerEntry>> LedgerOfAsync(Guid reference)
        {
            await using var db = NewFinance(Org);
            return await db.SupplierLedgerEntries.AsNoTracking().Where(e => e.ReferenceId == reference).OrderBy(e => e.SequenceNo).ToListAsync();
        }

        public async Task<(string Status, Guid? AppliedTo)> NoteAsync(string kind, Guid uuid)
        {
            await using var db = NewFinance(Org);
            if (kind == "credit")
            {
                var c = await db.CreditNotes.AsNoTracking().SingleAsync(x => x.UUID == uuid);
                return (c.ApplicationStatus, c.AppliedToInvoiceUuid);
            }

            var d = await db.DebitNotes.AsNoTracking().SingleAsync(x => x.UUID == uuid);
            return (d.ApplicationStatus, d.AppliedToInvoiceUuid);
        }

        /// <summary>
        /// The supplier ledger and the master ledger tell the same story: every supplier entry has its master twin,
        /// each chain's running balance is the sum of what came before, and the master's last balance is the sum of
        /// every supplier's.
        /// </summary>
        public async Task ShouldBeBalancedAsync()
        {
            await using var db = NewFinance(Org);
            var supplierEntries = await db.SupplierLedgerEntries.AsNoTracking().ToListAsync();
            var masterEntries   = await db.MasterFinancialLedgers.AsNoTracking().OrderBy(e => e.SequenceNo).ToListAsync();

            masterEntries.Should().HaveCount(supplierEntries.Count, "every supplier ledger entry is mirrored once");
            masterEntries.Select(e => e.SequenceNo).Should().Equal(Enumerable.Range(1, masterEntries.Count));

            var running = 0m;
            foreach (var entry in masterEntries)
            {
                running += entry.DebitAmount - entry.CreditAmount;
                entry.BalanceAfter.Should().Be(running);
            }

            foreach (var chain in supplierEntries.GroupBy(e => e.SupplierId))
            {
                var balance = 0m;
                foreach (var entry in chain.OrderBy(e => e.SequenceNo))
                {
                    balance += entry.DebitAmount - entry.CreditAmount;
                    entry.BalanceAfter.Should().Be(balance);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            SqlConnection.ClearAllPools();
            await _harness.DisposeAsync();
        }
    }

    // ── Holding a racer at a chosen point ─────────────────────────────────────

    private sealed class Rendezvous(int parties)
    {
        private int _arrived;
        private readonly TaskCompletionSource _everyone = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= parties) _everyone.TrySetResult();
            await Task.WhenAny(_everyone.Task, Task.Delay(Hold));
        }
    }

    /// <summary>Pauses this context once it has finished its first read of <c>[schema].[table]</c> (its reader is closing).</summary>
    private abstract class AfterReading(string table, string schema) : DbCommandInterceptor
    {
        private int _paused;

        protected abstract Task PauseAsync();

        public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            var sql = command.CommandText;
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains($"FROM [{schema}].[{table}]", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _paused, 1) == 0)
                await PauseAsync();

            return result;
        }
    }

    /// <summary>Holds this context after its first read of the table until every racer has read it too (or <see cref="Hold"/> passes).</summary>
    private sealed class MeetAfterReading(string table, Rendezvous rendezvous, string schema = "finance") : AfterReading(table, schema)
    {
        protected override Task PauseAsync() => rendezvous.ArriveAsync();
    }

    /// <summary>Holds this context after its first read of the table until <paramref name="release"/> completes (or <see cref="Hold"/> passes).</summary>
    private sealed class PauseAfterReading(string table, Task release) : AfterReading(table, "finance")
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task PauseAsync()
        {
            Reached.TrySetResult();
            await Task.WhenAny(release, Task.Delay(Hold));
        }
    }

    /// <summary>Counts the commands SQL Server chose as a deadlock victim (error 1205) — which the retrying strategy would otherwise hide.</summary>
    private sealed class DeadlockCounter : DbCommandInterceptor
    {
        private int _count;
        public int Count => _count;

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is SqlException { Number: 1205 }) Interlocked.Increment(ref _count);
            return Task.CompletedTask;
        }

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
            if (eventData.Exception is SqlException { Number: 1205 }) Interlocked.Increment(ref _count);
        }
    }
}
