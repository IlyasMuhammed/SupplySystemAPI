using System.Data.Common;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.SupplierInvoices;

/// <summary>
/// Hardening of S-7 on a real SQL Server (LocalDB): the <c>invoices</c> table has no concurrency token, so before
/// this two requests could both read "Pending" and both approve — two INVOICE_APPROVED debits, the purchase order
/// invoiced twice — or a rejection could overwrite an approval that had already been booked. Each transition now
/// takes the invoice row's lock first, inside one transaction with its ledger entry and the purchase order's
/// quantities. Every racer here is its own set of contexts — its own request — and every context uses the
/// retrying execution strategy production registers.
/// <para>
/// The races are made certain rather than likely: a racer is held just after it has read the invoice until the
/// other has read it too, or has finished (or <see cref="Hold"/> passes). Without the lock both act on the same
/// status; with it the second one cannot even read until the first has committed, so the hold simply times out.
/// </para>
/// </summary>
public class InvoiceTransitionConcurrencyTests
{
    private const int User = 7;

    /// <summary>How long a held racer waits for the other before it carries on by itself.</summary>
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private enum Outcome { Done, Conflict, NotFound }

    // ── The races ────────────────────────────────────────────────────────────

    [FinanceSqlServerFact]
    public async Task Two_approvals_at_once_book_the_invoice_once_and_invoice_the_PO_once()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.PendingInvoiceAsync();

        var meet    = new Rendezvous(2);
        var results = await Race(
            () => world.RunAsync(r => r.ApproveAsync(invoice, null, User), new MeetAfterReadingTheInvoice(meet)),
            () => world.RunAsync(r => r.ApproveAsync(invoice, null, User), new MeetAfterReadingTheInvoice(meet)));

        results.Should().BeEquivalentTo([Outcome.Done, Outcome.Conflict]);
        (await world.LedgerAsync(invoice)).Should().ContainSingle(e => e.TransactionType == InvoiceRepository.ApprovedLedgerType)
            .Which.DebitAmount.Should().Be(1000m);
        (await world.MasterAsync(invoice)).Should().ContainSingle();
        (await world.PoLineAsync()).QtyInvoiced.Should().Be(10m, "the PO is invoiced once, not twice");
        await world.ShouldBeBalancedAsync();
    }

    [FinanceSqlServerFact]
    public async Task Two_reversals_at_once_credit_the_supplier_once()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.PendingInvoiceAsync();
        (await world.RunAsync(r => r.ApproveAsync(invoice, null, User))).Should().Be(Outcome.Done);

        var meet    = new Rendezvous(2);
        var results = await Race(
            () => world.RunAsync(r => r.ReverseAsync(invoice, "Entered twice", User), new MeetAfterReadingTheInvoice(meet)),
            () => world.RunAsync(r => r.ReverseAsync(invoice, "Entered twice", User), new MeetAfterReadingTheInvoice(meet)));

        results.Should().BeEquivalentTo([Outcome.Done, Outcome.Conflict]);
        var ledger = await world.LedgerAsync(invoice);
        ledger.Where(e => e.TransactionType == InvoiceRepository.ReversedLedgerType).Should().ContainSingle()
            .Which.CreditAmount.Should().Be(1000m);
        ledger.Sum(e => e.DebitAmount - e.CreditAmount).Should().Be(0m, "the reversal undoes the approval exactly once");
        (await world.MasterAsync(invoice)).Should().HaveCount(2);
        (await world.PoLineAsync()).QtyInvoiced.Should().Be(0m);
        await world.ShouldBeBalancedAsync();
    }

    /// <summary>
    /// The approval is held just before it writes the purchase order's quantities while a reversal is fired at the
    /// same invoice. Before, the approval had already committed its ledger debit by then, so the reversal ran to the
    /// end — and the approval's late PO write then put back the quantities the reversal had taken off. Now the PO
    /// write is inside the approval's transaction, so the reversal waits for all of it and undoes all of it.
    /// </summary>
    [FinanceSqlServerFact]
    public async Task A_reversal_fired_while_the_approval_is_still_writing_undoes_all_of_it()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.PendingInvoiceAsync();

        var reversalDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold         = new HoldThePoWrite(reversalDone.Task);

        var approve = Task.Run(() => world.RunAsync(r => r.ApproveAsync(invoice, null, User), demandInterceptor: hold));
        await hold.Reached.Task.WaitAsync(Patience);

        var reverse = Task.Run(() => world.RunAsync(r => r.ReverseAsync(invoice, "Entered twice", User)));
        _ = reverse.ContinueWith(_ => reversalDone.TrySetResult(), TaskScheduler.Default);

        var results = await Task.WhenAll(approve, reverse).WaitAsync(Patience);

        results.Should().Equal(Outcome.Done, Outcome.Done);
        (await world.InvoiceAsync(invoice)).MatchStatus.Should().Be(InvoiceMatchStatus.Reversed);
        (await world.LedgerAsync(invoice)).Sum(e => e.DebitAmount - e.CreditAmount).Should().Be(0m);
        (await world.PoLineAsync()).QtyInvoiced.Should().Be(0m, "a reversed invoice leaves nothing invoiced on its PO");
        (await world.PoAsync()).Status.Should().Be("RECEIVED");
        await world.ShouldBeBalancedAsync();
    }

    /// <summary>
    /// The rejection reads the invoice first and is held there while an approval is fired at it. Before, the approval
    /// ran to the end — debit booked — and the rejection then wrote "Rejected" over it: a rejected invoice that the
    /// supplier ledger says is owed, and that can no longer be reversed. Now the approval waits for the rejection and
    /// is refused.
    /// </summary>
    [FinanceSqlServerFact]
    public async Task An_approval_fired_while_a_rejection_is_under_way_never_leaves_a_rejected_invoice_booked()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.PendingInvoiceAsync();

        var (reject, approve) = await HeldThenFiredAsync(
            world,
            held:  r => r.RejectAsync(invoice, "Wrong prices", User),
            fired: r => r.ApproveAsync(invoice, null, User));

        (reject, approve).Should().Be((Outcome.Done, Outcome.Conflict));
        (await world.InvoiceAsync(invoice)).MatchStatus.Should().Be(InvoiceMatchStatus.Rejected);
        (await world.LedgerAsync(invoice)).Should().BeEmpty("a rejected invoice was never booked");
        (await world.PoLineAsync()).QtyInvoiced.Should().Be(0m);
        await world.ShouldBeBalancedAsync();
    }

    /// <summary>
    /// As above with an edit of the match status: before, the approval was booked and the edit then turned the
    /// invoice back to "Variance" — approvable again, so the next approval would have debited it twice.
    /// </summary>
    [FinanceSqlServerFact]
    public async Task An_approval_fired_while_an_edit_of_the_match_status_is_under_way_is_not_undone_by_it()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.PendingInvoiceAsync();

        var (edit, approve) = await HeldThenFiredAsync(
            world,
            held:  r => r.PatchAsync(invoice, new PatchInvoiceRequest { MatchStatus = "Variance" }, User),
            fired: r => r.ApproveAsync(invoice, null, User));

        (edit, approve).Should().Be((Outcome.Done, Outcome.Done), "the edit happens first, then the approval");
        (await world.InvoiceAsync(invoice)).MatchStatus.Should().Be(InvoiceMatchStatus.Approved,
            "an approved invoice whose status an edit turned back would be booked again by the next approval");
        (await world.LedgerAsync(invoice)).Should().ContainSingle();
        (await world.PoLineAsync()).QtyInvoiced.Should().Be(10m);
    }

    /// <summary>
    /// Runs <paramref name="held"/> until it has read the invoice, holds it there, fires <paramref name="fired"/>,
    /// and lets the held one go on once the fired one has finished — or after <see cref="Hold"/>, when the fired one
    /// is (rightly) waiting for it.
    /// </summary>
    private static async Task<(Outcome Held, Outcome Fired)> HeldThenFiredAsync(
        World world, Func<InvoiceRepository, Task<bool>> held, Func<InvoiceRepository, Task<bool>> fired)
    {
        var firedDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause     = new PauseAfterReadingTheInvoice(firedDone.Task);

        var first = Task.Run(() => world.RunAsync(held, pause));
        await pause.Reached.Task.WaitAsync(Patience);

        var second = Task.Run(() => world.RunAsync(fired));
        _ = second.ContinueWith(_ => firedDone.TrySetResult(), TaskScheduler.Default);

        var results = await Task.WhenAll(first, second).WaitAsync(Patience);
        return (results[0], results[1]);
    }

    /// <summary>
    /// The lock is one invoice's row, not the table: an approval held mid-transaction does not hold up an approval
    /// of another invoice. (The other invoice has no purchase order, so nothing but the invoices table and the
    /// ledgers is shared between them.)
    /// </summary>
    [FinanceSqlServerFact]
    public async Task A_transition_of_one_invoice_does_not_wait_for_a_transition_of_another()
    {
        await using var world = await World.CreateAsync();
        var held  = await world.PendingInvoiceAsync();
        var other = await world.PoLessInvoiceAsync();

        var otherDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold      = new HoldThePoWrite(otherDone.Task, TimeSpan.FromSeconds(30));

        var first = Task.Run(() => world.RunAsync(r => r.ApproveAsync(held, null, User), demandInterceptor: hold));
        await hold.Reached.Task.WaitAsync(Patience);

        var second = await world.RunAsync(r => r.ApproveAsync(other, null, User)).WaitAsync(TimeSpan.FromSeconds(20));
        otherDone.TrySetResult();

        second.Should().Be(Outcome.Done);
        (await first.WaitAsync(Patience)).Should().Be(Outcome.Done);
        hold.ReleasedEarly.Should().BeTrue("the other invoice's approval finished while the first was still held");
        await world.ShouldBeBalancedAsync();
    }

    [FinanceSqlServerFact]
    public async Task Another_organizations_invoice_is_not_found_by_any_transition()
    {
        await using var world = await World.CreateAsync();
        var invoice  = await world.PendingInvoiceAsync();
        var stranger = Guid.NewGuid();

        (await world.RunAsync(r => r.ApproveAsync(invoice, null, User), org: stranger)).Should().Be(Outcome.NotFound);
        (await world.RunAsync(r => r.RejectAsync(invoice, "No", User), org: stranger)).Should().Be(Outcome.NotFound);
        (await world.RunAsync(r => r.PatchAsync(invoice, new PatchInvoiceRequest { Notes = "x" }, User), org: stranger)).Should().Be(Outcome.NotFound);
        (await world.RunAsync(r => r.ApproveAsync(invoice, null, User))).Should().Be(Outcome.Done);
        (await world.RunAsync(r => r.ReverseAsync(invoice, "No", User), org: stranger)).Should().Be(Outcome.NotFound);

        (await world.InvoiceAsync(invoice)).MatchStatus.Should().Be(InvoiceMatchStatus.Approved);
    }

    [FinanceSqlServerFact]
    public async Task Run_one_after_another_the_transitions_still_commit_and_a_refused_one_changes_nothing()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.PendingInvoiceAsync();

        (await world.RunAsync(r => r.PatchAsync(invoice, new PatchInvoiceRequest { Notes = "Checked", DueDate = new DateTime(2026, 11, 1) }, User)))
            .Should().Be(Outcome.Done);
        (await world.RunAsync(r => r.ApproveAsync(invoice, "OK to pay", User))).Should().Be(Outcome.Done);
        (await world.RunAsync(r => r.ApproveAsync(invoice, null, User))).Should().Be(Outcome.Conflict);
        (await world.RunAsync(r => r.RejectAsync(invoice, "Too late", User))).Should().Be(Outcome.Conflict);
        (await world.RunAsync(r => r.ReverseAsync(invoice, "Entered twice", User))).Should().Be(Outcome.Done);

        var stored = await world.InvoiceAsync(invoice);
        stored.MatchStatus.Should().Be(InvoiceMatchStatus.Reversed);
        stored.DueDate.Should().Be(new DateTime(2026, 11, 1));
        stored.Notes.Should().Be("OK to pay");
        (await world.LedgerAsync(invoice)).Should().HaveCount(2);
        (await world.PoLineAsync()).QtyInvoiced.Should().Be(0m);
        await world.ShouldBeBalancedAsync();
    }

    // ── Two invoices on one purchase order ───────────────────────────────────
    // Each invoice has its own lock, so these two never waited for each other — and both read the PO's invoiced
    // quantity, added or took off their own, and wrote it back: the second write lost the first. The PO row is now
    // locked too (after the invoice), so the second reads what the first committed.

    [FinanceSqlServerFact]
    public async Task Approving_two_invoices_of_one_PO_at_once_invoices_both_halves()
    {
        await using var world = await World.CreateAsync();
        var first  = await world.PendingInvoiceAsync(qty: 5m);
        var second = await world.PendingInvoiceAsync(qty: 5m);

        var (held, fired) = await HeldOnThePoThenFiredAsync(
            world,
            held:  r => r.ApproveAsync(first, null, User),
            fired: r => r.ApproveAsync(second, null, User));

        (held, fired).Should().Be((Outcome.Done, Outcome.Done));
        (await world.PoLineAsync()).QtyInvoiced.Should().Be(10m, "5 + 5 — neither approval's quantity is lost");
        (await world.PoAsync()).Status.Should().Be("CLOSED");
        await world.ShouldBeBalancedAsync();
    }

    [FinanceSqlServerFact]
    public async Task Reversing_one_invoice_of_a_PO_while_approving_another_leaves_the_others_quantity()
    {
        await using var world = await World.CreateAsync();
        var reversed = await world.PendingInvoiceAsync(qty: 5m);
        (await world.RunAsync(r => r.ApproveAsync(reversed, null, User))).Should().Be(Outcome.Done);
        var approved = await world.PendingInvoiceAsync(qty: 5m);

        var (held, fired) = await HeldOnThePoThenFiredAsync(
            world,
            held:  r => r.ReverseAsync(reversed, "Entered twice", User),
            fired: r => r.ApproveAsync(approved, null, User));

        (held, fired).Should().Be((Outcome.Done, Outcome.Done));
        (await world.PoLineAsync()).QtyInvoiced.Should().Be(5m, "only the approved invoice's 5 remain invoiced");
        (await world.PoAsync()).Status.Should().Be("PARTIALLY_INVOICED");
        await world.ShouldBeBalancedAsync();
    }

    [FinanceSqlServerFact]
    public async Task An_approval_locks_its_invoice_then_its_purchase_order_in_one_transaction_before_reading_either()
    {
        await using var world = await World.CreateAsync();
        var invoice = await world.PendingInvoiceAsync(qty: 5m);
        var both    = new CommandLog();

        (await world.RunAsync(r => r.ApproveAsync(invoice, null, User), both, both)).Should().Be(Outcome.Done);

        var commands = both.Commands;
        var invoiceLock = commands.FindIndex(c => c.Sql.StartsWith("UPDATE [i]") && c.Sql.Contains("[finance].[invoices]"));
        var poLock      = commands.FindIndex(c => c.Sql.StartsWith("UPDATE [p]") && c.Sql.Contains("[demand].[purchase_orders]"));
        invoiceLock.Should().Be(0, "the invoice is locked before anything about it is read");
        poLock.Should().BeGreaterThan(invoiceLock, "invoice first, then the purchase order — the one lock order");
        commands.Take(poLock).Should().NotContain(c => c.Sql.Contains("FROM [demand].[purchase_orders]"), "the PO is locked before it is read");
        commands.Select(c => c.Transaction).Distinct().Should().ContainSingle()
            .Which.Should().NotBeNull("the locks, the PO's quantities and the ledger entries are one transaction");
    }

    /// <summary>Every command either context sends, in order, with the transaction it ran in.</summary>
    private sealed class CommandLog : DbCommandInterceptor
    {
        private readonly object _gate = new();
        public List<(string Sql, System.Data.Common.DbTransaction? Transaction)> Commands { get; } = [];

        private void Add(DbCommand command)
        {
            lock (_gate) Commands.Add((command.CommandText.TrimStart(), command.Transaction));
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Add(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Add(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Runs <paramref name="held"/> until it has read the purchase order, holds it there, fires <paramref name="fired"/>
    /// at another invoice of the same PO, and lets the held one go on once the fired one has finished — or after
    /// <see cref="Hold"/>, when the fired one is (rightly) waiting for the PO.
    /// </summary>
    private static async Task<(Outcome Held, Outcome Fired)> HeldOnThePoThenFiredAsync(
        World world, Func<InvoiceRepository, Task<bool>> held, Func<InvoiceRepository, Task<bool>> fired)
    {
        var firedDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pause     = new PauseAfterReadingThePo(firedDone.Task);

        var first = Task.Run(() => world.RunAsync(held, demandInterceptor: pause));
        await pause.Reached.Task.WaitAsync(Patience);

        var second = Task.Run(() => world.RunAsync(fired));
        _ = second.ContinueWith(_ => firedDone.TrySetResult(), TaskScheduler.Default);

        var results = await Task.WhenAll(first, second).WaitAsync(Patience);
        return (results[0], results[1]);
    }

    private static async Task<Outcome[]> Race(params Func<Task<Outcome>>[] racers) =>
        await Task.WhenAll(racers.Select(r => Task.Run(r))).WaitAsync(Patience);

    // ── The world: one throwaway database, one organization, one PO ───────────

    private sealed class World : IAsyncDisposable
    {
        private readonly FinanceSqlServerHarness _harness;
        private int _invoices;

        private World(FinanceSqlServerHarness harness) => _harness = harness;

        public Guid Org        { get; } = Guid.NewGuid();
        public Guid Supplier   { get; } = Guid.NewGuid();
        public Guid PoUuid     { get; } = Guid.NewGuid();
        public Guid PoLineUuid { get; } = Guid.NewGuid();

        public static async Task<World> CreateAsync()
        {
            var world = new World(await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true));
            await world.SeedPoAsync();
            return world;
        }

        private async Task SeedPoAsync()
        {
            var po = new PurchaseOrder
            {
                UUID = PoUuid, TraceId = Guid.NewGuid(), PoNumber = "PO-2026-00001", SupplierId = Supplier, SupplierName = "Karachi Steel",
                Status = "RECEIVED", TotalAmount = 1000m, IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
            };
            po.Lines.Add(new PurchaseOrderLine
            {
                UUID = PoLineUuid, LineNo = 1, ItemDescription = "Steel bar", UnitOfMeasure = "PC",
                Quantity = 10m, UnitPrice = 100m, LineTotal = 1000m, QtyReceived = 10m
            });

            await using var demand = NewDemand(Org);
            demand.PurchaseOrders.Add(po);
            await demand.SaveChangesAsync();
        }

        /// <summary>A Pending invoice against the PO's one line (10 ordered and received at 100): all of it, or <paramref name="qty"/>.</summary>
        public Task<Guid> PendingInvoiceAsync(decimal qty = 10m) => CreateAsync(new CreateInvoiceRequest
        {
            SupplierId = Supplier, SupplierInvoiceNo = $"KSW/{++_invoices}", PoUuid = PoUuid,
            InvoiceDate = new DateTime(2026, 9, 15), ReceivedDate = new DateTime(2026, 9, 16), DueDate = new DateTime(2026, 10, 15),
            Currency = "PKR",
            Lines = [new InvoiceLineRequest { PoLineUuid = PoLineUuid, ItemDescription = "Steel bar", QtyInvoiced = qty, UnitPrice = 100m }]
        });

        /// <summary>A freight bill from another supplier: no purchase order, so it stays Pending.</summary>
        public Task<Guid> PoLessInvoiceAsync() => CreateAsync(new CreateInvoiceRequest
        {
            SupplierId = Guid.NewGuid(), SupplierInvoiceNo = $"TCS/{++_invoices}",
            InvoiceDate = new DateTime(2026, 9, 15), ReceivedDate = new DateTime(2026, 9, 16), DueDate = new DateTime(2026, 10, 15),
            Currency = "PKR", Subtotal = 250m
        });

        private async Task<Guid> CreateAsync(CreateInvoiceRequest request)
        {
            Guid uuid = Guid.Empty;
            (await RunAsync(async r => { uuid = await r.CreateAsync(request, User); return true; })).Should().Be(Outcome.Done);
            return uuid;
        }

        /// <summary>One request: its own contexts, its own repository, the action, and how it ended.</summary>
        public async Task<Outcome> RunAsync(
            Func<InvoiceRepository, Task<bool>> action, IInterceptor? financeInterceptor = null,
            IInterceptor? demandInterceptor = null, Guid? org = null)
        {
            var organization = org ?? Org;
            await using var finance   = NewFinance(organization, financeInterceptor);
            await using var demand    = NewDemand(organization, demandInterceptor);
            await using var warehouse = NewWarehouse(organization);
            var repo = new InvoiceRepository(finance, demand, warehouse, new SupplierLedgerService(finance), new FakeSupplierNameLookup());

            try
            {
                return await action(repo) ? Outcome.Done : Outcome.NotFound;
            }
            catch (ConflictException)
            {
                return Outcome.Conflict;
            }
        }

        /// <summary>
        /// The retrying strategy production uses, and a command timeout well past the default 30 seconds. A racer
        /// held on purpose keeps its lock while the other waits for it inside SQL Server; the hold is
        /// <see cref="Hold"/>, but in a full, parallel test run the held racer's continuation can be starved of a
        /// thread for far longer — and at 30 seconds the waiting command times out (SqlException -2, which the
        /// strategy rightly does not retry). That, not the locking, was the one-off 31-second failure of
        /// <see cref="Two_reversals_at_once_credit_the_supplier_once"/>; a holder paused past 30 seconds reproduces it.
        /// </summary>
        private static void Retrying(SqlServerDbContextOptionsBuilder sql) =>
            sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null).CommandTimeout(180);

        private FinanceDbContext NewFinance(Guid org, IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<FinanceDbContext>().UseSqlServer(_harness.ConnectionString, Retrying);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new FinanceDbContext(options.Options, new StaticTenantContext { OrganizationId = org });
        }

        private DemandDbContext NewDemand(Guid org, IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(_harness.ConnectionString, Retrying);
            if (interceptor is not null) options.AddInterceptors(interceptor);
            return new DemandDbContext(options.Options, new StaticTenantContext { OrganizationId = org });
        }

        private WarehouseDbContext NewWarehouse(Guid org) =>
            new(new DbContextOptionsBuilder<WarehouseDbContext>().UseSqlServer(_harness.ConnectionString, Retrying).Options,
                new StaticTenantContext { OrganizationId = org });

        // ── What is stored ──────────────────────────────────────────────────

        public async Task<SMS.Modules.Finance.Domain.Invoice> InvoiceAsync(Guid uuid)
        {
            await using var db = _harness.NewContext(Org);
            return await db.Invoices.AsNoTracking().SingleAsync(i => i.UUID == uuid);
        }

        public async Task<List<SMS.Modules.Finance.Domain.SupplierLedgerEntry>> LedgerAsync(Guid uuid)
        {
            await using var db = _harness.NewContext(Org);
            return await db.SupplierLedgerEntries.AsNoTracking().Where(e => e.ReferenceId == uuid).OrderBy(e => e.SequenceNo).ToListAsync();
        }

        public async Task<List<SMS.Modules.Finance.Domain.MasterFinancialLedger>> MasterAsync(Guid uuid)
        {
            await using var db = _harness.NewContext(Org);
            return await db.MasterFinancialLedgers.AsNoTracking().Where(e => e.ReferenceId == uuid).OrderBy(e => e.SequenceNo).ToListAsync();
        }

        public async Task<PurchaseOrder> PoAsync()
        {
            await using var db = NewDemand(Org);
            return await db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).SingleAsync(p => p.UUID == PoUuid);
        }

        public async Task<PurchaseOrderLine> PoLineAsync() => (await PoAsync()).Lines.Single();

        /// <summary>
        /// The supplier ledgers and the master ledger tell the same story: every supplier entry has its master twin,
        /// each chain's running balance is the sum of what came before, and the master's last balance is the sum of
        /// every supplier's.
        /// </summary>
        public async Task ShouldBeBalancedAsync()
        {
            await using var db = _harness.NewContext(Org);
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

            running.Should().Be(supplierEntries.Sum(e => e.DebitAmount - e.CreditAmount));
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

    /// <summary>
    /// Pauses this context once it has finished reading an invoice for the first time (its reader is closing):
    /// whatever it does next, it does on what it read.
    /// </summary>
    private abstract class AfterReadingTheInvoice : DbCommandInterceptor
    {
        private int _paused;

        protected abstract Task PauseAsync();

        public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            var sql = command.CommandText;
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("FROM [finance].[invoices]", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _paused, 1) == 0)
                await PauseAsync();

            return result;
        }
    }

    /// <summary>Holds this context after its first read of the invoice until every racer has read it too (or <see cref="Hold"/> passes).</summary>
    private sealed class MeetAfterReadingTheInvoice(Rendezvous rendezvous) : AfterReadingTheInvoice
    {
        protected override Task PauseAsync() => rendezvous.ArriveAsync();
    }

    /// <summary>Holds this context after its first read of the invoice until <paramref name="release"/> completes (or <see cref="Hold"/> passes).</summary>
    private sealed class PauseAfterReadingTheInvoice(Task release) : AfterReadingTheInvoice
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task PauseAsync()
        {
            Reached.TrySetResult();
            await Task.WhenAny(release, Task.Delay(Hold));
        }
    }

    /// <summary>Holds this Demand context once it has read the purchase order, until <paramref name="release"/> completes (or <see cref="Hold"/> passes).</summary>
    private sealed class PauseAfterReadingThePo(Task release) : DbCommandInterceptor
    {
        private int _paused;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> DataReaderClosingAsync(
            DbCommand command, DataReaderClosingEventData eventData, InterceptionResult result)
        {
            var sql = command.CommandText;
            if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                && sql.Contains("FROM [demand].[purchase_orders]", StringComparison.OrdinalIgnoreCase)
                && Interlocked.Exchange(ref _paused, 1) == 0)
            {
                Reached.TrySetResult();
                await Task.WhenAny(release, Task.Delay(Hold));
            }

            return result;
        }
    }

    /// <summary>Holds this Demand context's first write to a purchase order until <paramref name="release"/> completes (or the patience runs out).</summary>
    private sealed class HoldThePoWrite(Task release, TimeSpan? patience = null) : DbCommandInterceptor
    {
        private int _held;

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReleasedEarly { get; private set; }

        private async Task HoldAsync(DbCommand command)
        {
            if (!command.CommandText.Contains("UPDATE [demand].[purchase_order", StringComparison.OrdinalIgnoreCase)
                || Interlocked.Exchange(ref _held, 1) != 0)
                return;

            Reached.TrySetResult();
            ReleasedEarly = await Task.WhenAny(release, Task.Delay(patience ?? Hold)) == release;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await HoldAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await HoldAsync(command);
            return result;
        }
    }
}
