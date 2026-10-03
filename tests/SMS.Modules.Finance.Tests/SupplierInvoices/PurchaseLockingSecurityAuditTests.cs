using System.Collections.Concurrent;
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
/// Round-2 security audit, on real SQL Server (LocalDB; skipped when none is reachable): what row locks and lock
/// ORDER must guarantee, under the retrying execution strategy production uses.
/// <list type="bullet">
/// <item>One approved payment posted twice at the same moment moves money once — not a double payment.</item>
/// <item>Invoice approvals and reversals on one purchase order, and multi-invoice payments naming the same invoices in
/// opposite orders, all running at once, never deadlock (SQL error 1205 — which the retrying strategy would otherwise
/// hide by silently re-running a victim) and leave the books consistent.</item>
/// </list>
/// </summary>
public class PurchaseLockingSecurityAuditTests
{
    private const int User = 7;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    /// <summary>Counts deadlock victims (1205) on every command, whatever the execution strategy then does about them.</summary>
    private sealed class DeadlockCounter : DbCommandInterceptor
    {
        public int Deadlocks;

        public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        {
            if (eventData.Exception is SqlException { Number: 1205 }) Interlocked.Increment(ref Deadlocks);
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            CommandFailed(command, eventData);
            return Task.CompletedTask;
        }
    }

    private sealed class World : IAsyncDisposable
    {
        private FinanceSqlServerHarness _harness = null!;
        public Guid Org { get; } = Guid.NewGuid();
        public Guid Supplier { get; } = Guid.NewGuid();
        public DeadlockCounter Counter { get; } = new();

        public static async Task<World> CreateAsync()
        {
            var w = new World { _harness = await FinanceSqlServerHarness.CreateAsync(withStockAndPurchasing: true, retryOnFailure: true) };
            await using var wh = w.Warehouse();
            await wh.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
            return w;
        }

        private T Sql<T>(Func<DbContextOptions<T>, ITenantContext, T> make) where T : DbContext
        {
            var options = new DbContextOptionsBuilder<T>()
                .UseSqlServer(_harness.ConnectionString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null))
                .AddInterceptors(Counter)
                .Options;
            return make(options, new StaticTenantContext { OrganizationId = Org });
        }

        public FinanceDbContext   Finance()   => Sql<FinanceDbContext>((o, t) => new FinanceDbContext(o, t));
        public DemandDbContext    Demand()    => Sql<DemandDbContext>((o, t) => new DemandDbContext(o, t));
        public WarehouseDbContext Warehouse() => Sql<WarehouseDbContext>((o, t) => new WarehouseDbContext(o, t));

        /// <summary>One request's worth of the purchase side: fresh contexts, the real services.</summary>
        public async Task<T> As<T>(Func<InvoiceService, SupplierPaymentRepository, Task<T>> act)
        {
            await using var fin = Finance();
            await using var dem = Demand();
            await using var wh  = Warehouse();
            var invoices = new InvoiceService(
                new InvoiceRepository(fin, dem, wh, new SupplierLedgerService(fin), new FakeSupplierNameLookup()),
                new Mock<IBackgroundJobClient>().Object);
            var payments = new SupplierPaymentRepository(fin, new SupplierLedgerService(fin), new Mock<INotificationService>().Object);
            return await act(invoices, payments);
        }

        public async Task<(PurchaseOrder Po, List<PurchaseOrderLine> Lines)> PoAsync(int lines)
        {
            await using var dem = Demand();
            var po = new PurchaseOrder
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PoNumber = $"PO-{Guid.NewGuid():N}"[..14],
                SupplierId = Supplier, SupplierName = "Karachi Steel", Status = "RECEIVED", CreatedBy = 1, CreatedDate = DateTime.UtcNow
            };
            for (var i = 0; i < lines; i++)
                po.Lines.Add(new PurchaseOrderLine
                {
                    UUID = Guid.NewGuid(), LineNo = i + 1, ItemDescription = $"Item {i + 1}", Quantity = 10m, UnitPrice = 100m,
                    LineTotal = 1000m, QtyReceived = 10m
                });
            po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
            dem.PurchaseOrders.Add(po);
            await dem.SaveChangesAsync();
            return (po, po.Lines.OrderBy(l => l.LineNo).ToList());
        }

        /// <summary>An invoice for one PO line's full quantity (1000), approved or not.</summary>
        public async Task<Guid> InvoiceAsync(PurchaseOrder po, PurchaseOrderLine line, bool approve)
        {
            var uuid = await As((s, _) => s.CreateAsync(new CreateInvoiceRequest
            {
                SupplierId = Supplier, SupplierInvoiceNo = $"S-{Guid.NewGuid():N}"[..10], PoUuid = po.UUID,
                InvoiceDate = new DateTime(2026, 9, 15), ReceivedDate = new DateTime(2026, 9, 16), DueDate = new DateTime(2026, 10, 15),
                Currency = "PKR",
                Lines = [new InvoiceLineRequest { PoLineUuid = line.UUID, ItemDescription = line.ItemDescription, QtyInvoiced = 10m, UnitPrice = 100m }]
            }, User));
            if (approve) (await As((s, _) => s.ApproveAsync(uuid, null, User))).Should().BeTrue();
            return uuid;
        }

        public async Task<Guid> ApprovedPaymentAsync(params Guid[] invoices)
        {
            var uuid = await As((_, p) => p.CreateAsync(new CreateSupplierPaymentRequest
            {
                SupplierId = Supplier, SupplierName = "Karachi Steel", PaymentDate = new DateTime(2026, 9, 30), PaymentMethod = "CASH",
                TotalAmount = 100m * invoices.Length,
                Lines = [.. invoices.Select(i => new CreateSupplierPaymentLineRequest { InvoiceUuid = i, AllocatedAmount = 100m })]
            }, User));
            (await As((_, p) => p.ApproveAsync(uuid, User))).Should().BeTrue();
            return uuid;
        }

        public async ValueTask DisposeAsync() => await _harness.DisposeAsync();
    }

    private static async Task<object?> Outcome(Func<Task<bool>> act)
    {
        try { return await act(); }
        catch (Exception ex) when (ex is BadRequestException or ConflictException or UnprocessableEntityException) { return ex; }
    }

    [FinanceSqlServerFact]
    public async Task SecurityAudit_one_approved_payment_posted_twice_at_once_pays_once()
    {
        await using var w = await World.CreateAsync();
        var (po, lines) = await w.PoAsync(1);
        var invoice = await w.InvoiceAsync(po, lines[0], approve: true);
        var payment = await w.ApprovedPaymentAsync(invoice);

        using var gate = new Barrier(2);
        var posts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            gate.SignalAndWait(Patience);
            return await Outcome(() => w.As((_, p) => p.PostAsync(payment, User)));
        })).ToArray();
        var results = await Task.WhenAll(posts).WaitAsync(Patience);

        results.Count(r => r is true).Should().Be(1, "exactly one of two simultaneous posts may move the money");

        await using var db = w.Finance();
        (await db.Invoices.SingleAsync(i => i.UUID == invoice)).PaidAmount.Should().Be(100m, "the payment was paid twice");
        (await db.SupplierLedgerEntries.CountAsync(e => e.ReferenceId == payment && e.TransactionType == "PAYMENT_POSTED")).Should().Be(1);
        (await db.SupplierPayments.SingleAsync(p => p.UUID == payment)).Status.Should().Be("POSTED");
    }

    [FinanceSqlServerFact]
    public async Task SecurityAudit_approvals_reversals_and_opposite_order_payments_running_together_never_deadlock()
    {
        await using var w = await World.CreateAsync();
        const int rounds = 4;
        var failures = new ConcurrentBag<string>();

        for (var round = 0; round < rounds; round++)
        {
            // PO P carries one invoice to approve and one approved invoice to reverse — both lock P. PO Q carries two
            // approved invoices that two payments name in opposite orders.
            var (p, pLines) = await w.PoAsync(2);
            var (q, qLines) = await w.PoAsync(2);
            var toApprove = await w.InvoiceAsync(p, pLines[0], approve: false);
            var toReverse = await w.InvoiceAsync(p, pLines[1], approve: true);
            var i3 = await w.InvoiceAsync(q, qLines[0], approve: true);
            var i4 = await w.InvoiceAsync(q, qLines[1], approve: true);
            var payForward  = await w.ApprovedPaymentAsync(i3, i4);
            var payBackward = await w.ApprovedPaymentAsync(i4, i3);

            using var gate = new Barrier(4);
            async Task Run(string what, Func<Task<bool>> act)
            {
                gate.SignalAndWait(Patience);
                try { await act(); }
                catch (Exception ex) when (ex is not (BadRequestException or ConflictException or UnprocessableEntityException))
                {
                    failures.Add($"round {round} {what}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            await Task.WhenAll(
                Task.Run(() => Run("approve", () => w.As((s, _) => s.ApproveAsync(toApprove, null, User)))),
                Task.Run(() => Run("reverse", () => w.As((s, _) => s.ReverseAsync(toReverse, "Entered twice", User)))),
                Task.Run(() => Run("post forward", () => w.As((_, pay) => pay.PostAsync(payForward, User)))),
                Task.Run(() => Run("post backward", () => w.As((_, pay) => pay.PostAsync(payBackward, User))))
            ).WaitAsync(Patience);

            await using var dem = w.Demand();
            var pNow = await dem.PurchaseOrders.AsNoTracking().Include(x => x.Lines).SingleAsync(x => x.UUID == p.UUID);
            pNow.Lines.Single(l => l.UUID == pLines[0].UUID).QtyInvoiced.Should().Be(10m, $"round {round}: the approval's quantity must not be lost");
            pNow.Lines.Single(l => l.UUID == pLines[1].UUID).QtyInvoiced.Should().Be(0m, $"round {round}: the reversal's quantity must not be lost");

            await using var fin = w.Finance();
            (await fin.Invoices.SingleAsync(i => i.UUID == i3)).PaidAmount.Should().Be(200m, $"round {round}: both payments paid I3 once each");
            (await fin.Invoices.SingleAsync(i => i.UUID == i4)).PaidAmount.Should().Be(200m, $"round {round}: both payments paid I4 once each");
        }

        failures.Should().BeEmpty();
        w.Counter.Deadlocks.Should().Be(0, "a deadlock victim is re-run silently by the retrying strategy, but it is still a lock-order bug");
    }
}
