using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.ArAp.Payables;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using Xunit.Abstractions;

namespace SMS.Modules.Finance.Tests.ArApPayables;

/// <summary>
/// A48 C11 on a real SQL Server (LocalDB, a throwaway database; never SMSGlobal): TS-PR-012 — two proposals and two
/// executions at once never pay a schedule twice — and the 200-vendor / 500-item run timing.
/// </summary>
public class PaymentRunSqlServerTests
{
    private static readonly ApActor Actor = new(7, "Tess Tester");
    private readonly ITestOutputHelper _out;

    public PaymentRunSqlServerTests(ITestOutputHelper output) => _out = output;

    private sealed class Racer : IAsyncDisposable
    {
        public required FinanceDbContext Fin;
        public required PaymentRunService Runs;
        public ValueTask DisposeAsync() => Fin.DisposeAsync();
    }

    /// <summary>Writes the run's timing line to the console (the perf test's report).</summary>
    private sealed class ConsoleLog : Microsoft.Extensions.Logging.ILogger<PaymentRunService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Information;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) Console.WriteLine("A48-PERF " + formatter(state, exception));
        }
    }

    private static Racer NewRacer(FinanceSqlServerHarness h, Guid org, bool log = false)
    {
        if (log)
        {
            var f = h.NewContext(org);
            var l = new SupplierLedgerService(f);
            var sch = new PaymentScheduleService(f, NullLogger<PaymentScheduleService>.Instance);
            var pay = new SupplierPaymentRepository(f, l, new Mock<INotificationService>().Object);
            return new Racer { Fin = f, Runs = new PaymentRunService(f, sch, pay, log: new ConsoleLog()) };
        }
        var fin = h.NewContext(org);
        var ledger = new SupplierLedgerService(fin);
        var schedules = new PaymentScheduleService(fin, NullLogger<PaymentScheduleService>.Instance);
        var payments = new SupplierPaymentRepository(fin, ledger, new Mock<INotificationService>().Object);
        return new Racer { Fin = fin, Runs = new PaymentRunService(fin, schedules, payments) };
    }

    /// <summary>Approved invoices (with their INVOICE_APPROVED ledger debit), round-robin over the vendors.</summary>
    private static async Task SeedAsync(FinanceSqlServerHarness h, Guid org, int vendors, int invoices)
    {
        await using var fin = h.NewContext(org);
        var ids = Enumerable.Range(0, vendors).Select(_ => Guid.NewGuid()).ToList();
        var seq = new Dictionary<Guid, int>();
        for (var n = 0; n < invoices; n++)
        {
            var vendor = ids[n % vendors];
            var uuid = Guid.NewGuid();
            var amount = 100m + n;
            fin.Invoices.Add(new Invoice
            {
                UUID = uuid, TraceId = Guid.NewGuid(), OrganizationId = org, InvoiceNumber = $"INV-2026-{n + 1:D5}", SupplierInvoiceNo = $"S-{n}",
                SupplierId = vendor, SupplierName = $"Vendor {n % vendors:D3}", InvoiceDate = new DateTime(2026, 9, 1), ReceivedDate = new DateTime(2026, 9, 1),
                DueDate = DateTime.UtcNow.Date.AddDays(n % 20), Currency = "PKR", Subtotal = amount, TotalAmount = amount,
                MatchStatus = InvoiceMatchStatus.Approved, PaymentStatus = "Unpaid", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
            });
            var s = seq[vendor] = seq.GetValueOrDefault(vendor) + 1;
            fin.SupplierLedgerEntries.Add(new SupplierLedgerEntry
            {
                UUID = Guid.NewGuid(), OrganizationId = org, SupplierId = vendor, SequenceNo = s, TransactionType = "INVOICE_APPROVED",
                ReferenceType = "Invoice", ReferenceId = uuid, ReferenceNo = $"INV-2026-{n + 1:D5}", EntryDate = DateTime.UtcNow,
                DebitAmount = amount, BalanceAfter = 0m, CreatedBy = 1, CreatedDate = DateTime.UtcNow
            });
        }
        await fin.SaveChangesAsync();
    }

    private static ProposePaymentRunRequest Proposal() => new()
    {
        PaymentDate = DateOnly.FromDateTime(DateTime.UtcNow), DueDateCutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(60), BankAccount = "HBL-0001-OPS"
    };

    private static async Task<object?> Try(Func<Task> work)
    {
        try { await work(); return null; }
        catch (Exception ex) { return ex; }
    }

    [FinanceSqlServerFact]
    public async Task TS_PR_012_two_proposals_at_once_never_hold_the_same_schedule()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);
        var org = Guid.NewGuid();
        await SeedAsync(h, org, vendors: 10, invoices: 60);

        await using var a = NewRacer(h, org);
        await using var b = NewRacer(h, org);
        var results = await Task.WhenAll(
            Try(() => a.Runs.ProposeAsync(Proposal(), Actor)),
            Try(() => b.Runs.ProposeAsync(Proposal(), Actor)));
        foreach (var r in results.OfType<Exception>()) r.Should().BeOfType<BadRequestException>("the loser finds nothing left: {0}", r.InnerException?.Message ?? r.Message);

        await using var check = h.NewContext(org);
        var items = await check.Set<PaymentRunItem>().AsNoTracking().ToListAsync();
        items.Select(i => i.PaymentScheduleId).Should().OnlyHaveUniqueItems("no schedule is in two runs");
        items.Should().HaveCount(60, "between them the runs hold every schedule once");
        var schedules = await check.Set<PaymentSchedule>().AsNoTracking().ToListAsync();
        schedules.Should().HaveCount(60).And.OnlyContain(s => s.ReservedByRunId != null && s.Status == "Scheduled");
        foreach (var s in schedules)
            items.Single(i => i.PaymentScheduleId == s.Id).PaymentRunId.Should().Be(s.ReservedByRunId!.Value);
    }

    [FinanceSqlServerFact]
    public async Task TS_PR_012_two_executions_of_one_run_at_once_pay_once()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);
        var org = Guid.NewGuid();
        await SeedAsync(h, org, vendors: 5, invoices: 20);

        Guid run;
        await using (var setup = NewRacer(h, org))
        {
            run = (await setup.Runs.ProposeAsync(Proposal(), Actor)).Run.Uuid;
            await setup.Runs.ApproveAsync(run, Actor);
        }

        await using var a = NewRacer(h, org);
        await using var b = NewRacer(h, org);
        var results = await Task.WhenAll(
            Try(() => a.Runs.ExecuteAsync(run, Actor)),
            Try(() => b.Runs.ExecuteAsync(run, Actor)));

        results.Count(r => r is null).Should().Be(1);
        results.OfType<ConflictException>().Should().ContainSingle().Which.Message.Should().Be("Only an approved payment run can be executed.");

        await using var check = h.NewContext(org);
        var payments = await check.SupplierPayments.AsNoTracking().Include(p => p.Lines).ToListAsync();
        payments.Should().HaveCount(5).And.OnlyContain(p => p.Status == "POSTED");
        payments.SelectMany(p => p.Lines).Select(l => l.InvoiceUuid).Should().OnlyHaveUniqueItems().And.HaveCount(20);
        (await check.Invoices.AsNoTracking().ToListAsync()).Should().OnlyContain(i => i.PaidAmount == i.TotalAmount);
        (await check.Set<PaymentRun>().AsNoTracking().SingleAsync()).Status.Should().Be("Completed");
    }

    [FinanceSqlServerFact]
    public async Task D_AP_13_two_resumes_of_a_stuck_run_at_once_pay_every_batch_once()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);
        var org = Guid.NewGuid();
        await SeedAsync(h, org, vendors: 8, invoices: 24);

        Guid run;
        await using (var setup = NewRacer(h, org))
        {
            run = (await setup.Runs.ProposeAsync(Proposal(), Actor)).Run.Uuid;
            await setup.Runs.ApproveAsync(run, Actor);
            // As a process that died right after Approved → Executing.
            await setup.Fin.Set<PaymentRun>().Where(r => r.Uuid == run)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, ArApEnums.PaymentRunStatus.Executing));
        }

        await using var a = NewRacer(h, org);
        await using var b = NewRacer(h, org);
        var results = await Task.WhenAll(Try(() => a.Runs.ResumeAsync(run, Actor)), Try(() => b.Runs.ResumeAsync(run, Actor)));
        results.Should().OnlyContain(r => r == null, "both resumes finish; each batch is claimed by one of them");

        await using var check = h.NewContext(org);
        var payments = await check.SupplierPayments.AsNoTracking().Include(p => p.Lines).ToListAsync();
        payments.Should().HaveCount(8);
        payments.SelectMany(p => p.Lines).Select(l => l.InvoiceUuid).Should().OnlyHaveUniqueItems().And.HaveCount(24);
        var stored = await check.Set<PaymentRun>().AsNoTracking().Include(r => r.Batches).SingleAsync();
        stored.Status.Should().Be("Completed");
        stored.Batches.Should().OnlyContain(x => x.Status == "Paid");
    }

    [FinanceSqlServerFact]
    public async Task Debit_memo_create_and_apply_and_schedule_actions_work_under_the_retrying_strategy()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);
        var org = Guid.NewGuid();
        await SeedAsync(h, org, vendors: 1, invoices: 2);
        await using var fin = h.NewContext(org);
        var invoices = await fin.Invoices.AsNoTracking().OrderBy(i => i.Id).ToListAsync();
        var schedules = new PaymentScheduleService(fin, NullLogger<PaymentScheduleService>.Instance);
        var memos = new ApDebitMemoService(fin, new SupplierLedgerService(fin), new FakeSupplierNameLookup(), null, schedules);

        var memo = await memos.CreateAsync(new CreateApDebitMemoRequest { VendorUuid = invoices[0].SupplierId, Reason = "REBATE", Amount = 50m }, Actor);
        var applied = await memos.ApplyAsync(memo.Uuid, new ApplyApDebitMemoRequest
            { Applications = [new() { InvoiceUuid = invoices[0].UUID, Amount = 30m }, new() { InvoiceUuid = invoices[1].UUID, Amount = 20m }] }, Actor);
        (applied.ApplicationStatus, applied.Applications.Count).Should().Be(("APPLIED", 2));

        var list = await schedules.ListAsync(null, null, null, null, 1, 25);
        list.Data.Should().HaveCount(2);
        list.Data.Single(s => s.InvoiceUuid == invoices[0].UUID).AmountRemaining.Should().Be(invoices[0].TotalAmount - 30m);
        var blocked = await schedules.BlockAsync(list.Data[0].Uuid, "dispute", Actor);
        blocked.Status.Should().Be("Blocked");
        (await schedules.UpdateAsync(list.Data[1].Uuid, new UpdateScheduleRequest { PaymentPriority = "High", RowVersion = list.Data[1].RowVersion }, Actor))
            .PaymentPriority.Should().Be("High");
        fin.ChangeTracker.Clear();
        await FluentActions.Awaiting(() => schedules.UpdateAsync(list.Data[1].Uuid, new UpdateScheduleRequest { PaymentPriority = "Low", RowVersion = list.Data[1].RowVersion }, Actor))
            .Should().ThrowAsync<ConflictException>().WithMessage("The payment schedule was changed by someone else; reload and try again.");
    }

    [FinanceSqlServerFact]
    public async Task Performance_a_run_of_200_vendors_and_500_items()
    {
        await using var h = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);
        var org = Guid.NewGuid();
        await SeedAsync(h, org, vendors: 200, invoices: 500);
        await using var r = NewRacer(h, org, log: true);

        var sw = Stopwatch.StartNew();
        var proposal = await r.Runs.ProposeAsync(Proposal(), Actor);
        var propose = sw.Elapsed;
        await r.Runs.ApproveAsync(proposal.Run.Uuid, Actor);
        var approve = sw.Elapsed - propose;
        sw.Restart();
        var done = await r.Runs.ExecuteAsync(proposal.Run.Uuid, Actor);
        var execute = sw.Elapsed;
        sw.Restart();
        var file = await r.Runs.ExportAsync(proposal.Run.Uuid, "csv", Actor);
        var export = sw.Elapsed;

        _out.WriteLine($"A48 perf (LocalDB): propose 500 items / 200 vendors {propose.TotalMilliseconds:N0} ms (incl. creating 500 schedules), " +
                       $"approve {approve.TotalMilliseconds:N0} ms, execute 200 payments {execute.TotalMilliseconds:N0} ms, export {export.TotalMilliseconds:N0} ms.");
        Console.WriteLine($"A48-PERF propose={propose.TotalMilliseconds:N0}ms approve={approve.TotalMilliseconds:N0}ms execute={execute.TotalMilliseconds:N0}ms export={export.TotalMilliseconds:N0}ms");

        (proposal.Run.TotalVendors, proposal.Run.TotalItems).Should().Be((200, 500));
        done.Run.Status.Should().Be("Completed");
        done.Run.FailedBatchCount.Should().Be(0);
        file.Content.Length.Should().BeGreaterThan(0);
        await using var check = h.NewContext(org);
        (await check.SupplierPayments.CountAsync(p => p.Status == "POSTED")).Should().Be(200);
        (await check.Set<PaymentSchedule>().CountAsync(s => s.Status == "Paid")).Should().Be(500);
        propose.Should().BeLessThan(TimeSpan.FromSeconds(30));
        execute.Should().BeLessThan(TimeSpan.FromMinutes(5));
    }
}
