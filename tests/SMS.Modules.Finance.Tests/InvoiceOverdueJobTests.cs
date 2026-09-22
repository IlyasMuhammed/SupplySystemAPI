using FluentAssertions;
using Hangfire;
using Hangfire.InMemory;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-07 §9.5 — the daily sweep that marks ISSUED and PARTIALLY_PAID invoices OVERDUE once their
/// due date has passed with a balance still owing.
/// </summary>
public class InvoiceOverdueJobTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Yesterday = new(2026, 9, 19);
    private static readonly DateTime Today = new(2026, 9, 20);

    private sealed class FakeClock : TimeProvider
    {
        public DateTime Value { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Value, DateTimeKind.Utc));
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Records how many invoices each save carried, and can fail one chosen save.</summary>
    private sealed class SaveTracker : SaveChangesInterceptor
    {
        private readonly int _failOnSave;
        public List<int> ModifiedPerSave { get; } = [];

        public SaveTracker(int failOnSave = 0) => _failOnSave = failOnSave;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            ModifiedPerSave.Add(eventData.Context!.ChangeTracker.Entries<SalesInvoice>().Count(e => e.State == EntityState.Modified));
            if (ModifiedPerSave.Count == _failOnSave) throw new DbUpdateException("the database dropped the connection");
            return base.SavingChangesAsync(eventData, result, ct);
        }
    }

    private sealed class Harness
    {
        public required FinanceDbContext Db;
        public required InvoiceOverdueJob Job;
        public required FakeClock Clock;
        public required string DbName;
        public required ListLogger<InvoiceOverdueJob> Log;
    }

    /// <summary>
    /// The job as Hangfire runs it: no organization of its own — the tenant context reports a bypass,
    /// as <c>TenantContext.IsSuperAdmin</c> does for a bare recurring job.
    /// </summary>
    private static Harness NewJob(string? dbName = null, FakeClock? clock = null, IInterceptor? interceptor = null, StaticTenantContext? tenant = null)
    {
        dbName ??= Guid.NewGuid().ToString();
        clock  ??= new FakeClock();
        var options = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var db  = new FinanceDbContext(options.Options, tenant ?? new StaticTenantContext { IsSuperAdmin = true });
        var log = new ListLogger<InvoiceOverdueJob>();
        return new Harness { Db = db, Job = new InvoiceOverdueJob(db, log, clock), Clock = clock, DbName = dbName, Log = log };
    }

    /// <summary>Reads across every organization, as an auditor would.</summary>
    private static FinanceDbContext Reader(string dbName) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { IsSuperAdmin = true });

    private static SalesInvoice Invoice(
        string number, DateTime due, string status = "ISSUED", decimal balance = 1000m, decimal grand = 1000m,
        Guid? org = null, bool deleted = false) =>
        new()
        {
            UUID = Guid.NewGuid(), OrganizationId = org ?? Guid.NewGuid(), TraceId = Guid.NewGuid(),
            InvoiceNumber = number, SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = "SO-2026-00042",
            PartnerId = Guid.NewGuid(), PartnerName = "Acme Ltd",
            InvoiceDate = due.AddDays(-30), DueDate = due,
            Subtotal = grand, GrandTotal = grand, AmountPaid = grand - balance, BalanceDue = balance,
            Status = status, CurrencyCode = "PKR", IsDelete = deleted, CreatedBy = 1, CreatedDate = due.AddDays(-30)
        };

    private static async Task Seed(Harness h, params SalesInvoice[] invoices)
    {
        h.Db.SalesInvoices.AddRange(invoices);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
    }

    private static Task<SalesInvoice> Stored(Harness h, string number) =>
        Reader(h.DbName).SalesInvoices.AsNoTracking().SingleAsync(i => i.InvoiceNumber == number);

    // ── What becomes overdue ─────────────────────────────────────────────────

    [Fact]
    public async Task An_issued_invoice_past_its_due_date_becomes_overdue()
    {
        var h = NewJob();
        await Seed(h, Invoice("SINV-1", Yesterday));

        var flagged = await h.Job.SweepAsync();

        flagged.Should().Be(1);
        (await Stored(h, "SINV-1")).Status.Should().Be("OVERDUE");
    }

    [Fact]
    public async Task A_partially_paid_invoice_past_its_due_date_becomes_overdue()
    {
        var h = NewJob();
        await Seed(h, Invoice("SINV-1", Yesterday, "PARTIALLY_PAID", balance: 700m));

        await h.Job.SweepAsync();

        var invoice = await Stored(h, "SINV-1");
        invoice.Status.Should().Be("OVERDUE");
        (invoice.AmountPaid, invoice.BalanceDue).Should().Be((300m, 700m), "what was paid stays paid");
    }

    [Theory]
    [InlineData(-400, true)]   // long overdue
    [InlineData(-1, true)]     // due yesterday: overdue from today
    [InlineData(0, false)]     // due today: the customer still has the day
    [InlineData(1, false)]
    [InlineData(30, false)]
    public async Task An_invoice_is_overdue_the_day_after_its_due_date_not_on_it(int dueOffsetDays, bool overdue)
    {
        var h = NewJob();
        await Seed(h, Invoice("SINV-1", Today.AddDays(dueOffsetDays)));

        await h.Job.SweepAsync();

        (await Stored(h, "SINV-1")).Status.Should().Be(overdue ? "OVERDUE" : "ISSUED");
    }

    [Fact]
    public async Task A_due_date_with_a_time_of_day_is_still_due_for_the_whole_of_that_day()
    {
        var h = NewJob();
        await Seed(h, Invoice("SINV-1", Today.AddHours(15)));   // due 15:00 today; it is 10:30

        h.Clock.Value = new DateTime(2026, 9, 20, 23, 59, 59, DateTimeKind.Utc);
        await h.Job.SweepAsync();
        (await Stored(h, "SINV-1")).Status.Should().Be("ISSUED", "still the due date at 23:59:59");

        h.Clock.Value = new DateTime(2026, 9, 21, 0, 0, 0, DateTimeKind.Utc);
        await h.Job.SweepAsync();
        (await Stored(h, "SINV-1")).Status.Should().Be("OVERDUE", "overdue from the first moment of the next day");
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("PAID")]
    [InlineData("CANCELLED")]
    [InlineData("CREDIT_NOTE")]
    [InlineData("OVERDUE")]
    public async Task No_other_status_is_touched(string status)
    {
        var h = NewJob();
        var original = Invoice("SINV-1", Yesterday, status, balance: status == "PAID" ? 0m : 1000m);
        await Seed(h, original);

        var flagged = await h.Job.SweepAsync();

        flagged.Should().Be(0);
        var stored = await Stored(h, "SINV-1");
        stored.Status.Should().Be(status);
        stored.ModifiedBy.Should().BeNull("an invoice the sweep did not flag is not modified");
        stored.ModifiedDate.Should().BeNull();
    }

    [Fact]
    public async Task A_deleted_invoice_and_one_with_nothing_owing_are_left_alone()
    {
        var h = NewJob();
        await Seed(h,
            Invoice("SINV-DELETED", Yesterday, deleted: true),
            Invoice("SINV-SETTLED", Yesterday, "ISSUED", balance: 0m));

        var flagged = await h.Job.SweepAsync();

        flagged.Should().Be(0);
        (await Stored(h, "SINV-DELETED")).Status.Should().Be("ISSUED");
        (await Stored(h, "SINV-SETTLED")).Status.Should().Be("ISSUED", "§9.5 asks for balance_due > 0");
    }

    // ── What the sweep changes, and what it must not ─────────────────────────

    [Fact]
    public async Task Only_the_status_and_who_and_when_change_and_no_money_moves()
    {
        var h = NewJob();
        var original = Invoice("SINV-1", Yesterday, "PARTIALLY_PAID", balance: 640m, grand: 1000m);
        await Seed(h, original);

        await h.Job.SweepAsync();

        var stored = await Stored(h, "SINV-1");
        stored.Should().BeEquivalentTo(original, o => o
            .Excluding(i => i.Status).Excluding(i => i.ModifiedBy).Excluding(i => i.ModifiedDate)
            .Excluding(i => i.Lines).Excluding(i => i.Allocations).Excluding(i => i.Id));
        stored.ModifiedBy.Should().Be(InvoiceOverdueJob.SystemUserId, "recorded against the system, not a person");
        stored.ModifiedDate.Should().Be(Now);

        using var reader = Reader(h.DbName);
        (await reader.CustomerLedgerEntries.CountAsync()).Should().Be(0, "going overdue books nothing");
        (await reader.CustomerPayments.CountAsync()).Should().Be(0);
        (await reader.PaymentAllocations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Running_it_again_changes_nothing()
    {
        var h = NewJob();
        await Seed(h, Invoice("SINV-1", Yesterday), Invoice("SINV-2", Yesterday.AddDays(-5), "PARTIALLY_PAID", balance: 200m));

        (await h.Job.SweepAsync()).Should().Be(2);
        h.Clock.Value = Now.AddDays(1);
        (await h.Job.SweepAsync()).Should().Be(0, "flagged invoices no longer match");

        (await Stored(h, "SINV-1")).ModifiedDate.Should().Be(Now, "not re-stamped by the second run");
    }

    [Fact]
    public async Task An_invoice_paid_in_full_in_the_instant_the_sweep_reads_it_is_not_flagged_overdue()
    {
        // The sweep reads a part-paid invoice as owing 700. Before it saves, a payment settles it. The
        // sweep's save then matches no row (the invoice's ModifiedDate has moved) and fails, instead
        // of overwriting PAID with OVERDUE; Hangfire retries the job, and the retry finds it settled.
        var dbName = Guid.NewGuid().ToString();
        var clock  = new FakeClock();
        var seed   = NewJob(dbName, clock);
        var invoice = Invoice("SINV-1", Yesterday, "PARTIALLY_PAID", balance: 700m);
        await Seed(seed, invoice);

        var race = new RunOnceBeforeSave(db => db.ChangeTracker.Entries<SalesInvoice>().Any(e => e.State == EntityState.Modified), async () =>
        {
            using var payer = Reader(dbName);
            var row = await payer.SalesInvoices.SingleAsync(i => i.InvoiceNumber == "SINV-1");
            row.AmountPaid = row.GrandTotal;
            row.BalanceDue = 0m;
            row.Status = "PAID";
            row.ModifiedBy = 5;
            row.ModifiedDate = Now.AddSeconds(-1);
            await payer.SaveChangesAsync();
        });
        var sweeping = NewJob(dbName, clock, race);

        var act = async () => await sweeping.Job.SweepAsync();

        await act.Should().ThrowAsync<DbUpdateConcurrencyException>();
        race.Fired.Should().BeTrue();
        (await Stored(seed, "SINV-1")).Status.Should().Be("PAID", "the payment stands");

        var retry = await NewJob(dbName, clock).Job.SweepAsync();
        retry.Should().Be(0);
        (await Stored(seed, "SINV-1")).Status.Should().Be("PAID");
    }

    [Fact]
    public async Task Only_the_overdue_ones_are_flagged_when_they_share_a_run_with_the_rest()
    {
        var h = NewJob();
        await Seed(h,
            Invoice("LATE-1", Yesterday),
            Invoice("LATE-2", Yesterday.AddDays(-40), "PARTIALLY_PAID", balance: 10m),
            Invoice("NOT-YET", Today.AddDays(5)),
            Invoice("DRAFT", Yesterday, "DRAFT"),
            Invoice("PAID", Yesterday, "PAID", balance: 0m));

        var flagged = await h.Job.SweepAsync();

        flagged.Should().Be(2);
        using var reader = Reader(h.DbName);
        (await reader.SalesInvoices.AsNoTracking().OrderBy(i => i.InvoiceNumber).Select(i => new { i.InvoiceNumber, i.Status }).ToListAsync())
            .Select(i => (i.InvoiceNumber, i.Status)).Should().Equal(
                ("DRAFT", "DRAFT"), ("LATE-1", "OVERDUE"), ("LATE-2", "OVERDUE"), ("NOT-YET", "ISSUED"), ("PAID", "PAID"));
    }

    // ── Tenancy ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Every_organizations_overdue_invoices_are_swept_in_one_pass_and_each_keeps_its_own()
    {
        var h = NewJob();
        var orgs = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await Seed(h,
            Invoice("A-LATE", Yesterday, org: orgs[0]), Invoice("A-OK", Today, org: orgs[0]),
            Invoice("B-LATE", Yesterday, "PARTIALLY_PAID", 10m, org: orgs[1]),
            Invoice("C-LATE", Yesterday, org: orgs[2]));

        var flagged = await h.Job.SweepAsync();

        flagged.Should().Be(3);
        (await Stored(h, "A-LATE")).OrganizationId.Should().Be(orgs[0]);
        (await Stored(h, "B-LATE")).OrganizationId.Should().Be(orgs[1]);
        (await Stored(h, "C-LATE")).OrganizationId.Should().Be(orgs[2]);
        (await Stored(h, "A-OK")).Status.Should().Be("ISSUED");
    }

    [Fact]
    public async Task The_sweep_does_not_depend_on_which_organization_happens_to_be_ambient()
    {
        // Triggered from inside one organization's context, it still sweeps them all: it is a system
        // job, not that organization's action.
        var ours = Guid.NewGuid();
        var h = NewJob(tenant: new StaticTenantContext { OrganizationId = ours, IsSuperAdmin = false });
        await Seed(h, Invoice("OURS", Yesterday, org: ours), Invoice("THEIRS", Yesterday, org: Guid.NewGuid()));

        var flagged = await h.Job.SweepAsync();

        flagged.Should().Be(2);
        (await Stored(h, "THEIRS")).Status.Should().Be("OVERDUE");
    }

    // ── Batching and failure ─────────────────────────────────────────────────

    [Fact]
    public async Task A_large_backlog_is_flagged_in_batches()
    {
        var tracker = new SaveTracker();
        var h = NewJob(interceptor: tracker);
        await Seed(h, Enumerable.Range(1, 450).Select(i => Invoice($"SINV-{i:D4}", Yesterday.AddDays(-i % 7))).ToArray());
        tracker.ModifiedPerSave.Clear();

        var flagged = await h.Job.SweepAsync();

        flagged.Should().Be(450);
        tracker.ModifiedPerSave.Should().Equal(new List<int> { 200, 200, 50 }, "a batch at a time, saved as it goes");
        using var reader = Reader(h.DbName);
        (await reader.SalesInvoices.CountAsync(i => i.Status == "OVERDUE")).Should().Be(450);
        (await reader.SalesInvoices.CountAsync(i => i.Status == "ISSUED")).Should().Be(0);
        h.Db.ChangeTracker.Entries().Should().BeEmpty("a finished batch is let go, so the backlog is not held in memory");
    }

    [Fact]
    public async Task A_failure_part_way_keeps_what_was_saved_and_the_retry_finishes_without_repeating_it()
    {
        var dbName = Guid.NewGuid().ToString();
        var clock  = new FakeClock();
        await Seed(NewJob(dbName, clock), Enumerable.Range(1, 450).Select(i => Invoice($"SINV-{i:D4}", Yesterday)).ToArray());
        var failing = NewJob(dbName, clock, new SaveTracker(failOnSave: 3));   // 200 saved, 200 saved, then the third fails

        var act = async () => await failing.Job.SweepAsync();

        await act.Should().ThrowAsync<DbUpdateException>();
        using (var reader = Reader(dbName))
            (await reader.SalesInvoices.CountAsync(i => i.Status == "OVERDUE")).Should().Be(400, "the batches before the failure are kept");

        clock.Value = Now.AddHours(1);
        var retry = NewJob(dbName, clock);
        var flagged = await retry.Job.SweepAsync();

        flagged.Should().Be(50, "only what was left");
        using var after = Reader(dbName);
        (await after.SalesInvoices.CountAsync(i => i.Status == "OVERDUE")).Should().Be(450);
        (await after.SalesInvoices.CountAsync(i => i.ModifiedDate == Now)).Should().Be(400, "the first 400 were not touched again");
        (await after.SalesInvoices.CountAsync(i => i.ModifiedDate == Now.AddHours(1))).Should().Be(50);
    }

    // ── The Hangfire entry point ─────────────────────────────────────────────

    [Fact]
    public async Task Run_does_the_sweep_and_says_how_many_it_marked()
    {
        var h = NewJob();
        await Seed(h, Invoice("SINV-1", Yesterday), Invoice("SINV-2", Yesterday));

        await h.Job.RunAsync();

        (await Stored(h, "SINV-1")).Status.Should().Be("OVERDUE");
        h.Log.Entries.Should().ContainSingle().Which.Should().Be((LogLevel.Information, "Sales invoice overdue sweep: marked 2 invoice(s) OVERDUE."));
    }

    [Fact]
    public async Task Run_is_quiet_when_there_is_nothing_to_do()
    {
        var h = NewJob();
        await Seed(h, Invoice("SINV-1", Today.AddDays(3)));

        await h.Job.RunAsync();

        h.Log.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Hangfire_retries_the_job_a_few_times_before_giving_up()
    {
        var attribute = typeof(InvoiceOverdueJob).GetMethod(nameof(InvoiceOverdueJob.RunAsync))!
            .GetCustomAttributes(typeof(AutomaticRetryAttribute), false).Cast<AutomaticRetryAttribute>().Single();

        attribute.Attempts.Should().Be(3);
    }

    [Fact]
    public void It_is_scheduled_daily_under_a_stable_id_and_scheduling_twice_does_not_duplicate_it()
    {
        JobStorage.Current = new InMemoryStorage();

        InvoiceOverdueJob.Schedule();
        InvoiceOverdueJob.Schedule();

        using var connection = JobStorage.Current.GetConnection();
        var job = connection.GetRecurringJobs().Should().ContainSingle(j => j.Id == InvoiceOverdueJob.RecurringJobId).Subject;

        InvoiceOverdueJob.RecurringJobId.Should().Be("finance-sales-invoice-overdue-sweep");
        job.Cron.Should().Be("0 0 * * *", "daily, at midnight UTC — the start of the day after a due date");
        job.Job.Type.Should().Be(typeof(InvoiceOverdueJob));
        job.Job.Method.Name.Should().Be(nameof(InvoiceOverdueJob.RunAsync));
    }

    // ── With the payment service (P7-05) ─────────────────────────────────────

    [Fact]
    public async Task A_part_payment_on_an_overdue_invoice_shows_partially_paid_until_the_next_sweep_flags_it_overdue_again()
    {
        var org = Guid.NewGuid();
        var h = NewJob();
        var invoice = Invoice("SINV-1", Yesterday, "OVERDUE", org: org);
        invoice.PartnerId = Guid.NewGuid();
        await Seed(h, invoice);

        // The payment side works inside the organization, as a request does.
        var payments = PaymentsFor(h.DbName, org);
        var partner = invoice.PartnerId;

        await payments.RecordPaymentAsync(partner, 400m, "CASH", new CustomerPaymentDetails("PKR"), 5);
        (await Stored(h, "SINV-1")).Status.Should().Be("PARTIALLY_PAID", "§9.5: PARTIALLY_PAID when amount_paid > 0");

        await h.Job.SweepAsync();
        var stillOwing = await Stored(h, "SINV-1");
        stillOwing.Status.Should().Be("OVERDUE", "past due with 600 still owing — the sweep says so again");
        (stillOwing.AmountPaid, stillOwing.BalanceDue).Should().Be((400m, 600m));

        await payments.RecordPaymentAsync(partner, 600m, "CASH", new CustomerPaymentDetails("PKR"), 5);
        (await Stored(h, "SINV-1")).Status.Should().Be("PAID");

        h.Clock.Value = Now.AddDays(1);
        (await h.Job.SweepAsync()).Should().Be(0);
        (await Stored(h, "SINV-1")).Status.Should().Be("PAID", "a settled invoice is never flagged");
    }

    private static CustomerPaymentService PaymentsFor(string dbName, Guid org)
    {
        var db = new FinanceDbContext(
            new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = org });

        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => ids.ToDictionary(id => id, _ => "Acme Ltd"));
        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns([new CurrencyModel { Id = Guid.NewGuid(), Name = "Pakistani Rupee", Code = "PKR" }]);

        return new CustomerPaymentService(db, new CustomerLedgerService(db), names.Object, lookups.Object, new FakeClock());
    }
}
